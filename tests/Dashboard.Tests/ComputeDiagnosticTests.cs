using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Auth;
using Microsoft.Extensions.AI;

namespace Dashboard.Tests;

public sealed class ComputeDiagnosticTests
{
    private static JsonElement Sku(object[] restrictions, string region = "testregion", string? lowPriorityCapable = null) => JsonSerializer.SerializeToElement(new
    {
        resourceType = "virtualMachines",
        name = "Standard_Test",
        family = "testFamily",
        locations = new[] { region },
        locationInfo = new[] { new { location = region, zones = new[] { "1", "2" } } },
        capabilities = new[] { new { name = "vCPUs", value = (string?)"8" }, new { name = "LowPriorityCapable", value = lowPriorityCapable } },
        restrictions
    });
    private static JsonElement Usage(string name, int limit, int current = 0) => JsonSerializer.SerializeToElement(new { name = new { value = name }, limit, currentValue = current });

    [Fact]
    public void RestrictionsOverrideSufficientQuota()
    {
        var result = ComputeDiagnosticTools.Evaluate(Sku([new { type = "Location", values = new[] { "testregion" } }]), "testregion", null,
            [Usage("cores", 100), Usage("testFamily", 100)], 1, "standard");
        Assert.Equal("blocked", result.SkuStatus);
        Assert.Equal("sufficient", result.QuotaStatus);
    }

    [Fact]
    public void SpotUsesSpotQuotaInsteadOfStandardFamilyQuota()
    {
        var usages = new[] { Usage("cores", 0), Usage("testFamily", 0), Usage("lowPriorityCores", 32) };
        Assert.Equal("sufficient", ComputeDiagnosticTools.Evaluate(Sku([]), "testregion", null, usages, 2, "spot").QuotaStatus);
        Assert.Equal("insufficient", ComputeDiagnosticTools.Evaluate(Sku([]), "testregion", null, usages, 2, "standard").QuotaStatus);
    }

    [Fact]
    public void QuotaBalanceIsReportedSeparatelyFromRequiredDemand()
    {
        var usages = new[] { Usage("cores", 100, 10), Usage("testFamily", 64, 24), Usage("lowPriorityCores", 500, 20) };
        var standard = ComputeDiagnosticTools.Evaluate(Sku([]), "testregion", null, usages, 1, "standard");
        Assert.Equal(40, standard.AvailableQuotaVcpus);
        Assert.NotEqual(standard.RequiredVcpus, standard.AvailableQuotaVcpus);
        Assert.Equal(480, ComputeDiagnosticTools.Evaluate(Sku([]), "testregion", null, usages, 1, "spot").AvailableQuotaVcpus);
        Assert.Null(ComputeDiagnosticTools.Evaluate(Sku([]), "testregion", null, [Usage("cores", 100)], 1, "standard").AvailableQuotaVcpus);
    }

    [Fact]
    public void ZoneAndMissingQuotaAreNotAssumedAvailable()
    {
        var result = ComputeDiagnosticTools.Evaluate(Sku([new { type = "Zone", restrictionInfo = new { locations = new[] { "testregion" }, zones = new[] { "2" } } }]), "testregion", "2", [], 1, "standard");
        Assert.Equal("blocked", result.SkuStatus);
        Assert.Equal("unknown", result.QuotaStatus);
        Assert.Equal(["1"], result.EligibleZones);
    }

