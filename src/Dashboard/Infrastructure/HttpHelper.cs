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
    /// Per-turn hook for reporting 429/5xx retries to the SSE stream. Keyed by
    /// <c>userId:sessionId</c> (the "turn id") so it survives the JSON-RPC tool-callback
    /// boundary from the Copilot CLI (where AsyncLocal does NOT flow), AND so concurrent
    /// turns from the same user (two tabs, sidebar score racing chat) don't clobber each
    /// other's reporter. ChatEndpoints stamps the turn id into Activity Baggage as
    /// <c>finops.turn.id</c>; tools look it up via the current activity's baggage.
    /// Null lookup = no-op. Signature: (attemptNumber, waitSeconds, url, telemetryPrefix, statusCode).
    /// </summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Func<int, double, string, string, int, Task>> RetryReporters = new();

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

    // Max retry attempts on HTTP 429. Interactive Cost Management query/forecast
    // calls use a lower limit below because five exponential waits can pin one chat
    // turn for >30s even when the tenant throttle never clears.
    private const int MaxThrottleRetries = 5;
    private const int MaxInteractiveCostAttempts = 2;

    // Cap on a single wait between retries (seconds). Honors Retry-After up to this ceiling
    // so a misbehaving service can't pin us indefinitely. Cost Management commonly returns
    // 30–60s; bigger waits are clamped to keep tool latency bounded.
    private const int MaxRetryWaitSeconds = 60;

    // Cost Management / Consumption / Billing share an aggressive per-tenant throttle pool.
    // App Insights showed 87/89 (97.8%) of /query calls returning 429 inside a single 34s burst
    // — the LLM (esp. via BulkAzureRequest with parallelism=20) was fan-firing parallel queries
    // that all collided. This semaphore globally serializes those calls to a small concurrency,
    // turning a retry storm into ordered execution. Other ARM/Graph/LogAnalytics calls are
    // unaffected and still parallelize freely.
    private static readonly SemaphoreSlim CostMgmtGate = new(2, 2);
    private const int CostMgmtQueueNotifyMs = 250;

    private static bool IsCostManagementUrl(string url) =>
        url.Contains("/Microsoft.CostManagement/", StringComparison.OrdinalIgnoreCase)
        || url.Contains("/Microsoft.Consumption/", StringComparison.OrdinalIgnoreCase)
        || url.Contains("/Microsoft.Billing/", StringComparison.OrdinalIgnoreCase)
        || url.Contains("/Microsoft.CostManagementExports/", StringComparison.OrdinalIgnoreCase);

    private static bool IsInteractiveCostQueryUrl(string url) =>
        url.Contains("/Microsoft.CostManagement/query", StringComparison.OrdinalIgnoreCase)
        || url.Contains("/Microsoft.CostManagement/forecast", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Sends an HTTP request with silent retry on 429 (up to 5 attempts). On each 429 we honor,
    /// in priority order: Cost Management's <c>x-ms-ratelimit-microsoft.costmanagement-qpu-retry-after</c>,
    /// then the standard <c>Retry-After</c> header (delta or HTTP-date), then exponential backoff
    /// with jitter. After 5 failed attempts the 429 response is returned to the caller.
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
        int? maxAttemptsOverride = null)
    {
        method ??= HttpMethod.Get;

        var totalSw = Stopwatch.StartNew();
        var totalWaitSec = 0.0;
        var retryCount = 0;
        HttpResponseMessage res = null!;

        // Resolve the per-turn SSE reporter ONCE up front — used for queue waits,
        // in-flight heartbeats, and retry backoffs alike. Returns no-op when absent.
        var turnKey = Activity.Current?.GetBaggageItem("finops.turn.id");
        RetryReporters.TryGetValue(turnKey ?? "", out var report);
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
        var safeMetadataBypass = bypassCostManagementGate
            && method == HttpMethod.Get
            && !IsInteractiveCostQueryUrl(url);
        var isCostMgmt = IsCostManagementUrl(url) && !safeMetadataBypass;
        var heldGate = false;
        if (isCostMgmt)
        {
            if (!await CostMgmtGate.WaitAsync(CostMgmtQueueNotifyMs))
            {
                // Couldn't grab the gate immediately — surface a queue-wait event
                // and keep retrying every ~3s so the ghost row stays alive in the UI.
                var queuedSw = Stopwatch.StartNew();
                while (!await CostMgmtGate.WaitAsync(3000))
                {
                    Logger?.LogInformation("HTTP queued {Tool} waitedSec={Wait:F1} url={Url}",
                        telemetryPrefix, queuedSw.Elapsed.TotalSeconds, url);
                    if (report is not null)
                    {
                        try { await report(0, 5, url, telemetryPrefix + " (queued)", 0); }
                        catch (Exception emitEx) { Logger?.LogWarning(emitEx, "SSE queued emit failed for {Tool}", telemetryPrefix); }
                    }
                }
                // One final emit on acquire so the UI clears stale wait time.
                if (report is not null)
                {
                    try { await report(0, 1, url, telemetryPrefix + " (queued)", 0); }
                    catch { /* swallow */ }
                }
                activity?.SetTag($"{telemetryPrefix}.queued_sec", queuedSw.Elapsed.TotalSeconds);
            }
            heldGate = true;
        }

        try
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
                            try { await report(0, 6, url, telemetryPrefix + " (slow)", 0); }
                            catch (Exception emitEx) { Logger?.LogWarning(emitEx, "SSE slow emit failed for {Tool}", telemetryPrefix); }
                            await Task.Delay(5000, hbCts.Token);
                        }
                    }
                    catch (OperationCanceledException) { /* expected on response */ }
                }, hbCts.Token);

                try
                {
                    res = await Http.SendAsync(req);
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
                var isTransientServer = status == 502 || status == 503 || status == 504;
                if (!isThrottle && !isTransientServer) break;
                if (attempt == maxAttempts - 1) break; // last attempt — return as-is to caller

                var waitSeconds = ResolveRetryAfterSeconds(res, attempt);
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
                if (report is not null)
                {
                    try { await report(attempt + 1, waitSeconds, url, telemetryPrefix, status); }
                    catch (Exception emitEx) { Logger?.LogWarning(emitEx, "SSE cooling_down emit failed for {Tool}", telemetryPrefix); }
                }
                else
                {
                    Logger?.LogWarning("SSE cooling_down skipped — no reporter for turn={Turn} (tool={Tool})",
                        turnKey ?? "<none>", telemetryPrefix);
                }
                await Task.Delay(TimeSpan.FromSeconds(waitSeconds));
            }
        }
        finally
        {
            if (heldGate) CostMgmtGate.Release();
        }

        var responseBody = await res.Content.ReadAsStringAsync();
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

        // Trim chatty PUT/PATCH echoes — ARM returns the full resource (often 5–20KB) on success.
        // For bulk mutations this dominates LLM input tokens with no informational value.
        // Compact to a one-line {ok,status,name,id} summary; failures still return the full body
        // so the LLM can diagnose. Reads (GET) and query POSTs are untouched.
        if (res.IsSuccessStatusCode
            && (method == HttpMethod.Put || method == HttpMethod.Patch)
            && responseBody.Length > 256)
        {
            string? id = null, name = null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("id", out var idEl)) id = idEl.GetString();
                    if (doc.RootElement.TryGetProperty("name", out var nameEl)) name = nameEl.GetString();
                }
            }
            catch { /* not JSON or unexpected shape — fall through to full body */ }

            if (id is not null || name is not null)
            {
                result += $"{{\"ok\":true,\"status\":{(int)res.StatusCode},\"method\":\"{method.Method}\",\"name\":\"{name}\",\"id\":\"{id}\"}}";
                activity?.SetTag($"{telemetryPrefix}.response_trimmed", true);
                return result;
            }
        }

        if (maxResponseChars.HasValue && responseBody.Length > maxResponseChars.Value)
        {
            result += responseBody[..maxResponseChars.Value];
            result += $"\n\n[TRUNCATED — showing first {maxResponseChars.Value / 1024}KB of {responseBody.Length / 1024}KB. Use Python with pandas for full analysis.]";
        }
        else
        {
            result += responseBody;
        }

        return result;
    }

    /// <summary>
    /// Resolves how long to wait before the next retry on a 429 response. Priority:
    /// (1) Cost Management's QPU-specific header, (2) standard Retry-After (delta or HTTP-date),
    /// (3) exponential backoff with jitter (2s, 4s, 8s, 16s...). Result is clamped to
    /// [1, MaxRetryWaitSeconds].
    /// </summary>
    private static double ResolveRetryAfterSeconds(HttpResponseMessage res, int attempt)
    {
        // Cost Management exposes a service-specific retry header — prefer it when present.
        if (res.Headers.TryGetValues("x-ms-ratelimit-microsoft.costmanagement-qpu-retry-after", out var qpuValues)
            && double.TryParse(qpuValues.FirstOrDefault(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var qpuSeconds)
            && qpuSeconds > 0)
        {
            return Math.Min(Math.Max(qpuSeconds, 1), MaxRetryWaitSeconds);
        }

        // Standard Retry-After (seconds) or HTTP-date.
        var standard = res.Headers.RetryAfter?.Delta?.TotalSeconds
                    ?? res.Headers.RetryAfter?.Date?.Subtract(DateTimeOffset.UtcNow).TotalSeconds;
        if (standard is > 0)
        {
            return Math.Min(Math.Max(standard.Value, 1), MaxRetryWaitSeconds);
        }

        // Fallback: exponential backoff with small jitter to avoid lockstep retries.
        var backoff = Math.Pow(2, attempt + 1); // 2, 4, 8, 16, 32
        var jitter = Random.Shared.NextDouble(); // 0..1s
        return Math.Min(backoff + jitter, MaxRetryWaitSeconds);
    }

    /// <summary>
    /// Returns a standardized 401 error message when a token is missing.
    /// </summary>
    public static string TokenMissing(string tokenName, Activity? activity, string telemetryPrefix)
    {
        activity?.SetTag($"{telemetryPrefix}.result", "not_connected");
        activity?.SetStatus(ActivityStatusCode.Error, $"{tokenName} not connected");
        return $"HTTP 401 Unauthorized\n{tokenName} is null — the user must click 'Connect Azure' in the sidebar to authenticate, then retry.";
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
