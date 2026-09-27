using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.AI;

using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Infrastructure;

namespace AzureFinOps.Dashboard.AI.Tools;

/// <summary>
/// The single HTTP evidence tool. The model authors the URL, method and body for every supported API;
/// the host resolves the service from the exact URL host, attaches only that service's delegated token
/// and enforces its method, scope, throttling, pagination, redaction and approval rules. Unknown hosts
/// are public, credential-free GETs. All calls are traced via OpenTelemetry → Application Insights.
///
/// Security model: DELETE is blocked everywhere. ARM POST is restricted to known read-only
/// query/report/calculation/diagnostic endpoints; ARM PUT/PATCH only create owner-bound proposals that
/// the user approves in the UI. Storage, Retail Prices and public pages are GET-only. Beyond that the
/// user's Entra RBAC and delegated consent are the security boundary.
/// </summary>
public sealed partial class AzureQueryTools(UserTokens tokens)
{
    internal enum Service { Arm, Graph, LogAnalytics, Storage, RetailPrices, PublicWeb }

    private const string ArmHost = "management.azure.com";
    private const string RetailBase = "https://prices.azure.com/api/retail/prices";
    private const string StorageVersion = "2026-02-06";
    internal const int MaxStorageBytes = 6 * 1024 * 1024;
    private const int RetailAttempts = 4;

