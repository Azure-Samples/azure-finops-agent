using System.ComponentModel;
using System.Diagnostics;
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
        yield return AIFunctionFactory.Create(QueryGraph, "QueryGraph", @"Calls Microsoft Graph API (https://graph.microsoft.com) using the signed-in user's token. Returns raw JSON.
Method: GET only — every consented Graph scope is read-only (*.Read.All / Reports.Read.All) and write methods are blocked at the code level.
DATA SCOPING: ALWAYS use $select to pick only needed fields, $top to limit rows, $filter to scope. Never fetch full user objects. Paginate via @odata.nextLink for large tenants.

Use standard Graph URL conventions; you know the v1.0 surface. FinOps-relevant areas:
- Licenses: /v1.0/subscribedSkus (consumedUnits vs prepaidUnits.enabled = unused licenses), /v1.0/users?$select=assignedLicenses
- M365 usage reports (period='D30'): /v1.0/reports/getOffice365ActiveUserDetail, getMailboxUsageDetail, getTeamsUserActivityUserDetail, getOneDriveUsageAccountDetail, getSharePointSiteUsageDetail, getM365AppUserDetail
- M365 Copilot usage (BETA-ONLY — v1.0 returns 404): GET /beta/reports/getMicrosoft365CopilotUsageUserDetail(period='D30'), GET /beta/reports/getMicrosoft365CopilotUserCountSummary(period='D30') — find users with Copilot licenses but no activity
- Intune: /v1.0/deviceManagement/managedDevices (use /beta/ only for preview-only fields)
- Directory / chargeback: /v1.0/organization, /v1.0/users (department/companyName/officeLocation), /v1.0/groups, /v1.0/administrativeUnits, /v1.0/users/{id}/manager
- Security: /v1.0/security/secureScores
- Apps & roles: /v1.0/applications, /v1.0/servicePrincipals, /v1.0/directoryRoles[/{id}/members]
- Domains: /v1.0/domains");
    }

    private async Task<string> QueryGraph(
        [Description("API path starting with /, e.g. /v1.0/subscribedSkus")] string path,
        [Description("HTTP method: GET only. Write methods are blocked at the code level.")] string? method = "GET",
        [Description("Ignored — QueryGraph is GET-only and never sends a request body.")] string? body = null)
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

        // Read-only enforcement: every consented Graph scope is read-only and the
        // UI consent table promises \"read-only by scope definition\" — block write
        // methods here as defense in depth instead of relying on Graph to 403 them.
        if (httpMethod != HttpMethod.Get)
        {
            activity?.SetTag("graph.result", "blocked_write");
            activity?.SetStatus(ActivityStatusCode.Error, "Non-GET blocked");
            return "HTTP 403 Forbidden\nQueryGraph is read-only (GET only) — all consented Microsoft Graph scopes are read-only. Re-issue the request as GET.";
        }

        return await HttpHelper.SendWithRetryAsync(
            $"https://graph.microsoft.com{path}",
            token, activity, "graph");
    }
}
