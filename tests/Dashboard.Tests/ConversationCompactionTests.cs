using AzureFinOps.Dashboard.AI.Runtime;

namespace Dashboard.Tests;

public sealed class ConversationCompactionTests
{
    [Theory]
    [InlineData(null, true, false, false)]
    [InlineData(AgentConversation.CompactAfterInputTokens, true, false, false)]
    [InlineData(AgentConversation.CompactAfterInputTokens + 1, true, false, true)]
    [InlineData(AgentConversation.CompactAfterInputTokens + 1, false, false, false)]
    [InlineData(AgentConversation.CompactAfterInputTokens + 1, true, true, false)]
    public void CompactsOnlyALargeChainWithoutPendingApprovals(long? tokens, bool hasContext, bool pendingApprovals, bool expected) =>
        Assert.Equal(expected, AgentConversation.ShouldCompact(tokens, hasContext, pendingApprovals));

    [Fact]
    public void RecapCarriesVisibleExchangesWithoutContextOrToolResults()
    {
        var recap = AgentConversation.Recap(
        [
            new UserMessageEvent("[CONTEXT: User IS connected to Azure. Today is 2026-01-01 (UTC).]\nWhat did Standard_D4s_v5 cost?"),
            new ReasoningEvent("private thinking"),
            new ToolStartEvent("call-1", "QueryAzure", "{\"url\":\"https://management.azure.com/synthetic\"}"),
            new ToolCompleteEvent("call-1", true, "{\"rows\":[[\"tool-only-value\"]]}", null),
            new AssistantMessageEvent("m1", "Checking the cost."),
            new AssistantMessageEvent("m2", "Standard_D4s_v5 cost **USD 12.34**."),
            new UserMessageEvent("<skill-context>injected</skill-context>"),
            new TurnIdleEvent(),
            new UserMessageEvent("Chart it by day"),
        ]);

        Assert.NotNull(recap);
        Assert.StartsWith("[CONVERSATION SO FAR:", recap);
        Assert.Contains("User: What did Standard_D4s_v5 cost?", recap);
        Assert.Contains("Assistant: Checking the cost.\n\nStandard_D4s_v5 cost **USD 12.34**.", recap);
        Assert.Contains("User: Chart it by day", recap);
        Assert.DoesNotContain("[CONTEXT", recap);
        Assert.DoesNotContain("skill-context", recap);
        Assert.DoesNotContain("tool-only-value", recap);
        Assert.DoesNotContain("private thinking", recap);
        Assert.EndsWith("The user's new message follows.]", recap);
    }

    [Fact]
    public void RecapKeepsTheNewestMessagesWithinItsBudget()
    {
        var events = new List<AgentEvent>();
        for (var i = 0; i < 20; i++)
        {
            events.Add(new UserMessageEvent($"Question {i}"));
            events.Add(new AssistantMessageEvent($"m{i}", $"Answer {i} " + new string('x', AgentConversation.RecapMessageCharacters * 2)));
        }

        var recap = AgentConversation.Recap(events)!;

        Assert.Contains("Answer 19 ", recap);
        Assert.Contains("Question 19", recap);
        Assert.DoesNotContain("Question 0\n", recap);
        Assert.Contains("earlier messages omitted", recap);
        Assert.Contains("…[truncated]", recap);
        Assert.True(recap.Length < AgentConversation.RecapCharacters + 1_000);
    }

    [Fact]
    public void RecapIsNullWithoutVisibleMessages() =>
        Assert.Null(AgentConversation.Recap([new UserMessageEvent("[CONTEXT: only context]"), new TurnIdleEvent()]));
}