    private static readonly HttpClient StorageHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10) })
    { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly HttpClient RetailHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };

    internal const string ToolDescription = """
        The one tool for every API and for reading what earlier calls returned. You author url, method and body; the host picks the credential from the exact host (the signed-in user's delegated token, so their RBAC and consent are the boundary). Every response body is stored whole in this conversation's SQLite table responses(id, url, method, status, retrieved_utc, body) and returned with its responses.id: a small response in full, a large one as its shape (path, type, items, example; the first element of each array stands for its siblings) so you can then read exactly what you need. CSV and XML become JSON (tabular sources are columns [{name,type}] plus rows of arrays, like Cost Management and Log Analytics tables), HTML becomes text. The Current UTC time line (or retrievedAtUtc) is the retrieval time, not the source's data-as-of time.
        sql is read-only SQLite with JSON1 and window functions over the stored bodies; several SELECT statements separated by ; each run independently and return their own table, so one sql carries every view you need; a failed statement is reported by number as a PARTIAL RESULT beside the others' tables, so rerun only that statement if you still need it. With url it runs on the new response ($id is its responses.id) and only its rows come back; with sql and no url it reads earlier responses by id without calling an API. Read arrays with json_each and fields with json_extract, for example SELECT json_extract(v.value,'$.name') AS name, json_extract(v.value,'$.properties.state') AS state FROM responses r, json_each(r.body,'$.value') v WHERE r.id = $id; read columnar rows by position (json_extract(v.value,'$[2]')) after reading the column order; search text with instr and substr. Name every alias and CTE column with a plain word that is not an SQL keyword (refs, not references; grp, not group; ord, not order) so the statement parses the first time. Compute every total, count, share, difference, ranking and date span in sql rather than by reading rows or doing arithmetic yourself; numbers are binary floats, so round money with round(x, 2). A sql error returns the shape and does not repeat the request.
        Endpoints and where to look up their contracts (look it up instead of guessing: a failed call does not answer the question):
        - Azure Resource Manager: url is an ARM path starting with / (https://management.azure.com is implied) including api-version. Covers Cost Management, Consumption, Billing, Advisor, Resource Graph, Compute, Monitor, Policy, Network, Resource Health and every provider. Live apiVersions: GET /subscriptions/{id}/providers/{namespace}?api-version=2021-04-01. Schemas: the official OpenAPI specs; list folders and versions with https://api.github.com/repos/Azure/azure-rest-api-specs/contents/specification/{service}/resource-manager, then read the spec JSON on raw.githubusercontent.com with sql over its paths and definitions. Reference: https://learn.microsoft.com/rest/api/{service}/.
        - Microsoft Graph: https://graph.microsoft.com/v1.0/... or /beta/... Reference: https://learn.microsoft.com/graph/api/{resource}-{verb}?view=graph-rest-1.0 and https://github.com/microsoftgraph/msgraph-metadata. Many endpoints, such as subscribedSkus and report functions, reject $filter/$top/$select; report functions return CSV, converted to a rows table. The host sends ConsistencyLevel: eventual, so directory advanced queries work when they also include $count=true (for example users?$filter=assignedLicenses/$count ne 0&$count=true).
        - Log Analytics: POST https://api.loganalytics.io/v1/workspaces/{customerId}/query; Application Insights: POST https://api.applicationinsights.io/v1/apps/{appId}/query; body {"query":"<KQL>","timespan":"P7D"}. KQL: https://learn.microsoft.com/kusto/query/. Discover populated tables with `Usage | summarize GB=sum(Quantity)/1024 by DataType` and columns with `<Table> | getschema`; filter by time first, summarize and project inside KQL, and take/top only after aggregation. FinOps signals: Usage and _BilledSize (ingestion cost), Perf/InsightsMetrics (utilization), Heartbeat and AzureActivity (who changed what).
        - Blob Storage (cost exports): GET https://{account}.blob.core.windows.net/{container}?restype=container&comp=list&prefix={export/period} lists blobs (use the narrowest prefix; a NextMarker means more blobs); GET https://{account}.blob.core.windows.net/{container}/{blob} reads the first 6 MiB of one blob (CSV becomes rows; complete=false when the blob is larger). Never use a SAS or other credential-bearing URL; for complete analysis of a large export ask for an upload and use QueryUploadedFile. Reference: https://learn.microsoft.com/rest/api/storageservices/list-blobs.
        - Azure Retail Prices (public list prices, no auth): GET https://prices.azure.com/api/retail/prices?currencyCode=USD&$filter=<OData>. Fields and operators (eq, and, or, contains(field,'x')): https://learn.microsoft.com/rest/api/cost-management/retail-prices/azure-retail-prices. Values are case-sensitive; armRegionName is lowercase (eastus); Azure OpenAI and other Foundry models use serviceName 'Foundry Models'. Meter, product and SKU names are not derivable from ARM SKU names: start with structural fields (serviceName, armRegionName, armSkuName, priceType) and read the returned values; armSkuName is the full ARM size name with its prefix (Standard_D4s_v5, never D4s_v5). Compare regions or SKUs with 'or' in one filter; for a multi-service estimate, fetch every component in the first round as parallel calls with one structural filter per service, then select rows with sql. Storage and SQL Database publish thousands of rows per region, so their filter also needs a product family term (contains(productName,'Premium SSD'), contains(productName,'General Purpose')) that still returns every billed component: SQL Database 'General Purpose' returns both the '- Compute Gen5' and the '- Storage' products, while a Compute-only term misses storage. A lookup that returns complete=false has missed rows. Some services publish one worldwide rate under armRegionName 'Global' and no per-region rows (Load Balancer has none in any region), so filter those with (armRegionName eq '<region>' or armRegionName eq 'Global') in the first request and label rows returned as Global as worldwide rates, not the region's. The host follows NextPageLink; never send $top. Zero Items means the filter matched nothing, not a zero price; tierMinimumUnits marks volume bands. Quote each rate with productName, skuName, meterName, unitOfMeasure, currency and retrievedAtUtc, and state the lookup's coverage (complete or partial, from its complete flag); a sized skuName (for example '4 vCore' with unitOfMeasure '1 Hour') prices that whole size per unit, not each vCore, so say so and never multiply it by the size.
        - Any other public https URL, GET only and without credentials: Microsoft Learn (search https://learn.microsoft.com/api/search?search=<terms>&locale=en-us, then fetch only returned URLs), GitHub, vendor pricing pages, and the public Azure status feed https://azure.status.microsoft/en-us/status/feed/ (not tenant-specific: an empty feed does not prove a resource is healthy; use ARM Microsoft.ResourceHealth for a named resource). Pages are stored whole as text; read the part you need with sql.
        - operation:<operationId> checks a host-registered operation (approval proposal, 202 accepted diagnostic or report download such as a pricesheet): it polls only the stored ARM URL, never repeats a mutation, and returns status and nextPollUtc; respect nextPollUtc and reuse terminal results. operation: alone lists up to 100 owner-bound operations in this conversation, including prerequisites left by a partial deployment, for a reviewed cleanup script. Accepted, unknown and in-progress operations are not completed.
        Host rules:
        - DELETE is blocked. ARM POST is limited to read-only endpoints: Cost Management query/forecast/report generation/pricesheet download, Resource Graph, reservation and savings-plan price calculation, PolicyInsights policy-state summarize/queryResults and policy-event queryResults, management-group entities, carbon reports, Spot placement scores and Network Watcher connectivityCheck from an existing VM (body {source:{resourceId:<VM id>},destination:{address,port}}). Action POSTs such as start, restart, deallocate, power off or return are blocked. PUT/PATCH never execute directly: they create a proposal that the user must approve in the UI; never claim a change was applied. Asynchronous (202) results return an operationId to check with url operation:<operationId>. Standard Graph consent is read-only, so writes return 403: report that instead of retrying.
        - Cost Management, Consumption and PolicyInsights paths need a scope prefix (/subscriptions/{id}, a resource group, a management group or a billing account). Resource Graph bodies list the requested scope in an explicit subscriptions or managementGroups array, and Resource Graph KQL rejects kind, type and count as assigned column names in project, extend or summarize (write resourceKind='x' or n=count()).
        - Send GETs of the same kind (one per region, subscription or scope) as several url lines in one call; the host runs at most 4 at a time and shows a shape shared by several large responses once. Make other independent calls in parallel in one response instead of one after another. Cost Management /query and /forecast are tenant-throttled: the host runs them one at a time and retries once after a short cooldown. After a returned 429, make no further Cost Management calls this turn and report the retry deadline.
        - Paginated GET results (nextLink, @odata.nextLink, NextPageLink) are followed on the same host for up to 10 pages; complete=false means more pages remained.
        Filter, aggregate and limit at the source ($filter/$top where supported, KQL summarize/project, Cost Management grouping), aggregating before limiting. Preserve scope, dates, currency, cost type, pagination and partial coverage in the answer.
        """;

    private const string OperationPrefix = "operation:";
    private const int Pages = 10;
    private static readonly HttpClient PollClient = new(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5) })
    { Timeout = TimeSpan.FromSeconds(15) };

    public IEnumerable<AIFunction> Create() =>
    [
        AIFunctionFactory.Create(QueryAzure, nameof(QueryAzure), ToolDescription),
    ];

    private async Task<string> QueryAzure(
        [Description("An ARM path starting with / (with api-version); a full https URL for Microsoft Graph, Log Analytics, Application Insights, Blob Storage, Retail Prices or a public page; or operation:<operationId> for a host-registered operation (operation: alone lists this conversation's operations). Several GET urls, one per line (at most 50), are one call: each is stored as its own response, sql runs on each ($id is that response) and results return in url order. Omit to run sql over stored responses only.")] string url = "",
        [Description("GET (default), POST, PUT or PATCH. DELETE is blocked.")] string method = "GET",
        [Description("Request body for POST/PUT/PATCH as a native JSON object, not an escaped string; omit for GET.")] JsonElement? body = null,
        [Description("Optional read-only SQLite over table responses(id, url, method, status, retrieved_utc, body); $id is this call's stored response. Only the query's rows are returned.")] string sql = "",
        CancellationToken cancellationToken = default)
    {
        // sql runs in ProtectedTool over the stored, redacted response.
        _ = sql;
        using var activity = HttpHelper.Telemetry.StartActivity("QueryAzure");
        var target = url.Trim();
        if (target.Length == 0) return "HTTP 400 BadRequest\nProvide url for a request, or sql alone to read stored responses. No request was sent.";
        if (target.StartsWith(OperationPrefix, StringComparison.OrdinalIgnoreCase))
            return await OperationAsync(target[OperationPrefix.Length..].Trim(), cancellationToken);
        return await SendAsync(target, method, ModelJson.Text(body), activity, cancellationToken);
    }

    private async Task<string> OperationAsync(string operationId, CancellationToken cancellationToken)
    {
        if (operationId.Length == 0)
        {
            var sessionId = ToolExecutionContext.Current?.SessionId;
            if (sessionId is null) return "Error: no active conversation.";
            var operations = OperationStore.Default.ForSession(tokens.UserId, sessionId).Select(operation => OperationStore.Envelope(operation));
            return "Operations in this conversation (up to 100, newest first):\n" + string.Join('\n', operations)
                + "\nUse resource IDs from these results in a reviewed script. A failed allocation can leave successfully created prerequisites. No cleanup was executed.";
        }
        var stored = OperationStore.Default.Find(operationId, tokens.UserId);
        if (stored is null) return "Error: operation not found or unavailable.";
        if (stored.Status is "awaitingApproval" or "expired" or "rejected" or "succeeded" or "failed" || stored.NextPollUtc > DateTimeOffset.UtcNow)
            return OperationStore.Envelope(stored);
        if (string.IsNullOrEmpty(tokens.AzureToken)) return HttpHelper.TokenMissing("AzureToken", null, "operation");
        using var request = new HttpRequestMessage(HttpMethod.Get, stored.PollUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AzureToken);
        using var response = await PollClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return OperationStore.Envelope(OperationStore.Default.ObserveResponse(stored, response, content, polling: true));
    }

    private async Task<string> SendAsync(string? url, string? method, string? body, Activity? activity, CancellationToken cancellationToken)
    {
        const bool timestamp = true;
        const int maxPages = Pages;
        var uri = ResolveTarget(url);
        if (uri is null)
            return "HTTP 400 BadRequest\nurl must be an ARM path starting with / or an absolute https:// URL without credentials, fragment or custom port. No request was sent.";
        var service = Classify(uri);
        var prefix = service.ToString().ToLowerInvariant();
        activity?.SetTag("query.service", prefix);
        var (httpMethod, methodError) = HttpHelper.ResolveMethod(string.IsNullOrWhiteSpace(method) ? "GET" : method, activity, prefix);
        if (methodError is not null) return methodError;
        body = string.IsNullOrWhiteSpace(body) ? null : body;
        return service switch
        {
            Service.Arm => await ArmAsync(uri, httpMethod!, body, maxPages, timestamp, activity, cancellationToken),
            Service.Graph => await GraphAsync(uri, httpMethod!, body, maxPages, timestamp, activity, cancellationToken),
            Service.LogAnalytics => await LogAnalyticsAsync(uri, httpMethod!, body, timestamp, activity, cancellationToken),
            Service.Storage => await StorageAsync(uri, httpMethod!, timestamp, activity, cancellationToken),
            Service.RetailPrices => await RetailAsync(uri, httpMethod!, maxPages, timestamp, activity, cancellationToken),
            _ => await PublicAsync(uri, httpMethod!, timestamp, cancellationToken),
        };
    }

    internal static Uri? ResolveTarget(string? url)
    {
        var text = url?.Trim() ?? "";
        if (text.Length == 0 || text.Contains('\\') || text.Contains('#') || text.Any(char.IsControl)) return null;
        if (text[0] == '/')
        {
            if (text.StartsWith("//", StringComparison.Ordinal)) return null;
            text = "https://" + ArmHost + text;
        }
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            && uri.UserInfo.Length == 0 && uri.IsDefaultPort && uri.IdnHost.Length > 0 ? uri : null;
    }

    // Credentials are chosen from the exact host only; any other host is a public, credential-free GET.
    internal static Service Classify(Uri uri) => uri.IdnHost.ToLowerInvariant() switch
    {
        ArmHost => Service.Arm,
        "graph.microsoft.com" => Service.Graph,
        "api.loganalytics.io" or "api.loganalytics.azure.com" or "api.applicationinsights.io" => Service.LogAnalytics,
        "prices.azure.com" => Service.RetailPrices,
        var host when StorageHost().IsMatch(host) => Service.Storage,
        _ => Service.PublicWeb,
    };

    [GeneratedRegex(@"^[a-z0-9]{3,24}\.blob\.core\.windows\.net$", RegexOptions.CultureInvariant)]
    private static partial Regex StorageHost();

    private async Task<string> ArmAsync(Uri uri, HttpMethod method, string? body, int maxPages, bool timestamp,
        Activity? activity, CancellationToken cancellationToken)
    {
        var path = uri.PathAndQuery;
        activity?.SetTag("azure.method", method.Method);
        activity?.SetTag("azure.path", path);
        activity?.SetTag("azure.has_body", body is not null);
        var token = tokens.AzureToken;
        if (string.IsNullOrEmpty(token)) return HttpHelper.TokenMissing("AzureToken", activity, "azure");
        var removedCurrencyGrouping = false;

        // Scope-prefix preflight: bare /providers/Microsoft.CostManagement|Consumption|... paths without a
        // {scope} prefix return a precise 400 with the grammar instead of an ARM 404 round-trip.
        if (ValidateScopePrefix(path) is { } scopeError)
        {
            activity?.SetTag("azure.result", "missing_scope");
            activity?.SetStatus(ActivityStatusCode.Error, "Missing scope prefix");
            return scopeError;
        }
        if (method == HttpMethod.Post)
        {
            body = CanonicalJsonBody(body);
            path = CanonicalResourceGraphPath(path, body);
            if (ValidateReadOnlyPostPath(path, activity) is { } postError) return postError;
            (body, removedCurrencyGrouping) = WithoutCurrencyGrouping(path, body);
            if (ValidateQueryBody(path, body) is { } queryError) return queryError;
        }
        var url = "https://" + ArmHost + path;
        var sendBody = method == HttpMethod.Get ? null : body;
        var response = await HttpHelper.SendWithRetryAsync(url, token, activity, "azure", method,
            sendBody, timestamp, cancellationToken: cancellationToken);
        string? requestedVersion = null, usedVersion = null;
        if (method == HttpMethod.Get || method == HttpMethod.Post)
        {
            // ARM names the supported versions but can list ones the provider does not serve yet; a provider's own
            // rejection names none, so its manifest supplies the older stable versions.
            List<string> candidates = [.. SupportedApiVersions(path, response)];
            if (candidates.Count == 0 && IsUnlistedApiVersionRejection(response) && ResourceTypeOf(path) is { } resource)
            {
                var manifest = await HttpHelper.SendWithRetryAsync($"https://{ArmHost}{resource.Scope}/providers/{resource.Namespace}?api-version=2021-04-01",
                    token, activity, "azure", HttpMethod.Get, cancellationToken: cancellationToken);
                candidates.AddRange(OlderStableVersions(manifest, resource.Type, Uri.UnescapeDataString(ApiVersionParameter().Match(path).Groups[1].Value)));
            }
            foreach (var version in candidates)
            {
                var correctedPath = WithApiVersion(path, version);
                var corrected = await HttpHelper.SendWithRetryAsync("https://" + ArmHost + correctedPath, token, activity, "azure", method,
                    sendBody, timestamp, cancellationToken: cancellationToken);
                if (corrected.StartsWith("HTTP 2", StringComparison.Ordinal))
                {
                    requestedVersion = Uri.UnescapeDataString(ApiVersionParameter().Match(path).Groups[1].Value);
                    usedVersion = version;
                    response = corrected;
                    uri = new Uri("https://" + ArmHost + correctedPath);
                    activity?.SetTag("azure.api_version_corrected", version);
                    break;
                }
                if (!IsUnlistedApiVersionRejection(corrected)) break;
            }
        }
        var result = method == HttpMethod.Get
            ? await PaginateAsync(response, uri, maxPages, next => HttpHelper.SendWithRetryAsync(next.AbsoluteUri, token, activity, "azure",
                HttpMethod.Get, cancellationToken: cancellationToken))
            : response;
        if (removedCurrencyGrouping)
        {
            activity?.SetTag("azure.currency_grouping_removed", true);
            result = AnnotateRoot(result, "_request", new Dictionary<string, string>
            {
                ["removedGrouping"] = "Currency",
                ["reason"] = "Cost Management rejects Currency as a grouping dimension and every row already carries a Currency column, so the grouping was removed; read each row's Currency.",
            });
        }
        return usedVersion is null ? result : AnnotateApiVersion(result, requestedVersion!, usedVersion);
    }

    private async Task<string> GraphAsync(Uri uri, HttpMethod method, string? body, int maxPages, bool timestamp,
        Activity? activity, CancellationToken cancellationToken)
    {
        activity?.SetTag("graph.method", method.Method);
        activity?.SetTag("graph.path", uri.AbsolutePath);
        var token = tokens.GraphToken;
        if (string.IsNullOrEmpty(token)) return HttpHelper.TokenMissing("GraphToken", activity, "graph");
        if (!uri.AbsolutePath.StartsWith("/v1.0/", StringComparison.OrdinalIgnoreCase) && !uri.AbsolutePath.StartsWith("/beta/", StringComparison.OrdinalIgnoreCase))
            return "HTTP 400 BadRequest\nMicrosoft Graph paths start with /v1.0/ or /beta/. No request was sent.";
        var response = await HttpHelper.SendWithRetryAsync(uri.AbsoluteUri, token, activity, "graph", method,
            method == HttpMethod.Get ? null : body, timestamp, GraphHeaders(), cancellationToken: cancellationToken);
        if (IsReportServiceAbsent(uri.AbsolutePath, response)) return ReportServiceAbsent(response, activity);
        if (method == HttpMethod.Get)
            response = await PaginateAsync(response, uri, maxPages, next => HttpHelper.SendWithRetryAsync(next.AbsoluteUri, token, activity, "graph",
                HttpMethod.Get, extraHeaders: GraphHeaders(), cancellationToken: cancellationToken));
        return ResponseShaper.Normalize(response);
    }

    // Directory advanced queries ($count, $search, assignedLicenses/$count, ne/not/endsWith filters) fail with 400
    // unless this header is present; simple Graph reads return the same result with or without it.
    internal static Dictionary<string, string> GraphHeaders() => new(StringComparer.OrdinalIgnoreCase) { ["ConsistencyLevel"] = "eventual" };

    // Graph answers report functions with 404 UnknownTenantId when Microsoft 365 usage reporting is not
    // provisioned for the tenant. That is a determinate source state, not a failed request.
    internal static bool IsReportServiceAbsent(string path, string result) =>
        result.StartsWith("HTTP 404", StringComparison.Ordinal)
        && result.Contains("UnknownTenantId", StringComparison.Ordinal)
        && path.Contains("/reports/", StringComparison.OrdinalIgnoreCase);

    internal static string ReportServiceAbsent(string result, Activity? activity)
    {
        activity?.SetTag("graph.result", "report_service_absent");
        var retrieved = result.Split('\n').FirstOrDefault(line => line.StartsWith(ResponseShaper.TimestampPrefix, StringComparison.Ordinal))?.Trim();
        return "HTTP 200 OK\n" + (retrieved is null ? "" : retrieved + "\n") +
            """{"reportServiceProvisioned":false,"sourceStatus":404,"sourceCode":"UnknownTenantId","meaning":"Microsoft 365 usage reporting has no data service for this tenant, so no per-user activity rows exist from this source. Activity for licensed users is unknown from reports; combine with subscribedSkus/assignedLicenses to state what is determinate (for example zero assigned seats means zero licensed users to be active or inactive)."}""";
    }

    private async Task<string> LogAnalyticsAsync(Uri uri, HttpMethod method, string? body, bool timestamp,
        Activity? activity, CancellationToken cancellationToken)
    {
        activity?.SetTag("la.host", uri.Host);
        activity?.SetTag("la.query_length", body?.Length ?? 0);
        var token = tokens.LogAnalyticsToken;
        if (string.IsNullOrEmpty(token)) return HttpHelper.TokenMissing("LogAnalyticsToken", activity, "la");
        if (method != HttpMethod.Get && method != HttpMethod.Post)
            return "HTTP 405 MethodNotAllowed\nLog Analytics and Application Insights queries use GET or POST. No request was sent.";
        if (!uri.AbsolutePath.StartsWith("/v1/", StringComparison.Ordinal))
            return "HTTP 400 BadRequest\nUse POST /v1/workspaces/{customerId}/query or /v1/apps/{appId}/query with body {\"query\":\"<KQL>\",\"timespan\":\"P1D\"}. No request was sent.";
        return await HttpHelper.SendWithRetryAsync(uri.AbsoluteUri, token, activity, "la", method,
            method == HttpMethod.Post ? CanonicalJsonBody(body) ?? "{}" : null, timestamp, cancellationToken: cancellationToken);
    }

    private async Task<string> StorageAsync(Uri uri, HttpMethod method, bool timestamp, Activity? activity, CancellationToken cancellationToken)
    {
        activity?.SetTag("storage.account", uri.Host.Split('.')[0]);
        activity?.SetTag("storage.path", uri.AbsolutePath);
        var token = tokens.StorageToken;
        if (string.IsNullOrEmpty(token)) return HttpHelper.TokenMissing("StorageToken", activity, "storage");
        if (method != HttpMethod.Get)
            return "HTTP 405 MethodNotAllowed\nBlob Storage is read-only here: GET lists or reads blobs. No request was sent.";
        var listing = Regex.IsMatch(uri.Query, @"[?&]comp=list", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ToolExecutionContext.Current?.CancellationToken ?? CancellationToken.None);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("FinOps-Dashboard/1.0");
        request.Headers.TryAddWithoutValidation("x-ms-version", StorageVersion);
        if (!listing) request.Headers.TryAddWithoutValidation("x-ms-range", $"bytes=0-{MaxStorageBytes - 1}");
        using var response = await StorageHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        var (text, bytes, capped) = await ReadBoundedAsync(response.Content, listing ? 16_000_000 : MaxStorageBytes, cancellation.Token);
        var total = response.Content.Headers.ContentRange?.Length;
        var truncated = capped || total > bytes;
        var status = (int)response.StatusCode == 206 && !truncated ? "HTTP 200 OK" : $"HTTP {(int)response.StatusCode} {response.StatusCode}";
        activity?.SetTag("storage.status_code", (int)response.StatusCode);
        activity?.SetTag("storage.bytes", bytes);
        activity?.SetTag("storage.truncated", truncated);
        var result = status + "\n" + (timestamp ? ResponseShaper.TimestampLine() : "");
        if (!response.IsSuccessStatusCode) return result + (text.Length > 4000 ? text[..4000] : text);
        var shaped = ResponseShaper.Normalize(result + text, truncated);
        if (!shaped.EndsWith(text, StringComparison.Ordinal) || !truncated && IsJson(text)) return shaped;
        // Other bodies (truncated JSON, text, binary formats such as Parquet) are never inlined whole.
        const int maxText = 100_000;
        var shown = text.Length > maxText ? text[..maxText] : text;
        return result + shown + (truncated || shown.Length < text.Length
            ? $"\n\n[PARTIAL RESULT: showing {shown.Length} characters of a {(total is { } length ? length.ToString(CultureInfo.InvariantCulture) : "larger")}-byte blob. Aggregate from an export upload or a narrower blob before a complete-coverage claim.]"
            : "");
    }

    private static bool IsJson(string text)
    {
        var lead = text.AsSpan().TrimStart();
        if (lead.IsEmpty || lead[0] is not ('{' or '[')) return false;
        try
        {
            using var document = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static async Task<(string Text, int Bytes, bool Capped)> ReadBoundedAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[65_536];
        int read;
        while (buffer.Length < maxBytes && (read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, maxBytes - buffer.Length)), cancellationToken)) > 0)
            buffer.Write(chunk, 0, read);
        var capped = buffer.Length >= maxBytes && await stream.ReadAsync(chunk.AsMemory(0, 1), cancellationToken) > 0;
        return (Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), (int)buffer.Length, capped);
    }

    private static async Task<string> RetailAsync(Uri uri, HttpMethod method, int maxPages, bool timestamp,
        Activity? activity, CancellationToken cancellationToken)
    {
        if (method != HttpMethod.Get)
            return "HTTP 405 MethodNotAllowed\nThe Retail Prices API is GET only. No request was sent.";
        if (!uri.AbsolutePath.TrimEnd('/').Equals("/api/retail/prices", StringComparison.OrdinalIgnoreCase))
            return "HTTP 400 BadRequest\nUse https://prices.azure.com/api/retail/prices?currencyCode=USD&$filter=<OData>. No request was sent.";
        var query = QueryHelpers.ParseQuery(uri.Query);
        var filter = query.FirstOrDefault(pair => pair.Key.Equals("$filter", StringComparison.OrdinalIgnoreCase)).Value.ToString();
        if (string.IsNullOrWhiteSpace(filter))
            return "HTTP 400 BadRequest\nAdd an OData $filter; the unfiltered catalogue has millions of rows. No request was sent.";
        var currency = query.FirstOrDefault(pair => pair.Key.Equals("currencyCode", StringComparison.OrdinalIgnoreCase)).Value.ToString().Trim('\'', '"', ' ').ToUpperInvariant();
        if (currency.Length == 0) currency = "USD";
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetter))
            return "HTTP 400 BadRequest\ncurrencyCode must be a three-letter ISO code. No request was sent.";

        JsonArray items = [];
        var next = BuildRetailUrl(query, filter, currency);
        var pages = 0;
        for (; next is not null && pages < maxPages; pages++)
        {
            var (status, reason, body) = await FetchRetailPageAsync(next, activity, cancellationToken);
            if (status is < 200 or >= 300)
                return $"HTTP {status} {reason}\n{(body.Length > 800 ? body[..800] : body)}";
            var root = JsonNode.Parse(body);
            if (root?["Items"] is JsonArray pageItems)
                foreach (var item in pageItems.ToArray())
                {
                    pageItems.Remove(item);
                    items.Add(item);
                }
            next = root?["NextPageLink"] is JsonValue link && link.TryGetValue<string>(out var value)
                && value.StartsWith(RetailBase, StringComparison.OrdinalIgnoreCase) ? value : null;
        }
        activity?.SetTag("pricing.pages", pages);
        activity?.SetTag("pricing.rows", items.Count);
        return "HTTP 200 OK\n" + (timestamp ? ResponseShaper.TimestampLine() : "") + new JsonObject
        {
            ["source"] = "Azure Retail Prices API",
            ["retrievedAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["filter"] = filter,
            ["currencyCode"] = currency,
            ["pages"] = pages,
            ["complete"] = next is null,
            ["count"] = items.Count,
            ["Items"] = items,
        }.ToJsonString();
    }

    // Rebuilds the pinned origin with the model's own filter and options; the host owns paging ($top/$skip).
    internal static string BuildRetailUrl(IDictionary<string, Microsoft.Extensions.Primitives.StringValues> query, string filter, string currency)
    {
        var parameters = new List<KeyValuePair<string, string?>>
        {
            new("api-version", query.FirstOrDefault(pair => pair.Key.Equals("api-version", StringComparison.OrdinalIgnoreCase)).Value.ToString() is { Length: > 0 } version ? version : "2023-01-01-preview"),
            new("currencyCode", currency),
            new("$filter", filter),
        };
        foreach (var pair in query)
            if (pair.Key.ToLowerInvariant() is not ("api-version" or "currencycode" or "$filter" or "$top" or "$skip"))
                parameters.Add(new(pair.Key, pair.Value.ToString()));
        return QueryHelpers.AddQueryString(RetailBase, parameters);
    }

    private static async Task<(int Status, string Reason, string Body)> FetchRetailPageAsync(string url, Activity? activity, CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ToolExecutionContext.Current?.CancellationToken ?? CancellationToken.None);
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("FinOps-Dashboard/1.0");
            using var response = await RetailHttp.SendAsync(request, cancellation.Token);
            var body = await response.Content.ReadAsStringAsync(cancellation.Token);
            var status = (int)response.StatusCode;
            if (status is not (429 or >= 500) || attempt == RetailAttempts)
                return (status, response.ReasonPhrase ?? response.StatusCode.ToString(), body);

            var waitSeconds = Math.Max(1, response.Headers.RetryAfter?.Delta?.TotalSeconds
                ?? Math.Min(Math.Pow(2, attempt) + Random.Shared.NextDouble(), 30));
            activity?.SetTag($"pricing.retry_{attempt}", $"{status}, waiting {waitSeconds:F0}s");
            if (Activity.Current?.GetBaggageItem("finops.turn.id") is { } turnKey
                && HttpHelper.RetryReporters.TryGetValue(turnKey, out var report))
            {
                try { await report(new(attempt, waitSeconds, url, "pricing", status)); }
                catch (Exception ex) { HttpHelper.Logger?.LogWarning(ex, "SSE cooling_down emit failed for pricing attempt={Attempt}", attempt); }
            }
            await Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellation.Token);
        }
    }

    private static async Task<string> PublicAsync(Uri uri, HttpMethod method, bool timestamp, CancellationToken cancellationToken)
    {
        if (method != HttpMethod.Get)
            return "HTTP 405 MethodNotAllowed\nPublic web requests are GET only and never carry credentials. No request was sent.";
        if (PublicWebReader.IsBlockedHost(uri))
            return "HTTP 403 Forbidden\nPrivate, loopback and link-local hosts are not reachable. No request was sent.";
        var cancellation = ToolExecutionContext.Current?.CancellationToken ?? CancellationToken.None;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cancellation);
        return await PublicWebReader.FetchPageAsync(PublicWebReader.Http, PublicWebReader.Canonicalize(uri), linked.Token, timestamp);
    }

    /// <summary>
    /// Follows same-origin nextLink/@odata.nextLink pages of a successful GET, merging their value arrays.
    /// Adds pagesRead and complete only when a next link exists; a failed later page keeps earlier pages
    /// and reports partial coverage instead of failing the call.
    /// </summary>
    internal static async Task<string> PaginateAsync(string response, Uri origin, int maxPages, Func<Uri, Task<string>> fetch)
    {
        var (preamble, body) = ResponseShaper.SplitPreamble(response);
        if (!preamble.StartsWith("HTTP 2", StringComparison.Ordinal) || !body.StartsWith('{')) return response;
        JsonObject? root;
        try { root = JsonNode.Parse(body) as JsonObject; }
        catch (JsonException) { return response; }
        if (root?["value"] is not JsonArray items) return response;
        var linkName = new[] { "nextLink", "@odata.nextLink" }.FirstOrDefault(name => NextLink(root, name, origin) is not null);
        if (linkName is null) return response;
        var next = NextLink(root, linkName, origin);
        var pages = 1;
        string? failure = null;
        while (next is not null && pages < maxPages)
        {
            var page = await fetch(next);
            var (pagePreamble, pageBody) = ResponseShaper.SplitPreamble(page);
            JsonObject? pageRoot = null;
            if (pagePreamble.StartsWith("HTTP 2", StringComparison.Ordinal))
                try { pageRoot = JsonNode.Parse(pageBody) as JsonObject; }
                catch (JsonException) { }
            if (pageRoot?["value"] is not JsonArray pageItems)
            {
                failure = page.Split('\n')[0];
                break;
            }
            foreach (var item in pageItems.ToArray())
            {
                pageItems.Remove(item);
                items.Add(item);
            }
            pages++;
            next = NextLink(pageRoot, linkName, origin);
        }
        root[linkName] = next?.AbsoluteUri;
        root["pagesRead"] = pages;
        root["complete"] = next is null;
        if (failure is not null) root["pageFailure"] = failure + " on a later page; earlier pages are included and coverage is partial.";
        return preamble + root.ToJsonString();
    }

    private static Uri? NextLink(JsonObject root, string name, Uri origin) =>
        root[name] is JsonValue value && value.TryGetValue<string>(out var text)
        && Uri.TryCreate(text, UriKind.Absolute, out var link) && link.Scheme == Uri.UriSchemeHttps && link.IsDefaultPort
        && link.UserInfo.Length == 0 && string.Equals(link.IdnHost, origin.IdnHost, StringComparison.OrdinalIgnoreCase) ? link : null;

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

    internal static string? ValidateReadOnlyPostPath(string path, Activity? activity)
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
            // Read-only policy compliance queries; triggerEvaluation stays blocked.
            $@"^(?:{subscriptionScope}|{managementGroupScope})/providers/Microsoft\.PolicyInsights/(?:policyStates/latest/summarize|policyStates/(?:latest|default)/queryResults|policyEvents/default/queryResults)$",
            // Read-only placement-likelihood diagnostic; creates no resources.
            @"^/subscriptions/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/providers/Microsoft\.Compute/locations/[a-z0-9]+/placementScores/spot/generate$",
            // Diagnostic probe from an existing VM; the body is restricted by ValidateConnectivityBody.
            $@"^{ConnectivityCheckPath}$",
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

    [GeneratedRegex(@"[?&]api-version=([^&#]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ApiVersionParameter();

    [GeneratedRegex(@"^/subscriptions/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})(/providers/Microsoft\.ResourceGraph/resources(?:\?.*)?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SubscriptionScopedResourceGraph();

    /// <summary>
    /// Resource Graph takes its scope from the body, so a subscription path prefix that names a subscription
    /// already listed in the body's subscriptions array is dropped. Any other prefix is left for the allowlist to reject.
    /// </summary>
    internal static string CanonicalResourceGraphPath(string path, string? body)
    {
        var match = SubscriptionScopedResourceGraph().Match(path);
        if (!match.Success || string.IsNullOrWhiteSpace(body)) return path;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("subscriptions", out var subscriptions)
                && subscriptions.ValueKind == JsonValueKind.Array
                && subscriptions.EnumerateArray().Any(subscription => subscription.ValueKind == JsonValueKind.String
                    && string.Equals(subscription.GetString(), match.Groups[1].Value, StringComparison.OrdinalIgnoreCase)))
                return match.Groups[2].Value;
        }
        catch (JsonException) { }
        return path;
    }

    [GeneratedRegex(@"supported (?:api-)?versions are '([^']+)'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SupportedVersionList();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}(-[a-z]+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ApiVersionValue();

    /// <summary>
    /// When ARM rejects the requested api-version and names the supported ones, returns the newest stable
    /// listed version (or newest preview when no stable exists) so a read can be retried once. Otherwise null.
    /// </summary>
    internal static string? SupportedApiVersion(string path, string response) => SupportedApiVersions(path, response).FirstOrDefault();

    /// <summary>
    /// The newest three stable listed versions, newest first (previews when no stable exists). ARM's list can name
    /// versions the provider does not serve yet, so a provider UnsupportedApiVersion reply moves on to the next one.
    /// </summary>
    internal static IReadOnlyList<string> SupportedApiVersions(string path, string response)
    {
        if (!response.StartsWith("HTTP 400", StringComparison.Ordinal) && !response.StartsWith("HTTP 404", StringComparison.Ordinal)) return [];
        var requested = ApiVersionParameter().Match(path);
        if (!requested.Success) return [];
        string? code, message;
        try
        {
            using var document = JsonDocument.Parse(ResponseShaper.SplitPreamble(response).Body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object) return [];
            code = error.TryGetProperty("code", out var codeValue) && codeValue.ValueKind == JsonValueKind.String ? codeValue.GetString() : null;
            message = error.TryGetProperty("message", out var messageValue) && messageValue.ValueKind == JsonValueKind.String ? messageValue.GetString() : null;
        }
        catch (JsonException) { return []; }
        if (code is null || message is null
            || !(code is "InvalidResourceType" or "NoRegisteredProviderFound" || code.Contains("ApiVersion", StringComparison.OrdinalIgnoreCase)))
            return [];
        var list = SupportedVersionList().Match(message);
        if (!list.Success) return [];
        var versions = list.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(version => ApiVersionValue().IsMatch(version)).ToList();
        var stable = versions.Where(version => version.Length == 10).ToList();
        var ordered = (stable.Count > 0 ? stable : versions).OrderByDescending(version => version, StringComparer.Ordinal).ToList();
        var rejected = Uri.UnescapeDataString(requested.Groups[1].Value);
        // A rejected newest version means the problem is not the version, so nothing is retried.
        return ordered.Count == 0 || ordered[0].Equals(rejected, StringComparison.OrdinalIgnoreCase)
            ? []
            : ordered.Where(version => !version.Equals(rejected, StringComparison.OrdinalIgnoreCase)).Take(3).ToList();
    }

    internal static string WithApiVersion(string path, string version) =>
        ApiVersionParameter().Replace(path, match => match.Value[..(match.Value.IndexOf('=') + 1)] + Uri.EscapeDataString(version), 1);

    /// <summary>True when a resource provider rejects the api-version with UnsupportedApiVersion without naming supported versions.</summary>
    internal static bool IsUnlistedApiVersionRejection(string response)
    {
        if (!response.StartsWith("HTTP 400", StringComparison.Ordinal)) return false;
        try
        {
            using var document = JsonDocument.Parse(ResponseShaper.SplitPreamble(response).Body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                && string.Equals(code.GetString(), "UnsupportedApiVersion", StringComparison.OrdinalIgnoreCase)
                && !(error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                    && SupportedVersionList().IsMatch(message.GetString()!));
        }
        catch (JsonException) { return false; }
    }

    [GeneratedRegex(@"^/subscriptions/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}(?=/)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SubscriptionPrefix();

    /// <summary>
    /// The provider namespace and resource type addressed by an ARM path (for example Microsoft.CostManagement and
    /// scheduledActions), with the subscription prefix used to read that provider's manifest; null when the path names no provider.
    /// </summary>
    internal static (string Scope, string Namespace, string Type)? ResourceTypeOf(string path)
    {
        var queryIndex = path.IndexOf('?');
        var clean = queryIndex < 0 ? path : path[..queryIndex];
        var index = clean.LastIndexOf("/providers/", StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        var segments = clean[(index + "/providers/".Length)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || segments.Any(segment => !ResourceSegment().IsMatch(segment))) return null;
        var type = string.Join('/', segments.Skip(1).Where((_, position) => position % 2 == 0));
        var scope = SubscriptionPrefix().Match(clean) is { Success: true } subscription ? subscription.Value : "";
        return (scope, segments[0], type);
    }

    [GeneratedRegex(@"^[A-Za-z0-9._()~-]{1,260}$", RegexOptions.CultureInvariant)]
    private static partial Regex ResourceSegment();

    /// <summary>Stable manifest api-versions of a resource type older than the rejected one, newest first (at most two).</summary>
    internal static IReadOnlyList<string> OlderStableVersions(string manifestResponse, string type, string requested)
    {
        if (!manifestResponse.StartsWith("HTTP 2", StringComparison.Ordinal)) return [];
        try
        {
            using var document = JsonDocument.Parse(ResponseShaper.SplitPreamble(manifestResponse).Body);
            if (!document.RootElement.TryGetProperty("resourceTypes", out var types) || types.ValueKind != JsonValueKind.Array) return [];
            foreach (var entry in types.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("resourceType", out var name) || name.ValueKind != JsonValueKind.String
                    || !string.Equals(name.GetString(), type, StringComparison.OrdinalIgnoreCase)
                    || !entry.TryGetProperty("apiVersions", out var versions) || versions.ValueKind != JsonValueKind.Array) continue;
                return versions.EnumerateArray()
                    .Where(version => version.ValueKind == JsonValueKind.String)
                    .Select(version => version.GetString()!)
                    .Where(version => version.Length == 10 && ApiVersionValue().IsMatch(version) && string.CompareOrdinal(version, requested) < 0)
                    .Distinct(StringComparer.Ordinal)
                    .OrderDescending(StringComparer.Ordinal)
                    .Take(2)
                    .ToList();
            }
        }
        catch (JsonException) { }
        return [];
    }

    /// <summary>Adds a leading root _apiVersion note to a JSON object body; other bodies are unchanged.</summary>
    internal static string AnnotateApiVersion(string response, string requested, string used) =>
        AnnotateRoot(response, "_apiVersion", new Dictionary<string, string>
        {
            ["requested"] = requested,
            ["used"] = used,
            ["reason"] = "The service rejected the requested api-version for this resource type; a supported version (stable preferred) answered.",
        });

    /// <summary>Adds a leading root note to a JSON object body; other bodies are unchanged.</summary>
    internal static string AnnotateRoot(string response, string name, IReadOnlyDictionary<string, string> note)
    {
        var (preamble, body) = ResponseShaper.SplitPreamble(response);
        var trimmed = body.TrimStart();
        if (!trimmed.StartsWith('{')) return response;
        var rest = trimmed[1..].TrimStart();
        return preamble + "{" + JsonSerializer.Serialize(name) + ":" + JsonSerializer.Serialize(note) + (rest.StartsWith('}') ? "" : ",") + rest;
    }

    // Cost Management rows always carry a Currency column and the service rejects a Currency dimension grouping,
    // so that grouping is redundant: it is removed (and reported through _request) rather than failing the query.
    // A TagKey grouping named Currency is a real tag and is kept.
    internal static (string? Body, bool Removed) WithoutCurrencyGrouping(string path, string? body)
    {
        var queryIndex = path.IndexOf('?');
        var requestPath = (queryIndex < 0 ? path : path[..queryIndex]).TrimEnd('/');
        if (body is null || !requestPath.EndsWith("/Microsoft.CostManagement/query", StringComparison.OrdinalIgnoreCase)) return (body, false);
        try
        {
            if (JsonNode.Parse(body) is not JsonObject root || root["dataset"] is not JsonObject dataset
                || dataset["grouping"] is not JsonArray grouping) return (body, false);
            static bool Text(JsonNode? node, string expected) =>
                node is JsonValue value && value.TryGetValue<string>(out var text) && text.Equals(expected, StringComparison.OrdinalIgnoreCase);
            var currency = grouping.Where(item => item is JsonObject entry && Text(entry["type"], "Dimension") && Text(entry["name"], "Currency")).ToList();
            if (currency.Count == 0) return (body, false);
            foreach (var item in currency) grouping.Remove(item);
            return (root.ToJsonString(), true);
        }
        catch (JsonException) { return (body, false); }
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
        if (Regex.IsMatch(requestPath, $"^{ConnectivityCheckPath}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return ValidateConnectivityBody(body);
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

    private const string ConnectivityCheckPath =
        @"/subscriptions/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/resourceGroups/[^/?#%]+/providers/Microsoft\.Network/networkWatchers/[^/?#%]+/connectivityCheck";

    [GeneratedRegex(@"^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/[^/\\?#%]+/providers/Microsoft\.Compute/virtualMachines/[^/\\?#%]+$", RegexOptions.IgnoreCase)]
    private static partial Regex VirtualMachineId();

    // Only a probe from an existing VM to one destination: no other source types, captures or settings.
    internal static string? ValidateConnectivityBody(string? body)
    {
        const string error = "HTTP 400 BadRequest\nconnectivityCheck body must be {\"source\":{\"resourceId\":\"<existing VM resource ID>\"},\"destination\":{\"address\":\"<host or IP>\",\"port\":<1-65535>},\"protocol\":\"Tcp\"}. No request was sent.";
        try
        {
            using var document = JsonDocument.Parse(body ?? "");
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Any(property => property.Name is not ("source" or "destination" or "protocol" or "preferredIPVersion" or "protocolConfiguration"))
                || !root.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object
                || source.EnumerateObject().Any(property => property.Name is not ("resourceId" or "port"))
                || !source.TryGetProperty("resourceId", out var vm) || vm.ValueKind != JsonValueKind.String
                || !VirtualMachineId().IsMatch(vm.GetString() ?? "")
                || !root.TryGetProperty("destination", out var destination) || destination.ValueKind != JsonValueKind.Object
                || destination.EnumerateObject().Any(property => property.Name is not ("address" or "resourceId" or "port"))
                || !destination.TryGetProperty("address", out _) && !destination.TryGetProperty("resourceId", out _))
                return error;
            if (destination.TryGetProperty("port", out var port)
                && !(port.ValueKind == JsonValueKind.Number && port.TryGetInt32(out var number) && number is >= 1 and <= 65535
                    || port.ValueKind == JsonValueKind.String && int.TryParse(port.GetString(), out number) && number is >= 1 and <= 65535))
                return error;
            return null;
        }
        catch (JsonException) { return error; }
    }

    private static string BlockMutatingPost(Activity? activity)
    {
        activity?.SetTag("azure.result", "blocked_mutating_post");
        activity?.SetStatus(ActivityStatusCode.Error, "Mutating POST blocked");
        return "HTTP 403 Forbidden\nThis agent only performs allowlisted read-only Azure POST operations. Mutating actions such as start, restart, deallocate, power off, or return are blocked.";
    }
}
