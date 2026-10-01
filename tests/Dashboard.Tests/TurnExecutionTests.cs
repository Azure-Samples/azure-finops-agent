using AzureFinOps.Dashboard.AI;
using AzureFinOps.Dashboard.AI.Runtime;

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
    public async Task CancellationHoldsTheGateUntilTheRunIsTerminal()
    {
        var sessionId = Guid.NewGuid().ToString();
        Assert.True(TurnExecution.TryBegin(sessionId, 101, null, out var turn));
        turn.Cancel();
        Assert.True(turn.CancellationToken.IsCancellationRequested);
        Assert.False(await turn.FinishAsync());
        Assert.True(TurnExecution.Active.ContainsKey(sessionId));
        turn.ConfirmTerminal();
        Assert.True(await turn.FinishAsync());
        Assert.False(TurnExecution.Active.ContainsKey(sessionId));
    }

    [Fact]
    public async Task ActiveProbeReportsTheRunningTurnsAgeAndProgress()
    {
        var sessionId = Guid.NewGuid().ToString();
        Assert.Equal(new ChatEndpoints.ActiveTurnSnapshot(false), ChatEndpoints.ActiveTurnState(sessionId));
        Assert.True(TurnExecution.TryBegin(sessionId, 101, null, out var turn));
        try
        {
            turn.RecordTool(success: true);
            turn.RecordTool(success: false);
            var state = ChatEndpoints.ActiveTurnState(sessionId);
            Assert.Equal(new ChatEndpoints.ActiveTurnSnapshot(true, turn.StartedAt, 2, false), state);

            // The browser reads these exact property names after a reload.
            var json = System.Text.Json.JsonSerializer.SerializeToElement(state, System.Text.Json.JsonSerializerOptions.Web);
            Assert.True(json.GetProperty("active").GetBoolean());
            Assert.Equal(turn.StartedAt, json.GetProperty("startedUtc").GetDateTimeOffset());
            Assert.Equal(2, json.GetProperty("toolsCompleted").GetInt32());
            Assert.False(json.GetProperty("scheduled").GetBoolean());
        }
        finally { turn.ConfirmTerminal(); await turn.FinishAsync(); }
        Assert.False(ChatEndpoints.ActiveTurnState(sessionId).Active);
    }

    [Fact]
    public async Task EvidenceIsWhatTheToolReturnedNotTheModelsAccount()
    {
        Assert.True(TurnExecution.TryBegin(Guid.NewGuid().ToString(), 101, null, out var turn));
        try
        {
            turn.RecordToolStart(new("call-1", "QueryAzure", """{"url":"/subscriptions/s/providers/Microsoft.CostManagement/query?api-version=2025-03-01"}"""));
            turn.RecordToolResult(new("call-1", true, "HTTP 200 OK\nCurrent UTC time: 2026-09-25 20:20:56\n{\"value\":[]}", null));
            turn.RecordToolStart(new("call-2", "QueryAzure", """{"url":"/subscriptions/s/resources?api-version=2021-04-01"}"""));
            turn.RecordToolResult(new("call-2", true, "HTTP 403 Forbidden\n{\"error\":{\"code\":\"AuthorizationFailed\"}}", null));
            turn.RecordToolResult(new("never-started", false, null, "Error: Function failed."));
            turn.RecordToolStart(new("call-3", "QueryAzure", """{"query":"2 * 3"}"""));
            turn.RecordToolResult(new("call-3", true, AzureFinOps.Dashboard.AI.Tools.AzureQueryTools.CalculationPrefix + "\n6", null));

            Assert.Equal(4, turn.ToolsCompleted);
            Assert.Equal(2, turn.ToolsFailed);
            var evidence = turn.ToolEvidence.ToArray();
            Assert.Equal(3, evidence.Length);
            Assert.True(evidence[0] is { Name: "QueryAzure", Success: true, Fresh: true, Partial: false });
            Assert.NotNull(evidence[0].ScopeKey);
            Assert.True(evidence[1] is { Name: "QueryAzure", Success: false });
            Assert.True(evidence[2] is { Name: "unknown", Success: false, Fresh: false });
        }
        finally { turn.ConfirmTerminal(); await turn.FinishAsync(); }
    }
}