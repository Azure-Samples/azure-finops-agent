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
    public void SourceUrlPreservesFiltersWithoutUnsupportedTop()
    {
        const string filter = "serviceName eq 'Synthetic' and contains(skuName, 'model&name')";
        var uri = new Uri(RetailPricingTools.BuildRetailUrl(filter, "CAD"));
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);

        Assert.Equal("https", uri.Scheme);
        Assert.Equal("prices.azure.com", uri.Host);
        Assert.Equal("2023-01-01-preview", query["api-version"].ToString());
        Assert.Equal("CAD", query["currencyCode"].ToString());
        Assert.Equal(filter, query["$filter"].ToString());
        Assert.Equal(3, query.Count);
        Assert.False(query.ContainsKey("$top"));
    }

    [Fact]
    public void RepeatedCheapRegionsDoNotBuryOtherMeters()
    {
        var json = JsonSerializer.Serialize(new
        {
            BillingCurrency = "USD",
            Items = new[] { "Cached", "Ordinary input", "Ordinary output" }
                .SelectMany(meter => Enumerable.Range(0, meter == "Cached" ? 220 : 2).Select(index => new
                {
                    armRegionName = $"region{index}", armSkuName = "synthetic", skuName = meter,
                    meterName = meter, productName = "Synthetic", unitOfMeasure = "1K", type = "Consumption",
                    retailPrice = meter == "Cached" ? 0.001 : 0.01
                }))
        });

        var result = RetailPricingTools.CompactBatchResult(json);
        var resolution = ReadResolution(result);
        Assert.Equal(224, resolution.GetProperty("fetchedRows").GetInt32());
        Assert.Equal(200, resolution.GetProperty("deliveredRows").GetInt32());
        Assert.False(resolution.GetProperty("complete").GetBoolean());
        Assert.True(resolution.GetProperty("paginationComplete").GetBoolean());
        Assert.Contains("0.01\tregion0\tsynthetic\tSynthetic\tOrdinary input\t", result);
        Assert.Contains("0.01\tregion1\tsynthetic\tSynthetic\tOrdinary input\t", result);
        Assert.Contains("0.01\tregion0\tsynthetic\tSynthetic\tOrdinary output\t", result);
        Assert.Contains("0.01\tregion1\tsynthetic\tSynthetic\tOrdinary output\t", result);
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

    [Fact]
    public void FirstBatchExposesSixStandardGlobalRatesWithoutResolvingTheWholeCatalogue()
    {
        var results = new[] { "Synthetic model A", "Synthetic model A-mini", "Synthetic model B" }
            .Select(model => (Model: model, Result: RetailPricingTools.CompactBatchResult(ModelRatesPayload(model))))
            .ToArray();

        foreach (var (model, result) in results)
        {
            var resolution = ReadResolution(result);
            Assert.False(resolution.GetProperty("complete").GetBoolean());
            Assert.False(resolution.GetProperty("detailsComplete").GetBoolean());
            Assert.True(resolution.GetProperty("paginationComplete").GetBoolean());
            Assert.Equal(200, resolution.GetProperty("deliveredRows").GetInt32());
            Assert.Equal(3_012, resolution.GetProperty("fetchedRows").GetInt32());
            Assert.Equal(251, resolution.GetProperty("variants").GetInt32());

            var rows = ReadRows(result);
            Assert.Equal(200, rows.Length);
            Assert.True(result.Length < 60_000);
            foreach (var (direction, price) in new[] { ("Inp", "0.002"), ("Outp", "0.008") })
            {
                var row = Assert.Single(rows, row => row["skuName"] == $"{model} {direction} glbl");
                Assert.Equal(price, row["retailPrice"]);
                Assert.Equal("1K", row["unitOfMeasure"]);
                Assert.Equal("USD", row["currencyCode"]);
                Assert.Equal("1", row["observedVariantPrices"]);
                Assert.Equal("12", row["observedVariantRegions"]);
                Assert.Equal("true", row["variantSourceComplete"]);
            }

            Assert.Contains(rows, row => row["skuName"] == $"{model} Batch Inp glbl");
            Assert.Contains(rows, row => row["skuName"] == $"{model} Inp Data Zone");
            Assert.Contains(rows, row => row["skuName"] == $"{model} Inp regional");
            Assert.Contains(rows, row => row["skuName"] == $"{model} cached Inp glbl");
            Assert.Contains("do not refine merely because status is ambiguous or partial",
                resolution.GetProperty("instruction").GetString());
        }
    }

    [Fact]
    public void VariantCoverageNeverHidesRegionalPriceDifferencesOrUnfinishedPagination()
    {
        var json = JsonSerializer.Serialize(new
        {
            BillingCurrency = "USD",
            Items = Enumerable.Range(0, 220).Select(index => new
            {
                armRegionName = $"region{index}", armSkuName = "synthetic", skuName = "Synthetic Inp glbl",
                meterName = "Synthetic Inp glbl Tokens", productName = "Synthetic", unitOfMeasure = "1K",
                type = "Consumption", retailPrice = index == 219 ? 0.02 : 0.01
            })
        });

        var completeSource = RetailPricingTools.CompactBatchResult(json);
        var partialSource = RetailPricingTools.CompactBatchResult(json, paginationComplete: false);
        foreach (var (result, expectedComplete) in new[] { (completeSource, "true"), (partialSource, "false") })
        {
            var resolution = ReadResolution(result);
            Assert.False(resolution.GetProperty("complete").GetBoolean());
            Assert.False(resolution.GetProperty("detailsComplete").GetBoolean());
            Assert.Equal(200, resolution.GetProperty("deliveredRows").GetInt32());
            Assert.All(ReadRows(result), row =>
            {
                Assert.Equal("0.01", row["retailPrice"]);
                Assert.Equal("2", row["observedVariantPrices"]);
                Assert.Equal("220", row["observedVariantRegions"]);
                Assert.Equal(expectedComplete, row["variantSourceComplete"]);
            });
        }
        Assert.False(ReadResolution(partialSource).GetProperty("paginationComplete").GetBoolean());
    }

    [Fact]
    public void VariantCoverageSeparatesSkusProductsUnitsPurchaseTypesCurrenciesAndVolumeBands()
    {
        var json = JsonSerializer.Serialize(new
        {
            BillingCurrency = "USD",
            Items = Enumerable.Range(0, 8).Select(index => new
            {
                armRegionName = "synthetic", armSkuName = index == 7 ? "other-sku" : "synthetic", skuName = "Synthetic input",
                meterName = "Synthetic input", productName = index == 1 ? "Other product" : "Synthetic",
                unitOfMeasure = index == 2 ? "1M" : "1K",
                type = index == 3 ? "Reservation" : "Consumption",
                reservationTerm = index == 4 ? "1 Year" : "",
                currencyCode = index == 5 ? "CAD" : "USD",
                tierMinimumUnits = index == 6 ? 50_000 : 0,
                retailPrice = index + 1d
            })
        });

        var result = RetailPricingTools.CompactBatchResult(json);
        Assert.Equal(8, ReadResolution(result).GetProperty("variants").GetInt32());
        Assert.All(ReadRows(result), row =>
        {
            Assert.Equal("1", row["observedVariantPrices"]);
            Assert.Equal("1", row["observedVariantRegions"]);
            Assert.Equal("true", row["variantSourceComplete"]);
        });
    }

    [Fact]
    public void UniformObservedRatesPreserveMissingPageWideningAndRegionCaveats()
    {
        var result = RetailPricingTools.CompactBatchResult(ModelRatesPayload("Synthetic"), paginationComplete: false,
            widened: true, requestedRegions: ["missing"]);
        var resolution = ReadResolution(result);

        Assert.Equal("partial", resolution.GetProperty("status").GetString());
        Assert.False(resolution.GetProperty("complete").GetBoolean());
        Assert.False(resolution.GetProperty("paginationComplete").GetBoolean());
        Assert.True(resolution.GetProperty("vocabularyWidened").GetBoolean());
        Assert.Equal("missing", resolution.GetProperty("missingRequestedRegions")[0].GetString());
        Assert.All(ReadRows(result), row =>
        {
            Assert.Equal("1", row["observedVariantPrices"]);
            Assert.Equal("false", row["variantSourceComplete"]);
        });
    }

    [Fact]
    public void MissingPricePreventsCompleteVariantCoverage()
    {
        var json = JsonSerializer.Serialize(new
        {
            BillingCurrency = "USD",
            Items = new object[]
            {
                new { armRegionName = "first", skuName = "Synthetic", meterName = "Input", retailPrice = 0.01 },
                new { armRegionName = "second", skuName = "Synthetic", meterName = "Input" }
            }
        });

        Assert.All(ReadRows(RetailPricingTools.CompactBatchResult(json)), row =>
        {
            Assert.Equal("1", row["observedVariantPrices"]);
            Assert.Equal("2", row["observedVariantRegions"]);
            Assert.Equal("false", row["variantSourceComplete"]);
        });
    }

    [Fact]
    public void PricingToolMetadataRequiresReuseOfResolvedRequestedRates()
    {
        var tools = RetailPricingTools.Create().ToArray();
        Assert.All(tools, tool =>
        {
            Assert.Contains("RESOLUTION describes the whole filtered catalogue", tool.Description);
            Assert.Contains("do not refine merely because status is ambiguous or partial", tool.Description);
            Assert.Contains("Only a missing or unresolved requested rate permits one targeted refinement", tool.Description);
        });
        var batch = tools.Single(tool => tool.Name == "GetAzureRetailPricingBatch");
        Assert.Contains("identify both rates for each requested model in this first batch", batch.Description);
        Assert.Contains("do not issue another batch merely to isolate exact names already returned",
            batch.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("catalogue-level partial/ambiguous status alone does not require another batch",
            batch.JsonSchema.GetProperty("properties").GetProperty("queriesJson").GetProperty("description").GetString());
    }

    [Fact]
    public void PricingMetadataRequiresMaterialConfigurationEvenForCrossRegionRankings()
    {
        var tools = RetailPricingTools.Create().ToArray();
        Assert.All(tools, tool =>
        {
            Assert.Contains("including cross-region rankings, establish material product configuration from the user or prior context",
                tool.Description);
            Assert.Contains("VM OS/license or database service tier/hardware", tool.Description);
            Assert.Contains("A fixed-region quote also requires the region", tool.Description);
            Assert.Contains("ask a concise clarification before pricing, calculation or charting", tool.Description);
            Assert.Contains("unless the user explicitly authorized an assumed scenario", tool.Description);
            Assert.Contains("Example filters are not defaults", tool.Description);
            Assert.Contains("a vCore count alone does not establish a database tier", tool.Description);
            Assert.Contains("cross-region ranking or global rate-card comparison waives only selecting a single region, not other material product configuration",
                tool.Description);
            Assert.Contains("Standard on-demand is the purchase-type default, not an OS/license or service tier default",
                tool.Description);
            Assert.Contains("Comparisons of explicitly named variants already establish those variants; compare them instead of asking the user to choose one",
                tool.Description);
        });

        var single = tools.Single(tool => tool.Name == "GetAzureRetailPricing");
        Assert.Contains("use standard on-demand purchase pricing unless other purchase variants are explicitly requested",
            single.Description);
        Assert.Contains("clarify missing material product configuration even for cross-region rankings", single.Description);
        Assert.DoesNotContain("unless the user explicitly asked for Spot, Low Priority, Windows", single.Description);
        Assert.Contains("clarify a missing region rather than choosing an example region",
            single.JsonSchema.GetProperty("properties").GetProperty("armRegionName").GetProperty("description").GetString());
        var batch = tools.Single(tool => tool.Name == "GetAzureRetailPricingBatch");
        Assert.Contains("East US example, not default quote inputs", batch.Description);
        var batchParameter = batch.JsonSchema.GetProperty("properties").GetProperty("queriesJson").GetProperty("description").GetString();
        Assert.Contains("ask a concise clarification before pricing, calculation or charting",
            batchParameter);
        Assert.Contains("including cross-region rankings, establish material product configuration", batchParameter);
        Assert.Contains("waives only selecting a single region, not other material product configuration", batchParameter);
        Assert.Contains("compare them instead of asking the user to choose one", batchParameter);
    }

    [Fact]
    public void SharedMeterProductsKeepSeparateRankingsAndRequireVisibleVariantQualifiers()
    {
        var json = JsonSerializer.Serialize(new
        {
            BillingCurrency = "USD",
            Items = new[] { (Product: "Synthetic compute", Price: 0.2), (Product: "Synthetic compute Windows", Price: 0.4) }
                .SelectMany(product => Enumerable.Range(0, 3).Select(region => new
                {
                    armRegionName = $"region{region}", armSkuName = "synthetic", skuName = "synthetic",
                    meterName = "Ordinary", productName = product.Product, unitOfMeasure = "1 Hour",
                    type = "Consumption", retailPrice = product.Price + region / 100d
                }))
        });

        var result = RetailPricingTools.CompactBatchResult(json, topPerVariant: 1);
        var resolution = ReadResolution(result);
        Assert.True(resolution.GetProperty("complete").GetBoolean());
        Assert.True(resolution.GetProperty("rankingComplete").GetBoolean());
        Assert.True(resolution.GetProperty("paginationComplete").GetBoolean());
        Assert.False(resolution.GetProperty("detailsComplete").GetBoolean());
        Assert.Equal(2, resolution.GetProperty("variants").GetInt32());
        var rows = ReadRows(result);
        Assert.Equal(2, rows.Length);
        Assert.Equal("0.2", Assert.Single(rows, row => row["productName"] == "Synthetic compute")["retailPrice"]);
        Assert.Equal("0.4", Assert.Single(rows, row => row["productName"] == "Synthetic compute Windows")["retailPrice"]);
        Assert.All(rows, row =>
        {
            Assert.Equal("Ordinary", row["meterName"]);
            Assert.Equal("3", row["observedVariantPrices"]);
            Assert.Equal("true", row["variantSourceComplete"]);
        });

        var guidance = resolution.GetProperty("instruction").GetString();
        Assert.Contains("Returned rates do not establish missing user quote inputs", guidance);
        Assert.Contains("OS/license, tier and purchase type", guidance);
        Assert.Contains("answer headlines and chart labels", guidance);
        Assert.Contains("a shared meterName or ARM SKU alone does not identify the price variant", guidance);
        Assert.All(RetailPricingTools.Create(), tool =>
            Assert.Contains("answer headlines and chart labels", tool.Description));
    }

    private static string ModelRatesPayload(string model)
    {
        var variants = new[]
        {
            (Sku: $"{model} Inp glbl", Price: 0.002),
            (Sku: $"{model} Outp glbl", Price: 0.008),
            (Sku: $"{model} Batch Inp glbl", Price: 0.001),
            (Sku: $"{model} Batch Outp glbl", Price: 0.004),
            (Sku: $"{model} Inp Data Zone", Price: 0.003),
            (Sku: $"{model} Outp Data Zone", Price: 0.012),
            (Sku: $"{model} Inp regional", Price: 0.004),
            (Sku: $"{model} Outp regional", Price: 0.016),
            (Sku: $"{model} cached Inp glbl", Price: 0.0001),
            (Sku: $"{model} cached Inp Data Zone", Price: 0.0002)
        }.Concat(Enumerable.Range(0, 241).Select(index => (Sku: $"{model} alternate {index}", Price: (index + 1) / 20_000d)));

        return JsonSerializer.Serialize(new
        {
            BillingCurrency = "USD",
            Items = variants.SelectMany(variant => Enumerable.Range(0, 12).Select(region => new
            {
                armRegionName = $"region{region}", armSkuName = variant.Sku, skuName = variant.Sku,
                meterName = $"{variant.Sku} Tokens", productName = "Synthetic models", unitOfMeasure = "1K",
                type = "Consumption", tierMinimumUnits = 0, retailPrice = variant.Price
            }))
        });
    }

    private static Dictionary<string, string>[] ReadRows(string result)
    {
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var header = Array.FindIndex(lines, line => line.StartsWith("retailPrice\t", StringComparison.Ordinal));
        Assert.True(header >= 0);
        var columns = lines[header].TrimEnd('\r').Split('\t');
        return lines.Skip(header + 1).Select(line =>
        {
            var cells = line.TrimEnd('\r').Split('\t');
            Assert.Equal(columns.Length, cells.Length);
            return columns.Zip(cells).ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
        }).ToArray();
    }

    private static JsonElement ReadResolution(string result) => JsonSerializer.Deserialize<JsonElement>(
        result.Split('\n').Single(line => line.StartsWith("RESOLUTION "))["RESOLUTION ".Length..]);
}