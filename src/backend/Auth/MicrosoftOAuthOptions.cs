namespace AzureFinOps.Dashboard.Auth;

using System.Text.RegularExpressions;

/// <summary>
/// Strongly-typed Microsoft Entra ID multi-tenant OAuth configuration.
/// Bound from <c>Microsoft:*</c> config keys.
/// </summary>
public sealed class MicrosoftOAuthOptions
{
    public string ClientId { get; init; } = "";
    /// <summary>
    /// Optional. Leave empty in production to use federated workload identity
    /// (App Service / Container Apps managed identity → Entra app federated credential).
    /// Only set in local dev environments without a federated MI.
    /// </summary>
    public string ClientSecret { get; init; } = "";
    public string TenantId { get; init; } = "common";
    public string HomeTenantId { get; init; } = "common";

    public bool IsConfigured => !string.IsNullOrEmpty(ClientId);

    /// <summary>Tiers the user is allowed to request via the <c>?tier=</c> query parameter.</summary>
    public static readonly IReadOnlySet<string> ValidTiers =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "base", "licenses", "chargeback", "loganalytics", "storage", "all"
        };

    public static readonly string[] AddOnTiers =
        ["licenses", "chargeback", "loganalytics", "storage"];

    private static readonly Regex GuidRegex =
        new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
            RegexOptions.Compiled);

    /// <summary>
    /// Whether a string is a valid Entra tenant identifier — either a GUID or one
    /// of the well-known aliases. Anything else is rejected to prevent attackers
    /// supplying a custom tenant via <c>?tenant=</c> for phishing-style consent.
    /// </summary>
    public static bool IsValidTenantId(string tenant) =>
        !string.IsNullOrEmpty(tenant)
        && (tenant == "common" || tenant == "organizations" || tenant == "consumers"
            || GuidRegex.IsMatch(tenant));

    /// <summary>Normalize a tier value supplied via query string. Falls back to <c>"base"</c>.</summary>
    public static string NormalizeTier(string? tier) =>
        !string.IsNullOrEmpty(tier) && ValidTiers.Contains(tier)
            ? tier.ToLowerInvariant()
            : "base";

    /// <summary>
    /// OAuth tier → resource-specific scopes. Single source of truth for both the
    /// authorize redirect and the token exchange.
    /// </summary>
    public static string[] GetScopesForTier(string tier) => tier switch
    {
        "licenses" => ["https://graph.microsoft.com/User.Read", "https://graph.microsoft.com/Organization.Read.All", "https://graph.microsoft.com/Reports.Read.All"],
        "chargeback" => ["https://graph.microsoft.com/User.Read", "https://graph.microsoft.com/User.Read.All", "https://graph.microsoft.com/Group.Read.All"],
        "loganalytics" => ["https://api.loganalytics.io/Data.Read"],
        "storage" => ["https://storage.azure.com/user_impersonation"],
        _ => ["https://management.azure.com/user_impersonation"]
    };

    /// <summary>
    /// Normalize host for OAuth callbacks — strip "www." so callbacks always match
    /// the registered redirect URIs (e.g. azure-finops-agent.com, not www.azure-finops-agent.com).
    /// </summary>
    public static string NormalizeCallbackHost(HttpContext ctx)
    {
        var host = ctx.Request.Host.ToString();
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            host = host[4..];
        return $"{ctx.Request.Scheme}://{host}";
    }
}
