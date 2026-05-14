using System.ComponentModel;
using Microsoft.Extensions.AI;

using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Infrastructure;

namespace AzureFinOps.Dashboard.AI.Tools;

/// <summary>
/// Negotiated EA / MCA pricesheet download. Retail prices (prices.azure.com) are
/// often wildly wrong for enterprise customers — actual contract rates can be
/// 30–60% off retail. Without this, right-sizing and region-comparison advice
/// can invert (the "expensive" region may be cheaper at the negotiated rate).
///
/// Mirrors the proven start/poll pattern from the Azure Cost Management MCP
/// server. Two tools because the operation is async and can take 1–15 minutes.
/// </summary>
public class PricesheetTools
{
    private readonly UserTokens _tokens;

    public PricesheetTools(UserTokens tokens) => _tokens = tokens;

    public IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(StartPricesheetDownload, "StartPricesheetDownload",
            @"Async download of the user's NEGOTIATED pricesheet (EA/MCA contract rates, NOT retail). Real contract rates can be 30–60% off retail — retail prices can invert right-sizing/region recommendations.

USE BEFORE: region migration recs, RI/SP recommendations, anytime user mentions EA/MCA/billing account/profile.

SCOPE FORMATS (one):
- EA:  /providers/Microsoft.Billing/billingAccounts/{billingAccountId}
- MCA: /providers/Microsoft.Billing/billingAccounts/{id}/billingProfiles/{profileId}

If you don't know the IDs, list them via QueryAzure first:
- EA:  GET /providers/Microsoft.Billing/billingAccounts?api-version=2024-04-01
- MCA: GET /providers/Microsoft.Billing/billingAccounts/{id}/billingProfiles?api-version=2024-04-01

Returns JSON with operationStatusUrl — pass to GetPricesheetStatus to poll. Typically 1–15 min for large EA, seconds for small MCA.");

        yield return AIFunctionFactory.Create(GetPricesheetStatus, "GetPricesheetStatus",
            @"Polls the pricesheet download started by StartPricesheetDownload. Pass the operationStatusUrl returned by that tool.

Returns one of:
- {""status"":""pending""}                  — keep polling, back off ~10s between calls
- {""status"":""ready"",""downloadUrl"":""<SAS>"",""validTill"":""...""}  — SAS valid ~1h
- {""status"":""failed"",""error"":""...""}

When ready: tell user the link is ready, OR if small enough use FetchPublicWebPage to grab the first chunk and parse rates inline. Do NOT poll faster than every 10s.");
    }

    private async Task<string> StartPricesheetDownload(
        [Description("Billing scope. EA: '/providers/Microsoft.Billing/billingAccounts/{id}'. MCA: '/providers/Microsoft.Billing/billingAccounts/{id}/billingProfiles/{profileId}'. Must start with '/'.")] string billingScope)
    {
        var token = _tokens.AzureToken;
        if (string.IsNullOrEmpty(token))
            return HttpHelper.TokenMissing("AzureToken", null, "pricesheet");

        if (string.IsNullOrWhiteSpace(billingScope) || !billingScope.StartsWith('/'))
            return "Error: billingScope must start with '/' and point to an EA billing account or MCA billing profile.";

        billingScope = billingScope.TrimEnd('/');

        // Pricesheet download API supports both EA and MCA scopes.
        // Reference: GET/POST {scope}/providers/Microsoft.CostManagement/pricesheets/default/download?api-version=2023-11-01
        var url = $"https://management.azure.com{billingScope}/providers/Microsoft.CostManagement/pricesheets/default/download?api-version=2023-11-01";

        using var activity = HttpHelper.Telemetry.StartActivity("StartPricesheetDownload");
        activity?.SetTag("pricesheet.scope", billingScope);

        var resp = await HttpHelper.SendWithRetryAsync(
            url, token, activity, "pricesheet.start",
            method: HttpMethod.Post,
            jsonBody: "{}");

        // 202 Accepted → Location header has the operation status URL
        // 200 OK → already complete (small accounts), body has downloadUrl
        if (resp.StartsWith("HTTP 202") || resp.StartsWith("HTTP 200"))
        {
            // SendWithRetryAsync currently doesn't surface response headers, so the LLM
            // gets the body. The Azure ARM long-running-operation pattern places the
            // poll URL in the body too for newer api-versions, but for safety we tell
            // the LLM to extract the operationStatusUrl from EITHER the body OR to
            // construct it from the original request scope.
            return $"Pricesheet download started. The response below contains the operation status URL — extract it (look for 'Location' / 'operationStatusUrl' in headers or the JSON body) and pass it to GetPricesheetStatus to poll.\n\n{resp}";
        }

        return resp;
    }

    private async Task<string> GetPricesheetStatus(
        [Description("Operation status URL returned by StartPricesheetDownload. Full https URL.")] string operationStatusUrl)
    {
        var token = _tokens.AzureToken;
        if (string.IsNullOrEmpty(token))
            return HttpHelper.TokenMissing("AzureToken", null, "pricesheet");

        if (string.IsNullOrWhiteSpace(operationStatusUrl) || !operationStatusUrl.StartsWith("https://"))
            return "Error: operationStatusUrl must be a full https URL returned by StartPricesheetDownload.";

        using var activity = HttpHelper.Telemetry.StartActivity("GetPricesheetStatus");

        return await HttpHelper.SendWithRetryAsync(
            operationStatusUrl, token, activity, "pricesheet.poll",
            method: HttpMethod.Get);
    }
}
