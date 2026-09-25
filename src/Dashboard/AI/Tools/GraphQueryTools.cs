using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.AI;

using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Infrastructure;

namespace AzureFinOps.Dashboard.AI.Tools;

/// <summary>
/// Queries Microsoft Graph API using the user's delegated Graph token.
/// Used for license inventory, directory objects, and org structure for FinOps chargebacks.
/// </summary>
public class GraphQueryTools
{
    private readonly UserTokens _tokens;

    public GraphQueryTools(UserTokens tokens) => _tokens = tokens;

    public IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(QueryGraph, "QueryGraph", @"Calls Microsoft Graph API (https://graph.microsoft.com) using the signed-in user's token. Returns the provider response (JSON, or CSV for report endpoints).
The Current UTC time preamble is the retrieval timestamp, not the report refresh date or proof of current activity. Preserve the provider's report dates and pagination; never invent a source data-as-of timestamp.
Methods: GET, POST, PUT, PATCH. DELETE is blocked at the code level. NOTE: the standard consent tiers only grant read-only scopes (*.Read.All / Reports.Read.All), so write calls return 403 insufficient privileges unless the tenant has consented to write scopes — surface that to the user rather than retrying.
DATA SCOPING: use $select for needed fields, $top for a small page, and $filter for the requested scope wherever that Graph endpoint supports them. Prefer supported server-side reports and counts for summaries; do not invent query options on endpoints such as reports or subscribedSkus. Avoid full user objects and broad collections when a narrower request answers the question. Follow @odata.nextLink only while the requested result needs more rows. A limited page is not a tenant-wide count; disclose incomplete pagination and preserve totals.
For Copilot activity counts and inactive-user lists prefer GetCopilotUsage, which processes the supported report on the host and returns bounded, dated counts/pages instead of an oversized raw report.

Use standard Graph URL conventions; you know the v1.0 surface. FinOps-relevant areas:
- Licenses: /v1.0/subscribedSkus?$select=skuId,skuPartNumber,prepaidUnits,consumedUnits,capabilityStatus. This collection supports ONLY $select: never add $top, $filter or $search. Compare consumedUnits with prepaidUnits.enabled for unassigned seats; the host appends a root `_licenseSummary` with per-SKU and total enabled/assigned/unassigned counts plus a headline, table and freshness sentence (retrieval time) — present those verbatim instead of adding or subtracting seats yourself. Assignment is not proof of active use. Label prepaidUnits.enabled as enabled license inventory, never as verified purchased or paid seats. Graph does not return contract unit prices or invoices. For actual purchased quantities and monthly waste, request the customer's invoice/pricesheet when billing evidence was not provided; public marketing prices cannot establish that bill. Fetch public pricing only for an explicitly requested list-price/hypothetical estimate, not as a substitute for missing contract inputs.
- M365 usage reports (period='D30'): /v1.0/reports/getOffice365ActiveUserDetail, getMailboxUsageDetail, getTeamsUserActivityUserDetail, getOneDriveUsageAccountDetail, getSharePointSiteUsageDetail, getM365AppUserDetail
- M365 Copilot usage: GET /v1.0/copilot/reports/getMicrosoft365CopilotUsageUserDetail(period='D30') or getMicrosoft365CopilotUserCountSummary(period='D30') returns CSV for licensed users. version='v1' is the default (D7/D30/D90/D180/ALL); version='v2' uses D28 instead of D30 and adds prompt counts/active days. The beta /copilot/reports equivalents return JSON. Do not add $filter, $top or $select to these functions. Legacy /beta/reports/getMicrosoft365CopilotUsageUserDetail and getMicrosoft365CopilotUserCountSummary support $format only, not $filter. Reports.Read.All plus a supported directory role is required. Missing report access or anonymized user names is not zero activity; licensed-user reports do not cover unlicensed Copilot Chat. An UnknownTenantId/report-unavailable result from GetCopilotUsage is not repaired by repeating the same report through QueryGraph; report the setup/data-availability blocker instead.
- Intune: /v1.0/deviceManagement/managedDevices (use /beta/ only for preview-only fields)
- Directory / chargeback: /v1.0/organization, /v1.0/users (department/companyName/officeLocation), /v1.0/groups, /v1.0/administrativeUnits, /v1.0/users/{id}/manager
- Security: /v1.0/security/secureScores
- Apps & roles: /v1.0/applications, /v1.0/servicePrincipals, /v1.0/directoryRoles[/{id}/members]
- Domains: /v1.0/domains");
    }

