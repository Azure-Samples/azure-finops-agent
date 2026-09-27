using AzureFinOps.Dashboard.AI.Tools;
using Microsoft.Extensions.AI;

namespace AzureFinOps.Dashboard.Tests;

public class QueryFanOutTests
{
    private sealed class Probe
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Calls = new();
        public readonly int[] Counters = new int[2];
        public int Peak => Volatile.Read(ref Counters[1]);
    }

    private static (ProtectedTool Tool, Probe Probe) Create(Func<string, string>? respond = null)
    {
        var probe = new Probe();
        var inner = AIFunctionFactory.Create(async (string url, string method = "GET", string sql = "") =>
        {
            probe.Calls.Enqueue(url);
            var active = Interlocked.Increment(ref probe.Counters[0]);
            int observed;
            while ((observed = Volatile.Read(ref probe.Counters[1])) < active
                && Interlocked.CompareExchange(ref probe.Counters[1], active, observed) != observed) { }
            await Task.Delay(20);
            Interlocked.Decrement(ref probe.Counters[0]);
            return respond?.Invoke(url) ?? $"HTTP 200 OK\n{{\"url\":\"{url}\"}}";
        }, "QueryAzure");
        return (new ProtectedTool(inner), probe);
    }

    [Fact]
    public async Task SeveralUrlLinesAreOneCallWithBoundedConcurrencyAndUrlOrder()
    {
        var (tool, probe) = Create();
        var urls = Enumerable.Range(0, 10).Select(index => $"/subscriptions/s/providers/Microsoft.Compute/locations/r{index}/usages?api-version=2024-07-01").ToArray();

        var output = Assert.IsType<string>(await tool.InvokeAsync(new AIFunctionArguments { ["url"] = string.Join("\r\n\n", urls) }));

        Assert.Equal(10, probe.Calls.Count);
        Assert.InRange(probe.Peak, 2, 4);
        Assert.StartsWith("10 requests succeeded; results follow in url order.\n[1] " + urls[0] + "\n", output);
        Assert.True(output.IndexOf("[9] " + urls[8], StringComparison.Ordinal) < output.IndexOf("[10] " + urls[9], StringComparison.Ordinal));
        Assert.True(ProtectedTool.InspectEvidence(output) is { Success: true, Partial: false });
    }

    [Fact]
    public async Task JsonArraysOfUrlsAreAcceptedAndOneUrlStaysASingleCall()
    {
        var (tool, probe) = Create();

        var many = Assert.IsType<string>(await tool.InvokeAsync(new AIFunctionArguments { ["url"] = "[\"/a?api-version=1\", \"/b?api-version=1\"]" }));
        var one = await tool.InvokeAsync(new AIFunctionArguments { ["url"] = "[\"/only?api-version=1\"]" });

        Assert.StartsWith("2 requests succeeded", many);
        Assert.Equal(["/a?api-version=1", "/b?api-version=1", "/only?api-version=1"], probe.Calls.ToArray().Order());
        Assert.DoesNotContain("requests succeeded", one?.ToString());
    }

    [Fact]
    public async Task FailedRequestsAreUnknownAndNeverHideTheFailure()
    {
        var (partialTool, _) = Create(url => url == "/b" ? "HTTP 404 NotFound\n{\"error\":{\"code\":\"NoRegisteredProviderFound\"}}" : "HTTP 200 OK\n{}");
        var partial = Assert.IsType<string>(await partialTool.InvokeAsync(new AIFunctionArguments { ["url"] = "/a\n/b\n/c" }));
        var (failedTool, _) = Create(url => url == "/x" ? throw new InvalidOperationException("synthetic failure") : "HTTP 503 ServiceUnavailable\n{}");
        var failed = Assert.IsType<string>(await failedTool.InvokeAsync(new AIFunctionArguments { ["url"] = "/x\n/y" }));

        Assert.StartsWith("PARTIAL RESULT: 1 of 3 requests failed; a failed request is unknown, not empty.\n", partial);
        Assert.Contains("[2] /b\nHTTP 404 NotFound", partial);
        Assert.True(ProtectedTool.InspectEvidence(partial) is { Success: true, Partial: true });
        Assert.StartsWith("Error: all 2 requests failed.\n", failed);
        Assert.Contains("[1] /x\nError: synthetic failure", failed);
        Assert.False(ProtectedTool.InspectEvidence(failed).Success);
    }

    [Theory]
    [InlineData("POST", null, 2, false)]
    [InlineData("GET", "{\"query\":\"x\"}", 2, false)]
    [InlineData("GET", null, ProtectedTool.MaxUrls + 1, false)]
    [InlineData("GET", null, 2, true)]
    public async Task SeveralUrlsAreGetOnlyBoundedAndNeverOperations(string method, string? body, int count, bool operation)
    {
        var (tool, probe) = Create();
        var urls = Enumerable.Range(0, count).Select(index => operation && index == 1 ? "operation:abc" : $"/r{index}").ToArray();
        var arguments = new AIFunctionArguments { ["url"] = string.Join('\n', urls), ["method"] = method };
        if (body is not null) arguments["body"] = body;

        var output = Assert.IsType<string>(await tool.InvokeAsync(arguments));

        Assert.StartsWith("HTTP 400 BadRequest\n", output);
        Assert.EndsWith("No request was sent.", output);
        Assert.Empty(probe.Calls);
    }
}