    [Fact]
    public async Task RequestedSkuBeyondThirtyThousandUnrelatedRowsIsRetained()
    {
        var unrelated = JsonSerializer.SerializeToElement(new { name = "Standard_Unrelated" });
        var payload = JsonSerializer.Serialize(new { value = Enumerable.Repeat(unrelated, 30001).Append(Sku([])) });

        var result = await ComputeDiagnosticTools.ReadCollectionAsync("/synthetic/skus",
            (url, token) => Task.FromResult("HTTP 200 OK\n" + payload), default,
            item => item.GetProperty("name").GetString() == "Standard_Test");

    Assert.True(result.Complete);
        Assert.Equal(200, result.Status);
        Assert.Equal(30002, result.ItemsExamined);
        Assert.Equal(1, result.PagesRead);
        Assert.Null(result.IncompleteReason);
        Assert.Equal("Standard_Test", Assert.Single(result.Items).GetProperty("name").GetString());
    }

[Fact]
public async Task UnrelatedRowsDoNotPreventFollowingCataloguePages()
{
    var unrelated = JsonSerializer.SerializeToElement(new { name = "Standard_Unrelated" });
    var firstPage = JsonSerializer.Serialize(new
    {
        value = Enumerable.Repeat(unrelated, 30001),
        nextLink = "https://management.azure.com/synthetic/second"
    });
    var secondPage = JsonSerializer.Serialize(new { value = new[] { Sku([]) } });
var calls = 0;

var result = await ComputeDiagnosticTools.ReadCollectionAsync("/synthetic/skus",
    (url, token) =>
    {
        calls++;
        return Task.FromResult("HTTP 200 OK\n" + (calls == 1 ? firstPage : secondPage));
    }, default, item => item.GetProperty("name").GetString() == "Standard_Test");

Assert.True(result.Complete);
Assert.Single(result.Items);
Assert.Equal(2, calls);
    }

    [Fact]
public void RegionalSkuSelectionDoesNotReuseAnotherRegionsRestrictions()
{
    var first = Sku([new { type = "Location", values = new[] { "testregion" } }]);
var second = Sku([], "OtherRegion");

var selected = ComputeDiagnosticTools.FindRegionalSku([first, second], "standard_test", "otherregion");
var result = ComputeDiagnosticTools.Evaluate(selected, "otherregion", null,

    [Usage("lowPriorityCores", 100)], 1, "spot");

Assert.Equal("permitted", result.SkuStatus);
Assert.Equal("sufficient", result.QuotaStatus);
Assert.Equal(["1", "2"], result.EligibleZones);
Assert.Equal(JsonValueKind.Undefined, ComputeDiagnosticTools.FindRegionalSku([first], "Standard_Test", "otherregion").ValueKind);
    }

    [Theory]
[InlineData("False", false)]
[InlineData("True", true)]
[InlineData(null, null)]
public void AdvertisedSpotCapabilityIsSeparateFromCataloguePermission(string? capability, bool? expected)
{
    var result = ComputeDiagnosticTools.Evaluate(Sku([], lowPriorityCapable: capability), "testregion", null,
            [Usage("lowPriorityCores", 100)], 1, "spot");

Assert.Equal("permitted", result.SkuStatus);
Assert.Equal(expected, result.LowPriorityCapable);
    }

