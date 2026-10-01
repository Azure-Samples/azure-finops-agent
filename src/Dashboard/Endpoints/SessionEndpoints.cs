using System.Text.Json;
using AzureFinOps.Dashboard.AI;
using AzureFinOps.Dashboard.AI.Runtime;
using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Observability;
using AzureFinOps.Dashboard.Infrastructure;

namespace AzureFinOps.Dashboard.Endpoints;

/// <summary>
/// Per-user chat session management — list past conversations, start a new
/// one, switch between them, delete. Backed by the per-owner conversation
/// directories managed by <see cref="AgentSessionFactory"/>.
///
/// Anonymous (non-Entra) users get an ephemeral working dir so these endpoints
/// always return an empty list for them — multi-session is an Entra-only feature.
/// </summary>
public static class SessionEndpoints
{
    public static void MapSessionEndpoints(
        this IEndpointRouteBuilder app,
        AgentSessionFactory agentFactory,
        AiTelemetry telemetry,
        AzureFinOps.Dashboard.Jobs.JobStore jobStore,
        ILogger logger)
    {
        app.MapGet("/api/sessions", async (HttpContext ctx) =>
        {
            if (!TryResolveUser(ctx, out var userId, out _, out var entraTenantId, out var entraOid))
                return Results.Unauthorized();

            // Anonymous users get a random userId per browser session, so they
            // can never re-find their old conversations after a refresh anyway.
            // Hide the sidebar entirely for them; multi-session is Entra-only.
            if (string.IsNullOrEmpty(entraOid))
                return Results.Ok(new { sessions = Array.Empty<object>(), currentSessionId = (string?)null });

            var sessions = await agentFactory.ListUserSessionsAsync(
                userId, entraTenantId, entraOid, ctx.RequestAborted);
            telemetry.CurrentSessionId.TryGetValue(userId, out var currentId);
            var payload = sessions.Select(s => new
            {
                id = s.SessionId,
                summary = telemetry.SessionTitles.TryGetValue(s.SessionId, out var t) && !string.IsNullOrWhiteSpace(t)
                    ? CleanSummary(t)
                    : CleanSummary(s.Summary),
                modified = s.ModifiedTime,
                started = s.StartTime,
            });
            return Results.Ok(new { sessions = payload, currentSessionId = currentId });
        });

        app.MapPost("/api/sessions/new", async (HttpContext ctx) =>
        {
            if (!TryResolveUser(ctx, out var userId, out var userLogin, out var entraTenantId, out var entraOid))
                return Results.Unauthorized();

            var session = await agentFactory.CreateNewAsync(
                userId, userLogin, entraTenantId, entraOid);
            UserStateJanitor.LastSeenUtc[userId] = DateTimeOffset.UtcNow;
            return Results.Ok(new { sessionId = session.SessionId });
        });

        // Turn-activity probe: lets the frontend re-attach after a page refresh —
        // if a turn is still running it polls until done, then reloads the
        // transcript instead of leaving the user staring at dead air.
        app.MapGet("/api/sessions/{sessionId}/active", async (HttpContext ctx, string sessionId) =>
        {
            if (!TryResolveUser(ctx, out var userId, out _, out var entraTenantId, out var entraOid))
                return Results.Unauthorized();
            if (!await agentFactory.UserOwnsSessionAsync(
                userId, entraTenantId, entraOid, sessionId, ctx.RequestAborted))
                return Results.NotFound();
            return Results.Ok(AzureFinOps.Dashboard.AI.ChatEndpoints.ActiveTurnState(sessionId));
        });

        app.MapGet("/api/sessions/{sessionId}/outcomes", async (HttpContext ctx, string sessionId) =>
        {
            if (!TryResolveUser(ctx, out var userId, out _, out var entraTenantId, out var entraOid)) return Results.Unauthorized();
            if (!await agentFactory.UserOwnsSessionAsync(
                userId, entraTenantId, entraOid, sessionId, ctx.RequestAborted)) return Results.NotFound();
            return Results.Ok(new { outcomes = TurnOutcomeStore.Default.ForSession(userId, sessionId) });
        });

        app.MapPost("/api/sessions/{sessionId}/select", async (HttpContext ctx, string sessionId) =>
        {
            if (!TryResolveUser(ctx, out var userId, out _, out var entraTenantId, out var entraOid))
                return Results.Unauthorized();
            // No-op for anonymous; they only have one ephemeral session.
            if (string.IsNullOrEmpty(entraOid)) return Results.NoContent();

            // IDOR guard: a sessionId is a public-ish string (it's emitted to the
            // browser and logged to App Insights). Reject any id that doesn't
            // belong to this user's workdir.
            if (!await agentFactory.UserOwnsSessionAsync(
                userId, entraTenantId, entraOid, sessionId, ctx.RequestAborted))
                return Results.NotFound();

            agentFactory.SetCurrentSession(userId, sessionId);
            UserStateJanitor.LastSeenUtc[userId] = DateTimeOffset.UtcNow;
            logger.LogInformation("User {UserId} switched to session {SessionId}", userId, sessionId);
            return Results.NoContent();
        });

        app.MapDelete("/api/sessions/{sessionId}", async (HttpContext ctx, string sessionId) =>
        {
            if (!TryResolveUser(ctx, out var userId, out _, out var entraTenantId, out var entraOid))
                return Results.Unauthorized();
            if (string.IsNullOrEmpty(entraOid)) return Results.NoContent();

            if (!await agentFactory.UserOwnsSessionAsync(
                userId, entraTenantId, entraOid, sessionId, ctx.RequestAborted))
                return Results.NotFound();

            if (!ChatEndpoints.TryBeginTurn(sessionId, userId, null, out var deletionGate))
                return Results.Conflict(new
                {
                    code = "session_active",
                    error = "This conversation is still answering. Open it and press Stop, or wait for it to finish, then delete it.",
                });

            try
            {
                await agentFactory.DeleteUserSessionAsync(userId, entraTenantId, entraOid, sessionId, ctx.RequestAborted);
                // If this conversation was a job's run log, detach the job so it
                // behaves as "never ran" (next run creates a fresh session) instead
                // of pointing at a dead transcript.
                jobStore.DetachSession(sessionId);
                logger.LogInformation("User {UserId} deleted session {SessionId}", userId, sessionId);
                return Results.NoContent();
            }
            finally
            {
                await ChatEndpoints.EndTurnAsync(deletionGate, dispatchAttempted: false);
            }
        });

        // Replay endpoint: returns the persisted user/assistant/tool transcript
        // for a session so the frontend can rebuild the chat UI exactly as it
        // was when the user last left it.
        app.MapGet("/api/sessions/{sessionId}/messages", async (HttpContext ctx, string sessionId) =>
        {
            if (!TryResolveUser(ctx, out var userId, out _, out var entraTenantId, out var entraOid))
                return Results.Unauthorized();

            // NB: unlike GET /api/sessions (the sidebar list, which stays hidden
            // for anonymous users), transcript replay IS allowed for anonymous
            // users. It's how a backgrounded/minimized tab recovers the answer
            // the server persisted while the SSE was frozen or severed — the
            // client still holds the sessionId in memory even though anon convos
            // never appear in the sidebar and can't be re-found after a refresh.
            // The IDOR guard below scopes to the caller's anonymous or
            // tenant-and-object working directory and is the security boundary
            // for both anonymous and Entra callers.
            if (!await agentFactory.UserOwnsSessionAsync(
                userId, entraTenantId, entraOid, sessionId, ctx.RequestAborted))
                return Results.NotFound();

            UserStateJanitor.LastSeenUtc[userId] = DateTimeOffset.UtcNow;

            // Read-only load — does NOT register this session as the user's
            // current and does NOT bump the ActiveSessions gauge. Just viewing
            // a past conversation must not switch the user's active thread.
            IReadOnlyList<AgentEvent> events;
            try
            {
                events = await agentFactory.LoadTranscriptAsync(
                    sessionId, userId, entraTenantId, entraOid, ctx.RequestAborted);
            }
            catch (AgentSessionFactory.HistoryUnavailableException)
            {
                return Results.NotFound(new { code = "history_unavailable", error = "The retained conversation history is unavailable. Start a new conversation to continue." });
            }

            var messages = BuildTranscript(events, userId);
            // A pending approval is answered by the next user message, so only requests after the last one still wait.
            var lastUser = events.Select((item, index) => (item, index)).LastOrDefault(entry => entry.item is UserMessageEvent).index;
            var pendingChanges = events.Skip(lastUser).OfType<ApprovalRequestEvent>().Select(ChatEndpoints.PendingChange).ToArray();
            return Results.Ok(new { messages, pendingChanges });
        });
    }

