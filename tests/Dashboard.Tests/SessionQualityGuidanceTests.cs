using AzureFinOps.Dashboard.AI;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Infrastructure;

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
    [InlineData("Leave api-version out of Azure Resource Manager reads")]
    [InlineData("an ApplyAzureChange write carries the version its read used")]
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
    [InlineData("list it as a candidate with unknown quota, saying that the locations list omits it")]
    [InlineData("A budget answer gives the dates of the cycle its currentSpend covers")]
    [InlineData("a scan that finds none says no resource matched those conditions, never that no resource has a license setting")]
    [InlineData("Microsoft 365 subscription quantities come from Microsoft Graph GET /directory/subscriptions")]
    [InlineData("show totalLicenses as the subscription quantity, marking trial subscriptions, never as purchased or paid")]
    [InlineData("describe the Forecast sum by its own firstDate and lastDate")]
    [InlineData("An Azure license scan reads every license resource and every resource that carries a license setting instead of a hand-picked list of types")]
    [InlineData("Never intersect region lists from two responses by typing them into a query-only call")]
    [InlineData("each valid only for the scope and compute mode it was read at")]
    [InlineData("offered in a region needs no existing resource")]
    [InlineData("never carry to another, including a resource not yet created")]
    [InlineData("allocation is unverified without a documented signal")]
    [InlineData("a resource that does not exist makes its own quota not applicable, never zero")]
    [InlineData("never environment variables, app settings or secrets")]
    [InlineData("a response returned this turn shows its API's fields")]
    [InlineData("Microsoft.PolicyInsights/policyStates/latest/summarize` (no body)")]
    [InlineData("Microsoft.Advisor/recommendations?$filter=Category eq 'Cost'")]
    [InlineData("never compute a score total, maximum, percentage or average")]
    [InlineData("Sponsored_2016-01-01 (Microsoft Azure Sponsorship)")]
    [InlineData("the offer is unsupported, not zero spend or ingestion lag")]
    // Users on an unsupported offer (4 of 95 conversations) were told their spend was unknown and left there.
    [InlineData("offer once, in one short sentence, to analyze a cost or usage file the user uploads from any source")]
    [InlineData("name no portal or export for it that you have not verified")]
    [InlineData("The user's stated goal and constraints come first")]
    [InlineData("use its exact values and give a better practice as one note, never as a change")]
    [InlineData("Take names, regions, codes and values from the user or from a read, never from an example")]
    [InlineData("an identifier is copied exactly as returned, never shortened, numbered or relabelled")]
    [InlineData("when rows share a name the cell adds the parent's exact name in parentheses, even in a column layout the user specified")]
    [InlineData("an optional property it does not return is not set, so report its documented default")]
    [InlineData("ManagementPolicyNotFound")]
    [InlineData("A 403 from Azure means the user lacks an Azure role on that scope")]
    [InlineData("never send the user to App registrations to add permissions to this app")]
    [InlineData("microsoft_docs_search")]
    [InlineData("never tenant, resource, user or customer names, IDs or values")]
    [InlineData("never a round of its own for what the fetching call could compute")]
    // Calculation rounds that retyped figures from several calls were 17% of model time in production (October 2026).
    [InlineData("with combine when the answer totals, merges or compares their responses")]
    [InlineData("one QueryAzure call with one url line per subscription, the shared body and combine for the totals")]
    [InlineData("never whole tables, since repeating the request with query or combine recomputes many rows faster than retyping them")]
    // Guidance answers searched or read pages one round at a time.
    [InlineData("three products are three searches in the first response, never one now and another later")]
    [InlineData("never a third round, a reworded search, a page reached through another page's links")]
    // Unrequested data files cost about a minute of writing each.
    [InlineData("offer the complete list as a file in a follow-up link")]
    [InlineData("a breakdown such as per model or by subscription is a table, not a file")]
    // A token-usage question spent four documentation rounds finding the Azure Monitor token metrics.
    [InlineData("metricnames=InputTokens,OutputTokens,TotalTokens&aggregation=Total&interval=FULL")]
    [InlineData("their dimensions are ApiName, Region, ModelDeploymentName, ModelName and ModelVersion, none per project")]
    // The token metrics name their dimensions in lowercase (modelname), so a query comparing "ModelName" found no model and
    // the agent re-read every account; per-project figures then took three rounds of Application Insights exploration.
    [InlineData("so compare dimension names with ToLower()")]
    [InlineData("the account's FoundryModelEstimatedCost metric (preview; estimated USD for Foundry models, not billed cost)")]
    // A per-model token answer dropped an image model billed USD 296 and an audio model, so its rows missed the total.
    [InlineData("A breakdown of a stated total accounts for all of it")]
    [InlineData("For Azure cost, budget, inventory, tag and pricing answers, quote the reported retrieval time")]
    [InlineData("Documentation answers carry no retrieval time")]
    [InlineData("when they are not in your tool list, search with QueryAzure GET https://learn.microsoft.com/api/search")]
    // Thirty one-per-subscription cost queries made one session's first answer take 395 seconds.
    [InlineData("never a query per subscription, which the host runs one at a time")]
    [InlineData("Enterprise Agreement subscriptions only: for Microsoft Customer Agreement, CSP and other offers")]
    [InlineData("{\"usage\":{\"name\":\"UsageQuantity\",\"function\":\"Sum\"}}")]
    // The agent inventory card asks what each step needs; 12 of its 22 answers wrote "eligible plan" or "appropriate
    // permissions" although the documentation they had read named the license, preview opt-in and admin role (Oct 2026).
    // Asking only to name each item left rows vague, and the quality report's judge wanted plan names in the rows and
    // unstated items marked, so the rule covers all three items in every row.
    [InlineData("a column for what a step needs covers the license, the preview opt-in and the admin role in every row")]
    [InlineData("\"none needed\" where a source says so and unconfirmed where no source states it")]
    [InlineData("never eligible plan, existing licenses or appropriate permissions")]
    // A tender needing a PDF got HTML without a word, and "execute the bash script" got a request to upload the script back.
    [InlineData("You cannot write PDF, Word or PowerPoint files or run a script")]
    [InlineData("say so in the first sentence and deliver the closest result yourself from data your tools read")]
    [InlineData("never claiming the format or the run")]
    // A statement with no question was repeated back as the whole answer.
    [InlineData("states a goal or capability instead of asking a question")]
    [InlineData("never repeat the message back")]
    // A request unrelated to the product (a recipe, the weather, the news) is declined without a tool call or a web search.
    [InlineData("completely unrelated to Azure, Microsoft 365, cloud spend or their tenant")]
    [InlineData("call no tool and do not search the web; politely decline in one sentence")]
    [InlineData("your purpose is to answer Azure, spend or tenant related questions")]
    // An idle-resource sweep dropped public IPs that had a publicIPPrefix, reported USD 0 and put the same filter in its
    // cleanup script. The prefix names where an address was allocated from, not an attachment (Azure REST API spec,
    // Microsoft.Network publicIpAddress.json: ipConfiguration "associated with the public IP address", publicIPPrefix
    // "should be allocated from").
    [InlineData("one is unattached when properties.ipConfiguration and properties.natGateway are both empty")]
    [InlineData("properties.publicIPPrefix only names the prefix it was allocated from and says nothing about attachment")]
    // Session evaluation of 107 conversations (November 2026). The agent-inventory card, a quarter of all points lost,
    // left its steps without a way to follow them and most of what they need unconfirmed after three page reads.
    [InlineData("for a table that states what each step needs, one page per product the question names, at most five")]
    // A later quality report put the links in citations after the table instead (the "link each source once" rule
    // pulled them out of the cells) and wrote "tenant permissions; licensing varies" for a step no source covered.
    [InlineData("In a table of steps, each step's tool cell is itself a link, [tool](url) inside the cell, to the Microsoft Learn page a search or fetch returned for that step")]
    [InlineData("that cell link is the page's citation, so the page needs no second link after the table, and a link after the table never replaces it")]
    [InlineData("nor any other phrase that names no license, opt-in or role (such as tenant permissions or licensing varies), which is unconfirmed")]
    // The best-value card reported Claude as having no published price: Claude models have no Retail Prices meter, and
    // Microsoft bills them at Anthropic's own rates (Learn, Claude Consumption Units billing in Microsoft Foundry).
    [InlineData("Claude (Anthropic) models are the one exception")]
    [InlineData("so they have no Retail Prices meter and are never reported as having no published price")]
    [InlineData("QueryAzure GET https://platform.claude.com/docs/en/about-claude/pricing in the same response as the price list")]
    [InlineData("US Data Zone Standard costs 1.1 times as much")]
    [InlineData("quote only Claude models that https://learn.microsoft.com/azure/foundry/foundry-models/concepts/claude-models lists")]
    // Cost Management answers a missing role with 401 RBACAccessDenied and Graph with 403; no answer named the remedy.
    [InlineData("A failed read's _access note says which role or permission grants that access and how the user gets it")]
    // A VM that cost USD 363 in a month was promised USD 309 a month from a 730-hour retail difference.
    [InlineData("A saving on an existing resource follows the hours it actually ran")]
    [InlineData("saves that actual cost times (1 - new rate / current rate)")]
    [InlineData("a difference over 730 hours is only the ceiling for a resource that ran all month")]
    [InlineData("A reservation or savings plan priced from retail rates bills every hour of its term")]
    // Advisor's reservation and savings plan estimates come from hourly usage over the lookback period, so the rule
    // above must not put a "runs all month" caveat on them (Microsoft Learn, Determine what reservation to purchase).
    [InlineData("Advisor's reservation and savings plan estimates already come from hourly usage over their lookbackPeriod")]
    // The quality report's reservation eval (November 2026) was rejected because "currently on pay-as-you-go" was never
    // read: the answer left reservation coverage unverified. Cost Management's PricingModel dimension returns OnDemand for
    // pay-as-you-go usage and Reservation, SavingsPlan or Spot otherwise.
    [InlineData("Which hours were pay-as-you-go is read, not assumed: make PricingModel one of that cost query's two grouping dimensions")]
    [InlineData("base the comparison on OnDemand rows only and state the coverage found")]
    public void PromptKeepsHostInvariants(string phrase) =>
        Assert.Contains(phrase, Prompt, StringComparison.OrdinalIgnoreCase);

    // Four signed-out cost questions got a text-only "select Connect Azure" and no alternative.
    [Fact]
    public void ASignedOutTenantQuestionShowsTheConnectButtonAndOffersAnUpload()
    {
        var context = ChatEndpoints.ConnectionContext([], null, null, new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc));

        Assert.StartsWith("[CONTEXT: Azure NOT connected. Today is 2026-11-10 (UTC).", context);
        Assert.Contains("call QueryAzure once with GET /subscriptions, which sends nothing without a connection and shows the user a Connect Azure button", context);
        Assert.Contains("offer to analyze a cost or usage export they upload now instead", context);
        Assert.Contains("Do NOT refuse public questions", context);
        // Without a token that read is the host's own consent result, which the page shows as the Connect Azure button.
        using var actions = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(
            HttpHelper.ConsentActions(HttpHelper.TokenMissing("AzureToken", null, "azure"))));
        var action = Assert.Single(actions.RootElement.EnumerateArray());
        Assert.Equal("Connect Azure", action.GetProperty("label").GetString());
        Assert.Equal("/auth/microsoft?tier=base", action.GetProperty("href").GetString());
    }

    [Fact]
    public void PromptNoLongerSuggestsOneCostQueryPerScope() =>
        Assert.DoesNotContain("one cost query per scope", Prompt);

    [Fact]
    public void WebSearchIsOnByDefaultAndScopedToPublicNews()
    {
        Assert.True(AgentSessionFactory.DefaultWebSearch);
        var withSearch = AgentSessionFactory.Instructions(webSearch: true);
        Assert.StartsWith(Prompt, withSearch);
        Assert.Contains("never web-search a question those answer", withSearch);
        Assert.Contains("including Azure OpenAI and other Foundry model token prices", withSearch);
        Assert.Contains("Never web-search to confirm, cross-check or add background to evidence QueryAzure or the Microsoft Learn tools already returned", withSearch);
        Assert.Contains("read a page a search returned with microsoft_docs_fetch, never by opening it through web search", withSearch);
        Assert.Contains("Never web-search or open Microsoft documentation, pricing, API or API specification URLs (prices.azure.com", withSearch);
        Assert.Contains("github.com, api.github.com and raw.githubusercontent.com including the Azure/azure-rest-api-specs and microsoftgraph/msgraph-metadata repositories", withSearch);
        Assert.Contains("never web-search an API path, api-version or field name", withSearch);
        Assert.Contains("needs no web search at all: start it with QueryAzure", withSearch);
        Assert.Contains("These rules take precedence over any general instruction to browse for current information or to cite web results", withSearch);
        Assert.Contains("QueryAzure, microsoft_docs_search and microsoft_docs_fetch are themselves live web request tools", withSearch);
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
        Assert.Contains("Name newer benchmarked versions that have no Foundry price in one sentence below the table instead of adding rows", withSearch);
        Assert.Contains("A Foundry model price, deployment-type, PTU, caching or cost-estimate question needs no web search", withSearch);
        Assert.Contains("never search the web or documentation to identify or name models, their availability or deployment types for a pricing question", Prompt);
        // A price answer once added "newer announced" models from release trackers that no returned evidence supported.
        Assert.Contains("a pricing answer names only models the price list returned, never a newer or announced version from search results or memory", Prompt);
        // A Global Standard answer quoted East US 2 rows, then told the user the rates were region-independent; the judge
        // rejected that unshown claim (live gate run 37756397255).
        Assert.Contains("quote eastus2 rows and name that region as the rows' source, without telling the user the rates apply to other regions", Prompt);
        // A budget answer gauged unfiltered subscription spend against the budget and called the budget's own currentSpend
        // "an older snapshot"; the judge rejected the comparison (live gate run 37772538434, attempt 2).
        Assert.Contains("Its currentSpend and forecastSpend are the service's own measure for the budget's scope, filter and period, so budget-versus-actual answers, risk calls and gauges use them", Prompt);
        Assert.Contains("never call either figure an older snapshot or a lagging copy of the other", Prompt);
        Assert.Contains("the Retail Prices API lists every priced model and deployment type except Claude's (see the Retail Prices API facts), and the answer names only the models it returned", withSearch);
        // A best-value pick named a winner by no stated measure, although a much cheaper scored model was in the table.
        Assert.Contains("by score per USD of one blended per-1M price (3 input tokens to 1 output token unless the user gives a mix), computed in the query or combine that built the table", withSearch);
        // The best-value eval scored 2 of 6 requested models from a web search's page view, which the user and the judge
        // never see, while the same leaderboard read as a page held scores for 5 of them and showed the sixth has none.
        Assert.Contains("then read at most one results page it found, with QueryAzure GET and never through web search", withSearch);
        Assert.Contains("a web search's own page view is not shown and can be partial), and take every figure from that read", withSearch);
        Assert.Contains("at the highest reasoning effort the source lists for it, named in the note", withSearch);
        // The next report's best-value answer left two models unscored although the page it read scored them: the price
        // list shortens a tier to its initials and the leaderboard adds a release date, so names never matched exactly.
        Assert.Contains("so match them by family and version number, not spelling", withSearch);
        Assert.Contains("is the same model when that family has exactly one model with that version number on each side; the row keeps the price list's name and the note names each such match", withSearch);
        Assert.Contains("Never search or open third-party price, calculator or comparison sites: they copy the Retail Prices API and are not evidence (Anthropic's own pricing page is not one of them", withSearch);
        Assert.Contains("so an abbreviation stays an abbreviation and is said to be one", Prompt);
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
        Assert.Contains("they render as buttons, so no tool call is needed for them", Prompt);
        Assert.Contains("after scoring call no other tool", Prompt);
        Assert.Contains("no GenerateDataReport unless the user asked for a file", Prompt);
        Assert.DoesNotContain("SuggestFollowUp", Prompt);

        // The link the prompt teaches must match the chip pattern ChatView.vue renders as a button.
        var link = System.Text.RegularExpressions.Regex.Match(Prompt, @"\[[^\]]+\]\(prompt:[^)]+\)");
        Assert.True(link.Success);
        Assert.Equal("[short label](prompt:complete next instruction)", link.Value);

        var score = new ScoreTools(new UserTokens { UserId = 101 }).Create().First();
        Assert.Equal("ReportMaturityScore", score.Name);
        Assert.Contains("never alongside QueryAzure calls, then answer with no further tool call", score.Description);
        Assert.DoesNotContain("SuggestFollowUp", score.Description);
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
        Assert.Contains("or more pages for detail the short answer will not show", Prompt);
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
        Assert.Contains("_apiVersion", azure.Description);
        Assert.Contains("GET /providers/{namespace}", azure.Description);
        Assert.Contains("learn.microsoft.com/graph", azure.Description);
        Assert.Contains("learn.microsoft.com/rest/api/cost-management/retail-prices", azure.Description);
        Assert.Contains("learn.microsoft.com/kusto", azure.Description);
        Assert.Contains("learn.microsoft.com/rest/api/storageservices", azure.Description);
        Assert.Contains("azure.status.microsoft", azure.Description);
        // Azure's pricing pages fill their tables in the browser, so five sessions read pages that held no prices.
        Assert.Contains("a GET returns placeholders ($-) instead of prices: take Azure prices from the Retail Prices API and pricing terms (what a price includes, how a meter bills) from Microsoft Learn, never from those pages", azure.Description);
        // An audited SQL estimate left out the log storage; the corrected agent then spent two rounds looking it up, and an
        // answer that applied the rule without a source was judged ungrounded, so one search goes with the first prices.
        Assert.Contains("bill the configured maximum data size plus 30 percent for the log file (500 GB of data bills 650 GB", azure.Description);
        Assert.Contains("send one microsoft_docs_search for SQL Database vCore data and log storage in the same response as the first price request", azure.Description);
        // Several url lines can be POSTs with one body, and combine totals their responses in the same call.
        Assert.Contains("all GET, or all POST with one body sent to each", azure.Description);
        Assert.Contains("combine instead runs once over all responses", azure.Description);
        // A judge failed an Advisor answer that called its complete list "pagination not reported": the host adds pagesRead
        // and complete only when a next link existed, so their absence means the one page was the whole list.
        Assert.Contains("a response with no pagesRead or complete had no next page, so its list is complete", azure.Description);
        // Learn's own search stays the fallback when its MCP tools are not available.
        Assert.Contains("only when those tools are not available, search https://learn.microsoft.com/api/search", azure.Description);
    }

    // The host takes ARM api-versions from ARM itself, so model-facing text never pins a dated one.
    [Fact]
    public void ModelFacingTextNamesNoDatedApiVersion()
    {
        var tokens = new UserTokens { UserId = 101 };
        Microsoft.Extensions.AI.AIFunction[] tools =
        [
            .. ChartTools.Create(), .. new HtmlPresentationTools(101).Create(), .. new ScriptTools(101).Create(),
            .. new MaturityReportTools(101).Create(), .. new AzureFinOps.Dashboard.Jobs.JobOutcomeTools(101).Create(), .. new ReportTools(101).Create(),
            .. new ScoreTools(tokens).Create(), .. new AzureQueryTools(tokens).Create(), .. new SavingsLedgerTools(tokens).Create(),
            .. new UploadedFileTools(tokens).Create(), .. new FaqTools(tokens).Create(),
        ];
        string[] texts =
        [
            AgentSessionFactory.Instructions(webSearch: true),
            AgentSessionFactory.Instructions(webSearch: false),
            AzureQueryTools.ValidateScopePrefix("/providers/Microsoft.CostManagement/query")!,
            AzureQueryTools.ValidateScopePrefix("/providers/Microsoft.Consumption/reservationSummaries")!,
            .. tools.Select(tool => tool.Name + ": " + tool.Description + tool.JsonSchema.GetRawText()),
        ];
        Assert.True(tools.Length > 10);
        Assert.All(texts, text => Assert.DoesNotMatch(@"api-version[= ]\d{4}-\d{2}-\d{2}", text));
    }
}