    [Theory]
[InlineData("HTTP 403 Forbidden\n{}", 403, "http_failure")]
[InlineData("HTTP 200 OK\n{}", 200, "invalid_collection_shape")]
public async Task FailedOrMalformedCatalogueIsExplicitlyIncomplete(string response, int status, string reason)
{
    var result = await ComputeDiagnosticTools.ReadCollectionAsync("/synthetic/skus",
        (url, token) => Task.FromResult(response), default);

    Assert.False(result.Complete);
    Assert.Equal(status, result.Status);
    Assert.Equal(reason, result.IncompleteReason);
    Assert.Empty(result.Items);
}

[Fact]
public async Task PageLimitIsNotReportedAsCompleteOrMissingSkus()
{
    var response = "HTTP 200 OK\n{\"value\":[],\"nextLink\":\"https://management.azure.com/synthetic/next\"}";
    var result = await ComputeDiagnosticTools.ReadCollectionAsync("/synthetic/skus",
        (url, token) => Task.FromResult(response), default);

    Assert.False(result.Complete);
    Assert.Equal(30, result.PagesRead);
    Assert.Equal("page_limit", result.IncompleteReason);
}

[Fact]
public async Task SpotHistoryUsesOneScopedReadAndPreservesRateBands()
{
    var calls = 0;
    var result = await ComputeDiagnosticTools.ReadSpotEvictionHistoryAsync(["00000000-0000-0000-0000-000000000001"], ["Standard_Test"], ["testregion"],
            (body, token) =>
            {
        calls++;
        using var request = JsonDocument.Parse(body);
        var query = request.RootElement.GetProperty("query").GetString()!;
        Assert.Contains("SpotResources", query);
        Assert.Contains("sku.name in~ ('Standard_Test')", query);
        Assert.Contains("location in~ ('testregion')", query);
        Assert.Single(request.RootElement.GetProperty("subscriptions").EnumerateArray());
        Assert.Equal("objectArray", request.RootElement.GetProperty("options").GetProperty("resultFormat").GetString());
        return Task.FromResult("HTTP 200 OK\n{\"data\":[{\"sku\":\"Standard_Test\",\"region\":\"testregion\",\"evictionRate\":\"5-10\"}],\"totalRecords\":1,\"resultTruncated\":\"false\"}");
    }, default);

Assert.Equal(1, calls);
Assert.True(result.Complete);
Assert.True(result.QueryComplete);
Assert.Equal("5-10", Assert.Single(result.Rows).GetProperty("evictionRate").GetString());
    }

    [Theory]
[InlineData("HTTP 403 Forbidden\n{}", false)]
[InlineData("HTTP 200 OK\n{\"data\":[],\"totalRecords\":0,\"resultTruncated\":\"false\"}", true)]
[InlineData("HTTP 200 OK\n{\"data\":[],\"totalRecords\":1,\"resultTruncated\":\"true\",\"$skipToken\":\"more\"}", false)]
public async Task MissingSpotHistoryNeverBecomesZeroEvictionRisk(string response, bool queryComplete)
{
    var result = await ComputeDiagnosticTools.ReadSpotEvictionHistoryAsync(["00000000-0000-0000-0000-000000000001"], ["Standard_Test"], [],
            (body, token) => Task.FromResult(response), default);

Assert.Equal("unknown", result.Status);
Assert.False(result.Complete);
Assert.Equal(queryComplete, result.QueryComplete);
Assert.Empty(result.Rows);
    }

