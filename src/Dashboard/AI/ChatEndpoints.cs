using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Observability;
using GitHub.Copilot.SDK;

namespace AzureFinOps.Dashboard.AI;

/// <summary>
/// Stateless SSE chat endpoint. Each request creates a fresh one-shot Copilot
/// session, replays the browser-provided history as a composed prompt, streams
/// the assistant response, then disposes. No server-side conversation state.
///
/// Request body: <c>{ "prompt": "...", "history": [{ "role": "user"|"assistant", "content": "..." }] }</c>
/// </summary>
public static class ChatEndpoints
{
    public static void MapChatEndpoints(
        this IEndpointRouteBuilder app,
        CopilotSessionFactory copilotFactory,
        SessionTokenStore tokenStore,
        AiTelemetry telemetry,
        ILogger logger)
    {
        app.MapPost("/api/chat", async (HttpContext ctx, IHttpClientFactory httpFactory) =>
        {
            var userJson = ctx.Session.GetString("user");
            if (userJson is null)
            {
                ctx.Response.StatusCode = 401;
                return;
            }

            using var bodyDoc = await JsonDocument.ParseAsync(ctx.Request.Body);
            var prompt = bodyDoc.RootElement.TryGetProperty("prompt", out var pp) ? pp.GetString() : null;
            if (string.IsNullOrWhiteSpace(prompt))
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new { error = "prompt is required" });
                return;
            }

            // Browser-provided conversation history (oldest first). Each item is
            // { role: "user"|"assistant", content: string }. Tool calls / charts /
            // scripts are NOT replayed — the assistant text already summarises
            // what happened in previous turns from the user's perspective.
            var history = new List<(string Role, string Content)>();
            if (bodyDoc.RootElement.TryGetProperty("history", out var histProp) && histProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in histProp.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var role = item.TryGetProperty("role", out var rp) ? rp.GetString() : null;
                    var content = item.TryGetProperty("content", out var cp) ? cp.GetString() : null;
                    if (role is null || string.IsNullOrEmpty(content)) continue;
                    if (role != "user" && role != "assistant") continue;
                    history.Add((role, content));
                }
            }

            var user = JsonSerializer.Deserialize<JsonElement>(userJson);
            var userId = user.GetProperty("id").GetInt64();
            var userLogin = user.TryGetProperty("login", out var loginProp) ? loginProp.GetString() : userId.ToString();

            UserStateJanitor.LastSeenUtc[userId] = DateTimeOffset.UtcNow;

            var chatSw = Stopwatch.StartNew();
            telemetry.ChatRequests.Add(1,
                new KeyValuePair<string, object?>("model", copilotFactory.Deployment),
                new KeyValuePair<string, object?>("user", userLogin));

            using var chatActivity = telemetry.ActivitySource.StartActivity("ChatRequest");
            chatActivity?.SetTag("ai.user", userLogin);
            chatActivity?.SetTag("ai.model", copilotFactory.Deployment);
            chatActivity?.SetTag("ai.prompt_length", prompt!.Length);
            chatActivity?.SetTag("ai.history_turns", history.Count);
            chatActivity?.SetTag("ai.prompt", prompt.Length > 500 ? prompt[..500] + "..." : prompt);
            logger.LogInformation("Chat request from {User} model={Model} promptLen={PromptLen} historyTurns={Hist}",
                userLogin, copilotFactory.Deployment, prompt.Length, history.Count);

            var tokens = telemetry.UserTokens.GetOrAdd(userId, uid => new UserTokens { UserId = uid });
            // Kick off session creation IN PARALLEL with token refresh — both
            // are independent (tools close over `tokens` by reference, so they
            // see refreshed tokens regardless of when the session is built).
            // Awaited together below so the slowest leg sets the floor.
            var sessionCreateTask = copilotFactory.CreateOneShotAsync(userId);

