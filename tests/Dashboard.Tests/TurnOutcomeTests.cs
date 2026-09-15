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
}