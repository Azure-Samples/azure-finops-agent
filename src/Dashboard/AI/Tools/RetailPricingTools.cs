using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
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
    private sealed record BatchRow(int Score, string MeterKey, string Key, string Line);
    private sealed record RetailPage(int Status, string StatusText, string Body);

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
- Context tier (some reasoning families): 'ShortCo' = short-context · 'LongCo' = long-context · 'PP' = priority processing. Each is a separate price.

HOW TO PICK THE RIGHT ROW (the #1 mistake is reporting a cheaper variant as the headline price):
- HEADLINE pay-as-you-go = Standard + Global: skuName has NO 'Batch', ends in 'Gl', and for input uses 'Inp' (NOT 'cd Inp'). Example (a nano SKU): 'X nano Inp Gl' = input, 'X nano cd Inp Gl' = cached input, 'X nano Opt Gl' = output.
- prices.azure.com repeats the SAME price across every armRegionName (the value is flat per zone) AND returns every Batch / Data Zone / Regional / cached variant in the same response. So the result set legitimately contains many rows at DIFFERENT prices for one model.
- NEVER take the minimum retailPrice across rows. The lowest row is almost always Batch + cached — not the real price. Instead, match the EXACT skuName for the variant asked about (default Standard + Global) and report THAT row's retailPrice.

MANDATORY — ALWAYS STATE THE BASIS OF EVERY PRICE YOU QUOTE. Never give a bare number. Every price MUST be labelled with the full context it is based on:
  • Deployment type — Standard (real-time) or Batch (async, ~50% off)
  • Zone — Global / Data Zone / Regional
  • Direction — input / cached input / output
  • Context tier (some reasoning families) — ShortContext / LongContext / Priority Processing, when present
  • Region — the armRegionName (or 'flat across all regions' if it does not vary)
  • Currency + unit — e.g. USD per 1M tokens
  Template: '<model>, <Deployment> <Zone>[ , <tier>], <region> — input $X, cached input $Y, output $Z per 1M tokens (<currency>)'.
  Example: 'a nano model, Global Standard, flat across all regions — input $0.20, cached input $0.02, output $1.25 per 1M tokens (USD)'.
  In a comparison table, add explicit columns/labels for Deployment, Zone, and Region so the basis is visible per row. Mention Batch / Data Zone / Regional / cached only as clearly-labelled separate options, NEVER as the headline. If you cannot determine the deployment/zone/region for a row, say so rather than guessing.

MONTHLY / VOLUME COST ESTIMATES — DO NOT DO THE MATH YOURSELF: after you have the per-1M rates, for ANY 'monthly cost', 'cost for N conversations/requests', or model-vs-model total comparison you MUST call EstimateTokenCost with those rates and one shared set of token assumptions, then report ITS numbers verbatim. Hand-computing token costs in prose produces summary tables that disagree with the step-by-step — always delegate the arithmetic to EstimateTokenCost.

Common queries:
- Compare regions: serviceName='Virtual Machines' + armSkuName='Standard_D4s_v5' + priceType='Consumption'
- RI vs PAYG: serviceName='Virtual Machines' + armSkuName='Standard_D4s_v5' + armRegionName='eastus' (returns both)
- Storage tier costs: serviceName='Storage' + armRegionName='eastus' + meterName contains 'LRS'
- GPT model per-token: serviceName='Foundry Models' + productNameContains='GPT' (returns ALL variants — then pick the exact skuName: no 'Batch', ends 'Gl', 'Inp'/'Opt'/'cd Inp' — for Global Standard; do NOT min across rows)
- Llama / Phi / Mistral: serviceName='Foundry Models' + productNameContains='Llama' (or 'Phi', 'Mistral')
- Spot vs on-demand: serviceName='Virtual Machines' + armSkuName='Standard_D4s_v5' + meterName contains 'Spot'");

        yield return AIFunctionFactory.Create(GetAzureRetailPricingBatch, "GetAzureRetailPricingBatch",
            @"PUBLIC (no auth): Runs 2-8 independent Azure Retail Prices lookups IN PARALLEL inside ONE tool call. Use this whenever a comparison or estimate needs more than one distinct service/SKU filter. Do NOT call GetAzureRetailPricing repeatedly, and do NOT use bash/powershell/rg/grep to parse or combine pricing rows.

queriesJson is a JSON array. Each object supports: label (required for readable output), serviceName (required), armRegionName, armSkuName, priceType, meterNameContains, productNameContains, skuNameContains, currencyCode, rank, top.

Example — several VM SKUs in one model round-trip:
[{""label"":""D4s_v5"",""serviceName"":""Virtual Machines"",""armRegionName"":""eastus"",""armSkuName"":""Standard_D4s_v5"",""top"":20},{""label"":""D8s_v5"",""serviceName"":""Virtual Machines"",""armRegionName"":""eastus"",""armSkuName"":""Standard_D8s_v5"",""top"":20}]

Example — named Foundry models (go straight to this batch; NEVER run a broad GPT query first):
[{""label"":""GPT-4o"",""serviceName"":""Foundry Models"",""productNameContains"":""Azure OpenAI"",""skuNameContains"":""4o"",""priceType"":""Consumption"",""top"":50},{""label"":""GPT-4o-mini"",""serviceName"":""Foundry Models"",""productNameContains"":""Azure OpenAI"",""skuNameContains"":""4o-mini"",""priceType"":""Consumption"",""top"":50},{""label"":""GPT-4.1"",""serviceName"":""Foundry Models"",""productNameContains"":""Azure OpenAI"",""skuNameContains"":""4.1"",""priceType"":""Consumption"",""top"":50}]
For each, the tool returns the latest Standard Global text input/output rows, excluding Batch, cached, Data Zone, Regional, fine-tuning, audio and priority-processing variants, with prices normalized to USD per 1M tokens. Treat that summary as authoritative and do not verify it with another source.

Known-good database filters (East US example):
- SQL GP Gen5 compute: serviceName='SQL Database', armSkuName='SQLDB_GP_Compute_Gen5_8', productNameContains='Single/Elastic Pool General Purpose - Compute Gen5', meterNameContains='vCore'. Choose the ordinary `vCore` row, not `Zone Redundancy vCore`.
- SQL GP storage: serviceName='SQL Database', productNameContains='Single/Elastic Pool General Purpose - Storage', skuNameContains='General Purpose', meterNameContains='Data Stored'. Choose the non-Free paid row.
- Cosmos provisioned throughput: serviceName='Azure Cosmos DB', productNameContains='Azure Cosmos DB', skuNameContains='RUs', meterNameContains='100 RU/s'.
- Cosmos storage: serviceName='Azure Cosmos DB', productNameContains='Azure Cosmos DB', skuNameContains='RUs', meterNameContains='Data Stored'.
- PostgreSQL Flexible 8-vCore: serviceName='Azure Database for PostgreSQL', armSkuName='Standard_D8ds_v5'. Storage: productNameContains='Flex Server Storage', meterNameContains='Storage Data Stored'.

For one SKU across several regions, use ONE GetAzureRetailPricing call with comma-separated armRegionName instead. For storage tiers sharing one service/region, use ONE broad GetAzureRetailPricing call (for example meterNameContains='LRS'). Never re-query a batch result in the same turn unless that specific query returned no usable row.");
    }

    private static async Task<string> GetAzureRetailPricingBatch(
        [Description("JSON array of 2-8 pricing query objects. Each object: label, serviceName, and optional armRegionName, armSkuName, priceType, meterNameContains, productNameContains, skuNameContains, currencyCode, rank, top.")] string queriesJson)
    {
        using var doc = JsonDocument.Parse(queriesJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("queriesJson must be a JSON array.", nameof(queriesJson));

        static string? Str(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        static int Int(JsonElement item, string name, int fallback) =>
            item.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed)
                ? parsed
                : fallback;

        var queries = doc.RootElement.EnumerateArray().Take(9).Select((item, index) => new
        {
            Label = Str(item, "label") ?? $"Query {index + 1}",
            ServiceName = Str(item, "serviceName") ?? "",
            Region = Str(item, "armRegionName"),
            Sku = Str(item, "armSkuName"),
            PriceType = Str(item, "priceType"),
            Meter = Str(item, "meterNameContains"),
            Product = Str(item, "productNameContains"),
            SkuName = Str(item, "skuNameContains"),
            Currency = Str(item, "currencyCode"),
            Rank = Str(item, "rank"),
            Top = Math.Clamp(Int(item, "top", 25), 1, 50),
        }).ToList();

        if (queries.Count is < 2 or > 8)
            throw new ArgumentException("queriesJson must contain between 2 and 8 query objects.", nameof(queriesJson));
        if (queries.Any(q => string.IsNullOrWhiteSpace(q.ServiceName)))
            throw new ArgumentException("Every batch query requires serviceName.", nameof(queriesJson));

        var results = await Task.WhenAll(queries.Select(async q => new
        {
            q.Label,
            Result = await GetAzureRetailPricing(q.ServiceName, q.Region, q.Sku, q.PriceType,
                q.Meter, q.Product, q.SkuName, q.Currency, q.Rank, q.Top),
        }));

        var output = new StringBuilder();
        output.AppendLine($"BATCH RETAIL PRICING RESULTS — {results.Length} queries completed in parallel.");
        output.AppendLine("AUTHORITATIVE RETAIL API RESULT. Use it directly; do not re-query, invoke shell/search, or fetch a pricing web page when every section has rows.");
        foreach (var result in results)
        {
            output.AppendLine().Append("=== ").Append(result.Label).AppendLine(" ===");
            output.AppendLine(CompactBatchResult(result.Label, result.Result));
        }
        return output.ToString();
    }

    // Batch responses must stay below the Copilot CLI's inline-result limit.
    // Otherwise it writes the payload to a temp file and the model spends one
    // full round-trip per `view` call reading chunks — measured at 3 extra calls
    // and ~37 seconds for the 3-tier starter. Keep the decision-relevant fields
    // from the raw API rows, relevance-rank them by the caller's label, and also
    // retain the best row for each meter type so mixed compute/storage queries
    // do not lose a lower-scoring but necessary component.
    private static string CompactBatchResult(string label, string result)
    {
        var jsonStart = result.IndexOf("{\"BillingCurrency\"", StringComparison.Ordinal);
        if (jsonStart < 0)
            return result.Length <= 8_000 ? result : result[..8_000] + "\n[TRUNCATED]";

        try
        {
            using var doc = JsonDocument.Parse(result[jsonStart..]);
            if (!doc.RootElement.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array)
                return result.Length <= 8_000 ? result : result[..8_000] + "\n[TRUNCATED]";

            if (result.Contains("serviceName eq 'Foundry Models'", StringComparison.Ordinal))
            {
                var billingCurrency = doc.RootElement.TryGetProperty("BillingCurrency", out var currencyElement)
                    ? currencyElement.GetString() ?? "currency unknown"
                    : "currency unknown";
                var paginationComplete = !result.Contains("paginationComplete=False", StringComparison.OrdinalIgnoreCase);
                var foundrySummary = BuildFoundryStandardGlobalSummary(label, items, billingCurrency, paginationComplete);
                if (!string.IsNullOrEmpty(foundrySummary)) return foundrySummary;
            }

            static string Str(JsonElement item, string name) =>
                item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? ""
                    : "";
            static string Clean(string value) => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

            var tokens = label
                .Split(new[] { ' ', '-', '_', '/', '(', ')', '×' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim().ToLowerInvariant())
                .Where(t => t.Length >= 3 || t.All(char.IsDigit))
                .Distinct()
                .ToArray();

            var rows = items.EnumerateArray().Select(item =>
            {
                var region = Str(item, "armRegionName");
                var armSku = Str(item, "armSkuName");
                var product = Str(item, "productName");
                var sku = Str(item, "skuName");
                var meter = Str(item, "meterName");
                var unit = Str(item, "unitOfMeasure");
                var type = Str(item, "type");
                var term = Str(item, "reservationTerm");
                var searchable = string.Join(' ', region, armSku, product, sku, meter, unit, type, term).ToLowerInvariant();
                var score = tokens.Count(searchable.Contains);
                var price = item.TryGetProperty("retailPrice", out var p) && p.TryGetDouble(out var parsed)
                    ? parsed.ToString(CultureInfo.InvariantCulture)
                    : "";
                var savings = item.TryGetProperty("savingsPlan", out var sp) && sp.ValueKind == JsonValueKind.Array
                    ? string.Join(',', sp.EnumerateArray().Select(plan =>
                    {
                        var planPrice = plan.TryGetProperty("retailPrice", out var pp) && pp.TryGetDouble(out var pv)
                            ? pv.ToString(CultureInfo.InvariantCulture)
                            : "";
                        return $"{Str(plan, "term")}:{planPrice}";
                    }))
                    : "";
                return new BatchRow(
                    score,
                    meter,
                    string.Join('|', price, region, armSku, product, sku, meter, unit, type, term, savings),
                    string.Join('\t', price, Clean(region), Clean(armSku), Clean(product), Clean(sku),
                        Clean(meter), Clean(unit), Clean(type), Clean(term), Clean(savings)));
            }).ToList();

            var ordered = rows.OrderByDescending(r => r.Score).ThenBy(r => r.MeterKey).ThenBy(r => r.Key).ToList();
            var selected = new List<BatchRow>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Add(BatchRow row)
            {
                if (seen.Add(row.Key)) selected.Add(row);
            }
            foreach (var row in ordered.Take(12)) Add(row);
            foreach (var row in ordered.GroupBy(r => r.MeterKey).Select(g => g.First())) Add(row);

            var queryLine = result.Split('\n').FirstOrDefault(line => line.StartsWith("Query: ", StringComparison.Ordinal));
            var output = new StringBuilder();
            if (queryLine is not null) output.AppendLine(queryLine);
            output.AppendLine($"API rows: {rows.Count}; showing {Math.Min(selected.Count, 24)} relevance-ranked, meter-diverse rows.");
            output.AppendLine("retailPrice\tarmRegionName\tarmSkuName\tproductName\tskuName\tmeterName\tunitOfMeasure\ttype\treservationTerm\tsavingsPlan(term:price)");
            foreach (var row in selected.Take(24)) output.AppendLine(row.Line);
            return output.ToString();
        }
        catch (JsonException)
        {
            return result.Length <= 8_000 ? result : result[..8_000] + "\n[TRUNCATED — narrow filters]";
        }
    }

    private static string BuildFoundryStandardGlobalSummary(
        string label,
        JsonElement items,
        string currency,
        bool paginationComplete)
    {
        static string Str(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";

        var normalizedLabel = label.ToLowerInvariant().Replace('-', ' ');
        var candidates = new List<(string Direction, int VersionRank, DateTimeOffset Effective, string Sku, string Meter, double PerMillion, string Unit)>();
        foreach (var item in items.EnumerateArray())
        {
            var sku = Str(item, "skuName");
            var normalized = sku.ToLowerInvariant().Replace('-', ' ');
            if (!Str(item, "type").Equals("Consumption", StringComparison.OrdinalIgnoreCase))
                continue;
            var modelMatches = normalizedLabel.Contains("4o mini", StringComparison.Ordinal)
                ? normalized.Contains("4o mini", StringComparison.Ordinal)
                : normalizedLabel.Contains("4o", StringComparison.Ordinal)
                    ? normalized.Contains("4o", StringComparison.Ordinal) && !normalized.Contains("mini", StringComparison.Ordinal)
                    : normalizedLabel.Contains("4.1", StringComparison.Ordinal)
                        ? normalized.Contains("4.1", StringComparison.Ordinal) &&
                          !normalized.Contains("mini", StringComparison.Ordinal) &&
                          !normalized.Contains("nano", StringComparison.Ordinal)
                        : normalized.Contains(normalizedLabel, StringComparison.Ordinal);
            if (!modelMatches) continue;
            if (normalized.Contains("batch", StringComparison.Ordinal) ||
                normalized.Contains("cached", StringComparison.Ordinal) ||
                normalized.Contains("cchd", StringComparison.Ordinal) ||
                normalized.Contains("cd inp", StringComparison.Ordinal) ||
                normalized.Contains("data zone", StringComparison.Ordinal) ||
                normalized.Contains(" regnl", StringComparison.Ordinal) ||
                normalized.Contains(" regional", StringComparison.Ordinal) ||
                normalized.Contains(" ft ", StringComparison.Ordinal) ||
                normalized.Contains(" dev ", StringComparison.Ordinal) ||
                normalized.Contains("training", StringComparison.Ordinal) ||
                normalized.Contains("hosting", StringComparison.Ordinal) ||
                normalized.Contains("audio", StringComparison.Ordinal) ||
                normalized.Contains(" aud ", StringComparison.Ordinal) ||
                normalized.Contains("transcribe", StringComparison.Ordinal) ||
                normalized.Contains(" tts ", StringComparison.Ordinal) ||
                normalized.Contains(" tcrb ", StringComparison.Ordinal) ||
                normalized.Contains(" pp ", StringComparison.Ordinal))
                continue;
            if (!(normalized.EndsWith(" gl", StringComparison.Ordinal) ||
                  normalized.EndsWith(" glbl", StringComparison.Ordinal) ||
                  normalized.EndsWith(" global", StringComparison.Ordinal)))
                continue;

            var direction = normalized.Contains(" outp ", StringComparison.Ordinal) ||
                            normalized.Contains(" output ", StringComparison.Ordinal) ||
                            normalized.Contains(" opt ", StringComparison.Ordinal)
                ? "output"
                : normalized.Contains(" inp ", StringComparison.Ordinal) || normalized.Contains(" input ", StringComparison.Ordinal)
                    ? "input"
                    : "";
            if (direction.Length == 0 ||
                !item.TryGetProperty("retailPrice", out var priceElement) ||
                !priceElement.TryGetDouble(out var price))
                continue;

            var unit = Str(item, "unitOfMeasure");
            var perMillion = unit.Equals("1K", StringComparison.OrdinalIgnoreCase) ? price * 1000 : price;
            var versionRank = normalizedLabel.Contains("4o mini", StringComparison.Ordinal)
                ? normalized.Contains("0718", StringComparison.Ordinal) ? 100 : 0
                : normalizedLabel.Contains("4o", StringComparison.Ordinal)
                    ? normalized.Contains("1120", StringComparison.Ordinal) ? 100
                        : normalized.Contains("0806", StringComparison.Ordinal) ? 90
                        : normalized.Contains("0513", StringComparison.Ordinal) ? 80
                        : 0
                    : 0;
            _ = DateTimeOffset.TryParse(Str(item, "effectiveStartDate"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var effective);
            candidates.Add((direction, versionRank, effective, sku, Str(item, "meterName"), perMillion, unit));
        }

        var selected = candidates
            .GroupBy(row => row.Direction, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(row => row.VersionRank).ThenByDescending(row => row.Effective).First())
            .OrderBy(row => row.Direction)
            .ToList();
        if (selected.Count < 2) return "";

        var output = new StringBuilder();
        output.AppendLine(paginationComplete
            ? "FOUNDRY STANDARD GLOBAL TEXT PRICING — AUTHORITATIVE, COMPLETE; DO NOT VERIFY ELSEWHERE."
            : "FOUNDRY STANDARD GLOBAL TEXT PRICING — PARTIAL; RETAIL API PAGINATION DID NOT COMPLETE.");
        output.Append("model\tdirection\t").Append(currency)
            .AppendLine(" per 1M tokens\tskuName\tmeterName\teffectiveStartDate");
        foreach (var row in selected)
            output.Append(label).Append('\t').Append(row.Direction).Append('\t')
                .Append(row.PerMillion.ToString("0.########", CultureInfo.InvariantCulture)).Append('\t')
                .Append(row.Sku).Append('\t').Append(row.Meter).Append('\t')
                .AppendLine(row.Effective.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return output.ToString();
    }

    private static async Task<string> GetAzureRetailPricing(
        [Description("Service name, e.g. 'Virtual Machines', 'Storage', 'SQL Database', 'Foundry Models'. REQUIRED.")] string serviceName,
        [Description("ARM region (lowercase, no spaces), e.g. 'eastus', 'westeurope'. Pass a COMMA-SEPARATED LIST to compare regions in ONE call, e.g. 'eastus,westeurope,swedencentral' — always do this instead of calling the tool once per region. Empty = all regions.")] string? armRegionName = null,
        [Description("ARM SKU name, e.g. 'Standard_D4s_v5'. Empty = all SKUs.")] string? armSkuName = null,
        [Description("Price type: 'Consumption' (PAYG), 'Reservation' (1y/3y RI), 'DevTestConsumption'. Empty = all.")] string? priceType = null,
        [Description("Substring match on meterName, e.g. 'Spot' or 'LRS'. Empty = no meter filter.")] string? meterNameContains = null,
        [Description("Substring match on productName, e.g. 'GPT' / 'Llama' / 'Phi' for Foundry Models, or 'Premium SSD' for storage. Foundry productName is a family bucket — use 'GPT' not 'gpt-4'. Empty = no product filter.")] string? productNameContains = null,
        [Description("Substring match on skuName, e.g. '8 vCore', 'RUs', 'GPT-4o Inp Gl'. Use with productNameContains when the product is a broad family. Empty = no SKU-name filter.")] string? skuNameContains = null,
        [Description("Currency code (default 'USD'). Supported: USD, EUR, GBP, JPY, NOK, etc.")] string? currencyCode = null,
        [Description("Set to 'cheapest' to PREPEND a price-sorted summary of the matching rows (one line per row, lowest retailPrice first). Use this for any 'cheapest/lowest/top N regions' question so a SINGLE call answers it — do NOT call this tool once per region and do NOT sort the rows yourself.")] string? rank = null,
        [Description("Max results (default 50, max 100). Lower = faster.")] int top = 50)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
            return "Error: serviceName is required (e.g. 'Virtual Machines'). Querying without a service filter would return millions of rows.";

        top = Math.Clamp(top, 1, 100);
        currencyCode = string.IsNullOrWhiteSpace(currencyCode) ? "USD" : currencyCode.Trim().ToUpperInvariant();
        var isFoundry = serviceName.Trim().Equals("Foundry Models", StringComparison.OrdinalIgnoreCase);

        var filters = new List<string> { $"serviceName eq '{Esc(serviceName)}'" };
        // Multi-region in ONE call: a 3-region comparison used to cost 3 sequential
        // model round-trips (~2.5s each) to fetch data the API returns in ~230ms.
        var regionCount = 0;
        var requestedRegions = new List<string>();
        if (!string.IsNullOrWhiteSpace(armRegionName))
        {
            requestedRegions = armRegionName
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(r => r.ToLowerInvariant())
                .Distinct()
                .ToList();
            regionCount = requestedRegions.Count;
            if (requestedRegions.Count == 1)
                filters.Add($"armRegionName eq '{Esc(requestedRegions[0])}'");
            else if (requestedRegions.Count > 1)
                filters.Add("(" + string.Join(" or ", requestedRegions.Select(r => $"armRegionName eq '{Esc(r)}'")) + ")");
        }
        // Rows are shared across the requested regions, so the default cap could
        // truncate a region away entirely and silently skew the comparison.
        if (regionCount > 1)
            top = Math.Clamp(Math.Max(top, 25 * regionCount), 1, 100);
        if (!string.IsNullOrWhiteSpace(armSkuName)) filters.Add($"armSkuName eq '{Esc(armSkuName.Trim())}'");
        if (!string.IsNullOrWhiteSpace(priceType)) filters.Add($"priceType eq '{Esc(priceType.Trim())}'");
        if (!string.IsNullOrWhiteSpace(meterNameContains)) filters.Add($"contains(meterName, '{Esc(meterNameContains.Trim())}')");
        if (!string.IsNullOrWhiteSpace(productNameContains)) filters.Add($"contains(productName, '{Esc(productNameContains.Trim())}')");
        if (!string.IsNullOrWhiteSpace(skuNameContains)) filters.Add($"contains(skuName, '{Esc(skuNameContains.Trim())}')");

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

        var firstPage = await FetchRetailPage(url, activity);
        var body = firstPage.Body;
        var pageCount = 1;
        var paginationComplete = true;
        var paginate = firstPage.Status is >= 200 and < 300
            && (string.Equals(rank?.Trim(), "cheapest", StringComparison.OrdinalIgnoreCase)
                || regionCount > 1
                || !string.IsNullOrWhiteSpace(armSkuName)
                || isFoundry);
        var allItems = new List<JsonElement>();
        string? billingCurrency = null;
        string? nextLink = null;

        if (paginate)
        {
            const int maxPages = 20;
            const int maxItems = 5000;
            var page = firstPage;
            for (var pageIndex = 0; pageIndex < maxPages; pageIndex++)
            {
                try
                {
                    using var pageDoc = JsonDocument.Parse(page.Body);
                    billingCurrency ??= pageDoc.RootElement.TryGetProperty("BillingCurrency", out var currencyElement)
                        ? currencyElement.GetString()
                        : null;
                    if (!pageDoc.RootElement.TryGetProperty("Items", out var items)
                        || items.ValueKind != JsonValueKind.Array)
                        break;
                    allItems.AddRange(items.EnumerateArray().Select(item => item.Clone()));
                    nextLink = pageDoc.RootElement.TryGetProperty("NextPageLink", out var nextElement)
                        ? nextElement.GetString()
                        : null;
                }
                catch (JsonException)
                {
                    paginationComplete = false;
                    break;
                }

                if (allItems.Count > maxItems)
                {
                    allItems = allItems.Take(maxItems).ToList();
                    paginationComplete = false;
                    break;
                }
                if (string.IsNullOrWhiteSpace(nextLink)) break;
                if (pageIndex == maxPages - 1)
                {
                    paginationComplete = false;
                    break;
                }
                if (!Uri.TryCreate(nextLink, UriKind.Absolute, out var nextUri)
                    || nextUri.Scheme != Uri.UriSchemeHttps
                    || !nextUri.Host.Equals("prices.azure.com", StringComparison.OrdinalIgnoreCase))
                {
                    paginationComplete = false;
                    break;
                }

                page = await FetchRetailPage(nextUri.AbsoluteUri, activity);
                pageCount++;
                if (page.Status is < 200 or >= 300)
                {
                    paginationComplete = false;
                    break;
                }
            }

            body = JsonSerializer.Serialize(new
            {
                BillingCurrency = billingCurrency ?? currencyCode,
                Items = allItems,
                NextPageLink = paginationComplete ? null : nextLink
            });
        }

        activity?.SetTag("pricing.status_code", firstPage.Status);
        activity?.SetTag("pricing.response_length", body.Length);
        activity?.SetTag("pricing.pages", pageCount);
        activity?.SetTag("pricing.pagination_complete", paginationComplete);

        var foundRegions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bodyRowCount = 0;
        try
        {
            using var coverageDoc = JsonDocument.Parse(body);
            if (coverageDoc.RootElement.TryGetProperty("Items", out var coverageItems)
                && coverageItems.ValueKind == JsonValueKind.Array)
            {
                bodyRowCount = coverageItems.GetArrayLength();
                foreach (var item in coverageItems.EnumerateArray())
                    if (item.TryGetProperty("armRegionName", out var regionElement)
                        && !string.IsNullOrWhiteSpace(regionElement.GetString()))
                        foundRegions.Add(regionElement.GetString()!);
            }
        }
        catch (JsonException) { }
        var missingRegions = requestedRegions.Where(region => !foundRegions.Contains(region)).ToArray();

        var header = $"HTTP {firstPage.Status} {firstPage.StatusText}\nQuery: {filter} (top={top}, currency={currencyCode})\nUTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}\nPages: {pageCount}; paginationComplete={paginationComplete}; rows={bodyRowCount}\n";
        if (requestedRegions.Count > 0)
            header += missingRegions.Length == 0
                ? $"Region coverage: {requestedRegions.Count}/{requestedRegions.Count}.\n"
                : $"Region coverage incomplete: missing {string.Join(", ", missingRegions)}.\n";

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

        var cheapestSummary = BuildCheapestSummary(rank, serviceName, armSkuName, body, paginationComplete, missingRegions);
        if (!string.IsNullOrEmpty(cheapestSummary))
            return header + cheapestSummary;
        if (body.Length > 8_000)
        {
            var compactLabel = string.Join(' ', new[] { armSkuName, skuNameContains, productNameContains, meterNameContains, serviceName }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            return header + CompactBatchResult(compactLabel, header + body);
        }
        return header + body;
    }

    private static async Task<RetailPage> FetchRetailPage(string url, System.Diagnostics.Activity? activity)
    {
        const int maxAttempts = 4;
        HttpResponseMessage response = null!;
        string body = "";
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "FinOps-Dashboard/1.0");
            response = await Http.SendAsync(request);
            body = await response.Content.ReadAsStringAsync();

            if ((int)response.StatusCode != 429 && (int)response.StatusCode < 500) break;
            if (attempt == maxAttempts - 1) break;

            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds
                          ?? Math.Min(Math.Pow(2, attempt + 1) + Random.Shared.NextDouble(), 30);
            var waitSeconds = Math.Max(1, retryAfter);
            activity?.SetTag($"pricing.retry_{attempt}", $"{(int)response.StatusCode}, waiting {waitSeconds:F0}s");
            var turnKey = System.Diagnostics.Activity.Current?.GetBaggageItem("finops.turn.id");
            if (turnKey is not null && HttpHelper.RetryReporters.TryGetValue(turnKey, out var report))
            {
                try { await report(attempt + 1, waitSeconds, url, "pricing", (int)response.StatusCode); }
                catch (Exception emitEx)
                {
                    HttpHelper.Logger?.LogWarning(emitEx,
                        "SSE cooling_down emit failed for pricing attempt={Attempt}", attempt + 1);
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(waitSeconds));
        }

        return new RetailPage((int)response.StatusCode, response.StatusCode.ToString(), body);
    }

    // "Cheapest N regions" is the single most common pricing question and the
    // slowest: unsorted rows forced the model to either shell out to sort them
    // (~19s of a 37s turn) or issue one API call per region (11 calls, 126s).
    // A compact, pre-sorted digest removes both. For VM surveys, select the
    // ordinary commercial Linux PAYG meter (not Windows, Spot, Low Priority,
    // US Gov/DoD or China), then deduplicate by region. Returning the full page
    // alongside the summary defeated the optimization because the CLI moved it
    // to a temp file and the model started shell-parsing it anyway.
    private static string BuildCheapestSummary(
        string? rank,
        string serviceName,
        string? armSkuName,
        string body,
        bool paginationComplete,
        IReadOnlyCollection<string> missingRegions)
    {
        if (!string.Equals(rank?.Trim(), "cheapest", StringComparison.OrdinalIgnoreCase))
            return "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("Items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
                return "";

            var rows = new List<(double Price, string Region, string ArmSku, string Product, string Sku, string Meter, string PriceType)>();
            foreach (var it in items.EnumerateArray())
            {
                if (!it.TryGetProperty("retailPrice", out var p) ||
                    !p.TryGetDouble(out var price)) continue;
                string Str(string name) =>
                    it.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                        ? v.GetString() ?? "" : "";
                rows.Add((price, Str("armRegionName"), Str("armSkuName"), Str("productName"),
                    Str("skuName"), Str("meterName"), Str("type")));
            }
            if (rows.Count == 0) return "";

            IEnumerable<(double Price, string Region, string ArmSku, string Product, string Sku, string Meter, string PriceType)> ranked = rows;
            // Pay-as-you-go only, for every service: Reservation and DevTest rows
            // carry a lower unit price and would otherwise win "cheapest".
            ranked = ranked.Where(r =>
                !string.IsNullOrWhiteSpace(r.Region) &&
                r.PriceType.Equals("Consumption", StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(armSkuName) ||
                 r.ArmSku.Equals(armSkuName.Trim(), StringComparison.OrdinalIgnoreCase)));
            if (serviceName.Trim().Equals("Virtual Machines", StringComparison.OrdinalIgnoreCase))
            {
                ranked = ranked.Where(r =>
                    !r.Region.StartsWith("usgov", StringComparison.OrdinalIgnoreCase) &&
                    !r.Region.StartsWith("usdod", StringComparison.OrdinalIgnoreCase) &&
                    !r.Region.StartsWith("china", StringComparison.OrdinalIgnoreCase) &&
                    !r.Product.Contains("Windows", StringComparison.OrdinalIgnoreCase) &&
                    !r.Meter.Contains("Spot", StringComparison.OrdinalIgnoreCase) &&
                    !r.Meter.Contains("Low Priority", StringComparison.OrdinalIgnoreCase));
            }

            var byRegion = ranked
                .GroupBy(r => r.Region, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderBy(r => r.Price).First())
                .OrderBy(r => r.Price)
                .ToList();
            if (byRegion.Count == 0) return "";

            var sb = new StringBuilder();
            sb.Append(paginationComplete && missingRegions.Count == 0
                ? "CHEAPEST-FIRST SUMMARY (complete, already filtered/sorted — do not re-query, view files, or shell-sort):\n"
                : "CHEAPEST-FIRST SUMMARY (PARTIAL — pagination or requested-region coverage was incomplete):\n");
            if (serviceName.Trim().Equals("Virtual Machines", StringComparison.OrdinalIgnoreCase))
                sb.Append("Basis: commercial Azure regions, Linux standard PAYG; Windows, Spot, Low Priority, US Gov/DoD and China excluded.\n");
            sb.Append("retailPrice\tarmRegionName\tarmSkuName\tproductName\tskuName\tmeterName\tpriceType\n");
            foreach (var r in byRegion.Take(40))
                sb.Append(r.Price.ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append(r.Region).Append('\t').Append(r.ArmSku).Append('\t')
                    .Append(r.Product).Append('\t').Append(r.Sku).Append('\t')
                    .Append(r.Meter).Append('\t').Append(r.PriceType).Append('\n');
            if (byRegion.Count > 40) sb.Append($"[{byRegion.Count - 40} higher-priced regions omitted]\n");
            sb.Append('\n');
            return sb.ToString();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return "";
        }
    }

    // OData single-quote escape: ' → ''
    private static string Esc(string s) => s.Replace("'", "''");
}
