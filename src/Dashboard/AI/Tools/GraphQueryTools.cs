using System.ComponentModel;
using Microsoft.Extensions.AI;

using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Infrastructure;

namespace AzureFinOps.Dashboard.AI.Tools;

/// <summary>
/// Thin Microsoft Graph pass-through using the user's delegated Graph token.
/// The model chooses the endpoint; the host only enforces method and token rules.
/// </summary>
public sealed class GraphQueryTools(UserTokens tokens)
{
    public IEnumerable<AIFunction> Create() =>
    [
        AIFunctionFactory.Create(QueryGraph, nameof(QueryGraph), """
            Calls Microsoft Graph (https://graph.microsoft.com) with the signed-in user's delegated token and returns the raw response (JSON, or CSV for report functions). The Current UTC time line is the retrieval time, not the source refresh date.
            You choose the endpoint. When unsure of a path, its supported query options, permission or response shape, read the reference first (FetchPublicWebPage on https://learn.microsoft.com/graph/api/overview, or the OpenAPI metadata in https://github.com/microsoftgraph/msgraph-metadata) instead of guessing: many endpoints, such as subscribedSkus and report functions, reject $filter/$top/$select.
            Request only the fields and rows you need and follow @odata.nextLink when the complete set is required. Preserve report periods, refresh dates and paging coverage.
            DELETE is blocked. Standard consent tiers are read-only, so writes return 403: report that instead of retrying.
            """),
    ];

    private async Task<string> QueryGraph(
        [Description("Graph path starting with /v1.0/ or /beta/, including only query options that endpoint supports.")] string path,
        [Description("HTTP method: GET, POST, PUT, or PATCH (DELETE is blocked).")] string? method = "GET",
        [Description("Optional JSON request body for POST/PUT/PATCH. Omit for GET.")] string? body = null)
    {
        using var activity = HttpHelper.Telemetry.StartActivity(nameof(QueryGraph));
        activity?.SetTag("graph.method", method);
        activity?.SetTag("graph.path", path);
        activity?.SetTag("graph.has_body", !string.IsNullOrWhiteSpace(body));

        if (string.IsNullOrEmpty(tokens.GraphToken))
            return HttpHelper.TokenMissing("GraphToken", activity, "graph");
        if (path is not ['/', ..])
        {
            activity?.SetTag("graph.result", "invalid_path");
            return $"HTTP 400 BadRequest\nInvalid path: '{path}'. Path must start with /.";
        }

        var (httpMethod, methodError) = HttpHelper.ResolveMethod(method, activity, "graph");
        if (methodError is not null) return methodError;
        var result = await HttpHelper.SendWithRetryAsync(
            $"https://graph.microsoft.com{path}", tokens.GraphToken, activity, "graph",
            method: httpMethod,
            jsonBody: !string.IsNullOrWhiteSpace(body) && httpMethod != HttpMethod.Get ? body : null,
            includeTimestamp: true);
        return IsReportServiceAbsent(path, result) ? ReportServiceAbsent(result, activity) : result;
    }

    // Graph answers report functions with 404 UnknownTenantId when Microsoft 365 usage reporting is not
    // provisioned for the tenant. That is a determinate source state, not a failed request.
    internal static bool IsReportServiceAbsent(string path, string result) =>
        result.StartsWith("HTTP 404", StringComparison.Ordinal)
        && result.Contains("UnknownTenantId", StringComparison.Ordinal)
        && path.Contains("/reports/", StringComparison.OrdinalIgnoreCase);

    internal static string ReportServiceAbsent(string result, System.Diagnostics.Activity? activity)
    {
        activity?.SetTag("graph.result", "report_service_absent");
        var retrieved = result.Split('\n').FirstOrDefault(line => line.StartsWith("Current UTC time:", StringComparison.Ordinal))?.Trim();
        return $$"""
            {{retrieved ?? ""}}
            {"reportServiceProvisioned":false,"sourceStatus":404,"sourceCode":"UnknownTenantId","meaning":"Microsoft 365 usage reporting has no data service for this tenant, so no per-user activity rows exist from this source. Activity for licensed users is unknown from reports; combine with subscribedSkus/assignedLicenses to state what is determinate (for example zero assigned seats means zero licensed users to be active or inactive)."}
            """;
    }
}