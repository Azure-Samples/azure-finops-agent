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

    [Fact]
    public async Task CallbackWaitsForMatchingAdmissionAndCannotRunTwice()
    {
        Assert.True(TurnExecution.TryBegin(Guid.NewGuid().ToString(), 101, null, out var turn));
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
                await turn.AcquireToolAsync(202, "pending-call", CancellationToken.None));
            var pending = turn.AcquireToolAsync(101, "pending-call", CancellationToken.None).AsTask();
            Assert.False(pending.IsCompleted);
            turn.AdmitTool("different-call");
            Assert.False(pending.IsCompleted);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await turn.AcquireToolAsync(101, "pending-call", CancellationToken.None));
            turn.AdmitTool("pending-call");
            using var lease = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            turn.AdmitTool("pending-call");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await turn.AcquireToolAsync(101, "pending-call", CancellationToken.None));
        }
        finally { turn.ConfirmTerminal(); await turn.FinishAsync(); }
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("turn")]
    [InlineData("terminal")]
    public async Task PendingAdmissionStopsWhenCancelledOrTerminal(string reason)
    {
        Assert.True(TurnExecution.TryBegin(Guid.NewGuid().ToString(), 101, null, out var turn));
        using var caller = new CancellationTokenSource();
        try
        {
            var pending = turn.AcquireToolAsync(101, "pending-call", caller.Token).AsTask();
            Assert.False(pending.IsCompleted);
            if (reason == "caller") caller.Cancel();
            else if (reason == "turn") turn.Cancel();
            else turn.ConfirmTerminal();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, turn.ToolsCompleted);
        }
        finally { turn.ConfirmTerminal(); Assert.True(await turn.FinishAsync()); }
    }

    [Fact]
    public async Task SdkRejectionBeforeCallbackIsRecordedExactlyOnce()
    {
        Assert.True(TurnExecution.TryBegin(Guid.NewGuid().ToString(), 101, null, out var turn));
        try
        {
            turn.AdmitTool("rejected-call", "RenderChart");
            turn.RecordUndispatchedToolFailure("rejected-call");
            turn.RecordUndispatchedToolFailure("rejected-call");
            turn.RecordUndispatchedToolFailure("unknown-call");
            Assert.Equal(1, turn.ToolsCompleted);
            Assert.Equal(1, turn.ToolsFailed);
            var evidence = Assert.Single(turn.ToolEvidence);
            Assert.Equal("RenderChart", evidence.Name);
            Assert.False(evidence.Success);
            Assert.False(evidence.Fresh);
            Assert.Throws<OperationCanceledException>(() => turn.AcquireTool(101, "rejected-call"));
        }
        finally { turn.ConfirmTerminal(); await turn.FinishAsync(); }
    }

    [Fact]
    public async Task SdkCompletionDoesNotDoubleCountAnAcquiredCallback()
    {
        Assert.True(TurnExecution.TryBegin(Guid.NewGuid().ToString(), 101, null, out var turn));
        try
        {
            turn.AdmitTool("callback-call", "QueryAzure");
            using (turn.AcquireTool(101, "callback-call"))
                turn.RecordTool(false);
            turn.RecordUndispatchedToolFailure("callback-call");
            Assert.Equal(1, turn.ToolsCompleted);
            Assert.Equal(1, turn.ToolsFailed);
            Assert.Empty(turn.ToolEvidence);
        }
        finally { turn.ConfirmTerminal(); await turn.FinishAsync(); }
    }
}