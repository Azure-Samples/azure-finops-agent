using System.Collections.Concurrent;
using GitHub.Copilot;
using AzureFinOps.Dashboard.Jobs;
using AzureFinOps.Dashboard.Observability;

namespace AzureFinOps.Dashboard.AI;

internal sealed class TurnExecution
{
    internal static readonly ConcurrentDictionary<string, TurnExecution> Active = new();
    private readonly object _sync = new();
    private readonly HashSet<string> _admittedTools = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cancellation = new();
    private int _tools;
    private int _costQueriesBlocked;
    private int _answerCharacters;
    private int _toolsCompleted;
    private int _toolsFailed;
    private bool _closed;
    private bool _handlerFinished;
    private bool _released;
    private IDisposable? _terminalSubscription;

    internal long UserId { get; }
    internal string RequestId { get; } = Guid.NewGuid().ToString("N");
    internal string? CancellationReason { get; private set; }
    internal int AnswerCharacters => Volatile.Read(ref _answerCharacters);
    internal int ToolsCompleted => Volatile.Read(ref _toolsCompleted);
    internal int ToolsFailed => Volatile.Read(ref _toolsFailed);
    internal ConcurrentQueue<string> ArtifactIds { get; } = new();
    internal void RecordTool(bool success) { Interlocked.Increment(ref _toolsCompleted); if (!success) Interlocked.Increment(ref _toolsFailed); }
    internal string SessionId { get; private set; }
    internal CopilotSession? Session { get; private set; }
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

    private TurnExecution(string sessionId, long userId, CopilotSession? session)
    {
        SessionId = sessionId;
        UserId = userId;
        Session = session;
        CancellationToken = _cancellation.Token;
    }

    internal static bool TryBegin(string sessionId, long userId, CopilotSession? session, out TurnExecution turn)
    {
        turn = new TurnExecution(sessionId, userId, session);
        if (!Active.TryAdd(sessionId, turn)) { turn._cancellation.Dispose(); return false; }
        turn.Observe(session);
        if (session is not null) TurnOutcomeStore.Default.Start(turn);
        return true;
    }

    internal bool MoveTo(CopilotSession session)
    {
        if (SessionId != session.SessionId)
        {
            if (!Active.TryAdd(session.SessionId, this)) return false;
            Active.TryRemove(new KeyValuePair<string, TurnExecution>(SessionId, this));
            SessionId = session.SessionId;
        }
        Observe(session);
        return true;
    }

    private void Observe(CopilotSession? session)
    {
        _terminalSubscription?.Dispose();
        Session = session;
        _terminalSubscription = session?.On<SessionEvent>(item =>
        {
            if (item is ToolExecutionStartEvent tool && !string.IsNullOrWhiteSpace(tool.Data.ToolCallId))
                AdmitTool(tool.Data.ToolCallId);
            if (item is AssistantMessageEvent message) Interlocked.Exchange(ref _answerCharacters, message.Data.Content?.Length ?? 0);
            if (item is SessionErrorEvent) Cancel("error");
            if (item is SessionIdleEvent or SessionErrorEvent) ConfirmTerminal();
        });
    }

    internal void AdmitTool(string toolCallId)
    {
        lock (_sync)
            if (!_closed && !_released) _admittedTools.Add(toolCallId);
    }

    internal IDisposable AcquireTool(long owner, string? toolCallId = null)
    {
        lock (_sync)
        {
            if (owner != UserId) throw new UnauthorizedAccessException("Tool ownership could not be verified.");
            if (_closed || _released) throw new OperationCanceledException("The originating turn is no longer accepting tools.", CancellationToken);
            if (toolCallId is not null && !_admittedTools.Remove(toolCallId))
                throw new OperationCanceledException("The tool invocation was not admitted by this turn.", CancellationToken);
            _tools++;
        }
        return new ToolLease(this);
    }

    internal void Cancel(string reason = "stopped")
    {
        lock (_sync)
        {
            if (_released) return;
            _closed = true;
            CancellationReason ??= reason;
            _cancellation.Cancel();
        }
    }

    internal void ConfirmTerminal()
    {
        lock (_sync) { _closed = true; Terminal.TrySetResult(); }
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
        lock (_sync) { _closed = true; _handlerFinished = true; }
        TryRelease();
        if (!Terminal.Task.IsCompleted) return false;
        try { await Completion.Task.WaitAsync(TimeSpan.FromSeconds(10)); return true; }
        catch (TimeoutException) { return false; }
    }

    private void TryRelease()
    {
        lock (_sync)
        {
            if (_released || !_handlerFinished || !Terminal.Task.IsCompleted || _tools != 0) return;
            _released = true;
            if (Session is not null) TurnOutcomeStore.Default.Complete(this);
            Active.TryRemove(new KeyValuePair<string, TurnExecution>(SessionId, this));
            _terminalSubscription?.Dispose();
            _cancellation.Dispose();
            Completion.TrySetResult();
        }
    }

    private sealed class ToolLease(TurnExecution turn) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (turn._sync) turn._tools--;
            turn.TryRelease();
        }
    }
}