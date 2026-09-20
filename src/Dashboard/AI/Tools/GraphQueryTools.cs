using System.ComponentModel;
using System.Diagnostics;
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
Methods: GET, POST, PUT, PATCH. DELETE is blocked at the code level. NOTE: the standard consent tiers only grant read-only scopes (*.Read.All / Reports.Read.All), so write calls return 403 insufficient privileges unless the tenant has consented to write scopes — surface that to the user rather than retrying.
DATA SCOPING: use $select for needed fields, $top for a small page, and $filter for the requested scope wherever that Graph endpoint supports them. Prefer supported server-side reports and counts for summaries; do not invent query options on endpoints such as reports or subscribedSkus. Avoid full user objects and broad collections when a narrower request answers the question. Follow @odata.nextLink only while the requested result needs more rows. A limited page is not a tenant-wide count; disclose incomplete pagination and preserve totals.
For Copilot activity counts and inactive-user lists prefer GetCopilotUsage, which processes the supported report on the host and returns bounded, dated counts/pages instead of an oversized raw report.

Use standard Graph URL conventions; you know the v1.0 surface. FinOps-relevant areas:
- Licenses: /v1.0/subscribedSkus?$select=skuId,skuPartNumber,prepaidUnits,consumedUnits,capabilityStatus. This collection supports ONLY $select: never add $top, $filter or $search. Compare consumedUnits with prepaidUnits.enabled for unassigned seats; assignment is not proof of active use.
- M365 usage reports (period='D30'): /v1.0/reports/getOffice365ActiveUserDetail, getMailboxUsageDetail, getTeamsUserActivityUserDetail, getOneDriveUsageAccountDetail, getSharePointSiteUsageDetail, getM365AppUserDetail
- M365 Copilot usage: GET /v1.0/copilot/reports/getMicrosoft365CopilotUsageUserDetail(period='D30') or getMicrosoft365CopilotUserCountSummary(period='D30') returns CSV for licensed users. version='v1' is the default (D7/D30/D90/D180/ALL); version='v2' uses D28 instead of D30 and adds prompt counts/active days. The beta /copilot/reports equivalents return JSON. Do not add $filter, $top or $select to these functions. Legacy /beta/reports/getMicrosoft365CopilotUsageUserDetail and getMicrosoft365CopilotUserCountSummary support $format only, not $filter. Reports.Read.All plus a supported directory role is required. Missing report access or anonymized user names is not zero activity; licensed-user reports do not cover unlicensed Copilot Chat.
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
        return await HttpHelper.SendWithRetryAsync(
            $"https://graph.microsoft.com{path}",
            token, activity, "graph",
            method: httpMethod,
            jsonBody: hasBody && httpMethod != HttpMethod.Get ? body : null);
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
