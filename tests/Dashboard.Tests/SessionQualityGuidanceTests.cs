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
    private static string Prompt => AgentSessionFactory.SystemPrompt;

    [Theory]
    [InlineData("language of the latest user message")]
    [InlineData("exactly one visual")]
    [InlineData("never put definitions, method notes or caveats between the headline and the visual")]
    [InlineData("one row per compared item, named in the first column")]
    [InlineData("unknown, never zero")]
    [InlineData("look it up instead of guessing")]
    [InlineData("never resend a failing request unchanged")]
    [InlineData("never search documentation for script commands")]
    [InlineData("azure-rest-api-specs")]
    [InlineData("apiVersions")]
    [InlineData("one at a time")]
    [InlineData("from query results")]
    [InlineData("retrievedAtUtc")]
    [InlineData("Never delete resources")]
    [InlineData("approves in the UI")]
    [InlineData("GenerateScript directly")]
    [InlineData("only when the user explicitly requests it")]
    [InlineData("Never request, echo or store passwords")]
    [InlineData("ReportMaturityScore")]
    [InlineData("request period totals with granularity None")]
    [InlineData("test every offered key against billed cost")]
    [InlineData("a Resource Graph tag count is inventory context, never billing evidence")]
    [InlineData("Disk Mount bills each VM a shared Premium SSD is mounted to")]
    [InlineData("location in~ (every Retail region, copied exactly from its result)")]
    [InlineData("A Retail region missing from those virtualMachines locations")]
    [InlineData("only an open region without a quota row needs GET /subscriptions/{id}/providers/Microsoft.Compute/locations/{region}/usages")]
    [InlineData("for every Retail region as url lines of one call, in the same response as the quota query")]
    [InlineData("so every Retail region is accounted for")]
    [InlineData("Never intersect region lists from two responses by typing them into a query-only call")]
    [InlineData("a response returned this turn shows its API's fields")]
    [InlineData("policyStates/latest/summarize?api-version=2024-10-01")]
    [InlineData("Microsoft.Advisor/recommendations?api-version=2025-01-01")]
    [InlineData("never compute a score total, maximum, percentage or average")]
    [InlineData("Sponsored_2016-01-01 (Microsoft Azure Sponsorship)")]
    [InlineData("the offer is unsupported, not zero spend or ingestion lag")]
    public void PromptKeepsHostInvariants(string phrase) =>
        Assert.Contains(phrase, Prompt, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void WebSearchIsOnByDefaultAndScopedToPublicNews()
    {
        Assert.True(AgentSessionFactory.DefaultWebSearch);
        var withSearch = AgentSessionFactory.Instructions(webSearch: true);
        Assert.StartsWith(Prompt, withSearch);
        Assert.Contains("never web-search a question those answer", withSearch);
        Assert.Contains("including Azure OpenAI and other Foundry model token prices", withSearch);
        Assert.Contains("Never web-search to confirm, cross-check or add background to evidence QueryAzure already returned", withSearch);
        Assert.Contains("Never web-search or open Microsoft documentation, pricing, API or API specification URLs (prices.azure.com", withSearch);
        Assert.Contains("github.com, api.github.com and raw.githubusercontent.com including the Azure/azure-rest-api-specs and microsoftgraph/msgraph-metadata repositories", withSearch);
        Assert.Contains("never web-search an API path, api-version or field name", withSearch);
        Assert.Contains("needs no web search at all: start it with QueryAzure", withSearch);
        Assert.Contains("These rules take precedence over any general instruction to browse for current information or to cite web results", withSearch);
        Assert.Contains("QueryAzure is itself a live web request tool", withSearch);
        Assert.Contains("already satisfies any instruction to use the web for current information, prices or citations", withSearch);
        Assert.Contains("a price it returned this turn is already up to date", withSearch);
        Assert.Contains("so an answer built from them needs no web citation", withSearch);
        Assert.Contains("web_search is never a calculator or unit converter (no \"calculator:\" queries)", withSearch);
        Assert.Contains("A question about the current or last billing period uses timeframe BillingMonthToDate or TheLastBillingMonth", Prompt);
        Assert.Contains("unitOfMeasure is '1K' on older token meters and '1M' on newer ones, so return per-1M rates from that same query as retailPrice * (unitOfMeasure == \"1K\" ? 1000 : 1), never with one fixed factor", Prompt);
        Assert.Contains("'Azure OpenAI GPT' followed by the generation number for each newer GPT generation", Prompt);
        Assert.Contains("x.meterName.ToLower().Contains(\"glbl\") in query and contains(tolower(meterName),'glbl') in $filter", Prompt);
        Assert.Contains("effectiveStartDate is when a price took effect, not when the model was released", Prompt);
        Assert.Contains("pick each family's newest model by the version in its meter or SKU names", Prompt);
        Assert.DoesNotContain("newest release by effectiveStartDate", Prompt);
        Assert.DoesNotContain("equals that family's latest date", Prompt);
        Assert.DoesNotContain("contains(meterName,", Prompt);
        Assert.Contains("Std Gl (Global Standard), Std DZ (Data Zone Standard), PP (Priority Processing)", Prompt);
        Assert.Contains("whose retailPrice is per PTU for the whole reservationTerm even though unitOfMeasure reads '1/Hour'", Prompt);
        Assert.Contains("published model benchmarks and leaderboards (quality, speed, latency)", withSearch);
        Assert.Contains("Only when the user asks for model benchmarks, speed or latency, answer directly with no clarifying question", withSearch);
        Assert.Contains("one row per newest priced model of each requested family", withSearch);
        Assert.Contains("name newer benchmarked versions that have no Foundry price in one sentence below the table instead of adding rows", withSearch);
        Assert.Contains("A Foundry model price, deployment-type, PTU, caching or cost-estimate question needs no web search", withSearch);
        Assert.Contains("never search or read documentation to identify models, their availability or deployment types for a pricing question", Prompt);
        Assert.DoesNotContain("model benchmark", Prompt);
        Assert.Contains("Never search or open third-party price, calculator or comparison sites", withSearch);
        Assert.Contains("never research a caveat the question did not ask about", withSearch);
        Assert.Contains("an answer that cites no web source should have made no search", withSearch);
        Assert.Contains("source URL", withSearch);
        Assert.Contains("because citation markers are removed before the user sees the answer", withSearch);
        Assert.Equal(Prompt, AgentSessionFactory.Instructions(webSearch: false));
    }

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
        var tool = new FaqTools(new UserTokens { UserId = 101 }).Create().First();
        Assert.Contains("only when the user explicitly requests", tool.Description);
        Assert.Contains("Pending review is not publication", tool.Description);
    }

    [Fact]
    public void NextStepsNeverCostAModelRoundOfTheirOwn()
    {
        Assert.Contains("next steps never get a round of their own", Prompt);
        Assert.Contains("call SuggestFollowUp only in the same response as another tool call that completes the answer", Prompt);
        Assert.Contains("make any SuggestFollowUp call in the ReportMaturityScore response, and after scoring call no other tool", Prompt);
        Assert.Contains("no GenerateDataReport unless the user asked for a file", Prompt);

        // The link the prompt teaches must match the chip pattern ChatView.vue renders as a button.
        var link = System.Text.RegularExpressions.Regex.Match(Prompt, @"\[[^\]]+\]\(prompt:[^)]+\)");
        Assert.True(link.Success);
        Assert.Equal("[short label](prompt:complete next instruction)", link.Value);

        var followUp = FollowUpTools.Create().Single();
        Assert.Contains("never in a response of its own", followUp.Description);
        Assert.Contains("[label](prompt:instruction)", followUp.Description);

        var score = new ScoreTools(new UserTokens { UserId = 101 }).Create().First();
        Assert.Equal("ReportMaturityScore", score.Name);
        Assert.Contains("put any SuggestFollowUp call in this same response, then answer with no further tool call", score.Description);
        Assert.DoesNotContain("by itself", score.Description);
    }

    [Fact]
    public void GuidanceNamesModelFamiliesNeverAVersion()
    {
        // A hardcoded version ages and steers the agent away from the newest models in the price list.
        var azure = new AzureQueryTools(new UserTokens { UserId = 101 }).Create().First();
        foreach (var text in new[] { AgentSessionFactory.Instructions(webSearch: true), azure.Description })
            Assert.DoesNotMatch(
                @"(?i)\bgpt[- ]?\d|\b\d+(\.\d+)?-(sol|luna|astra|terra)\b|\b4o\b|\bgrok[- ]\d|\bdeepseek[- ]v?\d|\bllama[- ]\d|\bmistral[- ]\w+[- ]\d",
                text);
    }

    [Fact]
    public void AnswersStayShortPlainAndReadable()
    {
        Assert.Contains("Write for someone new to Azure", Prompt);
        Assert.Contains("about 50-100 words outside the visual", Prompt);
        Assert.Contains("never two sentences", Prompt);
        Assert.Contains("never reword a search or read more pages for detail the short answer will not show", Prompt);
        Assert.Contains("at most six rows and four columns unless the question asks for more items or metrics", Prompt);
        Assert.Contains("never pairs such as 2 / 4 or short / long", Prompt);
        Assert.Contains("never 2,762.5M or fractional tokens", Prompt);
        Assert.Contains("offer it as a follow-up link instead", Prompt);
        Assert.DoesNotContain("a / b / c", Prompt);
    }

    [Fact]
    public void QueryToolsPointAtAuthoritativeApiReferences()
    {
        var azure = new AzureQueryTools(new UserTokens { UserId = 101 }).Create().First();
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