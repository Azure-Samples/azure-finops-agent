using System.ComponentModel;
using Microsoft.Extensions.AI;

using AzureFinOps.Dashboard.Infrastructure;

namespace AzureFinOps.Dashboard.AI.Tools;

/// <summary>
/// Public Azure Retail Prices API wrapper (https://prices.azure.com — no auth required).
/// Encodes correct OData $filter syntax and enforces $top to keep responses bounded.
/// </summary>
public static class RetailPricingTools
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(GetAzureRetailPricing, "GetAzureRetailPricing",
            @"PUBLIC (no auth): Queries the Azure Retail Prices API for current pay-as-you-go, reservation, and savings plan pricing. Use this BEFORE QueryAzure when comparing SKUs, regions, or estimating cost for a workload that hasn't been deployed yet.

CRITICAL FILTERING (always provide as much as possible to keep results small):
- serviceName: e.g. 'Virtual Machines', 'Storage', 'SQL Database', 'Azure App Service', 'Foundry Models' (covers ALL AOAI + open-model inference — the legacy 'Azure OpenAI' serviceName returns 0 rows)
- armRegionName: e.g. 'eastus', 'westeurope', 'northeurope' (lowercase, no spaces)
- armSkuName: e.g. 'Standard_D4s_v5', 'Standard_E16ads_v5'
- priceType: 'Consumption' (PAYG), 'Reservation' (1y/3y RI), 'DevTestConsumption'
- meterName: e.g. 'D4s v5' for VMs, or 'Hot LRS Data Stored' for storage

Returns up to $top items (default 50, max 100). Note: the API sometimes ignores $top and returns a full page (~1000 rows) — always filter aggressively. For broad surveys, aggregate client-side; never call without filters.

FOUNDRY MODELS (serviceName='Foundry Models') — productName is a FAMILY bucket; the specific model, I/O direction, residency zone, and deployment type all live INSIDE skuName/meterName:
- productName values: 'Azure OpenAI GPT5', 'Azure OpenAI Reasoning', 'Azure OpenAI Embedding', 'Azure OpenAI Media', 'Azure OpenAI', 'Azure OpenAI PP FT GPT4s' (fine-tune), 'Azure OpenAI OSS Models', 'Azure Phi Models', 'Azure Llama Models', 'Azure Mistral Models', 'Azure Grok Models', 'Azure Deepseek Models', 'Azure Fireworks Models', 'Azure BFL Flux Models', 'Azure Kimi', 'Cohere Models', 'Qwen models', 'MAI Models', 'Azure AI Foundry Provisioned Throughput Reservation', 'Managed Compute'.
- Use productNameContains='GPT' / 'Llama' / 'Phi' / 'OpenAI' to scope. Do NOT pass model strings like 'gpt-4' to productNameContains — they won't match.

DECODE skuName TOKEN-BY-TOKEN BEFORE QUOTING ANY PRICE (this is where wrong prices come from — every token changes the price):
- Direction: 'Inp'/'Input' = input · 'Outp'/'Opt'/'Output' = output · 'cd Inp' = CACHED input (only applies to cache hits, far cheaper than normal input).
- Zone: ends in 'Gl' (or 'glbl') = Global · 'Dz' (or 'dtstr') = Data Zone · 'regnl' = Regional. Different zones are different products at different prices.
- Deployment: contains 'Batch' = Batch API (async, ~50% off, NOT real-time). NO 'Batch' token = Standard (real-time) — this is the default/headline.
- Context tier (gpt-5.5 family): 'ShortCo' = short-context · 'LongCo' = long-context · 'PP' = priority processing. Each is a separate price.

HOW TO PICK THE RIGHT ROW (the #1 mistake is reporting a cheaper variant as the headline price):
- HEADLINE pay-as-you-go = Standard + Global: skuName has NO 'Batch', ends in 'Gl', and for input uses 'Inp' (NOT 'cd Inp'). Example gpt-5.4-nano: 'X nano Inp Gl' = input, 'X nano cd Inp Gl' = cached input, 'X nano Opt Gl' = output.
- prices.azure.com repeats the SAME price across every armRegionName (the value is flat per zone) AND returns every Batch / Data Zone / Regional / cached variant in the same response. So the result set legitimately contains many rows at DIFFERENT prices for one model.
- NEVER take the minimum retailPrice across rows. The lowest row is almost always Batch + cached — not the real price. Instead, match the EXACT skuName for the variant asked about (default Standard + Global) and report THAT row's retailPrice.

MANDATORY — ALWAYS STATE THE BASIS OF EVERY PRICE YOU QUOTE. Never give a bare number. Every price MUST be labelled with the full context it is based on:
  • Deployment type — Standard (real-time) or Batch (async, ~50% off)
  • Zone — Global / Data Zone / Regional
  • Direction — input / cached input / output
  • Context tier (gpt-5.5 family) — ShortContext / LongContext / Priority Processing, when present
  • Region — the armRegionName (or 'flat across all regions' if it does not vary)
  • Currency + unit — e.g. USD per 1M tokens
  Template: '<model>, <Deployment> <Zone>[ , <tier>], <region> — input $X, cached input $Y, output $Z per 1M tokens (<currency>)'.
  Example: 'gpt-5.4-nano, Global Standard, flat across all regions — input $0.20, cached input $0.02, output $1.25 per 1M tokens (USD)'.
  In a comparison table, add explicit columns/labels for Deployment, Zone, and Region so the basis is visible per row. Mention Batch / Data Zone / Regional / cached only as clearly-labelled separate options, NEVER as the headline. If you cannot determine the deployment/zone/region for a row, say so rather than guessing.

MONTHLY / VOLUME COST ESTIMATES — DO NOT DO THE MATH YOURSELF: after you have the per-1M rates, for ANY 'monthly cost', 'cost for N conversations/requests', or model-vs-model total comparison you MUST call EstimateTokenCost with those rates and one shared set of token assumptions, then report ITS numbers verbatim. Hand-computing token costs in prose produces summary tables that disagree with the step-by-step — always delegate the arithmetic to EstimateTokenCost.

Common queries:
- Compare regions: serviceName='Virtual Machines' + armSkuName='Standard_D4s_v5' + priceType='Consumption'
- RI vs PAYG: serviceName='Virtual Machines' + armSkuName='Standard_D4s_v5' + armRegionName='eastus' (returns both)
- Storage tier costs: serviceName='Storage' + armRegionName='eastus' + meterName contains 'LRS'
- GPT model per-token: serviceName='Foundry Models' + productNameContains='GPT' (returns ALL variants — then pick the exact skuName: no 'Batch', ends 'Gl', 'Inp'/'Opt'/'cd Inp' — for Global Standard; do NOT min across rows)
- Llama / Phi / Mistral: serviceName='Foundry Models' + productNameContains='Llama' (or 'Phi', 'Mistral')
- Spot vs on-demand: serviceName='Virtual Machines' + armSkuName='Standard_D4s_v5' + meterName contains 'Spot'");
    }

    private static async Task<string> GetAzureRetailPricing(
        [Description("Service name, e.g. 'Virtual Machines', 'Storage', 'SQL Database', 'Foundry Models'. REQUIRED.")] string serviceName,
        [Description("ARM region (lowercase, no spaces), e.g. 'eastus', 'westeurope'. Empty = all regions.")] string? armRegionName = null,
        [Description("ARM SKU name, e.g. 'Standard_D4s_v5'. Empty = all SKUs.")] string? armSkuName = null,
        [Description("Price type: 'Consumption' (PAYG), 'Reservation' (1y/3y RI), 'DevTestConsumption'. Empty = all.")] string? priceType = null,
        [Description("Substring match on meterName, e.g. 'Spot' or 'LRS'. Empty = no meter filter.")] string? meterNameContains = null,
        [Description("Substring match on productName, e.g. 'GPT' / 'Llama' / 'Phi' for Foundry Models, or 'Premium SSD' for storage. Foundry productName is a family bucket — use 'GPT' not 'gpt-4'. Empty = no product filter.")] string? productNameContains = null,
        [Description("Currency code (default 'USD'). Supported: USD, EUR, GBP, JPY, NOK, etc.")] string? currencyCode = null,
        [Description("Max results (default 50, max 100). Lower = faster.")] int top = 50)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
            return "Error: serviceName is required (e.g. 'Virtual Machines'). Querying without a service filter would return millions of rows.";

        top = Math.Clamp(top, 1, 100);
        currencyCode = string.IsNullOrWhiteSpace(currencyCode) ? "USD" : currencyCode.Trim().ToUpperInvariant();
        var isFoundry = serviceName.Trim().Equals("Foundry Models", StringComparison.OrdinalIgnoreCase);

        var filters = new List<string> { $"serviceName eq '{Esc(serviceName)}'" };
        if (!string.IsNullOrWhiteSpace(armRegionName)) filters.Add($"armRegionName eq '{Esc(armRegionName.Trim().ToLowerInvariant())}'");
        if (!string.IsNullOrWhiteSpace(armSkuName)) filters.Add($"armSkuName eq '{Esc(armSkuName.Trim())}'");
        if (!string.IsNullOrWhiteSpace(priceType)) filters.Add($"priceType eq '{Esc(priceType.Trim())}'");
        if (!string.IsNullOrWhiteSpace(meterNameContains)) filters.Add($"contains(meterName, '{Esc(meterNameContains.Trim())}')");
        if (!string.IsNullOrWhiteSpace(productNameContains)) filters.Add($"contains(productName, '{Esc(productNameContains.Trim())}')");

        var filter = string.Join(" and ", filters);
        var url = $"https://prices.azure.com/api/retail/prices?api-version=2023-01-01-preview" +
                  $"&currencyCode={Uri.EscapeDataString(currencyCode)}" +
                  $"&$filter={Uri.EscapeDataString(filter)}" +
                  $"&$top={top}";

        using var activity = HttpHelper.Telemetry.StartActivity("GetAzureRetailPricing");
        activity?.SetTag("pricing.service", serviceName);
        activity?.SetTag("pricing.region", armRegionName ?? "any");
        activity?.SetTag("pricing.sku", armSkuName ?? "any");
        activity?.SetTag("pricing.top", top);

        // Lightweight retry on 429 / transient — prices.azure.com is public but rate-limits
        // when an agent fans out 5+ pricing lookups in one turn (Persistence ladder pattern).
        HttpResponseMessage res = null!;
        string body = "";
        const int MaxAttempts = 4;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("User-Agent", "FinOps-Dashboard/1.0");
            res = await Http.SendAsync(req);
            body = await res.Content.ReadAsStringAsync();

            if ((int)res.StatusCode != 429 && (int)res.StatusCode < 500) break;
            if (attempt == MaxAttempts - 1) break;

            var retryAfter = res.Headers.RetryAfter?.Delta?.TotalSeconds
                          ?? Math.Min(Math.Pow(2, attempt + 1) + Random.Shared.NextDouble(), 30);
            var waitSeconds = Math.Max(1, retryAfter);
            activity?.SetTag($"pricing.retry_{attempt}", $"{(int)res.StatusCode}, waiting {waitSeconds:F0}s");
            // Surface the cool-down to the chat UI via the same baggage-keyed SSE channel HttpHelper uses.
            var turnKey = System.Diagnostics.Activity.Current?.GetBaggageItem("finops.turn.id");
            if (turnKey is not null && HttpHelper.RetryReporters.TryGetValue(turnKey, out var report))
            {
                try { await report(attempt + 1, waitSeconds, url, "pricing", (int)res.StatusCode); }
                catch (Exception emitEx)
                {
                    HttpHelper.Logger?.LogWarning(emitEx,
                        "SSE cooling_down emit failed for pricing attempt={Attempt}", attempt + 1);
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(waitSeconds));
        }

        activity?.SetTag("pricing.status_code", (int)res.StatusCode);
        activity?.SetTag("pricing.response_length", body.Length);

        var header = $"HTTP {(int)res.StatusCode} {res.StatusCode}\nQuery: {filter} (top={top}, currency={currencyCode})\nUTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}\n";

        // Foundry pricing is the one place an agent reliably mis-reads the raw rows: the response
        // mixes Standard/Batch, Global/DataZone/Regional, and cached/non-cached SKUs (all at
        // different prices, each repeated per region). The full JSON is still returned below — we
        // only PREPEND a reminder so the model picks the right skuName instead of the cheapest row.
        // This is guidance text, not response parsing (raw-JSON contract preserved).
        if (isFoundry)
            header +=
                "FOUNDRY PRICING — READ skuName BEFORE QUOTING: headline PAYG = Standard + Global "
                + "(skuName has NO 'Batch', ends in 'Gl', input is 'Inp' not 'cd Inp'). The same price "
                + "repeats across every region, and Batch/DataZone/Regional/cached variants are mixed in "
                + "at different prices. Match the EXACT variant asked for (default Standard+Global) and "
                + "state which one you quote. NEVER report the minimum retailPrice across rows.\n";

        if (body.Length > 200_000)
            return header + body[..200_000] + $"\n\n[TRUNCATED — {body.Length / 1024}KB total. Add more filters (armRegionName, armSkuName, priceType) to narrow results.]";
        return header + body;
    }

    // OData single-quote escape: ' → ''
    private static string Esc(string s) => s.Replace("'", "''");
}
