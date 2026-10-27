using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AzureFinOps.Dashboard.AI.Runtime;

/// <summary>
/// Resends one model request once when its stream fails with the service's <c>server_error</c>
/// ("The model deployment encountered an error processing your request. You can retry your request")
/// before it produced answer text or a function call. The SDK retries HTTP 5xx, but not an error
/// reported inside a response stream that already opened, and such a failure otherwise ends the
/// whole turn without an answer after every tool round succeeded. Sits below Agent Framework's
/// function invocation, so only the failed model request is sent again, never a tool.
/// </summary>
internal sealed class ServerErrorRetryChatClient(IChatClient inner, ILogger logger) : DelegatingChatClient(inner)
{
    internal static bool IsRetryable(ErrorContent error) =>
        string.Equals(error.ErrorCode, "server_error", StringComparison.OrdinalIgnoreCase)
        && !ModelRunCompletion.IsRateLimit(error.ErrorCode, error.Message);

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        for (var attempt = 1; ; attempt++)
        {
            var committed = false;
            var retry = false;
            await foreach (var update in base.GetStreamingResponseAsync(request, options, cancellationToken))
            {
                if (attempt == 1 && !committed && update.Contents.OfType<ErrorContent>().FirstOrDefault(IsRetryable) is { } error)
                {
                    logger.LogWarning("Model stream failed with {ErrorCode} before any answer; sending the request once more", error.ErrorCode);
                    retry = true;
                    break;
                }
                committed |= update.Contents.Any(content => content is TextContent { Text.Length: > 0 } or FunctionCallContent);
                yield return update;
            }
            if (!retry) yield break;
        }
    }
}
