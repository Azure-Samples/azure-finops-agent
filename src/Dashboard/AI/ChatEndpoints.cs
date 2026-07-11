using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Observability;
using GitHub.Copilot;

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

            // === TIMING HOOKS ===
            // Per-phase stopwatch buffer flushed as SSE `timing` events once
            // the response headers are open. We can't emit before
            // ctx.Response.Headers are written (SSE preamble below), so we
            // buffer here and replay after the headers go out.
            var timingBuf = new List<(string Phase, double Ms, object? Extra)>();
            void RecordPhase(string phase, double ms, object? extra = null)
            {
                timingBuf.Add((phase, ms, extra));
                logger.LogInformation("timing phase={Phase} ms={Ms:F0} user={User}", phase, ms, userLogin);
            }

            var tokens = telemetry.UserTokens.GetOrAdd(userId, uid => new UserTokens { UserId = uid });
            // Fast path: anonymous user (no Entra OID) has nothing to refresh.
            // Skipping the lock + 4 fetches saves ~5-30ms per Pricing/Estimates
            // click and avoids touching the DNS-poisoned token endpoint at all.
            if (entraOid is null)
            {
                RecordPhase("token.skipped_anonymous", 0);
            }
            else
            {
            var tokenLockSw = Stopwatch.StartNew();
            await tokens.RefreshLock.WaitAsync(ctx.RequestAborted);
            tokenLockSw.Stop();
            RecordPhase("token.lock_wait", tokenLockSw.Elapsed.TotalMilliseconds);
            try
            {
                // Fan out all four token fetches in parallel — they hit different
                // Entra scopes and are independent. Each task wraps a try/catch so
                // a single scope failure (e.g. user never consented to Storage)
                // doesn't drag down the others. Saves ~1.2 s on warm turns.
                async Task<(string Name, string? Value, double Ms, string? Error)> Fetch(string name, Func<Task<string?>> fn)
                {
                    var sw = Stopwatch.StartNew();
                    try { var v = await fn(); sw.Stop(); return (name, v, sw.Elapsed.TotalMilliseconds, null); }
                    catch (OperationCanceledException) { sw.Stop(); throw; }
                    catch (HttpRequestException ex) { sw.Stop(); return (name, null, sw.Elapsed.TotalMilliseconds, ex.Message); }
                    catch (UnauthorizedAccessException ex) { sw.Stop(); return (name, null, sw.Elapsed.TotalMilliseconds, ex.Message); }
                    catch (InvalidOperationException ex) { sw.Stop(); return (name, null, sw.Elapsed.TotalMilliseconds, ex.Message); }
                }
                var fetchSw = Stopwatch.StartNew();
                var results = await Task.WhenAll(
                    Fetch("azure", () => tokenStore.GetAzureTokenAsync(ctx, httpFactory)),
                    Fetch("graph", () => tokenStore.GetGraphTokenAsync(ctx, httpFactory)),
                    Fetch("loganalytics", () => tokenStore.GetLogAnalyticsTokenAsync(ctx, httpFactory)),
                    Fetch("storage", () => tokenStore.GetStorageTokenAsync(ctx, httpFactory)));
                fetchSw.Stop();
                foreach (var r in results)
                {
                    RecordPhase($"token.{r.Name}", r.Ms, new { hit = r.Value is not null, error = r.Error });
                    if (r.Error is not null)
                        logger.LogWarning("Token fetch failed scope={Scope} ms={Ms:F0} err={Err}", r.Name, r.Ms, r.Error);
                }
                RecordPhase("token.parallel_total", fetchSw.Elapsed.TotalMilliseconds);
                tokens.AzureToken = results[0].Value;
                tokens.GraphToken = results[1].Value;
                tokens.LogAnalyticsToken = results[2].Value;
                tokens.StorageToken = results[3].Value;

                // Mirror expiry from session into the volatile bag so the
                // TenantTokenRefresher background service can refresh proactively
                // when no HTTP request is around (browser closed, background turn).
                tokens.AzureTokenExpiry = ParseExpiry(ctx.Session.GetString("azure_token_expiry"));
                tokens.GraphTokenExpiry = ParseExpiry(ctx.Session.GetString("graph_token_expiry"));
                tokens.LogAnalyticsTokenExpiry = ParseExpiry(ctx.Session.GetString("loganalytics_token_expiry"));
                tokens.StorageTokenExpiry = ParseExpiry(ctx.Session.GetString("storage_token_expiry"));
            }
            finally
            {
                tokens.RefreshLock.Release();
            }
            } // end if (entraOid is not null)

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

            // Declared outside the try so the finally block can deterministically
            // remove this exact turn's reporter (sweeping by userId prefix would
            // clobber a concurrent turn in another tab).
            string? turnKey = null;
            try
            {
                CopilotSession session;
                var sessionSw = Stopwatch.StartNew();
                string sessionAcquireMode;
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
                        sessionAcquireMode = "fallback_current";
                    }
                    else
                    {
                        session = await copilotFactory.GetOrResumeAsync(userId, requestedSessionId, userLogin!, entraOid);
                        sessionAcquireMode = "resume";
                    }
                }
                else
                {
                    session = await copilotFactory.GetCurrentOrCreateAsync(userId, userLogin!, entraOid);
                    sessionAcquireMode = "current_or_new";
                }
                sessionSw.Stop();
                RecordPhase($"session.{sessionAcquireMode}", sessionSw.Elapsed.TotalMilliseconds);
                var activeSessionId = session.SessionId;

                var done = new TaskCompletionSource();
                var cancelled = false;
                var toolTracker = new ConcurrentDictionary<string, (string Name, DateTimeOffset StartTime, Activity? Activity)>();
                var firstEventLogged = 0;

                // Browser disconnect releases this SSE handler but does NOT
                // abort the running turn. The Copilot CLI keeps generating
                // and persists the assistant message + tool results to the
                // on-disk session state ($COPILOT_HOME/.copilot/session-state).
                // The user can reload the conversation later and see the full
                // result via LoadTranscriptAsync. Without this detach, closing
                // the tab during a long "score my estate" run would silently
                // kill the work mid-flight.
                ctx.RequestAborted.Register(() =>
                {
                    if (!cancelled)
                    {
                        cancelled = true;
                        done.TrySetResult();
                    }
                });

                // SSE write lock + emit helper — declared up here so the
                // session.On callback below can use SafeEmit for the
                // sdk.first_event timing ping.
                using var sseLock = new SemaphoreSlim(1, 1);
                async Task SafeEmit(string sseData)
                {
                    await sseLock.WaitAsync();
                    try { await EmitAsync(ctx, sseData); }
                    finally { sseLock.Release(); }
                }
                // sdkSw measures time from subscription registration to the
                // first SDK event arrival (time-to-first-byte from model).
                // Started immediately before the subscription so a fast first
                // event can't be observed before the stopwatch is running
                // (which would log a misleading ms=0).
                var sdkSw = Stopwatch.StartNew();

                using var subscription = session.On(async (SessionEvent evt) =>
                {
                    if (cancelled) return;
                    if (System.Threading.Interlocked.Exchange(ref firstEventLogged, 1) == 0)
                    {
                        try
                        {
                            var firstMs = sdkSw.Elapsed.TotalMilliseconds;
                            logger.LogInformation("timing phase=sdk.first_event ms={Ms:F0} user={User}", firstMs, userLogin);
                            await SafeEmit(JsonSerializer.Serialize(new { type = "timing", phase = "sdk.first_event", ms = Math.Round(firstMs, 1), extra = new { evt = evt.GetType().Name } }));
                        }
                        catch (OperationCanceledException)
                        {
                            logger.LogDebug("First-event timing emit canceled for user={User}", userLogin);
                        }
                        catch (ObjectDisposedException)
                        {
                            logger.LogDebug("First-event timing emit skipped because stream was disposed for user={User}", userLogin);
                        }
                        catch (InvalidOperationException)
                        {
                            logger.LogDebug("First-event timing emit skipped due to invalid stream state for user={User}", userLogin);
                        }
                    }
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

                // Flush buffered timing phases (token refresh + session acquire)
                // now that the SSE stream is open. The frontend collects these
                // alongside its own perf marks to build the timing table.
                foreach (var t in timingBuf)
                {
                    var p = t.Extra is null
                        ? JsonSerializer.Serialize(new { type = "timing", phase = t.Phase, ms = Math.Round(t.Ms, 1) })
                        : JsonSerializer.Serialize(new { type = "timing", phase = t.Phase, ms = Math.Round(t.Ms, 1), extra = t.Extra });
                    await ctx.Response.WriteAsync($"data: {p}\n\n");
                }
                await ctx.Response.Body.FlushAsync();

                // Wire the retry hook so HttpHelper can push "Cooling down" pings
                // to this SSE stream during 429 backoff. The sseLock / SafeEmit
                // were declared above so the subscription callback can share them.
                // Register the SSE retry hook keyed by *turn id* (userId:sessionId)
                // — NOT just userId — so concurrent turns from the same user
                // (two tabs, sidebar score racing chat) don't clobber each
                // other's reporter. Propagated to all child activities (incl.
                // across the Copilot CLI JSON-RPC tool-callback boundary) via
                // Activity Baggage. Earlier we tried AsyncLocal and Activity.RootId
                // — both failed to flow through that boundary; baggage does.
                turnKey = $"{userId}:{activeSessionId}";
                chatActivity?.SetBaggage("finops.turn.id", turnKey);
                Infrastructure.HttpHelper.RetryReporters[turnKey] = (attempt, waitSec, url, tool, status) =>
                {
                    logger.LogInformation("EMIT cooling_down sse turn={Turn} attempt={Attempt} status={Status} tool={Tool} waitSec={Wait:F1}",
                        turnKey, attempt, status, tool, waitSec);
                    return SafeEmit(JsonSerializer.Serialize(new { type = "cooling_down", attempt, waitSeconds = waitSec, url, tool, status }));
                };
                // Belt-and-braces cleanup on request abort.
                var turnKeyForAbort = turnKey;
                ctx.RequestAborted.Register(() => Infrastructure.HttpHelper.RetryReporters.TryRemove(turnKeyForAbort, out _));

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
                        // Fire-and-forget: a fresh title is nice-to-have, not
                        // worth blocking the SSE close for. The next turn (or a
                        // sidebar refresh) will pick up the saved title via the
                        // session_title re-emit path above. Capture references
                        // so the background task is independent of the request.
                        var bgPrompt = prompt;
                        var bgReply = assistantReply;
                        var bgSessionId = activeSessionId;
                        // Race the title call against the SSE close so a fast title
                        // (~150ms p50) still gets pushed to the live stream. If it
                        // misses the window, the next turn's re-emit path picks it up.
                        var titleTask = Task.Run(async () =>
                        {
                            try
                            {
                                using var bgCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                                var generated = await copilotFactory.GenerateTitleAsync(bgPrompt, bgReply, bgCts.Token);
                                if (!string.IsNullOrWhiteSpace(generated))
                                    telemetry.SaveTitle(bgSessionId, generated);
                                return generated;
                            }
                            catch (OperationCanceledException bgEx)
                            {
                                logger.LogWarning(bgEx, "Background title generation timed out or was canceled for session {Sid}", bgSessionId);
                                return null;
                            }
                            catch (InvalidOperationException bgEx)
                            {
                                logger.LogWarning(bgEx, "Background title generation failed for session {Sid}", bgSessionId);
                                return null;
                            }
                        });
                        var winner = await Task.WhenAny(titleTask, Task.Delay(1500));
                        if (winner == titleTask && !ctx.RequestAborted.IsCancellationRequested)
                        {
                            var generated = await titleTask;
                            if (!string.IsNullOrWhiteSpace(generated))
                            {
                                try
                                {
                                    var p = JsonSerializer.Serialize(new { type = "session_title", id = activeSessionId, title = generated });
                                    await ctx.Response.WriteAsync($"data: {p}\n\n");
                                    await ctx.Response.Body.FlushAsync();
                                }
                                catch { /* client may have disconnected — title is still saved */ }
                            }
                        }
                    }
                }

                chatSw.Stop();
                telemetry.ChatDuration.Record(chatSw.Elapsed.TotalMilliseconds,
                    new KeyValuePair<string, object?>("model", copilotFactory.Deployment),
                    new KeyValuePair<string, object?>("user", userLogin));
                chatActivity?.SetTag("ai.duration_ms", chatSw.Elapsed.TotalMilliseconds);
                try
                {
                    var donePayload = JsonSerializer.Serialize(new { type = "timing", phase = "chat.total", ms = Math.Round(chatSw.Elapsed.TotalMilliseconds, 1) });
                    await ctx.Response.WriteAsync($"data: {donePayload}\n\n");
                    await ctx.Response.Body.FlushAsync();
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
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
                // Release this turn's reporter only — never sweep by userId
                // prefix, since a concurrent turn from the same user (two tabs,
                // sidebar score racing chat) holds its own key in the dict.
                if (turnKey is not null)
                    Infrastructure.HttpHelper.RetryReporters.TryRemove(turnKey, out _);
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
        else if (evt is AssistantReasoningDeltaEvent reasoningDelta)
        {
            // Live "thinking" feedback — reasoning models are silent for many
            // seconds while they reason; streaming the concise summary keeps the
            // UI from looking frozen (bare blinking cursor).
            if (!string.IsNullOrEmpty(reasoningDelta.Data.DeltaContent))
                sseData = JsonSerializer.Serialize(new { type = "reasoning", content = reasoningDelta.Data.DeltaContent });
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

    private static DateTimeOffset? ParseExpiry(string? raw)
        => DateTimeOffset.TryParse(raw, out var v) ? v : (DateTimeOffset?)null;
}
