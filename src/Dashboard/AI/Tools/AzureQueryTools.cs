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

Use standard ARM URL conventions; you know the resource providers and current api-versions. Common surfaces: Microsoft.CostManagement (query/forecast/exports), Microsoft.Consumption (budgets/pricesheets/reservation*), Microsoft.Capacity (reservations), Microsoft.BillingBenefits (savingsPlans), Microsoft.Advisor (recommendations), Microsoft.ResourceGraph (KQL across subs), Microsoft.Insights (metrics/diagnostics/autoscale), Microsoft.Compute, Microsoft.ContainerService, Microsoft.Network, Microsoft.Storage, Microsoft.Sql, Microsoft.Web, Microsoft.OperationalInsights, Microsoft.MachineLearningServices, Microsoft.CognitiveServices, Microsoft.App, Microsoft.Authorization (RBAC/Policy/Locks), Microsoft.Management, Microsoft.Quota, Microsoft.Carbon, Microsoft.Migrate, Microsoft.Support, Microsoft.ResourceHealth, Microsoft.Security.

=== NON-OBVIOUS RULES (read carefully) ===
{scope} GRAMMAR (REQUIRED for ALL Microsoft.CostManagement, Microsoft.Consumption, and Microsoft.CostManagement/budgets paths) — must be ONE of:
  /subscriptions/{subId}
  /subscriptions/{subId}/resourceGroups/{rgName}
  /providers/Microsoft.Management/managementGroups/{mgId}
  /providers/Microsoft.Billing/billingAccounts/{billingAccountId}[/billingProfiles/{id}|/invoiceSections/{id}]
Never bare /providers/Microsoft.CostManagement/... — that returns 400.

COST MANAGEMENT QUERY: use api-version=2026-08-01. ALWAYS group by a real dimension (ServiceName, ResourceGroupName, MeterCategory). Do NOT add 'UsageDate' to the grouping array — it's a response column, not a dimension; use granularity=""Daily"" for per-day. Never request raw ungrouped cost data. For totals across all subscriptions, query the tenant/root management-group scope ONCE and group by SubscriptionName; never fan out one query per subscription.

THROTTLING: Cost Management /query and /forecast are aggressively throttled per-tenant. Interactive queries make at most one short retry; other transient calls retain the standard retry policy. Do NOT call multiple CostManagement endpoints in parallel from the same turn — Resource Graph and Advisor parallelize fine. If a call still returns HTTP 429, do not make another Cost Management call in the same turn; report the throttle and offer to retry later.

RESOURCE GRAPH (POST /providers/Microsoft.ResourceGraph/resources): always use 'project' to limit columns and 'top N' to limit rows. Use one pipeline; Azure Resource Graph does not accept multi-statement `let ...; let ...;` queries.

SPOT QUOTA: Spot/low-priority VM quota is a SINGLE regional bucket called 'lowPriorityCores' (NOT per VM family) — covers ALL spot VMs including H100/A100. Standard quotas are per-family ('standardNDSH100v5Family', 'StandardNCadsH100v5Family'). Microsoft.Quota requires RP registration (PUT /subscriptions/{subId}/providers/Microsoft.Quota/register) — fall back to GET .../Microsoft.Compute/locations/{region}/usages if not registered.

FOUNDRY / AZURE OPENAI QUOTA: Lives under Microsoft.CognitiveServices, NOT prices.azure.com. Per-region quota: GET /subscriptions/{id}/providers/Microsoft.CognitiveServices/locations/{region}/usages — returns name.value entries like 'OpenAI.GlobalStandard.gpt-5.6-sol'. For deployments: GET .../accounts/{name}/deployments returns properties.model.name + sku.capacity (TPM in thousands).

MIGRATE: Use resource type 'assessmentProjects' (NOT 'migrateProjects' — returns 404).

CONSUMPTION DEPRECATIONS: usageDetails → use Microsoft.CostManagement/generateCostDetailsReport. reservationDetails → use Microsoft.CostManagement/generateReservationDetailsReport.

