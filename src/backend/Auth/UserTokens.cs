namespace AzureFinOps.Dashboard.Auth;

/// <summary>
/// Per-user mutable token holder. One instance per user, stored in a ConcurrentDictionary
/// keyed by userId. Passed to tool constructors via closure — tools always read the latest
/// tokens via direct reference. Volatile fields ensure cross-thread visibility.
/// </summary>
public class UserTokens
{
    private volatile string? _azureToken;
    private volatile string? _graphToken;
    private volatile string? _logAnalyticsToken;
    private volatile string? _storageToken;

    // Expiry tracking is stored as Unix-seconds in a long, written/read
    // atomically via Interlocked. The TenantTokenRefresher background service
    // uses these to decide which scopes to refresh proactively for users with
    // active background turns (where no HTTP request is around to top them up).
    private long _azureTokenExpiryUnixSec;
    private long _graphTokenExpiryUnixSec;
    private long _logAnalyticsTokenExpiryUnixSec;
    private long _storageTokenExpiryUnixSec;

    /// <summary>The user id this token bag belongs to (set by the per-user factory).</summary>
    public long UserId { get; init; }

    /// <summary>Azure ARM API token (management.azure.com)</summary>
    public string? AzureToken { get => _azureToken; set => _azureToken = value; }

    /// <summary>Microsoft Graph API token (graph.microsoft.com)</summary>
    public string? GraphToken { get => _graphToken; set => _graphToken = value; }

    /// <summary>Log Analytics / App Insights API token (api.loganalytics.io)</summary>
    public string? LogAnalyticsToken { get => _logAnalyticsToken; set => _logAnalyticsToken = value; }

    /// <summary>Azure Storage data-plane token (storage.azure.com) for reading cost exports</summary>
    public string? StorageToken { get => _storageToken; set => _storageToken = value; }

    /// <summary>Expiry of <see cref="AzureToken"/>, or null if unknown.</summary>
    public DateTimeOffset? AzureTokenExpiry
    {
        get => ReadExpiry(ref _azureTokenExpiryUnixSec);
        set => WriteExpiry(ref _azureTokenExpiryUnixSec, value);
    }

    /// <summary>Expiry of <see cref="GraphToken"/>, or null if unknown.</summary>
    public DateTimeOffset? GraphTokenExpiry
    {
        get => ReadExpiry(ref _graphTokenExpiryUnixSec);
        set => WriteExpiry(ref _graphTokenExpiryUnixSec, value);
    }

    /// <summary>Expiry of <see cref="LogAnalyticsToken"/>, or null if unknown.</summary>
    public DateTimeOffset? LogAnalyticsTokenExpiry
    {
        get => ReadExpiry(ref _logAnalyticsTokenExpiryUnixSec);
        set => WriteExpiry(ref _logAnalyticsTokenExpiryUnixSec, value);
    }

    /// <summary>Expiry of <see cref="StorageToken"/>, or null if unknown.</summary>
    public DateTimeOffset? StorageTokenExpiry
    {
        get => ReadExpiry(ref _storageTokenExpiryUnixSec);
        set => WriteExpiry(ref _storageTokenExpiryUnixSec, value);
    }

    /// <summary>Lock for serializing token refresh operations to prevent double-refresh races.</summary>
    public SemaphoreSlim RefreshLock { get; } = new(1, 1);

    private static DateTimeOffset? ReadExpiry(ref long field)
    {
        var v = System.Threading.Interlocked.Read(ref field);
        return v == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(v);
    }

    private static void WriteExpiry(ref long field, DateTimeOffset? value)
    {
        System.Threading.Interlocked.Exchange(ref field, value?.ToUnixTimeSeconds() ?? 0);
    }
}
