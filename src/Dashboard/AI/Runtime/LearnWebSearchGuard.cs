using System.Runtime.CompilerServices;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Infrastructure;
using Microsoft.Extensions.AI;

namespace AzureFinOps.Dashboard.AI.Runtime;

/// <summary>
/// Leaves hosted web search out of a turn's later model requests once the model has called a Microsoft Learn tool.
/// Three prompt rules and a note in the Learn result did not stop the model from re-checking a Learn result with a
/// site:learn.microsoft.com web search in about a third of documentation answers, which costs the user time and which the
/// live gate rightly scores as waste. It sits below Agent Framework's function-invocation loop, so it sees every model
/// request of the turn; a search in the same response as the first Learn call is still possible.
/// </summary>
internal sealed class LearnWebSearchGuard(IChatClient inner) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var turn = ToolExecutionContext.Current;
        var response = await base.GetResponseAsync(messages, Without(options, turn), cancellationToken);
        Observe(turn, response.Messages.SelectMany(message => message.Contents));
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var turn = ToolExecutionContext.Current;
        await foreach (var update in base.GetStreamingResponseAsync(messages, Without(options, turn), cancellationToken))
        {
            Observe(turn, update.Contents);
            yield return update;
        }
    }

    private static void Observe(ToolExecutionContext? turn, IEnumerable<AIContent> contents)
    {
        if (turn is { MicrosoftLearnCalled: false }
            && contents.Any(content => content is FunctionCallContent call && MicrosoftLearnMcp.IsLearnTool(call.Name)))
            turn.MicrosoftLearnCalled = true;
    }

    /// <summary>The request options without hosted web search once this turn has called Microsoft Learn; otherwise unchanged.</summary>
    internal static ChatOptions? Without(ChatOptions? options, ToolExecutionContext? turn)
    {
        if (turn is not { MicrosoftLearnCalled: true } || options?.Tools is not { } tools || !tools.Any(tool => tool is HostedWebSearchTool))
            return options;
        var trimmed = options.Clone();
        trimmed.Tools = [.. tools.Where(tool => tool is not HostedWebSearchTool)];
        return trimmed;
    }
}
