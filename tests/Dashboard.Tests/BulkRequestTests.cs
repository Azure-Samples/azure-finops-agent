using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;

namespace Dashboard.Tests;

public sealed class BulkRequestTests
{
    [Theory]
    [InlineData("accepted", 202, 1)]
    [InlineData("failed", 200, 0)]
    [InlineData("awaitingApproval", 409, 1)]
    public async Task OperationStateOverridesHttpSuccess(string state, int code, int pending)
    {
        var result = await AzureQueryTools.ExecuteBulkAsync([new() { Method = "PUT" }], 1, false,
            (item, token) => Task.FromResult($"HTTP {code} Result\n" + JsonSerializer.Serialize(new { operationId = "synthetic", status = state })), default);
        using var document = JsonDocument.Parse(result);
        Assert.False(document.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal(0, document.RootElement.GetProperty("succeeded").GetInt32());
        Assert.Equal(pending, document.RootElement.GetProperty("pending").GetInt32());
        Assert.Equal(state, document.RootElement.GetProperty("results")[0].GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task SuccessfulReadsRetainEveryQuotaBody()
    {
        var items = Enumerable.Range(0, 28).Select(index => new AzureQueryTools.BulkRequestItem { Path = $"/synthetic/{index}" }).ToArray();
        var result = await AzureQueryTools.ExecuteBulkAsync(items, 8, false,
            (item, token) => Task.FromResult("HTTP 200 OK\n{\"limit\":100,\"currentValue\":40}"), default);
        using var document = JsonDocument.Parse(result);
        var rows = document.RootElement.GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(28, rows.Length);
        Assert.True(document.RootElement.GetProperty("complete").GetBoolean());
        Assert.All(rows, row => Assert.Equal(100, row.GetProperty("body").GetProperty("limit").GetInt32()));
        Assert.Equal(Enumerable.Range(0, 28), rows.Select(row => row.GetProperty("index").GetInt32()));
    }

    [Fact]
    public async Task StopOnFailureReturnsAnIndexedPartialSummary()
    {
        var calls = 0;
        var items = Enumerable.Range(0, 4).Select(_ => new AzureQueryTools.BulkRequestItem()).ToArray();
        var result = await AzureQueryTools.ExecuteBulkAsync(items, 1, true,
            (item, token) => { calls++; return Task.FromResult("HTTP 403 Forbidden\n{\"error\":\"denied\"}"); }, default);
        using var document = JsonDocument.Parse(result);
        Assert.Equal(1, calls);
        Assert.Equal(3, document.RootElement.GetProperty("unattempted").GetInt32());
        Assert.True(document.RootElement.GetProperty("stopped").GetBoolean());
        Assert.False(document.RootElement.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task OversizedBodiesAreExplicitlyPartial()
    {
        var result = await AzureQueryTools.ExecuteBulkAsync([new()], 1, false,
            (item, token) => Task.FromResult("HTTP 200 OK\n" + JsonSerializer.Serialize(new { text = new string('x', 13000) })), default);
        using var document = JsonDocument.Parse(result);
        Assert.False(document.RootElement.GetProperty("complete").GetBoolean());
        Assert.True(document.RootElement.GetProperty("results")[0].GetProperty("partial").GetBoolean());
    }

    [Theory]
    [InlineData("query")]
    [InlineData("forecast")]
    public async Task CostBatchesRemainSequentialEvenWhenParallelismIsRequested(string operation)
    {
        var items = Enumerable.Range(0, 3).Select(index => new AzureQueryTools.BulkRequestItem
        {
            Method = "post",
            Path = $"/subscriptions/synthetic-{index}/providers/Microsoft.CostManagement/{operation}?api-version=2026-08-01"
        }).ToArray();
        var active = 0;
        var maximum = 0;
        var calls = new List<string>();
        var result = await AzureQueryTools.ExecuteBulkAsync(items, 50, false, async (item, token) =>
        {
            var concurrent = Interlocked.Increment(ref active);
            lock (calls)
            {
                maximum = Math.Max(maximum, concurrent);
                calls.Add(item.Path);
            }
            await Task.Delay(10, token);
            Interlocked.Decrement(ref active);
            return "HTTP 200 OK\n{\"properties\":{\"rows\":[]},\"_finops\":{\"cacheStatus\":\"queried\"}}";
        }, default);

        using var document = JsonDocument.Parse(result);
        Assert.Equal(1, maximum);
        Assert.Equal(items.Select(item => item.Path), calls);
        Assert.True(document.RootElement.GetProperty("complete").GetBoolean());
        Assert.All(document.RootElement.GetProperty("results").EnumerateArray(), item =>
            Assert.Equal("queried", item.GetProperty("sourceEvidence").GetProperty("cacheStatus").GetString()));
    }

    [Fact]
    public async Task FinalCostThrottleStopsTheBatchWithoutOptingIntoStopOnFirstError()
    {
        const string retryAt = "2026-01-01T00:05:00Z";
        var calls = 0;
        var items = new[]
        {
            new AzureQueryTools.BulkRequestItem { Method = "POST", Path = "/subscriptions/one/providers/Microsoft.CostManagement/query" },
            new AzureQueryTools.BulkRequestItem { Method = "POST", Path = "/subscriptions/two/providers/Microsoft.CostManagement/forecast" },
            new AzureQueryTools.BulkRequestItem { Method = "GET", Path = "/subscriptions/three/resources" }
        };
        var result = await AzureQueryTools.ExecuteBulkAsync(items, 20, false, (item, token) =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? "HTTP 200 OK\n{\"_finops\":{\"cacheStatus\":\"queried\"}}"
                : "HTTP 429 TooManyRequests\n" + JsonSerializer.Serialize(new
                {
                    error = "throttled",
                    _finops = new { cacheStatus = "not_available", retryAtUtc = retryAt }
                }));
        }, default);

        using var document = JsonDocument.Parse(result);
        Assert.Equal(2, calls);
        Assert.True(document.RootElement.GetProperty("stopped").GetBoolean());
        Assert.False(document.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal(1, document.RootElement.GetProperty("unattempted").GetInt32());
        Assert.Equal(retryAt, document.RootElement.GetProperty("results")[1].GetProperty("sourceEvidence").GetProperty("retryAtUtc").GetString());
        Assert.Equal((false, false, true), ProtectedTool.InspectEvidence(result));
    }

    [Fact]
    public async Task OmittedCostBodiesKeepFreshnessAndFullRetryDeadline()
    {
        const string retryAt = "2026-01-01T00:30:00Z";
        var result = await AzureQueryTools.ExecuteBulkAsync(
            [new() { Method = "POST", Path = "/subscriptions/one/providers/Microsoft.CostManagement/query" }],
            1, false, (item, token) => Task.FromResult("HTTP 200 OK\n" + JsonSerializer.Serialize(new
            {
                padding = new string('x', 13000),
                _finops = new { cacheStatus = "stale_during_cooldown", retryAtUtc = retryAt }
            })), default);

        using var document = JsonDocument.Parse(result);
        var row = document.RootElement.GetProperty("results")[0];
        Assert.Equal(JsonValueKind.Null, row.GetProperty("body").ValueKind);
        Assert.True(row.GetProperty("partial").GetBoolean());
        Assert.Equal(retryAt, row.GetProperty("sourceEvidence").GetProperty("retryAtUtc").GetString());
        Assert.Equal((false, false, true), ProtectedTool.InspectEvidence(result));
    }

    [Fact]
    public async Task NonCostThrottleStillRespectsTheCallersStopPolicy()
    {
        var calls = 0;
        var result = await AzureQueryTools.ExecuteBulkAsync([new(), new()], 1, false,
            (item, token) => { calls++; return Task.FromResult("HTTP 429 TooManyRequests\n{}"); }, default);
        using var document = JsonDocument.Parse(result);
        Assert.Equal(2, calls);
        Assert.False(document.RootElement.GetProperty("stopped").GetBoolean());
    }

    [Fact]
    public async Task BatchBudgetDoesNotDiscardCostSourceEvidence()
    {
        const string retrievedAt = "2026-01-01T00:00:00Z";
        var items = Enumerable.Range(0, 9).Select(index => new AzureQueryTools.BulkRequestItem
        {
            Method = "POST",
            Path = $"/subscriptions/synthetic-{index}/providers/Microsoft.CostManagement/query"
        }).ToArray();
        var result = await AzureQueryTools.ExecuteBulkAsync(items, 20, false,
            (item, token) => Task.FromResult("HTTP 200 OK\n" + JsonSerializer.Serialize(new
            {
                padding = new string('x', 11000),
                _finops = new { cacheStatus = "cached", retrievedAtUtc = retrievedAt }
            })), default);

        using var document = JsonDocument.Parse(result);
        var rows = document.RootElement.GetProperty("results").EnumerateArray().ToArray();
        Assert.False(document.RootElement.GetProperty("complete").GetBoolean());
        Assert.Contains(rows, row => row.GetProperty("body").ValueKind == JsonValueKind.Null
            && row.GetProperty("partial").GetBoolean());
        Assert.All(rows, row => Assert.Equal(retrievedAt,
            row.GetProperty("sourceEvidence").GetProperty("retrievedAtUtc").GetString()));
        Assert.False(ProtectedTool.InspectEvidence(result).Fresh);
    }
}