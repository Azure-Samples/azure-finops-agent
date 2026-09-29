using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;

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
    }

    [Fact]
    public void JsonParametersAdvertiseNativeJson()
    {
        var properties = new AzureQueryTools(new AzureFinOps.Dashboard.Auth.UserTokens { UserId = 101 }).Create().First().JsonSchema.GetProperty("properties");
        Assert.False(properties.GetProperty("body").TryGetProperty("type", out _));
        Assert.Equal("string", properties.GetProperty("url").GetProperty("type").GetString());
        Assert.Equal("string", properties.GetProperty("query").GetProperty("type").GetString());
    }
}