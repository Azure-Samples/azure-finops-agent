using System.Text.Json;
using AzureFinOps.Dashboard.AI;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Auth;
using Microsoft.Extensions.AI;

namespace Dashboard.Tests;

public sealed class ApiQueryContractTests
{
    private const string ResourceGraphPath = "/providers/Microsoft.ResourceGraph/resources?api-version=2024-04-01";

    [Theory]
    [InlineData(null)]
    [InlineData("[]")]
    [InlineData("{\"query\":\"resources | summarize count()\"}")]
    [InlineData("{\"subscriptions\":[]}")]
    [InlineData("{\"subscriptions\":\"11111111-1111-1111-1111-111111111111\"}")]
    [InlineData("{\"subscriptions\":[\"not-a-subscription-id\"]}")]
    [InlineData("{\"subscriptions\":[123]}")]
    [InlineData("{\"managementGroups\":[\"\"]}")]
    [InlineData("{\"managementGroups\":null}")]
    [InlineData("{\"managementGroups\":[\"synthetic\"],\"subscriptions\":[]}")]
    [InlineData("{\"managementGroups\":[\"synthetic\"],\"managementGroups\":[\"another\"]}")]
    public void ResourceGraphRejectsImplicitOrAmbiguousScopes(string? body)
    {
        var error = AzureQueryTools.ValidateQueryBody(ResourceGraphPath, body);
        Assert.NotNull(error);
        Assert.Contains("Implicit tenant-wide scope is not supported", error);
        Assert.Contains("No request was sent", error);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("{\"subscriptions\":[\"11111111-1111-1111-1111-111111111111\"],\"query\":\"resources | take 200\"")]
    public void ResourceGraphReportsMalformedJsonAsMalformed(string body)
    {
        var error = AzureQueryTools.ValidateQueryBody(ResourceGraphPath, body);
        Assert.NotNull(error);
        Assert.Contains("must be one complete valid JSON object", error);
        Assert.Contains("No request was sent", error);
    }

    [Theory]
    [InlineData("{\"subscriptions\":[\"11111111-1111-1111-1111-111111111111\"],\"query\":\"resources | summarize count()\"}")]
    [InlineData("{\"managementGroups\":[\"synthetic-group\"],\"query\":\"resources | summarize count()\"}")]
    public void ResourceGraphPreservesExplicitScopes(string body) =>
        Assert.Null(AzureQueryTools.ValidateQueryBody(ResourceGraphPath, body));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SingleAndBulkResourceGraphReadsRejectMissingScopeBeforeDispatch(bool bulk)
    {
        var tool = new AzureQueryTools(new UserTokens { UserId = 101, AzureToken = "synthetic-test-only" }).Create()
            .Single(candidate => candidate.Name == (bulk ? "BulkAzureRequest" : "QueryAzure"));
        const string body = "{\"query\":\"resources | summarize count()\"}";
        var arguments = bulk
            ? new AIFunctionArguments { ["requestsJson"] = JsonSerializer.Serialize(new[] { new { method = "POST", path = ResourceGraphPath, body } }) }
            : new AIFunctionArguments { ["method"] = "POST", ["path"] = ResourceGraphPath, ["body"] = body };
        var result = (await tool.InvokeAsync(arguments))!.ToString()!;
        Assert.Contains("Implicit tenant-wide scope is not supported", result);
        Assert.Contains("No request was sent", result);
    }

    [Fact]
    public void ResourceGraphGuidanceUsesValidKqlLimitsAndDeclaredScope()
    {
        var tool = new AzureQueryTools(new UserTokens { UserId = 101 }).Create().Single(candidate => candidate.Name == "QueryAzure");
        Assert.Contains("top N by count_ desc", tool.Description);
        Assert.Contains("use `take N`, NOT bare `top N`", tool.Description);
        Assert.Contains("full requested connection-context scope", tool.Description);
        Assert.Contains("not a leading union of parenthesized subqueries", tool.Description);
        Assert.Contains("separate simple aggregate queries", tool.Description);
    }

    [Theory]
    [InlineData("/v1.0/subscribedSkus?$select=skuId,consumedUnits&$top=100")]
    [InlineData("/beta/subscribedSkus?%24filter=consumedUnits%20gt%200")]
    [InlineData("/v1.0/subscribedSkus/?$search=Copilot")]
    [InlineData("/beta/reports/getMicrosoft365CopilotUsageUserDetail(period='D30')?$filter=lastActivityDate%20eq%20null")]
    [InlineData("/beta/reports/getMicrosoft365CopilotUserCountSummary(period='D30')?$top=50")]
    [InlineData("/v1.0/copilot/reports/getMicrosoft365CopilotUsageUserDetail(period='D30')?$select=lastActivityDate")]
    [InlineData("/v1.0/copilot/reports/getMicrosoft365CopilotUserCountSummary(period='D30')?$top=50")]
    [InlineData("/beta/copilot/reports/getMicrosoft365CopilotUsageUserDetail(period='D28',version='v2')?$filter=lastActivityDate%20eq%20null")]
    public void UnsupportedGraphQueryOptionsFailWithoutDispatch(string path)
    {
        var error = GraphQueryTools.ValidateQueryParameters(path);
        Assert.NotNull(error);
        Assert.StartsWith("HTTP 400", error);
        Assert.Contains("No request was sent", error);
    }

