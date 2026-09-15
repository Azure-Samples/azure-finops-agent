using AzureFinOps.Dashboard.AI;

namespace Dashboard.Tests;

public sealed class ChatRoutingTests
{
    [Theory]
    [InlineData("hi", true)]
    [InlineData(" Hello! ", true)]
    [InlineData("bonjour", true)]
    [InlineData("hej", true)]
    [InlineData("", false)]
    [InlineData("thanks", false)]
    [InlineData("yes", false)]
    [InlineData("do it", false)]
    [InlineData("then do it for me", false)]
    [InlineData("give me the list", false)]
    [InlineData("generate a script", false)]
    [InlineData("sous forme de tableau", false)]
    [InlineData("reverifie le contenu", false)]
    [InlineData("lav en tabel", false)]
    [InlineData("no, use Central US", false)]
    [InlineData("help", false)]
    [InlineData("hello, compare these files", false)]
    public void OnlyStandaloneGreetingsSuppressTools(string prompt, bool expected) =>
        Assert.Equal(expected, ChatEndpoints.IsTrivialPrompt(prompt));

    [Fact]
    public void PromptKeepsSpecificTasksAndUnknownEvidenceExplicit()
    {
        Assert.DoesNotContain("Do NOT answer literally", CopilotSessionFactory.SystemPrompt);
        Assert.DoesNotContain("Pushback is uncapped budget", CopilotSessionFactory.SystemPrompt);
        Assert.Contains("Missing rates are unknown, not zero", CopilotSessionFactory.SystemPrompt);
        Assert.Contains("never silently switch to live tenant spend", CopilotSessionFactory.SystemPrompt);
        Assert.Contains("GenerateDataReport supports CSV, XLSX", CopilotSessionFactory.SystemPrompt);
    }
}