            await tokens.RefreshLock.WaitAsync(ctx.RequestAborted);
            try
            {
                // Skip token refreshes for tiers the user never consented to —
                // otherwise every turn burns 1–60s on Entra round-trips that are
                // guaranteed to fail with 400 invalid_grant. ARM (base) is always
                // tried; the rest are opt-in via `graph_tier` in session.
                var graphTier = ctx.Session.GetString("graph_tier") ?? "";
                var tierSet = new HashSet<string>(
                    graphTier.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    StringComparer.OrdinalIgnoreCase);
                var wantGraph = tierSet.Contains("licenses") || tierSet.Contains("chargeback");
                var wantLogAnalytics = tierSet.Contains("loganalytics");
                var wantStorage = tierSet.Contains("storage");

                // Refresh in parallel so a single slow-Entra leg doesn't serialise the rest.
                var azureTask = tokenStore.GetAzureTokenAsync(ctx, httpFactory);
                var graphTask = wantGraph
                    ? tokenStore.GetGraphTokenAsync(ctx, httpFactory)
                    : Task.FromResult<string?>(null);
                var laTask = wantLogAnalytics
                    ? tokenStore.GetLogAnalyticsTokenAsync(ctx, httpFactory)
                    : Task.FromResult<string?>(null);
                var storageTask = wantStorage
                    ? tokenStore.GetStorageTokenAsync(ctx, httpFactory)
                    : Task.FromResult<string?>(null);
                await Task.WhenAll(azureTask, graphTask, laTask, storageTask);
                tokens.AzureToken = azureTask.Result;
                tokens.GraphToken = graphTask.Result;
                tokens.LogAnalyticsToken = laTask.Result;
                tokens.StorageToken = storageTask.Result;

                tokens.AzureTokenExpiry = ParseExpiry(ctx.Session.GetString("azure_token_expiry"));
                tokens.GraphTokenExpiry = ParseExpiry(ctx.Session.GetString("graph_token_expiry"));
                tokens.LogAnalyticsTokenExpiry = ParseExpiry(ctx.Session.GetString("loganalytics_token_expiry"));
                tokens.StorageTokenExpiry = ParseExpiry(ctx.Session.GetString("storage_token_expiry"));
            }
            catch (Exception tokenEx)
            {
                // Token refresh failure (e.g. login.microsoftonline.com unreachable,
                // refresh token rejected). Surface as a structured SSE error rather
                // than a 500 — the browser renders it inline in the chat instead of
                // dropping a blank assistant bubble.
                logger.LogError(tokenEx, "Token refresh failed for {User}", userLogin);
                ctx.Response.Headers.ContentType = "text/event-stream";
                ctx.Response.Headers.CacheControl = "no-cache";
                var msg = $"Token refresh failed: {tokenEx.Message}";
                var errPayload = JsonSerializer.Serialize(new { type = "error", message = msg });
                await ctx.Response.WriteAsync($"data: {errPayload}\n\n");
                await ctx.Response.WriteAsync("data: [DONE]\n\n");
                await ctx.Response.Body.FlushAsync();
                // Release the parallel session we started so it doesn't leak.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var leaked = await sessionCreateTask;
                        await leaked.DisposeAsync();
                        telemetry.ActiveSessions.Add(-1);
                    }
                    catch { }
                });
                return;
            }
            finally
            {
                tokens.RefreshLock.Release();
            }

            var connectedApis = new List<string>();
            if (tokens.AzureToken is not null) connectedApis.Add("Azure ARM (QueryAzure)");
            if (tokens.GraphToken is not null) connectedApis.Add("Microsoft Graph (QueryGraph)");
            if (tokens.LogAnalyticsToken is not null) connectedApis.Add("Log Analytics (QueryLogAnalytics)");
            if (tokens.StorageToken is not null) connectedApis.Add("Azure Storage (ListCostExportBlobs, ReadCostExportBlob)");
            var connectionContext = connectedApis.Count > 0
                ? $"[CONTEXT: User IS connected to Azure. Available APIs: {string.Join(", ", connectedApis)}. Proceed with tool calls directly.]"
                : "[CONTEXT: User is NOT connected to Azure. You can still answer any question that does NOT require their tenant-specific data — including public Azure information (regions, datacenters, services, pricing via RetailPrices, service health, general FinOps guidance), rendering charts/maps with public data, and explaining concepts. Use your built-in knowledge and public tools freely. Only ask the user to click 'Connect Azure' when the question genuinely requires their subscription/tenant data.]";

            var uploads = UploadedFileTools.ListForUser(userId);
            string uploadsContext = "";
            if (uploads.Count > 0)
            {
                var sb = new StringBuilder();
                sb.Append("[UPLOADED FILES IN THIS SESSION — the user dropped these in. Use QueryUploadedFile(fileId, mode, paramsJson) to drill in.");
                foreach (var u in uploads)
                {
                    sb.Append($"\n  - fileId={u.FileId} name='{u.FileName}' kind={u.Kind} size={u.SizeBytes}B");
                    if (!string.IsNullOrEmpty(u.SchemaSummary))
                        sb.Append($"\n      schema: {u.SchemaSummary}");
                }
                sb.Append("]");
                uploadsContext = sb.ToString();
            }

            // Compose: [CONTEXT] + [UPLOADS] + <conversation_history> + new prompt.
            var composed = new StringBuilder();
            composed.Append(connectionContext);
            if (uploadsContext.Length > 0) composed.Append('\n').Append(uploadsContext);
            if (history.Count > 0)
            {
                composed.Append("\n<conversation_history>\n");
                foreach (var (role, content) in history)
                {
                    composed.Append(role.ToUpperInvariant()).Append(": ").Append(content).Append("\n\n");
                }
                composed.Append("</conversation_history>\n");
            }
            composed.Append('\n').Append(prompt);
            var composedPrompt = composed.ToString();

            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.Connection = "keep-alive";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            CopilotSession? session = null;
            try
            {
                // Session creation was kicked off in parallel with token refresh
                // (see sessionCreateTask above) — await whichever finishes last.
                session = await sessionCreateTask;
                var done = new TaskCompletionSource();
                var cancelled = false;
                var toolTracker = new ConcurrentDictionary<string, (string Name, DateTimeOffset StartTime, Activity? Activity)>();

                ctx.RequestAborted.Register(() =>
                {
                    if (!cancelled)
                    {
                        cancelled = true;
                        done.TrySetResult();
                    }
                });

                using var subscription = session.On(async (SessionEvent evt) =>
                {
                    if (cancelled) return;
                    try
                    {
                        await HandleSessionEventAsync(evt, ctx, toolTracker, telemetry,
                            userLogin!, chatActivity, logger, done);
                    }
                    catch
                    {
                        cancelled = true;
                        done.TrySetResult();
                    }
                });

                var assistantBuf = new StringBuilder();
                var assistantBufLock = new object();
                using var assistantCapture = session.On(async (SessionEvent evt) =>
                {
                    if (evt is AssistantMessageDeltaEvent ad && !string.IsNullOrEmpty(ad.Data.DeltaContent))
                        lock (assistantBufLock) { assistantBuf.Append(ad.Data.DeltaContent); }
                    else if (evt is AssistantMessageEvent am && !string.IsNullOrWhiteSpace(am.Data.Content))
                        lock (assistantBufLock) { assistantBuf.Clear(); assistantBuf.Append(am.Data.Content); }
                    await Task.CompletedTask;
                });

                var sseLock = new SemaphoreSlim(1, 1);
                async Task SafeEmit(string sseData)
                {
                    await sseLock.WaitAsync();
                    try { await EmitAsync(ctx, sseData); }
                    finally { sseLock.Release(); }
                }
                Infrastructure.HttpHelper.RetryReporter.Value = (attempt, waitSec) =>
                    SafeEmit(JsonSerializer.Serialize(new { type = "cooling_down", attempt, waitSeconds = waitSec }));

                // Dev-only: opt into a one-shot synthetic 429 on the next HTTP
                // call so the cool-down UI badge can be exercised end-to-end.
                // Off unless FINOPS_FORCE_THROTTLE_DEMO=1 is set in the environment.
                if (string.Equals(
                        Environment.GetEnvironmentVariable("FINOPS_FORCE_THROTTLE_DEMO"),
                        "1",
                        StringComparison.Ordinal))
                {
                    Infrastructure.HttpHelper.ForceThrottleNext.Value = true;
                }

                await session.SendAsync(new MessageOptions { Prompt = composedPrompt });
                await done.Task;

                // Generate a short title for the conversation on the FIRST turn
                // (history is empty). Browser persists it in IndexedDB.
                string assistantReply;
                lock (assistantBufLock) { assistantReply = assistantBuf.ToString(); }
                if (!cancelled && history.Count == 0 && !string.IsNullOrWhiteSpace(assistantReply))
                {
                    using var titleCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
                    titleCts.CancelAfter(TimeSpan.FromSeconds(10));
                    try
                    {
                        var generated = await copilotFactory.GenerateTitleAsync(prompt, assistantReply, titleCts.Token);
                        if (!string.IsNullOrWhiteSpace(generated))
                        {
                            try
                            {
                                var payload = JsonSerializer.Serialize(new { type = "session_title", title = generated });
                                await ctx.Response.WriteAsync($"data: {payload}\n\n");
                                await ctx.Response.Body.FlushAsync();
                            }
                            catch { }
                        }
                    }
                    catch (OperationCanceledException) { }
                }

                chatSw.Stop();
                telemetry.ChatDuration.Record(chatSw.Elapsed.TotalMilliseconds,
                    new KeyValuePair<string, object?>("model", copilotFactory.Deployment),
                    new KeyValuePair<string, object?>("user", userLogin));
                chatActivity?.SetTag("ai.duration_ms", chatSw.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                chatSw.Stop();
                telemetry.ChatErrors.Add(1,
                    new KeyValuePair<string, object?>("model", copilotFactory.Deployment),
                    new KeyValuePair<string, object?>("user", userLogin),
                    new KeyValuePair<string, object?>("error_type", ex.GetType().Name));
                chatActivity?.SetTag("ai.error", ex.Message);
                chatActivity?.SetTag("ai.error_type", ex.GetType().Name);
                logger.LogError(ex, "Chat request failed for {User}", userLogin);
                var errorData = JsonSerializer.Serialize(new { type = "error", message = ex.Message });
                await ctx.Response.WriteAsync($"data: {errorData}\n\n");
                await ctx.Response.WriteAsync("data: [DONE]\n\n");
                await ctx.Response.Body.FlushAsync();
            }
            finally
            {
                if (session is not null)
                {
                    try { await session.DisposeAsync(); } catch { }
                    telemetry.ActiveSessions.Add(-1);
                }
            }
        });
    }

    private static async Task HandleSessionEventAsync(
        SessionEvent evt,
        HttpContext ctx,
        ConcurrentDictionary<string, (string Name, DateTimeOffset StartTime, Activity? Activity)> toolTracker,
        AiTelemetry telemetry,
        string userLogin,
        Activity? chatActivity,
        ILogger logger,
        TaskCompletionSource done)
    {
        string? sseData = null;

        if (evt is AssistantMessageDeltaEvent delta)
        {
            sseData = JsonSerializer.Serialize(new { type = "delta", content = delta.Data.DeltaContent });
        }
        else if (evt is AssistantMessageEvent msg)
        {
            sseData = JsonSerializer.Serialize(new { type = "message", content = msg.Data.Content });
        }
        else if (evt is ToolExecutionStartEvent toolStart)
        {
            var toolId = toolStart.Data.ToolCallId ?? Guid.NewGuid().ToString();
            telemetry.ToolCalls.Add(1,
                new KeyValuePair<string, object?>("tool", toolStart.Data.ToolName),
                new KeyValuePair<string, object?>("user", userLogin));
            var toolActivity = telemetry.ActivitySource.StartActivity($"Tool:{toolStart.Data.ToolName}");
            toolActivity?.SetTag("ai.tool.name", toolStart.Data.ToolName);
            toolActivity?.SetTag("ai.tool.id", toolId);
            toolTracker[toolId] = (toolStart.Data.ToolName, DateTimeOffset.UtcNow, toolActivity);
            string? argsJson = null;
            if (toolStart.Data.Arguments is not null)
            {
                try { argsJson = JsonSerializer.Serialize(toolStart.Data.Arguments); }
                catch (Exception serializeEx)
                {
                    logger.LogWarning(serializeEx, "Failed to serialise tool arguments for telemetry (tool={Tool})", toolStart.Data.ToolName);
                }
            }
            toolActivity?.SetTag("ai.tool.args", argsJson?.Length > 1000 ? argsJson[..1000] + "..." : argsJson);
            logger.LogInformation("Tool start: {Tool} id={ToolId}", toolStart.Data.ToolName, toolId);
            sseData = JsonSerializer.Serialize(new { type = "tool_start", tool = toolStart.Data.ToolName, id = toolId, args = argsJson });
        }
        else if (evt is ToolExecutionCompleteEvent toolDone)
        {
            sseData = await HandleToolDoneAsync(toolDone, ctx, toolTracker, telemetry, userLogin, logger);
        }
        else if (evt is SessionErrorEvent error)
        {
            sseData = JsonSerializer.Serialize(new { type = "error", message = error.Data.Message });
            logger.LogError("Session error for {User}: {Error}", userLogin, error.Data.Message);
            chatActivity?.SetTag("ai.error", error.Data.Message);
        }

        if (sseData is not null)
        {
            await ctx.Response.WriteAsync($"data: {sseData}\n\n");
            await ctx.Response.Body.FlushAsync();
        }

        if (evt is SessionIdleEvent || evt is SessionErrorEvent)
        {
            await ctx.Response.WriteAsync("data: [DONE]\n\n");
            await ctx.Response.Body.FlushAsync();
            done.TrySetResult();
        }
    }

    private static async Task<string?> HandleToolDoneAsync(
        ToolExecutionCompleteEvent toolDone,
        HttpContext ctx,
        ConcurrentDictionary<string, (string Name, DateTimeOffset StartTime, Activity? Activity)> toolTracker,
        AiTelemetry telemetry,
        string userLogin,
        ILogger logger)
    {
        var toolId = toolDone.Data.ToolCallId ?? "";
        var toolName = toolTracker.TryGetValue(toolId, out var info) ? info.Name : "unknown";
        var durationMs = toolTracker.TryGetValue(toolId, out var info2)
            ? (long)(DateTimeOffset.UtcNow - info2.StartTime).TotalMilliseconds : (long?)null;

        if (toolTracker.TryRemove(toolId, out var removed))
        {
            removed.Activity?.SetTag("ai.tool.success", toolDone.Data.Success);
            removed.Activity?.SetTag("ai.tool.durationMs", durationMs);
            if (toolDone.Data.Error?.Message is not null)
                removed.Activity?.SetTag("ai.tool.error", toolDone.Data.Error.Message);
            removed.Activity?.Dispose();
        }

        string? resultText = null;
        string? errorText = null;
        if (toolDone.Data.Result?.Content is not null) resultText = toolDone.Data.Result.Content;
        else if (toolDone.Data.Result?.DetailedContent is not null) resultText = toolDone.Data.Result.DetailedContent;
        if (toolDone.Data.Error?.Message is not null) errorText = toolDone.Data.Error.Message;

        if (!toolDone.Data.Success)
            telemetry.ToolErrors.Add(1,
                new KeyValuePair<string, object?>("tool", toolName),
                new KeyValuePair<string, object?>("user", userLogin));

        var sseData = JsonSerializer.Serialize(new { type = "tool_done", tool = toolName, id = toolId, success = toolDone.Data.Success, durationMs, result = resultText, error = errorText });
        logger.LogInformation("Tool done: {Tool} id={ToolId} success={Success} durationMs={Duration} resultLen={ResultLen}",
            toolName, toolId, toolDone.Data.Success, durationMs, resultText?.Length ?? 0);

        // Marker-based side channels (chart / html / script / maturity).
        if ((toolName == "RenderChart" || toolName == "RenderAdvancedChart") && toolDone.Data.Success && resultText is not null)
        {
            try
            {
                await EmitAsync(ctx, sseData);
                await EmitAsync(ctx, JsonSerializer.Serialize(new { type = "chart", options = resultText }));
                return null;
            }
            catch (Exception ex) when (IsClientDisconnect(ex)) { }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to emit chart marker for tool {Tool}", toolName); }
        }
        else if (toolDone.Data.Success && resultText is not null && resultText.Contains("__CHART__:"))
        {
            try
            {
                foreach (var line in resultText.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("__CHART__:"))
                    {
                        var chartJson = trimmed["__CHART__:".Length..].Trim();
                        await EmitAsync(ctx, sseData);
                        await EmitAsync(ctx, JsonSerializer.Serialize(new { type = "chart", options = chartJson }));
                        return null;
                    }
                }
            }
            catch (Exception ex) when (IsClientDisconnect(ex)) { }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to emit __CHART__ marker"); }
        }

        if (toolDone.Data.Success && resultText is not null && resultText.Contains("__HTML_READY__:"))
        {
            try
            {
                foreach (var line in resultText.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("__HTML_READY__:"))
                    {
                        var parts = trimmed["__HTML_READY__:".Length..].Split(':', 3);
                        if (parts.Length >= 2)
                        {
                            var htmlPayload = JsonSerializer.Serialize(new { type = "html_ready", fileId = parts[0], fileName = parts[1], slideCount = parts.Length > 2 ? parts[2] : "" });
                            await EmitAsync(ctx, sseData);
                            await EmitAsync(ctx, htmlPayload);
                            return null;
                        }
                        break;
                    }
                }
            }
            catch (Exception ex) when (IsClientDisconnect(ex)) { }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to emit __HTML_READY__ marker"); }
        }

        if (toolDone.Data.Success && resultText is not null && resultText.Contains("__SCRIPT_READY__:"))
        {
            try
            {
                foreach (var line in resultText.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("__SCRIPT_READY__:"))
                    {
                        var parts = trimmed["__SCRIPT_READY__:".Length..].Split(':', 5);
                        if (parts.Length >= 4)
                        {
                            var scriptFileId = parts[0];
                            var scriptContent = "";
                            if (ScriptTools.GeneratedFiles.TryGetValue(scriptFileId, out var scriptEntry))
                                scriptContent = scriptEntry.Content ?? "";
                            var scriptPayload = JsonSerializer.Serialize(new { type = "script_ready", fileId = parts[0], fileName = parts[1], lineCount = parts[2], language = parts[3], description = parts.Length > 4 ? parts[4] : "", content = scriptContent });
                            await EmitAsync(ctx, sseData);
                            await EmitAsync(ctx, scriptPayload);
                            return null;
                        }
                        break;
                    }
                }
            }
            catch (Exception ex) when (IsClientDisconnect(ex)) { }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to emit __SCRIPT_READY__ marker"); }
        }

        if (toolDone.Data.Success && resultText is not null && resultText.Contains("__MATURITY_SCORE__:"))
        {
            try
            {
                var trimmed = resultText.Trim();
                if (trimmed.StartsWith("__MATURITY_SCORE__:"))
                {
                    var rest = trimmed["__MATURITY_SCORE__:".Length..];
                    var colonIdx = rest.IndexOf(':');
                    if (colonIdx > 0)
                    {
                        var level = rest[..colonIdx];
                        var scoresJson = rest[(colonIdx + 1)..];
                        var scorePayload = JsonSerializer.Serialize(new { type = "maturity_score", level, scores = scoresJson });
                        await EmitAsync(ctx, sseData);
                        await EmitAsync(ctx, scorePayload);
                        return null;
                    }
                }
            }
            catch (Exception ex) when (IsClientDisconnect(ex)) { }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to emit __MATURITY_SCORE__ marker"); }
        }

        return sseData;
    }

    private static bool IsClientDisconnect(Exception ex) =>
        ex is OperationCanceledException
        || ex is System.IO.IOException
        || ex is ObjectDisposedException;

    private static async Task EmitAsync(HttpContext ctx, string sseData)
    {
        await ctx.Response.WriteAsync($"data: {sseData}\n\n");
        await ctx.Response.Body.FlushAsync();
    }

    private static DateTimeOffset? ParseExpiry(string? raw)
        => DateTimeOffset.TryParse(raw, out var v) ? v : (DateTimeOffset?)null;
}
