using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Infrastructure;

namespace AzureFinOps.Dashboard.AI.Tools;

/// <summary>
/// Single tool for querying any Azure ARM API using the user's delegated access token.
/// The LLM constructs the URL and optional body; this tool executes the HTTP request.
/// All calls are traced via OpenTelemetry → Application Insights for analysis.
///
/// Security model: GET, PUT, and PATCH are allowed; POST is restricted to known
/// read-only query/report/calculation endpoints. Mutating action POSTs and DELETE
/// are blocked at the code level. Beyond that, the user's Entra RBAC role is the security boundary —
/// assign Reader / Cost Management Reader for read-only access.
/// </summary>
public class AzureQueryTools
{
    private readonly UserTokens _tokens;

    public AzureQueryTools(UserTokens tokens) => _tokens = tokens;

    public IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(QueryAzure, "QueryAzure", @"Queries Azure ARM REST APIs (https://management.azure.com) using the signed-in user's delegated token. Returns raw JSON.
Methods: GET, PUT, PATCH, plus allowlisted read-only POST endpoints. Mutating action POSTs and DELETE are blocked at the code level. The user's Entra RBAC is the effective access boundary.

PAYLOAD DISCIPLINE: filter, aggregate, project, and limit every broad read at the source using only options that the endpoint supports. Use `$filter`, `$select`, and a small `$top` where supported; otherwise choose a narrower resource endpoint or a scoped Resource Graph query. Resource Graph uses `where`, `summarize`, `project`, and `top`; Cost Management uses dataset filters and aggregation/grouping over the requested date range. Aggregate before limiting rows so totals are not calculated from a sample. For requested full results preserve pagination, counts, `_finops`/sourceEvidence and partial coverage. Never rely on response truncation or client-side filtering to make an unfiltered collection small.

Use standard ARM URL conventions; you know the resource providers and current api-versions. Common surfaces: Microsoft.CostManagement (query/forecast/exports), Microsoft.Consumption (budgets/pricesheets/reservation*), Microsoft.Capacity (reservations), Microsoft.BillingBenefits (savingsPlans), Microsoft.Advisor (recommendations), Microsoft.ResourceGraph (KQL across subs), Microsoft.Insights (metrics/diagnostics/autoscale), Microsoft.Compute, Microsoft.ContainerService, Microsoft.Network, Microsoft.Storage, Microsoft.Sql, Microsoft.Web, Microsoft.OperationalInsights, Microsoft.MachineLearningServices, Microsoft.CognitiveServices, Microsoft.App, Microsoft.Authorization (RBAC/Policy/Locks), Microsoft.Management, Microsoft.Quota, Microsoft.Carbon, Microsoft.Migrate, Microsoft.Support, Microsoft.ResourceHealth, Microsoft.Security.

=== NON-OBVIOUS RULES (read carefully) ===
{scope} GRAMMAR (REQUIRED for ALL Microsoft.CostManagement, Microsoft.Consumption, and Microsoft.CostManagement/budgets paths) — must be ONE of:
  /subscriptions/{subId}
  /subscriptions/{subId}/resourceGroups/{rgName}
  /providers/Microsoft.Management/managementGroups/{mgId}
  /providers/Microsoft.Billing/billingAccounts/{billingAccountId}[/billingProfiles/{id}|/invoiceSections/{id}]
Never bare /providers/Microsoft.CostManagement/... — that returns 400.

COST MANAGEMENT QUERY: use api-version=2026-08-01 and dataset.aggregation for totals. Add real grouping dimensions (ServiceName, ResourceGroupName, MeterCategory) only for the requested breakdown. Do NOT add 'UsageDate' to the grouping array — it's a response column, not a dimension; use granularity=""Daily"" for per-day. Never request raw cost detail rows for a summary. For totals across all subscriptions, call QueryCostsAcrossSubscriptions exactly once with connection-context scopes; never fan out one query per subscription yourself. For grouped detail across two or more known subscription scopes, use ONE BulkAzureRequest with parallelism=1 and the exact scopes, dates, cost type and filters. The host executes cost reads sequentially; do not spend a model round-trip on every subscription.

FORECASTS AND BUDGETS: In raw Cost Management /query and /forecast bodies, timePeriod.to is the INCLUSIVE last day: a whole-month window for September 2026 is from=2026-09-01, to=2026-09-30; never pass the next month's first day, which adds an extra day of rows. For a whole-month chart, a Daily forecast with includeActualCost=true and explicit month bounds can supply both actual and forecast rows. includeActualCost and includeFreshPartialCost are TOP-LEVEL request fields beside type/timeframe/timePeriod/dataset, NEVER under dataset.configuration. Keep dataset.aggregation for Cost/Sum; do not combine aggregation with dataset.configuration (the service rejects that). Shape example (substitute dates and preserve any requested cost type, filters and grouping): {""type"":""Usage"",""timeframe"":""Custom"",""timePeriod"":{""from"":""<requested-start>"",""to"":""<requested-end>""},""includeActualCost"":true,""dataset"":{""granularity"":""Daily"",""aggregation"":{""totalCost"":{""name"":""Cost"",""function"":""Sum""}}}}. Inspect CostStatus, dates and currency before summing nonoverlapping rows. For remaining spend, aggregate only forecast rows in the requested future window. Before comparing with a budget, inspect its timeGrain, filters, currentSpend AND forecastSpend. The budget's forecastSpend is an independent periodically evaluated projection, not the sum of your daily forecast. Explicitly disclose conflicting month-end estimates, especially opposite under/over-budget outcomes, before concluding whether the budget is safe. A generic freshness caveat does not reconcile them. Never mix incompatible scopes, filters, currencies or unknown date coverage.

DETAIL REQUESTS: for costs by resource/model, start with a valid resource/meter breakdown, not a totals-only query followed by another request for the actual question. Cost Management permits at most two grouping dimensions. At subscription scope use ResourceId plus Meter; derive subscription/resource-group from the scope or ResourceId instead of adding third/fourth grouping dimensions. At management-group scope use SubscriptionId plus ResourceId for resource attribution, then a targeted meter query only when still needed. Reuse returned detail to compute totals; follow pagination and disclose partial coverage. Token activity/inventory is not billed resource or model cost.

THROTTLING: the host serializes Cost Management /query and /forecast and automatically retries once after the full service deadline when it is at most five minutes. The UI reports that wait. A returned HTTP 429 means the bounded retry is exhausted or the deadline is longer; do not make another Cost Management call in this turn. Read _finops.retryAtUtc or retryAtUtc, report the exact deadline, and do not offer an immediate retry before it. Other independent read services remain available, but do not present their activity as billing detail.

RESOURCE GRAPH (POST /providers/Microsoft.ResourceGraph/resources): declare exactly one non-empty subscriptions array of GUID strings or managementGroups array of IDs in the JSON body, using the full requested connection-context scope. Never omit scope: the service would otherwise query other accessible subscriptions. Use project to limit columns and aggregate before limiting rows. KQL top REQUIRES a by expression: `top N by count_ desc`; after `order by`, use `take N`, NOT bare `top N`. Preserve full-result pagination and totals. Start with a supported table such as resources, then a single pipeline; not a leading union of parenthesized subqueries or multi-statement `let ...; let ...;`. For overall and resource-group tag percentages, use separate simple aggregate queries, or derive overall counts from complete grouped results with QueryToolResult. Never reference placeholder or undefined columns. Changing table capitalization or API versions does not fix an unsupported query shape.

SPOT QUOTA: Spot/low-priority VM quota is a SINGLE regional bucket called 'lowPriorityCores' (NOT per VM family) — covers ALL spot VMs including H100/A100. Standard quotas are per-family ('standardNDSH100v5Family', 'StandardNCadsH100v5Family'). Microsoft.Quota requires RP registration (PUT /subscriptions/{subId}/providers/Microsoft.Quota/register) — fall back to GET .../Microsoft.Compute/locations/{region}/usages if not registered.

FOUNDRY / AZURE OPENAI QUOTA: Lives under Microsoft.CognitiveServices, NOT prices.azure.com. Per-region quota: GET /subscriptions/{id}/providers/Microsoft.CognitiveServices/locations/{region}/usages — returns name.value entries like 'OpenAI.GlobalStandard.gpt-5.6-sol'. For deployments: GET .../accounts/{name}/deployments returns properties.model.name + sku.capacity (TPM in thousands).

MIGRATE: Use resource type 'assessmentProjects' (NOT 'migrateProjects' — returns 404).

CONSUMPTION DEPRECATIONS: usageDetails → use Microsoft.CostManagement/generateCostDetailsReport. reservationDetails → use Microsoft.CostManagement/generateReservationDetailsReport.

RESERVATION UTILIZATION: reservationSummaries is NOT a tenant-root endpoint. Discover reservation orders first, then GET /providers/Microsoft.Capacity/reservationOrders/{orderId}/providers/Microsoft.Consumption/reservationSummaries?api-version=2024-08-01&grain=monthly. A discovered billing-account or billing-profile scope is another supported route. Do not invent a scope or cycle API versions after a missing-scope error. Denied/missing utilization is unknown, never proof that rightsizing will not strand a commitment.

For public retail pricing use GetAzureRetailPricing, or GetAzureRetailPricingBatch for independent filter combinations; QueryAzure calls ARM only.");

        yield return AIFunctionFactory.Create(QueryCostsAcrossSubscriptions, "QueryCostsAcrossSubscriptions", @"Gets a reported Cost Management total and per-subscription breakdown in ONE agent tool call. Use this for requested subscription totals, not as a prerequisite for resource/service/model detail. For detailed spending questions use a valid grouped QueryAzure request first and derive totals from the detail when complete. Do not make users repeat the request for detail after a totals-only answer.
Input subscriptionsJson: the exact `subscriptions` JSON array supplied in the connection context ({id,name,...}). Input managementGroupId: the optional id/name from the context's managementGroups array. Dates are yyyy-MM-dd; `to` is the exclusive end date. Keep the exclusive-end label on the exact returned `to`; if showing the last included day instead, label that date inclusive, never exclusive.
DATA SCOPING: use only the requested dates and subscriptions, but include every requested subscription for a whole-estate total. Never take a top-N sample of subscriptions or filtered budget snapshots and label it total spend. This tool has no service/resource-group filter; use a scoped QueryAzure aggregate for those breakdowns. Its host-built queries aggregate before returning results; preserve sourceEvidence and all failed/unattempted scope coverage instead of re-querying returned totals.
Cost-query responses declare costType=ActualCost, timePeriod with an exclusive end, and aggregation=Cost. Reuse those successful results for the same period and scope; do not repeat them through QueryAzure merely to establish the cost type or request PreTaxCost instead. Budget snapshots have costType=null because this response does not establish a comparable query basis. For a matched ActualCost comparison, query only the missing period/basis and reuse any historical ActualCost results already returned.
    For the current calendar month, the tool first reads each subscription's unfiltered monthly budget `currentSpend` in parallel. Budgets are evaluated periodically: report this as a delayed MTD snapshot, not a real-time or finalized bill. For other periods it tries one management-group aggregate query, then the minimum sequential per-subscription fallback. Preserve sourceEvidence cache/freshness metadata. It stops immediately when Cost Management remains throttled and reports completed, failed, and unattempted scopes. Never call this tool twice in one turn after a 429.");


        yield return AIFunctionFactory.Create(BulkAzureRequest, "BulkAzureRequest", @"Executes MANY Azure ARM requests in ONE tool call, server-side. Independent non-cost requests can run in parallel. Any batch containing Cost Management /query or /forecast is automatically sequential, even if a larger parallelism was requested, and stops after a final cost 429. Use one batch for grouped cost detail across two or more known subscription scopes instead of a QueryAzure model round-trip per subscription. Keep every requested scope and the exact date window, cost type, filters and grouping; do not mix speculative management-group probes with subscription fallbacks.
Use this whenever you would otherwise loop QueryAzure for the same kind of operation across multiple resources (bulk tagging, cleanup discovery, autoshutdown rollout, budget rollout across subs, multi-resource right-sizing, RBAC fan-out, etc.).
Input: requestsJson = JSON array of {""method"":""GET|POST|PUT|PATCH"",""path"":""/...?api-version=..."",""body"":""<optional JSON string>""}.
Optional: parallelism (default 20, max 50), stopOnFirstError (default false).
    PAYLOAD DISCIPLINE: every read in requestsJson must filter, aggregate, project, and limit at the source using only supported API options. Prefer one scoped Resource Graph query over many broad inventory GETs. Never batch unfiltered collections and rely on trimming. Request parallelism=1 for Cost Management /query and /forecast; never fan out separate cost tools in parallel.
    Returns total, succeeded, failed, pending, unattempted, cancelled, stopped, complete, and indexed results with status, outcome, partial, body, error, and cost sourceEvidence. At most 200 requests, 4,000,000 response characters per item and a 12,000,000-character batch data budget; omitted bodies are explicitly partial, not complete evidence. Large complete batches return a queryable_tool_result schema and resultId for QueryToolResult. Source freshness and retry deadlines survive body omission. Narrow only those indexed reads if further detail is needed.
    DELETE and mutating action POSTs remain blocked. PUT/PATCH create exact owner/session-bound proposals requiring explicit UI approval. Accepted, pending, unknown, and awaitingApproval outcomes are not successful writes. Respect all returned retry deadlines; never retry Cost Management after a final 429 in this turn.
Use this INSTEAD of looping QueryAzure for two or more grouped cost reads, or five or more other similar requests. Cost scopes come from connection context; other resource targets come from the prior Resource Graph discovery query in the same turn.");
    }

    private async Task<string> QueryAzure(
        [Description("HTTP method: GET, POST, PUT, or PATCH (DELETE is blocked)")] string method,
        [Description("Scoped ARM path starting with / and an api-version. Use $filter/$select/$top only when the endpoint supports them; otherwise choose a narrower endpoint or Resource Graph query. Do not fetch full collections for a summary.")] string path,
        [Description("Optional JSON request body for POST/PUT/PATCH. Resource Graph POST requires an explicit subscriptions array of GUID strings or managementGroups array of IDs from the requested connection-context scope, plus query. KQL top requires by; use order by ... | take N rather than bare top N. For queries, filter and aggregate at the source and limit only after aggregation. Cost Management permits at most two grouping dimensions; use ResourceId plus Meter at subscription scope for resource/model detail. Preserve totals and coverage. Omit for GET.")] string? body = null)
    {
        using var activity = HttpHelper.Telemetry.StartActivity("QueryAzure");
        activity?.SetTag("azure.method", method);
        activity?.SetTag("azure.path", path);
        activity?.SetTag("azure.has_body", !string.IsNullOrWhiteSpace(body));
        if (!string.IsNullOrWhiteSpace(body))
            activity?.SetTag("azure.body_length", body.Length);

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
        if (httpMethod == HttpMethod.Post)
        {
            body = CanonicalJsonBody(body);
            var postError = ValidateReadOnlyPostPath(path, activity);
            if (postError is not null) return postError;
            var queryError = ValidateQueryBody(path, body);
            if (queryError is not null) return queryError;
        }

        var hasBody = !string.IsNullOrWhiteSpace(body);
        return await HttpHelper.SendWithRetryAsync(
            $"https://management.azure.com{path}",
            token, activity, "azure",
            method: httpMethod,
            jsonBody: hasBody && httpMethod != HttpMethod.Get ? body : null,
            includeTimestamp: true);
    }

    // Cost Management treats timePeriod.to as an inclusive day, whereas this
    // tool's contract is an exclusive end, so send the last included day.
    internal static object CostQueryTimePeriod(DateOnly fromDate, DateOnly toExclusive) => new
    {
        from = fromDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        to = toExclusive.AddDays(-1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
    };

    private async Task<string> QueryCostsAcrossSubscriptions(
        [Description("JSON array of all subscription objects in the requested scope, from connection context, with id and name fields. Never sample the array for an all-subscription total.")] string subscriptionsJson,
        [Description("Inclusive start date in yyyy-MM-dd format. Bound to the requested period; the full date range must not exceed 366 days.")] string from,
        [Description("Exclusive end date in yyyy-MM-dd format. Keep the requested window; do not broaden it to retrieve unrelated billing history.")] string to,
        [Description("Optional management-group id or full ARM path from the connection context")] string? managementGroupId = null)
    {
        using var activity = HttpHelper.Telemetry.StartActivity("QueryCostsAcrossSubscriptions");
        var token = _tokens.AzureToken;
        if (string.IsNullOrEmpty(token))
            return HttpHelper.TokenMissing("AzureToken", activity, "cost.cross_subscription");

        if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var fromDate)
            || !DateOnly.TryParseExact(to, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var toDate)
            || fromDate >= toDate
            || toDate.DayNumber - fromDate.DayNumber > 366)
        {
            return "HTTP 400 BadRequest\nfrom/to must be valid yyyy-MM-dd dates, from must precede to, and the range must not exceed 366 days.";
        }

        var scopes = new List<(string Id, string Name)>();
        try
        {
            using var doc = JsonDocument.Parse(subscriptionsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return "HTTP 400 BadRequest\nsubscriptionsJson must be a JSON array.";
            if (doc.RootElement.GetArrayLength() > 500)
                return "HTTP 400 BadRequest\nsubscriptionsJson supports at most 500 entries; split larger estates into explicit scopes.";

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var rawId = item.ValueKind == JsonValueKind.String
                    ? item.GetString()
                    : item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                rawId = rawId?.Trim();
                if (rawId?.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase) == true)
                    rawId = rawId["/subscriptions/".Length..].Trim('/');
                if (!Guid.TryParse(rawId, out var parsedId)) continue;

                var name = item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("name", out var nameEl)
                    ? nameEl.GetString()
                    : null;
                var canonicalId = parsedId.ToString();
                if (!seen.Add(canonicalId)) continue;
                scopes.Add((canonicalId, string.IsNullOrWhiteSpace(name) ? canonicalId : name!));
            }
        }
        catch (JsonException ex)
        {
            return $"HTTP 400 BadRequest\nInvalid subscriptionsJson: {ex.Message}";
        }

        if (scopes.Count == 0)
            return "HTTP 400 BadRequest\nsubscriptionsJson contained no valid subscription IDs.";

        // The Consumption budgets endpoint returns subscription-level reported
        // `currentSpend` without consuming the Cost Management query QPU pool.
        // It is valid only for the current calendar month and only when an
        // unfiltered monthly budget covers the whole subscription. Use this
        // before /query, while retaining the source's periodic-update caveat.
        var utcToday = DateOnly.FromDateTime(DateTime.UtcNow);
        var currentMonthStart = new DateOnly(utcToday.Year, utcToday.Month, 1);
        if (fromDate == currentMonthStart && toDate == utcToday.AddDays(1))
        {
            var budgetSpend = await TryReadCurrentMonthSpendFromBudgets(token, scopes, activity);
            if (budgetSpend is not null) return budgetSpend;
        }

        var body = JsonSerializer.Serialize(new
        {
            type = "ActualCost",
            timeframe = "Custom",
            timePeriod = CostQueryTimePeriod(fromDate, toDate),
            dataset = new
            {
                granularity = "None",
                aggregation = new { totalCost = new { name = "Cost", function = "Sum" } }
            }
        });

        Dictionary<string, CostScopeResult>? aggregateResults = null;
        var sourceEvidence = new List<JsonElement>();

        // Prefer one aggregate call. An accessible management group is not
        // guaranteed to contain the delegated subscriptions, so only 400/403/404
        // fall back; a 429 must stop immediately to protect the tenant quota.
        if (!string.IsNullOrWhiteSpace(managementGroupId))
        {
            var mgName = managementGroupId.Trim().TrimEnd('/').Split('/').Last();
            if (mgName.Length > 0)
            {
                var mgBody = JsonSerializer.Serialize(new
                {
                    type = "ActualCost",
                    timeframe = "Custom",
                    timePeriod = CostQueryTimePeriod(fromDate, toDate),
                    dataset = new
                    {
                        granularity = "None",
                        aggregation = new { totalCost = new { name = "Cost", function = "Sum" } },
                        grouping = new[]
                        {
                            new { type = "Dimension", name = "SubscriptionId" },
                            new { type = "Dimension", name = "SubscriptionName" }
                        }
                    }
                });
                var mgUrl = $"https://management.azure.com/providers/Microsoft.Management/managementGroups/{Uri.EscapeDataString(mgName)}/providers/Microsoft.CostManagement/query?api-version=2026-08-01";
                var mgResponse = await HttpHelper.SendWithRetryAsync(
                    mgUrl, token, activity, "cost.cross_subscription.mg",
                    method: HttpMethod.Post, jsonBody: mgBody);
                sourceEvidence.Add(ReadCostSourceEvidence(mgResponse));
                if (mgResponse.StartsWith("HTTP 200", StringComparison.Ordinal))
                {
                    var aggregate = ParseAggregateCostResponse(mgResponse, scopes);
                    if (aggregate.Error is null && aggregate.Results.Count == scopes.Count)
                        return BuildCostResponse("managementGroup", scopes, aggregate.Results, false, sourceEvidence, fromDate, toDate);

                    // Keep any requested subscriptions returned by the aggregate
                    // and query only the missing scopes below. Extra management-
                    // group subscriptions are ignored by ParseAggregateCostResponse.
                    aggregateResults = aggregate.Error is null
                        ? aggregate.Results
                        : new Dictionary<string, CostScopeResult>(StringComparer.OrdinalIgnoreCase);
                }
                if (mgResponse.StartsWith("HTTP 429", StringComparison.Ordinal))
                    return JsonSerializer.Serialize(new
                    {
                        complete = false,
                        source = "managementGroup",
                        costType = "ActualCost",
                        timePeriod = new { from = fromDate, to = toDate, endExclusive = true },
                        aggregation = "Cost",
                        throttled = true,
                        attempted = 1,
                        subscriptionCount = scopes.Count,
                        sourceEvidence,
                        detail = FirstLineAndBody(mgResponse, 500)
                    });
            }
        }

        aggregateResults ??= new Dictionary<string, CostScopeResult>(StringComparer.OrdinalIgnoreCase);
        var reusedAggregateResults = aggregateResults.Count > 0;
        var resultsById = aggregateResults;
        var remainingScopes = scopes.Where(s => !resultsById.ContainsKey(s.Id)).ToList();
        var throttled = false;

        for (var i = 0; i < remainingScopes.Count; i++)
        {
            var scope = remainingScopes[i];
            var url = $"https://management.azure.com/subscriptions/{scope.Id}/providers/Microsoft.CostManagement/query?api-version=2026-08-01";
            var response = await HttpHelper.SendWithRetryAsync(
                url, token, activity, "cost.cross_subscription.subscription",
                method: HttpMethod.Post, jsonBody: body);
            sourceEvidence.Add(ReadCostSourceEvidence(response));

            string? parseError = null;
            if (response.StartsWith("HTTP 200", StringComparison.Ordinal)
                && TryReadCost(response, out var cost, out var currency, out parseError))
            {
                resultsById[scope.Id] = new(scope.Id, scope.Name, 200, cost, currency, null);
                continue;
            }

            var status = ParseStatusCode(response);
            resultsById[scope.Id] = new(
                scope.Id,
                scope.Name,
                status,
                null,
                null,
                status == 200 ? parseError : FirstLineAndBody(response, 400));
            if (status == 429)
            {
                throttled = true;
                for (var j = i + 1; j < remainingScopes.Count; j++)
                {
                    var unattempted = remainingScopes[j];
                    resultsById[unattempted.Id] = new(
                        unattempted.Id,
                        unattempted.Name,
                        0,
                        null,
                        null,
                        "not attempted after tenant throttle");
                }
                break;
            }
        }

        var source = reusedAggregateResults ? "managementGroup+subscriptions" : "subscriptions";
        return BuildCostResponse(source, scopes, resultsById, throttled, sourceEvidence, fromDate, toDate);
    }

