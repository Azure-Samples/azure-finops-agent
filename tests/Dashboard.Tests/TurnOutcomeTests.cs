using System.Text.Json;
using AzureFinOps.Dashboard.AI;
using AzureFinOps.Dashboard.Observability;

namespace Dashboard.Tests;

public sealed class TurnOutcomeTests
{
    [Fact]
    public void StartupRecoversInterruptedTurnsAndExpiresOldOutcomes()
    {
        var root = Path.Combine(Path.GetTempPath(), "finops-outcome-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var pending = new TurnOutcomeStore.Outcome(Guid.NewGuid().ToString("N"), 101, "synthetic-session", "running", "not_evaluated",
                DateTimeOffset.UtcNow.AddMinutes(-5), null, 0, 0, 0, 0, [], false);
            var expired = pending with { RequestId = Guid.NewGuid().ToString("N"), StartedUtc = DateTimeOffset.UtcNow.AddDays(-31) };
            File.WriteAllText(Path.Combine(root, pending.RequestId + ".json"), JsonSerializer.Serialize(pending));
            File.WriteAllText(Path.Combine(root, expired.RequestId + ".json"), JsonSerializer.Serialize(expired));
            var store = new TurnOutcomeStore(root);
            var recovered = Assert.Single(store.ForSession(101, pending.SessionId));
            Assert.Equal("interrupted", recovered.Status);
            Assert.Equal("not_evaluated", recovered.Fulfillment);
            Assert.NotNull(recovered.CompletedUtc);
            Assert.False(File.Exists(Path.Combine(root, expired.RequestId + ".json")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DurableOutcomesAreOwnerBoundAndIdempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), "finops-outcome-test-" + Guid.NewGuid().ToString("N"));
        var store = new TurnOutcomeStore(root);
        Assert.True(TurnExecution.TryBegin(Guid.NewGuid().ToString(), 101, null, out var turn));
        try
        {
            store.Start(turn);
            Assert.Equal("running", Assert.Single(store.ForSession(101, turn.SessionId)).Status);
            turn.Cancel("timeout");
            store.Complete(turn);
            store.Complete(turn);
            var saved = Assert.Single(new TurnOutcomeStore(root).ForSession(101, turn.SessionId));
            Assert.Equal("timeout", saved.Status);
            Assert.Equal("not_evaluated", saved.Fulfillment);
            Assert.NotNull(saved.CompletedUtc);
            Assert.Empty(store.ForSession(202, turn.SessionId));
            Assert.DoesNotContain("prompt", JsonSerializer.Serialize(saved), StringComparison.OrdinalIgnoreCase);
        }
        finally { turn.ConfirmTerminal(); await turn.FinishAsync(); Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedToolsCannotLookLikeCleanExecution(bool rejectedBeforeCallback)
    {
        var root = Path.Combine(Path.GetTempPath(), "finops-outcome-failure-" + Guid.NewGuid().ToString("N"));
        var store = new TurnOutcomeStore(root);
        Assert.True(TurnExecution.TryBegin(Guid.NewGuid().ToString(), 101, null, out var turn));
        try
        {
            turn.RecordAnswer("A synthetic fallback answer is not proof the requested chart was delivered.");
            if (rejectedBeforeCallback)
            {
                turn.AdmitTool("synthetic-call", "RenderChart");
                turn.RecordUndispatchedToolFailure("synthetic-call");
            }
            else
                turn.RecordTool(false);
            store.Complete(turn);
            var outcome = Assert.Single(store.ForSession(101, turn.SessionId));
            Assert.Equal("partial", outcome.Status);
            Assert.Equal(1, outcome.ToolsFailed);
            Assert.Equal("not_evaluated", outcome.Fulfillment);
        }
        finally { turn.ConfirmTerminal(); await turn.FinishAsync(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task TrulyEmptyTurnsHaveAnExplicitRecoverableNotice()
    {
        Assert.True(TurnExecution.TryBegin(Guid.NewGuid().ToString(), 101, null, out var turn));
        try
        {
            using var notice = JsonDocument.Parse(ChatEndpoints.EmptyResultNotice(turn)!);
            Assert.Equal("empty_result", notice.RootElement.GetProperty("code").GetString());
            turn.RecordVisibleOutput();
            Assert.Null(ChatEndpoints.EmptyResultNotice(turn));
        }
        finally { turn.ConfirmTerminal(); await turn.FinishAsync(); }
    }

    [Fact]
    public async Task ExplicitStopIsNotMisreportedAsAnEmptyModelResult()
    {
        Assert.True(TurnExecution.TryBegin(Guid.NewGuid().ToString(), 101, null, out var turn));
        try
        {
            turn.Cancel("stopped");
            Assert.Null(ChatEndpoints.EmptyResultNotice(turn));
        }
        finally { turn.ConfirmTerminal(); await turn.FinishAsync(); }
    }

    [Fact]
    public async Task AToolOnlyMessageDoesNotEraseAnAlreadyDeliveredAnswer()
    {
        Assert.True(TurnExecution.TryBegin(Guid.NewGuid().ToString(), 101, null, out var turn));
        try
        {
            turn.RecordAnswer("Synthetic completed answer.");
            turn.RecordAnswer("");
            Assert.True(turn.HasUserOutput);
            Assert.Null(ChatEndpoints.EmptyResultNotice(turn));
        }
        finally { turn.ConfirmTerminal(); await turn.FinishAsync(); }
    }

    [Fact]
    public async Task DistinctAssistantMessagesAreCountedWithoutDuplicatingSnapshots()
    {
        Assert.True(TurnExecution.TryBegin(Guid.NewGuid().ToString(), 101, null, out var turn));
        try
        {
            turn.RecordAnswer("Cost table", "answer");
            turn.RecordAnswer("Follow-up link", "follow-up");
            turn.RecordAnswer("Follow-up link", "follow-up");
            turn.RecordAnswer("", "tool-only");
            Assert.Equal("Cost table\n\nFollow-up link".Length, turn.AnswerCharacters);
        }
        finally { turn.ConfirmTerminal(); await turn.FinishAsync(); }
    }
}