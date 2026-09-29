using Microsoft.Extensions.AI;

namespace AzureFinOps.Dashboard.AI.Runtime;

/// <summary>
/// Tracks whether the last model response of a run actually completed. A stream that ends
/// without a finish reason or usage, or that reports an error, is a failed run: the host
/// must not advance the response chain to it or present it as an empty answer.
/// </summary>
internal sealed class ModelRunCompletion
{
    private string? _error;

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
        ? $"The model service returned an error: {_error}"
        : Completed ? null
        : "The model service ended its response before completing it, so nothing from this turn was kept. Ask again; if it keeps happening, check Azure status for Azure OpenAI and Foundry in the model's region.";
}

internal sealed class IncompleteModelResponseException(string message) : Exception(message);
