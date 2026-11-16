using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using AzureFinOps.Dashboard.AI.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;

namespace Dashboard.Tests;

public sealed class ModelStreamRetryChatClientTests
{
    private const string ServerErrorMessage =
        "The model deployment encountered an error processing your request. You can retry your request, or contact us through an Azure support request.";

    private static ChatResponseUpdate Update(params AIContent[] contents) => new(ChatRole.Assistant, contents);
    private static ChatResponseUpdate ServerError() => Update(new ErrorContent(ServerErrorMessage) { ErrorCode = "server_error" });
    private static ChatResponseUpdate Done() =>
        new(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = 1, OutputTokenCount = 1 })]) { FinishReason = ChatFinishReason.Stop };
    // What System.ClientModel throws when a streamed read waits longer than the network timeout.
    private static TaskCanceledException ReadTimeout() =>
        new("The operation was cancelled because it exceeded the configured timeout of 0:01:40.");

    // Each attempt yields its updates in order and throws an exception placed among them.
    private sealed class ScriptedClient(params object[][] attempts) : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var steps = attempts[Calls++];
            foreach (var step in steps)
            {
                await Task.Yield();
                if (step is Exception failure) throw failure;
                yield return (ChatResponseUpdate)step;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static async Task<(List<ChatResponseUpdate> Updates, ModelRunCompletion Completion)> RunAsync(IChatClient client, CancellationToken cancellationToken = default)
    {
        var updates = new List<ChatResponseUpdate>();
        var completion = new ModelRunCompletion();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "q")], cancellationToken: cancellationToken))
        {
            completion.Observe(update.Contents, update.FinishReason, update);
            updates.Add(update);
        }
        return (updates, completion);
    }

    [Fact]
    public async Task AServerErrorBeforeAnyAnswerIsSentOnceMoreAndHidden()
    {
        var inner = new ScriptedClient(
            [Update(new TextReasoningContent("thinking")), ServerError()],
            [Update(new TextContent("Answer")), Done()]);

        var (updates, completion) = await RunAsync(new ModelStreamRetryChatClient(inner, NullLogger.Instance));

        Assert.Equal(2, inner.Calls);
        Assert.DoesNotContain(updates, update => update.Contents.OfType<ErrorContent>().Any());
        Assert.Contains(updates, update => update.Contents.OfType<TextContent>().Any(text => text.Text == "Answer"));
        Assert.Null(completion.Failure);
    }

    [Fact]
    public async Task ASecondServerErrorIsReported()
    {
        var inner = new ScriptedClient([ServerError()], [ServerError()]);

        var (_, completion) = await RunAsync(new ModelStreamRetryChatClient(inner, NullLogger.Instance));

        Assert.Equal(2, inner.Calls);
        Assert.Equal($"The model service returned an error: {ServerErrorMessage}", completion.Failure);
    }

    [Fact]
    public async Task AServerErrorAfterAnswerTextIsNotResent()
    {
        var inner = new ScriptedClient([Update(new TextContent("Partial")), ServerError()]);

        var (_, completion) = await RunAsync(new ModelStreamRetryChatClient(inner, NullLogger.Instance));

        Assert.Equal(1, inner.Calls);
        Assert.NotNull(completion.Failure);
    }

    [Fact]
    public async Task AServerErrorAfterAFunctionCallIsNotResent()
    {
        var inner = new ScriptedClient([Update(new FunctionCallContent("call-1", "QueryAzure")), ServerError()]);

        await RunAsync(new ModelStreamRetryChatClient(inner, NullLogger.Instance));

        Assert.Equal(1, inner.Calls);
    }

    [Theory]
    [InlineData("service_unavailable", "The service is temporarily unavailable.")]
    [InlineData(null, "Service Unavailable")]
    public async Task AServiceUnavailableErrorBeforeAnyAnswerIsSentOnceMore(string? code, string message)
    {
        var inner = new ScriptedClient(
            [Update(new ErrorContent(message) { ErrorCode = code })],
            [Update(new TextContent("Answer")), Done()]);

        var (updates, completion) = await RunAsync(new ModelStreamRetryChatClient(inner, NullLogger.Instance));

        Assert.Equal(2, inner.Calls);
        Assert.DoesNotContain(updates, update => update.Contents.OfType<ErrorContent>().Any());
        Assert.Null(completion.Failure);
    }

    [Theory]
    [InlineData("rate_limit_exceeded", "Rate limit exceeded.")]
    [InlineData("content_filter", "The response was filtered.")]
    [InlineData(null, "Unknown failure.")]
    public async Task OtherErrorsAreNotResent(string? code, string message)
    {
        var inner = new ScriptedClient([Update(new ErrorContent(message) { ErrorCode = code })]);

        var (_, completion) = await RunAsync(new ModelStreamRetryChatClient(inner, NullLogger.Instance));

        Assert.Equal(1, inner.Calls);
        Assert.NotNull(completion.Failure);
    }

    // The live gate's tool round finished, then the next model request's stream went silent; the service completed every
    // request it received, and the 10-minute read timeout held the turn until the case's limit (16 November 2026).
    public static TheoryData<Exception> WireFailures => new() { ReadTimeout(), new IOException("Connection reset by peer"), new HttpRequestException("The response ended prematurely.") };

    [Theory]
    [MemberData(nameof(WireFailures))]
    public async Task AStreamThatStopsOnTheWireBeforeAnyAnswerIsSentOnceMore(Exception failure)
    {
        var inner = new ScriptedClient(
            [Update(new TextReasoningContent("thinking")), failure],
            [Update(new TextContent("Answer")), Done()]);

        var (updates, completion) = await RunAsync(new ModelStreamRetryChatClient(inner, NullLogger.Instance));

        Assert.Equal(2, inner.Calls);
        Assert.Contains(updates, update => update.Contents.OfType<TextContent>().Any(text => text.Text == "Answer"));
        Assert.Null(completion.Failure);
    }

    [Fact]
    public async Task AStreamThatStopsTwiceFails()
    {
        var inner = new ScriptedClient([ReadTimeout()], [ReadTimeout()]);

        await Assert.ThrowsAsync<TaskCanceledException>(() => RunAsync(new ModelStreamRetryChatClient(inner, NullLogger.Instance)));
        Assert.Equal(2, inner.Calls);
    }

    public static TheoryData<AIContent> Commitments => new()
    {
        new TextContent("Partial"),
        new FunctionCallContent("call-1", "QueryAzure"),
        new WebSearchToolCallContent("search-1"),
    };

    [Theory]
    [MemberData(nameof(Commitments))]
    public async Task AStreamThatStopsAfterTheAnswerStartedIsNotResent(AIContent started)
    {
        var inner = new ScriptedClient([Update(started), ReadTimeout()]);

        await Assert.ThrowsAsync<TaskCanceledException>(() => RunAsync(new ModelStreamRetryChatClient(inner, NullLogger.Instance)));
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task AStopTheCallerAskedForIsNeverResent()
    {
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();
        var inner = new ScriptedClient([new OperationCanceledException(stop.Token)]);

        await Assert.ThrowsAsync<OperationCanceledException>(() => RunAsync(new ModelStreamRetryChatClient(inner, NullLogger.Instance), stop.Token));
        Assert.Equal(1, inner.Calls);
    }

    private const string Created = """
        event: response.created
        data: {"type":"response.created","sequence_number":0,"response":{"id":"resp_test","object":"response","created_at":1794843755,"status":"in_progress","model":"test-model","output":[]}}


        """;

    private const string Answered = """
        event: response.output_item.added
        data: {"type":"response.output_item.added","output_index":0,"sequence_number":1,"item":{"type":"message","id":"msg_test","role":"assistant","content":[],"status":"in_progress"}}

        event: response.output_text.delta
        data: {"type":"response.output_text.delta","item_id":"msg_test","output_index":0,"content_index":0,"delta":"Answer","sequence_number":2}

        event: response.output_item.done
        data: {"type":"response.output_item.done","output_index":0,"sequence_number":3,"item":{"type":"message","id":"msg_test","role":"assistant","content":[{"type":"output_text","text":"Answer","annotations":[]}],"status":"completed"}}

        event: response.completed
        data: {"type":"response.completed","sequence_number":4,"response":{"id":"resp_test","object":"response","created_at":1794843755,"status":"completed","model":"test-model","output":[{"type":"message","id":"msg_test","role":"assistant","content":[{"type":"output_text","text":"Answer","annotations":[]}],"status":"completed"}],"usage":{"input_tokens":5,"output_tokens":1,"total_tokens":6,"input_tokens_details":{"cached_tokens":0},"output_tokens_details":{"reasoning_tokens":0}}}}


        """;

    // The real Responses client, SSE parser and System.ClientModel read timeout: a stream that goes silent after
    // response.created ends with the client's timeout, and the request is sent once more.
    [Fact]
    public async Task AResponsesStreamThatGoesSilentIsResentThroughTheRealClient()
    {
        var requests = 0;
        using var release = new CancellationTokenSource();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.MapPost("/v1/responses", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/event-stream";
            if (Interlocked.Increment(ref requests) > 1)
            {
                await context.Response.WriteAsync(Created + Answered);
                return;
            }
            await context.Response.WriteAsync(Created.Replace("resp_test", "resp_stalled", StringComparison.Ordinal));
            await context.Response.Body.FlushAsync();
            using var silence = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, release.Token);
            try { await Task.Delay(Timeout.Infinite, silence.Token); }
            catch (OperationCanceledException) { }
        });
        await app.StartAsync();
