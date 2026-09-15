using System.Net;
using System.Text.Json;
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

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan duration) => _now += duration;
    }
}