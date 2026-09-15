using AzureFinOps.Dashboard.AI;

namespace Dashboard.Tests;

public sealed class TurnExecutionTests
{
    [Fact]
    public async Task RejectedInputReleasesAnUndispatchedTurn()
    {
        var sessionId = Guid.NewGuid().ToString();
        Assert.True(TurnExecution.TryBegin(sessionId, 101, null, out var turn));
        Assert.True(await ChatEndpoints.EndTurnAsync(turn, dispatchAttempted: false));
        Assert.Equal("rejected", turn.CancellationReason);
        Assert.False(TurnExecution.Active.ContainsKey(sessionId));
        Assert.True(TurnExecution.TryBegin(sessionId, 101, null, out var next));
        await ChatEndpoints.EndTurnAsync(next, dispatchAttempted: false);
    }

    [Fact]
    public async Task TerminalSignalDoesNotReleaseRunningHostTools()
    {
        var sessionId = Guid.NewGuid().ToString();
        Assert.True(TurnExecution.TryBegin(sessionId, 101, null, out var turn));
        var lease = turn.AcquireTool(101);
        turn.ConfirmTerminal();
        var finish = turn.FinishAsync();
        Assert.False(finish.IsCompleted);
        Assert.False(TurnExecution.TryBegin(sessionId, 101, null, out _));
        lease.Dispose();
        Assert.True(await finish);
        Assert.False(TurnExecution.Active.ContainsKey(sessionId));
    }

    [Fact]
    public async Task LateCompletionCannotReleaseAReplacementTurn()
    {
        var sessionId = Guid.NewGuid().ToString();
        Assert.True(TurnExecution.TryBegin(sessionId, 101, null, out var previous));
        previous.ConfirmTerminal();
        Assert.True(await previous.FinishAsync());
        Assert.True(TurnExecution.TryBegin(sessionId, 101, null, out var current));
        await previous.FinishAsync();
        Assert.Same(current, TurnExecution.Active[sessionId]);
        current.ConfirmTerminal();
        Assert.True(await current.FinishAsync());
    }

    [Fact]
    public async Task PreviousOrRepeatedToolIdsCannotBorrowALaterTurn()
    {
        var sessionId = Guid.NewGuid().ToString();
        Assert.True(TurnExecution.TryBegin(sessionId, 101, null, out var previous));
        previous.AdmitTool("old-call");
        previous.ConfirmTerminal();
        await previous.FinishAsync();
        Assert.True(TurnExecution.TryBegin(sessionId, 101, null, out var current));
        try
        {
            Assert.Throws<OperationCanceledException>(() => current.AcquireTool(101, "old-call"));
            current.AdmitTool("new-call");
            using (current.AcquireTool(101, "new-call"))
                Assert.Throws<OperationCanceledException>(() => current.AcquireTool(101, "new-call"));
        }
        finally { current.ConfirmTerminal(); await current.FinishAsync(); }
    }

    [Fact]
    public async Task CancellationClosesAdmissionAndRejectsWrongOwner()
    {
        var sessionId = Guid.NewGuid().ToString();
        Assert.True(TurnExecution.TryBegin(sessionId, 101, null, out var turn));
        Assert.Throws<UnauthorizedAccessException>(() => turn.AcquireTool(202));
        turn.Cancel();
        Assert.True(turn.CancellationToken.IsCancellationRequested);
        Assert.Throws<OperationCanceledException>(() => turn.AcquireTool(101));
        Assert.False(await turn.FinishAsync());
        Assert.True(TurnExecution.Active.ContainsKey(sessionId));
        turn.ConfirmTerminal();
        Assert.True(await turn.FinishAsync());
    }
}