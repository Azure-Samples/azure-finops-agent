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
    public void ScriptContractSeparatesKqlFromOutputSelectionAndPreservesFailures()
    {
        var tool = GetTool("GenerateScript");
        Assert.Contains("az graph query --graph-query (or -q) for KQL", tool.Description);
        Assert.Contains("--query is only the JMESPath selector", tool.Description);
        Assert.Contains("a missing count is unknown, never zero", tool.Description);

        var content = tool.JsonSchema.GetProperty("properties").GetProperty("scriptContent")
            .GetProperty("description").GetString();
        Assert.Contains("KQL goes in --graph-query/-q", content);
        Assert.Contains("never replace failed queries or invalid/missing counts with zero", content);
        Assert.Contains("never a script that writes it", tool.Description);
        Assert.Contains("read the answer from the terminal (read -r answer < /dev/tty)", tool.Description);
        Assert.Contains("a plain read there consumes the next candidate", tool.Description);
        Assert.Contains("reads from the terminal (read -r answer < /dev/tty)", content);
        var language = tool.JsonSchema.GetProperty("properties").GetProperty("language");
        Assert.Contains("'arm' for an ARM template .json", language.GetProperty("description").GetString());
        Assert.DoesNotContain("language", tool.JsonSchema.GetProperty("required").EnumerateArray().Select(name => name.GetString()));
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

    // Run 36481780607: the model appended leaked format tokens after the array; the tool
    // reported success, the browser could not parse it, and the promised chart never rendered.
    [Theory]
    [InlineData("[{\"name\":\"A\",\"value\":1}]}  亚洲日韩functions.RenderChartոխcommentary.debate {", "not valid JSON")]
    [InlineData("[{\"name\":\"A\",\"value\":1},]", "not valid JSON")]
    [InlineData("name,value\nA,1", "not valid JSON")]
    [InlineData("[]", "non-empty")]
    [InlineData("{\"name\":\"A\",\"value\":1}", "non-empty")]
    [InlineData("[1,2,3]", "entries must be objects")]
    [InlineData("  ", "requires data")]
    public async Task MalformedChartDataFailsSoTheModelResendsIt(string data, string expected)
    {
        var result = await GetTool("RenderChart").InvokeAsync(new AIFunctionArguments
        {
            ["type"] = "horizontal_bar", ["title"] = "Synthetic chart", ["seriesName"] = "Cost", ["data"] = data
        });
        var text = result!.ToString()!;
        Assert.StartsWith("Error: RenderChart", text);
        Assert.Contains(expected, text);
        Assert.Contains("Nothing was rendered", text);
        Assert.DoesNotContain("functions.RenderChart", text);
    }

    [Fact]
    public async Task ChartDataSentAsAJsonArrayIsAcceptedAsItsText()
    {
        using var array = JsonDocument.Parse("[{\"name\":\"A\",\"value\":1}]");
        var result = await GetTool("RenderChart").InvokeAsync(new AIFunctionArguments
        {
            ["type"] = "bar", ["title"] = "Synthetic chart", ["seriesName"] = "Cost", ["data"] = array.RootElement.Clone()
        });
        using var response = JsonDocument.Parse(Assert.IsType<JsonElement>(result).GetString()!);
        Assert.Equal("[{\"name\":\"A\",\"value\":1}]", response.RootElement.GetProperty("data").GetString());
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
