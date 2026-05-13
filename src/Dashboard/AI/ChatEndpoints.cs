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
            string? requestedSessionId = null;
            if (bodyDoc.RootElement.TryGetProperty("sessionId", out var sidProp) && sidProp.ValueKind == JsonValueKind.String)
            {
                var sidStr = sidProp.GetString();
                if (!string.IsNullOrWhiteSpace(sidStr)) requestedSessionId = sidStr;
            }

            if (string.IsNullOrWhiteSpace(prompt))
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new { error = "prompt is required" });
                return;
            }

            var user = JsonSerializer.Deserialize<JsonElement>(userJson);
            var userId = user.GetProperty("id").GetInt64();
            var userLogin = user.TryGetProperty("login", out var loginProp) ? loginProp.GetString() : userId.ToString();

            // Entra-connected users get persistent per-oid session storage; anonymous
            // users get an ephemeral working dir that won't appear in any list.
            string? entraOid = null;
            var azureUserJson = ctx.Session.GetString("azure_user");
            if (azureUserJson is not null)
            {
                try
                {
                    var au = JsonSerializer.Deserialize<JsonElement>(azureUserJson);
                    if (au.TryGetProperty("objectId", out var oidProp))
                        entraOid = oidProp.GetString();
                }
                catch { /* ignore malformed session blob */ }
            }

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
                CopilotSession session;
                if (!string.IsNullOrEmpty(requestedSessionId))
                {
                    // IDOR guard: a requested sessionId must belong to this
                    // user's persistent workdir (Entra OID workdir or anon-userId workdir).
                    // If it doesn't (stale localStorage after a redeploy, or a forged id),
                    // silently fall through to the user's current/new session so the
                    // UX doesn't dead-end &#8212; we never resume someone else's session.
                    if (!await copilotFactory.UserOwnsSessionAsync(userId, entraOid, requestedSessionId, ctx.RequestAborted))
                    {
                        logger.LogInformation("Requested sessionId {Sid} not owned by user {Uid}; falling back to current session", requestedSessionId, userId);
                        session = await copilotFactory.GetCurrentOrCreateAsync(userId, userLogin!, entraOid);
                    }
                    else
                    {
                        session = await copilotFactory.GetOrResumeAsync(userId, requestedSessionId, userLogin!, entraOid);
                    }
                }
                else
                {
                    session = await copilotFactory.GetCurrentOrCreateAsync(userId, userLogin!, entraOid);
                }
                var activeSessionId = session.SessionId;

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
                            userId, userLogin!, activeSessionId, chatActivity, logger, done, () => cancelled = true);
                    }
                    catch
                    {
                        cancelled = true;
                        done.TrySetResult();
                    }
                });

                // Capture the assistant's full reply so we can generate a sidebar
                // title after the turn completes (CLI's title_changed event just
                // echoes the user prompt, which makes a poor summary). Replies
                // are streamed as deltas, so we accumulate them here.
                // StringBuilder is NOT thread-safe — the SDK may dispatch event
                // callbacks concurrently — so we guard every mutation/read.
                var assistantBuf = new System.Text.StringBuilder();
                var assistantBufLock = new object();
                using var assistantCapture = session.On(async (SessionEvent evt) =>
                {
                    if (evt is AssistantMessageDeltaEvent ad && !string.IsNullOrEmpty(ad.Data.DeltaContent))
                        lock (assistantBufLock) { assistantBuf.Append(ad.Data.DeltaContent); }
                    else if (evt is AssistantMessageEvent am && !string.IsNullOrWhiteSpace(am.Data.Content))
                        lock (assistantBufLock) { assistantBuf.Clear(); assistantBuf.Append(am.Data.Content); }
                    await Task.CompletedTask;
                });

                // Emit the active sessionId as the first SSE event so the frontend
                // can highlight it in the Conversations sidebar and include it in
                // subsequent requests.
                await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { type = "session", id = activeSessionId })}\n\n");
                await ctx.Response.Body.FlushAsync();

                try
                {
                    await session.SendAsync(new MessageOptions { Prompt = prompt });
                }
                catch (Exception sendEx) when (sendEx.Message.Contains("Session not found", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("Copilot session expired for user {User}, recycling. Error: {Error}", userLogin, sendEx.Message);
                    chatActivity?.SetTag("ai.session_expired", true);
                    session = await copilotFactory.RecycleSessionAsync(userId, activeSessionId, userLogin!, entraOid);
                    await session.SendAsync(new MessageOptions { Prompt = prompt });
                }

                await done.Task;

                // Race-fix: a previous turn's background title call may have
                // saved a fresh title AFTER its SSE stream closed. Always re-emit
                // the persisted title on the current open stream so the sidebar
                // catches up.
                if (!cancelled
                    && telemetry.SessionTitles.TryGetValue(activeSessionId, out var persistedTitle)
                    && !string.IsNullOrWhiteSpace(persistedTitle))
                {
                    try
                    {
                        var p = JsonSerializer.Serialize(new { type = "session_title", id = activeSessionId, title = persistedTitle });
                        await ctx.Response.WriteAsync($"data: {p}\n\n");
                        await ctx.Response.Body.FlushAsync();
                    }
                    catch { }
                }

                // After each turn, refresh the sidebar title via Azure OpenAI if
                // the current persisted title is missing or still equals the raw
                // user prompt. Cheap (one ~24-token completion) — we await it so
                // the SSE stream actually delivers the new title for THIS turn.
                string assistantReply;
                lock (assistantBufLock) { assistantReply = assistantBuf.ToString(); }
                logger.LogDebug("Title-gen check: cancelled={Cancelled} replyLen={Len} sessionId={Sid}",
                    cancelled, assistantReply.Length, activeSessionId);
                if (!cancelled && !string.IsNullOrWhiteSpace(assistantReply))
                {
                    var existing = telemetry.SessionTitles.TryGetValue(activeSessionId, out var t) ? t : null;
                    var promptClean = AzureFinOps.Dashboard.Endpoints.SessionEndpoints.CleanSummary(prompt);
                    var needsTitle = string.IsNullOrWhiteSpace(existing)
                        || existing.Equals(promptClean, StringComparison.OrdinalIgnoreCase)
                        || existing.StartsWith("Untitled", StringComparison.OrdinalIgnoreCase);
                    if (needsTitle)
                    {
                        // Bound the wait so a slow title call never holds the SSE
                        // stream open for more than ~10s.
                        using var titleCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
                        titleCts.CancelAfter(TimeSpan.FromSeconds(10));
                        try
                        {
                            var generated = await copilotFactory.GenerateTitleAsync(prompt, assistantReply, titleCts.Token);
                            if (!string.IsNullOrWhiteSpace(generated))
                            {
                                telemetry.SaveTitle(activeSessionId, generated);
                                try
                                {
                                    var payload = JsonSerializer.Serialize(new { type = "session_title", id = activeSessionId, title = generated });
                                    await ctx.Response.WriteAsync($"data: {payload}\n\n");
                                    await ctx.Response.Body.FlushAsync();
                                }
                                catch { /* client may have disconnected — title is still saved */ }
                            }
                        }
                        catch (OperationCanceledException) { /* timeout or client abort */ }
                    }
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
        });

        app.MapPost("/api/chat/reset", async (HttpContext ctx) =>
        {
            var userJson = ctx.Session.GetString("user");
            if (userJson is null) { ctx.Response.StatusCode = 401; return; }

            var user = JsonSerializer.Deserialize<JsonElement>(userJson);
            var userId = user.GetProperty("id").GetInt64();
            var userLogin = user.TryGetProperty("login", out var loginProp) ? loginProp.GetString() : userId.ToString();

            string? entraOid = null;
            var azureUserJson = ctx.Session.GetString("azure_user");
            if (azureUserJson is not null)
            {
                try
                {
                    var au = JsonSerializer.Deserialize<JsonElement>(azureUserJson);
                    if (au.TryGetProperty("objectId", out var oidProp))
                        entraOid = oidProp.GetString();
                }
                catch { }
            }

            // "Reset" semantics: start a brand-new conversation. The previous one
            // remains on disk and can be resumed via the Conversations sidebar.
            var fresh = await copilotFactory.CreateNewAsync(userId, userLogin!, entraOid);
            AzureFinOps.Dashboard.AI.Tools.UploadedFileTools.ClearForUser(userId);
            logger.LogInformation("Started new conversation for user {UserId} sessionId={SessionId}", userId, fresh.SessionId);
            await ctx.Response.WriteAsJsonAsync(new { sessionId = fresh.SessionId });
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
        string activeSessionId,
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
        else if (evt is SessionTitleChangedEvent titleEvt)
        {
            var newTitle = AzureFinOps.Dashboard.Endpoints.SessionEndpoints.CleanSummary(titleEvt.Data.Title);
            telemetry.SaveTitle(activeSessionId, newTitle);
            sseData = JsonSerializer.Serialize(new { type = "session_title", id = activeSessionId, title = newTitle });
        }
        else if (evt is SessionErrorEvent error)
        {
            sseData = JsonSerializer.Serialize(new { type = "error", message = error.Data.Message });
            logger.LogError("Session error for {User}: {Error}", userLogin, error.Data.Message);
            chatActivity?.SetTag("ai.error", error.Data.Message);
            if (telemetry.LiveSessions.TryRemove(activeSessionId, out var dead))
            {
                telemetry.ActiveSessions.Add(-1);
                try { await dead.Session.DisposeAsync(); } catch { }
            }
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
