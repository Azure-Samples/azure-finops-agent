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
    // SocketsHttpHandler with PooledConnectionLifetime so a process running for weeks on App
    // Service eventually picks up DNS changes (the classic long-lived static HttpClient footgun).
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    { Timeout = TimeSpan.FromSeconds(60) };

    public static readonly ActivitySource Telemetry = new("AzureFinOps.AI");

    private static readonly Meter Meter = new("AzureFinOps.AI");
    private static readonly Counter<long> ThrottleRetries =
        Meter.CreateCounter<long>("finops.throttle.retries", description: "HTTP retries triggered by 429 or transient 5xx");

    /// <summary>
    /// Per-request hook for reporting 429 retries to the SSE stream. Set by ChatEndpoints
    /// before <c>session.SendAsync</c>; flows through AsyncLocal into every tool invocation
    /// on the same async context. Null in background workers / tests — calls become no-ops.
    /// </summary>
    public static readonly AsyncLocal<Func<int, double, Task>?> RetryReporter = new();

    /// <summary>
    /// Dev-only one-shot: when set to true at the start of a chat turn (gated on
    /// the env var <c>FINOPS_FORCE_THROTTLE_DEMO</c>), the very next call to
    /// <see cref="SendWithRetryAsync"/> simulates a single 429 + 4s wait + retry
    /// so the cool-down UI badge can be exercised end-to-end without depending
    /// on a real ARM throttle. Flag flips itself back to false after firing
    /// (one-shot). Never enabled in production.
    /// </summary>
    public static readonly AsyncLocal<bool> ForceThrottleNext = new();

    // Max retry attempts on HTTP 429. After this many failed attempts the throttled
    // response is returned to the caller so the LLM (or user) sees the throttle status.
    private const int MaxThrottleRetries = 5;

    // Cap on a single wait between retries (seconds). Honors Retry-After up to this ceiling
    // so a misbehaving service can't pin us indefinitely. Cost Management commonly returns
    // 30–60s; bigger waits are clamped to keep tool latency bounded.
    private const int MaxRetryWaitSeconds = 60;

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
        int? maxResponseChars = null)
    {
        method ??= HttpMethod.Get;

        // Dev-only one-shot synthetic throttle: fire a single fake cooling_down
        // event + brief wait before the real request, then clear the flag. Lets
        // us verify the SSE → UI badge wiring end-to-end without depending on
        // ARM actually rate-limiting us. See ForceThrottleNext.
        if (ForceThrottleNext.Value)
        {
            ForceThrottleNext.Value = false;
            const double demoWait = 4.0;
            ThrottleRetries.Add(1,
                new KeyValuePair<string, object?>("status", "429-demo"),
                new KeyValuePair<string, object?>("tool", telemetryPrefix));
            if (RetryReporter.Value is { } demoReport)
            {
                try { await demoReport(1, demoWait); } catch { }
            }
            await Task.Delay(TimeSpan.FromSeconds(demoWait));
        }

        HttpResponseMessage res = null!;
        for (var attempt = 0; attempt < MaxThrottleRetries; attempt++)
        {
            using var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("User-Agent", "FinOps-Dashboard/1.0");

            if (extraHeaders is not null)
                foreach (var (key, value) in extraHeaders)
                    req.Headers.TryAddWithoutValidation(key, value);

            if (jsonBody is not null)
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            res = await Http.SendAsync(req);

            // Retry on 429 (throttle) and transient 5xx (502/503/504 — typical ARM regional
            // failover or backend hiccups). Other non-success codes return to the caller.
            var status = (int)res.StatusCode;
            var isThrottle = status == 429;
            var isTransientServer = status == 502 || status == 503 || status == 504;
            if (!isThrottle && !isTransientServer) break;
            if (attempt == MaxThrottleRetries - 1) break; // last attempt — return as-is to caller

            var waitSeconds = ResolveRetryAfterSeconds(res, attempt);
            var reason = isThrottle ? "429" : status.ToString();
            activity?.SetTag($"{telemetryPrefix}.retry_{attempt}", $"{reason}, waiting {waitSeconds:F0}s");
            ThrottleRetries.Add(1,
                new KeyValuePair<string, object?>("status", reason),
                new KeyValuePair<string, object?>("tool", telemetryPrefix));
            if (RetryReporter.Value is { } report)
            {
                try { await report(attempt + 1, waitSeconds); } catch { /* UI hook is best-effort */ }
            }
            await Task.Delay(TimeSpan.FromSeconds(waitSeconds));
        }

        var responseBody = await res.Content.ReadAsStringAsync();

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
