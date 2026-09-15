using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;

namespace Dashboard.Tests;

public sealed class RetailPricingTests
{
    private static string Payload(int regions) => JsonSerializer.Serialize(new
    {
        BillingCurrency = "USD",
        Items = Enumerable.Range(0, regions).Select(index => new
        {
            armRegionName = $"region{index}", armSkuName = "synthetic", skuName = "synthetic",
            meterName = "OnDemand", productName = "Synthetic service", unitOfMeasure = "1 Hour", type = "Consumption", retailPrice = index + 1d
        })
    });

    [Fact]
    public void RegionComparisonRetainsAllSixtyFiveRegions()
    {
        var result = RetailPricingTools.CompactBatchResult(Payload(65));
        Assert.Contains("region64", result);
        var resolution = ReadResolution(result);
        Assert.True(resolution.GetProperty("complete").GetBoolean());
        Assert.Equal(65, resolution.GetProperty("deliveredRows").GetInt32());
    }

    [Fact]
    public void CapsAndUnfinishedPaginationAreExplicitlyPartial()
    {
        var result = RetailPricingTools.CompactBatchResult(Payload(220), paginationComplete: false, requestedRegions: ["absent"]);
        var resolution = ReadResolution(result);
        Assert.False(resolution.GetProperty("complete").GetBoolean());
        Assert.Equal("partial", resolution.GetProperty("status").GetString());
        Assert.Equal(200, resolution.GetProperty("deliveredRows").GetInt32());
        Assert.Equal("absent", resolution.GetProperty("missingRequestedRegions")[0].GetString());
    }

    [Fact]
    public void SkuFieldFallbackRetainsExactValueAndEscapesOData()
    {
        Assert.Equal("armSkuName eq 'B1'", RetailPricingTools.SkuFilter("B1", false));
        Assert.Equal("skuName eq 'B1'", RetailPricingTools.SkuFilter("B1", true));
        Assert.Equal("skuName eq 'test''sku'", RetailPricingTools.SkuFilter("test'sku", true));
    }

    private static JsonElement ReadResolution(string result) => JsonSerializer.Deserialize<JsonElement>(
        result.Split('\n').Single(line => line.StartsWith("RESOLUTION "))["RESOLUTION ".Length..]);
}