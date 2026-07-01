using AzureFinOps.Dashboard.Observability;

namespace AzureFinOps.Dashboard.Auth;

/// <summary>
/// Background service that proactively refreshes per-user tenant access tokens
/// (Azure ARM, Microsoft Graph, Log Analytics, Azure Storage) using the
/// long-lived MSAL refresh token persisted by <see cref="PersistentIdentity"/>.
///
/// Why this exists: tools called from a turn that outlives its HTTP request
/// (browser closed, long-running background job) read tokens directly from the
/// per-user <see cref="UserTokens"/> bag via closure. Without a request to
/// trigger <see cref="SessionTokenStore"/>, those access tokens (lifetime ~60
/// min) silently expire and tools start returning 401s.
///
/// This service walks every active <see cref="UserTokens"/> instance every
/// <see cref="SweepInterval"/>, and for each scope whose token is within
/// <see cref="RefreshThreshold"/> of expiry, exchanges the cached refresh
/// token for a fresh access token and writes it back into the bag — the same
/// volatile fields tools already read.
///
/// Refresh tokens themselves rotate on use (Entra default); the rotated value
/// is persisted via <see cref="PersistentIdentity.UpdateRefreshToken"/> so the
/// next refresh tick uses the latest one.
///
/// Anonymous users (no Entra OID, no refresh token) are skipped — they have no
/// tokens to refresh.
///
/// Failure modes: if the refresh fails (consent revoked, password change, MFA
/// required, refresh token rotated out), the corresponding token is set to
/// null on the bag so the next tool call returns a clean "not authenticated"
/// rather than a stale 401.
/// </summary>
public sealed class TenantTokenRefresher : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    /// <summary>How close to expiry before we proactively refresh. Set high
    /// enough that a single sweep miss can't run a token to its hard expiry.</summary>
    private static readonly TimeSpan RefreshThreshold = TimeSpan.FromMinutes(15);

    private const string ScopeArm =
        "openid profile email https://management.azure.com/user_impersonation offline_access";
    private const string ScopeGraph =
        "https://graph.microsoft.com/User.Read https://graph.microsoft.com/User.Read.All https://graph.microsoft.com/Organization.Read.All https://graph.microsoft.com/Group.Read.All https://graph.microsoft.com/Reports.Read.All offline_access";
    private const string ScopeLogAnalytics =
        "https://api.loganalytics.io/Data.Read offline_access";
    private const string ScopeStorage =
        "https://storage.azure.com/user_impersonation offline_access";

    private readonly AiTelemetry _telemetry;
    private readonly SessionTokenStore _tokenStore;
    private readonly PersistentIdentity _identity;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<TenantTokenRefresher> _logger;

    public TenantTokenRefresher(
        AiTelemetry telemetry,
        SessionTokenStore tokenStore,
        PersistentIdentity identity,
        IHttpClientFactory httpFactory,
        ILogger<TenantTokenRefresher> logger)
    {
        _telemetry = telemetry;
        _tokenStore = tokenStore;
        _identity = identity;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial small delay so the first sweep doesn't race the host startup.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Sweep(stoppingToken); }
            catch (Exception ex) { _logger.LogWarning(ex, "TenantTokenRefresher sweep failed"); }

            try { await Task.Delay(SweepInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task Sweep(CancellationToken ct)
    {
        var refreshed = 0;
        var failed = 0;
        foreach (var (userId, tokens) in _telemetry.UserTokens)
        {
            if (ct.IsCancellationRequested) return;

            // Skip users with no expiring tokens — anonymous, or never
            // connected to Azure.
            if (!NeedsRefresh(tokens.AzureTokenExpiry)
                && !NeedsRefresh(tokens.GraphTokenExpiry)
                && !NeedsRefresh(tokens.LogAnalyticsTokenExpiry)
                && !NeedsRefresh(tokens.StorageTokenExpiry))
                continue;

            var record = _identity.LoadByUserId(userId);
            if (record is null || string.IsNullOrEmpty(record.RefreshToken))
            {
                _logger.LogDebug("TenantTokenRefresher: no identity record for user {UserId}; skipping", userId);
                continue;
            }

            await tokens.RefreshLock.WaitAsync(ct);
            try
            {
                if (NeedsRefresh(tokens.AzureTokenExpiry))
                {
                    var (ok, fail) = await TryRefresh(record, tokens, ScopeArm, "ARM",
                        v => { tokens.AzureToken = v.Token; tokens.AzureTokenExpiry = v.Expiry; },
                        () => { tokens.AzureToken = null; tokens.AzureTokenExpiry = null; });
                    refreshed += ok; failed += fail;
                }

                if (NeedsRefresh(tokens.GraphTokenExpiry))
                {
                    var (ok, fail) = await TryRefresh(record, tokens, ScopeGraph, "Graph",
                        v => { tokens.GraphToken = v.Token; tokens.GraphTokenExpiry = v.Expiry; },
                        () => { tokens.GraphToken = null; tokens.GraphTokenExpiry = null; });
                    refreshed += ok; failed += fail;
                }

                if (NeedsRefresh(tokens.LogAnalyticsTokenExpiry))
                {
                    var (ok, fail) = await TryRefresh(record, tokens, ScopeLogAnalytics, "LogAnalytics",
                        v => { tokens.LogAnalyticsToken = v.Token; tokens.LogAnalyticsTokenExpiry = v.Expiry; },
                        () => { tokens.LogAnalyticsToken = null; tokens.LogAnalyticsTokenExpiry = null; });
                    refreshed += ok; failed += fail;
                }

                if (NeedsRefresh(tokens.StorageTokenExpiry))
                {
                    var (ok, fail) = await TryRefresh(record, tokens, ScopeStorage, "Storage",
                        v => { tokens.StorageToken = v.Token; tokens.StorageTokenExpiry = v.Expiry; },
                        () => { tokens.StorageToken = null; tokens.StorageTokenExpiry = null; });
                    refreshed += ok; failed += fail;
                }
            }
            finally
            {
                tokens.RefreshLock.Release();
            }
        }

        if (refreshed > 0 || failed > 0)
            _logger.LogInformation("TenantTokenRefresher swept {UserCount} user(s); refreshed={Refreshed} failed={Failed}",
                _telemetry.UserTokens.Count, refreshed, failed);
    }

    private static bool NeedsRefresh(DateTimeOffset? expiry)
        => expiry.HasValue && expiry.Value - DateTimeOffset.UtcNow < RefreshThreshold;

    /// <summary>Returns (refreshedCount, failedCount) — exactly one will be 1, the other 0.</summary>
    private async Task<(int Refreshed, int Failed)> TryRefresh(
        IdentityRecord record,
        UserTokens tokens,
        string scope,
        string label,
        Action<(string Token, DateTimeOffset Expiry)> apply,
        Action clear)
    {
        try
        {
            var http = _httpFactory.CreateClient();
            var result = await _tokenStore.ExchangeRefreshTokenForResource(
                http, record.RefreshToken!, scope, record.TenantId);
            if (result is null)
            {
                _logger.LogWarning("TenantTokenRefresher: {Scope} refresh returned no token for user {UserId}; clearing", label, tokens.UserId);
                clear();
                return (0, 1);
            }

            apply((result.Value.Token, result.Value.Expiry));

            if (!string.IsNullOrEmpty(result.Value.RotatedRefreshToken)
                && result.Value.RotatedRefreshToken != record.RefreshToken)
            {
                await _identity.UpdateRefreshTokenAsync(record.Oid, result.Value.RotatedRefreshToken);
                record.RefreshToken = result.Value.RotatedRefreshToken;
            }

            // Keep the janitor from evicting an actively-refreshing user — its
            // idle clock would otherwise tear down UserTokens / LiveSessions
            // out from under a long-running background turn that has no HTTP
            // requests touching LastSeenUtc.
            UserStateJanitor.LastSeenUtc[tokens.UserId] = DateTimeOffset.UtcNow;

            return (1, 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TenantTokenRefresher: {Scope} refresh threw for user {UserId}", label, tokens.UserId);
            return (0, 1);
        }
    }
}