    [Theory]
    [InlineData("/v1.0/subscribedSkus")]
    [InlineData("/v1.0/subscribedSkus?%24select=skuId,prepaidUnits,consumedUnits")]
    [InlineData("/beta/reports/getMicrosoft365CopilotUsageUserDetail(period='D30')?$format=application/json")]
    [InlineData("/v1.0/copilot/reports/getMicrosoft365CopilotUsageUserDetail(period='D30')")]
    [InlineData("/beta/copilot/reports/getMicrosoft365CopilotUsageUserDetail(period='D28',version='v2')")]
    [InlineData("/v1.0/copilot/reports/getMicrosoft365CopilotUserCountSummary(period='D7')?$format=application/json")]
    [InlineData("/v1.0/users?$filter=accountEnabled%20eq%20true&$select=id&$top=50")]
    public void SupportedGraphOptionsAndUnrelatedEndpointsArePreserved(string path) =>
        Assert.Null(GraphQueryTools.ValidateQueryParameters(path));

    [Theory]
    [InlineData("/providers/Microsoft.Consumption/reservationSummaries?api-version=2024-08-01&grain=monthly")]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/providers/Microsoft.Consumption/reservationSummaries?grain=monthly")]
    public void ReservationUtilizationRequiresItsActualSupportedScope(string path)
    {
        var error = AzureQueryTools.ValidateScopePrefix(path);
        Assert.NotNull(error);
        Assert.Contains("reservation-order", error);
        Assert.Contains("No request was sent", error);
    }

    [Theory]
    [InlineData("/providers/Microsoft.Capacity/reservationOrders/synthetic-order/providers/Microsoft.Consumption/reservationSummaries?api-version=2024-08-01&grain=monthly")]
    [InlineData("/providers/Microsoft.Capacity/reservationOrders/synthetic-order/reservations/synthetic-reservation/providers/Microsoft.Consumption/reservationSummaries?grain=monthly")]
    [InlineData("/providers/Microsoft.Billing/billingAccounts/synthetic-account/providers/Microsoft.Consumption/reservationSummaries?grain=monthly")]
    [InlineData("/providers/Microsoft.Billing/billingAccounts/synthetic-account/billingProfiles/synthetic-profile/providers/Microsoft.Consumption/reservationSummaries?grain=monthly")]
    [InlineData("/providers/Microsoft.Capacity/reservationOrders?api-version=2022-11-01")]
    public void DiscoveredReservationAndBillingScopesRemainSupported(string path) =>
        Assert.Null(AzureQueryTools.ValidateScopePrefix(path));

    [Fact]
    public void PromptAndGraphSchemaUseCurrentEndpointContracts()
    {
        var graph = new GraphQueryTools(new UserTokens { UserId = 101 }).Create().Single();
        Assert.Contains("/v1.0/copilot/reports/", graph.Description);
        Assert.Contains("CSV", graph.Description);
        Assert.DoesNotContain("BETA-ONLY", graph.Description);
        Assert.Contains("supports ONLY $select", graph.Description);
        Assert.Contains("retrieval timestamp, not the report refresh date", graph.Description);
        Assert.DoesNotContain("GET /providers/Microsoft.Consumption/reservationSummaries", CopilotSessionFactory.SystemPrompt);
        Assert.Contains("commitment impact as UNKNOWN", CopilotSessionFactory.SystemPrompt);
    }

    [Fact]
    public void SubscribedSkusResponseCarriesHostComputedSeatTotals()
    {
        Assert.True(GraphQueryTools.IsSubscribedSkusPath("/v1.0/subscribedSkus?$select=skuPartNumber"));
        Assert.False(GraphQueryTools.IsSubscribedSkusPath("/v1.0/users"));
        const string response = "HTTP 200 OK\nCurrent UTC time: 2026-09-25 15:56:00\n" +
            "{\"value\":[{\"skuPartNumber\":\"SKU_A\",\"capabilityStatus\":\"Enabled\",\"consumedUnits\":2,\"prepaidUnits\":{\"enabled\":50}}," +
            "{\"skuPartNumber\":\"SKU_B\",\"capabilityStatus\":\"Enabled\",\"consumedUnits\":1,\"prepaidUnits\":{\"enabled\":50}}]}";
        var result = GraphQueryTools.AppendLicenseSummary(response, new DateTimeOffset(2026, 9, 25, 15, 56, 0, TimeSpan.Zero));
        Assert.StartsWith("HTTP 200 OK\nCurrent UTC time: 2026-09-25 15:56:00\n", result);
        using var doc = JsonDocument.Parse(result[result.IndexOf('{')..]);
        Assert.Equal(2, doc.RootElement.GetProperty("value").GetArrayLength());
        var summary = doc.RootElement.GetProperty("_licenseSummary");
        Assert.Equal(97, summary.GetProperty("totals").GetProperty("unassignedEnabled").GetInt64());
        Assert.Equal(100, summary.GetProperty("totals").GetProperty("enabled").GetInt64());
        Assert.StartsWith("97 enabled Microsoft 365 license seats are unassigned across 2 SKUs (100 enabled, 3 assigned)", summary.GetProperty("headline").GetString());
        var table = summary.GetProperty("answerTable").GetString()!;
        Assert.Contains("| SKU_B | Enabled | Not returned by Graph | 50 | 1 | 49 |", table);
        Assert.Contains("| **Total** | | **Not returned by Graph** | **100** | **3** | **97** | **Unknown** |", table);
        Assert.StartsWith("2026-09-25T15:56:00", summary.GetProperty("retrievedAtUtc").GetString());
        Assert.Contains("retrieved from Microsoft Graph subscribedSkus at 2026-09-25 15:56 UTC", summary.GetProperty("freshness").GetString());
        Assert.Equal("HTTP 403 Forbidden\n{}", GraphQueryTools.AppendLicenseSummary("HTTP 403 Forbidden\n{}"));
    }
}
