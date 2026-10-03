using System.Text.Json;
using AzureFinOps.Dashboard.Endpoints;
using AzureFinOps.Dashboard.AI.Runtime;

namespace Dashboard.Tests;

public sealed class TranscriptProjectionTests
{
    [Fact]
    public void SessionFailureRetainsItsReasonAfterPartialAnswers()
    {
        var question = new UserMessageEvent("Synthetic spending question");
        var answer = new AssistantMessageEvent("answer", "| Service | Cost |\n|---|---|\n| Compute | USD 25 |");
        var followUp = new AssistantMessageEvent("follow-up", "Synthetic follow-up");
        var error = new TurnErrorEvent("Authentication failed with provider (HTTP 401)", "model_error");

        using var result = Project(question, answer, followUp, error);
        var messages = result.RootElement;
        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Contains("USD 25", messages[1].GetProperty("content").GetString());
        Assert.Contains("Synthetic follow-up", messages[1].GetProperty("content").GetString());
        Assert.Equal("system", messages[2].GetProperty("role").GetString());
        Assert.Equal("error", messages[2].GetProperty("terminalStatus").GetString());
        Assert.Contains("HTTP 401", messages[2].GetProperty("content").GetString());
    }

    [Fact]
    public void FailuresBeforeAVisibleUserTurnAreNotConversationReplies()
    {
        var context = new UserMessageEvent("<skill-context>synthetic context</skill-context>");
        var error = new TurnErrorEvent("Synthetic warmup failure", "model_error");
        using var result = Project(context, error);
        Assert.Equal(0, result.RootElement.GetArrayLength());
    }

    [Fact]
    public void FailureDoesNotBecomeTheNextTurnsAnswer()
    {
        var first = new UserMessageEvent("First question");
        var error = new TurnErrorEvent("Synthetic failure", "model_error");
        var second = new UserMessageEvent("Second question");
        var answer = new AssistantMessageEvent("answer", "Second answer");
        using var result = Project(first, error, second, answer);
        Assert.Equal(4, result.RootElement.GetArrayLength());
        Assert.Equal("error", result.RootElement[1].GetProperty("terminalStatus").GetString());
        Assert.Equal("Second question", result.RootElement[2].GetProperty("content").GetString());
        Assert.Equal("Second answer", result.RootElement[3].GetProperty("content").GetString());
    }

    [Fact]
    public void ToolsReplayWithTheirResultsAndCharts()
    {
        var question = new UserMessageEvent("Synthetic chart question");
        var start = new ToolStartEvent("call-1", "RenderChart", "{\"type\":\"bar\"}");
        var done = new ToolCompleteEvent("call-1", true, "{\"type\":\"bar\",\"data\":[]}", null);
        var pending = new ToolStartEvent("call-2", "QueryAzure", "{}");
        var answer = new AssistantMessageEvent("answer", "Synthetic answer");
        using var result = Project(question, start, done, pending, answer);
        var reply = result.RootElement[1];
        Assert.Equal("Synthetic answer", reply.GetProperty("content").GetString());
        var tools = reply.GetProperty("toolCalls");
        Assert.Equal(2, tools.GetArrayLength());
        Assert.True(tools[0].GetProperty("success").GetBoolean());
        Assert.Equal(JsonValueKind.Null, tools[1].GetProperty("success").ValueKind);
        Assert.Equal(JsonValueKind.Null, tools[1].GetProperty("durationMs").ValueKind);
        Assert.Equal(1, reply.GetProperty("charts").GetArrayLength());
    }

    [Fact]
    public void ToolsReplayWithTheirDurations()
    {
        var started = DateTimeOffset.Parse("2026-10-03T06:00:00Z");
        var question = new UserMessageEvent("Synthetic pricing question");
        var measuredStart = new ToolStartEvent("call-1", "QueryAzure", "{}") { Timestamp = started };
        var measured = new ToolCompleteEvent("call-1", true, "{}", null, 1234) { Timestamp = started.AddSeconds(9) };
        var searchStart = new ToolStartEvent("call-2", "web_search", null) { Timestamp = started };
        var search = new ToolCompleteEvent("call-2", true, "{}", null) { Timestamp = started.AddMilliseconds(2600) };
        var answer = new AssistantMessageEvent("answer", "Synthetic answer");
        using var result = Project(question, measuredStart, searchStart, measured, search, answer);
        var tools = result.RootElement[1].GetProperty("toolCalls");
        Assert.Equal(1234, tools[0].GetProperty("durationMs").GetInt64());
        Assert.Equal(2600, tools[1].GetProperty("durationMs").GetInt64());
    }

    [Fact]
    public void PersistedThinkingReplaysWithItsAnswer()
    {
        var question = new UserMessageEvent("Synthetic question");
        var firstThought = new ReasoningEvent("**Checking scope**\n\nReading the subscription list.");
        var start = new ToolStartEvent("call-1", "QueryAzure", "{}");
        var done = new ToolCompleteEvent("call-1", true, "{}", null);
        var secondThought = new ReasoningEvent("**Summarizing**\n\nTotals are ready.");
        var answer = new AssistantMessageEvent("answer", "Synthetic answer");
        var next = new UserMessageEvent("Next question");
        var plain = new AssistantMessageEvent("plain", "Plain answer");
        using var result = Project(question, firstThought, start, done, secondThought, answer, next, plain);
        var reply = result.RootElement[1];
        Assert.Equal("**Checking scope**\n\nReading the subscription list.\n\n**Summarizing**\n\nTotals are ready.",
            reply.GetProperty("thinking").GetString());
        Assert.Equal("Synthetic answer", reply.GetProperty("content").GetString());
        Assert.Equal(JsonValueKind.Null, result.RootElement[3].GetProperty("thinking").ValueKind);
    }

    [Fact]
    public void ThinkingAloneDoesNotBecomeAnAnswer()
    {
        var question = new UserMessageEvent("Synthetic question");
        var thought = new ReasoningEvent("**Planning**");
        var error = new TurnErrorEvent("Synthetic failure", "model_error");
        using var result = Project(question, thought, error);
        Assert.Equal(2, result.RootElement.GetArrayLength());
        Assert.Equal("system", result.RootElement[1].GetProperty("role").GetString());
    }

    private static JsonDocument Project(params AgentEvent[] events) =>
        JsonDocument.Parse(JsonSerializer.Serialize(SessionEndpoints.BuildTranscript(events, 101)));
}