#pragma warning disable OPENAI001 // The Responses client is evaluation-only in OpenAI .NET.
        var model = new OpenAIClient(new ApiKeyCredential("test"), new OpenAIClientOptions
        {
            Endpoint = new Uri("http://localhost/v1"),
            Transport = new HttpClientPipelineTransport(app.GetTestClient()),
            NetworkTimeout = TimeSpan.FromSeconds(1),
        }).GetResponsesClient().AsIChatClient("test-model");
#pragma warning restore OPENAI001

        try
        {
            var (updates, completion) = await RunAsync(new ModelStreamRetryChatClient(model, NullLogger.Instance));

            Assert.Equal(2, requests);
            Assert.Equal("Answer", string.Concat(updates.SelectMany(update => update.Contents.OfType<TextContent>()).Select(text => text.Text)));
            Assert.Null(completion.Failure);
            // The response chain continues from the answered response, never from the one that went silent.
            Assert.Equal("resp_test", updates.ToChatResponse().ConversationId);
        }
        finally { await release.CancelAsync(); }
    }

    private const string AnswerStarted = """
        event: response.output_item.added
        data: {"type":"response.output_item.added","output_index":0,"sequence_number":1,"item":{"type":"message","id":"msg_test","role":"assistant","content":[],"status":"in_progress"}}

        event: response.output_text.delta
        data: {"type":"response.output_text.delta","item_id":"msg_test","output_index":0,"content_index":0,"delta":"Answer","sequence_number":2}


        """;

    private const string AnswerCompleted = """
        event: response.output_item.done
        data: {"type":"response.output_item.done","output_index":0,"sequence_number":3,"item":{"type":"message","id":"msg_test","role":"assistant","content":[{"type":"output_text","text":"Answer","annotations":[]}],"status":"completed"}}


        """;

    // The service's error event nests its fields under "error", then the response is marked failed.
    private const string FailedByTheService = """
        event: error
        data: {"type":"error","sequence_number":4,"error":{"type":"server_error","code":"server_error","message":"The model deployment encountered an error processing your request.","param":null}}

        event: response.failed
        data: {"type":"response.failed","sequence_number":5,"response":{"id":"resp_test","object":"response","created_at":1794843755,"status":"failed","model":"test-model","output":[],"error":{"code":"server_error","message":"The model deployment encountered an error processing your request."}}}


        """;

    private static async Task<(int Requests, string Text, ModelRunCompletion Completion)> StreamThroughRealClientAsync(string body)
    {
        var requests = 0;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.MapPost("/v1/responses", async (HttpContext context) =>
        {
            Interlocked.Increment(ref requests);
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(body);
        });
        await app.StartAsync();
#pragma warning disable OPENAI001 // The Responses client is evaluation-only in OpenAI .NET.
        var model = new OpenAIClient(new ApiKeyCredential("test"), new OpenAIClientOptions
        {
            Endpoint = new Uri("http://localhost/v1"),
            Transport = new HttpClientPipelineTransport(app.GetTestClient()),
        }).GetResponsesClient().AsIChatClient("test-model");
#pragma warning restore OPENAI001
        var (updates, completion) = await RunAsync(new ModelStreamRetryChatClient(model, NullLogger.Instance));
        return (requests, string.Concat(updates.SelectMany(update => update.Contents.OfType<TextContent>()).Select(text => text.Text)), completion);
    }

    // The live gate's answer streamed whole and the service failed the response ten seconds later (16 November 2026):
    // the answer stands and is not generated again, and the failed response is kept out of the chain.
    [Fact]
    public async Task AServiceErrorAfterTheAnswerCompletedKeepsTheAnswer()
    {
        var (requests, text, completion) = await StreamThroughRealClientAsync(Created + AnswerStarted + AnswerCompleted + FailedByTheService);

        Assert.Equal(1, requests);
        Assert.Equal("Answer", text);
        Assert.True(completion.AnswerKeptAfterServiceError);
        Assert.Null(completion.Failure);
        Assert.Contains("OutputItemDone(message:Completed), Error", completion.Trail);
    }

    // Before the service marked the message completed, the text may be cut off, so the turn still fails.
    [Fact]
    public async Task AServiceErrorBeforeTheAnswerCompletedStillFailsTheTurn()
    {
        var (requests, text, completion) = await StreamThroughRealClientAsync(Created + AnswerStarted + FailedByTheService);

        Assert.Equal(1, requests);
        Assert.Equal("Answer", text);
        Assert.False(completion.AnswerKeptAfterServiceError);
        Assert.Equal("The model service returned an error: The model deployment encountered an error processing your request.", completion.Failure);
    }
}
