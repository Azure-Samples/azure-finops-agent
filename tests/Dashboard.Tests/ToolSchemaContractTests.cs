using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Infrastructure;
using Microsoft.Extensions.AI;

namespace Dashboard.Tests;

public sealed class ToolSchemaContractTests
{
    [Theory]
    [InlineData("RenderChart", "xAxisName")]
    [InlineData("RenderChart", "yAxisName")]
    [InlineData("GenerateScript", "filename")]
    [InlineData("GenerateScript", "language")]
    [InlineData("GenerateScript", "description")]
    [InlineData("GenerateHtmlPresentation", "filename")]
    [InlineData("QueryUploadedFile", "paramsJson")]
    [InlineData("UpdateSavingsAction", "verifiedMonthlyUsd")]
    public void DocumentedOptionalInputsAreNotRequired(string toolName, string parameter)
    {
        var tool = GetTool(toolName);
        var required = tool.JsonSchema.TryGetProperty("required", out var names)
            ? names.EnumerateArray().Select(name => name.GetString()).ToArray()
            : [];
        Assert.DoesNotContain(parameter, required);
    }

    [Fact]
    public async Task ChartAxesCanBeOmittedAtInvocation()
    {
        var result = await GetTool("RenderChart").InvokeAsync(new AIFunctionArguments
        {
            ["type"] = "bar",
            ["title"] = "Synthetic costs",
            ["seriesName"] = "USD",
            ["data"] = "[[\"A\",10],[\"B\",20]]"
        });
        using var chart = JsonDocument.Parse(Assert.IsType<JsonElement>(result).GetString()!);
        Assert.Equal(JsonValueKind.Null, chart.RootElement.GetProperty("xAxisName").ValueKind);
        Assert.Equal(JsonValueKind.Null, chart.RootElement.GetProperty("yAxisName").ValueKind);
    }

    [Theory]
    [InlineData("GenerateScript", "scriptContent", "printf 'synthetic\\n'", "finops-remediation.sh", "__SCRIPT_READY__:")]
    [InlineData("GenerateHtmlPresentation", "slidesJson", "[{\"layout\":\"title\",\"title\":\"Synthetic report\"}]", "FinOps-Deck.html", "__HTML_READY__:")]
    public async Task ArtifactDefaultsWorkWhenOptionalInputsAreOmitted(
        string toolName, string inputName, string content, string filename, string prefix)
    {
        var result = await GetTool(toolName).InvokeAsync(new AIFunctionArguments { [inputName] = content });
        var marker = Assert.IsType<JsonElement>(result).GetString()!;
        Assert.StartsWith(prefix, marker);
        var identifier = marker.Split(':')[1];
        try
        {
            var artifact = ArtifactStore.Default.Find(identifier, 101);
            Assert.NotNull(artifact);
            Assert.Equal(filename, artifact.FileName);
            Assert.Null(ArtifactStore.Default.Find(identifier, 202));
        }
        finally { ArtifactStore.Default.Remove(identifier, 101); }
    }

    [Fact]
    public async Task ParameterlessUploadModeReachesTheOwnerCheckedReader()
    {
        var result = await GetTool("QueryUploadedFile").InvokeAsync(new AIFunctionArguments
        {
            ["fileId"] = "synthetic-missing-upload",
            ["mode"] = "workbook"
        });
        using var response = JsonDocument.Parse(Assert.IsType<JsonElement>(result).GetString()!);
        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("upload_unavailable", response.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("bar", "[{\"name\":\"Synthetic\",\"value\":10}]")]
    [InlineData("bar", "[[\"Synthetic\",10],[\"Other\",20]]")]
    [InlineData("pie", "[[\"Synthetic\",10],[\"Other\",20]]")]
    public async Task RetainedChartAliasIsNormalizedWithoutChangingTheData(string type, string data)
    {
        var result = await GetTool("RenderChart").InvokeAsync(new AIFunctionArguments
        {
            ["chart"] = type, ["title"] = "Synthetic chart", ["seriesName"] = "Count", ["data"] = data
        });
        using var response = JsonDocument.Parse(Assert.IsType<JsonElement>(result).GetString()!);
        Assert.Equal(type, response.RootElement.GetProperty("type").GetString());
        Assert.Equal(data, response.RootElement.GetProperty("data").GetString());
    }

    [Fact]
    public async Task ConflictingChartKindsReturnAnActionableError()
    {
        var result = await GetTool("RenderChart").InvokeAsync(new AIFunctionArguments { ["type"] = "bar", ["chart"] = "pie" });
        Assert.Contains("conflicting type and chart", result!.ToString());
    }

    private static AIFunction GetTool(string name)
    {
        var tools = name switch
        {
            "RenderChart" => ChartTools.Create(),
            "GenerateScript" => new ScriptTools(101).Create(),
            "GenerateHtmlPresentation" => new HtmlPresentationTools(101).Create(),
            "QueryUploadedFile" => new UploadedFileTools(new UserTokens { UserId = 101 }).Create(),
            "UpdateSavingsAction" => new SavingsLedgerTools(new UserTokens { UserId = 101 }).Create(),
            _ => throw new InvalidOperationException("Unexpected synthetic tool.")
        };
        return tools.Single(tool => tool.Name == name);
    }
}
