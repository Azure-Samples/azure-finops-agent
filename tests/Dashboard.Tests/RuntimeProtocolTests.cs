using System.Collections.Concurrent;
using System.Text.Json;
using AzureFinOps.Dashboard.AI;
using AzureFinOps.Dashboard.AI.Tools;
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeCallsOnlyRegisteredToolsAndSupportsAbort(bool abort)
    {
        var root = Path.Combine(Path.GetTempPath(), "finops-runtime-test-" + Guid.NewGuid().ToString("N"));
        var requests = new ConcurrentQueue<JsonElement>();
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
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
                return "synthetic evidence";
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
                var events = await session.GetEventsAsync();
                Assert.Contains(events.OfType<AssistantMessageEvent>(), item => item.Data.Content.Contains("Synthetic result"));
                Assert.Contains(events.OfType<ToolExecutionCompleteEvent>(), item => item.Data.Result?.Content == "synthetic evidence");
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
                Model = config.Model, Provider = config.Provider, Streaming = true, Tools = config.Tools, WorkingDirectory = root
            };
            RuntimePolicy.Apply(resumeConfig);
            await using var resumed = await client.ResumeSessionAsync(config.SessionId, resumeConfig);
            Assert.Contains((await resumed.GetEventsAsync()).OfType<UserMessageEvent>(), item => item.Data.Content.Contains("Read the synthetic evidence"));
            await resumed.DisposeAsync();
            await client.DeleteSessionAsync(config.SessionId);
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

    private static async Task WriteResponse(HttpResponse response, bool toolCall, bool streaming)
    {
        const string responseId = "resp_synthetic";
        object item = toolCall
            ? new { id = "fc_synthetic", type = "function_call", call_id = "call_synthetic", name = "ApprovedRead", arguments = "{}", status = "completed" }
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
            await Event(response, "response.function_call_arguments.done", new { type = "response.function_call_arguments.done", item_id = "fc_synthetic", output_index = 0, arguments = "{}" });
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