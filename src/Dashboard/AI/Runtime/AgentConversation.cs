using System.ClientModel;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

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
    /// and the turn always ends with exactly one <see cref="TurnIdleEvent"/> or <see cref="TurnErrorEvent"/>.</summary>
    public async Task SendAsync(string prompt, IReadOnlyList<ImageAttachment>? images = null, bool lightweight = false)
    {
        if (IsRunning) throw new InvalidOperationException("A turn is already running in this conversation.");
        var run = new CancellationTokenSource();
        _run = run;
        _startedCalls.Clear();
        _completedCalls.Clear();
        await PublishAsync(new UserMessageEvent(prompt));
        if (string.IsNullOrWhiteSpace(_meta.Summary))
        {
            _meta.Summary = prompt.Length > 400 ? prompt[..400] : prompt;
            SaveMeta();
        }
        _runTask = Task.Run(() => RunAsync(prompt, images, lightweight, run.Token));
    }

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
        _completedCalls.TryAdd(callId, 0) ? PublishAsync(new ToolCompleteEvent(callId, success, result, error)) : Task.CompletedTask;

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

    private async Task RunAsync(string prompt, IReadOnlyList<ImageAttachment>? images, bool lightweight, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        string? messageId = null;
        var messageIndex = 0;

        async Task FlushMessageAsync()
        {
            if (text.Length > 0) await PublishAsync(new AssistantMessageEvent(messageId ?? $"{SessionId}:{messageIndex}", text.ToString()));
            text.Clear();
            messageId = null;
            messageIndex++;
        }

        try
        {
            var agent = _factory.Agent;
            List<AIContent> contents = [new TextContent(prompt)];
            foreach (var image in images ?? [])
                contents.Add(await DataContent.LoadFromAsync(image.Path, image.MimeType, cancellationToken));
            var message = new ChatMessage(ChatRole.User, contents);
            var session = await RestoreSessionAsync(agent, cancellationToken);

            await foreach (var update in agent.RunStreamingAsync(message, session, _factory.RunOptions(UserId, SessionId, lightweight), cancellationToken))
            {
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent { Text.Length: > 0 } delta:
                            var id = update.MessageId ?? update.ResponseId;
                            if (messageId is not null && id is not null && id != messageId) await FlushMessageAsync();
                            messageId ??= id;
                            text.Append(delta.Text);
                            await PublishAsync(new MessageDeltaEvent(messageId ?? $"{SessionId}:{messageIndex}", delta.Text));
                            break;
                        case TextReasoningContent { Text.Length: > 0 } reasoning:
                            await PublishAsync(new ReasoningDeltaEvent(reasoning.Text));
                            break;
                        case FunctionCallContent:
                            await FlushMessageAsync();
                            break;
                        case FunctionResultContent result when !_startedCalls.ContainsKey(result.CallId):
                            // The function was never invoked (unknown name or rejected before dispatch).
                            await ToolStartedAsync(result.CallId, "unknown", null);
                            await ToolCompletedAsync(result.CallId, false, null,
                                $"{TurnExecution.RejectedToolPrefix} {result.Result ?? result.Exception?.Message}".Trim());
                            break;
                        case WebSearchToolCallContent search:
                            await FlushMessageAsync();
                            await ToolStartedAsync(search.CallId, "web_search",
                                search.Queries is { Count: > 0 } queries ? JsonSerializer.Serialize(new { queries }) : null);
                            break;
                        case WebSearchToolResultContent searchResult:
                            await ToolCompletedAsync(searchResult.CallId, true, "Web search completed.", null);
                            break;
                        case UsageContent usage:
                            await PublishAsync(new UsageEvent(usage.Details.InputTokenCount, usage.Details.OutputTokenCount, update.FinishReason?.Value));
                            break;
                    }
                }
            }
            await FlushMessageAsync();
            await CompleteOpenCallsAsync();
            _meta.AgentSession = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken);
            await PublishAsync(new TurnIdleEvent());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await FlushMessageAsync();
            await CompleteOpenCallsAsync();
            await PublishAsync(new TurnIdleEvent());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await FlushMessageAsync();
            await CompleteOpenCallsAsync();
            // Stored responses expire (30 days by default). Drop an expired chain so the
            // next turn starts a fresh model context instead of failing forever.
            if (exception is ClientResultException { Status: 400 or 404 } expired
                && expired.Message.Contains("previous response", StringComparison.OrdinalIgnoreCase))
                _meta.AgentSession = null;
            await PublishAsync(new TurnErrorEvent(_factory.DescribeFailure(exception), "model_error"));
        }
        finally
        {
            SaveMeta();
        }
    }

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