    private async Task<string> QueryGraph(
        [Description("Graph path starting with /. Use supported $filter/$select/$top on users and groups; subscribedSkus supports only $select. Copilot report functions do not support $filter/$top/$select; use period and the documented report version. Example: /v1.0/users?$filter=accountEnabled eq true&$select=id,department&$top=50.")] string path,
        [Description("HTTP method: GET, POST, PUT, or PATCH (DELETE is blocked)")] string? method = "GET",
        [Description("Optional JSON request body for POST/PUT/PATCH requests. Omit for GET.")] string? body = null)
    {
        using var activity = HttpHelper.Telemetry.StartActivity("QueryGraph");
        activity?.SetTag("graph.method", method);
        activity?.SetTag("graph.path", path);
        activity?.SetTag("graph.has_body", !string.IsNullOrWhiteSpace(body));

        var token = _tokens.GraphToken;
        if (string.IsNullOrEmpty(token))
            return HttpHelper.TokenMissing("GraphToken", activity, "graph");

        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/'))
        {
            activity?.SetTag("graph.result", "invalid_path");
            return $"HTTP 400 BadRequest\nInvalid path: '{path}'. Path must start with /.";
        }

        var (httpMethod, methodError) = HttpHelper.ResolveMethod(method, activity, "graph");
        if (methodError is not null) return methodError;
        if (httpMethod == HttpMethod.Get && ValidateQueryParameters(path) is { } queryError)
        {
            activity?.SetTag("graph.result", "unsupported_query_options");
            activity?.SetStatus(ActivityStatusCode.Error, "Unsupported Graph query options");
            return queryError;
        }

        var hasBody = !string.IsNullOrWhiteSpace(body);
        var response = await HttpHelper.SendWithRetryAsync(
            $"https://graph.microsoft.com{path}",
            token, activity, "graph",
            method: httpMethod,
            jsonBody: hasBody && httpMethod != HttpMethod.Get ? body : null,
            includeTimestamp: true);
        return httpMethod == HttpMethod.Get && IsSubscribedSkusPath(path) ? AppendLicenseSummary(response) : response;
    }

    internal static bool IsSubscribedSkusPath(string path)
    {
        var bare = path.Split('?', 2)[0].TrimEnd('/');
        return bare.Equals("/v1.0/subscribedSkus", StringComparison.OrdinalIgnoreCase)
            || bare.Equals("/beta/subscribedSkus", StringComparison.OrdinalIgnoreCase);
    }

