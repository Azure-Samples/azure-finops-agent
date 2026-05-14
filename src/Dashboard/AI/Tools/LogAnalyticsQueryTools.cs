using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;

using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Infrastructure;

namespace AzureFinOps.Dashboard.AI.Tools;

/// <summary>
/// Queries Log Analytics workspaces and Application Insights via their direct query APIs.
/// Uses a Log Analytics-scoped token (also accepted by App Insights query API).
/// </summary>
public class LogAnalyticsQueryTools
{
    private readonly UserTokens _tokens;

    public LogAnalyticsQueryTools(UserTokens tokens) => _tokens = tokens;

    public IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(QueryLogAnalytics, "QueryLogAnalytics", @"Runs a KQL query against a Log Analytics workspace or Application Insights component.
DATA SCOPING: ALWAYS use summarize/top/take/where to limit. Use bin(TimeGenerated, 1d) for time aggregation — never raw per-minute. Project only needed columns. Start aggregated, then drill down.
LOG ANALYTICS: workspaceId is the workspace GUID — find via QueryAzure GET .../Microsoft.OperationalInsights/workspaces (customerId field).
APP INSIGHTS: appId is the App Insights component GUID; set target='appinsights'.

FinOps-relevant tables (you know KQL syntax):
- Perf / InsightsMetrics — VM CPU, memory, disk, network. Idle-VM detection: AvgCPU < 5% over 7d.
- Heartbeat — gaps indicate offline/deallocated VMs (LastHeartbeat < ago(7d) = potentially orphaned but billed).
- KubePodInventory / ContainerInventory — AKS pod requests vs limits for over-provisioning. ContainerLog — often #1 ingestion cost driver; group by ContainerID and sum(_BilledSize).
- AzureMetrics — PaaS metrics (SQL DTU, Cosmos RU/s).
- AzureDiagnostics — diagnostic logs (App Gateway, SQL, Firewall, Key Vault).
- AzureActivity — who created/deleted/modified resources (OperationName, Caller); cost attribution audit trail.
- AppRequests / AppDependencies — App Insights request and dependency telemetry.
- Usage — Log Analytics ingestion volume per DataType. sum(Quantity)/1024 = GB/day. Identifies top ingestion cost drivers.
- _BilledSize — per-record ingestion size column on all tables; use for cost attribution.
- SecurityEvent / SecurityAlert / Syslog / W3CIISLog / Update — security/OS/web/patch tables.");
    }

    private async Task<string> QueryLogAnalytics(
        [Description("The workspace GUID (Log Analytics) or app GUID (App Insights)")] string id,
        [Description("KQL query to execute")] string query,
        [Description("Optional timespan, e.g. PT1H, P1D, P7D, P30D. Default: P1D")] string? timespan = "P1D",
        [Description("Target API: 'loganalytics' (default) or 'appinsights'")] string? target = "loganalytics")
    {
        using var activity = HttpHelper.Telemetry.StartActivity("QueryLogAnalytics");
        activity?.SetTag("la.id", id);
        activity?.SetTag("la.target", target);
        activity?.SetTag("la.query", query?.Length > 500 ? query[..500] + "..." : query);
        activity?.SetTag("la.timespan", timespan);

        var token = _tokens.LogAnalyticsToken;
        if (string.IsNullOrEmpty(token))
            return HttpHelper.TokenMissing("LogAnalyticsToken", activity, "la");

        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(query))
        {
            activity?.SetTag("la.result", "invalid_input");
            return $"HTTP 400 BadRequest\nMissing required parameters: id='{id}', query='{query}'. Both are required.";
        }

        var isAppInsights = target?.Trim().Equals("appinsights", StringComparison.OrdinalIgnoreCase) == true;
        var baseUrl = isAppInsights
            ? $"https://api.applicationinsights.io/v1/apps/{Uri.EscapeDataString(id)}/query"
            : $"https://api.loganalytics.io/v1/workspaces/{Uri.EscapeDataString(id)}/query";

        var bodyObj = string.IsNullOrWhiteSpace(timespan)
            ? new { query }
            : (object)new { query, timespan };

        return await HttpHelper.SendWithRetryAsync(
            baseUrl, token, activity, "la",
            method: HttpMethod.Post,
            jsonBody: JsonSerializer.Serialize(bodyObj));
    }

}
