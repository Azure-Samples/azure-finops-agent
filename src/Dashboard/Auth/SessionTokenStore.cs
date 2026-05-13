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

        var res = await http.SendAsync(req);
        if (!res.IsSuccessStatusCode) return null;

        var body = await res.Content.ReadAsStringAsync();
        var json = JsonSerializer.Deserialize<JsonElement>(body);
        if (!json.TryGetProperty("access_token", out var tokenProp)) return null;

        var expiresIn = json.TryGetProperty("expires_in", out var expProp) ? expProp.GetInt32() : 3600;
        var rotated = json.TryGetProperty("refresh_token", out var newRt) ? newRt.GetString() : null;
        return (tokenProp.GetString()!, DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60), rotated);
    }

    public async Task<string?> GetSessionTokenAsync(HttpContext ctx, IHttpClientFactory httpFactory,
        string tokenKey, string expiryKey, string refreshScope)
    {
        var token = ctx.Session.GetString(tokenKey);
        var expiryStr = ctx.Session.GetString(expiryKey);

        // No cached access token but we have a refresh token (typical right after a
        // restart-driven session hydration) &#8212; fall through to mint a fresh one.
        var hasRefresh = !string.IsNullOrEmpty(ctx.Session.GetString("azure_refresh_token"));
        if (token is null)
        {
            if (!hasRefresh) return null;
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
            var http = httpFactory.CreateClient();
            var sessionTenant = ctx.Session.GetString("auth_tenant");
            var result = await ExchangeRefreshTokenForResource(http, refreshToken, refreshScope, sessionTenant);
            if (result is null)
            {
                _logger.LogWarning("Token {Key} refresh failed; user must re-authenticate", tokenKey);
                ctx.Session.Remove(tokenKey);
                ctx.Session.Remove(expiryKey);
                return null;
            }
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
                    try
                    {
                        var au = JsonSerializer.Deserialize<JsonElement>(azureUserJson);
                        if (au.TryGetProperty("objectId", out var oidProp))
                        {
                            var oid = oidProp.GetString();
                            if (!string.IsNullOrEmpty(oid))
                                _identity.UpdateRefreshToken(oid, result.Value.RotatedRefreshToken);
                        }
                    }
                    catch { }
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
            "https://graph.microsoft.com/User.Read https://graph.microsoft.com/User.Read.All https://graph.microsoft.com/Organization.Read.All https://graph.microsoft.com/Group.Read.All https://graph.microsoft.com/Reports.Read.All offline_access");

    public Task<string?> GetLogAnalyticsTokenAsync(HttpContext ctx, IHttpClientFactory httpFactory) =>
        GetSessionTokenAsync(ctx, httpFactory, "loganalytics_token", "loganalytics_token_expiry",
            "https://api.loganalytics.io/Data.Read offline_access");

    public Task<string?> GetStorageTokenAsync(HttpContext ctx, IHttpClientFactory httpFactory) =>
        GetSessionTokenAsync(ctx, httpFactory, "storage_token", "storage_token_expiry",
            "https://storage.azure.com/user_impersonation offline_access");
}
