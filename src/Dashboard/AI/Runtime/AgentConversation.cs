using System.ClientModel;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AzureFinOps.Dashboard.Infrastructure;

namespace AzureFinOps.Dashboard.AI.Runtime;

/// <summary>
/// One owner-bound chat conversation. The model context is the serialized
/// <see cref="AgentSession"/> (a chained Responses ID that only advances when a
/// turn completes, so a stopped turn never leaves unanswered tool calls). The UI
/// transcript and listing metadata live in the owner's directory. A turn runs in
/// the background so it survives browser disconnects, and every event is fanned
/// out to subscribers in order.
/// </summary>
public sealed class AgentConversation : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly AgentSessionFactory _factory;
    private readonly string _metaPath;
    private readonly string _eventsPath;
    private readonly object _handlersLock = new();
    private readonly List<Func<AgentEvent, Task>> _handlers = [];
    private readonly SemaphoreSlim _publish = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _startedCalls = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _completedCalls = new(StringComparer.Ordinal);
    private Meta _meta;
    private CancellationTokenSource? _run;
    private Task _runTask = Task.CompletedTask;

    public string SessionId { get; }
    public long UserId { get; }
    public string WorkingDirectory { get; }
    public bool IsRunning => !_runTask.IsCompleted;

    private AgentConversation(AgentSessionFactory factory, long userId, string workingDirectory, string sessionId, Meta meta)
    {
        _factory = factory;
        UserId = userId;
        WorkingDirectory = workingDirectory;
        SessionId = sessionId;
        var directory = SessionDirectory(workingDirectory, sessionId);
        _metaPath = Path.Combine(directory, "session.json");
        _eventsPath = Path.Combine(directory, "events.jsonl");
        _meta = meta;
    }

    internal static string SessionDirectory(string workingDirectory, string sessionId) =>
        Path.Combine(workingDirectory, "sessions", sessionId);

    internal static bool IsValidId(string sessionId) => Guid.TryParseExact(sessionId, "D", out _);

    internal static AgentConversation Create(AgentSessionFactory factory, long userId, string workingDirectory)
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var now = DateTimeOffset.UtcNow;
        var conversation = new AgentConversation(factory, userId, workingDirectory, sessionId, new Meta { Created = now, Modified = now });
        Directory.CreateDirectory(SessionDirectory(workingDirectory, sessionId));
        conversation.SaveMeta();
        return conversation;
    }

    internal static AgentConversation? Open(AgentSessionFactory factory, long userId, string workingDirectory, string sessionId)
    {
        var meta = ReadMeta(workingDirectory, sessionId);
        return meta is null ? null : new AgentConversation(factory, userId, workingDirectory, sessionId, meta);
    }

    internal static AgentSessionInfo? Describe(string workingDirectory, string sessionId)
    {
        var meta = ReadMeta(workingDirectory, sessionId);
        return meta is null ? null : new AgentSessionInfo(sessionId, meta.Created, meta.Modified, meta.Summary, workingDirectory);
    }

    private static Meta? ReadMeta(string workingDirectory, string sessionId)
    {
        if (!IsValidId(sessionId)) return null;
        var path = Path.Combine(SessionDirectory(workingDirectory, sessionId), "session.json");
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Meta>(File.ReadAllText(path), Json) : null; }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public IDisposable On(Func<AgentEvent, Task> handler)
    {
        lock (_handlersLock) _handlers.Add(handler);
        return new Subscription(this, handler);
    }

    /// <summary>Starts a turn in the background. Events flow to <see cref="On"/> subscribers
    /// and the turn always ends with exactly one <see cref="TurnIdleEvent"/> or <see cref="TurnErrorEvent"/>.
    /// Every change still awaiting approval is answered: approved only when <paramref name="approval"/> approves it, otherwise rejected.</summary>
    public async Task SendAsync(string prompt, IReadOnlyList<ImageAttachment>? images = null, bool lightweight = false, ApprovalDecision? approval = null)
    {
        if (IsRunning) throw new InvalidOperationException("A turn is already running in this conversation.");
        var run = new CancellationTokenSource();
        _run = run;
        _startedCalls.Clear();
        _completedCalls.Clear();
        string? recap = null;
        if (CompactsNextTurn)
        {
            recap = Recap(await GetEventsAsync(), _meta.Attachments);
            System.Diagnostics.Activity.Current?.AddEvent(new System.Diagnostics.ActivityEvent("finops.context.compacted",
                tags: new System.Diagnostics.ActivityTagsCollection { ["finops.context.input_tokens"] = _meta.ContextTokens }));
        }
        await PublishAsync(new UserMessageEvent(prompt));
        if (images is { Count: > 0 })
        {
            _meta.Attachments = [.. (_meta.Attachments ?? []).Concat(images.Select(image => image.DisplayName)).TakeLast(20)];
            SaveMeta();
        }
        if (string.IsNullOrWhiteSpace(_meta.Summary))
        {
            _meta.Summary = prompt.Length > 400 ? prompt[..400] : prompt;
            SaveMeta();
        }
        _runTask = Task.Run(() => RunAsync(prompt, recap, images, lightweight, approval, run.Token));
    }

    /// <summary>
    /// Model input, in tokens, above which the next turn starts a fresh model context. The chained context keeps every
    /// earlier tool result; in production, calls above about 180k input tokens waited 10-17 s for their first token
    /// (about 1 s below 100k) even when nearly all input was cached, and one turn can add 50k tokens of tool results.
    /// </summary>
    internal const long CompactAfterInputTokens = 100_000;

    /// <summary>Whether the next turn starts without the chained model context, so per-session context such as the connection context must be sent again.</summary>
    internal bool StartsFreshContext => _meta.AgentSession is null || CompactsNextTurn;

    private bool CompactsNextTurn =>
        ShouldCompact(_meta.ContextTokens, _meta.AgentSession is not null, _meta.PendingApprovals is { Count: > 0 });

    /// <summary>A pending approval must be answered in the context that proposed it, so it postpones compaction.</summary>
    internal static bool ShouldCompact(long? contextTokens, bool hasModelContext, bool hasPendingApprovals) =>
        hasModelContext && !hasPendingApprovals && contextTokens > CompactAfterInputTokens;

    internal const int RecapCharacters = 24_000;
    internal const int RecapMessageCharacters = 4_000;
    internal const int RecapScriptCharacters = 6_000;

    /// <summary>
    /// The visible exchanges a compacted turn carries into its fresh model context: user questions without host-injected
    /// context and the assistant's answers, newest kept first within <see cref="RecapCharacters"/>, followed by the files
    /// earlier turns produced or attached, with the latest generated script's code. Tool results are left out.
    /// </summary>
    internal static string? Recap(IReadOnlyList<AgentEvent> events, IReadOnlyList<string>? attachments = null)
    {
        var entries = new List<(bool User, string Text)>();
        foreach (var item in events)
        {
            if (item is UserMessageEvent user && Endpoints.SessionEndpoints.VisibleUserText(user.Content) is { } question)
                entries.Add((true, question));
            else if (item is AssistantMessageEvent answer && answer.Content.Trim() is { Length: > 0 } text)
            {
                if (entries.Count > 0 && !entries[^1].User) entries[^1] = (false, entries[^1].Text + "\n\n" + text);
                else entries.Add((false, text));
            }
        }
        var kept = new List<string>();
        var used = 0;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var text = entries[i].Text.Length > RecapMessageCharacters
                ? entries[i].Text[..RecapMessageCharacters] + " …[truncated]"
                : entries[i].Text;
            var entry = (entries[i].User ? "User: " : "Assistant: ") + text;
            if (used + entry.Length > RecapCharacters) break;
            kept.Add(entry);
            used += entry.Length;
        }
        var files = RecapFiles(events, attachments);
        if (kept.Count == 0 && files is null) return null;
        kept.Reverse();
        var omitted = entries.Count - kept.Count;
        return "[CONVERSATION SO FAR: the earlier model context was reset to keep answers fast, so earlier tool results are no longer available. " +
               "These are the messages already exchanged; use them for continuity and query again whenever the new message needs figures, rows or identifiers they do not show.]\n" +
               (omitted > 0 ? $"({omitted} earlier messages omitted)\n\n" : "") +
               string.Join("\n\n", kept) +
               (files is null ? "" : "\n\n" + files) +
               "\n[END OF CONVERSATION SO FAR. The user's new message follows.]";
    }

    /// <summary>Generated files and attached images from earlier turns, with the code of the latest generated script.</summary>
    private static string? RecapFiles(IReadOnlyList<AgentEvent> events, IReadOnlyList<string>? attachments)
    {
        var starts = new Dictionary<string, ToolStartEvent>(StringComparer.Ordinal);
        foreach (var start in events.OfType<ToolStartEvent>()) starts[start.CallId] = start;
        var lines = new List<string>();
        (string Name, string Language, string Code)? latest = null;
        foreach (var done in events.OfType<ToolCompleteEvent>().Where(done => done.Success && done.Result is not null))
            foreach (var line in done.Result!.Split('\n').Select(line => line.Trim()))
            {
                if (line.StartsWith("__SCRIPT_READY__:", StringComparison.Ordinal) && line["__SCRIPT_READY__:".Length..].Split(':', 5) is { Length: >= 4 } script)
                {
                    lines.Add($"- {script[1]} ({script[3]} script, {script[2]} lines)");
                    if (starts.TryGetValue(done.CallId, out var call) && ScriptCode(call.Arguments) is { } code) latest = (script[1], script[3], code);
                }
                else if (line.StartsWith("__HTML_READY__:", StringComparison.Ordinal) && line["__HTML_READY__:".Length..].Split(':', 3) is { Length: >= 2 } file)
                    lines.Add($"- {file[1]}");
            }
        foreach (var name in attachments ?? [])
            lines.Add($"- {name}, an image the user attached (no longer visible to you: ask the user to attach it again if you need it)");
        if (lines.Count == 0) return null;
        var text = "Files from earlier turns (the user can still download generated files for 24 hours; you cannot open them):\n" + string.Join('\n', lines.TakeLast(20));
        if (latest is { } last)
            text += $"\nCode of the latest script, {last.Name}:\n```{last.Language}\n" +
                    (last.Code.Length > RecapScriptCharacters ? last.Code[..RecapScriptCharacters] + "\n…[truncated]" : last.Code) + "\n```";
        return text;

        static string? ScriptCode(string? arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments)) return null;
            try
            {
                using var document = JsonDocument.Parse(arguments);
                return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("scriptContent", out var code)
                    && code.ValueKind == JsonValueKind.String ? code.GetString() : null;
            }
            catch (JsonException) { return null; }
        }
    }

    /// <summary>Whether <paramref name="requestId"/> is a change this conversation is still waiting for the user to approve.</summary>
    internal bool HasPendingApproval(string requestId) =>
        requestId != _meta.InterruptedApproval && PendingApprovals().Any(request => request.RequestId == requestId);

    private List<ToolApprovalRequestContent> PendingApprovals() =>
        [.. (_meta.PendingApprovals ?? []).Select(item => item.Deserialize<AIContent>(AIJsonUtilities.DefaultOptions)).OfType<ToolApprovalRequestContent>()];

    public async Task AbortAsync(CancellationToken cancellationToken = default)
    {
        _run?.Cancel();
        await _runTask.WaitAsync(cancellationToken);
    }

    /// <summary>Starts a fresh model context while keeping the visible transcript.</summary>
    public void Compact()
    {
        if (IsRunning) throw new InvalidOperationException("A turn is running in this conversation.");
        _meta.AgentSession = null;
        _meta.ContextTokens = null;
        _meta.PendingApprovals = null;
        _meta.InterruptedApproval = null;
        SaveMeta();
    }

    public async Task<IReadOnlyList<AgentEvent>> GetEventsAsync(CancellationToken cancellationToken = default)
    {
        var events = new List<AgentEvent>();
        if (!File.Exists(_eventsPath)) return events;
        await _publish.WaitAsync(cancellationToken);
        try
        {
            foreach (var line in await File.ReadAllLinesAsync(_eventsPath, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { if (JsonSerializer.Deserialize<AgentEvent>(line, Json) is { } item) events.Add(item); }
                catch (JsonException) { }
            }
        }
        finally { _publish.Release(); }
        return events;
    }

    internal Task ToolStartedAsync(string callId, string toolName, string? arguments) =>
        _startedCalls.TryAdd(callId, 0) ? PublishAsync(new ToolStartEvent(callId, toolName, arguments)) : Task.CompletedTask;

    internal Task ToolCompletedAsync(string callId, bool success, string? result, string? error) =>
        _completedCalls.TryAdd(callId, 0)
            ? PublishAsync(new ToolCompleteEvent(callId, success, result, error,
                ToolExecutionContext.Current?.ToolDurations.TryGetValue(callId, out var ms) == true ? ms : null))
            : Task.CompletedTask;

    // A hosted web_search item starts before the service fills in its action, so the query, opened page
    // or in-page find is known only from the completed item carried by the result.
#pragma warning disable OPENAI001, CS0618 // Experimental Responses item types; Query is the older single-query field.
    internal static string WebSearchOutcome(object? completedItem) =>
        (completedItem as OpenAI.Responses.WebSearchCallResponseItem)?.Action switch
        {
            OpenAI.Responses.WebSearchSearchAction { Queries.Count: > 0 } search =>
                $"Web search completed: searched {JsonSerializer.Serialize(search.Queries)}.",
            OpenAI.Responses.WebSearchSearchAction { Query.Length: > 0 } search =>
                $"Web search completed: searched {JsonSerializer.Serialize(new[] { search.Query })}.",
            OpenAI.Responses.WebSearchOpenPageAction open => $"Web search completed: opened page {open.Uri}.",
            OpenAI.Responses.WebSearchFindInPageAction find =>
                $"Web search completed: found {JsonSerializer.Serialize(find.Pattern)} in page {find.Uri}.",
            _ => "Web search completed."
        };

    /// <summary>The (reasoning item, summary part) a streamed reasoning-summary delta belongs to, when the raw update says.</summary>
    internal static (string? Item, int Index)? ReasoningPart(object? raw) => raw switch
    {
        OpenAI.Responses.StreamingResponseReasoningSummaryTextDeltaUpdate delta => (delta.ItemId, delta.SummaryIndex),
        AgentResponseUpdate agent when !ReferenceEquals(agent.RawRepresentation, agent) => ReasoningPart(agent.RawRepresentation),
        ChatResponseUpdate chat when !ReferenceEquals(chat.RawRepresentation, chat) => ReasoningPart(chat.RawRepresentation),
        _ => null,
    };
#pragma warning restore OPENAI001, CS0618

    /// <summary>
    /// Collects one turn's streamed reasoning summary. The service streams every summary part (a bold
    /// heading and its paragraph) back to back, so a new part is started on its own paragraph instead of
    /// gluing its heading to the previous sentence. <see cref="Take"/> returns the text gathered since the
    /// last tool call or answer so it can be kept with the transcript.
    /// </summary>
    internal sealed class ReasoningText
    {
        private readonly StringBuilder _segment = new();
        private (string? Item, int Index)? _part;
        private int _trailingNewlines = -1;

        /// <summary>Records one delta and returns what to stream, including any paragraph break before a new part.</summary>
        public string Append(string delta, (string? Item, int Index)? part)
        {
            var chunk = delta;
            if (_trailingNewlines >= 0 && part is not null && _part is not null && part != _part && _trailingNewlines < 2)
                chunk = new string('\n', 2 - _trailingNewlines) + delta;
            if (part is not null) _part = part;
            _segment.Append(chunk);
            var newlines = 0;
            for (var i = chunk.Length - 1; i >= 0 && chunk[i] == '\n' && newlines < 2; i--) newlines++;
            _trailingNewlines = newlines == chunk.Length && _trailingNewlines > 0 ? Math.Min(2, _trailingNewlines + newlines) : newlines;
            return chunk;
        }

        public string Take()
        {
            if (_segment.Length == 0) return string.Empty;
            var text = _segment.ToString().Trim();
            _segment.Clear();
            return text;
        }
    }

    internal async Task PublishAsync(AgentEvent item)
    {
        await _publish.WaitAsync();
        try
        {
            if (item.Persisted)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_eventsPath)!);
                await File.AppendAllTextAsync(_eventsPath, JsonSerializer.Serialize(item, Json) + "\n");
                _meta.Modified = item.Timestamp;
            }
            Func<AgentEvent, Task>[] handlers;
            lock (_handlersLock) handlers = [.. _handlers];
            foreach (var handler in handlers)
            {
                try { await handler(item); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { }
            }
        }
        finally { _publish.Release(); }
    }

    private async Task RunAsync(string prompt, string? recap, IReadOnlyList<ImageAttachment>? images, bool lightweight, ApprovalDecision? approval, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var thinking = new ReasoningText();
        string? messageId = null;
        var messageIndex = 0;
        string? approvedRequest = null;
        long? contextTokens = null;

        async Task FlushThinkingAsync()
        {
            if (thinking.Take() is { Length: > 0 } summary) await PublishAsync(new ReasoningEvent(summary));
        }

        async Task FlushMessageAsync()
        {
            await FlushThinkingAsync();
            var content = StripCitationMarkers(text.ToString());
            if (content.Length > 0) await PublishAsync(new AssistantMessageEvent(messageId ?? $"{SessionId}:{messageIndex}", content));
            text.Clear();
            messageId = null;
            messageIndex++;
        }

        // Tools see their conversation and cancellation through this ambient context.
        using var context = new ToolExecutionContext(SessionId, UserId, cancellationToken);
        try
        {
            var agent = _factory.Agent;
            var options = _factory.RunOptions(UserId, lightweight, await _factory.DocumentationToolsAsync(cancellationToken));
            var gated = options.ChatOptions?.Tools?.OfType<ApprovalRequiredAIFunction>().Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal) ?? [];
            List<ChatMessage> messages = [];
            // Agent Framework needs an answer to every approval it surfaced; a new message instead of approving rejects the change.
            var answers = new List<AIContent>();
            foreach (var request in PendingApprovals())
            {
                var interrupted = request.RequestId == _meta.InterruptedApproval;
                var approved = !interrupted && approval is { Approved: true } && approval.RequestId == request.RequestId;
                answers.Add(request.CreateResponse(approved,
                    approved ? null
                    : interrupted ? "The turn that ran this approved change stopped before it finished, so whether it was applied is unknown. Read the resource before proposing it again."
                    : approval?.RequestId == request.RequestId ? "The user rejected this change."
                    : "The user sent a new message instead of approving this change."));
                if (request.ToolCall is not FunctionCallContent call) continue;
                if (approved)
                {
                    approvedRequest = request.RequestId;
                    await ToolStartedAsync(call.CallId, call.Name, Arguments(call));
                }
                else _completedCalls.TryAdd(call.CallId, 0);
            }
            if (answers.Count > 0) messages.Add(new ChatMessage(ChatRole.User, answers));
            List<AIContent> contents = recap is null ? [new TextContent(prompt)] : [new TextContent(recap), new TextContent(prompt)];
            foreach (var image in images ?? [])
                contents.Add(await DataContent.LoadFromAsync(image.Path, image.MimeType, cancellationToken));
            messages.Add(new ChatMessage(ChatRole.User, contents));
            var session = CompactsNextTurn
                ? await agent.CreateSessionAsync(cancellationToken)
                : await RestoreSessionAsync(agent, cancellationToken);
            var surfaced = new List<JsonElement>();
            var completion = new ModelRunCompletion();

            await foreach (var update in agent.RunStreamingAsync(messages, session, options, cancellationToken))
            {
                completion.Observe(update.Contents, update.FinishReason);
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent { Text.Length: > 0 } delta:
                            var id = update.MessageId ?? update.ResponseId;
                            if (messageId is not null && id is not null && id != messageId) await FlushMessageAsync();
                            messageId ??= id;
                            await FlushThinkingAsync();
                            text.Append(delta.Text);
                            await PublishAsync(new MessageDeltaEvent(messageId ?? $"{SessionId}:{messageIndex}", delta.Text));
                            break;
                        case TextReasoningContent { Text.Length: > 0 } reasoning:
                            var part = ReasoningPart(reasoning.RawRepresentation) ?? ReasoningPart(update.RawRepresentation);
                            await PublishAsync(new ReasoningDeltaEvent(thinking.Append(reasoning.Text, part)));
                            break;
                        case FunctionCallContent call:
                            await FlushMessageAsync();
                            // A change starts only once approved, so its request is shown instead.
                            if (!gated.Contains(call.Name)) await ToolStartedAsync(call.CallId, call.Name, Arguments(call));
                            break;
                        case FunctionResultContent result when !_completedCalls.ContainsKey(result.CallId):
                            await ToolStartedAsync(result.CallId, "unknown", null);
                            await ToolCompletedAsync(result.CallId, result.Exception is null, ResultText(result.Result), result.Exception?.Message);
                            break;
                        case ToolApprovalRequestContent { ToolCall: FunctionCallContent call } request:
                            await FlushMessageAsync();
                            surfaced.Add(JsonSerializer.SerializeToElement<AIContent>(request, AIJsonUtilities.DefaultOptions));
                            await PublishAsync(new ApprovalRequestEvent(request.RequestId, call.Name, Arguments(call)));
                            break;
                        case WebSearchToolCallContent search:
                            await FlushMessageAsync();
                            await ToolStartedAsync(search.CallId, "web_search",
                                search.Queries is { Count: > 0 } queries ? JsonSerializer.Serialize(new { queries }) : null);
                            break;
                        case WebSearchToolResultContent searchResult:
                            await ToolCompletedAsync(searchResult.CallId, true, WebSearchOutcome(searchResult.RawRepresentation), null);
                            break;
                        case UsageContent usage:
                            if (usage.Details.InputTokenCount is { } input) contextTokens = Math.Max(contextTokens ?? 0, input);
                            await PublishAsync(new UsageEvent(usage.Details.InputTokenCount, usage.Details.OutputTokenCount, update.FinishReason?.Value));
                            break;
                    }
                }
            }
            await FlushMessageAsync();
            await CompleteOpenCallsAsync();
            if (completion.Failure is { } failure) throw new IncompleteModelResponseException(failure);
            _meta.AgentSession = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken);
            _meta.ContextTokens = contextTokens;
            _meta.PendingApprovals = surfaced.Count > 0 ? surfaced : null;
            _meta.InterruptedApproval = null;
            await PublishAsync(new TurnIdleEvent());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await FlushMessageAsync();
            await CompleteOpenCallsAsync();
            _meta.InterruptedApproval = approvedRequest ?? _meta.InterruptedApproval;
            await PublishAsync(new TurnIdleEvent());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await FlushMessageAsync();
            await CompleteOpenCallsAsync();
            _meta.InterruptedApproval = approvedRequest ?? _meta.InterruptedApproval;
            // Stored responses expire (30 days by default). Drop an expired chain so the
            // next turn starts a fresh model context instead of failing forever.
            if (exception is ClientResultException { Status: 400 or 404 } expired
                && expired.Message.Contains("previous response", StringComparison.OrdinalIgnoreCase))
            {
                _meta.AgentSession = null;
                _meta.ContextTokens = null;
                _meta.PendingApprovals = null;
                _meta.InterruptedApproval = null;
            }
            await PublishAsync(new TurnErrorEvent(_factory.DescribeFailure(exception), "model_error"));
        }
        finally
        {
            SaveMeta();
        }
    }

    private static string Arguments(FunctionCallContent call) =>
        JsonSerializer.Serialize(call.Arguments ?? new Dictionary<string, object?>(), AIJsonUtilities.DefaultOptions);

    // Hosted web search makes the model write private-use citation tokens such as
    // U+E200 "cite" U+E202 "turn0search0" U+E201 into its text. They are not content
    // and render as garbage, so they are removed (with an unterminated one at the end).
    private static readonly Regex CitationMarker = new(
        "[ \\t]*\\uE200cite\\uE202[^\\uE200\\uE201]{0,500}\\uE201|[ \\t]*\\uE200[^\\uE200\\uE201]{0,500}$",
        RegexOptions.CultureInvariant);

    internal static string StripCitationMarkers(string text) =>
        text.Contains('\uE200') ? CitationMarker.Replace(text, string.Empty) : text;

    internal static string? ResultText(object? result) => result switch
    {
        null => null,
        string text => text,
        // MCP tools (Microsoft Learn) return their text as AIContent.
        TextContent content => content.Text,
        IEnumerable<AIContent> contents when contents.All(content => content is TextContent) =>
            string.Join("\n", contents.Cast<TextContent>().Select(content => content.Text)),
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        JsonElement element => element.GetRawText(),
        var other => JsonSerializer.Serialize(other, AIJsonUtilities.DefaultOptions),
    };

    private async Task<AgentSession> RestoreSessionAsync(AIAgent agent, CancellationToken cancellationToken) =>
        _meta.AgentSession is { } saved
            ? await agent.DeserializeSessionAsync(saved, cancellationToken: cancellationToken)
            : await agent.CreateSessionAsync(cancellationToken);

    private async Task CompleteOpenCallsAsync()
    {
        foreach (var callId in _startedCalls.Keys.Where(id => !_completedCalls.ContainsKey(id)).ToList())
            await ToolCompletedAsync(callId, false, null, "The turn ended before this tool completed.");
    }

    private void SaveMeta()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_metaPath)!);
            var temporary = _metaPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_meta, Json));
            File.Move(temporary, _metaPath, overwrite: true);
        }
        catch (IOException) { }
    }

    internal void DeleteLocal()
    {
        try { Directory.Delete(SessionDirectory(WorkingDirectory, SessionId), recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (IsRunning)
        {
            _run?.Cancel();
            try { await _runTask.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException) { }
        }
    }

    private sealed class Meta
    {
        public JsonElement? AgentSession { get; set; }
        public DateTimeOffset Created { get; set; }
        public DateTimeOffset Modified { get; set; }
        public string? Summary { get; set; }
        /// <summary>Largest model input, in tokens, of the last completed turn; drives automatic compaction.</summary>
        public long? ContextTokens { get; set; }
        /// <summary>Approval requests the last completed turn surfaced; the next turn must answer each one.</summary>
        public List<JsonElement>? PendingApprovals { get; set; }
        /// <summary>An approved change whose turn stopped before it finished; it is never re-run, only reported as unknown.</summary>
        public string? InterruptedApproval { get; set; }
        /// <summary>Names of images the user attached; a compacted context no longer shows them, so its recap names them.</summary>
        public List<string>? Attachments { get; set; }
    }

    private sealed class Subscription(AgentConversation owner, Func<AgentEvent, Task> handler) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._handlersLock) owner._handlers.Remove(handler);
        }
    }
}
