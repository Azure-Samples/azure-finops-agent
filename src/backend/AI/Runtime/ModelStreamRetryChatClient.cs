using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AzureFinOps.Dashboard.AI.Runtime;

/// <summary>
/// Resends one model request once when its stream fails before it produced answer text, a function call or a hosted
/// web search: the service's <c>server_error</c> ("The model deployment encountered an error processing your request.
/// You can retry your request") or a transient service-unavailable error reported inside the stream, or a stream that
/// died on the wire (the client's read timeout, or a dropped connection). The SDK retries HTTP 5xx and a request that
/// timed out before its response started, but not a stream that already opened, and such a failure otherwise ends
/// the whole turn without an answer after every tool round succeeded. Sits below Agent Framework's function
/// invocation, so only the failed model request is sent again, never a tool.
/// </summary>
internal sealed class ModelStreamRetryChatClient(IChatClient inner, ILogger logger) : DelegatingChatClient(inner)
{
    /// <summary>Shown when a model stream stops twice in a row, or after the answer had started.</summary>
    internal const string StalledMessage = "The connection to the model stopped responding, so this answer stopped. Ask again.";

    internal static bool IsRetryable(ErrorContent error) =>
        (string.Equals(error.ErrorCode, "server_error", StringComparison.OrdinalIgnoreCase) || IsServiceUnavailable(error))
        && !ModelRunCompletion.IsRateLimit(error.ErrorCode, error.Message);

    // A transient 503 reported inside the stream: the evaluation gate failed one case on "Service Unavailable" while the
    // deployment's other calls in the same minutes succeeded.
    private static bool IsServiceUnavailable(ErrorContent error) =>
        error.ErrorCode is { } code && (code.Contains("service_unavailable", StringComparison.OrdinalIgnoreCase) || code.Contains("503", StringComparison.Ordinal))
        || string.Equals(error.Message?.Trim().TrimEnd('.'), "Service Unavailable", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A stream that died on the wire rather than one the caller stopped: System.ClientModel ends a read that waited
    /// longer than the network timeout with a <see cref="TaskCanceledException"/> while the caller's token is still
    /// live, and a dropped connection surfaces as an <see cref="IOException"/> or <see cref="HttpRequestException"/>.
    /// </summary>
    internal static bool IsTransportFailure(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        IOException or HttpRequestException => true,
        _ => false,
    };

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        for (var attempt = 1; ; attempt++)
        {
            var committed = false;
            var retry = false;
            await using var stream = base.GetStreamingResponseAsync(request, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                try
                {
                    if (!await stream.MoveNextAsync()) break;
                }
                catch (Exception exception) when (attempt == 1 && !committed && IsTransportFailure(exception, cancellationToken))
                {
                    logger.LogWarning("Model stream stopped ({Failure}) before any answer; sending the request once more", exception.GetType().Name);
                    retry = true;
                    break;
                }
                var update = stream.Current;
                if (attempt == 1 && !committed && update.Contents.OfType<ErrorContent>().FirstOrDefault(IsRetryable) is { } error)
                {
                    logger.LogWarning("Model stream failed with {ErrorCode} before any answer; sending the request once more", error.ErrorCode);
                    retry = true;
                    break;
                }
                committed |= update.Contents.Any(content => content is TextContent { Text.Length: > 0 } or FunctionCallContent or WebSearchToolCallContent);
                yield return update;
            }
            if (!retry) yield break;
        }
    }
}
