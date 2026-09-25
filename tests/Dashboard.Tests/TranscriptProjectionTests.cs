using System.Text.Json;
using AzureFinOps.Dashboard.Endpoints;
using GitHub.Copilot;

namespace Dashboard.Tests;

public sealed class TranscriptProjectionTests
{
    [Fact]
    public void SessionFailureRetainsItsReasonAfterPartialAnswers()
    {
        var question = new UserMessageEvent { Data = new() { Content = "Synthetic spending question" } };
        var answer = new AssistantMessageEvent { Data = new() { MessageId = "answer", Content = "| Service | Cost |\n|---|---|\n| Compute | USD 25 |" } };
        var followUp = new AssistantMessageEvent { Data = new() { MessageId = "follow-up", Content = "Synthetic follow-up" } };
        var error = new SessionErrorEvent { Data = new() { ErrorType = "provider_error", Message = "Authentication failed with provider (HTTP 401)" } };

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
        var context = new UserMessageEvent { Data = new() { Content = "<skill-context>synthetic context</skill-context>" } };
        var error = new SessionErrorEvent { Data = new() { ErrorType = "provider_error", Message = "Synthetic warmup failure" } };
        using var result = Project(context, error);
        Assert.Equal(0, result.RootElement.GetArrayLength());
    }

    [Fact]
    public void FailureDoesNotBecomeTheNextTurnsAnswer()
    {
        var first = new UserMessageEvent { Data = new() { Content = "First question" } };
        var error = new SessionErrorEvent { Data = new() { ErrorType = "provider_error", Message = "Synthetic failure" } };
        var second = new UserMessageEvent { Data = new() { Content = "Second question" } };
        var answer = new AssistantMessageEvent { Data = new() { MessageId = "answer", Content = "Second answer" } };
        using var result = Project(first, error, second, answer);
        Assert.Equal(4, result.RootElement.GetArrayLength());
        Assert.Equal("error", result.RootElement[1].GetProperty("terminalStatus").GetString());
        Assert.Equal("Second question", result.RootElement[2].GetProperty("content").GetString());
        Assert.Equal("Second answer", result.RootElement[3].GetProperty("content").GetString());
    }

    [Fact]
    public void PersistedFailuresAreRedactedBeforeReplay()
    {
        var secret = "sk-" + new string('x', 48);
        var question = new UserMessageEvent { Data = new() { Content = "Synthetic question" } };
        var error = new SessionErrorEvent { Data = new() { ErrorType = "provider_error", Message = $"Synthetic failure with api_key={secret}" } };
        using var result = Project(question, error);
        Assert.DoesNotContain(secret, result.RootElement[1].GetProperty("content").GetString());
    }

    private static JsonDocument Project(params SessionEvent[] events) =>
        JsonDocument.Parse(JsonSerializer.Serialize(SessionEndpoints.BuildTranscript(events, 101)));
}
