using System.Runtime.CompilerServices;
using AzureFinOps.Dashboard.AI.Runtime;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dashboard.Tests;

public sealed class ServerErrorRetryChatClientTests
{
    private const string ServerErrorMessage =
        "The model deployment encountered an error processing your request. You can retry your request, or contact us through an Azure support request.";

    private static ChatResponseUpdate Update(params AIContent[] contents) => new(ChatRole.Assistant, contents);
    private static ChatResponseUpdate ServerError() => Update(new ErrorContent(ServerErrorMessage) { ErrorCode = "server_error" });
    private static ChatResponseUpdate Done() =>
        new(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = 1, OutputTokenCount = 1 })]) { FinishReason = ChatFinishReason.Stop };

    private sealed class ScriptedClient(params ChatResponseUpdate[][] attempts) : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var updates = attempts[Calls++];
            foreach (var update in updates)
            {
                await Task.Yield();
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static async Task<(List<ChatResponseUpdate> Updates, ModelRunCompletion Completion)> RunAsync(IChatClient client)
    {
        var updates = new List<ChatResponseUpdate>();
        var completion = new ModelRunCompletion();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "q")]))
        {
            completion.Observe(update.Contents, update.FinishReason);
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

        var (updates, completion) = await RunAsync(new ServerErrorRetryChatClient(inner, NullLogger.Instance));

        Assert.Equal(2, inner.Calls);
        Assert.DoesNotContain(updates, update => update.Contents.OfType<ErrorContent>().Any());
        Assert.Contains(updates, update => update.Contents.OfType<TextContent>().Any(text => text.Text == "Answer"));
        Assert.Null(completion.Failure);
    }

    [Fact]
    public async Task ASecondServerErrorIsReported()
    {
        var inner = new ScriptedClient([ServerError()], [ServerError()]);

        var (_, completion) = await RunAsync(new ServerErrorRetryChatClient(inner, NullLogger.Instance));

        Assert.Equal(2, inner.Calls);
        Assert.Equal($"The model service returned an error: {ServerErrorMessage}", completion.Failure);
    }

    [Fact]
    public async Task AServerErrorAfterAnswerTextIsNotResent()
    {
        var inner = new ScriptedClient([Update(new TextContent("Partial")), ServerError()]);

        var (_, completion) = await RunAsync(new ServerErrorRetryChatClient(inner, NullLogger.Instance));

        Assert.Equal(1, inner.Calls);
        Assert.NotNull(completion.Failure);
    }

    [Fact]
    public async Task AServerErrorAfterAFunctionCallIsNotResent()
    {
        var inner = new ScriptedClient([Update(new FunctionCallContent("call-1", "QueryAzure")), ServerError()]);

        await RunAsync(new ServerErrorRetryChatClient(inner, NullLogger.Instance));

        Assert.Equal(1, inner.Calls);
    }

    [Theory]
    [InlineData("rate_limit_exceeded", "Rate limit exceeded.")]
    [InlineData("content_filter", "The response was filtered.")]
    [InlineData(null, "Unknown failure.")]
    public async Task OtherErrorsAreNotResent(string? code, string message)
    {
        var inner = new ScriptedClient([Update(new ErrorContent(message) { ErrorCode = code })]);

        var (_, completion) = await RunAsync(new ServerErrorRetryChatClient(inner, NullLogger.Instance));

        Assert.Equal(1, inner.Calls);
        Assert.NotNull(completion.Failure);
    }
}
