using System.Runtime.CompilerServices;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Infrastructure;
using Microsoft.Extensions.AI;

namespace AzureFinOps.Dashboard.AI.Runtime;

/// <summary>
/// Hosted web search is for public information no API returns, which the question itself shows, so a turn may start a
/// search only in its first model response; later responses may continue a search it already started (open a page it
/// found), and none may search after the model has called a Microsoft Learn tool. Prompt rules alone did not stop the model
/// from re-checking evidence QueryAzure or Microsoft Learn had just returned (site:learn.microsoft.com searches, opening the
/// Retail Prices URL it had queried), which costs the user time and which the live gate rightly scores as waste. This client
/// sits below Agent Framework's function-invocation loop, so it sees every model request of the turn.
/// </summary>
internal sealed class WebSearchGuard(IChatClient inner) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var turn = ToolExecutionContext.Current;
        var response = await base.GetResponseAsync(messages, ForRequest(options, turn), cancellationToken);
        Observe(turn, response.Messages.SelectMany(message => message.Contents));
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var turn = ToolExecutionContext.Current;
        await foreach (var update in base.GetStreamingResponseAsync(messages, ForRequest(options, turn), cancellationToken))
        {
            Observe(turn, update.Contents);
            yield return update;
        }
    }

    private static void Observe(ToolExecutionContext? turn, IEnumerable<AIContent> contents)
    {
        if (turn is null) return;
        foreach (var content in contents)
        {
            if (content is WebSearchToolCallContent) turn.WebSearchStarted = true;
            else if (content is FunctionCallContent call && MicrosoftLearnMcp.IsLearnTool(call.Name)) turn.MicrosoftLearnCalled = true;
        }
    }

    /// <summary>
    /// The options for the turn's next model request: unchanged for its first request, for a turn that already searched,
    /// and outside a turn; otherwise without hosted web search.
    /// </summary>
    internal static ChatOptions? ForRequest(ChatOptions? options, ToolExecutionContext? turn)
    {
        if (turn is null) return options;
        var first = turn.NextModelRequest() == 1;
        if (!turn.MicrosoftLearnCalled && (first || turn.WebSearchStarted)) return options;
        if (options?.Tools is not { } tools || !tools.Any(tool => tool is HostedWebSearchTool)) return options;
        var trimmed = options.Clone();
        trimmed.Tools = [.. tools.Where(tool => tool is not HostedWebSearchTool)];
        return trimmed;
    }
}
