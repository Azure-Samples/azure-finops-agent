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

    public IEnumerable<AIFunction> Create() =>
    [
        AIFunctionFactory.Create(QueryAzure, nameof(QueryAzure), """
            Calls Azure Resource Manager (https://management.azure.com) with the signed-in user's delegated token and returns the raw JSON. The Current UTC time line is the retrieval time, not the source data-as-of time. The user's RBAC is the access boundary.
            You choose the resource provider, path, api-version and body. When unsure, look it up instead of guessing (a failed call does not answer the question):
            - GET /subscriptions/{id}/providers/{namespace}?api-version=2021-04-01 lists each resource type's live apiVersions.
            - Request and response schemas are in the official OpenAPI specs at https://github.com/Azure/azure-rest-api-specs: list folders and versions with FetchPublicWebPage on https://api.github.com/repos/Azure/azure-rest-api-specs/contents/specification/{service}/resource-manager, then read the JSON from raw.githubusercontent.com using grepFor. The REST reference is at https://learn.microsoft.com/rest/api/.
            Host rules:
            - GET is allowed. POST is limited to read-only endpoints: Cost Management query/forecast/report generation, Resource Graph, reservation and savings-plan price calculation, Advisor summarize, management-group entities, carbon reports and Spot placement scores. Action POSTs and DELETE are blocked.
            - PUT/PATCH never execute directly; they create a proposal that the user must approve in the UI. Never claim a change was applied.
            - Cost Management, Consumption and PolicyInsights paths need a scope prefix (/subscriptions/{id}, a resource group, a management group or a billing account). Resource Graph bodies must list the requested scope in an explicit subscriptions or managementGroups array.
            - Cost Management /query and /forecast are tenant-throttled: the host runs them one at a time and retries once after a short cooldown. After a returned 429, make no further Cost Management calls this turn and report the retry deadline. For several cost scopes use one BulkAzureRequest with parallelism=1.
            - Large results are retained: use QueryToolResult with the returned resultId for exact totals, grouping and sorting.
            Filter, aggregate and limit at the source ($filter/$top where supported, KQL summarize/project, Cost Management grouping), aggregating before limiting. Preserve scope, dates, currency, cost type, pagination and partial coverage in the answer. For public list prices use GetAzureRetailPricing.
            """),
        AIFunctionFactory.Create(BulkAzureRequest, nameof(BulkAzureRequest), """
            Runs up to 200 QueryAzure-style requests in one call with the same host rules; use it instead of looping QueryAzure over similar reads or writes (for example one cost query per subscription).
            Batches containing Cost Management /query or /forecast run sequentially and stop after a final 429. Returns indexed results with status, body and error plus total/succeeded/failed counts; large batches return a resultId for QueryToolResult. PUT/PATCH items become approval proposals, never applied writes.
            """),
    ];
    private async Task<string> QueryAzure(
        [Description("HTTP method: GET, POST, PUT, or PATCH (DELETE is blocked)")] string method,
        [Description("ARM path starting with / and including api-version.")] string path,
        [Description("JSON request body for POST/PUT/PATCH; omit for GET.")] string? body = null)
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
            // Read-only placement-likelihood diagnostic; creates no resources.
            @"^/subscriptions/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/providers/Microsoft\.Compute/locations/[a-z0-9]+/placementScores/spot/generate$",
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
        [Description("JSON array of 1-200 {\"method\",\"path\",\"body\"} objects; body is a JSON string.")] string requestsJson,
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
