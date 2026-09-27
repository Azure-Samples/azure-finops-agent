using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Infrastructure;
using Microsoft.Extensions.AI;

namespace Dashboard.Tests;

public sealed class ModelJsonTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void NativeJsonArgumentsAreForwardedAndStringsAreRead()
    {
        Assert.Equal("", ModelJson.Text(null));
        Assert.Equal("", ModelJson.Text(Json("null")));
        Assert.Equal("[{\"url\":\"/a\"}]", ModelJson.Text(Json("[{\"url\":\"/a\"}]")));
        Assert.Equal("{\"a\":1}", ModelJson.Text(Json("\"{\\\"a\\\":1}\"")));
    }

    [Fact]
    public void LenientJsonIsReadButGarbledJsonIsNeverRepaired()
    {
        using var lenient = ModelJson.TryParse("{\"a\":1,// note\n}", JsonValueKind.Object);
        Assert.Equal(1, lenient!.RootElement.GetProperty("a").GetInt32());
        Assert.Null(ModelJson.TryParse("[1,2]", JsonValueKind.Object));
        foreach (var garbled in new[] { "[1,2", "{\"a\":1} }},{", "[{\"a\":1}},{\"b\":2}]", "{\"a\":\"unterminated", "" })
            Assert.Null(ModelJson.TryParse(garbled, JsonValueKind.Object) ?? ModelJson.TryParse(garbled, JsonValueKind.Array));
        Assert.Throws<FormatException>(() => AzureQueryTools.ParseBatch("[{\"url\":\"/a\"},{\"url\":\"/b\"]"));
    }

    [Fact]
    public async Task QueryToolResultAcceptsANativeQueryArray()
    {
        var entry = ToolResultStore.Default.Retain(101, "native-session", """{"rows":[{"count":4},{"count":6}]}""",
            new ToolResultStore.Source("SyntheticRead", DateTimeOffset.UtcNow, true, false, true, ""))!;
        var tool = new ToolResultQueryTools(101).Create().Single();
        using var context = new ToolExecutionContext("native-session", 101, CancellationToken.None);
        var output = (await tool.InvokeAsync(new AIFunctionArguments
        {
            ["resultId"] = entry.Id,
            ["queryJson"] = Json("""[{"path":"$.rows[*]","select":{"c":"$.count"}},{"path":"$.rows[*]","aggregates":[{"op":"sum","path":"$.count","as":"total"}],"limit":0}]"""),
        }))!.ToString()!;
        using var result = JsonDocument.Parse(output);
        Assert.Equal(10m, result.RootElement.GetProperty("queries")[1].GetProperty("totals").GetProperty("total").GetDecimal());
    }

    [Fact]
    public void JsonParametersAdvertiseNativeJson()
    {
        var properties = new AzureQueryTools(new AzureFinOps.Dashboard.Auth.UserTokens { UserId = 101 }).Create().Single().JsonSchema.GetProperty("properties");
        foreach (var name in new[] { "body", "requests", "forEach", "resultQuery" })
            Assert.False(properties.GetProperty(name).TryGetProperty("type", out _), name);
        Assert.False(new ToolResultQueryTools(101).Create().Single().JsonSchema.GetProperty("properties").GetProperty("queryJson").TryGetProperty("type", out _));
    }
}
