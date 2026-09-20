using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http.Headers;
using System.Text;

namespace AzureFinOps.Dashboard.Infrastructure;

/// <summary>
/// Shared HTTP helper for all API tools — handles retry on 429/5xx, response formatting, and telemetry.
/// Eliminates duplicated retry loops, response formatting, and HttpClient instances across tools.
/// </summary>
public static class HttpHelper
{
    // Shared IPv4-only SocketsHttpHandler (see Ipv4HttpHandler.cs). Corp egress
    // drops IPv6 SYNs and the OS only gives up after ~21 s; forcing IPv4 here
    // matches the factory defaults wired up in Program.cs.
    private static readonly HttpClient Http = new(Ipv4HttpHandler.Create())
    { Timeout = TimeSpan.FromSeconds(60) };

    public static readonly ActivitySource Telemetry = new("AzureFinOps.AI");

    private static readonly Meter Meter = new("AzureFinOps.AI");
    private static readonly Counter<long> ThrottleRetries =
        Meter.CreateCounter<long>("finops.throttle.retries", description: "HTTP retries triggered by 429 or transient 5xx");
    // Time the caller actually spent waiting on backoff (sum of Task.Delay
    // across all retry attempts on a single call). Distinct from request
    // latency — this is pure throttle penalty.
    private static readonly Histogram<double> RetryWaitMs =
        Meter.CreateHistogram<double>("finops.http.retry_wait_ms", "ms", "Cumulative backoff wait per HTTP call");
    private static readonly Histogram<double> RequestTotalMs =
        Meter.CreateHistogram<double>("finops.http.request_total_ms", "ms", "Total time per HTTP call including retries");

    /// <summary>Optional logger plumbed from Program.cs so retries surface in
    /// the console / Application Insights traces instead of being silently
    /// counted. Null when running outside the host (tests).</summary>
    public static ILogger? Logger { get; set; }

    /// <summary>
    /// Per-turn retry status, resolved by the owner/session context established
    /// inside the protected SDK callback. Cost throttles carry the full deadline
    /// and whether this request will retry automatically.
    /// </summary>
    public sealed record RetryNotice(int Attempt, double WaitSeconds, string Url, string Tool, int Status,
        DateTimeOffset? RetryAtUtc = null, bool? WillRetry = null);

    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Func<RetryNotice, Task>> RetryReporters = new();

    /// <summary>
    /// Resolves the calling user's id from the per-turn Activity Baggage
    /// (<c>finops.turn.id</c> = <c>{userId}:{sessionId}</c>, stamped by ChatEndpoints
    /// before SendAsync). Null when called outside a chat turn. Used to bind
    /// generated artifacts (scripts, decks) to their owner so the download
    /// endpoints can enforce per-user access.
    /// </summary>
    public static long? CurrentTurnUserId()
    {
        var turnKey = Activity.Current?.GetBaggageItem("finops.turn.id");
        if (string.IsNullOrEmpty(turnKey)) return null;
        var sep = turnKey.IndexOf(':');
        var uidPart = sep > 0 ? turnKey[..sep] : turnKey;
        return long.TryParse(uidPart, out var uid) ? uid : null;
    }

    private const int MaxThrottleRetries = 5;
    private const int MaxInteractiveCostAttempts = 2;

    private const int MaxRetryWaitSeconds = 60;

    private static readonly CostQueryCoordinator CostQueries = new();

