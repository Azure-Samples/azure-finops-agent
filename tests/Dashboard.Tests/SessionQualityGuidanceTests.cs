using AzureFinOps.Dashboard.AI;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Auth;

namespace Dashboard.Tests;

/// <summary>
/// Guards the few prompt and tool-description invariants that the host relies on,
/// without pinning incidental wording.
/// </summary>
public sealed class SessionQualityGuidanceTests
{
    private static string Prompt => CopilotSessionFactory.SystemPrompt;

    [Theory]
    [InlineData("language of the latest user message")]
    [InlineData("exactly one visual")]
    [InlineData("unknown, never zero")]
    [InlineData("look it up instead of guessing")]
    [InlineData("azure-rest-api-specs")]
    [InlineData("apiVersions")]
    [InlineData("one at a time")]
    [InlineData("QueryToolResult")]
    [InlineData("retrievedAtUtc")]
    [InlineData("Never delete resources")]
    [InlineData("approves in the UI")]
    [InlineData("GenerateScript directly")]
    [InlineData("only when the user explicitly requests it")]
    [InlineData("Never request, echo or store passwords")]
    [InlineData("ReportMaturityScore")]
    public void PromptKeepsHostInvariants(string phrase) =>
        Assert.Contains(phrase, Prompt, StringComparison.OrdinalIgnoreCase);

    [Theory]
    [InlineData("GetCrawlMaturityEvidence")]
    [InlineData("CheckComputeFeasibility")]
    [InlineData("QueryCostsAcrossSubscriptions")]
    [InlineData("GetAzureRetailPricingBatch")]
    [InlineData("GetCopilotUsage")]
    [InlineData("GetTagCoverage")]
    [InlineData("GetChargebackReport")]
    [InlineData("DetectCostAnomalies")]
    [InlineData("FindIdleResources")]
    [InlineData("BulkAzureRequest")]
    [InlineData("QueryGraph")]
    [InlineData("QueryLogAnalytics")]
    [InlineData("GetAzureRetailPricing")]
    [InlineData("FetchPublicWebPage")]
    public void PromptDoesNotReferenceRemovedTools(string tool) =>
        Assert.DoesNotContain(tool, Prompt);

    [Fact]
    public void PublicFaqSubmissionRequiresAnExplicitUserRequest()
    {
        var tool = new FaqTools(new UserTokens { UserId = 101 }).Create().Single();
        Assert.Contains("only when the user explicitly requests", tool.Description);
        Assert.Contains("Pending review is not publication", tool.Description);
    }

    [Fact]
    public void QueryToolsPointAtAuthoritativeApiReferences()
    {
        var azure = new AzureQueryTools(new UserTokens { UserId = 101 }).Create().Single();
        Assert.Equal("QueryAzure", azure.Name);
        Assert.Contains("azure-rest-api-specs", azure.Description);
        Assert.Contains("apiVersions", azure.Description);
        Assert.Contains("learn.microsoft.com/graph", azure.Description);
        Assert.Contains("learn.microsoft.com/rest/api/cost-management/retail-prices", azure.Description);
        Assert.Contains("learn.microsoft.com/kusto", azure.Description);
        Assert.Contains("learn.microsoft.com/rest/api/storageservices", azure.Description);
        Assert.Contains("azure.status.microsoft", azure.Description);
    }
}