    internal static IReadOnlyList<object> BuildTranscript(IReadOnlyList<AgentEvent> events, long userId)
    {
        // First pass: index tool results by call id so each started tool
        // gets its result / success / error.
        var resultsById = new Dictionary<string, (string? Result, bool Success, string? Error)>();
        foreach (var evt in events)
        {
            if (evt is ToolCompleteEvent done && !string.IsNullOrEmpty(done.CallId))
                resultsById[done.CallId] = (done.Result, done.Success, done.Error);
        }

        var messages = new List<object>();
        string? pendingAssistantText = null;
        var pendingThinking = new List<string>();
        var pendingTools = new List<object>();
        var pendingCharts = new List<string>();
        object? pendingHtml = null;
        object? pendingScript = null;
        var hasUserMessage = false;

        void FlushAssistant()
        {
            if (pendingAssistantText is null && pendingTools.Count == 0
                && pendingCharts.Count == 0 && pendingHtml is null && pendingScript is null)
            {
                pendingThinking.Clear();
                return;
            }
            messages.Add(new
            {
                role = "assistant",
                content = pendingAssistantText ?? "",
                thinking = pendingThinking.Count > 0 ? string.Join("\n\n", pendingThinking) : null,
                toolCalls = pendingTools.ToArray(),
                charts = pendingCharts.ToArray(),
                html = pendingHtml,
                script = pendingScript,
            });
            pendingAssistantText = null;
            pendingThinking.Clear();
            pendingTools.Clear();
            pendingCharts.Clear();
            pendingHtml = null;
            pendingScript = null;
        }

        foreach (var evt in events)
        {
            if (evt is UserMessageEvent um)
            {
                var raw = um.Content;
                if (IsInjectedUserContext(raw)) continue;
                FlushAssistant();
                var clean = StripContextPrefix(raw);
                if (string.IsNullOrWhiteSpace(clean)) continue;
                messages.Add(new { role = "user", content = clean });
                hasUserMessage = true;
            }
            else if (evt is AssistantMessageEvent am)
            {
                if (!string.IsNullOrEmpty(am.Content))
                    pendingAssistantText = (pendingAssistantText is null ? "" : pendingAssistantText + "\n\n") + am.Content;
            }
            else if (evt is ReasoningEvent thought)
            {
                if (!string.IsNullOrWhiteSpace(thought.Content)) pendingThinking.Add(thought.Content);
            }
            else if (evt is ToolStartEvent r)
            {
                resultsById.TryGetValue(r.CallId, out var ex);
                pendingTools.Add(new
                {
                    name = r.ToolName,
                    args = r.Arguments ?? "",
                    id = r.CallId,
                    intent = (string?)null,
                    result = ex.Result,
                    success = resultsById.ContainsKey(r.CallId) ? ex.Success : (bool?)null,
                    error = ex.Error,
                });

                // Mirror ChatEndpoints.HandleToolDoneAsync side-channel parsing
                // so charts/scripts/decks survive a session resume.
                if (ex.Success && ex.Result is { } rt)
                {
                    if (r.ToolName == "RenderChart" || r.ToolName == "RenderAdvancedChart")
                    {
                        pendingCharts.Add(rt);
                    }
                    else if (rt.Contains("__CHART__:"))
                    {
                        foreach (var line in rt.Split('\n'))
                        {
                            var t = line.Trim();
                            if (t.StartsWith("__CHART__:"))
                            {
                                pendingCharts.Add(t["__CHART__:".Length..].Trim());
                                break;
                            }
                        }
                    }
                    if (rt.Contains("__HTML_READY__:"))
                    {
                        foreach (var line in rt.Split('\n'))
                        {
                            var t = line.Trim();
                            if (t.StartsWith("__HTML_READY__:"))
                            {
                                var parts = t["__HTML_READY__:".Length..].Split(':', 3);
                                if (parts.Length >= 2)
                                    pendingHtml = new
                                    {
                                        fileId = parts[0],
                                        fileName = parts[1],
                                        slideCount = parts.Length > 2 ? parts[2] : "",
                                        // Artifacts live 30 min in-memory + on temp disk; after a
                                        // TTL sweep or restart the download link is dead — let the
                                        // UI render an \"expired\" state instead of a 404 link.
                                        expired = ArtifactStore.Default.Find(parts[0], userId) is null,
                                    };
                                break;
                            }
                        }
                    }
                    if (rt.Contains("__SCRIPT_READY__:"))
                    {
                        foreach (var line in rt.Split('\n'))
                        {
                            var t = line.Trim();
                            if (t.StartsWith("__SCRIPT_READY__:"))
                            {
                                var parts = t["__SCRIPT_READY__:".Length..].Split(':', 5);
                                if (parts.Length >= 4)
                                {
                                    var entry = ArtifactStore.Default.Find(parts[0], userId);
                                    var live = entry is not null;
                                    pendingScript = new
                                    {
                                        fileId = parts[0],
                                        fileName = parts[1],
                                        lineCount = parts[2],
                                        language = parts[3],
                                        description = parts.Length > 4 ? parts[4] : "",
                                        content = entry is not null ? File.ReadAllText(entry.Path) : "",
                                        // See __HTML_READY__ above — expired artifacts render a
                                        // \"regenerate\" hint instead of dead download/copy buttons.
                                        expired = !live,
                                    };
                                }
                                break;
                            }
                        }
                    }
                }
            }
            else if (evt is TurnErrorEvent error && hasUserMessage)
            {
                FlushAssistant();
                messages.Add(new
                {
                    role = "system",
                    content = error.Message,
                    terminalStatus = "error",
                });
            }
        }
        FlushAssistant();
        return messages;
    }

