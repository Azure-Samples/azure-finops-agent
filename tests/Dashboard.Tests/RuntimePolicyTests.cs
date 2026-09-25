using System.Text.Json;
using AzureFinOps.Dashboard.AI;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Auth;
using GitHub.Copilot;
using Microsoft.Extensions.AI;

namespace Dashboard.Tests;

public sealed class RuntimePolicyTests
{
    [Theory]
    [InlineData(false, 3)]
    [InlineData(false, 4)]
    [InlineData(true, 3)]
    [InlineData(true, 4)]
    public async Task CostQueriesRejectExcessGroupingBeforeDispatch(bool bulk, int dimensions)
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
        var tool = new AzureQueryTools(new UserTokens { UserId = 101, AzureToken = "synthetic-test-only" }).Create()
            .Single(candidate => candidate.Name == (bulk ? "BulkAzureRequest" : "QueryAzure"));
        var arguments = bulk
            ? new AIFunctionArguments { ["requestsJson"] = JsonSerializer.Serialize(new[] { new { method = "POST", path, body } }) }
            : new AIFunctionArguments { ["method"] = "POST", ["path"] = path, ["body"] = body };
        var result = (await tool.InvokeAsync(arguments))!.ToString()!;
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

    [Fact]
    public async Task NullBatchItemsAreRejectedBeforeDispatch()
    {
        var tool = new AzureQueryTools(new UserTokens { UserId = 101, AzureToken = "synthetic-test-only" }).Create()
            .Single(candidate => candidate.Name == "BulkAzureRequest");
        var result = (await tool.InvokeAsync(new AIFunctionArguments { ["requestsJson"] = "[null]" }))!.ToString()!;
        Assert.Contains("Every batch item must be a request object", result);
        Assert.Contains("No request was sent", result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateAndResumeExposeOnlyHostTools(bool resume)
    {
        SessionConfigBase config = resume ? new ResumeSessionConfig() : new SessionConfig();
        config.Tools = [AIFunctionFactory.Create(() => "synthetic result", "ApprovedRead")];
        RuntimePolicy.Apply(config);
        Assert.Equal(["custom:ApprovedRead"], config.AvailableTools);
        Assert.Contains("builtin:*", config.ExcludedTools!);
        Assert.Contains("mcp:*", config.ExcludedTools!);
        Assert.False(config.EnableSessionStore);
        Assert.False(config.Memory!.Enabled);
        Assert.False(config.ToolSearch!.Enabled);
        Assert.Equal("disable", config.ManagedSettings!.Permissions!.DisableBypassPermissionsMode);
        Assert.NotNull(config.OnPermissionRequest);
        Assert.NotNull(config.Hooks!.OnPreToolUse);
    }

    [Theory]
    [InlineData("QueryAzure", null, "$filter")]
    [InlineData("QueryAzure", null, "look it up instead of guessing")]
    [InlineData("BulkAzureRequest", null, "sequentially")]
    [InlineData("QueryGraph", null, "learn.microsoft.com/graph")]
    [InlineData("QueryLogAnalytics", "query", "summarize")]
    [InlineData("ListCostExportBlobs", "prefix", "prefix")]
    [InlineData("ReadCostExportBlob", "blobPath", "exact")]
    [InlineData("QueryUploadedFile", "paramsJson", "filters")]
    [InlineData("CheckVmConnectivity", "sourceVmResourceId", "VM")]
    [InlineData("GetAzureRetailPricing", "filter", "OData")]
    [InlineData("GetSavingsLedger", "status", "filter")]
    [InlineData("GetSavingsLedger", "limit", "limit")]
    [InlineData("StartPricesheetDownload", "billingScope", "billing scope")]
    [InlineData("GetPricesheetStatus", "operationStatusUrl", "returned")]
    [InlineData("GetOperationStatus", "operationId", "exact")]
    [InlineData("ListOperationResults", null, "current conversation")]
    [InlineData("GetAzureServiceHealth", null, "no service/region filtering")]
    public void QueryGuidanceIsPresentInToolAndParameterSchemas(string toolName, string? parameterName, string guidance)
    {
        var tokens = new UserTokens { UserId = 101 };
        var tools = toolName switch
        {
            "QueryAzure" or "BulkAzureRequest" => new AzureQueryTools(tokens).Create(),
            "QueryGraph" => new GraphQueryTools(tokens).Create(),
            "QueryLogAnalytics" => new LogAnalyticsQueryTools(tokens).Create(),
            "ListCostExportBlobs" or "ReadCostExportBlob" => new StorageQueryTools(tokens).Create(),
            "QueryUploadedFile" => new UploadedFileTools(tokens).Create(),
            "CheckVmConnectivity" => new ComputeDiagnosticTools(tokens).Create(),
            "GetAzureRetailPricing" => RetailPricingTools.Create(),
            "GetSavingsLedger" => new SavingsLedgerTools(tokens).Create(),
            "StartPricesheetDownload" or "GetPricesheetStatus" => new PricesheetTools(tokens).Create(),
            "GetOperationStatus" or "ListOperationResults" => new OperationTools(tokens).Create(),
            "GetAzureServiceHealth" => HealthTools.Create(),
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
        var tool = new ScriptTools(101).Create().Single();
        Assert.Contains("complete code", tool.Description);
        Assert.Contains("never executes", tool.Description);
        Assert.DoesNotContain("ONLY AFTER", tool.Description);
        var content = tool.JsonSchema.GetProperty("properties").GetProperty("scriptContent");
        Assert.Contains("complete executable", content.GetProperty("description").GetString()!);
        Assert.Contains("GenerateScript directly", CopilotSessionFactory.SystemPrompt);
    }

    [Fact]
    public void ScriptDeliveryIsOnlyAProposedSavingsAction()
    {
        var tool = new SavingsLedgerTools(new UserTokens { UserId = 101 }).Create()
            .Single(candidate => candidate.Name == "RecordSavingsAction");
        Assert.Contains("proposed, not executed", tool.Description);
        var status = tool.JsonSchema.GetProperty("properties").GetProperty("status");
        Assert.Contains("generation alone is never execution", status.GetProperty("description").GetString()!);
        Assert.Contains("A generated script is not an executed change", CopilotSessionFactory.SystemPrompt);
    }

    [Theory]
    [InlineData("RenderChart", "data", "scoped")]
    [InlineData("RenderAdvancedChart", "options", "scoped")]
    [InlineData("EstimateTokenCost", "modelsJson", "requested")]
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
            "EstimateTokenCost" => CostEstimateTools.Create(),
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