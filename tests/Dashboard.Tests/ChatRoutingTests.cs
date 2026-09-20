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
    [InlineData("comment mettre en place", false)]
    [InlineData("vérifie le calcul", false)]
    [InlineData("actually use West Europe", false)]
    [InlineData("lav en tabel", false)]
    [InlineData("no, use Central US", false)]
    [InlineData("help", false)]
    [InlineData("hello, compare these files", false)]
    [InlineData("how about h100?", false)]
    [InlineData("I already have a h100 deployed", false)]
    [InlineData("how about on spot you think I can try the other regions for bigger chance for not getting shut down like nwo", false)]
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

    [Fact]
    public void ComputeGuidancePreservesUnknownsAndSeparatesSpotEvidence()
    {
        Assert.Contains("unverified, never unavailable everywhere", CopilotSessionFactory.SystemPrompt);
        Assert.Contains("A priced region is not a verified deployable region", CopilotSessionFactory.SystemPrompt);
        Assert.Contains("Missing history is unknown, not zero eviction risk", CopilotSessionFactory.SystemPrompt);
        Assert.Contains("do not issue an unrelated refusal", CopilotSessionFactory.SystemPrompt);
        Assert.Contains("notScopes and exemptions", CopilotSessionFactory.SystemPrompt);
        Assert.DoesNotContain("If requested SKU not allowed, lead with the policy block", CopilotSessionFactory.SystemPrompt);
    }
}