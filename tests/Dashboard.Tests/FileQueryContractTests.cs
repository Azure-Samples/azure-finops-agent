using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;

namespace Dashboard.Tests;

public sealed class FileQueryContractTests
{
    [Fact]
    public void ArraysAndObjectsRemainStructuredAcrossTheHostBoundary()
    {
        using var input = JsonDocument.Parse("{\"group_by\":[\"month\",\"category\"],\"columns\":[\"month\",\"total\"],\"filters\":[{\"column\":\"cost\",\"op\":\"gt\",\"value\":1.5}]}");
        var converted = UploadedFileTools.JsonValueToObject(input.RootElement);
        var output = JsonSerializer.SerializeToElement(converted);
        Assert.Equal(JsonValueKind.Array, output.GetProperty("group_by").ValueKind);
        Assert.Equal(JsonValueKind.Array, output.GetProperty("columns").ValueKind);
        Assert.Equal("total", output.GetProperty("columns")[1].GetString());
        Assert.Equal(JsonValueKind.Object, output.GetProperty("filters")[0].ValueKind);
        Assert.Equal(1.5m, output.GetProperty("filters")[0].GetProperty("value").GetDecimal());
    }
}