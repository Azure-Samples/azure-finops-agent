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
}