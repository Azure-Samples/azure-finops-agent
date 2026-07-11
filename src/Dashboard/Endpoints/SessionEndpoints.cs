using System.Text.Json;
using AzureFinOps.Dashboard.AI;
using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Observability;

namespace AzureFinOps.Dashboard.Endpoints;

/// <summary>
/// Per-user chat session management — list past conversations, start a new
/// one, switch between them, delete. Backed by the Copilot SDK's on-disk
/// session store, scoped per Entra <c>oid</c> via <see cref="CopilotSessionFactory.GetWorkingDirectory"/>.
///
/// Anonymous (non-Entra) users get an ephemeral working dir so these endpoints
/// always return an empty list for them — multi-session is an Entra-only feature.
/// </summary>
public static class SessionEndpoints
{
    public static void MapSessionEndpoints(
        this IEndpointRouteBuilder app,
        CopilotSessionFactory copilotFactory,
        AiTelemetry telemetry,
        ILogger logger)
    {
        app.MapGet("/api/sessions", async (HttpContext ctx) =>
        {
            if (!TryResolveUser(ctx, out var userId, out _, out var entraOid))
                return Results.Unauthorized();

            // Anonymous users get a random userId per browser session, so they
            // can never re-find their old conversations after a refresh anyway.
            // Hide the sidebar entirely for them; multi-session is Entra-only.
            if (string.IsNullOrEmpty(entraOid))
                return Results.Ok(new { sessions = Array.Empty<object>(), currentSessionId = (string?)null });

            var sessions = await copilotFactory.ListUserSessionsAsync(userId, entraOid, ctx.RequestAborted);
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
            if (!TryResolveUser(ctx, out var userId, out var userLogin, out var entraOid))
                return Results.Unauthorized();

            var session = await copilotFactory.CreateNewAsync(userId, userLogin, entraOid);
            UserStateJanitor.LastSeenUtc[userId] = DateTimeOffset.UtcNow;
            return Results.Ok(new { sessionId = session.SessionId });
        });

        // Turn-activity probe: lets the frontend re-attach after a page refresh —
        // if a turn is still running it polls until done, then reloads the
        // transcript instead of leaving the user staring at dead air.
        app.MapGet("/api/sessions/{sessionId}/active", async (HttpContext ctx, string sessionId) =>
        {
            if (!TryResolveUser(ctx, out var userId, out _, out var entraOid))
                return Results.Unauthorized();
            if (!await copilotFactory.UserOwnsSessionAsync(userId, entraOid, sessionId, ctx.RequestAborted))
                return Results.NotFound();
            return Results.Ok(new { active = AzureFinOps.Dashboard.AI.ChatEndpoints.IsTurnActive(sessionId) });
        });

        app.MapPost("/api/sessions/{sessionId}/select", async (HttpContext ctx, string sessionId) =>
        {
            if (!TryResolveUser(ctx, out var userId, out _, out var entraOid))
                return Results.Unauthorized();
            // No-op for anonymous; they only have one ephemeral session.
            if (string.IsNullOrEmpty(entraOid)) return Results.NoContent();

            // IDOR guard: a sessionId is a public-ish string (it's emitted to the
            // browser and logged to App Insights). Reject any id that doesn't
            // belong to this user's workdir.
            if (!await copilotFactory.UserOwnsSessionAsync(userId, entraOid, sessionId, ctx.RequestAborted))
                return Results.NotFound();

            copilotFactory.SetCurrentSession(userId, sessionId);
            UserStateJanitor.LastSeenUtc[userId] = DateTimeOffset.UtcNow;
            logger.LogInformation("User {UserId} switched to session {SessionId}", userId, sessionId);
            return Results.NoContent();
        });

        app.MapDelete("/api/sessions/{sessionId}", async (HttpContext ctx, string sessionId) =>
        {
            if (!TryResolveUser(ctx, out var userId, out _, out var entraOid))
                return Results.Unauthorized();
            if (string.IsNullOrEmpty(entraOid)) return Results.NoContent();

            if (!await copilotFactory.UserOwnsSessionAsync(userId, entraOid, sessionId, ctx.RequestAborted))
                return Results.NotFound();

            await copilotFactory.DeleteUserSessionAsync(userId, sessionId, ctx.RequestAborted);
            logger.LogInformation("User {UserId} deleted session {SessionId}", userId, sessionId);
            return Results.NoContent();
        });

        // Replay endpoint: returns the persisted user/assistant/tool transcript
        // for a session so the frontend can rebuild the chat UI exactly as it
        // was when the user last left it.
        app.MapGet("/api/sessions/{sessionId}/messages", async (HttpContext ctx, string sessionId) =>
        {
            if (!TryResolveUser(ctx, out var userId, out _, out var entraOid))
                return Results.Unauthorized();
            if (string.IsNullOrEmpty(entraOid))
                return Results.Ok(new { messages = Array.Empty<object>() });

            // IDOR guard — see /select for context.
            if (!await copilotFactory.UserOwnsSessionAsync(userId, entraOid, sessionId, ctx.RequestAborted))
                return Results.NotFound();

            UserStateJanitor.LastSeenUtc[userId] = DateTimeOffset.UtcNow;

            // Read-only load — does NOT register this session as the user's
            // current and does NOT bump the ActiveSessions gauge. Just viewing
            // a past conversation must not switch the user's active thread.
            var events = await copilotFactory.LoadTranscriptAsync(sessionId, userId, entraOid, ctx.RequestAborted);

            // First pass: index tool execution results by ToolCallId so we
            // can attach result / success / error to each requested tool.
            var resultsById = new Dictionary<string, (string? Result, bool Success, string? Error)>();
            foreach (var evt in events)
            {
                if (evt is GitHub.Copilot.ToolExecutionCompleteEvent tec && tec.Data is { } d && !string.IsNullOrEmpty(d.ToolCallId))
                {
                    resultsById[d.ToolCallId] = (
                        d.Result?.DetailedContent ?? d.Result?.Content,
                        d.Success,
                        d.Error?.ToString()
                    );
                }
            }

            var messages = new List<object>();
            string? pendingAssistantText = null;
            var pendingTools = new List<object>();
            var pendingCharts = new List<string>();
            object? pendingHtml = null;
            object? pendingScript = null;

            void FlushAssistant()
            {
                if (pendingAssistantText is null && pendingTools.Count == 0
                    && pendingCharts.Count == 0 && pendingHtml is null && pendingScript is null) return;
                messages.Add(new
                {
                    role = "assistant",
                    content = pendingAssistantText ?? "",
                    toolCalls = pendingTools.ToArray(),
                    charts = pendingCharts.ToArray(),
                    html = pendingHtml,
                    script = pendingScript,
                });
                pendingAssistantText = null;
                pendingTools.Clear();
                pendingCharts.Clear();
                pendingHtml = null;
                pendingScript = null;
            }

            foreach (var evt in events)
            {
                if (evt is GitHub.Copilot.UserMessageEvent um)
                {
                    FlushAssistant();
                    var raw = um.Data?.Content ?? "";
                    var clean = StripContextPrefix(raw);
                    if (string.IsNullOrWhiteSpace(clean)) continue;
                    messages.Add(new { role = "user", content = clean });
                }
                else if (evt is GitHub.Copilot.AssistantMessageEvent am)
                {
                    var text = am.Data?.Content;
                    if (!string.IsNullOrEmpty(text))
                    {
                        pendingAssistantText = (pendingAssistantText is null ? "" : pendingAssistantText + "\n\n") + text;
                    }
                    if (am.Data?.ToolRequests is { Length: > 0 } reqs)
                    {
                        foreach (var r in reqs)
                        {
                            resultsById.TryGetValue(r.ToolCallId ?? "", out var ex);
                            pendingTools.Add(new
                            {
                                name = r.Name,
                                args = r.Arguments?.ToString() ?? "",
                                id = r.ToolCallId,
                                intent = r.IntentionSummary,
                                result = ex.Result,
                                success = ex.Result is null ? (bool?)null : ex.Success,
                                error = ex.Error,
                            });

                            // Mirror ChatEndpoints.HandleToolDoneAsync side-channel parsing
                            // so charts/scripts/decks survive a session resume.
                            if (ex.Success && ex.Result is { } rt)
                            {
                                if (r.Name == "RenderChart" || r.Name == "RenderAdvancedChart")
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
                                                pendingHtml = new { fileId = parts[0], fileName = parts[1], slideCount = parts.Length > 2 ? parts[2] : "" };
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
                                                var content = "";
                                                if (AzureFinOps.Dashboard.AI.Tools.ScriptTools.GeneratedFiles.TryGetValue(parts[0], out var entry))
                                                    content = entry.Content ?? "";
                                                pendingScript = new
                                                {
                                                    fileId = parts[0],
                                                    fileName = parts[1],
                                                    lineCount = parts[2],
                                                    language = parts[3],
                                                    description = parts.Length > 4 ? parts[4] : "",
                                                    content,
                                                };
                                            }
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            FlushAssistant();

            return Results.Ok(new { messages });
        });
    }

    private static string StripContextPrefix(string raw)
    {
        var s = raw?.Trim() ?? "";
        while (s.StartsWith('['))
        {
            var close = s.IndexOf(']');
            if (close < 0) return ""; // truncated context block — no user content follows
            s = s[(close + 1)..].TrimStart();
        }
        return s;
    }

    private static bool TryResolveUser(HttpContext ctx, out long userId, out string userLogin, out string? entraOid)
    {
        userId = 0;
        userLogin = "";
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
    /// Without this, the SDK-derived session summary surfaces our internal prompt
    /// scaffolding ("User IS connected to Azure...") instead of the user's real
    /// first question. Falls back to "Untitled conversation" if nothing remains.
    /// </summary>
    internal static string CleanSummary(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Untitled conversation";
        var s = raw.Trim();
        // Strip every leading [..] block (CONTEXT, UPLOADED FILES, etc.).
        while (s.StartsWith('['))
        {
            var close = s.IndexOf(']');
            if (close < 0)
            {
                // The SDK truncates long first prompts when deriving a summary,
                // which can cut a [CONTEXT: ...] block before its closing bracket.
                // Nothing after the '[' is user content — discard it entirely
                // instead of surfacing prompt scaffolding in the sidebar.
                s = "";
                break;
            }
            s = s[(close + 1)..].TrimStart();
        }
        // Take the first non-empty line so multi-line prompts surface cleanly.
        var firstLine = s.Split('\n', 2, StringSplitOptions.RemoveEmptyEntries)
                         .FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(firstLine)) return "Untitled conversation";
        return firstLine.Length > 80 ? firstLine[..80] + "…" : firstLine;
    }
}
