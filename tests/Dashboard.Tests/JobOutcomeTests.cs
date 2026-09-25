using AzureFinOps.Dashboard.AI;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Jobs;

namespace Dashboard.Tests;

public sealed class JobOutcomeTests
{
    [Theory]
    [InlineData("{\n \"complete\": false\n}", true, true, true)]
    [InlineData("{\"sourceEvidence\":[{\"cacheStatus\": \"cached\"}]}", true, false, false)]
    [InlineData("{\"_finops\":{\"freshness\":\"periodic\"}}", true, false, false)]
    [InlineData("{\"status\": \"accepted\"}", true, true, true)]
    [InlineData("{\"results\":[{\"error\":\"unavailable\"}]}", false, true, false)]
    [InlineData("{\"complete\":false,\"results\":[{\"outcome\":\"failed\",\"body\":{\"error\":\"denied\"}}]}", false, true, true)]
    [InlineData("{\"complete\":false,\"results\":[{\"outcome\":\"cancelled\"}]}", false, true, true)]
    [InlineData("{\"complete\":true,\"results\":[{\"outcome\":\"succeeded\",\"body\":{\"_finops\":{\"cacheStatus\":\"cached\"}}}]}", true, false, false)]
    [InlineData("{\"complete\":false,\"results\":[{\"outcome\":\"succeeded\",\"partial\":true,\"sourceEvidence\":{\"cacheStatus\":\"stale_during_cooldown\"}}]}", true, false, true)]
    [InlineData("RESOLUTION {\"status\":\"ambiguous\",\"complete\":false}\n| prices |", true, true, true)]
    [InlineData("HTTP 200 OK\nCurrent UTC time: 2026-01-01 00:00:00\n{\"_finops\":{\"cacheStatus\":\"cached\"}}", true, false, false)]
    [InlineData("HTTP 200 OK\nCurrent UTC time: 2026-01-01 00:00:00\n{\"complete\":false,\"_finops\":{\"cacheStatus\":\"queried\"}}", true, true, true)]
    [InlineData("HTTP 429 TooManyRequests\nCurrent UTC time: 2026-01-01 00:00:00\n{\"error\":{\"code\":\"429\"},\"_finops\":{\"cacheStatus\":\"not_available\"}}", false, false, false)]
    public void EvidenceClassificationParsesStructuredMetadata(string text, bool success, bool fresh, bool partial)
    {
        Assert.Equal((success, fresh, partial), ProtectedTool.InspectEvidence(text));
    }