    private async Task<string?> TryReadCurrentMonthSpendFromBudgets(
        string token,
        IReadOnlyList<(string Id, string Name)> scopes,
        Activity? activity)
    {
        var utcToday = DateOnly.FromDateTime(DateTime.UtcNow);
        var currentMonthStart = new DateOnly(utcToday.Year, utcToday.Month, 1);
        var tasks = scopes.Select(async scope =>
        {
            var response = await HttpHelper.SendWithRetryAsync(
                $"https://management.azure.com/subscriptions/{scope.Id}/providers/Microsoft.Consumption/budgets?api-version=2024-08-01",
                token, null, "cost.cross_subscription.budget",
                bypassCostManagementGate: true,
                maxAttemptsOverride: 1);
            return ReadUnfilteredBudgetSpend(scope, response, currentMonthStart, utcToday);
        });
        var results = await Task.WhenAll(tasks);
        if (results.Any(r => !r.Success)) return null;

        var currencies = results
            .Select(r => r.Currency)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (currencies.Length != 1) return null;

        return JsonSerializer.Serialize(new
        {
            complete = true,
            source = "subscriptionBudgets.currentSpend",
            costType = (string?)null,
            timePeriod = new { from = currentMonthStart, to = utcToday.AddDays(1), endExclusive = true },
            _finops = new
            {
                cacheStatus = "queried",
                freshness = "periodic",
                retrievedAtUtc = DateTimeOffset.UtcNow,
                dataAsOfUtc = (DateTimeOffset?)null,
                caveat = "Budget currentSpend is evaluated periodically and may lag billing. This is a reported snapshot, not a real-time or finalized bill."
            },
            period = "currentMonthToDate",
            subscriptionCount = scopes.Count,
            totalCost = Math.Round(results.Sum(r => r.Cost), 6),
            currency = currencies[0],
            results = results.Select(r => new
            {
                subscriptionId = r.SubscriptionId,
                subscriptionName = r.SubscriptionName,
                status = 200,
                cost = Math.Round(r.Cost, 6),
                currency = r.Currency,
                budgetName = r.BudgetName
            })
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    internal static BudgetSpend ReadUnfilteredBudgetSpend(
        (string Id, string Name) scope,
        string response,
        DateOnly periodStart,
        DateOnly periodEndInclusive)
    {
        if (ParseStatusCode(response) != 200)
            return new(scope.Id, scope.Name, false, 0, null, null);

        try
        {
            using var doc = JsonDocument.Parse(ResponseBody(response));
            if (!doc.RootElement.TryGetProperty("value", out var values)
                || values.ValueKind != JsonValueKind.Array)
                return new(scope.Id, scope.Name, false, 0, null, null);

            var candidates = new List<(string? Name, double Cost, string? Currency)>();
            foreach (var budget in values.EnumerateArray())
            {
                if (!budget.TryGetProperty("properties", out var props)) continue;
                if (!props.TryGetProperty("category", out var category)
                    || !string.Equals(category.GetString(), "Cost", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!props.TryGetProperty("timeGrain", out var grain)
                    || !string.Equals(grain.GetString(), "Monthly", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (props.TryGetProperty("filter", out var filter) && HasEffectiveBudgetFilter(filter))
                    continue;
                if (!props.TryGetProperty("timePeriod", out var timePeriod)
                    || !timePeriod.TryGetProperty("startDate", out var startDateElement)
                    || !timePeriod.TryGetProperty("endDate", out var endDateElement)
                    || !TryReadDate(startDateElement, out var budgetStart)
                    || !TryReadDate(endDateElement, out var budgetEnd)
                    || budgetStart > periodStart
                    || budgetEnd < periodEndInclusive)
                    continue;
                if (!props.TryGetProperty("currentSpend", out var current)
                    || !current.TryGetProperty("amount", out var amount)
                    || !amount.TryGetDouble(out var cost)
                    || !double.IsFinite(cost)
                    || cost < 0)
                    continue;
                var currency = current.TryGetProperty("unit", out var unit) ? unit.GetString() : null;
                if (string.IsNullOrWhiteSpace(currency)) continue;
                var name = budget.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                candidates.Add((name, cost, currency));
            }

            if (candidates.Count == 0)
                return new(scope.Id, scope.Name, false, 0, null, null);

            // Multiple unfiltered subscription budgets should expose the same
            // subscription currentSpend. If they disagree, do not guess—fall
            // through to the authoritative Cost Management query path.
            var distinctCosts = candidates.Select(c => Math.Round(c.Cost, 6)).Distinct().ToArray();
            var distinctCurrencies = candidates
                .Select(c => c.Currency)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (distinctCosts.Length != 1 || distinctCurrencies.Length != 1)
                return new(scope.Id, scope.Name, false, 0, null, null);

            return new(scope.Id, scope.Name, true, candidates[0].Cost, distinctCurrencies[0], candidates[0].Name);
        }
        catch (JsonException)
        {
            return new(scope.Id, scope.Name, false, 0, null, null);
        }
    }

    private static bool HasEffectiveBudgetFilter(JsonElement filter)
    {
        if (filter.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return false;
        if (filter.ValueKind != JsonValueKind.Object) return true;

        foreach (var property in filter.EnumerateObject())
        {
            var value = property.Value;
            if (value.ValueKind == JsonValueKind.Null) continue;
            if (value.ValueKind == JsonValueKind.Object && !value.EnumerateObject().Any()) continue;
            if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0) continue;
            return true;
        }
        return false;
    }

    private static bool TryReadDate(JsonElement element, out DateOnly date)
    {
        date = default;
        var raw = element.GetString();
        return DateTimeOffset.TryParse(
                raw,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var timestamp)
            && (date = DateOnly.FromDateTime(timestamp.UtcDateTime)) != default;
    }

    private static AggregateCostResult ParseAggregateCostResponse(
        string response,
        IReadOnlyList<(string Id, string Name)> expectedScopes)
    {
        try
        {
            using var doc = JsonDocument.Parse(ResponseBody(response));
            var props = doc.RootElement.GetProperty("properties");
            var columns = props.GetProperty("columns").EnumerateArray()
                .Select((c, i) => (Name: c.GetProperty("name").GetString() ?? "", Index: i))
                .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);
            var costIndex = columns.TryGetValue("Cost", out var ci) ? ci : columns["PreTaxCost"];
            var idIndex = columns["SubscriptionId"];
            var currencyIndex = columns.TryGetValue("Currency", out var cui) ? cui : -1;
            var expectedById = expectedScopes.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
            var accumulators = new Dictionary<string, (double Cost, HashSet<string> Currencies)>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in props.GetProperty("rows").EnumerateArray())
            {
                var rawId = row[idIndex].GetString()?.Trim();
                if (rawId?.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase) == true)
                    rawId = rawId["/subscriptions/".Length..].Trim('/');
                if (!Guid.TryParse(rawId, out var parsedId)) continue;
                var id = parsedId.ToString();
                if (!expectedById.ContainsKey(id)) continue;

                var cost = row[costIndex].GetDouble();
                var currency = currencyIndex >= 0 ? row[currencyIndex].GetString() : null;
                if (!double.IsFinite(cost)) continue;

                if (!accumulators.TryGetValue(id, out var current))
                    current = (0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                current.Cost += cost;
                if (!string.IsNullOrWhiteSpace(currency)) current.Currencies.Add(currency);
                accumulators[id] = current;
            }

            var results = new Dictionary<string, CostScopeResult>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, value) in accumulators)
            {
                // A subscription should have one billing currency. If the
                // aggregate says otherwise (or omits currency for non-zero
                // cost), leave it missing so the per-subscription query path
                // can validate it independently.
                if (value.Currencies.Count > 1 || (value.Cost != 0 && value.Currencies.Count != 1))
                    continue;
                var scope = expectedById[id];
                results[id] = new(
                    id,
                    scope.Name,
                    200,
                    value.Cost,
                    value.Currencies.Count == 1 ? value.Currencies.Single() : null,
                    null);
            }

            return new(results, null);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new(
                new Dictionary<string, CostScopeResult>(StringComparer.OrdinalIgnoreCase),
                ex.Message);
        }
    }

    internal static JsonElement ReadCostSourceEvidence(string response)
    {
        try
        {
            using var document = JsonDocument.Parse(ResponseBody(response));
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("_finops", out var evidence)) return evidence.Clone();
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("retryAtUtc", out var retryAt))
                return JsonSerializer.SerializeToElement(new
                {
                    cacheStatus = "not_available",
                    freshness = "unknown",
                    retryAtUtc = retryAt.Clone(),
                    dataAsOfUtc = (DateTimeOffset?)null
                });
        }
        catch (JsonException) { }
        return JsonSerializer.SerializeToElement(new { cacheStatus = "unknown", freshness = "unknown", dataAsOfUtc = (DateTimeOffset?)null });
    }

