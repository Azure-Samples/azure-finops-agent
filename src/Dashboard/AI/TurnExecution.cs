using System.Collections.Concurrent;
using AzureFinOps.Dashboard.AI.Runtime;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Infrastructure;
using AzureFinOps.Dashboard.Jobs;
using AzureFinOps.Dashboard.Observability;

namespace AzureFinOps.Dashboard.AI;

internal sealed class TurnExecution
{
    internal static readonly ConcurrentDictionary<string, TurnExecution> Active = new();
    private readonly object _sync = new();
    private readonly Dictionary<string, int> _answerLengths = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ToolStartEvent> _calls = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cancellation = new();
    private int _costQueriesBlocked;
    private int _answerCharacters;
    private int _toolsCompleted;
    private int _toolsFailed;
    private int _visibleOutputs;
    private int _emptyNoticeSent;
    private bool _handlerFinished;
    private bool _released;
    private IDisposable? _terminalSubscription;

    internal long UserId { get; }
    internal string RequestId { get; } = Guid.NewGuid().ToString("N");
    internal string? CancellationReason { get; private set; }
    internal int AnswerCharacters => Volatile.Read(ref _answerCharacters);
    internal int ToolsCompleted => Volatile.Read(ref _toolsCompleted);
    internal int ToolsFailed => Volatile.Read(ref _toolsFailed);
    internal bool HasUserOutput => AnswerCharacters > 0 || Volatile.Read(ref _visibleOutputs) > 0 || !ArtifactIds.IsEmpty;
    internal ConcurrentQueue<string> ArtifactIds { get; } = new();
    internal void RecordTool(bool success) { Interlocked.Increment(ref _toolsCompleted); if (!success) Interlocked.Increment(ref _toolsFailed); }
    internal void RecordAnswer(string? content, string? messageId = null)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        lock (_sync)
        {
            var key = messageId ?? "legacy";
            var exists = _answerLengths.TryGetValue(key, out var previous);
            var separator = !exists && _answerLengths.Count > 0 ? 2 : 0;
            _answerLengths[key] = content.Length;
            Interlocked.Add(ref _answerCharacters, content.Length - previous + separator);
        }
    }
    internal void RecordVisibleOutput() => Interlocked.Increment(ref _visibleOutputs);
    internal bool TryClaimEmptyNotice() => Interlocked.CompareExchange(ref _emptyNoticeSent, 1, 0) == 0;
    internal string SessionId { get; }
    internal AgentConversation? Session { get; private set; }
    internal DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    internal CancellationToken CancellationToken { get; }
    internal bool CostQueriesBlocked => Volatile.Read(ref _costQueriesBlocked) != 0;
    internal void BlockCostQueries() => Interlocked.Exchange(ref _costQueriesBlocked, 1);
    internal DateTimeOffset? NextEligibleCostQueryUtc { get; set; }
    internal bool IsScheduled { get; set; }
    internal JobRunOutcome? JobOutcome { get; set; }
    internal ConcurrentQueue<ToolEvidenceEntry> ToolEvidence { get; } = new();
    internal sealed record ToolEvidenceEntry(string Name, bool Success, bool Fresh, bool Partial, DateTimeOffset ObservedUtc, string? ScopeKey = null);
    internal TaskCompletionSource Terminal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TurnExecution(string sessionId, long userId, AgentConversation? session)
    {
        SessionId = sessionId;
        UserId = userId;
        Session = session;
        CancellationToken = _cancellation.Token;
    }

    internal static bool TryBegin(string sessionId, long userId, AgentConversation? session, out TurnExecution turn)
    {
        turn = new TurnExecution(sessionId, userId, session);
        if (!Active.TryAdd(sessionId, turn)) { turn._cancellation.Dispose(); return false; }
        turn.Observe(session);
        if (session is not null) TurnOutcomeStore.Default.Start(turn);
        return true;
    }

    private void Observe(AgentConversation? session)
    {
        _terminalSubscription?.Dispose();
        Session = session;
        _terminalSubscription = session?.On(item =>
        {
            switch (item)
            {
                case AssistantMessageEvent message: RecordAnswer(message.Content, message.MessageId); break;
                case ToolStartEvent start: RecordToolStart(start); break;
                case ToolCompleteEvent done: RecordToolResult(done); break;
                case ApprovalRequestEvent: RecordVisibleOutput(); break;
                case TurnErrorEvent: Cancel("error"); break;
            }
            if (item is TurnIdleEvent or TurnErrorEvent) ConfirmTerminal();
            return Task.CompletedTask;
        });
    }

    internal void RecordToolStart(ToolStartEvent start) => _calls[start.CallId] = start;

    // Outcomes and scheduled-run validation read what each tool actually returned, never the model's account of it.
    internal void RecordToolResult(ToolCompleteEvent done)
    {
        _calls.TryGetValue(done.CallId, out var call);
        var name = call?.ToolName ?? "unknown";
        var text = done.Result ?? done.Error ?? "";
        var evidence = EvidenceInspector.Inspect(text);
        var success = done.Success && evidence.Success;
        RecordTool(success);
        if (EvidenceInspector.EvidenceTools.Contains(name) && !text.StartsWith(AzureQueryTools.CalculationPrefix, StringComparison.Ordinal))
            ToolEvidence.Enqueue(new(name, success, evidence.Fresh, evidence.Partial, DateTimeOffset.UtcNow,
                CostQueryCoordinator.RequestKey("", name, "", call?.Arguments ?? "")));
        else if (name == "unknown" && !success) ToolEvidence.Enqueue(new(name, false, false, false, DateTimeOffset.UtcNow));
        if (success && name is "RenderChart" or "RenderAdvancedChart" or "ReportMaturityScore") RecordVisibleOutput();
        if ((text.StartsWith("__HTML_READY__:", StringComparison.Ordinal) || text.StartsWith("__SCRIPT_READY__:", StringComparison.Ordinal))
            && text.Split(':').ElementAtOrDefault(1) is { Length: > 0 } identifier && ArtifactStore.Default.Find(identifier, UserId) is not null)
            ArtifactIds.Enqueue(identifier);
    }

    internal void Cancel(string reason = "stopped")
    {
        lock (_sync)
        {
            if (_released) return;
            CancellationReason ??= reason;
            _cancellation.Cancel();
        }
    }

    internal void ConfirmTerminal()
    {
        Terminal.TrySetResult();
        TryRelease();
    }

    internal async Task<bool> AbortAsync(string reason = "stopped")
    {
        Cancel(reason);
        try
        {
            if (!Terminal.Task.IsCompleted && Session is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await Session.AbortAsync(timeout.Token);
                await Terminal.Task.WaitAsync(timeout.Token);
            }
            return Terminal.Task.IsCompleted;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Terminal.Task.IsCompleted;
        }
    }

    internal async Task<bool> FinishAsync(bool dispatchAttempted = true)
    {
        if (!dispatchAttempted)
        {
            Cancel("rejected");
            ConfirmTerminal();
        }
        if (!Terminal.Task.IsCompleted) await AbortAsync("interrupted");
        lock (_sync) _handlerFinished = true;
        TryRelease();
        if (!Terminal.Task.IsCompleted) return false;
        try { await Completion.Task.WaitAsync(TimeSpan.FromSeconds(10)); return true; }
        catch (TimeoutException) { return false; }
    }

    private void TryRelease()
    {
        lock (_sync)
        {
            if (_released || !_handlerFinished || !Terminal.Task.IsCompleted) return;
            _released = true;
            if (Session is not null) TurnOutcomeStore.Default.Complete(this);
            Active.TryRemove(new KeyValuePair<string, TurnExecution>(SessionId, this));
            _terminalSubscription?.Dispose();
            _cancellation.Dispose();
            Completion.TrySetResult();
        }
    }
}