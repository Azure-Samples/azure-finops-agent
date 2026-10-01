using System.Text.Json;
using AzureFinOps.Dashboard.Auth;

namespace Dashboard.Tests;

public sealed class AzureSessionEndpointsTests
{
    // An observed Sponsorship subscription returned no Cost Management rows in every run of a daily digest; the offer
    // type the agent needs to recognize that is subscriptionPolicies.quotaId, so the cached scope context carries it.
    [Fact]
    public void SubscriptionScopeCarriesTheOfferQuotaId()
    {
        using var list = JsonDocument.Parse("""
            {"value":[
              {"subscriptionId":"00000000-0000-0000-0000-000000000001","displayName":"Sponsored","state":"Enabled","tenantId":"00000000-0000-0000-0000-0000000000aa",
               "subscriptionPolicies":{"locationPlacementId":"Public_2014-09-01","quotaId":"Sponsored_2016-01-01","spendingLimit":"Off"}},
              {"subscriptionId":"00000000-0000-0000-0000-000000000002","displayName":"No policies","state":"Enabled"}
            ]}
            """);

        var scopes = list.RootElement.GetProperty("value").EnumerateArray().Select(AzureSessionEndpoints.Subscription).ToList();

        Assert.Equal(
            """[{"id":"00000000-0000-0000-0000-000000000001","name":"Sponsored","state":"Enabled","tenantId":"00000000-0000-0000-0000-0000000000aa","quotaId":"Sponsored_2016-01-01"},{"id":"00000000-0000-0000-0000-000000000002","name":"No policies","state":"Enabled","tenantId":null,"quotaId":null}]""",
            JsonSerializer.Serialize(scopes));
    }
}