    // Adds host-computed per-SKU and total seat arithmetic so answers never add or subtract seat counts in prose.
    internal static string AppendLicenseSummary(string response, DateTimeOffset? retrievedAtUtc = null)
    {
        if (!response.StartsWith("HTTP 200", StringComparison.Ordinal)) return response;
        var start = response.IndexOf('{');
        if (start < 0) return response;
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(response[start..]) is not System.Text.Json.Nodes.JsonObject root
                || root["value"] is not System.Text.Json.Nodes.JsonArray skus) return response;
            var rows = new List<(string Sku, string Status, long? Enabled, long? Assigned, long? Unassigned)>();
            foreach (var sku in skus.OfType<System.Text.Json.Nodes.JsonObject>())
            {
                long? Number(System.Text.Json.Nodes.JsonNode? node) =>
                    node is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<long>(out var n) ? n : null;
                var enabled = Number(sku["prepaidUnits"]?["enabled"]);
                var assigned = Number(sku["consumedUnits"]);
                rows.Add((sku["skuPartNumber"]?.GetValue<string>() ?? sku["skuId"]?.GetValue<string>() ?? "unknown SKU",
                    sku["capabilityStatus"]?.GetValue<string>() ?? "unknown",
                    enabled, assigned,
                    enabled is null || assigned is null ? null : Math.Max(enabled.Value - assigned.Value, 0)));
            }
            var complete = rows.All(r => r.Unassigned is not null);
            var totalEnabled = rows.Sum(r => r.Enabled ?? 0);
            var totalAssigned = rows.Sum(r => r.Assigned ?? 0);
            var totalUnassigned = rows.Sum(r => r.Unassigned ?? 0);
            static string N(long? value) => value?.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
            var table = new System.Text.StringBuilder()
                .AppendLine("| License (SKU) | Status | Purchased | Enabled inventory | Assigned | Unassigned enabled | Monthly cost of unassigned |")
                .AppendLine("|---|---|---|---:|---:|---:|---|");
            foreach (var row in rows.OrderByDescending(r => r.Unassigned ?? -1).ThenBy(r => r.Sku, StringComparer.Ordinal))
                table.AppendLine($"| {row.Sku.Replace("|", "\\|", StringComparison.Ordinal)} | {row.Status} | Not returned by Graph | {N(row.Enabled)} | {N(row.Assigned)} | {N(row.Unassigned)} | Unknown (no contract rate) |");
            table.Append($"| **Total** | | **Not returned by Graph** | **{N(totalEnabled)}** | **{N(totalAssigned)}** | **{N(totalUnassigned)}** | **Unknown** |");
            var retrieved = (retrievedAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
            var retrievedText = retrieved.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            root["_licenseSummary"] = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new
            {
                note = "Host-computed from this response (not source data). Present headline, answerTable and freshness verbatim; do not recompute seat counts. Enabled is license inventory, not invoice-verified purchases; Graph returns no contract prices, so the cost of unassigned seats is unknown without the customer's price sheet or invoice.",
                headline = $"{N(totalUnassigned)} enabled Microsoft 365 license seats are unassigned across {rows.Count} SKU{(rows.Count == 1 ? "" : "s")} ({N(totalEnabled)} enabled, {N(totalAssigned)} assigned); their monthly cost cannot be verified without contract rates.",
                answerTable = table.ToString(),
                retrievedAtUtc = retrieved.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                freshness = $"Microsoft 365 license inventory retrieved from Microsoft Graph subscribedSkus at {retrievedText} UTC; Graph reports no source as-of time for this inventory, so assignment changes after retrieval are not reflected.",
                complete,
                totals = new { skuCount = rows.Count, enabled = totalEnabled, assigned = totalAssigned, unassignedEnabled = totalUnassigned },
                skus = rows.Select(r => new { skuPartNumber = r.Sku, capabilityStatus = r.Status, enabled = r.Enabled, assigned = r.Assigned, unassignedEnabled = r.Unassigned })
            }));
            return response[..start] + root.ToJsonString();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return response;
        }
    }

    private sealed record QueryContract(string[] Endpoints, string[] AllowedOptions, string Guidance);

    private static readonly QueryContract[] QueryContracts =
    [
        new(["/v1.0/subscribedSkus", "/beta/subscribedSkus"], ["$select"],
            "subscribedSkus supports only $select. Remove unsupported options such as $top, $filter and $search."),
        new(["/beta/reports/getMicrosoft365CopilotUsageUserDetail(", "/beta/reports/getMicrosoft365CopilotUserCountSummary("], ["$format"],
            "Legacy Copilot report functions support only $format as a query option, not $filter, $top or $select. Preserve the requested period and analyze the returned report without claiming a server-side filter."),
        new(["/v1.0/copilot/reports/getMicrosoft365CopilotUsageUserDetail(", "/beta/copilot/reports/getMicrosoft365CopilotUsageUserDetail(",
            "/v1.0/copilot/reports/getMicrosoft365CopilotUserCountSummary(", "/beta/copilot/reports/getMicrosoft365CopilotUserCountSummary("], ["$format"],
            "Copilot usage reports use period and optional version function arguments, not $filter, $top or $select query options. The v1.0 response is CSV; beta is JSON.")
    ];

    internal static string? ValidateQueryParameters(string path)
    {
        var separator = path.IndexOf('?');
        if (separator < 0) return null;
        var endpoint = path[..separator].TrimEnd('/');
        var contract = QueryContracts.FirstOrDefault(rule => rule.Endpoints.Any(pattern =>
            pattern.EndsWith('(') ? endpoint.StartsWith(pattern, StringComparison.OrdinalIgnoreCase)
                : endpoint.Equals(pattern, StringComparison.OrdinalIgnoreCase)));
        if (contract is null) return null;
        var options = QueryHelpers.ParseQuery(path[separator..]);
        return options.Keys.Any(option => !contract.AllowedOptions.Contains(option, StringComparer.Ordinal))
            ? $"HTTP 400 BadRequest\n{contract.Guidance} No request was sent."
            : null;
    }
}
