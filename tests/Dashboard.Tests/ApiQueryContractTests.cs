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
}
