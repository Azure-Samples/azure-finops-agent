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
        yield return AIFunctionFactory.Create(QueryLogAnalytics, nameof(QueryLogAnalytics), """
            Runs KQL against a Log Analytics workspace (id = workspace customerId GUID, discoverable through QueryAzure on Microsoft.OperationalInsights/workspaces) or an Application Insights component (target='appinsights', id = appId).
            Filter by time and resource first, then summarize and project inside KQL; take/top only after aggregation so counts and totals stay complete.
            Discover populated tables with `Usage | summarize GB=sum(Quantity)/1024 by DataType` and columns with `<Table> | getschema` rather than guessing. Useful FinOps signals include Usage and _BilledSize (ingestion cost), Perf/InsightsMetrics (utilization), Heartbeat and AzureActivity (who changed what).
            """);
    }
    private async Task<string> QueryLogAnalytics(
        [Description("The workspace GUID (Log Analytics) or app GUID (App Insights)")] string id,
        [Description("KQL query with source-side where/summarize/project and top/take bounds so only the rows and columns needed for the answer are returned.")] string query,
        [Description("Bound the scan to the requested time range, e.g. PT1H, P1D, P7D, P30D. Default: P1D. Keep the KQL time predicate consistent with this range.")] string? timespan = "P1D",
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
