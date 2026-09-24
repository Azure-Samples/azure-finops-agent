using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Auth;
using Microsoft.Extensions.AI;

namespace Dashboard.Tests;

public sealed class TagCoverageTests
{
    private const string Sub = "00000000-0000-0000-0000-000000000001";

    [Fact]
    public void SchemaRequiresOnlyTheSubscriptionScope()
    {
        var tool = new TagCoverageTools(new UserTokens { UserId = 7 }).Create().Single();
        Assert.Equal("GetTagCoverage", tool.Name);
        var required = tool.JsonSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(["subscriptionsJson"], required);
        Assert.Contains("instead of QueryAzure", tool.Description);
    }

    [Fact]
    public async Task InvalidInputFailsBeforeAnyNetworkRequest()
    {
        var tool = new TagCoverageTools(new UserTokens { UserId = 7 }).Create().Single();
        var noScope = (await tool.InvokeAsync(new AIFunctionArguments { ["subscriptionsJson"] = "[]" }))!.ToString()!;
        Assert.StartsWith("HTTP 400", noScope);
        Assert.Contains("No request was sent", noScope);

        var badTop = (await tool.InvokeAsync(new AIFunctionArguments
        {
            ["subscriptionsJson"] = $"[\"{Sub}\"]",
            ["topResourceGroups"] = "500"
        }))!.ToString()!;
        Assert.StartsWith("HTTP 400", badTop);
    }

    [Fact]
    public async Task MissingArmTokenRequestsSignInWithDefaultTags()
    {
        var tool = new TagCoverageTools(new UserTokens { UserId = 7 }).Create().Single();
        var response = (await tool.InvokeAsync(new AIFunctionArguments { ["subscriptionsJson"] = $"[{{\"id\":\"{Sub}\",\"name\":\"Prod\"}}]" }))!.ToString()!;
        Assert.StartsWith("HTTP 401", response);
    }

    [Fact]
    public void TagNamesAreDeduplicatedBySeparatorInsensitiveForm()
    {
        var (tags, error) = TagCoverageTools.ParseTagKeys("cost-center, CostCenter, environment ,owner");
        Assert.Null(error);
        Assert.Equal(["cost-center", "environment", "owner"], tags);
        Assert.NotNull(TagCoverageTools.ParseTagKeys(" , ").Error);
        Assert.NotNull(TagCoverageTools.ParseTagKeys(string.Join(",", Enumerable.Range(0, 11).Select(i => $"t{i}"))).Error);
    }

    [Fact]
    public void MatchingFindsVariantsListsSimilarKeysAndRefusesUnsafeKeys()
    {
        var discovered = new List<(string, long)>
        {
            ("CostCenter", 4), ("cost_center", 2), ("Cost Center", 1), ("cost'center", 1),
            ("ownerEmail", 3), ("Environment", 5)
        };
        var matches = TagCoverageTools.MatchTagKeys(["cost-center", "owner", "environment"], discovered);

        Assert.Equal(["CostCenter", "cost_center", "Cost Center"], matches[0].Keys);
        Assert.Equal(["cost'center"], matches[0].UnqueryableKeys);
        Assert.Empty(matches[1].Keys);
        Assert.Equal(["ownerEmail"], matches[1].SimilarKeys);
        Assert.Equal(["Environment"], matches[2].Keys);

        var query = TagCoverageTools.BuildCoverageQuery(matches);
        Assert.Contains("tags['CostCenter']", query);
        Assert.Contains("tags['Cost Center']", query);
        Assert.DoesNotContain("cost'center", query);
        Assert.Contains("h1=0, p1=0", query);
        Assert.Contains("hAll=iff(h0==1 and h1==1 and h2==1,1,0)", query);
        Assert.Contains("by subscriptionId, resourceGroup=tolower(resourceGroup)", query);
    }

    [Fact]
    public void SummaryCoversEveryGroupAndRanksWorstFirst()
    {
        var matches = TagCoverageTools.MatchTagKeys(["CostCenter", "Owner"], [("CostCenter", 3), ("Owner", 2)]);
        var rows = JsonSerializer.Deserialize<List<JsonElement>>($$"""
            [
              {"subscriptionId":"{{Sub}}","resourceGroup":"rg-good","resources":4,"h0":4,"p0":0,"h1":2,"p1":1,"hAll":2},
              {"subscriptionId":"{{Sub}}","resourceGroup":"rg-small","resources":2,"h0":0,"p0":0,"h1":0,"p1":0,"hAll":0},
              {"subscriptionId":"{{Sub}}","resourceGroup":"rg-big","resources":6,"h0":0,"p0":0,"h1":0,"p1":0,"hAll":0}
            ]
            """)!;

        using var doc = JsonDocument.Parse(TagCoverageTools.Summarize(
            [(Sub, "Prod")], matches, rows, 2, truncated: false, retrievedAtUtc: "2026-09-22T00:00:00Z"));
        var body = doc.RootElement;

        Assert.True(body.GetProperty("complete").GetBoolean());
        Assert.False(body.TryGetProperty("error", out _));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("dataAsOfUtc").ValueKind);
        Assert.Equal(12, body.GetProperty("totals").GetProperty("resources").GetInt64());
        Assert.Equal(16.7, body.GetProperty("totals").GetProperty("allTagsPercent").GetDouble());
        Assert.Equal(2, body.GetProperty("totals").GetProperty("resourceGroupsWithZeroAllTagsCoverage").GetInt32());

        var costCenter = body.GetProperty("tags")[0];
        Assert.Equal(4, costCenter.GetProperty("taggedResources").GetInt64());
        Assert.Equal(33.3, costCenter.GetProperty("percent").GetDouble());
        Assert.Equal(8, costCenter.GetProperty("untaggedResources").GetInt64());
        Assert.Equal(1, body.GetProperty("tags")[1].GetProperty("placeholderResources").GetInt64());

        var ranked = body.GetProperty("resourceGroupsWorstFirst");
        Assert.Equal(3, ranked.GetProperty("total").GetInt32());
        Assert.Equal(2, ranked.GetProperty("returned").GetInt32());
        Assert.Equal(["rg-big", "rg-small"], ranked.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("resourceGroup").GetString()!).ToArray());
        Assert.Equal("Prod", ranked.GetProperty("items")[0].GetProperty("subscriptionName").GetString());

        var evidence = ProtectedTool.InspectEvidence(doc.RootElement.GetRawText());
        Assert.True(evidence.Success);
        Assert.False(evidence.Partial);
    }

    [Fact]
    public void TruncationOrUnqueryableKeysAreReportedAsPartial()
    {
        var matches = TagCoverageTools.MatchTagKeys(["CostCenter"], [("Cost\"Center", 1)]);
        var partial = TagCoverageTools.Summarize([(Sub, Sub)], matches, [], 10, truncated: false, retrievedAtUtc: "t");
        using var doc = JsonDocument.Parse(partial);
        Assert.False(doc.RootElement.GetProperty("complete").GetBoolean());
        Assert.True(ProtectedTool.InspectEvidence(partial).Partial);
    }
}
