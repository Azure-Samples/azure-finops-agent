using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;
using Microsoft.Extensions.AI;

namespace Dashboard.Tests;

public sealed class RetailPricingTests
{
    private static AIFunction Tool => RetailPricingTools.Create().Single();

    [Fact]
    public void SourceUrlPreservesModelFilterWithoutUnsupportedTop()
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
    public void OnlyTheFilterIsRequired()
    {
        var required = Tool.JsonSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString());
        Assert.Equal(["filter"], required);
        Assert.Contains("OData", Tool.Description);
    }

    [Theory]
    [InlineData("", "USD", "filter is required")]
    [InlineData("serviceName eq 'Storage'", "US", "three-letter")]
    public async Task InvalidInputsFailBeforeAnyRequest(string filter, string currency, string error)
    {
        var result = await Tool.InvokeAsync(new AIFunctionArguments { ["filter"] = filter, ["currencyCode"] = currency });
        var text = result is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString() : result?.ToString();
        Assert.StartsWith("Error:", text);
        Assert.Contains(error, text);
    }
}