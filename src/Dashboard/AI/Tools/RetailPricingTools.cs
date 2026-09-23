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
    private sealed record RetailPage(int Status, string StatusText, string Body);

    private const int MaxProjectedRows = 40;
    private const int MaxFacetValues = 25;

    private static int RowCount(RetailPage page)
    {
        if (page.Status is < 200 or >= 300) return -1;
        try
        {
            using var doc = JsonDocument.Parse(page.Body);
            return doc.RootElement.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array
                ? items.GetArrayLength()
                : -1;
        }
        catch (JsonException) { return -1; }
    }

    public static IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(GetAzureRetailPricing, "GetAzureRetailPricing",
            @"PUBLIC (no auth): Azure Retail Prices API — pay-as-you-go, reservation and savings-plan rates for any Azure service. Use this BEFORE QueryAzure when comparing SKUs or regions, or costing a workload that is not deployed yet.

STRUCTURAL FILTERS (safe to supply from what the user named):
- serviceName (REQUIRED): e.g. 'Virtual Machines', 'Storage', 'SQL Database', 'Load Balancer', 'Foundry Models' (covers ALL Azure OpenAI + open-model inference; the legacy 'Azure OpenAI' serviceName returns 0 rows).
- armRegionName: lowercase, no spaces, e.g. 'eastus'. COMMA-SEPARATE to compare many regions in ONE call. Some services are priced globally rather than per region, so a region filter can legitimately match nothing.
- armSkuName: the ARM SKU, e.g. 'Standard_D4s_v5'.
- priceType: 'Consumption' (pay-as-you-go — note this ALSO includes Spot and Low Priority rows), 'Reservation', 'DevTestConsumption'.
- rank='cheapest': paginate fully so a cheapest-across-regions answer is complete.

VOCABULARY FILTERS (meterNameContains / productNameContains / skuNameContains) — DO NOT GUESS THESE. Meter and SKU names are not derivable from the ARM SKU: 'Standard_ND96asr_v4' meters as 'ND96asr_A100_v4'. If your guess matches nothing, the tool automatically drops it, returns the structural result set instead, and tells you it did.

EVERY RESPONSE STARTS WITH A `FACETS` BLOCK giving the live distinct values of each field. That is the authoritative vocabulary — read it, then filter with those exact strings. It reflects the API right now, so prefer it over anything you remember.

READING THE ROWS: they arrive grouped by meterName, cheapest-first within each meter. Spot, Low Priority, Windows, Reservation, cached-input and regional/zonal variants are all present and are distinguishable via meterName / skuName / type. NEVER compare across different meterName values, and never let the globally cheapest row become the headline.

DEFAULT INTERPRETATION: unless the user explicitly asked for Spot, Low Priority, Windows, reserved or zone-redundant pricing, answer with the ordinary on-demand meter — the meterName carrying no such qualifier — and name the meter you used. A 'cheapest region' question means cheapest on-demand region, not cheapest Spot region.

UNIT SEMANTICS: retailPrice is the price for ONE `unitOfMeasure` of the WHOLE SKU in armSkuName/skuName. Never multiply it by a core/vCore/GPU/node count that is already part of that SKU name — e.g. armSkuName 'SQLDB_GP_Compute_Gen5_4' / skuName '4 vCore' at 1 Hour is the total hourly price for all 4 vCores, not per vCore. Multiply only by quantity the user asked for (number of instances) and by hours.

MONTHLY / VOLUME TOTALS: call EstimateTokenCost with the per-1M rates instead of doing token arithmetic in prose.");

        yield return AIFunctionFactory.Create(GetAzureRetailPricingBatch, "GetAzureRetailPricingBatch",
            @"PUBLIC (no auth): Runs 2-8 independent Azure Retail Prices lookups IN PARALLEL inside ONE tool call. Use this whenever a comparison or estimate needs more than one distinct service/SKU filter. Do NOT call GetAzureRetailPricing repeatedly, and do NOT use bash/powershell/rg/grep to parse or combine pricing rows.

    The tool returns a FACETS block per section with the live distinct field values. If a section's vocabulary filter matched nothing, it is dropped automatically and the wider result set is returned instead — read that section's facets and re-filter from them rather than fetching a pricing web page.

queriesJson is a JSON array. Each object supports: label (required for readable output), serviceName (required), armRegionName, armSkuName, priceType, meterNameContains, productNameContains, skuNameContains, currencyCode, rank, top.

Example — several VM SKUs in one model round-trip:
[{""label"":""D4s_v5"",""serviceName"":""Virtual Machines"",""armRegionName"":""eastus"",""armSkuName"":""Standard_D4s_v5"",""top"":20},{""label"":""D8s_v5"",""serviceName"":""Virtual Machines"",""armRegionName"":""eastus"",""armSkuName"":""Standard_D8s_v5"",""top"":20}]

Example — named Foundry models (go straight to this batch; NEVER run a broad GPT query first):
[{""label"":""GPT-4o"",""serviceName"":""Foundry Models"",""productNameContains"":""Azure OpenAI"",""skuNameContains"":""4o"",""priceType"":""Consumption"",""top"":50},{""label"":""GPT-4o-mini"",""serviceName"":""Foundry Models"",""productNameContains"":""Azure OpenAI"",""skuNameContains"":""4o-mini"",""priceType"":""Consumption"",""top"":50},{""label"":""GPT-4.1"",""serviceName"":""Foundry Models"",""productNameContains"":""Azure OpenAI"",""skuNameContains"":""4.1"",""priceType"":""Consumption"",""top"":50}]
Foundry sections mix deployment types and residency zones in one result set, so read the skuName facet and quote the variant asked for — real-time Standard Global unless stated otherwise — rather than the cheapest row.

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
            output.AppendLine(CompactBatchResult(result.Result));
        }
        return output.ToString();
    }

    // Cap the payload so the CLI keeps the result inline: past its limit it spills
    // to a temp file and the model spends a `view` round-trip per chunk.
    private static string CompactBatchResult(string result)
    {
        var jsonStart = result.IndexOf("{\"BillingCurrency\"", StringComparison.Ordinal);
        if (jsonStart < 0)
            return result.Length <= 8_000 ? result : result[..8_000] + "\n[TRUNCATED]";

        try
        {
            using var doc = JsonDocument.Parse(result[jsonStart..]);
            if (!doc.RootElement.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array)
                return result.Length <= 8_000 ? result : result[..8_000] + "\n[TRUNCATED]";

            static string Str(JsonElement item, string name) =>
                item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? ""
                    : "";
            static string Clean(string value) => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

            var rows = items.EnumerateArray().Select(item =>
            {
                var price = item.TryGetProperty("retailPrice", out var p) && p.TryGetDouble(out var parsed)
                    ? parsed
                    : double.MaxValue;
                var savings = item.TryGetProperty("savingsPlan", out var sp) && sp.ValueKind == JsonValueKind.Array
                    ? string.Join(',', sp.EnumerateArray().Select(plan =>
                    {
                        var planPrice = plan.TryGetProperty("retailPrice", out var pp) && pp.TryGetDouble(out var pv)
                            ? pv.ToString(CultureInfo.InvariantCulture)
                            : "";
                        return $"{Str(plan, "term")}:{planPrice}";
                    }))
                    : "";
                var line = string.Join('\t',
                    price == double.MaxValue ? "" : price.ToString(CultureInfo.InvariantCulture),
                    Clean(Str(item, "armRegionName")), Clean(Str(item, "armSkuName")),
                    Clean(Str(item, "productName")), Clean(Str(item, "skuName")),
                    Clean(Str(item, "meterName")), Clean(Str(item, "unitOfMeasure")),
                    Clean(Str(item, "type")), Clean(Str(item, "reservationTerm")), Clean(savings));
                return (Price: price, Meter: Clean(Str(item, "meterName")), Line: line);
            })
            .GroupBy(row => row.Line, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

            // Round-robin across meterName groups. A flat cheapest-first cut buries
            // the ordinary on-demand meter under every Spot/Low-Priority row and the
            // model then quotes Spot as the headline price.
            var byMeter = rows
                .GroupBy(row => row.Meter, StringComparer.Ordinal)
                .Select(group => group.OrderBy(row => row.Price).ToList())
                .OrderBy(group => group[0].Price)
                .ToList();
            var selected = new List<(double Price, string Meter, string Line)>();
            for (var depth = 0; selected.Count < MaxProjectedRows; depth++)
            {
                var added = false;
                foreach (var group in byMeter)
                {
                    if (depth >= group.Count) continue;
                    selected.Add(group[depth]);
                    added = true;
                    if (selected.Count >= MaxProjectedRows) break;
                }
                if (!added) break;
            }

            var queryLine = result.Split('\n').FirstOrDefault(line => line.StartsWith("Query: ", StringComparison.Ordinal));
            var output = new StringBuilder();
            if (queryLine is not null) output.AppendLine(queryLine);
            output.Append(BuildFacets(items));
            if (byMeter.Count > 1)
                output.AppendLine($"These rows span {byMeter.Count} different meterName values. Compare like-for-like WITHIN one meterName; do not mix meters in one comparison.");
            output.AppendLine(rows.Count <= selected.Count
                ? $"{rows.Count} distinct row(s), cheapest first within each meter."
                : $"{rows.Count} distinct rows; showing {selected.Count} spread across meters, cheapest first within each. Narrow using the facet values above.");
            output.AppendLine("retailPrice\tarmRegionName\tarmSkuName\tproductName\tskuName\tmeterName\tunitOfMeasure\ttype\treservationTerm\tsavingsPlan(term:price)");
            foreach (var row in selected.OrderBy(r => r.Meter, StringComparer.Ordinal).ThenBy(r => r.Price))
                output.AppendLine(row.Line);
            return output.ToString();
        }
        catch (JsonException)
        {
            return result.Length <= 8_000 ? result : result[..8_000] + "\n[TRUNCATED — narrow filters]";
        }
    }

    // The model cannot guess vocabulary like `ND96asr_A100_v4` from the SKU name
    // `Standard_ND96asr_v4`, so every response ships the live distinct values.
    // Measured at ~281 chars for a 382-row payload — 0.12% overhead.
    private static string BuildFacets(JsonElement items)
    {
        var values = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            foreach (var property in item.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String) continue;
                var text = property.Value.GetString();
                if (string.IsNullOrEmpty(text) || text.Length > 120) continue;
                if (!values.TryGetValue(property.Name, out var set))
                    values[property.Name] = set = new SortedSet<string>(StringComparer.Ordinal);
                // Keep one past the cap purely as an overflow flag.
                if (set.Count <= MaxFacetValues) set.Add(text);
            }
        }

        var output = new StringBuilder("FACETS (live distinct values — filter with these exact strings):\n");
        foreach (var (field, set) in values.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (set.Count <= 1) continue;
            var truncated = set.Count > MaxFacetValues;
            output.Append("  ").Append(field).Append(" (");
            output.Append(truncated ? $"more than {MaxFacetValues}, showing {MaxFacetValues}" : set.Count.ToString(CultureInfo.InvariantCulture));
            output.Append("): ");
            output.AppendLine(string.Join(" | ", set.Take(MaxFacetValues)));
        }
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
        serviceName = serviceName.Trim();
        currencyCode = string.IsNullOrWhiteSpace(currencyCode) ? "USD" : currencyCode.Trim().ToUpperInvariant();
        var isFoundry = serviceName.Equals("Foundry Models", StringComparison.OrdinalIgnoreCase);

        // The public API's identifiers are not consistently aligned with the Azure
        // portal/display names callers naturally provide. Normalize the stable,
        // high-volume aliases here instead of forcing a zero-row response followed
        // by another model round and a web fallback.
        if (serviceName.Equals("Virtual Machines", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(armSkuName)
            && !string.IsNullOrWhiteSpace(meterNameContains))
        {
            var compactMeter = meterNameContains.Replace("_", "", StringComparison.Ordinal)
                .Replace(" ", "", StringComparison.Ordinal);
            var compactSku = armSkuName.Replace("Standard_", "", StringComparison.OrdinalIgnoreCase)
                .Replace("_", "", StringComparison.Ordinal)
                .Replace(" ", "", StringComparison.Ordinal);
            if (compactSku.Contains(compactMeter, StringComparison.OrdinalIgnoreCase)
                || compactMeter.Contains(compactSku, StringComparison.OrdinalIgnoreCase))
                meterNameContains = null;
        }

        if (serviceName.Equals("Storage", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(armSkuName)
            && armSkuName.StartsWith("P", StringComparison.OrdinalIgnoreCase)
            && armSkuName.Contains("LRS", StringComparison.OrdinalIgnoreCase))
        {
            skuNameContains ??= armSkuName;
            productNameContains ??= "Premium SSD Managed Disks";
            armSkuName = null;
        }

        if (serviceName.Equals("Load Balancer", StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(armRegionName)
                || !armRegionName.Equals("Global", StringComparison.OrdinalIgnoreCase)))
        {
            armRegionName = "Global";
            productNameContains = "Load Balancer";
            skuNameContains ??= "Standard";
        }

        var filters = new List<string> { $"serviceName eq '{Esc(serviceName)}'" };
        // Multi-region in ONE call: a 3-region comparison used to cost 3 sequential
        // model round-trips (~2.5s each) to fetch data the API returns in ~230ms.
        var regionCount = 0;
        var requestedRegions = new List<string>();
        if (!string.IsNullOrWhiteSpace(armRegionName))
        {
            requestedRegions = armRegionName
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(r => serviceName.Equals("Load Balancer", StringComparison.OrdinalIgnoreCase)
                    && r.Equals("Global", StringComparison.OrdinalIgnoreCase)
                        ? "Global"
                        : r.ToLowerInvariant())
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

        var vocabularyFilterCount = new[] { meterNameContains, productNameContains, skuNameContains }
            .Count(value => !string.IsNullOrWhiteSpace(value));
        var structuralFilters = filters.Take(filters.Count - vocabularyFilterCount).ToList();

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
        var vocabularyDropped = false;

        // Meter/product/SKU names are not derivable from the SKU the caller knows
        // (Standard_ND96asr_v4 meters as ND96asr_A100_v4), so a guess that matches
        // nothing must widen to the structural filter and let the facets teach the
        // real values — never hand back an empty table.
        if (vocabularyFilterCount > 0 && structuralFilters.Count > 0 && RowCount(firstPage) == 0)
        {
            var wideFilter = string.Join(" and ", structuralFilters);
            var widePage = await FetchRetailPage(
                $"https://prices.azure.com/api/retail/prices?api-version=2023-01-01-preview" +
                $"&currencyCode={Uri.EscapeDataString(currencyCode)}" +
                $"&$filter={Uri.EscapeDataString(wideFilter)}" +
                $"&$top={top}", activity);
            if (RowCount(widePage) > 0)
            {
                firstPage = widePage;
                filter = wideFilter;
                vocabularyDropped = true;
            }
        }
        activity?.SetTag("pricing.vocabulary_dropped", vocabularyDropped);

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
        if (vocabularyDropped)
            header += "NOTE: your meter/product/SKU-name filter matched 0 rows and was dropped. "
                + "These are all rows for the service/region/SKU; pick the right one using the facet values below.\n";

        // Always project + facet. Raw JSON leaves the model no vocabulary to
        // self-correct a wrong filter with, and a full page spills to a temp file.
        return header + CompactBatchResult(header + body);
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

    // OData single-quote escape: ' → ''
    private static string Esc(string s) => s.Replace("'", "''");
}