    internal static string BuildCostResponse(
        string source,
        IReadOnlyList<(string Id, string Name)> scopes,
        IReadOnlyDictionary<string, CostScopeResult> resultsById,
        bool throttled,
        IReadOnlyList<JsonElement> sourceEvidence,
        DateOnly fromDate,
        DateOnly toDate)
    {
        var orderedResults = scopes.Select(scope =>
            resultsById.TryGetValue(scope.Id, out var result)
                ? result
                : new CostScopeResult(scope.Id, scope.Name, 0, null, null, "not returned")).ToList();
        var succeeded = orderedResults.Count(r => r.Status == 200 && r.Cost is not null);
        var unknownCurrencyCost = orderedResults.Any(r =>
            r.Status == 200 && r.Cost is not null && r.Cost != 0 && string.IsNullOrWhiteSpace(r.Currency));
        var totalsByCurrency = orderedResults
            .Where(r => r.Status == 200 && r.Cost is not null && !string.IsNullOrWhiteSpace(r.Currency))
            .GroupBy(r => r.Currency!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => Math.Round(g.Sum(r => r.Cost!.Value), 6), StringComparer.OrdinalIgnoreCase);
        var complete = succeeded == scopes.Count && !unknownCurrencyCost;
        var singleCurrency = totalsByCurrency.Count == 1 ? totalsByCurrency.Keys.Single() : null;
        var safeAggregate = totalsByCurrency.Count <= 1 && !unknownCurrencyCost;
        var summedCost = safeAggregate
            ? orderedResults.Where(r => r.Status == 200 && r.Cost is not null).Sum(r => r.Cost!.Value)
            : (double?)null;

        return JsonSerializer.Serialize(new
        {
            complete,
            source,
            costType = "ActualCost",
            timePeriod = new { from = fromDate, to = toDate, endExclusive = true },
            aggregation = "Cost",
            sourceEvidence,
            throttled,
            subscriptionCount = scopes.Count,
            succeeded,
            mixedCurrencies = totalsByCurrency.Count > 1,
            totalCost = complete && safeAggregate ? Math.Round(summedCost!.Value, 6) : (double?)null,
            partialCost = !complete && safeAggregate && succeeded > 0 ? Math.Round(summedCost!.Value, 6) : (double?)null,
            currency = singleCurrency,
            totalsByCurrency,
            results = orderedResults.Select(r => new
            {
                subscriptionId = r.SubscriptionId,
                subscriptionName = r.SubscriptionName,
                status = r.Status,
                cost = r.Cost,
                currency = r.Currency,
                error = r.Error
            })
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static bool TryReadCost(string response, out double cost, out string? currency, out string? error)
    {
        cost = 0;
        currency = null;
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(ResponseBody(response));
            var props = doc.RootElement.GetProperty("properties");
            var columns = props.GetProperty("columns").EnumerateArray()
                .Select((c, i) => (Name: c.GetProperty("name").GetString() ?? "", Index: i))
                .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);
            var costIndex = columns.TryGetValue("Cost", out var ci) ? ci : columns["PreTaxCost"];
            var currencyIndex = columns.TryGetValue("Currency", out var cui) ? cui : -1;
            var currencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in props.GetProperty("rows").EnumerateArray())
            {
                var rowCost = row[costIndex].GetDouble();
                if (!double.IsFinite(rowCost))
                {
                    error = "Cost response contained a non-finite value.";
                    return false;
                }
                cost += rowCost;
                if (currencyIndex >= 0)
                {
                    var rowCurrency = row[currencyIndex].GetString();
                    if (!string.IsNullOrWhiteSpace(rowCurrency)) currencies.Add(rowCurrency);
                }
            }
            if (currencies.Count > 1)
            {
                error = "Cost response contained more than one currency.";
                return false;
            }
            if (cost != 0 && currencies.Count != 1)
            {
                error = "Cost response omitted currency for non-zero cost.";
                return false;
            }
            currency = currencies.Count == 1 ? currencies.Single() : null;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string ResponseBody(string response)
    {
        var firstNewline = response.IndexOf('\n');
        var body = firstNewline >= 0 ? response[(firstNewline + 1)..] : "";
        if (body.StartsWith("Current UTC time: ", StringComparison.Ordinal))
        {
            var timestampEnd = body.IndexOf('\n');
            body = timestampEnd >= 0 ? body[(timestampEnd + 1)..] : "";
        }
        return body;
    }

    private static int ParseStatusCode(string response)
    {
        var firstLine = response.Split('\n', 2)[0];
        var parts = firstLine.Split(' ', 3);
        return parts.Length > 1 && int.TryParse(parts[1], out var status) ? status : 0;
    }

    private static string FirstLineAndBody(string response, int maxChars) =>
        response.Length <= maxChars ? response : response[..maxChars];

    internal sealed record BudgetSpend(
        string SubscriptionId,
        string SubscriptionName,
        bool Success,
        double Cost,
        string? Currency,
        string? BudgetName);

    internal sealed record CostScopeResult(
        string SubscriptionId,
        string SubscriptionName,
        int Status,
        double? Cost,
        string? Currency,
        string? Error);

    private sealed record AggregateCostResult(
        Dictionary<string, CostScopeResult> Results,
        string? Error);

    /// <summary>
    /// Returns null if the path is acceptable, otherwise a ready-to-return HTTP 400 message explaining
    /// the missing {scope} prefix. Scope-required providers (Cost Management, Consumption budgets,
    /// PolicyInsights states, etc.) MUST be prefixed with one of the five canonical scope shapes.
    /// Bare /providers/Microsoft.CostManagement/query was 5/27 of all 4xx failures in the last 5 days.
    /// </summary>
    internal static string? ValidateScopePrefix(string path)
    {
        // Strip query string for the check
        var qIdx = path.IndexOf('?');
        var clean = qIdx >= 0 ? path[..qIdx] : path;

        if (clean.Contains("/providers/Microsoft.Consumption/reservationSummaries", StringComparison.OrdinalIgnoreCase)
            && !Regex.IsMatch(clean,
                @"^/providers/(?:Microsoft\.Billing/billingAccounts/[^/]+(?:/billingProfiles/[^/]+)?|Microsoft\.Capacity/reservationOrders/[^/]+(?:/reservations/[^/]+)?)/providers/Microsoft\.Consumption/reservationSummaries/?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return "HTTP 400 BadRequest\nReservation summaries require a discovered billing account/profile or reservation-order/reservation scope. " +
                "Use /providers/Microsoft.Capacity/reservationOrders/{orderId}/providers/Microsoft.Consumption/reservationSummaries?api-version=2024-08-01&grain=monthly after listing accessible reservation orders. " +
                "A bare tenant-root or subscription path is not supported. No request was sent.";

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
                   "Example: POST /subscriptions/abc-123/providers/Microsoft.CostManagement/query?api-version=2026-08-01";
        }
        return null;
    }

    private static string? ValidateReadOnlyPostPath(string path, Activity? activity)
    {
        if (path.Contains('\\')
            || path.Contains('#')
            || Regex.IsMatch(path, "%2f|%5c|%23", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return BlockMutatingPost(activity);

        var qIdx = path.IndexOf('?');
        var clean = (qIdx >= 0 ? path[..qIdx] : path).TrimEnd('/');
        const string subscriptionScope = @"/subscriptions/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}(?:/resourceGroups/[^/]+)?";
        const string managementGroupScope = @"/providers/Microsoft\.Management/managementGroups/[^/]+";
        const string billingScope = @"/providers/Microsoft\.Billing/billingAccounts/[^/]+(?:/billingProfiles/[^/]+)?(?:/invoiceSections/[^/]+)?";
        var scopedCostManagement = $@"^(?:{subscriptionScope}|{managementGroupScope}|{billingScope})/providers/Microsoft\.CostManagement/(?:query|forecast|generateCostDetailsReport|generateReservationDetailsReport|pricesheets/default/download)$";
        string[] allowedPatterns =
        [
            scopedCostManagement,
            @"^/providers/Microsoft\.ResourceGraph/resources$",
            @"^/providers/Microsoft\.Capacity/(?:calculatePrice|calculateExchange)$",
            @"^/providers/Microsoft\.BillingBenefits/(?:calculatePrice|validatePurchase)$",
            @"^/providers/Microsoft\.Carbon/carbonEmissionReports$",
            @"^/providers/Microsoft\.Management/getEntities$",
            $@"^{subscriptionScope}/providers/Microsoft\.Advisor/recommendations/summarize$",
        ];
        if (allowedPatterns.Any(pattern => Regex.IsMatch(
                clean,
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            return null;

        return BlockMutatingPost(activity);
    }

    private static readonly JsonDocumentOptions LenientJson = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    // Model-authored bodies sometimes carry trailing commas or comments; ARM needs strict JSON.
    internal static string? CanonicalJsonBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return body;
        try
        {
            using var strict = JsonDocument.Parse(body);
            return body;
        }
        catch (JsonException) { }
        try
        {
            using var lenient = JsonDocument.Parse(body, LenientJson);
            return JsonSerializer.Serialize(lenient.RootElement);
        }
        catch (JsonException) { return body; }
    }

    internal static string? ValidateCostQueryBody(string path, string? body)
    {
        var queryIndex = path.IndexOf('?');
        var requestPath = (queryIndex < 0 ? path : path[..queryIndex]).TrimEnd('/');
        if (!requestPath.EndsWith("/Microsoft.CostManagement/query", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var document = JsonDocument.Parse(body ?? "");
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return "HTTP 400 BadRequest\nCost Management query body must be a JSON object. No request was sent.";
            if (document.RootElement.TryGetProperty("dataset", out var dataset)
                && dataset.ValueKind == JsonValueKind.Object && dataset.TryGetProperty("grouping", out var grouping))
            {
                if (grouping.ValueKind != JsonValueKind.Array)
                    return "HTTP 400 BadRequest\nCost Management dataset.grouping must be an array. No request was sent.";
                if (grouping.GetArrayLength() > 2)
                    return "HTTP 400 BadRequest\nCost Management supports at most two grouping dimensions. Use ResourceId plus Meter at subscription scope, or SubscriptionId plus ResourceId at management-group scope. Derive resource-group from ResourceId; do not add a third grouping. No request was sent.";
            }
            return null;
        }
        catch (JsonException)
        {
            return "HTTP 400 BadRequest\nCost Management query body must be valid JSON. No request was sent.";
        }
    }

    internal static string? ValidateQueryBody(string path, string? body)
    {
        var queryIndex = path.IndexOf('?');
        var requestPath = (queryIndex < 0 ? path : path[..queryIndex]).TrimEnd('/');
        if (!requestPath.Equals("/providers/Microsoft.ResourceGraph/resources", StringComparison.OrdinalIgnoreCase))
            return ValidateCostQueryBody(path, body);

        const string error = "HTTP 400 BadRequest\nResource Graph queries must declare exactly one non-empty subscriptions array of GUID strings or managementGroups array of ID strings from the requested connection-context scope. Implicit tenant-wide scope is not supported by this tool. No request was sent.";
        if (string.IsNullOrWhiteSpace(body)) return error;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Count(property => property.Name is "subscriptions" or "managementGroups") != 1)
                return error;
            var subscriptionScope = root.TryGetProperty("subscriptions", out var scopes);
            if (!subscriptionScope) scopes = root.GetProperty("managementGroups");
            if (scopes.ValueKind != JsonValueKind.Array || scopes.GetArrayLength() == 0)
                return error;
            foreach (var scope in scopes.EnumerateArray())
                if (scope.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(scope.GetString())
                    || subscriptionScope && !Guid.TryParse(scope.GetString(), out _))
                    return error;
            return null;
        }
        catch (JsonException)
        {
            return "HTTP 400 BadRequest\nResource Graph query body must be one complete valid JSON object (check closing quotes and braces), with an explicit subscriptions or managementGroups array and a query string. No request was sent.";
        }
    }

    private static string BlockMutatingPost(Activity? activity)
    {
        activity?.SetTag("azure.result", "blocked_mutating_post");
        activity?.SetStatus(ActivityStatusCode.Error, "Mutating POST blocked");
        return "HTTP 403 Forbidden\nThis agent only performs allowlisted read-only Azure POST operations. Mutating actions such as start, restart, deallocate, power off, or return are blocked.";
    }

    private async Task<string> BulkAzureRequest(
        [Description("JSON array of 1-200 {method,path,body?} objects. Each read must filter, aggregate, project, and limit at the source with supported API options. Resource Graph POST bodies require an explicit subscriptions or managementGroups array covering the requested scope; use top N by field or order by field | take N, never bare top N. Use exact discovered targets for writes; body is a JSON string. Grouped cost reads across two or more scopes belong in one batch with exact requested scopes, dates, cost type and filters; they execute sequentially and stop after a final cost 429.")] string requestsJson,
        [Description("Max parallel requests in flight. Default 20, max 50. Request 1 for Cost Management; any batch containing /query or /forecast is forced to 1 by the host.")] int parallelism = 20,
        [Description("Stop the whole bulk run on the first failure. Default false (continue and report all failures). A final Cost Management 429 always stops the batch regardless of this setting.")] bool stopOnFirstError = false,
        CancellationToken cancellationToken = default)
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
        if (items.Any(item => item is null))
            return "HTTP 400 BadRequest\nEvery batch item must be a request object. No request was sent.";

        if (items.Count > 200) return "HTTP 400 BadRequest\nA batch supports at most 200 requests; split larger work explicitly.";
        return await ExecuteBulkAsync(items, parallelism, stopOnFirstError, async (item, requestToken) =>
        {
            var (method, methodError) = HttpHelper.ResolveMethod(item.Method, activity, "bulk");
            if (methodError is not null) return methodError;
            if (string.IsNullOrWhiteSpace(item.Path) || !item.Path.StartsWith('/') || item.Path.StartsWith("//")
                || item.Path.Contains('#') || item.Path.Contains('\\')) return "HTTP 400 BadRequest\nInvalid ARM path.";
            if (ValidateScopePrefix(item.Path) is { } scopeError) return scopeError;
            if (method == HttpMethod.Post && ValidateReadOnlyPostPath(item.Path, activity) is { } postError) return postError;
            var body = method == HttpMethod.Post ? CanonicalJsonBody(item.Body) : item.Body;
            if (method == HttpMethod.Post && ValidateQueryBody(item.Path, body) is { } queryError) return queryError;
            return await HttpHelper.SendWithRetryAsync($"https://management.azure.com{item.Path}", token, activity, "bulk",
                method: method, jsonBody: method == HttpMethod.Get ? null : body,
                cancellationToken: requestToken);
        }, cancellationToken);
    }

    internal static async Task<string> ExecuteBulkAsync(IReadOnlyList<BulkRequestItem> items, int parallelism, bool stopOnFirstError,
        Func<BulkRequestItem, CancellationToken, Task<string>> send, CancellationToken cancellationToken)
    {
        if (items.Count is < 1 or > 200) throw new ArgumentException("Batches support 1 to 200 items.");
        var results = new BulkResult?[items.Count];
        var stop = 0;
        var concurrency = items.Any(IsCostRead) ? 1 : Math.Clamp(parallelism, 1, 50);
        await Parallel.ForEachAsync(Enumerable.Range(0, items.Count),
            new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = CancellationToken.None },
            async (index, parallelToken) =>
            {
                if (cancellationToken.IsCancellationRequested || Volatile.Read(ref stop) != 0)
                {
                    results[index] = new(index, 0, "unattempted", false, null, "Not sent.");
                    return;
                }
                try
                {
                    var response = await send(items[index], cancellationToken);
                    var newline = response.IndexOf('\n');
                    int.TryParse((newline < 0 ? response : response[..newline]).Split(' ').ElementAtOrDefault(1), out var status);
                    var content = newline < 0 ? "" : response[(newline + 1)..];
                    object? body = null;
                    var partial = content.Length > MaxBulkItemCharacters;
                    if (!partial && content.Length > 0)
                    {
                        try { body = JsonSerializer.Deserialize<JsonElement>(content); }
                        catch (JsonException) { body = SensitiveContent.Redact(content); }
                    }
                    var succeeded = status is >= 200 and < 300;
                    var outcome = succeeded ? "succeeded" : "failed";
                    if (body is JsonElement { ValueKind: JsonValueKind.Object } structured)
                    {
                        if (structured.TryGetProperty("operationId", out _) && structured.TryGetProperty("status", out var operationStatus))
                            outcome = operationStatus.GetString() ?? "unknown";
                        else if (structured.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
                            && properties.TryGetProperty("provisioningState", out _)) outcome = OperationStore.Classify(status, structured);
                    }
                    if (status == 202 && outcome == "succeeded") outcome = "accepted";
                    results[index] = new(index, status, outcome, partial, body,
                        partial ? "Response exceeds the per-item limit. Narrow this indexed request; no complete-coverage claim is permitted." : null,
                        IsCostRead(items[index]) ? ReadCostSourceEvidence(response) : null);
                    if ((outcome is "failed" or "unknown" && stopOnFirstError)
                        || status == 429 && IsCostRead(items[index])) Interlocked.Exchange(ref stop, 1);
                }
                catch (OperationCanceledException)
                {
                    results[index] = new(index, 0, "cancelled", true, null, "Interrupted after dispatch; verify mutation state before retrying.");
                    if (stopOnFirstError) Interlocked.Exchange(ref stop, 1);
                }
                catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
                {
                    results[index] = new(index, 0, "failed", false, null, exception.GetType().Name);
                    if (stopOnFirstError) Interlocked.Exchange(ref stop, 1);
                }
            });
        var budget = MaxBulkBatchCharacters;
        for (var index = 0; index < results.Length; index++)
        {
            var item = results[index]!;
            var size = JsonSerializer.Serialize(item.Body).Length;
            if (size > budget) results[index] = item with { Body = null, Partial = true, Error = "Batch data budget reached. Retrieve this indexed item separately." };
            else budget -= size;
        }
        return JsonSerializer.Serialize(new
        {
            total = items.Count,
            succeeded = results.Count(item => item!.Outcome == "succeeded"),
            failed = results.Count(item => item!.Outcome == "failed"),
            pending = results.Count(item => item!.Outcome is "awaitingApproval" or "accepted" or "inProgress" or "dispatching" or "unknown"),
            unattempted = results.Count(item => item!.Outcome == "unattempted"),
            cancelled = results.Count(item => item!.Outcome == "cancelled"),
            stopped = Volatile.Read(ref stop) != 0,
            complete = results.All(item => item is { Outcome: "succeeded", Partial: false }),
            results
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    // Sized below ToolResultStore.MaxResultBytes so a complete batch can be retained and queried.
    internal const int MaxBulkItemCharacters = 4_000_000;
    internal const int MaxBulkBatchCharacters = 12_000_000;

    internal sealed class BulkRequestItem
    {
        public string Method { get; set; } = "GET";
        public string Path { get; set; } = "";
        public string? Body { get; set; }
    }

    private static bool IsCostRead(BulkRequestItem item) =>
        string.Equals(item.Method?.Trim(), "POST", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrEmpty(item.Path) && HttpHelper.IsInteractiveCostQueryUrl(item.Path);

    private sealed record BulkResult(int Index, int Status, string Outcome, bool Partial, object? Body, string? Error,
        JsonElement? SourceEvidence = null);
}