    internal static bool IsInteractiveCostQueryUrl(string url) =>
        url.Contains("/Microsoft.CostManagement/query", StringComparison.OrdinalIgnoreCase)
        || url.Contains("/Microsoft.CostManagement/forecast", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Sends HTTP requests with bounded retries. Cost query/forecast calls are
    /// tenant-serialized and retry once after a service deadline of at most five
    /// minutes; longer deadlines are returned without shortening them. Other
    /// retryable reads retain up to five attempts. Host cancellation interrupts waits.
    /// Returns formatted "HTTP {status}\n{body}" string for the LLM.
    /// </summary>
    public static async Task<string> SendWithRetryAsync(
        string url,
        string token,
        Activity? activity,
        string telemetryPrefix,
        HttpMethod? method = null,
        string? jsonBody = null,
        bool includeTimestamp = false,
        Dictionary<string, string>? extraHeaders = null,
        int? maxResponseChars = null,
        bool bypassCostManagementGate = false,
        int? maxAttemptsOverride = null,
        CancellationToken cancellationToken = default)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, ToolExecutionContext.Current?.CancellationToken ?? CancellationToken.None);
        var mutation = method == HttpMethod.Put || method == HttpMethod.Patch;
        OperationStore.Operation? operation = null;
        if (mutation && OperationStore.IsArmUrl(url))
        {
            var context = ToolExecutionContext.Current;
            if (context?.UserId is not { } owner || context.SessionId is null)
                return "HTTP 403 Forbidden\nA host-owned active turn is required for Azure changes.";
            cancellation.Token.ThrowIfCancellationRequested();
            if (context.ApprovedOperationId is { } approvedId)
            {
                operation = OperationStore.Default.Find(approvedId, owner);
                if (operation is null || !OperationStore.Default.MatchesApproved(operation, owner, context.SessionId, method!.Method, url, jsonBody))
                    return "HTTP 403 Forbidden\nThe approved operation does not match this request.";
            }
            else
            {
                operation = OperationStore.Default.Begin(owner, context.SessionId, method!.Method, url, jsonBody, out var created, requiresApproval: true);
                return (operation.Status == "awaitingApproval" ? "HTTP 409 ApprovalRequired\n" : "HTTP 202 Accepted\n") + OperationStore.Envelope(operation, duplicate: !created);
            }
        }
        async Task<string> Send(CancellationToken requestToken)
        {
            try
            {
                return await SendCoreAsync(url, token, activity, telemetryPrefix, method, jsonBody, includeTimestamp, extraHeaders,
                    maxResponseChars, bypassCostManagementGate, mutation ? 1 : maxAttemptsOverride, requestToken, operation);
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException)
            {
                if (operation is not null) OperationStore.Default.MarkUncertain(operation);
                throw;
            }
        }
        if (!IsInteractiveCostQueryUrl(url)) return await Send(cancellation.Token);
        return await CostQueries.ExecuteAsync(CostQueryCoordinator.TenantKey(token),
            CostQueryCoordinator.RequestKey(token, (method ?? HttpMethod.Get).Method, url, jsonBody), Send, cancellation.Token,
            (retryAt, willRetry) => ReportRetryAsync(CurrentRetryReporter(), new(0, Math.Max(0, (retryAt - DateTimeOffset.UtcNow).TotalSeconds),
                url, telemetryPrefix, 429, retryAt, willRetry)));
    }

    private static Func<RetryNotice, Task>? CurrentRetryReporter()
    {
        var context = ToolExecutionContext.Current;
        var key = context?.SessionId is not null ? $"{context.UserId}:{context.SessionId}" : "";
        return RetryReporters.TryGetValue(key, out var report) ? report : null;
    }

    private static async Task ReportRetryAsync(Func<RetryNotice, Task>? report, RetryNotice notice)
    {
        if (report is null) return;
        try { await report(notice); }
        catch (Exception exception)
        {
            Logger?.LogWarning("SSE retry status could not be emitted for {Tool}: {ErrorType}", notice.Tool, exception.GetType().Name);
        }
    }

    private static async Task<string> SendCoreAsync(
        string url,
        string token,
        Activity? activity,
        string telemetryPrefix,
        HttpMethod? method = null,
        string? jsonBody = null,
        bool includeTimestamp = false,
        Dictionary<string, string>? extraHeaders = null,
        int? maxResponseChars = null,
        bool bypassCostManagementGate = false,
        int? maxAttemptsOverride = null,
        CancellationToken cancellationToken = default,
        OperationStore.Operation? pendingOperation = null)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, ToolExecutionContext.Current?.CancellationToken ?? CancellationToken.None);
        var requestToken = cancellation.Token;
        requestToken.ThrowIfCancellationRequested();
        method ??= HttpMethod.Get;

        var totalSw = Stopwatch.StartNew();
        var totalWaitSec = 0.0;
        var retryCount = 0;
        HttpResponseMessage res = null!;

        // Resolve the per-turn SSE reporter ONCE up front — used for queue waits,
        // in-flight heartbeats, and retry backoffs alike. Returns no-op when absent.
        var report = CurrentRetryReporter();
        // Never fall back from an exact turn key to a user-level match. A
        // scheduled job and an interactive chat can run concurrently for one
        // user; user-level routing can inject a job's retry details into the
        // wrong conversation. Missing baggage means no SSE status event.

        // Gate Cost Management / Consumption / Billing calls behind a small global
        // semaphore (2 concurrent). Without this, the LLM (esp. via BulkAzureRequest
        // parallelism=20) fan-fires parallel /query calls that all collide on the
        // per-tenant throttle. While queued we emit cooling_down so the UI shows
        // "waiting in queue" instead of a frozen tool row. The opt-out is ONLY
        // valid for read-only metadata GETs issued by bounded aggregate tools;
        // it can never bypass query/forecast serialization or method security.
        {
            var maxAttempts = Math.Clamp(
                maxAttemptsOverride ?? (IsInteractiveCostQueryUrl(url)
                    ? MaxInteractiveCostAttempts
                    : MaxThrottleRetries),
                1,
                MaxThrottleRetries);
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                using var req = new HttpRequestMessage(method, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.Add("User-Agent", "FinOps-Dashboard/1.0");

                if (extraHeaders is not null)
                    foreach (var (key, value) in extraHeaders)
                        req.Headers.TryAddWithoutValidation(key, value);

                if (jsonBody is not null)
                    req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                // Heartbeat: if the request itself takes >5s (slow CM backend, async report
                // generation, etc.) emit periodic cooling_down events keyed by url so the
                // UI ghost row stays alive instead of looking frozen. Runs concurrently
                // with the actual request; cancelled the instant the response arrives.
                using var hbCts = new CancellationTokenSource();
                var heartbeat = report is null ? Task.CompletedTask : Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(5000, hbCts.Token);
                        while (!hbCts.IsCancellationRequested)
                        {
                            try { await report(new(0, 6, url, telemetryPrefix + " (slow)", 0)); }
                            catch (Exception emitEx) { Logger?.LogWarning(emitEx, "SSE slow emit failed for {Tool}", telemetryPrefix); }
                            await Task.Delay(5000, hbCts.Token);
                        }
                    }
                    catch (OperationCanceledException) { /* expected on response */ }
                }, hbCts.Token);

                try
                {
                    res = await Http.SendAsync(req, requestToken);
                }
                finally
                {
                    hbCts.Cancel();
                    try { await heartbeat; } catch { /* heartbeat already swallows */ }
                }

                // Retry on 429 (throttle) and transient 5xx (502/503/504 — typical ARM regional
                // failover or backend hiccups). Other non-success codes return to the caller.
                var status = (int)res.StatusCode;
                var isThrottle = status == 429;
                var costThrottle = isThrottle && IsInteractiveCostQueryUrl(url);
                var isTransientServer = status == 502 || status == 503 || status == 504;
                if (!isThrottle && !isTransientServer) break;
                var waitSeconds = ResolveRetryAfterSeconds(res, attempt, costThrottle);
                DateTimeOffset? retryAt = null;
                if (costThrottle)
                {
                    var final = attempt == maxAttempts - 1 || waitSeconds > CostQueryCoordinator.MaximumAutomaticWait.TotalSeconds;
                    retryAt = CostQueries.RecordThrottle(CostQueryCoordinator.TenantKey(token), waitSeconds, final);
                    if (final)
                    {
                        await ReportRetryAsync(report, new(attempt + 1, waitSeconds, url, telemetryPrefix, status, retryAt, false));
                        break;
                    }
                }
                if (attempt == maxAttempts - 1) break; // last attempt — return as-is to caller

                totalWaitSec += waitSeconds;
                retryCount++;
                var reason = isThrottle ? "429" : status.ToString();
                activity?.SetTag($"{telemetryPrefix}.retry_{attempt}", $"{reason}, waiting {waitSeconds:F0}s");
                ThrottleRetries.Add(1,
                    new KeyValuePair<string, object?>("status", reason),
                    new KeyValuePair<string, object?>("tool", telemetryPrefix));
                // Loud log so silent throttling can never hide a 60-second wait
                // again. Prefix matches the tool/scope tag in App Insights.
                Logger?.LogWarning("HTTP retry {Tool} attempt={Attempt} status={Status} waitSec={Wait:F1} url={Url}",
                    telemetryPrefix, attempt + 1, reason, waitSeconds, url);
                // Look up the SSE reporter via Activity Baggage — baggage
                // propagates across W3C tracecontext boundaries (including the
                // Copilot CLI subprocess JSON-RPC tool callback) where RootId
                // does not. ChatEndpoints stamps "finops.turn.id" (userId:sessionId)
                // on the chat activity before SendAsync.
                await ReportRetryAsync(report, new(attempt + 1, waitSeconds, url, telemetryPrefix, status,
                    retryAt, costThrottle ? true : null));
                res.Dispose();
                await Task.Delay(TimeSpan.FromSeconds(waitSeconds), requestToken);
            }
        }

        using var completedResponse = res;
        var responseBody = await res.Content.ReadAsStringAsync(requestToken);
        totalSw.Stop();

        RequestTotalMs.Record(totalSw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("tool", telemetryPrefix),
            new KeyValuePair<string, object?>("status", (int)res.StatusCode));
        if (retryCount > 0)
        {
            RetryWaitMs.Record(totalWaitSec * 1000.0,
                new KeyValuePair<string, object?>("tool", telemetryPrefix));
            Logger?.LogWarning("HTTP retried {Tool} retries={Retries} totalWaitSec={Wait:F1} totalMs={Total:F0} url={Url}",
                telemetryPrefix, retryCount, totalWaitSec, totalSw.Elapsed.TotalMilliseconds, url);
            activity?.SetTag($"{telemetryPrefix}.total_retries", retryCount);
            activity?.SetTag($"{telemetryPrefix}.total_wait_sec", totalWaitSec);
        }

        activity?.SetTag($"{telemetryPrefix}.status_code", (int)res.StatusCode);
        activity?.SetTag($"{telemetryPrefix}.response_length", responseBody.Length);
        activity?.SetTag($"{telemetryPrefix}.result", res.IsSuccessStatusCode ? "success" : "http_error");

        if (!res.IsSuccessStatusCode)
            activity?.SetStatus(ActivityStatusCode.Error, $"HTTP {(int)res.StatusCode}");

        var result = $"HTTP {(int)res.StatusCode} {res.StatusCode}\n";
        if (includeTimestamp)
            result += $"Current UTC time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}\n";

        if (OperationStore.IsArmUrl(url) && (method == HttpMethod.Put || method == HttpMethod.Patch || method == HttpMethod.Post && (int)res.StatusCode == 202)
            && ToolExecutionContext.Current is { UserId: { } owner, SessionId: { } sessionId })
        {
            var operation = pendingOperation is not null
                ? OperationStore.Default.ObserveResponse(pendingOperation, res, responseBody)
                : OperationStore.Default.Register(owner, sessionId, method.Method, url, jsonBody, res, responseBody);
            return result + OperationStore.Envelope(operation);
        }

        if (maxResponseChars.HasValue && responseBody.Length > maxResponseChars.Value)
        {
            result += responseBody[..maxResponseChars.Value];
            result += $"\n\n[PARTIAL RESULT: showing first {maxResponseChars.Value / 1024}KB of {responseBody.Length / 1024}KB. Narrow or aggregate the query before making a complete-coverage claim.]";
        }
        else
        {
            result += responseBody;
        }

        return result;
    }

    /// <summary>
    /// Uses the longest Cost Management or standard Retry-After deadline, without
    /// clamping a service-specified wait. Missing cost headers default to 60 seconds;
    /// other requests use bounded exponential backoff with jitter.
    /// </summary>
    internal static double ResolveRetryAfterSeconds(HttpResponseMessage res, int attempt, bool costQuery = false)
    {
        var serviceWait = 0d;
        foreach (var header in res.Headers.Where(header =>
            (header.Key.StartsWith("x-ms-ratelimit-microsoft.costmanagement-", StringComparison.OrdinalIgnoreCase)
                && header.Key.EndsWith("-retry-after", StringComparison.OrdinalIgnoreCase))
            || header.Key.Equals("x-ms-ratelimit-microsoft.consumption-retry-after", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var value in header.Value)
                if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
                    && double.IsFinite(seconds) && seconds > serviceWait)
                    serviceWait = seconds;
        }

        var standard = res.Headers.RetryAfter?.Delta?.TotalSeconds
                    ?? res.Headers.RetryAfter?.Date?.Subtract(DateTimeOffset.UtcNow).TotalSeconds;
        var requestedWait = Math.Max(serviceWait, standard ?? 0);
        if (requestedWait > 0) return Math.Max(requestedWait, 1);
        if (costQuery) return 60;

        // Fallback: exponential backoff with small jitter to avoid lockstep retries.
        var backoff = Math.Pow(2, attempt + 1); // 2, 4, 8, 16, 32
        var jitter = Random.Shared.NextDouble(); // 0..1s
        return Math.Min(backoff + jitter, MaxRetryWaitSeconds);
    }

    /// <summary>
    /// Returns a standardized 401 error message when a token is missing.
    /// </summary>
    public static string TokenMissing(string tokenName, Activity? activity, string telemetryPrefix, string? graphTier = null)
    {
        activity?.SetTag($"{telemetryPrefix}.result", "not_connected");
        activity?.SetStatus(ActivityStatusCode.Error, $"{tokenName} not connected");
        var tiers = tokenName switch
        {
            "LogAnalyticsToken" => new[] { "loganalytics" },
            "StorageToken" => ["storage"],
            "GraphToken" => graphTier switch
            {
                null => ["licenses", "chargeback"],
                "licenses" => ["licenses"],
                "chargeback" => ["chargeback"],
                _ => throw new ArgumentOutOfRangeException(nameof(graphTier))
            },
            _ => ["base"]
        };
        return "HTTP 401 Unauthorized\n" + System.Text.Json.JsonSerializer.Serialize(new
        {
            error = new { code = "consent_required", resource = tokenName, message = "The required resource token is unavailable. Grant the matching delegated access below; an existing Azure connection need not be disconnected." },
            authActions = tiers.Select(tier => new { label = ConsentLabels[tier], href = "/auth/microsoft?tier=" + tier }),
            recovery = "For HTTP 403, check the user's resource RBAC instead; reconnecting does not grant Azure roles. Graph licenses and chargeback are separate use cases."
        });
    }

    private static readonly IReadOnlyDictionary<string, string> ConsentLabels = new Dictionary<string, string>
    {
        ["base"] = "Connect Azure",
        ["loganalytics"] = "Grant Log Analytics access",
        ["storage"] = "Grant Storage access",
        ["licenses"] = "Grant license reporting access",
        ["chargeback"] = "Grant cost allocation access"
    };

    internal static object[] ConsentActions(string? response)
    {
        if (response is null || !response.StartsWith("HTTP 401 ", StringComparison.Ordinal)) return [];
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(response[(response.IndexOf('\n') + 1)..]);
            if (document.RootElement.GetProperty("error").GetProperty("code").GetString() != "consent_required") return [];
            return document.RootElement.GetProperty("authActions").EnumerateArray().Select(action => action.GetProperty("href").GetString())
                .Select(href => ConsentLabels.FirstOrDefault(pair => href == "/auth/microsoft?tier=" + pair.Key))
                .Where(pair => pair.Key is not null).Select(pair => (object)new { label = pair.Value, href = "/auth/microsoft?tier=" + pair.Key }).ToArray();
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException) { return []; }
    }

    /// <summary>
    /// Centralised method-policy for all pass-through HTTP tools (Azure ARM, Microsoft Graph, etc.).
    /// Allows GET/POST/PUT/PATCH. Blocks DELETE at the code level — the user's RBAC role is the
    /// effective access boundary for everything else.
    /// Returns the parsed <see cref="HttpMethod"/>, or a ready-to-return error string when the
    /// method is rejected. Callers do: <c>var (m, err) = HttpHelper.ResolveMethod(...); if (err != null) return err;</c>
    /// </summary>
    public static (HttpMethod? Method, string? ErrorResponse) ResolveMethod(
        string? method,
        Activity? activity,
        string telemetryPrefix)
    {
        var normalized = (method ?? "GET").Trim().ToUpperInvariant();

        if (normalized == "DELETE")
        {
            activity?.SetTag($"{telemetryPrefix}.result", "blocked_delete");
            activity?.SetStatus(ActivityStatusCode.Error, "DELETE blocked");
            return (null, "HTTP 403 Forbidden\nThis agent does not perform DELETE operations. Generate a script via GenerateScript for the user to review and run themselves.");
        }

        var resolved = normalized switch
        {
            "GET" => HttpMethod.Get,
            "POST" => HttpMethod.Post,
            "PUT" => HttpMethod.Put,
            "PATCH" => HttpMethod.Patch,
            _ => null
        };

        if (resolved is null)
        {
            activity?.SetTag($"{telemetryPrefix}.result", "invalid_method");
            activity?.SetStatus(ActivityStatusCode.Error, "Invalid method");
            return (null, $"HTTP 400 BadRequest\nInvalid method: '{method}'. Allowed: GET, POST, PUT, PATCH.");
        }

        return (resolved, null);
    }
}
