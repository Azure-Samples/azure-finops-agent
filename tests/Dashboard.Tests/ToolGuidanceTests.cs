using System.Text.Json;
using AzureFinOps.Dashboard.AI;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Auth;
using Microsoft.Extensions.AI;

namespace Dashboard.Tests;

public sealed class ToolGuidanceTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task CostQueriesRejectExcessGroupingBeforeDispatch(int dimensions)
    {
        const string path = "/subscriptions/11111111-1111-1111-1111-111111111111/providers/Microsoft.CostManagement/query?api-version=2026-08-01";
        var body = JsonSerializer.Serialize(new
        {
            dataset = new
            {
                grouping = new[] { "SubscriptionName", "ResourceGroupName", "ResourceId", "ServiceName" }
                    .Take(dimensions).Select(name => new { type = "Dimension", name })
            }
        });
        var tool = new AzureQueryTools(new UserTokens { UserId = 101, AzureToken = "synthetic-test-only" }).Create().First();
        var result = (await tool.InvokeAsync(new AIFunctionArguments { ["method"] = "POST", ["url"] = path, ["body"] = body }))!.ToString()!;
        Assert.Contains("at most two grouping dimensions", result);
        Assert.Contains("No request was sent", result);
    }

    [Fact]
    public void TwoGroupingDimensionsAreAccepted()
    {
        Assert.Null(AzureQueryTools.ValidateCostQueryBody("/providers/Microsoft.CostManagement/query", "{\"dataset\":{\"grouping\":[{\"type\":\"Dimension\",\"name\":\"ResourceId\"},{\"type\":\"Dimension\",\"name\":\"Meter\"}]}}"));
    }

    [Fact]
    public void LenientPostBodiesAreForwardedAsStrictJson()
    {
        var canonical = AzureQueryTools.CanonicalJsonBody("{\"type\":\"ActualCost\", // cost type\n\"dataset\":{\"granularity\":\"None\",},}");
        using var document = JsonDocument.Parse(canonical!);
        Assert.Equal("ActualCost", document.RootElement.GetProperty("type").GetString());
        Assert.Null(AzureQueryTools.ValidateCostQueryBody("/providers/Microsoft.CostManagement/query", canonical));
        Assert.Equal("{not json", AzureQueryTools.CanonicalJsonBody("{not json"));
    }

    [Theory]
    [InlineData("QueryAzure", null, "$filter")]
    [InlineData("QueryAzure", null, "look it up instead of guessing")]
    [InlineData("QueryAzure", null, "one at a time")]
    [InlineData("QueryAzure", null, "learn.microsoft.com/graph")]
    [InlineData("QueryAzure", null, "summarize")]
    [InlineData("QueryAzure", null, "prefix")]
    [InlineData("QueryAzure", null, "connectivityCheck from an existing VM")]
    [InlineData("QueryAzure", null, "OData")]
    [InlineData("QueryAzure", null, "pricesheet download")]
    [InlineData("QueryAzure", null, "not tenant-specific")]
    [InlineData("QueryAzure", null, "license-included pricing adds the tier's '- SQL License' product")]
    [InlineData("QueryAzure", null, "no documentation lookup or web search first")]
    [InlineData("QueryAzure", null, "Convert units in the same query that selects the rows")]
    [InlineData("QueryAzure", null, "returned rows carry priceType as type, so a query selects x.type, never x.priceType")]
    [InlineData("QueryAzure", "query", "LINQ")]
    [InlineData("QueryAzure", "url", "one per line")]
    [InlineData("ReportMaturityScore", null, "/providers/Microsoft.CostManagement/exports?api-version=2026-08-01")]
    [InlineData("ReportMaturityScore", null, "/providers/Microsoft.CostManagement/scheduledActions?api-version=2026-08-01")]
    [InlineData("ReportMaturityScore", null, "with no provider, documentation or spec lookup first")]
    [InlineData("ApplyAzureChange", null, "only after they approve")]
    [InlineData("ApplyAzureChange", null, "GenerateScript")]
    [InlineData("QueryUploadedFile", "paramsJson", "filters")]
    [InlineData("GetSavingsLedger", "status", "filter")]
    [InlineData("GetSavingsLedger", "limit", "limit")]
    public void QueryGuidanceIsPresentInToolAndParameterSchemas(string toolName, string? parameterName, string guidance)
    {
        var tokens = new UserTokens { UserId = 101 };
        var tools = toolName switch
        {
            "QueryAzure" or "ApplyAzureChange" => new AzureQueryTools(tokens).Create(),
            "ReportMaturityScore" => new ScoreTools(tokens).Create(),
            "QueryUploadedFile" => new UploadedFileTools(tokens).Create(),
            "GetSavingsLedger" => new SavingsLedgerTools(tokens).Create(),
            _ => throw new InvalidOperationException("Unexpected query tool.")
        };
        var tool = tools.Single(candidate => candidate.Name == toolName);
        Assert.Contains(guidance, tool.Description, StringComparison.OrdinalIgnoreCase);
        if (parameterName is not null)
        {
            var parameter = tool.JsonSchema.GetProperty("properties").GetProperty(parameterName);
            Assert.Contains(guidance, parameter.GetProperty("description").GetString()!, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ScriptGuidanceRequiresCompleteCodeWithoutHostExecution()
    {
        var tool = new ScriptTools(101).Create().First();
        Assert.Contains("complete code", tool.Description);
        Assert.Contains("never executes", tool.Description);
        Assert.DoesNotContain("ONLY AFTER", tool.Description);
        var content = tool.JsonSchema.GetProperty("properties").GetProperty("scriptContent");
        Assert.Contains("complete executable", content.GetProperty("description").GetString()!);
        Assert.Contains("GenerateScript directly", AgentSessionFactory.SystemPrompt);
    }

    [Fact]
    public void ScriptDeliveryIsOnlyAProposedSavingsAction()
    {
        var tool = new SavingsLedgerTools(new UserTokens { UserId = 101 }).Create()
            .Single(candidate => candidate.Name == "RecordSavingsAction");
        Assert.Contains("proposed, not executed", tool.Description);
        var status = tool.JsonSchema.GetProperty("properties").GetProperty("status");
        Assert.Contains("generation alone is never execution", status.GetProperty("description").GetString()!);
        Assert.Contains("A generated script is not an executed change", AgentSessionFactory.SystemPrompt);
    }

    [Theory]
    [InlineData("RenderChart", "data", "scoped")]
    [InlineData("RenderAdvancedChart", "options", "scoped")]
    [InlineData("GenerateDataReport", "dataJson", "sourceRowCount")]
    [InlineData("GenerateHtmlPresentation", "slidesJson", "scope")]
    [InlineData("GenerateMaturityReport", "reportJson", "source aggregates")]
    [InlineData("SuggestFollowUp", "prompt", "scope")]
    [InlineData("ReportJobOutcome", "summary", "scope")]
    public void OutputToolsDescribeBoundedEvidenceInputs(string toolName, string parameterName, string guidance)
    {
        var tools = toolName switch
        {
            "RenderChart" or "RenderAdvancedChart" => ChartTools.Create(),
            "GenerateDataReport" => new ReportTools(101).Create(),
            "GenerateHtmlPresentation" => new HtmlPresentationTools(101).Create(),
            "GenerateMaturityReport" => new MaturityReportTools(101).Create(),
            "SuggestFollowUp" => FollowUpTools.Create(),
            "ReportJobOutcome" => new AzureFinOps.Dashboard.Jobs.JobOutcomeTools(101).Create(),
            _ => throw new InvalidOperationException("Unexpected output tool.")
        };
        var tool = tools.Single(candidate => candidate.Name == toolName);
        Assert.Contains(guidance, tool.Description, StringComparison.OrdinalIgnoreCase);
        var parameter = tool.JsonSchema.GetProperty("properties").GetProperty(parameterName);
        Assert.Contains(guidance, parameter.GetProperty("description").GetString()!, StringComparison.OrdinalIgnoreCase);
    }
}