using AzureFinOps.Dashboard.AI;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Auth;

namespace Dashboard.Tests;

public sealed class ApiQueryContractTests
{
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
        Assert.DoesNotContain("GET /providers/Microsoft.Consumption/reservationSummaries", CopilotSessionFactory.SystemPrompt);
        Assert.Contains("commitment impact as UNKNOWN", CopilotSessionFactory.SystemPrompt);
    }
}
