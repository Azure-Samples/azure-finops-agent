using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using AzureFinOps.Dashboard.AI;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Infrastructure;
using GitHub.Copilot;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

#pragma warning disable GHCP001

namespace Dashboard.Tests;

public sealed class RuntimeProtocolTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RuntimeCallsOnlyRegisteredToolsAndSupportsAbort(bool abort, bool largeResult)
    {
        var evidence = largeResult ? "synthetic evidence " + new string('x', 60000) + " end-of-full-evidence" : "synthetic evidence";
        var root = Path.Combine(Path.GetTempPath(), "finops-runtime-test-" + Guid.NewGuid().ToString("N"));
        var requests = new ConcurrentQueue<JsonElement>();
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        var costRequests = new ConcurrentQueue<long>();
        var retryNotices = new ConcurrentQueue<HttpHelper.RetryNotice>();
        var retryTurnStates = new ConcurrentQueue<(bool Completed, bool CostBlocked)>();
        string? retryReporterKey = null;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        server.MapPost("/v1/responses", async (HttpContext context) =>
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body);
            requests.Enqueue(body.RootElement.Clone());
            var first = Interlocked.Increment(ref requestCount) == 1;
            var streaming = body.RootElement.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.True;
            await WriteResponse(context.Response, first, streaming);
        });
        server.MapPost("/providers/Microsoft.CostManagement/query", async (HttpContext context) =>
        {
            costRequests.Enqueue(Stopwatch.GetTimestamp());
            if (costRequests.Count == 1)
            {
                context.Response.StatusCode = 429;
                context.Response.Headers.RetryAfter = "1";
                await context.Response.WriteAsJsonAsync(new { error = new { code = "429", message = "Synthetic throttle" } });
            }
            else
                await context.Response.WriteAsJsonAsync(new { properties = new { rows = new[] { new object[] { 42.73, "USD" } } } });
        });
        await server.StartAsync();
        try
        {
            await using var client = new CopilotClient(new CopilotClientOptions
            {
                Mode = CopilotClientMode.Empty,
                BaseDirectory = root,
                UseLoggedInUser = false
            });
            await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
            async Task<string> ApprovedRead(CancellationToken cancellationToken)
            {
                called.TrySetResult();
                if (abort)
                {
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                    catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
                }
                var costResponse = await HttpHelper.SendWithRetryAsync(
                    server.Urls.Single() + "/providers/Microsoft.CostManagement/query", root, null, "synthetic-cost",
                    HttpMethod.Post, "{}", includeTimestamp: true, cancellationToken: cancellationToken);
                Assert.StartsWith("HTTP 200", costResponse);
                Assert.Contains("\"cacheStatus\":\"queried\"", costResponse);
                return evidence;
            }
            var config = new SessionConfig
            {
                SessionId = Guid.NewGuid().ToString(),
                Model = "synthetic-test-model",
                Streaming = true,
                WorkingDirectory = root,
                Provider = new ProviderConfig
                {
                    Type = "openai",
                    BaseUrl = server.Urls.Single() + "/v1/",
                    ApiKey = "synthetic-test-only",
                    WireApi = "responses"
                },
            };
            config.Tools = [new ProtectedTool(new InvocationProbe(DeferredTool.Wrap(AIFunctionFactory.Create(ApprovedRead, "ApprovedRead"))), 101, config.SessionId)];
            RuntimePolicy.Apply(config);
            await using var session = await client.CreateSessionAsync(config).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(TurnExecution.TryBegin(session.SessionId, 101, session, out var turn));
            retryReporterKey = "101:" + session.SessionId;
            HttpHelper.RetryReporters[retryReporterKey] = notice =>
            {
                retryNotices.Enqueue(notice);
                retryTurnStates.Enqueue((turn.Completion.Task.IsCompleted, turn.CostQueriesBlocked));
                return Task.CompletedTask;
            };
            var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var errors = new ConcurrentQueue<string>();
            var observed = new ConcurrentQueue<string>();
            using var subscription = session.On<SessionEvent>(item =>
            {
                observed.Enqueue(item.GetType().Name);
                if (item is SessionIdleEvent) idle.TrySetResult();
                if (item is SessionErrorEvent error)
                {
                    errors.Enqueue(error.Data.Message);
                    idle.TrySetException(new InvalidOperationException("Synthetic provider runtime error: " + error.Data.Message));
                }
            });
            var imagePath = Path.Combine(root, "synthetic.png");
            await File.WriteAllBytesAsync(imagePath, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jG3kAAAAASUVORK5CYII="));
            await session.SendAsync(new MessageOptions
            {
                Prompt = "Read the synthetic evidence with ApprovedRead.",
                Attachments = [new AttachmentFile { Path = imagePath, DisplayName = "synthetic.png", MimeType = "image/png" }]
            });
            await called.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (abort)
            {
                Assert.True(await turn.AbortAsync());
                await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(15));
            }
            else
            {
                try { await idle.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
                catch (TimeoutException)
                {
                    throw new Xunit.Sdk.XunitException(JsonSerializer.Serialize(new
                    {
                        error = "Runtime did not reach idle after the synthetic tool returned.",
                        providerRequests = requests.Count,
                        events = observed.ToArray(),
                        errors = errors.ToArray(),
                        hostToolsCompleted = turn.ToolsCompleted,
                        hostToolsFailed = turn.ToolsFailed,
                        cancellationReason = turn.CancellationReason
                    }));
                }
                Assert.Empty(errors);
                Assert.Equal(2, costRequests.Count);
                var costTimes = costRequests.ToArray();
                Assert.True(Stopwatch.GetElapsedTime(costTimes[0], costTimes[1]) >= TimeSpan.FromSeconds(1));
                var retry = Assert.Single(retryNotices);
                Assert.True(retry.WillRetry);
                Assert.NotNull(retry.RetryAtUtc);
                Assert.Equal((false, false), Assert.Single(retryTurnStates));
                var events = await session.GetEventsAsync();
                Assert.Contains(events.OfType<AssistantMessageEvent>(), item => item.Data.Content.Contains("Synthetic result"));
                Assert.Contains(events.OfType<ToolExecutionCompleteEvent>(), item => item.Data.Result?.Content == evidence);
                Assert.Contains(evidence, requests.ElementAt(1).GetRawText());
                Assert.DoesNotContain("Output too large to read at once", requests.ElementAt(1).GetRawText());
                var compacted = await session.Rpc.History.CompactAsync(new GitHub.Copilot.Rpc.SessionHistoryCompactRequest()).WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(compacted.Success);
                Assert.True(compacted.MessagesRemoved > 0);
                var retained = await session.GetEventsAsync();
                Assert.Contains(retained.OfType<UserMessageEvent>(), item => item.Data.Content.Contains("Read the synthetic evidence"));
                Assert.True(await turn.FinishAsync());
                idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Assert.True(TurnExecution.TryBegin(session.SessionId, 101, session, out turn));
                await session.SendAsync(new MessageOptions { Prompt = "Use only this new synthetic context." });
                await idle.Task.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Empty(errors);
                Assert.Contains("Use only this new synthetic context", requests.Last().GetRawText());
            }
            Assert.True(await turn.FinishAsync());
            Assert.NotEmpty(requests);
            Assert.Contains("data:image/png;base64,", requests.First().GetRawText());
            foreach (var request in requests)
            {
                if (!request.TryGetProperty("tools", out var toolArray)) continue;
                foreach (var tool in toolArray.EnumerateArray())
                    Assert.Equal("ApprovedRead", tool.GetProperty("name").GetString());
            }
            await session.DisposeAsync();
            var resumeConfig = new ResumeSessionConfig
            {
                Model = config.Model,
                Provider = config.Provider,
                Streaming = true,
                Tools = config.Tools,
                WorkingDirectory = root
            };
            RuntimePolicy.Apply(resumeConfig);
            await using var resumed = await client.ResumeSessionAsync(config.SessionId, resumeConfig);
            Assert.Contains((await resumed.GetEventsAsync()).OfType<UserMessageEvent>(), item => item.Data.Content.Contains("Read the synthetic evidence"));
            await resumed.DisposeAsync();
            await client.DeleteSessionAsync(config.SessionId);
        }
        finally
        {
            if (retryReporterKey is not null) HttpHelper.RetryReporters.TryRemove(retryReporterKey, out _);
            await server.StopAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public async Task RuntimeDeliversAChartUsingTheRetainedAliasContract(bool resume, bool otherSession, bool delayedEventDelivery)
    {
        var root = Path.Combine(Path.GetTempPath(), "finops-chart-protocol-" + Guid.NewGuid().ToString("N"));
        var calls = 0;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        server.MapPost("/v1/responses", async context =>
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body);
            var streaming = body.RootElement.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.True;
            await WriteResponse(context.Response, Interlocked.Increment(ref calls) == 1, streaming, "RenderChart",
                """{"chart":"bar","title":"Synthetic chart","seriesName":"Count","data":"[[\"A\",10],[\"B\",20]]"}""");
        });
        await server.StartAsync();
        try
        {
            await using var client = new CopilotClient(new CopilotClientOptions
            {
                Mode = CopilotClientMode.Empty, BaseDirectory = root, UseLoggedInUser = false
            });
await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
var config = new SessionConfig
{
    SessionId = Guid.NewGuid().ToString(),
    Model = "synthetic-test-model",
    Streaming = true,
    WorkingDirectory = root,
    Provider = new ProviderConfig { Type = "openai", BaseUrl = server.Urls.Single() + "/v1/", ApiKey = "synthetic-test-only", WireApi = "responses" }
};
config.Tools = [new ProtectedTool(ChartTools.Create().Single(tool => tool.Name == "RenderChart"), 101, config.SessionId)];
RuntimePolicy.Apply(config);
var created = await client.CreateSessionAsync(config);
var resumeConfig = new ResumeSessionConfig
{
    Model = config.Model,
    Provider = config.Provider,
    Streaming = true,
    Tools = config.Tools,
    WorkingDirectory = root
};
RuntimePolicy.Apply(resumeConfig);
if (resume)
{
    Interlocked.Exchange(ref calls, 1);
    var seeded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var seededSubscription = created.On<SessionIdleEvent>(_ => seeded.TrySetResult());
    await created.SendAsync(new MessageOptions { Prompt = "Store this synthetic context for a resumed tool call." });
    await seeded.Task.WaitAsync(TimeSpan.FromSeconds(30));
    await created.DisposeAsync();
    Interlocked.Exchange(ref calls, 0);
}
await using var session = resume ? await client.ResumeSessionAsync(config.SessionId, resumeConfig) : created;
var otherConfig = new SessionConfig
{
    SessionId = Guid.NewGuid().ToString(),
    Model = config.Model,
    Provider = config.Provider,
    Streaming = true,
    WorkingDirectory = root
};
otherConfig.Tools = [new ProtectedTool(ChartTools.Create().Single(tool => tool.Name == "RenderChart"), 202, otherConfig.SessionId)];
RuntimePolicy.Apply(otherConfig);
await using var other = otherSession ? await client.CreateSessionAsync(otherConfig) : null;
using var delayedEvents = session.On<AssistantMessageEvent>(_ =>
{
    if (delayedEventDelivery) Task.Delay(TimeSpan.FromMilliseconds(250)).GetAwaiter().GetResult();
});
Assert.True(TurnExecution.TryBegin(session.SessionId, 101, session, out var turn));
try
{
    await session.SendAsync(new MessageOptions { Prompt = "Render the two synthetic counts." });
    await turn.Terminal.Task.WaitAsync(TimeSpan.FromSeconds(30));
    var completed = Assert.Single((await session.GetEventsAsync()).OfType<ToolExecutionCompleteEvent>());
    Assert.True(completed.Data.Success);
    using var chart = JsonDocument.Parse(completed.Data.Result!.Content!);
    Assert.Equal("bar", chart.RootElement.GetProperty("type").GetString());
    Assert.Equal(0, turn.ToolsFailed);
    Assert.True(turn.HasUserOutput);
}
finally { await turn.FinishAsync(); }
        }
        finally
{
    await server.StopAsync();
    if (Directory.Exists(root)) Directory.Delete(root, true);
}
    }

    [Fact]
