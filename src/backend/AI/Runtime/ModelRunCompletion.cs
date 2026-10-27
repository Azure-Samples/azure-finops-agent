using Microsoft.Extensions.AI;

namespace AzureFinOps.Dashboard.AI.Runtime;

/// <summary>
/// Tracks whether the last model response of a run actually completed. A stream that ends
/// without a finish reason or usage, or that reports an error, is a failed run: the host
/// must not advance the response chain to it or present it as an empty answer.
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

    private string? _error;
    private bool _rateLimited;

    public bool Completed { get; private set; }

    public void Observe(IEnumerable<AIContent> contents, ChatFinishReason? finishReason)
    {
        foreach (var content in contents)
        {
            switch (content)
            {
                case UsageContent:
                    Completed = true;
                    break;
                case ErrorContent error:
                    _error ??= string.IsNullOrWhiteSpace(error.Message) ? error.ErrorCode ?? "unknown error" : error.Message;
                    _rateLimited |= IsRateLimit(error.ErrorCode, error.Message);
                    break;
                // Agent Framework surfaces approval requests after the model response has completed.
                case ToolApprovalRequestContent:
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
        : Completed ? null
        : "The model service ended its response before completing it, so nothing from this turn was kept. Ask again; if it keeps happening, check Azure status for Azure OpenAI and Foundry in the model's region.";
}

internal sealed class IncompleteModelResponseException(string message) : Exception(message);
