using System.Collections.Concurrent;
using System.Text.Json;

namespace AzureFinOps.Dashboard.Auth;

/// <summary>
/// Reads/refreshes the four resource tokens (ARM, Graph, Log Analytics, Storage)
/// from the user's session, using the cached refresh token to mint new access
/// tokens when the cached one is expired. Refreshes are serialised per
/// session+token to avoid concurrent duplicate refreshes from parallel SSE/tool calls.
/// </summary>
public sealed class SessionTokenStore
{
    private readonly MicrosoftOAuthOptions _options;
    private readonly EntraClientCredentials _credentials;
    private readonly PersistentIdentity _identity;
    private readonly ILogger<SessionTokenStore> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshLocks = new();

    public SessionTokenStore(MicrosoftOAuthOptions options, EntraClientCredentials credentials, PersistentIdentity identity, ILogger<SessionTokenStore> logger)
    {
        _options = options;
        _credentials = credentials;
        _identity = identity;
        _logger = logger;
    }

    public async Task<(string Token, DateTimeOffset Expiry, string? RotatedRefreshToken)?> ExchangeRefreshTokenForResource(
        HttpClient http, string refreshToken, string scope, string? tenantOverride = null)
    {
        var effectiveTenant = tenantOverride ?? _options.TenantId;
        // Up to 2 retries on transient TCP/HTTP timeouts (corporate proxy
        // dropping SYN on cold connections is the #1 cause of token-fetch
        // hangs). Connect timeout itself is 5 s via the named client's
        // SocketsHttpHandler.ConnectTimeout, so worst case here is ~15 s
        // instead of the OS default 63 s.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"https://login.microsoftonline.com/{Uri.EscapeDataString(effectiveTenant)}/oauth2/v2.0/token");
            var form = new Dictionary<string, string>
            {
                ["client_id"] = _options.ClientId,
                ["refresh_token"] = refreshToken,
                ["grant_type"] = "refresh_token",
                ["scope"] = scope
            };
            await _credentials.AddCredentialFieldsAsync(form);
            req.Content = new FormUrlEncodedContent(form);

            try
            {
                var res = await http.SendAsync(req);
                if (!res.IsSuccessStatusCode)
                {
                    // A non-transient 4xx from the token endpoint (typically 400
                    // invalid_grant / interaction_required) means the persisted
                    // refresh token can no longer mint this resource's token — the
                    // user's Azure connection is effectively dead until they
                    // re-consent. Previously this returned null silently, so the
                    // AADSTS reason was invisible in telemetry (18/21 refreshes
                    // were failing 400 in production with no diagnosable cause).
                    // The error body carries only the error code / description /
                    // correlation ids — never the access or refresh token.
                    string err = "unknown", desc = "";
                    try
                    {
                        var errBody = await res.Content.ReadAsStringAsync();
                        var ej = JsonSerializer.Deserialize<JsonElement>(errBody);
                        if (ej.TryGetProperty("error", out var e)) err = e.GetString() ?? "unknown";
                        if (ej.TryGetProperty("error_description", out var d)) desc = d.GetString() ?? "";
                    }
                    catch { /* non-JSON error body — status + scope below still logged */ }
                    _logger.LogWarning(
                        "Entra refresh-token exchange failed: HTTP {Status} error={Error} scope={Scope} tenant={Tenant} desc={Desc}",
                        (int)res.StatusCode, err, scope, effectiveTenant,
                        desc.Length > 300 ? desc[..300] : desc);
                    return null;
                }

                var body = await res.Content.ReadAsStringAsync();
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                if (!json.TryGetProperty("access_token", out var tokenProp)) return null;

                var expiresIn = json.TryGetProperty("expires_in", out var expProp) ? expProp.GetInt32() : 3600;
                var rotated = json.TryGetProperty("refresh_token", out var newRt) ? newRt.GetString() : null;
                return (tokenProp.GetString()!, DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60), rotated);
            }
            catch (Exception ex) when (attempt < 2 && IsTransientNetworkError(ex))
            {
                var delayMs = 250 * (1 << attempt); // 250 ms, 500 ms
                _logger.LogWarning("Entra token POST failed (attempt {Attempt}/3) for scope={Scope}: {Err}. Retrying in {Delay}ms",
                    attempt + 1, scope, ex.GetType().Name + ": " + ex.Message, delayMs);
                await Task.Delay(delayMs);
            }
            catch (Exception ex) when (attempt == 2 && IsTransientNetworkError(ex))
            {
                // Final attempt failed with a transient network error — swallow
                // and return null. Throwing here would surface as HTTP 500 from
                // /auth/azure/status (observed in production telemetry). Null
                // lets the endpoint return { connected = false } and the UI
                // shows "sign in again" instead of a generic error page.
                _logger.LogWarning("Entra token POST failed after 3 attempts for scope={Scope}: {Err}. Returning null (user must re-auth).",
                    scope, ex.GetType().Name + ": " + ex.Message);
            }
        }
        return null;
    }

    /// <summary>True for connect timeouts, socket resets, and DNS failures we
    /// expect to clear on a fresh connection (typical of corporate proxies that
    /// drop the first SYN). Deliberately excludes SSL/cert/auth failures —
    /// those won't recover on retry and burning 3 attempts on them is wasteful.</summary>
    private static bool IsTransientNetworkError(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            // Never retry auth/cert/SSL failures — they're deterministic.
            if (e is System.Security.Authentication.AuthenticationException) return false;
            if (e is System.Security.Cryptography.CryptographicException) return false;

            if (e is TimeoutException) return true;
            if (e is System.Net.Sockets.SocketException se)
            {
                return se.SocketErrorCode is
                    System.Net.Sockets.SocketError.TimedOut or
                    System.Net.Sockets.SocketError.ConnectionRefused or
                    System.Net.Sockets.SocketError.ConnectionReset or
                    System.Net.Sockets.SocketError.NetworkUnreachable or
                    System.Net.Sockets.SocketError.HostUnreachable;
            }
            // HttpRequestException with no SocketException or AuthException inner is
            // most often a connection-establishment failure (proxy / DNS / transient
            // network) — retry. SSL handshake failures bubble up as AuthException
            // inner and are filtered out above.
            if (e is System.Net.Http.HttpRequestException) return true;
        }
        return false;
    }

    public async Task<string?> GetSessionTokenAsync(HttpContext ctx, IHttpClientFactory httpFactory,
        string tokenKey, string expiryKey, string refreshScope, string? requiredTiers = null)
    {
        var token = ctx.Session.GetString(tokenKey);
        var expiryStr = ctx.Session.GetString(expiryKey);

        // No cached access token but we have a refresh token (typical right after a
        // restart-driven session hydration) — fall through to mint a fresh one.
        var hasRefresh = !string.IsNullOrEmpty(ctx.Session.GetString("azure_refresh_token"));
        if (token is null)
        {
            if (!hasRefresh) return null;

            // Consent gate — only exchange the refresh token for scopes the user
            // actually consented to. Without this, every chat message attempted
            // Graph/Log-Analytics/Storage exchanges for base-tier users, each a
            // guaranteed HTTP 400 (observed: 18/21 token calls failing in prod)
            // plus 3 wasted Entra round-trips of latency per message.
            if (requiredTiers is not null)
            {
                var consented = ctx.Session.GetString("graph_tier") ?? "";
                var anyConsent = requiredTiers.Split(',').Any(t => consented.Contains(t, StringComparison.OrdinalIgnoreCase));
                if (!anyConsent) return null;
            }

            // Failure backoff — a refresh for this scope recently failed (revoked
            // consent, CA policy, tenant restriction). Don't retry on every
            // message; wait out the backoff window instead.
            var backoffStr = ctx.Session.GetString(tokenKey + "_unavailable_until");
            if (backoffStr is not null && DateTimeOffset.TryParse(backoffStr, out var backoffUntil)
                && backoffUntil > DateTimeOffset.UtcNow)
                return null;
        }
        else if (expiryStr is null || !DateTimeOffset.TryParse(expiryStr, out var expiry) || expiry > DateTimeOffset.UtcNow)
        {
            return token;
        }

        var lockKey = $"{ctx.Session.Id}|{tokenKey}";
        var sem = _refreshLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ctx.RequestAborted);
        try
        {
            var freshToken = ctx.Session.GetString(tokenKey);
            var freshExpiryStr = ctx.Session.GetString(expiryKey);
            if (freshToken is not null && freshExpiryStr is not null
                && DateTimeOffset.TryParse(freshExpiryStr, out var freshExpiry)
                && freshExpiry > DateTimeOffset.UtcNow)
            {
                return freshToken;
            }

            var refreshToken = ctx.Session.GetString("azure_refresh_token");
            if (refreshToken is null)
            {
                _logger.LogWarning("Token {Key} expired and no refresh token available; user must re-authenticate", tokenKey);
                ctx.Session.Remove(tokenKey);
                ctx.Session.Remove(expiryKey);
                return null;
            }
            var http = httpFactory.CreateClient("entra-token");
            var sessionTenant = ctx.Session.GetString("auth_tenant");
            var result = await ExchangeRefreshTokenForResource(http, refreshToken, refreshScope, sessionTenant);
            if (result is null)
            {
                _logger.LogWarning("Token {Key} refresh failed; user must re-authenticate", tokenKey);
                ctx.Session.Remove(tokenKey);
                ctx.Session.Remove(expiryKey);
                // Add-on scopes: back off for 15 minutes so a deterministic failure
                // (consent revoked, CA policy) doesn't refire on every message. The
                // base ARM scope is exempt — it must keep retrying so a reconnect
                // is picked up immediately.
                if (requiredTiers is not null)
                    ctx.Session.SetString(tokenKey + "_unavailable_until",
                        DateTimeOffset.UtcNow.AddMinutes(15).ToString("o"));
                return null;
            }
            ctx.Session.Remove(tokenKey + "_unavailable_until");
            ctx.Session.SetString(tokenKey, result.Value.Token);
            ctx.Session.SetString(expiryKey, result.Value.Expiry.ToString("o"));
            if (!string.IsNullOrEmpty(result.Value.RotatedRefreshToken))
            {
                ctx.Session.SetString("azure_refresh_token", result.Value.RotatedRefreshToken);
                // Mirror the rotated refresh token to /home so survival across container
                // restarts keeps working after Entra has rotated the original.
                var azureUserJson = ctx.Session.GetString("azure_user");
                if (azureUserJson is not null)
                {
                    string? oid = null;
                    try
                    {
                        var au = JsonSerializer.Deserialize<JsonElement>(azureUserJson);
                        if (au.TryGetProperty("objectId", out var oidProp))
                            oid = oidProp.GetString();
                    }
                    catch { }
                    if (!string.IsNullOrEmpty(oid))
                    {
                        try { await _identity.UpdateRefreshTokenAsync(oid, result.Value.RotatedRefreshToken); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Failed to mirror rotated refresh token for oid={Oid}", oid); }
                    }
                }
            }
            return result.Value.Token;
        }
        finally
        {
            sem.Release();
        }
    }

    public Task<string?> GetAzureTokenAsync(HttpContext ctx, IHttpClientFactory httpFactory) =>
        GetSessionTokenAsync(ctx, httpFactory, "azure_token", "azure_token_expiry",
            "openid profile email https://management.azure.com/user_impersonation offline_access");

    public Task<string?> GetGraphTokenAsync(HttpContext ctx, IHttpClientFactory httpFactory) =>
        GetSessionTokenAsync(ctx, httpFactory, "graph_token", "graph_token_expiry",
            "https://graph.microsoft.com/User.Read https://graph.microsoft.com/User.Read.All https://graph.microsoft.com/Organization.Read.All https://graph.microsoft.com/Group.Read.All https://graph.microsoft.com/Reports.Read.All offline_access",
            requiredTiers: "licenses,chargeback");

    public Task<string?> GetLogAnalyticsTokenAsync(HttpContext ctx, IHttpClientFactory httpFactory) =>
        GetSessionTokenAsync(ctx, httpFactory, "loganalytics_token", "loganalytics_token_expiry",
            "https://api.loganalytics.io/Data.Read offline_access",
            requiredTiers: "loganalytics");

    public Task<string?> GetStorageTokenAsync(HttpContext ctx, IHttpClientFactory httpFactory) =>
        GetSessionTokenAsync(ctx, httpFactory, "storage_token", "storage_token_expiry",
            "https://storage.azure.com/user_impersonation offline_access",
            requiredTiers: "storage");
}