    private static string StripContextPrefix(string raw)
    {
        return StripLeadingBracketBlocks(raw);
    }

    /// <summary>The user's own words in a persisted prompt without host-injected context; null when nothing the user wrote remains.</summary>
    internal static string? VisibleUserText(string raw) =>
        IsInjectedUserContext(raw) || StripContextPrefix(raw) is not { } text || string.IsNullOrWhiteSpace(text) ? null : text;

    private static bool IsInjectedUserContext(string raw)
    {
        var s = raw.TrimStart();
        return s.StartsWith("<skill-context", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("<tool-context", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("<agent-context", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripLeadingBracketBlocks(string? raw)
    {
        var s = raw?.Trim() ?? "";
        while (s.StartsWith('['))
        {
            var depth = 0;
            var close = -1;
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] == '[') depth++;
                else if (s[i] == ']' && --depth == 0)
                {
                    close = i;
                    break;
                }
            }
            if (close < 0) return ""; // truncated context block — no user content follows
            s = s[(close + 1)..].TrimStart();
        }
        return s;
    }

    private static bool TryResolveUser(
        HttpContext ctx, out long userId, out string userLogin,
        out string? entraTenantId, out string? entraOid)
    {
        userId = 0;
        userLogin = "";
        entraTenantId = null;
        entraOid = null;

        var userJson = ctx.Session.GetString("user");
        if (userJson is null) return false;

        try
        {
            var user = JsonSerializer.Deserialize<JsonElement>(userJson);
            userId = user.GetProperty("id").GetInt64();
            userLogin = user.TryGetProperty("login", out var loginProp) ? (loginProp.GetString() ?? userId.ToString()) : userId.ToString();
        }
        catch { return false; }

        var azureUserJson = ctx.Session.GetString("azure_user");
        if (azureUserJson is not null)
        {
            try
            {
                var au = JsonSerializer.Deserialize<JsonElement>(azureUserJson);
                if (au.TryGetProperty("tenantId", out var tenantProp))
                    entraTenantId = tenantProp.GetString();
                if (au.TryGetProperty("objectId", out var oidProp))
                    entraOid = oidProp.GetString();
            }
            catch { /* ignore malformed */ }
        }

        return true;
    }

    /// <summary>
    /// Strips the injected <c>[CONTEXT: ...]</c> and <c>[UPLOADED FILES ...]</c>
    /// system prefixes that <c>ChatEndpoints</c> prepends to every user message.
    /// Without this, the stored conversation summary surfaces our internal prompt
    /// scaffolding ("User IS connected to Azure...") instead of the user's real
    /// first question. Falls back to "Untitled conversation" if nothing remains.
    /// </summary>
    internal static string CleanSummary(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Untitled conversation";
        // Balance nested schema arrays (for example columns=[A, B]) instead of
        // stopping at the first ']'. The latter leaked the remainder of the
        // [UPLOADED FILES] block into transcript rows and conversation titles.
        var s = StripLeadingBracketBlocks(raw);
        // Take the first non-empty line so multi-line prompts surface cleanly.
        var firstLine = s.Split('\n', 2, StringSplitOptions.RemoveEmptyEntries)
                         .FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(firstLine)) return "Untitled conversation";
        return firstLine.Length > 80 ? firstLine[..80] + "…" : firstLine;
    }
}