For public retail pricing use https://prices.azure.com (no auth) with ?$filter=armRegionName eq '...' and serviceName eq '...' and armSkuName eq '...'&$top=20.");

        yield return AIFunctionFactory.Create(QueryCostsAcrossSubscriptions, "QueryCostsAcrossSubscriptions", @"Gets an exact Cost Management total and per-subscription breakdown in ONE agent tool call. Use this for any cost request spanning all connected subscriptions; never loop QueryAzure yourself.
Input subscriptionsJson: the exact `subscriptions` JSON array supplied in the connection context ({id,name,...}). Input managementGroupId: the optional id/name from the context's managementGroups array. Dates are yyyy-MM-dd; `to` is the exclusive end date.
    For the current calendar month, the tool first reads each subscription's unfiltered monthly budget `currentSpend` in parallel; this is exact live MTD cost and avoids the heavily throttled query API. For other periods it tries one management-group aggregate query, then the minimum sequential per-subscription fallback. It stops immediately when Cost Management remains throttled and reports completed, failed, and unattempted scopes. Never call this tool twice in one turn after a 429.");


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
        if (httpMethod == HttpMethod.Post)
        {
            var postError = ValidateReadOnlyPostPath(path, activity);
            if (postError is not null) return postError;
        }

        var hasBody = !string.IsNullOrWhiteSpace(body);
        return await HttpHelper.SendWithRetryAsync(
            $"https://management.azure.com{path}",
            token, activity, "azure",
            method: httpMethod,
            jsonBody: hasBody && httpMethod != HttpMethod.Get ? body : null,
            includeTimestamp: true);
    }

    private async Task<string> QueryCostsAcrossSubscriptions(
        [Description("JSON array of subscription objects from the connection context, each with id and name fields")] string subscriptionsJson,
        [Description("Inclusive start date in yyyy-MM-dd format")] string from,
        [Description("Exclusive end date in yyyy-MM-dd format")] string to,
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

        // The Consumption budgets endpoint returns subscription-level live
        // `currentSpend` without consuming the Cost Management query QPU pool.
        // It is valid only for the current calendar month and only when an
        // unfiltered monthly budget covers the whole subscription. Use this
        // before /query so an unrelated tenant throttle cannot hide exact MTD.
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
            timePeriod = new
            {
                from = fromDate.ToString("yyyy-MM-dd"),
                to = toDate.ToString("yyyy-MM-dd")
            },
            dataset = new
            {
                granularity = "None",
                aggregation = new { totalCost = new { name = "Cost", function = "Sum" } }
            }
        });

        Dictionary<string, CostScopeResult>? aggregateResults = null;

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
                    timePeriod = new
                    {
                        from = fromDate.ToString("yyyy-MM-dd"),
                        to = toDate.ToString("yyyy-MM-dd")
                    },
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
                if (mgResponse.StartsWith("HTTP 200", StringComparison.Ordinal))
                {
                    var aggregate = ParseAggregateCostResponse(mgResponse, scopes);
                    if (aggregate.Error is null && aggregate.Results.Count == scopes.Count)
                        return BuildCostResponse("managementGroup", scopes, aggregate.Results, false);

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
                        throttled = true,
                        attempted = 1,
                        subscriptionCount = scopes.Count,
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
        return BuildCostResponse(source, scopes, resultsById, throttled);
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

    private static string BuildCostResponse(
        string source,
        IReadOnlyList<(string Id, string Name)> scopes,
        IReadOnlyDictionary<string, CostScopeResult> resultsById,
        bool throttled)
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
        return firstNewline >= 0 ? response[(firstNewline + 1)..] : "";
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

    private sealed record CostScopeResult(
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

    private static string BlockMutatingPost(Activity? activity)
    {
        activity?.SetTag("azure.result", "blocked_mutating_post");
        activity?.SetStatus(ActivityStatusCode.Error, "Mutating POST blocked");
        return "HTTP 403 Forbidden\nThis agent only performs allowlisted read-only Azure POST operations. Mutating actions such as start, restart, deallocate, power off, or return are blocked.";
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
                if (httpMethod == HttpMethod.Post)
                {
                    var postError = ValidateReadOnlyPostPath(item.Path, activity);
                    if (postError is not null)
                    {
                        results[i] = new BulkResult(i, 403, item.Path, postError, false);
                        if (stopOnFirstError) cts.Cancel();
                        return;
                    }
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
