using System.Net;
using System.Text.Json;
using AzureFinOps.Dashboard.AI;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Infrastructure;

namespace Dashboard.Tests;

public sealed class CostQueryCoordinatorTests
{
    private const string Result = "HTTP 200 OK\n{\"properties\":{\"rows\":[[42.73,\"USD\"]]}}";

    [Fact]
    public async Task SameTenantSerializesWithoutBlockingAnotherTenant()
    {
        using var coordinator = new CostQueryCoordinator();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = coordinator.ExecuteAsync("tenant-a", "request-a", async token => { entered.SetResult(); await release.Task.WaitAsync(token); return Result; }, default);
        await entered.Task;
        var second = coordinator.ExecuteAsync("tenant-a", "request-b", token => { secondEntered.SetResult(); return Task.FromResult(Result); }, default);
        try
        {
            Assert.False(secondEntered.Task.IsCompleted);
            await coordinator.ExecuteAsync("tenant-b", "request-c", token => Task.FromResult(Result), default).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(first, second);
        Assert.True(secondEntered.Task.IsCompleted);
    }

    [Fact]
    public async Task IdenticalQueriesShareCachedResultButCredentialsDoNot()
    {
        using var coordinator = new CostQueryCoordinator();
        var calls = 0;
        Task<string> Send(CancellationToken token) { Interlocked.Increment(ref calls); return Task.FromResult(Result); }
        var firstKey = CostQueryCoordinator.RequestKey("owner-a", "POST", "https://example.test/query", "{\"a\":1,\"b\":2}");
        var secondKey = CostQueryCoordinator.RequestKey("owner-a", "POST", "https://example.test/query", "{ \"b\":2, \"a\":1 }");
        Assert.Equal(firstKey, secondKey);
        await coordinator.ExecuteAsync("tenant", firstKey, Send, default);
        var cached = await coordinator.ExecuteAsync("tenant", secondKey, Send, default);
        Assert.Equal(1, calls);
        Assert.Contains("\"cacheStatus\":\"cached\"", cached);
        var otherKey = CostQueryCoordinator.RequestKey("owner-b", "POST", "https://example.test/query", "{\"a\":1,\"b\":2}");
        await coordinator.ExecuteAsync("tenant", otherKey, Send, default);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task FullCooldownSurvivesNewRequestsAndLabelsStaleData()
    {
        var clock = new TestClock();
        using var coordinator = new CostQueryCoordinator(clock);
        var calls = 0;
        Task<string> Send(CancellationToken token) { calls++; return Task.FromResult(Result); }
        await coordinator.ExecuteAsync("tenant", "known", Send, default);
        coordinator.RecordThrottle("tenant", 300);
        var stale = await coordinator.ExecuteAsync("tenant", "known", Send, default);
        Assert.Contains("stale_during_cooldown", stale);
        Assert.Contains("HTTP 429", await coordinator.ExecuteAsync("tenant", "unknown", Send, default));
        clock.Advance(TimeSpan.FromSeconds(299));
        Assert.Contains("HTTP 429", await coordinator.ExecuteAsync("tenant", "unknown", Send, default));
        Assert.Equal(1, calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        var fresh = await coordinator.ExecuteAsync("tenant", "unknown", Send, default);
        Assert.Equal(2, calls);
        using var parsed = JsonDocument.Parse(fresh[(fresh.IndexOf('\n') + 1)..]);
        Assert.Equal(42.73m, parsed.RootElement.GetProperty("properties").GetProperty("rows")[0][0].GetDecimal());
        Assert.Equal(JsonValueKind.Null, parsed.RootElement.GetProperty("_finops").GetProperty("dataAsOfUtc").ValueKind);
    }

    [Fact]
    public void ServiceRetryDeadlineIsNotShortened()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.Add("x-ms-ratelimit-microsoft.costmanagement-qpu-retry-after", "300");
        Assert.Equal(300, HttpHelper.ResolveRetryAfterSeconds(response, 0));
    }

    [Fact]
    public void LongestCostThrottleHeaderWinsAndMissingHeadersUseOneMinute()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        Assert.Equal(60, HttpHelper.ResolveRetryAfterSeconds(response, 0, costQuery: true));
        response.Headers.Add("x-ms-ratelimit-microsoft.costmanagement-qpu-retry-after", "10");
        response.Headers.Add("x-ms-ratelimit-microsoft.costmanagement-clienttype-retry-after", "300");
        response.Headers.Add("x-ms-ratelimit-microsoft.costmanagement-entity-retry-after", "120");
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(600));
        Assert.Equal(600, HttpHelper.ResolveRetryAfterSeconds(response, 0, costQuery: true));
        response.Headers.Add("x-ms-ratelimit-microsoft.consumption-retry-after", "1200");
        Assert.Equal(1200, HttpHelper.ResolveRetryAfterSeconds(response, 0, costQuery: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimestampedResponsesPreserveCostEvidenceAndRetryDeadline(bool throttled)
    {
        var clock = new TestClock();
        using var coordinator = new CostQueryCoordinator(clock);
        const string timestamp = "Current UTC time: 2026-01-01 00:00:00\n";
        var result = await coordinator.ExecuteAsync("tenant", "request", token =>
        {
            if (throttled) coordinator.RecordThrottle("tenant", 300);
            return Task.FromResult(throttled
                ? "HTTP 429 TooManyRequests\n" + timestamp + "{\"error\":{\"code\":\"429\",\"message\":\"Please retry.\"}}"
                : "HTTP 200 OK\n" + timestamp + "{\"properties\":{\"rows\":[[42.73,\"USD\"]]}}");
        }, default);
        Assert.Contains(timestamp, result);
        var bodyStart = result.IndexOf(timestamp, StringComparison.Ordinal) + timestamp.Length;
        using var parsed = JsonDocument.Parse(result[bodyStart..]);
        var evidence = parsed.RootElement.GetProperty("_finops");
        Assert.Equal(throttled ? "not_available" : "queried", evidence.GetProperty("cacheStatus").GetString());
        if (throttled)
            Assert.Equal(clock.GetUtcNow().AddMinutes(5), evidence.GetProperty("retryAtUtc").GetDateTimeOffset());
        else
            Assert.Equal(JsonValueKind.Null, evidence.GetProperty("retryAtUtc").ValueKind);
    }

    [Fact]
    public async Task CancellingCooldownWaitReleasesTenantGateWithoutDispatch()
    {
        var clock = new TestClock();
        using var coordinator = new CostQueryCoordinator(clock);
        using var cancellation = new CancellationTokenSource();
        coordinator.RecordThrottle("tenant", 60);
        var calls = 0;
        Task<string> Send(CancellationToken token) { calls++; return Task.FromResult(Result); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.ExecuteAsync("tenant", "request", Send, cancellation.Token,
            (deadline, willRetry) =>
            {
                Assert.True(willRetry);
                cancellation.Cancel();
                return Task.CompletedTask;
            }));
        Assert.Equal(0, calls);
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.StartsWith("HTTP 200", await coordinator.ExecuteAsync("tenant", "request", Send, default).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task OnlyFinalThrottleBlocksTheTurnAndItStaysBlockedAfterDeadline()
    {
        var clock = new TestClock();
        using var coordinator = new CostQueryCoordinator(clock);
        var sessionId = Guid.NewGuid().ToString();
        Assert.True(TurnExecution.TryBegin(sessionId, 101, null, out var turn));
        try
        {
            using var context = new ToolExecutionContext(sessionId, 101, CancellationToken.None);
            coordinator.RecordThrottle("tenant", 1, final: false);
            Assert.False(turn.CostQueriesBlocked);
            coordinator.RecordThrottle("tenant", 60);
            Assert.True(turn.CostQueriesBlocked);
            clock.Advance(TimeSpan.FromSeconds(60));
            var calls = 0;
            var response = await coordinator.ExecuteAsync("tenant", "new-request", token => { calls++; return Task.FromResult(Result); }, default,
                (deadline, willRetry) => { Assert.False(willRetry); return Task.CompletedTask; });
            Assert.Contains("\"blockedForTurn\":true", response);
            Assert.Equal(0, calls);
        }
        finally
        {
            turn.ConfirmTerminal();
            await turn.FinishAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConsolidatedCostEvidenceRetainsProviderAndHostRetryDeadlines(bool providerResponse)
    {
        const string retryAt = "2026-01-01T00:05:00+00:00";
        var response = providerResponse
            ? "HTTP 429 TooManyRequests\nCurrent UTC time: 2026-01-01 00:00:00\n{\"error\":{\"code\":\"429\"},\"_finops\":{\"cacheStatus\":\"not_available\",\"retryAtUtc\":\"" + retryAt + "\"}}"
            : "HTTP 429 TooManyRequests\n{\"error\":{\"code\":\"CostManagementCooldown\"},\"retryAtUtc\":\"" + retryAt + "\"}";
        var evidence = AzureQueryTools.ReadCostSourceEvidence(response);
        Assert.Equal("not_available", evidence.GetProperty("cacheStatus").GetString());
        Assert.Equal(DateTimeOffset.Parse(retryAt), evidence.GetProperty("retryAtUtc").GetDateTimeOffset());
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan duration) => _now += duration;
    }
}