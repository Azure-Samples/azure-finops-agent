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

DATA SCOPING: use the one billing scope whose contract rates are required, preferring the specific MCA billing profile when known. Reuse discovered scope IDs; do not export every account/profile or restart an existing download. This API generates the scope's pricesheet and has no SKU/region row filter. For a targeted lookup in an already uploaded pricesheet, use QueryUploadedFile filters and projection instead of generating a fresh full export.

SCOPE FORMATS (one):
- EA:  /providers/Microsoft.Billing/billingAccounts/{billingAccountId}
- MCA: /providers/Microsoft.Billing/billingAccounts/{id}/billingProfiles/{profileId}

If you don't know the IDs, list them via QueryAzure first:
- EA:  GET /providers/Microsoft.Billing/billingAccounts?api-version=2024-04-01
- MCA: GET /providers/Microsoft.Billing/billingAccounts/{id}/billingProfiles?api-version=2024-04-01

Returns JSON with operationStatusUrl — pass to GetPricesheetStatus to poll. Typically 1–15 min for large EA, seconds for small MCA.");

        yield return AIFunctionFactory.Create(GetPricesheetStatus, "GetPricesheetStatus",
            @"Poll only the existing pricesheet operationStatusUrl returned by StartPricesheetDownload. This is an exact-operation read, not a filtered rate lookup or a reason to download all contracts. Preserve the provider's status and body; a pending/ready operation is not proof that any requested rate was inspected. Respect retry deadlines and do not poll faster than every 10s. If the status URL is missing, report that blocker rather than constructing one.

Never pass a credential-bearing download/SAS URL to FetchPublicWebPage or other model-authored tool arguments. For rate analysis, request the downloaded pricesheet as an upload, then use QueryUploadedFile with the requested SKU/region filters and only needed output columns.");
    }

    private async Task<string> StartPricesheetDownload(
        [Description("Exact billing scope for the requested contract. EA: /providers/Microsoft.Billing/billingAccounts/{id}. MCA: /providers/Microsoft.Billing/billingAccounts/{id}/billingProfiles/{profileId}. Reuse known IDs; do not broaden a single-profile request to every billing account.")] string billingScope)
    {
        var token = _tokens.AzureToken;
        if (string.IsNullOrEmpty(token))
            return HttpHelper.TokenMissing("AzureToken", null, "pricesheet");

        if (string.IsNullOrWhiteSpace(billingScope) || !billingScope.StartsWith('/'))
            return "Error: billingScope must start with '/' and point to an EA billing account or MCA billing profile.";

        billingScope = billingScope.TrimEnd('/');

        // Pricesheet download API supports both EA and MCA scopes.
        // Reference: GET/POST {scope}/providers/Microsoft.CostManagement/pricesheets/default/download?api-version=2026-08-01
        var url = $"https://management.azure.com{billingScope}/providers/Microsoft.CostManagement/pricesheets/default/download?api-version=2026-08-01";

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
        [Description("Exact full HTTPS operation status URL returned by StartPricesheetDownload. Reuse it for this operation only; not a constructed URL or a credential-bearing download/SAS URL.")] string operationStatusUrl)
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
