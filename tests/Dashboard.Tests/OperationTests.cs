using System.Net;
using System.Text.Json;
using AzureFinOps.Dashboard.Infrastructure;

namespace Dashboard.Tests;

public sealed class OperationTests
{
    [Fact]
    public void RestartRetainsUncertainWritesAndExpiresUnapprovedProposals()
    {
        var root = Path.Combine(Path.GetTempPath(), "finops-operation-retention-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new OperationStore(root);
            var write = store.Begin(101, "synthetic", "PUT", "https://management.azure.com/synthetic/write", "{}", out _);
            var proposal = store.Begin(101, "synthetic", "PUT", "https://management.azure.com/synthetic/proposal", "{}", out _, requiresApproval: true);
            foreach (var operation in new[] { write, proposal })
                File.WriteAllText(Path.Combine(root, operation.Id + ".json"), JsonSerializer.Serialize(operation with
                { StartedUtc = DateTimeOffset.UtcNow.AddDays(-8), UpdatedUtc = DateTimeOffset.UtcNow.AddDays(-8) }));
            var restored = new OperationStore(root);
            Assert.Equal("unknown", restored.Pending(101, "PUT", write.ResourceUrl, "{}")!.Status);
            Assert.Equal("expired", restored.Find(proposal.Id, 101)!.Status);
            Assert.Null(restored.Find(proposal.Id, 101)!.PendingBody);
            Assert.Null(restored.Approve(proposal.Id, 101, "synthetic"));
            Assert.Null(restored.Pending(101, "PUT", proposal.ResourceUrl, "{}"));
            Assert.Contains("No write was sent", OperationStore.Envelope(restored.Find(proposal.Id, 101)!));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ReviewIsOwnerBoundSingleUseAndMatchesExactWrite()
    {
        var root = Path.Combine(Path.GetTempPath(), "finops-approval-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new OperationStore(root);
            var proposal = store.Begin(101, "session", "PATCH", "https://management.azure.com/synthetic/resource", "{\"tags\":{\"Owner\":\"team\"}}", out _, requiresApproval: true);
            Assert.Equal("awaitingApproval", proposal.Status);
            Assert.Null(store.Approve(proposal.Id, 202, "session"));
            Assert.Null(store.Approve(proposal.Id, 101, "other-session"));
            var approved = store.Approve(proposal.Id, 101, "session");
            Assert.NotNull(approved);
            Assert.True(store.MatchesApproved(approved, 101, "session", "PATCH", proposal.ResourceUrl, proposal.PendingBody));
            Assert.False(store.MatchesApproved(approved, 101, "session", "PUT", proposal.ResourceUrl, proposal.PendingBody));
            Assert.False(store.MatchesApproved(approved, 101, "session", "PATCH", proposal.ResourceUrl, "{}"));
            Assert.Null(store.Approve(proposal.Id, 101, "session"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task IntentIsRecordedOnceBeforeDispatchAndUnknownBlocksRetries()
    {
        var root = Path.Combine(Path.GetTempPath(), "finops-intent-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new OperationStore(root);
            var intents = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
            {
                var intent = store.Begin(101, "synthetic", "PUT", "https://management.azure.com/synthetic/resource", "{}", out var created);
                return (intent, created);
            })));
            Assert.Single(intents, item => item.created);
            Assert.Single(intents.Select(item => item.intent.Id).Distinct());
            store.MarkUncertain(intents[0].intent);
            var restarted = new OperationStore(root);
            Assert.Equal("unknown", restarted.Pending(101, "PUT", "https://management.azure.com/synthetic/resource", "{ }")!.Status);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DiagnosticResultsAndPollingDeadlinesAreRetained()
    {
        var root = Path.Combine(Path.GetTempPath(), "finops-diagnostic-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new OperationStore(root);
            using var accepted = new HttpResponseMessage(HttpStatusCode.Accepted);
            var operation = store.Register(101, "synthetic", "POST", "https://management.azure.com/synthetic/connectivityCheck", "{}", accepted, "{}");
            using var polled = new HttpResponseMessage(HttpStatusCode.OK);
            polled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(300));
            var result = store.ObserveResponse(operation, polled, "{\"connectionStatus\":\"Unreachable\",\"probesFailed\":5}", polling: true);
            Assert.Contains("Unreachable", OperationStore.Envelope(result));
            Assert.True(result.NextPollUtc > DateTimeOffset.UtcNow.AddSeconds(299));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(202, "Failed", "failed")]
    [InlineData(202, "Updating", "accepted")]
    [InlineData(200, "Succeeded", "succeeded")]
    [InlineData(200, "Creating", "inProgress")]
    public void HttpSuccessDoesNotEraseProvisioningState(int code, string state, string expected) =>
        Assert.Equal(expected, OperationStore.Classify(code, JsonSerializer.SerializeToElement(new { properties = new { provisioningState = state } })));

    [Fact]
    public void OperationPollingIsOwnerBoundAndSurvivesRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "finops-operation-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new OperationStore(root);
            using var response = new HttpResponseMessage(HttpStatusCode.Accepted);
            response.Headers.Add("Azure-AsyncOperation", "https://attacker.invalid/steal-token");
            var operation = store.Register(101, "synthetic", "PUT", "https://management.azure.com/synthetic/resource?api-version=test", "{}", response, "{\"properties\":{\"provisioningState\":\"Creating\"}}");
            Assert.Equal(operation.ResourceUrl, operation.PollUrl);
            Assert.Null(store.Find(operation.Id, 202));
            Assert.NotNull(store.Pending(101, "PUT", operation.ResourceUrl, "{}"));
            var restored = new OperationStore(root).Find(operation.Id, 101);
            Assert.NotNull(restored);
            var failed = store.Update(restored, 200, "{\"status\":\"Failed\",\"error\":{\"code\":\"AllocationFailed\"}}");
            Assert.Equal("failed", failed.Status);
            Assert.Contains("AllocationFailed", OperationStore.Envelope(failed));
            Assert.Null(store.Pending(101, "PUT", operation.ResourceUrl, "{}"));
        }
        finally { Directory.Delete(root, true); }
    }
}