    [Fact]
    public void CrossSubscriptionProjectionPreservesCacheProvenance()
    {
        var evidence = AzureQueryTools.ReadCostSourceEvidence("HTTP 200 OK\n{\"properties\":{},\"_finops\":{\"cacheStatus\":\"stale_during_cooldown\",\"retrievedAtUtc\":\"2026-01-01T00:00:00Z\"}}");
        Assert.Equal("stale_during_cooldown", evidence.GetProperty("cacheStatus").GetString());
        Assert.Equal("2026-01-01T00:00:00Z", evidence.GetProperty("retrievedAtUtc").GetString());
        Assert.False(ProtectedTool.InspectEvidence(evidence.GetRawText()).Fresh);
        Assert.False(ProtectedTool.InspectEvidence(AzureQueryTools.ReadCostSourceEvidence("HTTP 200 OK\n{}").GetRawText()).Fresh);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"Unavailable\"")]
    public void NonObjectCostResponsesHaveUnknownEvidence(string body)
    {
        var evidence = AzureQueryTools.ReadCostSourceEvidence("HTTP 400 BadRequest\n" + body);
        Assert.Equal("unknown", evidence.GetProperty("cacheStatus").GetString());
        Assert.False(ProtectedTool.InspectEvidence(evidence.GetRawText()).Fresh);
    }

    [Fact]
    public async Task AGoodReadCannotHideAnotherIncompleteReadOfTheSameTool()
    {
        Assert.True(TurnExecution.TryBegin(Guid.NewGuid().ToString(), 101, null, out var turn));
        try
        {
            turn.ToolEvidence.Enqueue(new("QueryAzure", true, true, false, DateTimeOffset.UtcNow));
            turn.ToolEvidence.Enqueue(new("QueryAzure", true, true, true, DateTimeOffset.UtcNow));
            var reported = new JobRunOutcome("unchanged", "Synthetic result", ["QueryAzure"], null, null);
            Assert.Equal("failed", JobRunOutcome.Validate(reported, turn, "Synthetic answer", DateTimeOffset.UtcNow).Status);
        }
        finally { turn.ConfirmTerminal(); await turn.FinishAsync(); }
    }

    [Theory]
    [InlineData("same-scope", "completed")]
    [InlineData("different-scope", "failed")]
    public async Task LaterEvidenceResolvesOnlyTheIdenticalRequest(string nextScope, string expected)
    {
        Assert.True(TurnExecution.TryBegin(Guid.NewGuid().ToString(), 101, null, out var turn));
        try
        {
            turn.ToolEvidence.Enqueue(new("GetOperationStatus", true, true, true, DateTimeOffset.UtcNow, "same-scope"));
            turn.ToolEvidence.Enqueue(new("GetOperationStatus", true, true, false, DateTimeOffset.UtcNow, nextScope));
            var reported = new JobRunOutcome("completed", "Synthetic result", ["GetOperationStatus"], null, null);
            Assert.Equal(expected, JobRunOutcome.Validate(reported, turn, "Synthetic answer", DateTimeOffset.UtcNow).Status);
        }
        finally { turn.ConfirmTerminal(); await turn.FinishAsync(); }
    }

    [Theory]
    [InlineData(false, false, "completed", "failed")]
    [InlineData(true, false, "completed", "failed")]
    [InlineData(true, true, "completed", "completed")]
    [InlineData(true, true, "goal_achieved", "goal_achieved")]
    public async Task SuccessRequiresObservedFreshEvidence(bool observed, bool fresh, string status, string expected)
    {
        Assert.True(TurnExecution.TryBegin(Guid.NewGuid().ToString(), 101, null, out var turn));
        if (observed) turn.ToolEvidence.Enqueue(new("QueryAzure", true, fresh, false, DateTimeOffset.UtcNow));
        try
        {
            var reported = new JobRunOutcome(status, "Synthetic result", ["QueryAzure"], null, null);
            var result = JobRunOutcome.Validate(reported, turn, "Synthetic answer", DateTimeOffset.UtcNow);
            Assert.Equal(expected, result.Status);
            Assert.Equal("failed", JobRunOutcome.Validate(reported, turn, "", DateTimeOffset.UtcNow).Status);
            Assert.Equal("failed", JobRunOutcome.Validate(null, turn, "disable this job", DateTimeOffset.UtcNow).Status);
        }
        finally { turn.ConfirmTerminal(); await turn.FinishAsync(); }
    }

    [Fact]
    public void GoalAchievedPausesAndBlockedRunsCountTowardPause()
    {
        var now = DateTimeOffset.UtcNow;
        var job = new ScheduledJob { ExpiresUtc = now.AddDays(7) };
        JobRunOutcome.Apply(job, new("goal_achieved", "Verified goal", ["QueryAzure"], now, null), now);
        Assert.False(job.Enabled);
        Assert.Equal(0, job.ConsecutiveFailures);
        job.Enabled = true;
        for (var index = 0; index < 5; index++)
            JobRunOutcome.Apply(job, new("blocked", "Cooldown", [], null, now.AddMinutes(10)), now);
        Assert.False(job.Enabled);
        Assert.Equal(5, job.ConsecutiveFailures);
        Assert.Equal(now.AddMinutes(10), job.NextRunUtc);
    }
}
