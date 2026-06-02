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

FOUNDRY MODELS (serviceName='Foundry Models') — productName is a FAMILY bucket, the specific model + I/O direction lives in skuName/meterName:
- productName values: 'Azure OpenAI GPT5', 'Azure OpenAI Reasoning', 'Azure OpenAI Embedding', 'Azure OpenAI Media', 'Azure OpenAI', 'Azure OpenAI PP FT GPT4s' (fine-tune), 'Azure OpenAI OSS Models', 'Azure Phi Models', 'Azure Llama Models', 'Azure Mistral Models', 'Azure Grok Models', 'Azure Deepseek Models', 'Azure Fireworks Models', 'Azure BFL Flux Models', 'Azure Kimi', 'Cohere Models', 'Qwen models', 'MAI Models', 'Azure AI Foundry Provisioned Throughput Reservation', 'Managed Compute'.
- skuName examples: 'gpt 4.1 Inp regnl', 'gpt 4.1 Outp glbl', 'o3 Inp regnl' — encodes model, Inp/Outp, regnl(regional)/glbl(global)/dtstr(data zone).
- Use productNameContains='GPT' / 'Llama' / 'Phi' / 'OpenAI' to scope. Do NOT pass model strings like 'gpt-4' to productNameContains — they won't match.

Common queries:
- Compare regions: serviceName='Virtual Machines' + armSkuName='Standard_D4s_v5' + priceType='Consumption'
- RI vs PAYG: serviceName='Virtual Machines' + armSkuName='Standard_D4s_v5' + armRegionName='eastus' (returns both)
- Storage tier costs: serviceName='Storage' + armRegionName='eastus' + meterName contains 'LRS'
- GPT-5 per-token: serviceName='Foundry Models' + armRegionName='eastus' + productNameContains='GPT' (then filter skuName client-side)
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
        if (body.Length > 200_000)
            return header + body[..200_000] + $"\n\n[TRUNCATED — {body.Length / 1024}KB total. Add more filters (armRegionName, armSkuName, priceType) to narrow results.]";
        return header + body;
    }

    // OData single-quote escape: ' → ''
    private static string Esc(string s) => s.Replace("'", "''");
}
