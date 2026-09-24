using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;

namespace Dashboard.Tests;

public sealed class CrossSubscriptionCostTests
{
    [Theory]
    [InlineData("subscriptions", false)]
    [InlineData("managementGroup", false)]
    [InlineData("managementGroup+subscriptions", true)]
    public void CostSummariesRetainTheirActualBasisDatesAndCoverage(string source, bool partial)
    {
        const string subscription = "11111111-1111-1111-1111-111111111111";
        const string missing = "22222222-2222-2222-2222-222222222222";
        var scopes = new List<(string Id, string Name)> { (subscription, "Synthetic subscription") };
        if (partial) scopes.Add((missing, "Unattempted subscription"));
        var results = new Dictionary<string, AzureQueryTools.CostScopeResult>
        {
            [subscription] = new(subscription, "Synthetic subscription", 200, 75.25, "USD", null)
        };
        var evidence = JsonSerializer.SerializeToElement(new
        {
            cacheStatus = "cached",
            retrievedAtUtc = "2026-08-24T10:00:00Z",
            dataAsOfUtc = (string?)null
        });
        using var document = JsonDocument.Parse(AzureQueryTools.BuildCostResponse(
            source, scopes, results, partial, [evidence], new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 24)));
        var body = document.RootElement;
        Assert.Equal("ActualCost", body.GetProperty("costType").GetString());
        Assert.Equal("Cost", body.GetProperty("aggregation").GetString());
        Assert.Equal("2026-08-01", body.GetProperty("timePeriod").GetProperty("from").GetString());
        Assert.Equal("2026-08-24", body.GetProperty("timePeriod").GetProperty("to").GetString());
        Assert.True(body.GetProperty("timePeriod").GetProperty("endExclusive").GetBoolean());
        Assert.Equal(!partial, body.GetProperty("complete").GetBoolean());
        Assert.Equal(partial, body.GetProperty("throttled").GetBoolean());
        Assert.Equal("cached", body.GetProperty("sourceEvidence")[0].GetProperty("cacheStatus").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("sourceEvidence")[0].GetProperty("dataAsOfUtc").ValueKind);
        Assert.Equal(75.25m, body.GetProperty(partial ? "partialCost" : "totalCost").GetDecimal());
        Assert.Equal(scopes.Count, body.GetProperty("results").GetArrayLength());
        if (partial) Assert.Equal(JsonValueKind.Null, body.GetProperty("totalCost").ValueKind);
    }

    [Fact]
    public void CostManagementRequestsSendTheLastIncludedDayForAnExclusiveEnd()
    {
        var period = JsonSerializer.SerializeToElement(
            AzureQueryTools.CostQueryTimePeriod(new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 1)));
        Assert.Equal("2026-08-01", period.GetProperty("from").GetString());
        Assert.Equal("2026-08-31", period.GetProperty("to").GetString());

        var singleDay = JsonSerializer.SerializeToElement(
            AzureQueryTools.CostQueryTimePeriod(new DateOnly(2026, 9, 24), new DateOnly(2026, 9, 25)));
        Assert.Equal("2026-09-24", singleDay.GetProperty("from").GetString());
        Assert.Equal("2026-09-24", singleDay.GetProperty("to").GetString());
    }
}
