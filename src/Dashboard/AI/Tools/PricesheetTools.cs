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
            @"Kicks off an async download of the user's NEGOTIATED Azure pricesheet (EA or MCA contract rates — NOT retail). Use this whenever right-sizing, RI/SP, or cross-region recommendations need real prices instead of public retail.

WHEN TO USE:
- Before recommending region migration (negotiated rates may invert the recommendation)
- Before recommending RI/SP purchases (real PAYG-vs-RI delta needs negotiated PAYG)
- When user mentions EA, MCA, enterprise agreement, billing account, billing profile

SCOPE FORMATS (provide ONE):
- EA billing account:    /providers/Microsoft.Billing/billingAccounts/{billingAccountId}
- MCA billing profile:   /providers/Microsoft.Billing/billingAccounts/{billingAccountId}/billingProfiles/{billingProfileId}

If you don't know the IDs, first call QueryAzure to list them:
- EA: GET /providers/Microsoft.Billing/billingAccounts?api-version=2024-04-01
- MCA: GET /providers/Microsoft.Billing/billingAccounts/{id}/billingProfiles?api-version=2024-04-01

Returns JSON with operationStatusUrl. Pass that URL to GetPricesheetStatus to poll. Typical wait 1–15 minutes for large EA accounts; a few seconds for small MCA profiles.");

        yield return AIFunctionFactory.Create(GetPricesheetStatus, "GetPricesheetStatus",
            @"Polls the pricesheet download started by StartPricesheetDownload. Pass the operationStatusUrl returned by that tool.

Returns one of:
- {""status"":""pending""}  — keep polling (back off ~10s between calls)
- {""status"":""ready"",""downloadUrl"":""<SAS>"",""validTill"":""...""} — SAS valid ~1h
- {""status"":""failed"",""error"":""...""}

When status=ready, the downloadUrl is a SAS URL to a CSV (typically multi-MB). Either:
1. Tell the user the link is ready and let them click to download, OR
2. If small enough, use the built-in fetch tool to grab the first chunk and parse the rates inline.

Do NOT poll faster than every 10 seconds — the operation is genuinely slow.");
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
