using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;

namespace AzureFinOps.Dashboard.Tests;

public class QueryFanOutTests
{
    private sealed class Probe
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Calls = new();
        public readonly int[] Counters = new int[2];
        public int Peak => Volatile.Read(ref Counters[1]);
    }

    private static (Func<string, CancellationToken, Task<string>> Send, Probe Probe) Create(Func<string, string>? respond = null)
    {
        var probe = new Probe();
        return (async (url, _) =>
        {
            probe.Calls.Enqueue(url);
            var active = Interlocked.Increment(ref probe.Counters[0]);
            int observed;
            while ((observed = Volatile.Read(ref probe.Counters[1])) < active
                && Interlocked.CompareExchange(ref probe.Counters[1], active, observed) != observed) { }
            await Task.Delay(20);
            Interlocked.Decrement(ref probe.Counters[0]);
            return respond?.Invoke(url) ?? $"HTTP 200 OK\n{{\"url\":\"{url}\"}}";
        }, probe);
    }

    private static Task<string> FanOut(string url, Func<string, CancellationToken, Task<string>> send, string method = "GET", string? body = null, string query = "", string combine = "") =>
        AzureQueryTools.FanOutAsync(AzureQueryTools.Urls(url), method, body is null ? null : JsonDocument.Parse(body).RootElement.Clone(), query, send, CancellationToken.None, combine);

    [Fact]
    public async Task SeveralUrlLinesAreOneCallWithBoundedConcurrencyAndUrlOrder()
    {
        var (send, probe) = Create();
        var urls = Enumerable.Range(0, 10).Select(index => $"/subscriptions/s/providers/Microsoft.Compute/locations/r{index}/usages?api-version=2024-07-01").ToArray();

        var output = await FanOut(string.Join("\r\n\n", urls), send);

        Assert.Equal(10, probe.Calls.Count);
        Assert.InRange(probe.Peak, 2, 4);
        Assert.StartsWith("10 requests succeeded; results follow in url order.\n[1] " + urls[0] + "\n", output);
        Assert.True(output.IndexOf("[9] " + urls[8], StringComparison.Ordinal) < output.IndexOf("[10] " + urls[9], StringComparison.Ordinal));
        Assert.True(EvidenceInspector.Inspect(output) is { Success: true, Partial: false });
    }

    [Fact]
    public void JsonArraysOfUrlsAreAccepted()
    {
        Assert.Equal(["/a?api-version=1", "/b?api-version=1"], AzureQueryTools.Urls("[\"/a?api-version=1\", \" /b?api-version=1 \"]"));
        Assert.Equal(["/only?api-version=1"], AzureQueryTools.Urls("[\"/only?api-version=1\"]"));
        Assert.Equal(["/a", "/b"], AzureQueryTools.Urls(" /a \r\n\n/b\n"));
    }

    [Fact]
    public async Task FailedRequestsAreUnknownAndNeverHideTheFailure()
    {
        var (partialSend, _) = Create(url => url == "/b" ? "HTTP 404 NotFound\n{\"error\":{\"code\":\"NoRegisteredProviderFound\"}}" : "HTTP 200 OK\n{}");
        var partial = await FanOut("/a\n/b\n/c", partialSend);
        var (failedSend, _) = Create(url => url == "/x" ? throw new InvalidOperationException("synthetic failure") : "HTTP 503 ServiceUnavailable\n{}");
        var failed = await FanOut("/x\n/y", failedSend);

        Assert.StartsWith("PARTIAL RESULT: 1 of 3 requests failed; a failed request is unknown, not empty.\n", partial);
        Assert.Contains("[2] /b\nHTTP 404 NotFound", partial);
        Assert.True(EvidenceInspector.Inspect(partial) is { Success: true, Partial: true });
        Assert.StartsWith("Error: all 2 requests failed.\n", failed);
        Assert.Contains("[1] /x\nError: synthetic failure", failed);
        Assert.False(EvidenceInspector.Inspect(failed).Success);
    }

    // Four storage accounts without a lifecycle policy were reported as "unconfirmed" instead of "none".
    [Fact]
    public async Task A404ThatSaysTheItemDoesNotExistIsNamedAsAbsent()
    {
        var (send, _) = Create(url => url == "/a"
            ? "HTTP 200 OK\n{\"name\":\"DefaultManagementPolicy\"}"
            : url == "/d"
                ? "HTTP 404 NotFound\n{\"error\":{\"code\":\"InvalidResourceType\",\"message\":\"synthetic\"}}"
                : "HTTP 404 NotFound\n{\"error\":{\"code\":\"ManagementPolicyNotFound\",\"message\":\"No ManagementPolicy found for account synthetic\"}}");

        var output = await FanOut("/a\n/b\n/c\n/d", send);

        Assert.StartsWith("PARTIAL RESULT: 3 of 4 requests failed; a failed request is unknown, not empty. 2 of them are 404s that say the requested item does not exist, which shows it is absent.\n", output);
        Assert.True(AzureQueryTools.IsAbsent("HTTP 404 NotFound\n{\"error\":{\"code\":\"ResourceNotFound\",\"message\":\"x\"}}"));
        Assert.False(AzureQueryTools.IsAbsent("HTTP 404 NotFound\n{\"error\":{\"code\":\"SubscriptionNotFound\",\"message\":\"x\"}}"));
        Assert.False(AzureQueryTools.IsAbsent("HTTP 403 Forbidden\n{\"error\":{\"code\":\"ResourceNotFound\"}}"));
        Assert.False(AzureQueryTools.IsAbsent("HTTP 404 NotFound\nnot json"));
        // A bare NotFound is also what a wrong path returns, so it proves nothing about the item.
        Assert.False(AzureQueryTools.IsAbsent("HTTP 404 NotFound\n{\"error\":{\"code\":\"NotFound\",\"message\":\"x\"}}"));
        Assert.True(AzureQueryTools.IsAbsent("HTTP 404 NotFound\n{\"error\":{\"code\":\"Request_ResourceNotFound\",\"message\":\"x\"}}"));
    }

    [Fact]
    public async Task DeniedRegionsAndIncompletePagesStayUnknownOrPartial()
    {
        const string eastus = "/subscriptions/s/providers/Microsoft.App/locations/eastus/availableManagedEnvironmentsWorkloadProfileTypes";
        const string westus3 = "/subscriptions/s/providers/Microsoft.App/locations/westus3/availableManagedEnvironmentsWorkloadProfileTypes";
        var (send, _) = Create(url => url == westus3
            ? "HTTP 403 Forbidden\n{\"error\":{\"code\":\"AuthorizationFailed\",\"message\":\"synthetic\"}}"
            : "HTTP 200 OK\n{\"value\":[{\"name\":\"Consumption\"}],\"nextLink\":\"https://management.azure.com/next\",\"pagesRead\":10,\"complete\":false}");

        var output = await FanOut(eastus + "\n" + westus3, send, query: "value.Select(p => p.name)");

        Assert.StartsWith("PARTIAL RESULT: 1 of 2 requests failed; a failed request is unknown, not empty.\n", output);
        Assert.Contains("[1] " + eastus + "\nHTTP 200 OK\nCoverage: {", output);
        Assert.Contains("\"complete\":false", output);
        Assert.Contains("[2] " + westus3 + "\nHTTP 403 Forbidden", output);
        Assert.True(EvidenceInspector.Inspect(output) is { Success: true, Partial: true });
    }

    [Fact]
    public async Task QueryRunsOnEachResponseAndRepeatedSchemasAreSharedByReference()
    {
        var (send, _) = Create(url => $"HTTP 200 OK\n{{\"value\":[{{\"name\":\"{url.Trim('/')}\",\"limit\":{new string('9', 5)}}}],\"pad\":\"{new string('x', AzureQueryTools.InlineCharacters)}\"}}");

        var cropped = await FanOut("/a\n/b", send, query: "value.Select(x => x.name)");
        var schemas = await FanOut("/a\n/b\n/c", send);

        Assert.Contains("[1] /a\nHTTP 200 OK\nQuery result:\n[\"a\"]", cropped);
        Assert.Contains("[2] /b\nHTTP 200 OK\nQuery result:\n[\"b\"]", cropped);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(schemas, "Schema:\n[^t]"));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(schemas, @"the same schema as \[\d\]").Count);
    }

    [Theory]
    [InlineData("DELETE", null, 2)]
    [InlineData("GET", "{\"query\":\"x\"}", 2)]
    [InlineData("GET", null, AzureQueryTools.MaxUrls + 1)]
    public async Task SeveralUrlsAreGetOrPostWithOneBodyAndBounded(string method, string? body, int count)
    {
        var (send, probe) = Create();
        var urls = Enumerable.Range(0, count).Select(index => $"/r{index}");

        var output = await FanOut(string.Join('\n', urls), send, method, body);

        Assert.StartsWith("HTTP 400 BadRequest\n", output);
        Assert.EndsWith("No request was sent.", output);
        Assert.Empty(probe.Calls);
    }

    // Per-subscription cost queries were separate calls whose figures the model then retyped into a calculation round
    // (one session: 30 queries and five calculation rounds). One call now sends the shared body to every url line.
    [Fact]
    public async Task SeveralPostUrlsShareOneBodyAndRunQueryOnEach()
    {
        var (send, probe) = Create(url => $"HTTP 200 OK\n{{\"properties\":{{\"columns\":[{{\"name\":\"PreTaxCost\"}},{{\"name\":\"ServiceName\"}}],\"rows\":[[{(url.Contains("/a/") ? 10 : 5)},\"Storage\"]]}}}}");

        var output = await FanOut("/subscriptions/a/providers/Microsoft.CostManagement/query\n/subscriptions/b/providers/Microsoft.CostManagement/query",
            send, "POST", "{\"type\":\"ActualCost\"}", query: "properties.rows.Sum(r => r.PreTaxCost)");

        Assert.Equal(2, probe.Calls.Count);
        Assert.StartsWith("2 requests succeeded; results follow in url order.\n", output);
        Assert.Contains("Query result:\n10", output);
        Assert.Contains("Query result:\n5", output);
    }

    [Fact]
    public async Task CombineRunsOnceOverAllResponsesInUrlOrder()
    {
        var (send, _) = Create(url => url switch
        {
            "/a" => "HTTP 200 OK\nCurrent UTC time: 2026-10-17 09:00:00\n{\"properties\":{\"columns\":[{\"name\":\"PreTaxCost\"},{\"name\":\"ServiceName\"}],\"rows\":[[10.5,\"Storage\"],[4,\"Compute\"]]}}",
            _ => "HTTP 200 OK\nCurrent UTC time: 2026-10-17 09:00:01\n{\"properties\":{\"columns\":[{\"name\":\"PreTaxCost\"},{\"name\":\"ServiceName\"}],\"rows\":[[1.25,\"Storage\"]]},\"complete\":true}",
        });

        var output = await FanOut("/a\n/b", send, combine:
            "new { total = Math.Round(responses.SelectMany(r => r.properties.rows).Sum(x => x.PreTaxCost ?? 0), 2), byService = responses.SelectMany(r => r.properties.rows).GroupBy(x => x.ServiceName).Select(g => new { service = g.Key, cost = g.Sum(x => x.PreTaxCost ?? 0) }).OrderByDescending(x => x.cost), first = responses[0].properties.rows.Count() }");

        Assert.StartsWith("2 requests succeeded; combine ran once over all of them.\n[1] /a\nHTTP 200 OK\nCurrent UTC time: 2026-10-17 09:00:00\n\n[2] /b\nHTTP 200 OK\n", output);
        Assert.Contains("Coverage: {\"complete\":true}", output);
        Assert.EndsWith("Combined result:\n{\"total\":15.75,\"byService\":[{\"service\":\"Storage\",\"cost\":11.75},{\"service\":\"Compute\",\"cost\":4}],\"first\":2}", output);
        Assert.DoesNotContain("\"rows\"", output);
        Assert.True(EvidenceInspector.Inspect(output) is { Success: true, Partial: false });
    }

    [Fact]
    public async Task CombineOverPartOfTheResponsesIsPartialAndKeepsTheFailure()
    {
        var missing = AzureFinOps.Dashboard.Infrastructure.HttpHelper.TokenMissing("LogAnalyticsToken", null, "test");
        var (send, _) = Create(url => url switch
        {
            "/a" => "HTTP 200 OK\n{\"value\":[{\"cost\":3}]}",
            "/b" => "HTTP 403 Forbidden\n{\"error\":{\"code\":\"AuthorizationFailed\",\"message\":\"synthetic\"}}",
            _ => missing,
        });

        var output = await FanOut("/a\n/b\n/c", send, combine: "new { total = responses.SelectMany(r => r.value).Sum(x => x.cost ?? 0), failed = responses.Count(r => r == null) }");

        Assert.StartsWith("PARTIAL RESULT: 2 of 3 requests failed; a failed request is unknown, not empty, and combine ran over the other 1 only.\n", output);
        Assert.Contains("[2] /b\nHTTP 403 Forbidden", output);
        Assert.EndsWith("Combined result:\n{\"total\":3,\"failed\":2}", output);
        Assert.True(EvidenceInspector.Inspect(output) is { Success: true, Partial: true });
        // The consent button a failed url asked for is still offered.
        Assert.Single(AzureFinOps.Dashboard.Infrastructure.HttpHelper.ConsentActions(output));
    }

    [Fact]
    public async Task CombineFailsLikeAQueryAndNeverHidesFailures()
    {
        var (okSend, _) = Create(url => "HTTP 200 OK\n{\"value\":[{\"cost\":3}]}");
        var (failedSend, _) = Create(url => "HTTP 503 ServiceUnavailable\n{}");

        var broken = await FanOut("/a\n/b", okSend, combine: "responses.Sum(r => r.missing.cost");
        var both = await FanOut("/a\n/b", okSend, query: "value.Count()", combine: "responses.Count()");
        var allFailed = await FanOut("/a\n/b", failedSend, combine: "responses.Count()");

        Assert.StartsWith("Error: the combine query failed: ", broken);
        Assert.Contains("Correct combine against this schema and repeat the request.", broken);
        Assert.Contains("Schema:\nit: ", broken);
        Assert.False(EvidenceInspector.Inspect(broken).Success);
        Assert.StartsWith("HTTP 400 BadRequest\nUse query (run on each response) or combine", both);
        Assert.StartsWith("Error: all 2 requests failed.\n[1] /a\nHTTP 503", allFailed);
        Assert.DoesNotContain("Combined result", allFailed);
    }
}