    [Theory]
[InlineData("High", true)]
[InlineData("RestrictedSkuNotAvailable", true)]
[InlineData("DataNotFound", false)]
[InlineData("FutureUnknownStatus", false)]
public void HttpSuccessDoesNotProvePlacementEvidenceExists(string score, bool complete)
{
    var response = "HTTP 200 OK\n" + JsonSerializer.Serialize(new
    {
        placementScores = new[] { new { sku = "Standard_Test", region = "testregion", score } }
    });
    var coverage = ComputeDiagnosticTools.GetPlacementCoverage(response, ["testregion"], ["Standard_Test"], null);

    Assert.Equal(complete, coverage.Complete);
    Assert.Equal(complete ? "reported" : "unknown", coverage.Status);
}

[Fact]
public void MissingRegionOrRequestedZoneMakesPlacementPartial()
{
    const string response = "HTTP 200 OK\n{\"placementScores\":[{\"sku\":\"Standard_Test\",\"region\":\"testregion\",\"availabilityZone\":\"1\",\"score\":\"High\"}]}";
    var regions = ComputeDiagnosticTools.GetPlacementCoverage(response, ["testregion", "otherregion"], ["Standard_Test"], "1");
    var zone = ComputeDiagnosticTools.GetPlacementCoverage(response, ["testregion"], ["Standard_Test"], "2");

    Assert.False(regions.Complete);
    Assert.Equal("partial", regions.Status);
    Assert.Equal(2, regions.ExpectedScores);
    Assert.Equal(1, regions.ReportedScores);
    Assert.False(zone.Complete);
    Assert.Equal("unknown", zone.Status);
}

[Theory]
[InlineData(false, 0)]
[InlineData(true, 0)]
[InlineData(false, 1)]
[InlineData(true, 1)]
[InlineData(false, 2)]
public async Task RegisteredSpotToolRetainsLateCatalogueRowsAndQueriesEveryAdvertisedRegion(bool missingRegionalHistory, int scopeShape)
{
    var requests = new System.Collections.Concurrent.ConcurrentBag<(string Url, HttpMethod Method)>();
    var unrelated = JsonSerializer.SerializeToElement(new { resourceType = "disks", name = "Unrelated" });
    var catalogue = JsonSerializer.Serialize(new { value = Enumerable.Repeat(unrelated, 30001).Concat([Sku([]), Sku([], "otherregion")]) });
var tool = new ComputeDiagnosticTools(new UserTokens { AzureToken = "synthetic-" + Guid.NewGuid() },
    (url, method, body, token) =>
    {
    requests.Add((url, method));
    if (url.Contains("/Microsoft.ResourceGraph/resources"))
        return Task.FromResult(missingRegionalHistory
            ? "HTTP 200 OK\n{\"data\":[{\"sku\":\"Standard_Test\",\"region\":\"testregion\",\"evictionRate\":\"5-10\"}],\"totalRecords\":1,\"resultTruncated\":\"false\"}"
            : "HTTP 200 OK\n{\"data\":[{\"sku\":\"Standard_Test\",\"region\":\"testregion\",\"evictionRate\":\"5-10\"},{\"sku\":\"Standard_Test\",\"region\":\"otherregion\",\"evictionRate\":\"0-5\"}],\"totalRecords\":2,\"resultTruncated\":\"false\"}");
    if (url.Contains("/Microsoft.Compute/skus?")) return Task.FromResult("HTTP 200 OK\n" + catalogue);
    if (url.Contains("/usages?"))
        return Task.FromResult("HTTP 200 OK\n" + JsonSerializer.Serialize(new { value = new[] { Usage("cores", 0), Usage("testFamily", 0), Usage("lowPriorityCores", 32) } }));
    Assert.Contains("/placementScores/spot/generate?", url);
    Assert.Equal(HttpMethod.Post, method);
    using var request = JsonDocument.Parse(body!);
    Assert.Equal(2, request.RootElement.GetProperty("desiredCount").GetInt32());
    Assert.Equal(["otherregion", "testregion"], request.RootElement.GetProperty("desiredLocations").EnumerateArray().Select(region => region.GetString()!).Order().ToArray());
return Task.FromResult("HTTP 200 OK\n{\"placementScores\":[{\"sku\":\"Standard_Test\",\"region\":\"testregion\",\"score\":\"High\"},{\"sku\":\"Standard_Test\",\"region\":\"otherregion\",\"score\":\"Medium\"}]}");
            }).Create().Single(tool => tool.Name == "CheckComputeFeasibility");

var result = await tool.InvokeAsync(new AIFunctionArguments
{
    ["subscriptionsJson"] = scopeShape switch
    {
        1 => "[{\"id\":\"00000000-0000-0000-0000-000000000001\",\"name\":\"Synthetic subscription\"}]",
        2 => "[{\"id\":\"00000000-0000-0000-0000-000000000001\"}]",
        _ => "[\"00000000-0000-0000-0000-000000000001\"]"
    },
    ["skuNames"] = "Standard_Test",
    ["regions"] = "all",
    ["priority"] = "spot",
    ["count"] = "2"
});
using var document = JsonDocument.Parse(result!.ToString()!);
var root = document.RootElement;

Assert.Equal(!missingRegionalHistory, root.GetProperty("complete").GetBoolean());
Assert.Equal(30003, root.GetProperty("catalogueCoverage")[0].GetProperty("itemsExamined").GetInt32());
Assert.Equal(2, root.GetProperty("rows").GetArrayLength());
Assert.All(root.GetProperty("rows").EnumerateArray(), row =>
{
    Assert.Equal("permitted", row.GetProperty("result").GetProperty("SkuStatus").GetString());
    Assert.Equal("sufficient", row.GetProperty("result").GetProperty("QuotaStatus").GetString());
    Assert.Equal(16, row.GetProperty("result").GetProperty("RequiredVcpus").GetInt32());
});
Assert.Equal("not_attempted", root.GetProperty("allocation").GetString());
Assert.Equal("not_performed", root.GetProperty("policyValidation").GetString());
Assert.Equal(missingRegionalHistory ? 1 : 2, root.GetProperty("spotEvictionHistory").GetProperty("rows").GetArrayLength());
var otherRegion = root.GetProperty("rows").EnumerateArray().Single(row => row.GetProperty("region").GetString() == "otherregion");
Assert.Equal(missingRegionalHistory ? "unknown" : "reported", otherRegion.GetProperty("evictionHistoryStatus").GetString());
Assert.Equal(missingRegionalHistory ? JsonValueKind.Null : JsonValueKind.String, otherRegion.GetProperty("spotEvictionRate").ValueKind);
Assert.Equal(5, requests.Count);
Assert.Equal(2, requests.Count(request => request.Method == HttpMethod.Post));
    }

