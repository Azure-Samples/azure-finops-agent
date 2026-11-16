using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace AzureFinOps.Dashboard.AI.Runtime;

/// <summary>
/// Tracks whether the last model response of a run actually completed. A stream that ends
/// without a finish reason or usage, or that reports an error, is a failed run: the host
/// must not advance the response chain to it or present it as an empty answer. The one exception is a service failure
/// reported after the final answer was already whole (<see cref="AnswerKeptAfterServiceError"/>).
/// </summary>
internal sealed class ModelRunCompletion
{
    /// <summary>
    /// Shown instead of a model rate-limit error. The SDK already retries HTTP 429 with backoff; a limit reported inside
    /// the stream is not retried, and its message names the deployment and region and asks for quota only the operator
    /// can request.
    /// </summary>
    internal const string BusyMessage =
        "The AI model is busy right now (it reached its rate limit), so this answer was not completed. Wait a minute and ask again.";

    internal static bool IsRateLimit(string? code, string? message) =>
        code is not null && (code.Contains("rate_limit", StringComparison.OrdinalIgnoreCase) || code.Contains("429", StringComparison.Ordinal)
            || code.Contains("too_many_requests", StringComparison.OrdinalIgnoreCase))
        || message is not null && message.Contains("rate limit", StringComparison.OrdinalIgnoreCase);

    private const int TrailLength = 12;

    private string? _error;
    private bool _rateLimited;
    // Per model response: whether its last output item is an assistant message the service marked completed, with
    // text, and whether the response called a function.
    private bool _answerCompleted;
    private bool _calledFunction;
    private bool _failedAfterAnswer;
    private readonly List<(string Name, int Count)> _trail = [];

    public bool Completed { get; private set; }

    /// <summary>
    /// The service failed the final response (<c>server_error</c> or 503) after its answer was whole: the answer message
    /// was completed, with text, as the response's last output item, and the response called no function. The live
    /// gate saw this once, ten seconds after a complete answer had streamed (16 November 2026). The answer stands, but
    /// the failed response must not continue the response chain.
    /// </summary>
    public bool AnswerKeptAfterServiceError => _error is null && _failedAfterAnswer;

    /// <summary>The current model response's last stream events, by name only, for diagnosing a failed run.</summary>
    public string Trail => string.Join(", ", _trail.Select(entry => entry.Count > 1 ? $"{entry.Name}*{entry.Count}" : entry.Name));

    /// <param name="raw">The update itself or its raw representation; the OpenAI stream event is read through it.</param>
    public void Observe(IEnumerable<AIContent> contents, ChatFinishReason? finishReason, object? raw = null)
    {
        ObserveEvent(StreamEvent(raw));
        foreach (var content in contents)
        {
            switch (content)
            {
                case UsageContent:
                    Completed = true;
                    break;
                case ErrorContent error when _answerCompleted && ModelStreamRetryChatClient.IsRetryable(error):
                    _failedAfterAnswer = true;
                    break;
                case ErrorContent error:
                    _error ??= string.IsNullOrWhiteSpace(error.Message) ? error.ErrorCode ?? "unknown error" : error.Message;
                    _rateLimited |= IsRateLimit(error.ErrorCode, error.Message);
                    break;
                // Agent Framework surfaces approval requests after the model response has completed.
                case ToolApprovalRequestContent:
                    break;
                case FunctionCallContent:
                    _calledFunction = true;
                    _answerCompleted = false;
                    Completed = false;
                    break;
                default:
                    Completed = false;
                    break;
            }
        }
        if (finishReason is not null) Completed = true;
    }

    public string? Failure => _error is not null
        ? _rateLimited ? BusyMessage : $"The model service returned an error: {_error}"
        : Completed || AnswerKeptAfterServiceError ? null
        : "The model service ended its response before completing it, so nothing from this turn was kept. Ask again; if it keeps happening, check Azure status for Azure OpenAI and Foundry in the model's region.";

    private static object? StreamEvent(object? raw) => raw switch
    {
        AgentResponseUpdate agent when agent.RawRepresentation is { } inner && !ReferenceEquals(inner, agent) => StreamEvent(inner),
        ChatResponseUpdate chat when chat.RawRepresentation is { } inner && !ReferenceEquals(inner, chat) => StreamEvent(inner),
        _ => raw,
    };

#pragma warning disable OPENAI001 // Experimental Responses stream types.
    private void ObserveEvent(object? streamEvent)
    {
        if (streamEvent is not StreamingResponseUpdate update) return;
        var name = update.GetType().Name;
        if (name.StartsWith("StreamingResponse", StringComparison.Ordinal) && name.EndsWith("Update", StringComparison.Ordinal))
            name = name["StreamingResponse".Length..^"Update".Length];
        switch (update)
        {
            case StreamingResponseCreatedUpdate:
                _answerCompleted = _calledFunction = false;
                _trail.Clear();
                break;
            case StreamingResponseOutputItemAddedUpdate added:
                _answerCompleted = false;
                name += $"({ItemName(added.Item)})";
                break;
            case StreamingResponseOutputItemDoneUpdate { Item: MessageResponseItem message }:
                _answerCompleted = !_calledFunction && message.Role == MessageRole.Assistant && message.Status == MessageStatus.Completed
                    && message.Content.Any(part => !string.IsNullOrWhiteSpace(part.Text));
                name += $"(message:{message.Status})";
                break;
            case StreamingResponseOutputItemDoneUpdate done:
                name += $"({ItemName(done.Item)})";
                break;
        }
        Note(name);
    }

    private static string ItemName(ResponseItem item) => item switch
    {
        MessageResponseItem => "message",
        FunctionCallResponseItem => "function_call",
        ReasoningResponseItem => "reasoning",
        WebSearchCallResponseItem => "web_search",
        _ => item.GetType().Name,
    };
#pragma warning restore OPENAI001

    private void Note(string name)
    {
        if (_trail.Count > 0 && _trail[^1].Name == name) _trail[^1] = (name, _trail[^1].Count + 1);
        else
        {
            if (_trail.Count == TrailLength) _trail.RemoveAt(0);
            _trail.Add((name, 1));
        }
    }
}

internal sealed class IncompleteModelResponseException(string message, string? trail = null) : Exception(message)
{
    /// <summary>The failed model response's last stream events, by name only.</summary>
    public string? Trail { get; } = trail;
}
