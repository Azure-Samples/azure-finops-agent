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
    private volatile string? _copilotCostToken;
    private volatile string? _copilotChangesToken;

    // Expiry tracking is stored as Unix-seconds in a long, written/read
    // atomically via Interlocked. The TenantTokenRefresher background service
    // uses these to decide which scopes to refresh proactively for users with
    // active background turns (where no HTTP request is around to top them up).
    private long _azureTokenExpiryUnixSec;
    private long _graphTokenExpiryUnixSec;
    private long _logAnalyticsTokenExpiryUnixSec;
    private long _storageTokenExpiryUnixSec;
    private long _copilotCostTokenExpiryUnixSec;
    private long _copilotChangesTokenExpiryUnixSec;

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

    /// <summary>Microsoft Graph token requested with the Copilot cost management read scopes (/copilot/costManagement).
    /// Entra puts every Graph scope the user granted into every Graph token, so the host's method rules, not this token,
    /// keep reads from writing.</summary>
    public string? CopilotCostToken { get => _copilotCostToken; set => _copilotCostToken = value; }

    /// <summary>Microsoft Graph token requested with the Copilot cost management write scopes; it marks that consent and is
    /// sent only by changes the user approved (ApplyAzureChange). Never hydrated for unattended job runs.</summary>
    public string? CopilotChangesToken { get => _copilotChangesToken; set => _copilotChangesToken = value; }

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

    /// <summary>Expiry of <see cref="CopilotCostToken"/>, or null if unknown.</summary>
    public DateTimeOffset? CopilotCostTokenExpiry
    {
        get => ReadExpiry(ref _copilotCostTokenExpiryUnixSec);
        set => WriteExpiry(ref _copilotCostTokenExpiryUnixSec, value);
    }

    /// <summary>Expiry of <see cref="CopilotChangesToken"/>, or null if unknown.</summary>
    public DateTimeOffset? CopilotChangesTokenExpiry
    {
        get => ReadExpiry(ref _copilotChangesTokenExpiryUnixSec);
        set => WriteExpiry(ref _copilotChangesTokenExpiryUnixSec, value);
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
