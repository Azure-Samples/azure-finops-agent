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
        yield return AIFunctionFactory.Create(QueryGraph, "QueryGraph", @"Calls Microsoft Graph API (https://graph.microsoft.com) using the signed-in user's token.
Provide path starting with /. Returns raw JSON.
Allowed methods: GET, POST, PUT, PATCH. DELETE is blocked at the code level. The user's Graph permissions (delegated scopes + Entra role) govern what they can actually do.
DATA SCOPING: ALWAYS use $select to pick only needed fields, $top to limit rows, $filter to scope. Never fetch full user objects — use $select=displayName,department,userPrincipalName. For large tenants, use $top=50 and paginate via @odata.nextLink.

=== LICENSES (v1.0) ===
GET /v1.0/subscribedSkus — full license inventory for the tenant; returns skuPartNumber (e.g. ENTERPRISEPACK=E3, SPE_E5=E5, MICROSOFT_365_COPILOT), consumedUnits vs prepaidUnits.enabled (consumed < enabled = unused licenses costing money). GET /v1.0/subscribedSkus/{skuId} — single SKU detail.
GET /v1.0/users?$select=displayName,assignedLicenses,userPrincipalName&$top=50 — per-user license assignments; cross-reference with usage reports to find assigned-but-unused licenses.
GET /v1.0/reports/getOffice365ActiveUserDetail(period='D30') — per-user active usage across Exchange, OneDrive, SharePoint, Teams, Yammer with lastActivityDate; users with no activity = license waste candidates.

=== M365 USAGE REPORTS (v1.0) — identify license waste ===
GET /v1.0/reports/getMailboxUsageDetail(period='D30') — per-mailbox storage used, item count, last activity date; inactive mailboxes with E3/E5 licenses = savings opportunity.
GET /v1.0/reports/getTeamsUserActivityUserDetail(period='D30') — per-user Teams calls, meetings, chats, last activity; users with Teams license but no activity.
GET /v1.0/reports/getOneDriveUsageAccountDetail(period='D30') — per-user OneDrive storage (bytes), file count, last activity; find over-provisioned or unused OneDrive accounts.
GET /v1.0/reports/getSharePointSiteUsageDetail(period='D30') — per-site storage, page views, file count, last activity; identify unused SharePoint sites consuming storage quota.
GET /v1.0/reports/getOffice365ActiveUserCounts(period='D30') — daily active user counts by product (Exchange, OneDrive, SharePoint, Teams, Yammer); trend analysis for license optimization.
GET /v1.0/reports/getOffice365ServicesUserCounts(period='D30') — user counts by service (active, inactive, total); overall license utilization overview.
GET /v1.0/reports/getEmailActivityCounts(period='D30') — email send/receive/read counts per day.
GET /v1.0/reports/getEmailAppUsageUserDetail(period='D30') — which email clients users use (Outlook desktop/mobile/web, other).

=== M365 COPILOT USAGE (v1.0 preferred, beta as fallback) — identify unused Copilot licenses ===
GET /v1.0/reports/getM365AppUserDetail(period='D30') — per-user M365 app usage (Word, Excel, PowerPoint, Outlook, Teams, OneNote) across platforms (Windows, Mac, Web, Mobile); find users with M365 Apps license but zero app usage.
GET /v1.0/reports/getMicrosoft365CopilotUsageUserDetail(period='D30') — per-user Copilot usage (prompts, active days, last activity); find users with assigned Copilot license but no usage = immediate savings. Now in v1.0 — use this. The /beta/ path still works as a fallback if v1.0 returns 400.
GET /v1.0/reports/getMicrosoft365CopilotUserCountSummary(period='D30') — aggregate Copilot adoption: enabled users, active users, adoption rate. Now in v1.0 — prefer over /beta/.

=== DEVICE MANAGEMENT (v1.0 preferred, beta for newer surface) — Intune ===
GET /v1.0/deviceManagement/managedDevices?$select=deviceName,operatingSystem,complianceState,lastSyncDateTime,userPrincipalName&$top=50 — Intune managed devices (now GA in v1.0); find devices not syncing (lastSyncDateTime old) for license reconciliation. Use v1.0 unless the user explicitly needs a preview-only field — then fall back to /beta/.
GET /beta/deviceManagement/deviceCompliancePolicySettingStateSummaries — compliance posture summary across all policies (still beta-only).
GET /beta/deviceManagement/detectedApps?$top=50 — apps detected on managed devices (shadow IT discovery; still beta-only).

=== DIRECTORY (v1.0) — org structure for cost allocation ===
GET /v1.0/organization — tenant info (displayName, verifiedDomains, assignedPlans, createdDateTime).
GET /v1.0/groups?$select=displayName,mail,groupTypes,membershipRule&$top=50 — all groups; use for chargeback mapping (map cost centers to groups).
GET /v1.0/users?$select=displayName,department,companyName,jobTitle,officeLocation,userPrincipalName&$top=50 — org structure for cost allocation by department/location.
GET /v1.0/users/{id}/manager?$select=displayName,department — user's manager for org hierarchy.
GET /v1.0/administrativeUnits?$select=displayName,description&$top=50 — administrative units for scoped cost management.

=== SECURITY (v1.0) ===
GET /v1.0/security/secureScores?$top=1 — latest Microsoft Secure Score (currentScore, maxScore, averageComparativeScores) for security posture baseline.

=== APPLICATIONS (v1.0) ===
GET /v1.0/applications?$select=displayName,appId,createdDateTime,signInAudience&$top=50 — app registrations; find unused apps with expired credentials.
GET /v1.0/servicePrincipals?$select=displayName,appId,servicePrincipalType,accountEnabled&$top=50 — enterprise apps and service principals.

=== DOMAINS (v1.0) ===
GET /v1.0/domains — verified domains in the tenant.

=== ROLE MANAGEMENT (v1.0) ===
GET /v1.0/directoryRoles?$select=displayName,roleTemplateId — active directory roles (Global Admin, Billing Admin, etc.) for governance audit.
GET /v1.0/directoryRoles/{roleId}/members?$select=displayName,userPrincipalName — members of a specific role.");
    }

    private async Task<string> QueryGraph(
        [Description("API path starting with /, e.g. /v1.0/subscribedSkus")] string path,
        [Description("HTTP method: GET (default), POST, PUT, or PATCH. DELETE is blocked.")] string? method = "GET",
        [Description("Optional JSON request body for POST/PUT/PATCH requests.")] string? body = null)
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

        var hasBody = !string.IsNullOrWhiteSpace(body);
        return await HttpHelper.SendWithRetryAsync(
            $"https://graph.microsoft.com{path}",
            token, activity, "graph",
            method: httpMethod,
            jsonBody: hasBody && httpMethod != HttpMethod.Get ? body : null);
    }
}
