using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Observability;
using GitHub.Copilot.SDK;

namespace AzureFinOps.Dashboard.AI;

/// <summary>
/// SSE chat endpoint and session reset. Owns the streaming handler, structured
/// marker parsing (chart / html / script / maturity), and the per-request
/// telemetry span.
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
            var prompt = bodyDoc.RootElement.GetProperty("prompt").GetString();

            if (string.IsNullOrWhiteSpace(prompt))
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new { error = "prompt is required" });
                return;
            }

            var user = JsonSerializer.Deserialize<JsonElement>(userJson);
            var userId = user.GetProperty("id").GetInt64();
            var userLogin = user.TryGetProperty("login", out var loginProp) ? loginProp.GetString() : userId.ToString();

            // Track activity for the janitor's idle eviction.
            UserStateJanitor.LastSeenUtc[userId] = DateTimeOffset.UtcNow;

            var chatSw = Stopwatch.StartNew();
            telemetry.ChatRequests.Add(1,
                new KeyValuePair<string, object?>("model", copilotFactory.Deployment),
                new KeyValuePair<string, object?>("user", userLogin));

            using var chatActivity = telemetry.ActivitySource.StartActivity("ChatRequest");
            chatActivity?.SetTag("ai.user", userLogin);
            chatActivity?.SetTag("ai.model", copilotFactory.Deployment);
            chatActivity?.SetTag("ai.prompt_length", prompt!.Length);
            chatActivity?.SetTag("ai.prompt", prompt.Length > 500 ? prompt[..500] + "..." : prompt);
            logger.LogInformation("Chat request from {User} model={Model} promptLen={PromptLen}",
                userLogin, copilotFactory.Deployment, prompt.Length);

            var tokens = telemetry.UserTokens.GetOrAdd(userId, uid => new UserTokens { UserId = uid });
            await tokens.RefreshLock.WaitAsync(ctx.RequestAborted);
            try
            {
                tokens.AzureToken = await tokenStore.GetAzureTokenAsync(ctx, httpFactory);
                tokens.GraphToken = await tokenStore.GetGraphTokenAsync(ctx, httpFactory);
                tokens.LogAnalyticsToken = await tokenStore.GetLogAnalyticsTokenAsync(ctx, httpFactory);
                tokens.StorageToken = await tokenStore.GetStorageTokenAsync(ctx, httpFactory);
            }
            finally
            {
                tokens.RefreshLock.Release();
            }

            logger.LogInformation("Chat tokens: azure={HasAzure} graph={HasGraph} la={HasLA} storage={HasStorage}",
                tokens.AzureToken is not null, tokens.GraphToken is not null,
                tokens.LogAnalyticsToken is not null, tokens.StorageToken is not null);

            var connectedApis = new List<string>();
            if (tokens.AzureToken is not null) connectedApis.Add("Azure ARM (QueryAzure)");
            if (tokens.GraphToken is not null) connectedApis.Add("Microsoft Graph (QueryGraph)");
            if (tokens.LogAnalyticsToken is not null) connectedApis.Add("Log Analytics (QueryLogAnalytics)");
            if (tokens.StorageToken is not null) connectedApis.Add("Azure Storage (ListCostExportBlobs, ReadCostExportBlob)");
            var connectionContext = connectedApis.Count > 0
                ? $"[CONTEXT: User IS connected to Azure. Available APIs: {string.Join(", ", connectedApis)}. Proceed with tool calls directly.]"
                : "[CONTEXT: User is NOT connected to Azure. You can still answer any question that does NOT require their tenant-specific data — including public Azure information (regions, datacenters, services, pricing via RetailPrices, service health, general FinOps guidance), rendering charts/maps with public data, and explaining concepts. Use your built-in knowledge and public tools (RenderChart, RenderAdvancedChart, RetailPricing, GetAzureServiceHealth, web fetch) freely. Only ask the user to click 'Connect Azure' when the question genuinely requires their subscription/tenant data (their costs, their resources, their usage). Do NOT refuse public questions.]";

            // Surface any files the user has dropped into this session so the LLM
            // immediately knows the fileIds it can pass to QueryUploadedFile.
            var uploads = AzureFinOps.Dashboard.AI.Tools.UploadedFileTools.ListForUser(userId);
            string uploadsContext = "";
            if (uploads.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("[UPLOADED FILES IN THIS SESSION — the user dropped these in. Their question is almost certainly about them. Use QueryUploadedFile(fileId, mode, paramsJson) to drill in. The schema is already shown below — you usually do NOT need a separate 'preview' call. Jump straight to head / slice / filter / aggregate / text_range / json_path. Responses are capped at ~200 rows / ~8000 chars; issue more calls if needed.");
                foreach (var u in uploads)
                {
                    sb.Append($"\n  - fileId={u.FileId} name='{u.FileName}' kind={u.Kind} size={u.SizeBytes}B");
                    if (!string.IsNullOrEmpty(u.SchemaSummary))
                        sb.Append($"\n      schema: {u.SchemaSummary}");
                }
                sb.Append("]\n[ANSWER SHAPE FOR FILE ANALYSIS: (1) ONE-sentence headline naming the #1 waste with $ amount + concrete owner/RG/resource. (2) ONE visual — RenderChart (horizontal_bar of top-5 by $) when ≥3 data points, else a tight ≤5-row markdown table with an Owner column. NEVER both. NEVER long bullet lists of generic advice. (3) Optional 1-line takeaway. Then call SuggestFollowUp.]\n[FOLLOW-UP DIRECTIVE: After answering, you MUST call SuggestFollowUp. When the answer involved file analysis, prefer 2-3 distinct actions via the optional label2/prompt2 + label3/prompt3 parameters. Each action must propose a concrete next ACTION on these files (e.g. 'Rank top 5 prioritized actions across all files', 'Generate a cleanup script for the disks identified', 'Build a CFO deck from these uploads', 'Tag the untagged resources via PATCH'). Never propose a follow-up that just re-asks for analysis the user already saw.]");
                uploadsContext = sb.ToString();
            }
            prompt = string.IsNullOrEmpty(uploadsContext)
                ? $"{connectionContext}\n{prompt}"
                : $"{connectionContext}\n{uploadsContext}\n{prompt}";

            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.Connection = "keep-alive";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            try
            {
                var session = await copilotFactory.GetOrCreateSessionAsync(userId, userLogin!);

                var done = new TaskCompletionSource();
                var cancelled = false;
                var toolTracker = new ConcurrentDictionary<string, (string Name, DateTimeOffset StartTime, Activity? Activity)>();

                ctx.RequestAborted.Register(async () =>
                {
                    if (!cancelled)
                    {
                        cancelled = true;
                        try { await session.AbortAsync(); } catch { }
                        done.TrySetResult();
                    }
                });

                using var subscription = session.On(async (SessionEvent evt) =>
                {
                    if (cancelled) return;
                    try
                    {
                        await HandleSessionEventAsync(evt, ctx, toolTracker, telemetry, copilotFactory.Deployment,
                            userId, userLogin!, chatActivity, logger, done, () => cancelled = true);
                    }
                    catch
                    {
                        cancelled = true;
                        done.TrySetResult();
                    }
                });

                try
                {
                    await session.SendAsync(new MessageOptions { Prompt = prompt });
                }
                catch (Exception sendEx) when (sendEx.Message.Contains("Session not found", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("Copilot session expired for user {User}, recreating. Error: {Error}", userLogin, sendEx.Message);
                    chatActivity?.SetTag("ai.session_expired", true);
                    session = await copilotFactory.RecreateSessionAsync(userId, userLogin!);
                    await session.SendAsync(new MessageOptions { Prompt = prompt });
                }

                await done.Task;

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
        });

        app.MapPost("/api/chat/reset", async (HttpContext ctx) =>
        {
            var userJson = ctx.Session.GetString("user");
            if (userJson is null) { ctx.Response.StatusCode = 401; return; }

            var user = JsonSerializer.Deserialize<JsonElement>(userJson);
            var userId = user.GetProperty("id").GetInt64();

            if (telemetry.UserSessions.TryRemove(userId, out var oldSession))
            {
                telemetry.ActiveSessions.Add(-1);
                try { await oldSession.DisposeAsync(); } catch { }
            }

            AzureFinOps.Dashboard.AI.Tools.UploadedFileTools.ClearForUser(userId);

            logger.LogInformation("Copilot session cleared for user {UserId}", userId);
            ctx.Response.StatusCode = 204;
        });
    }

    private static async Task HandleSessionEventAsync(
        SessionEvent evt,
        HttpContext ctx,
        ConcurrentDictionary<string, (string Name, DateTimeOffset StartTime, Activity? Activity)> toolTracker,
        AiTelemetry telemetry,
        string deployment,
        long userId,
        string userLogin,
        Activity? chatActivity,
        ILogger logger,
        TaskCompletionSource done,
        Action markCancelled)
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
            if (telemetry.UserSessions.TryRemove(userId, out _))
                telemetry.ActiveSessions.Add(-1);
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
        // If a marker is detected we emit the tool_done event followed by the
        // structured event, then return null so the caller skips re-emit.
        if ((toolName == "RenderChart" || toolName == "RenderAdvancedChart") && toolDone.Data.Success && resultText is not null)
        {
            try
            {
                await EmitAsync(ctx, sseData);
                await EmitAsync(ctx, JsonSerializer.Serialize(new { type = "chart", options = resultText }));
                return null;
            }
            catch (Exception ex) when (IsClientDisconnect(ex)) { /* SSE client gone — nothing to do */ }
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
            catch (Exception ex) when (IsClientDisconnect(ex)) { /* SSE client gone */ }
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
            catch (Exception ex) when (IsClientDisconnect(ex)) { /* SSE client gone */ }
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
            catch (Exception ex) when (IsClientDisconnect(ex)) { /* SSE client gone */ }
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
            catch (Exception ex) when (IsClientDisconnect(ex)) { /* SSE client gone */ }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to emit __MATURITY_SCORE__ marker"); }
        }

        return sseData;
    }

    /// <summary>True when the exception indicates the SSE client closed the connection.</summary>
    private static bool IsClientDisconnect(Exception ex) =>
        ex is OperationCanceledException
        || ex is System.IO.IOException
        || ex is ObjectDisposedException;

    private static async Task EmitAsync(HttpContext ctx, string sseData)
    {
        await ctx.Response.WriteAsync($"data: {sseData}\n\n");
        await ctx.Response.Body.FlushAsync();
    }
}
