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

    [Fact]
    public void GlobalTopTenIsRankedBeforeOutputLimiting()
    {
        var result = RetailPricingTools.CompactBatchResult(Payload(220), topPerVariant: 10);
        var resolution = ReadResolution(result);
        Assert.Equal(220, resolution.GetProperty("fetchedRows").GetInt32());
        Assert.Equal(10, resolution.GetProperty("deliveredRows").GetInt32());
        Assert.True(resolution.GetProperty("complete").GetBoolean());
        Assert.True(resolution.GetProperty("rankingComplete").GetBoolean());
        Assert.False(resolution.GetProperty("detailsComplete").GetBoolean());
        Assert.Contains("10\tregion9\t", result);
        Assert.DoesNotContain("11\tregion10\t", result);
        Assert.True(result.Length < 8_000);
    }

    [Fact]
    public void RankedIncompleteCatalogueStillDisclosesMissingCoverage()
    {
        var result = RetailPricingTools.CompactBatchResult(Payload(65), paginationComplete: false, topPerVariant: 10);
        var resolution = ReadResolution(result);
        Assert.False(resolution.GetProperty("complete").GetBoolean());
        Assert.False(resolution.GetProperty("rankingComplete").GetBoolean());
        Assert.Equal("partial", resolution.GetProperty("status").GetString());
    }

    [Fact]
    public void RankingsKeepSpotAndOrdinaryMetersSeparate()
    {
        var json = JsonSerializer.Serialize(new
        {
            BillingCurrency = "USD",
            Items = new[] { "Ordinary", "Spot" }.SelectMany(meter => Enumerable.Range(0, 30).Select(index => new
            {
                armRegionName = $"region{index}", armSkuName = "synthetic", skuName = meter,
                meterName = meter, productName = "Synthetic", unitOfMeasure = "1 Hour", type = "Consumption",
                retailPrice = meter == "Spot" ? (index + 1) / 100d : index + 1d
            }))
        });
        var result = RetailPricingTools.CompactBatchResult(json, topPerVariant: 3);
        Assert.Equal(6, ReadResolution(result).GetProperty("deliveredRows").GetInt32());
        Assert.Contains("1\tregion0\tsynthetic\tSynthetic\tOrdinary", result);
        Assert.Contains("0.01\tregion0\tsynthetic\tSynthetic\tSpot", result);
    }

    [Fact]
    public void VolumeBandsRemainSeparateAndRetainTheirThresholds()
    {
        var json = JsonSerializer.Serialize(new
        {
            BillingCurrency = "CAD",
            Items = new[] { 0, 50_000 }.Select(minimum => new
            {
                armRegionName = "synthetic", armSkuName = "synthetic", skuName = "Hot",
                meterName = "Stored Data", productName = "Synthetic storage", unitOfMeasure = "1 GB/Month",
                type = "Consumption", retailPrice = minimum == 0 ? 0.02 : 0.01, tierMinimumUnits = minimum
            })
        });
        var result = RetailPricingTools.CompactBatchResult(json, topPerVariant: 1);
        var resolution = ReadResolution(result);
        Assert.Equal(2, resolution.GetProperty("deliveredRows").GetInt32());
        Assert.Equal(2, resolution.GetProperty("variants").GetInt32());
        Assert.Contains("tierMinimumUnits\tcurrencyCode", result);
        Assert.Contains("\t50000\tCAD", result);
        Assert.Contains("\t0\tCAD", result);
    }

    private static JsonElement ReadResolution(string result) => JsonSerializer.Deserialize<JsonElement>(
        result.Split('\n').Single(line => line.StartsWith("RESOLUTION "))["RESOLUTION ".Length..]);
}