using System.Text.Json.Nodes;
using AzureFinOps.Dashboard.AI.Tools;

namespace Dashboard.Tests;

/// <summary>
/// The Retail Prices API returns 1,000 items per page and writes its next-page link with an explicit default port.
/// A text comparison against the pinned origin stopped after the first page and still reported complete coverage,
/// so a Foundry Models lookup (about 1,800 rows) silently lost every meter on page two.
/// </summary>
public sealed class RetailPagingTests
{
    private const string ServiceLink =
        "https://prices.azure.com:443/api/retail/prices?api-version=2023-01-01-preview&currencyCode=USD&$filter=serviceName%20eq%20%27Foundry%20Models%27%20and%20armRegionName%20eq%20%27eastus2%27&$skip=1000";

    private static JsonNode Page(string? link) => new JsonObject { ["Items"] = new JsonArray(), ["NextPageLink"] = link };

    [Fact]
    public void FollowsTheServiceLinkWithItsExplicitDefaultPort()
    {
        var (next, unfollowed) = AzureQueryTools.RetailNextPage(Page(ServiceLink));
        Assert.False(unfollowed);
        Assert.Equal(
            "https://prices.azure.com/api/retail/prices?api-version=2023-01-01-preview&currencyCode=USD&$filter=serviceName%20eq%20%27Foundry%20Models%27%20and%20armRegionName%20eq%20%27eastus2%27&$skip=1000",
            next);
    }

    [Theory]
    [InlineData("https://prices.azure.com/api/retail/prices?$skip=1000")]
    [InlineData("https://PRICES.azure.com/api/retail/prices/?$skip=2000")]
    public void FollowsSameOriginLinks(string link)
    {
        var (next, unfollowed) = AzureQueryTools.RetailNextPage(Page(link));
        Assert.False(unfollowed);
        Assert.NotNull(next);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AMissingLinkMeansTheLastPage(string? link)
    {
        Assert.Equal((null, false), AzureQueryTools.RetailNextPage(Page(link)));
        Assert.Equal((null, false), AzureQueryTools.RetailNextPage(new JsonObject { ["Items"] = new JsonArray() }));
    }

    [Theory]
    [InlineData("http://prices.azure.com/api/retail/prices?$skip=1000")]
    [InlineData("https://prices.azure.com:8443/api/retail/prices?$skip=1000")]
    [InlineData("https://example.com/api/retail/prices?$skip=1000")]
    [InlineData("https://user@prices.azure.com/api/retail/prices?$skip=1000")]
    [InlineData("https://prices.azure.com/api/other?$skip=1000")]
    [InlineData("/api/retail/prices?$skip=1000")]
    public void AForeignLinkIsNotFollowedAndNeverReadsAsComplete(string link)
    {
        Assert.Equal((null, true), AzureQueryTools.RetailNextPage(Page(link)));
    }
}