    [Fact]
public async Task LargeFeasibilityResultsKeepEveryRegionalRowWithoutGrouping()
{
    var regions = Enumerable.Range(0, 100).Select(index => "syntheticregion" + index).ToArray();
    var catalogue = JsonSerializer.Serialize(new { value = regions.Select(region => Sku([], region)) });
var tool = new ComputeDiagnosticTools(new UserTokens { AzureToken = "synthetic" }, (url, method, body, token) =>
{
    return Task.FromResult(url.Contains("/skus?") ? "HTTP 200 OK\n" + catalogue : "HTTP 200 OK\n{\"value\":[]}");
}).Create().Single(tool => tool.Name == "CheckComputeFeasibility");
var invocation = await tool.InvokeAsync(new AIFunctionArguments
{
    ["subscriptionsJson"] = "[{\"id\":\"00000000-0000-0000-0000-000000000001\"}]",
    ["skuNames"] = "Standard_Test",
    ["regions"] = "all"
});
var payload = invocation!.ToString()!;
Assert.True(System.Text.Encoding.UTF8.GetByteCount(payload) > 20000);
using var document = JsonDocument.Parse(payload);
var result = document.RootElement;
Assert.False(result.GetProperty("complete").GetBoolean());
Assert.Equal(100, result.GetProperty("rows").GetArrayLength());
Assert.Equal(regions, result.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("region").GetString()));
Assert.All(result.GetProperty("rows").EnumerateArray(), row => Assert.Equal("unknown", row.GetProperty("result").GetProperty("QuotaStatus").GetString()));
Assert.False(result.TryGetProperty("rowsFormat", out _));
Assert.Equal("not_performed", result.GetProperty("policyValidation").GetString());
    }

    [Theory]
[InlineData("[]")]
[InlineData("{}")]
[InlineData("[{\"name\":\"Missing id\"}]")]
[InlineData("[{\"id\":5}]")]
[InlineData("[{\"id\":\"not-a-guid\"}]")]
[InlineData("[\"00000000-0000-0000-0000-000000000001\",null]")]
public async Task InvalidSubscriptionObjectsFailBeforeAnyAzureCall(string subscriptions)
{
    var requests = 0;
    var tool = new ComputeDiagnosticTools(new UserTokens { AzureToken = "synthetic" }, (url, method, body, token) =>
    {
        requests++;
        throw new InvalidOperationException("No request should be dispatched.");
    }).Create().Single(tool => tool.Name == "CheckComputeFeasibility");
    var result = await tool.InvokeAsync(new AIFunctionArguments
    {
        ["subscriptionsJson"] = subscriptions,
        ["skuNames"] = "Standard_Test",
        ["priority"] = "spot"
    });
    Assert.StartsWith("Error:", result!.ToString());
    Assert.Equal(0, requests);
}
}