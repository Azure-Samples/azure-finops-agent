using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Infrastructure;

namespace Dashboard.Tests;

public sealed class ModelJsonTests
{
    [Theory]
    [InlineData("{\"a\":1} }},{", "{\"a\":1}")]
    [InlineData("{\"s\":[\"x\"],\"query\":\"q\"}}]}]}_parameters?", "{\"s\":[\"x\"],\"query\":\"q\"}")]
    [InlineData("{\"a\":1} extra", "{\"a\":1}")]
    [InlineData("{\"subscriptions\":[\"s\"],\"query\":\"Resources | take 5\"", "{\"subscriptions\":[\"s\"],\"query\":\"Resources | take 5\"}")]
    [InlineData("[{\"id\":\"x\"}]}", "[{\"id\":\"x\"}]")]
    [InlineData("[{\"id\":\"x\"},{\"id\":\"y\"}", "[{\"id\":\"x\"},{\"id\":\"y\"}]")]
    [InlineData("[{\"method\":\"GET\",\"url\":\"/u\"]", "[{\"method\":\"GET\",\"url\":\"/u\"}]")]
    [InlineData("[{\"a\":1}},{\"b\":2}]", "[{\"a\":1},{\"b\":2}]")]
    [InlineData("[{\"method\":\"POST\",\"url\":\"/u\",\"body\":{\"q\":\"x\"},{\"method\":\"GET\",\"url\":\"/v\"}]", "[{\"method\":\"POST\",\"url\":\"/u\",\"body\":{\"q\":\"x\"}},{\"method\":\"GET\",\"url\":\"/v\"}]")]
    [InlineData("[{\"method\":\"POST\",\"url\":\"/u\",\"body\":{\"q\":\"x\", {\"method\":\"GET\",\"url\":\"/v\"}]", "[{\"method\":\"POST\",\"url\":\"/u\",\"body\":{\"q\":\"x\"}},{\"method\":\"GET\",\"url\":\"/v\"}]")]
    [InlineData("{\"q\":\"where name == '{x}' and tag == \\\"}]\\\"\"", "{\"q\":\"where name == '{x}' and tag == \\\"}]\\\"\"}")]
    [InlineData("{\"a\":[1,2}", "{\"a\":[1,2]}")]
    [InlineData("[{\"path\":\"$.x\",\"limit\":50}]}}]}]}imuhamedassistant to=functions.QueryAzure? no, response multi tool. Let's see. The \"", "[{\"path\":\"$.x\",\"limit\":50}]")]
    [InlineData("{\"a\":1} assistant to=functions.QueryToolResult {\"b\":2}", "{\"a\":1}")]
    [InlineData("[{\"limit\":100}]} Hm tool call syntax JSON array is queryJson string yes. Transcript: assistant to=functions.QueryToolResult ... \"", "[{\"limit\":100}]")]
    public void RepairsOnlyStructuralBrackets(string text, string expected) => Assert.Equal(expected, ModelJson.Repair(text));

    [Theory]
    [InlineData("{\"a\":")]
    [InlineData("{\"a\":\"unterminated")]
    [InlineData("{\"a\":1} | take 5")]
    [InlineData("{\"a\":1} note: to=functions is mentioned later")]
    [InlineData("{\"a\":1} ,\"limit\":50} to=functions.X")]
    [InlineData("{\"a\":1}{\"b\":2}")]
    [InlineData("{\"a\":1,{\"b\":2}}")]
    [InlineData("{{\"a\":1}}")]
    [InlineData("\"text\"")]
    [InlineData("")]
    public void UnrepairableTextIsNotParsed(string text)
    {
        var repaired = ModelJson.Repair(text);
        if (repaired is null) return;
        Assert.Null(ModelJson.TryParse(text, JsonValueKind.Object));
        Assert.Null(ModelJson.TryParse(text, JsonValueKind.Array));
    }

    [Fact]
    public void ParsedRootMustHaveTheExpectedKind()
    {
        Assert.Null(ModelJson.TryParse("[1,2", JsonValueKind.Object));
        using var array = ModelJson.TryParse("[1,2", JsonValueKind.Array);
        Assert.Equal(2, array!.RootElement.GetArrayLength());
        using var lenient = ModelJson.TryParse("{\"a\":1,// note\n}", JsonValueKind.Object);
        Assert.Equal(1, lenient!.RootElement.GetProperty("a").GetInt32());
    }

    [Fact]
    public void GarbledBatchesKeepEveryRequest()
    {
        var items = AzureQueryTools.ParseBatch("[{\"method\":\"POST\",\"url\":\"/providers/Microsoft.ResourceGraph/resources?api-version=2022-10-01\",\"body\":{\"subscriptions\":[\"s\"],\"query\":\"Resources | summarize n=count()\"},{\"method\":\"GET\",\"url\":\"/b\"}]}");
        Assert.Equal(2, items.Count);
        Assert.Equal("Resources | summarize n=count()", JsonDocument.Parse(items[0].Body!).RootElement.GetProperty("query").GetString());
        Assert.Equal(("GET", "/b"), (items[1].Method, items[1].Path));
    }

    [Fact]
    public void RetainedResultQueriesMissingTheirFinalBraceRun()
    {
        var entry = new ToolResultStore().Retain(101, "session", """{"rows":[{"count":4},{"count":6}]}""", new ToolResultStore.Source("SyntheticRead", DateTimeOffset.UtcNow, true, false, true, ""))!;
        using var result = JsonDocument.Parse(ToolResultQueryTools.Execute(entry, """{"path":"$.rows[*]","aggregates":[{"op":"sum","path":"$.count","as":"total"}]"""));
        Assert.Equal(10m, result.RootElement.GetProperty("totals").GetProperty("total").GetDecimal());
    }
}