public async Task RuntimeQueriesLargeSourceAfterDiscoveringItsSchema()
{
    var root = Path.Combine(Path.GetTempPath(), "finops-query-protocol-" + Guid.NewGuid().ToString("N"));
    var calls = 0;
    var observedResults = new ConcurrentQueue<JsonElement>();
    var source = JsonSerializer.Serialize(new
    {
        complete = false,
        rows = Enumerable.Range(0, 1000).Select(index => new { region = "region" + index, value = index, detail = new string('x', 100) })
    });
    var builder = WebApplication.CreateBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    await using var server = builder.Build();
    server.MapPost("/v1/responses", async context =>
    {
        using var request = await JsonDocument.ParseAsync(context.Request.Body);
        var streaming = request.RootElement.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.True;
        var stage = Interlocked.Increment(ref calls);
        if (stage == 1) { await WriteResponse(context.Response, true, streaming, "QueryAzure"); return; }
        var output = request.RootElement.GetProperty("input").EnumerateArray()
            .Last(item => item.GetProperty("type").GetString() == "function_call_output").GetProperty("output").GetString()!;
        using var result = JsonDocument.Parse(output);
        observedResults.Enqueue(result.RootElement.Clone());
        if (stage == 2)
        {
            var resultId = result.RootElement.GetProperty("resultId").GetString();
            var queryJson = JsonSerializer.Serialize(new { path = "$.rows[?(@.value == 999)]", select = new { region = "$.region", value = "$.value" } });
            await WriteResponse(context.Response, true, streaming, "QueryToolResult", JsonSerializer.Serialize(new { resultId, queryJson }), "call_query");
        }
        else await WriteResponse(context.Response, false, streaming);
    });
    await server.StartAsync();
    try
    {
        await using var client = new CopilotClient(new CopilotClientOptions { Mode = CopilotClientMode.Empty, BaseDirectory = root, UseLoggedInUser = false });
        await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var config = new SessionConfig
        {
            SessionId = Guid.NewGuid().ToString(),
            Model = "synthetic-test-model",
            Streaming = true,
            WorkingDirectory = root,
            Provider = new ProviderConfig { Type = "openai", BaseUrl = server.Urls.Single() + "/v1/", ApiKey = "synthetic-test-only", WireApi = "responses" }
        };
        config.Tools = [new ProtectedTool(AIFunctionFactory.Create(() => source, "QueryAzure"), 101, config.SessionId),
            new ProtectedTool(new ToolResultQueryTools(101).Create().Single(), 101, config.SessionId)];
        RuntimePolicy.Apply(config);
        await using var session = await client.CreateSessionAsync(config);
        Assert.True(TurnExecution.TryBegin(session.SessionId, 101, session, out var turn));
        try
        {
            await session.SendAsync(new MessageOptions { Prompt = "Read the last synthetic region using the source schema." });
            await turn.Terminal.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var events = await session.GetEventsAsync();
            var completed = events.OfType<ToolExecutionCompleteEvent>().ToArray();
            Assert.Equal(2, completed.Length);
            Assert.All(completed, item => Assert.True(item.Data.Success));
            Assert.Equal(0, turn.ToolsFailed);
            var responses = observedResults.ToArray();
            Assert.Equal(2, responses.Length);
            Assert.Equal("queryable_tool_result", responses[0].GetProperty("kind").GetString());
            Assert.Contains(responses[0].GetProperty("schema").GetProperty("fields").EnumerateArray(), field => field.GetProperty("path").GetString()!.EndsWith("[\"region\"]"));
            Assert.Equal(999, responses[1].GetProperty("rows")[0].GetProperty("value").GetInt32());
            Assert.True(responses[1].GetProperty("source").GetProperty("Partial").GetBoolean());
            Assert.True(responses[1].GetProperty("complete").GetBoolean());
            Assert.DoesNotContain("Output too large", string.Join("", completed.Select(item => item.Data.Result?.Content)));
        }
        finally { await turn.FinishAsync(); }
    }
    finally
    {
        await server.StopAsync();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

private sealed class InvocationProbe(AIFunction inner) : DelegatingAIFunction(inner)
    {
        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
{
    var invocation = arguments.Services?.GetService(typeof(ToolInvocation)) as ToolInvocation
        ?? arguments.Context?.Values.OfType<ToolInvocation>().FirstOrDefault();
    Assert.NotNull(invocation);
    Assert.Equal("call_synthetic", invocation.ToolCallId);
    Assert.Equal("ApprovedRead", invocation.ToolName);
    return base.InvokeCoreAsync(arguments, cancellationToken);
}
    }

    private static async Task WriteResponse(HttpResponse response, bool toolCall, bool streaming, string toolName = "ApprovedRead", string arguments = "{}", string callId = "call_synthetic")
{
    const string responseId = "resp_synthetic";
    var functionId = "fc_" + callId;
    object item = toolCall
        ? new { id = functionId, type = "function_call", call_id = callId, name = toolName, arguments, status = "completed" }
        : new { id = "msg_synthetic", type = "message", role = "assistant", status = "completed", content = new[] { new { type = "output_text", text = "Synthetic result", annotations = Array.Empty<object>() } } };
    if (!streaming)
    {
        await response.WriteAsJsonAsync(new
        {
            id = responseId,
            @object = "response",
            created_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            status = "completed",
            model = "synthetic-test-model",
            output = new[] { item },
            usage = new { input_tokens = 10, output_tokens = 5, total_tokens = 15 }
        });
        return;
    }
    response.ContentType = "text/event-stream";
    await Event(response, "response.created", new { type = "response.created", response = new { id = responseId, status = "in_progress", output = Array.Empty<object>() } });
    await Event(response, "response.output_item.added", new { type = "response.output_item.added", output_index = 0, item });
    if (toolCall)
        await Event(response, "response.function_call_arguments.done", new { type = "response.function_call_arguments.done", item_id = functionId, output_index = 0, arguments });
    else
    {
        await Event(response, "response.content_part.added", new { type = "response.content_part.added", item_id = "msg_synthetic", output_index = 0, content_index = 0, part = new { type = "output_text", text = "", annotations = Array.Empty<object>() } });
        await Event(response, "response.output_text.delta", new { type = "response.output_text.delta", item_id = "msg_synthetic", output_index = 0, content_index = 0, delta = "Synthetic result" });
        await Event(response, "response.output_text.done", new { type = "response.output_text.done", item_id = "msg_synthetic", output_index = 0, content_index = 0, text = "Synthetic result" });
    }
    await Event(response, "response.output_item.done", new { type = "response.output_item.done", output_index = 0, item });
    await Event(response, "response.completed", new
    {
        type = "response.completed",
        response = new { id = responseId, status = "completed", model = "synthetic-test-model", output = new[] { item }, usage = new { input_tokens = 10, output_tokens = 5, total_tokens = 15 } }
    });
}

private static async Task Event(HttpResponse response, string name, object payload)
{
    await response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(payload)}\n\n");
    await response.Body.FlushAsync();
}
}