using AzureFinOps.Dashboard.AI;
using AzureFinOps.Dashboard.AI.Tools;

namespace Dashboard.Tests;

public sealed class SessionQualityGuidanceTests
{
    [Fact]
    public void MonetaryAndCohortGuidancePreservesSourceMeaning()
    {
        Assert.Contains("savingsCurrency=USD stays USD", CopilotSessionFactory.SystemPrompt);
        Assert.Contains("explicit dated exchange-rate source", CopilotSessionFactory.SystemPrompt);
        Assert.Contains("without matching identities and dates", CopilotSessionFactory.SystemPrompt);
        Assert.Contains("Normalize each returned rate's unitOfMeasure to per-1M tokens", CopilotSessionFactory.SystemPrompt);
        Assert.DoesNotContain("tokens × retail $/1K", CopilotSessionFactory.SystemPrompt);
    }

    [Fact]
    public void FollowUpsDoNotOverrideTheRequestedDeliverableOrCrawlWorkflow()
    {
        var followUp = FollowUpTools.Create().Single();
        Assert.Contains("Never substitute", followUp.Description);
        Assert.Contains("Do not call after GetCrawlMaturityEvidence", followUp.Description);
        Assert.Contains("At most one", followUp.Description);
        Assert.DoesNotContain("FIRST action MUST", followUp.Description);
        Assert.DoesNotContain("ALWAYS call SuggestFollowUp", CopilotSessionFactory.SystemPrompt);
    }
}
