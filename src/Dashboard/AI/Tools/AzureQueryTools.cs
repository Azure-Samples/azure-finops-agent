using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;

using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Infrastructure;

namespace AzureFinOps.Dashboard.AI.Tools;

/// <summary>
/// Single tool for querying any Azure ARM API using the user's delegated access token.
/// The LLM constructs the URL and optional body; this tool executes the HTTP request.
/// All calls are traced via OpenTelemetry → Application Insights for analysis.
///
/// Security model: GET, POST, PUT, and PATCH are allowed. DELETE is blocked at the
/// code level. Beyond that, the user's Entra RBAC role is the security boundary —
/// assign Reader / Cost Management Reader for read-only access.
/// </summary>
public class AzureQueryTools
{
    private readonly UserTokens _tokens;

    public AzureQueryTools(UserTokens tokens) => _tokens = tokens;

    public IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(QueryAzure, "QueryAzure", @"Queries Azure ARM REST APIs (https://management.azure.com) using the signed-in user's delegated token. Returns raw JSON.
Methods: GET, POST, PUT, PATCH. DELETE is blocked at the code level. The user's Entra RBAC is the effective access boundary.

Use standard ARM URL conventions; you know the resource providers and current api-versions. Common surfaces: Microsoft.CostManagement (query/forecast/exports), Microsoft.Consumption (budgets/pricesheets/reservation*), Microsoft.Capacity (reservations), Microsoft.BillingBenefits (savingsPlans), Microsoft.Advisor (recommendations), Microsoft.ResourceGraph (KQL across subs), Microsoft.Insights (metrics/diagnostics/autoscale), Microsoft.Compute, Microsoft.ContainerService, Microsoft.Network, Microsoft.Storage, Microsoft.Sql, Microsoft.Web, Microsoft.OperationalInsights, Microsoft.MachineLearningServices, Microsoft.CognitiveServices, Microsoft.App, Microsoft.Authorization (RBAC/Policy/Locks), Microsoft.Management, Microsoft.Quota, Microsoft.Carbon, Microsoft.Migrate, Microsoft.Support, Microsoft.ResourceHealth, Microsoft.Security.

=== NON-OBVIOUS RULES (read carefully) ===
{scope} GRAMMAR (REQUIRED for ALL Microsoft.CostManagement, Microsoft.Consumption, and Microsoft.CostManagement/budgets paths) — must be ONE of:
  /subscriptions/{subId}
  /subscriptions/{subId}/resourceGroups/{rgName}
  /providers/Microsoft.Management/managementGroups/{mgId}
  /providers/Microsoft.Billing/billingAccounts/{billingAccountId}[/billingProfiles/{id}|/invoiceSections/{id}]
Never bare /providers/Microsoft.CostManagement/... — that returns 400.

COST MANAGEMENT QUERY: ALWAYS group by a real dimension (ServiceName, ResourceGroupName, MeterCategory). Do NOT add 'UsageDate' to the grouping array — it's a response column, not a dimension; use granularity=""Daily"" for per-day. Never request raw ungrouped cost data.

THROTTLING: Cost Management /query and /forecast are aggressively throttled per-tenant. The agent retries 429s up to 5× with backoff. Do NOT call multiple CostManagement endpoints in parallel from the same turn — Resource Graph and Advisor parallelize fine.

RESOURCE GRAPH (POST /providers/Microsoft.ResourceGraph/resources): always use 'project' to limit columns and 'top N' to limit rows.

SPOT QUOTA: Spot/low-priority VM quota is a SINGLE regional bucket called 'lowPriorityCores' (NOT per VM family) — covers ALL spot VMs including H100/A100. Standard quotas are per-family ('standardNDSH100v5Family', 'StandardNCadsH100v5Family'). Microsoft.Quota requires RP registration (PUT /subscriptions/{subId}/providers/Microsoft.Quota/register) — fall back to GET .../Microsoft.Compute/locations/{region}/usages if not registered.

FOUNDRY / AZURE OPENAI QUOTA: Lives under Microsoft.CognitiveServices, NOT prices.azure.com. Per-region quota: GET /subscriptions/{id}/providers/Microsoft.CognitiveServices/locations/{region}/usages — returns name.value entries like 'OpenAI.GlobalStandard.gpt-5.6-sol'. For deployments: GET .../accounts/{name}/deployments returns properties.model.name + sku.capacity (TPM in thousands).

MIGRATE: Use resource type 'assessmentProjects' (NOT 'migrateProjects' — returns 404).

CONSUMPTION DEPRECATIONS: usageDetails → use Microsoft.CostManagement/generateCostDetailsReport. reservationDetails → use Microsoft.CostManagement/generateReservationDetailsReport.

For public retail pricing use https://prices.azure.com (no auth) with ?$filter=armRegionName eq '...' and serviceName eq '...' and armSkuName eq '...'&$top=20.");


        yield return AIFunctionFactory.Create(BulkAzureRequest, "BulkAzureRequest", @"Executes MANY Azure ARM requests in ONE tool call, in parallel, server-side. Use this whenever you would otherwise loop QueryAzure for the same kind of operation across multiple resources (bulk tagging, cleanup discovery, autoshutdown rollout, budget rollout across subs, multi-resource right-sizing, RBAC fan-out, etc.).
Input: requestsJson = JSON array of {""method"":""GET|POST|PUT|PATCH"",""path"":""/...?api-version=..."",""body"":""<optional JSON string>""}.
Optional: parallelism (default 20, max 50), stopOnFirstError (default false).
Returns ONE compact JSON summary: {""total"":N,""succeeded"":X,""failed"":Y,""durationMs"":Z,""failures"":[{""index"":i,""status"":code,""path"":""..."",""error"":""...""}],""successSamples"":[{""path"":""..."",""name"":""...""}]}.
DELETE is still blocked at the code level. Same per-request response trimming as QueryAzure (PUT/PATCH echoes are compacted). Throttling-aware: 429 retries are handled per request, batches stay below ARM's 1200 writes/hour/sub.
Use this INSTEAD of looping QueryAzure when you have ≥5 similar requests. Build the request list from your prior Resource Graph discovery query in the same turn.");
    }

    private async Task<string> QueryAzure(
        [Description("HTTP method: GET, POST, PUT, or PATCH (DELETE is blocked)")] string method,
        [Description("API path starting with /, e.g. /subscriptions?api-version=2022-12-01")] string path,
        [Description("Optional JSON request body for POST/PUT/PATCH requests. Omit or leave empty for GET.")] string? body = null)
    {
        using var activity = HttpHelper.Telemetry.StartActivity("QueryAzure");
        activity?.SetTag("azure.method", method);
        activity?.SetTag("azure.path", path);
        activity?.SetTag("azure.has_body", !string.IsNullOrWhiteSpace(body));
        if (!string.IsNullOrWhiteSpace(body))
            activity?.SetTag("azure.body", body.Length > 2000 ? body[..2000] + "..." : body);

        var token = _tokens.AzureToken;
        if (string.IsNullOrEmpty(token))
            return HttpHelper.TokenMissing("AzureToken", activity, "azure");

        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/'))
        {
            activity?.SetTag("azure.result", "invalid_path");
            activity?.SetStatus(ActivityStatusCode.Error, "Invalid path");
            return $"HTTP 400 BadRequest\nInvalid path: '{path}'. Path must start with /.";
        }

        // Scope-prefix preflight: catches the #1 production failure pattern observed in App Insights —
        // the LLM emitting bare /providers/Microsoft.CostManagement|Consumption|... paths without the
        // required {scope} prefix (subscriptions / resourceGroups / managementGroups / billingAccounts).
        // ARM responds 404 InvalidResourceType in that case; we return a precise 400 with the grammar
        // so the LLM corrects on the next turn instead of burning a round-trip.
        var scopeError = ValidateScopePrefix(path);
        if (scopeError is not null)
        {
            activity?.SetTag("azure.result", "missing_scope");
            activity?.SetStatus(ActivityStatusCode.Error, "Missing scope prefix");
            return scopeError;
        }

        var (httpMethod, methodError) = HttpHelper.ResolveMethod(method, activity, "azure");
        if (methodError is not null) return methodError;

        var hasBody = !string.IsNullOrWhiteSpace(body);
        return await HttpHelper.SendWithRetryAsync(
            $"https://management.azure.com{path}",
            token, activity, "azure",
            method: httpMethod,
            jsonBody: hasBody && httpMethod != HttpMethod.Get ? body : null,
            includeTimestamp: true);
    }

    /// <summary>
    /// Returns null if the path is acceptable, otherwise a ready-to-return HTTP 400 message explaining
    /// the missing {scope} prefix. Scope-required providers (Cost Management, Consumption budgets,
    /// PolicyInsights states, etc.) MUST be prefixed with one of the five canonical scope shapes.
    /// Bare /providers/Microsoft.CostManagement/query was 5/27 of all 4xx failures in the last 5 days.
    /// </summary>
    private static string? ValidateScopePrefix(string path)
    {
        // Strip query string for the check
        var qIdx = path.IndexOf('?');
        var clean = qIdx >= 0 ? path[..qIdx] : path;

        // Only enforce on the providers that actually require {scope}. Cost Management is the big one.
        // Consumption/budgets and PolicyInsights/policyStates also require it. ResourceGraph, Capacity,
        // BillingBenefits, Billing, Advisor, etc. live at root and are unaffected.
        string[] scopeRequired =
        [
            "/providers/Microsoft.CostManagement/",
            "/providers/Microsoft.Consumption/budgets",
            "/providers/Microsoft.PolicyInsights/policyStates",
        ];

        foreach (var marker in scopeRequired)
        {
            if (!clean.Contains(marker, StringComparison.OrdinalIgnoreCase)) continue;
            // Acceptable: the marker is preceded by a valid scope segment.
            if (clean.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase)
                || clean.StartsWith("/providers/Microsoft.Management/managementGroups/", StringComparison.OrdinalIgnoreCase)
                || clean.StartsWith("/providers/Microsoft.Billing/billingAccounts/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return "HTTP 400 BadRequest\n" +
                   $"Path is missing the required {{scope}} prefix before '{marker.TrimEnd('/')}'.\n" +
                   "Prepend exactly ONE of:\n" +
                   "  /subscriptions/{subId}\n" +
                   "  /subscriptions/{subId}/resourceGroups/{rgName}\n" +
                   "  /providers/Microsoft.Management/managementGroups/{mgId}\n" +
                   "  /providers/Microsoft.Billing/billingAccounts/{billingAccountId}\n" +
                   "  /providers/Microsoft.Billing/billingAccounts/{billingAccountId}/billingProfiles/{profileId}\n" +
                   "Example: POST /subscriptions/abc-123/providers/Microsoft.CostManagement/query?api-version=2025-03-01";
        }
        return null;
    }

    private async Task<string> BulkAzureRequest(
        [Description("JSON array of {method,path,body?} objects, e.g. [{\"method\":\"PATCH\",\"path\":\"/subscriptions/.../tags/default?api-version=2021-04-01\",\"body\":\"{...}\"}]")] string requestsJson,
        [Description("Max parallel requests in flight. Default 20, max 50.")] int parallelism = 20,
        [Description("Stop the whole bulk run on the first failure. Default false (continue and report all failures).")] bool stopOnFirstError = false)
    {
        using var activity = HttpHelper.Telemetry.StartActivity("BulkAzureRequest");
        var token = _tokens.AzureToken;
        if (string.IsNullOrEmpty(token))
            return HttpHelper.TokenMissing("AzureToken", activity, "bulk");

        if (string.IsNullOrWhiteSpace(requestsJson))
            return "HTTP 400 BadRequest\nrequestsJson is empty.";

        List<BulkRequestItem>? items;
        try
        {
            items = JsonSerializer.Deserialize<List<BulkRequestItem>>(
                requestsJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            return $"HTTP 400 BadRequest\nInvalid requestsJson: {ex.Message}";
        }
        if (items is null || items.Count == 0)
            return "HTTP 400 BadRequest\nrequestsJson must be a non-empty JSON array.";

        var maxPar = Math.Clamp(parallelism, 1, 50);
        activity?.SetTag("bulk.total", items.Count);
        activity?.SetTag("bulk.parallelism", maxPar);

        var sw = Stopwatch.StartNew();
        var results = new BulkResult[items.Count];
        var cts = new CancellationTokenSource();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, items.Count),
            new ParallelOptions { MaxDegreeOfParallelism = maxPar, CancellationToken = cts.Token },
            async (i, ct) =>
            {
                var item = items[i];
                var (httpMethod, methodError) = HttpHelper.ResolveMethod(item.Method, activity, "bulk");
                if (methodError is not null)
                {
                    results[i] = new BulkResult(i, 0, item.Path ?? "", methodError, false);
                    if (stopOnFirstError) cts.Cancel();
                    return;
                }
                if (string.IsNullOrWhiteSpace(item.Path) || !item.Path.StartsWith('/'))
                {
                    results[i] = new BulkResult(i, 400, item.Path ?? "", $"Invalid path: '{item.Path}'", false);
                    if (stopOnFirstError) cts.Cancel();
                    return;
                }

                var hasBody = !string.IsNullOrWhiteSpace(item.Body);
                var resp = await HttpHelper.SendWithRetryAsync(
                    $"https://management.azure.com{item.Path}",
                    token, activity, "bulk",
                    method: httpMethod,
                    jsonBody: hasBody && httpMethod != HttpMethod.Get ? item.Body : null,
                    includeTimestamp: false,
                    maxResponseChars: 1024); // hard cap so a stray verbose 4xx doesn't blow up the summary

                // Parse the "HTTP {code} {reason}\n{body}" envelope SendWithRetryAsync returns.
                var firstLine = resp.IndexOf('\n');
                var statusLine = firstLine > 0 ? resp[..firstLine] : resp;
                var statusParts = statusLine.Split(' ', 3);
                int.TryParse(statusParts.ElementAtOrDefault(1), out var status);
                var ok = status >= 200 && status < 300;
                var bodyPart = firstLine > 0 ? resp[(firstLine + 1)..] : "";

                string? name = null;
                try
                {
                    var idx = bodyPart.IndexOf('{');
                    if (idx >= 0)
                    {
                        using var doc = JsonDocument.Parse(bodyPart[idx..]);
                        if (doc.RootElement.ValueKind == JsonValueKind.Object
                            && doc.RootElement.TryGetProperty("name", out var n))
                            name = n.GetString();
                    }
                }
                catch { /* ignore */ }

                results[i] = new BulkResult(i, status, item.Path, ok ? null : bodyPart, ok, name);
                if (!ok && stopOnFirstError) cts.Cancel();
            });

        sw.Stop();
        var succeeded = results.Count(r => r is not null && r.Ok);
        var failed = results.Count(r => r is not null && !r.Ok);
        activity?.SetTag("bulk.succeeded", succeeded);
        activity?.SetTag("bulk.failed", failed);
        activity?.SetTag("bulk.duration_ms", sw.ElapsedMilliseconds);

        var failuresPayload = results
            .Where(r => r is not null && !r.Ok)
            .Take(20)
            .Select(r => new
            {
                index = r!.Index,
                status = r.Status,
                path = r.Path,
                error = (r.Error ?? "").Length > 200 ? r.Error![..200] : r.Error
            });

        var successSamples = results
            .Where(r => r is not null && r.Ok)
            .Take(5)
            .Select(r => new { path = r!.Path, name = r.Name });

        var summary = new
        {
            total = items.Count,
            succeeded,
            failed,
            durationMs = sw.ElapsedMilliseconds,
            stopped = cts.IsCancellationRequested && stopOnFirstError,
            failures = failuresPayload,
            successSamples
        };
        return JsonSerializer.Serialize(summary);
    }

    private sealed class BulkRequestItem
    {
        public string Method { get; set; } = "GET";
        public string Path { get; set; } = "";
        public string? Body { get; set; }
    }

    private sealed record BulkResult(int Index, int Status, string Path, string? Error, bool Ok, string? Name = null);
}
