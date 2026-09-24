using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

using AzureFinOps.Dashboard.Infrastructure;

namespace AzureFinOps.Dashboard.AI.Tools;

/// <summary>
/// Public Azure Retail Prices API wrapper (https://prices.azure.com — no auth required).
/// Encodes supported OData filters and returns a bounded cross-meter projection.
/// </summary>
public static class RetailPricingTools
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private sealed record RetailPage(int Status, string StatusText, string Body);

    private const int MaxProjectedRows = 200;
    private const int MaxFacetValues = 25;
    private const string QuoteInputGuidance =
        "QUOTE INPUTS: For every quote or comparison, including cross-region rankings, establish material product configuration from the user or prior context (for example, VM OS/license or database service tier/hardware). "
        + "A fixed-region quote also requires the region. "
        + "If missing, ask a concise clarification before pricing, calculation or charting unless the user explicitly authorized an assumed scenario. "
        + "Example filters are not defaults; a vCore count alone does not establish a database tier. "
        + "An explicit cross-region ranking or global rate-card comparison waives only selecting a single region, not other material product configuration. "
        + "Standard on-demand is the purchase-type default, not an OS/license or service tier default. "
        + "Comparisons of explicitly named variants already establish those variants; compare them instead of asking the user to choose one. "
        + "A storage comparison naming capacity, access tiers and region is fully specified: price Data Stored rates with LRS as the stated default redundancy instead of asking about redundancy.";
    private const string ReturnedRateGuidance =
        "QUOTE COVERAGE: RESOLUTION describes the whole filtered catalogue, not whether each returned price is usable. "
        + "Returned rates do not establish missing user quote inputs. "
        + "Match every requested model/SKU, input/output direction, deployment tier, residency, cache qualifier, unit and currency to the returned live fields. "
        + "Carry the selected product's material qualifiers (OS/license, tier and purchase type) into answer headlines and chart labels; a shared meterName or ARM SKU alone does not identify the price variant. "
        + "When all requested rates are present, answer from this result; do not refine merely because status is ambiguous or partial. "
        + "A row with variantSourceComplete=true and observedVariantPrices=1 has one retailPrice across that exact variant's fetched regions, even when catalogue details are capped. "
        + "Otherwise a valid returned rate quotes only the row's named region; do not infer a uniform price or fill a missing/invalid price. "
        + "Keep pagination, missing-region and widening caveats; quoting a row does not establish full coverage, a global cheapest price or deployment availability. "
        + "Only a missing or unresolved requested rate permits one targeted refinement using live FACETS.";

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

DATA SCOPING: filter at the Retail Prices API by the requested service, exact ARM SKU, regions and purchase type. Add only vocabulary filters supported by the user's wording or returned FACETS; do not invent meter names. The API pages up to 1000 rows and does not support $top. top limits cheapest-ranked regions per compatible variant, not downloaded rows. Ordinary lookups retain up to 200 rows spread across meters; inspect RESOLUTION and pagination. Preserve an explicit all-regions comparison and compute cheapest only after considering the full filtered candidate set. Do not widen to unrelated products merely to get a price.

STRUCTURAL FILTERS (safe to supply from what the user named):
- serviceName (REQUIRED): e.g. 'Virtual Machines', 'Storage', 'SQL Database', 'Load Balancer', 'Foundry Models' (covers ALL Azure OpenAI + open-model inference; the legacy 'Azure OpenAI' serviceName returns 0 rows).
- armRegionName: lowercase, no spaces, e.g. 'eastus'. COMMA-SEPARATE to compare many regions in ONE call. Some services are priced globally rather than per region, so a region filter can legitimately match nothing.
- armSkuName: the ARM SKU, e.g. 'Standard_D4s_v5'.
- priceType: 'Consumption' (pay-as-you-go — note this ALSO includes Spot and Low Priority rows), 'Reservation', 'DevTestConsumption'.
- rank='cheapest': paginate fully so a cheapest-across-regions answer is complete.

VOCABULARY FILTERS (meterNameContains / productNameContains / skuNameContains) — DO NOT GUESS THESE. Meter and SKU names are not derivable from the ARM SKU: 'Standard_ND96asr_v4' meters as 'ND96asr_A100_v4'. If your guess matches nothing, the tool automatically drops it, returns the structural result set instead, and tells you it did.

EVERY RESPONSE INCLUDES A `FACETS` BLOCK giving the live distinct values of each field. That is the authoritative vocabulary — use it to identify the requested rows already returned. Refine with those exact strings only if a requested rate is missing or unresolved, not simply because multiple variants are present. It reflects the API right now, so prefer it over anything you remember.

READING THE ROWS: they arrive grouped by meterName, cheapest-first within each meter. Spot, Low Priority, Windows, Reservation, cached-input and regional/zonal variants are all present and are distinguishable via meterName / skuName / type. Never treat different meters as interchangeable. A model/SKU comparison must match the requested pricing variant in each section, not select a minimum across incompatible meters.

DEFAULT INTERPRETATION: use standard on-demand purchase pricing unless other purchase variants are explicitly requested. This does not choose an OS/license or service tier; clarify missing material product configuration even for cross-region rankings. Preserve comparisons of explicitly named variants. productName can distinguish Windows from non-Windows even when meterName is identical. State the established OS/license basis. A 'cheapest region' question means cheapest on-demand region, not cheapest Spot region.

UNIT SEMANTICS: retailPrice is the price for ONE `unitOfMeasure` of the WHOLE SKU in armSkuName/skuName. Never multiply it by a core/vCore/GPU/node count that is already part of that SKU name — e.g. armSkuName 'SQLDB_GP_Compute_Gen5_4' / skuName '4 vCore' at 1 Hour is the total hourly price for all 4 vCores, not per vCore. Multiply only by quantity the user asked for (number of instances) and by hours.
VOLUME BANDS: retain tierMinimumUnits and currencyCode. A cheaper high-volume band is not the price for a small dataset. Match the requested quantity to the documented tier rules; split graduated tiers into separate CalculateCost lines instead of applying the cheapest band to every unit.

MONTHLY / VOLUME TOTALS: call EstimateTokenCost with the per-1M rates instead of doing token arithmetic in prose."
            + "\n\n" + QuoteInputGuidance + "\n\n" + ReturnedRateGuidance);

        yield return AIFunctionFactory.Create(GetAzureRetailPricingBatch, "GetAzureRetailPricingBatch",
            @"PUBLIC (no auth): Runs 2-8 independent Azure Retail Prices lookups IN PARALLEL inside ONE tool call. Use this whenever a comparison or estimate needs more than one distinct service/SKU filter. Do NOT call GetAzureRetailPricing repeatedly, and do NOT use bash/powershell/rg/grep to parse or combine pricing rows.

        DATA SCOPING: every queriesJson item needs its own narrow service/SKU/region/purchase-type filters. top limits cheapest-ranked regions per variant, not the API's page size. Do not duplicate identical filters or hide a whole-service scan inside the batch. Reuse one section for one SKU across comma-separated regions. Keep each section's FACETS, RESOLUTION and partial coverage; a batch does not make an incomplete price comparison complete.

    The tool returns a FACETS block per section with the live distinct field values. If a section's vocabulary filter matched nothing, it is dropped automatically and the wider result set is returned instead — match the requested variants against the live fields before reusing a row. Only missing or unresolved requested rates need refinement; do not fetch a pricing web page.

queriesJson is a JSON array. Each object supports: label (required for readable output), serviceName (required), armRegionName, armSkuName, priceType, meterNameContains, productNameContains, skuNameContains, currencyCode, rank, top.

Example — several VM SKUs in one model round-trip:
[{""label"":""D4s_v5"",""serviceName"":""Virtual Machines"",""armRegionName"":""eastus"",""armSkuName"":""Standard_D4s_v5"",""top"":20},{""label"":""D8s_v5"",""serviceName"":""Virtual Machines"",""armRegionName"":""eastus"",""armSkuName"":""Standard_D8s_v5"",""top"":20}]

Example — named Foundry models (go straight to this batch; NEVER run a broad GPT query first):
[{""label"":""GPT-4o"",""serviceName"":""Foundry Models"",""productNameContains"":""Azure OpenAI"",""skuNameContains"":""4o"",""priceType"":""Consumption"",""top"":50},{""label"":""GPT-4o-mini"",""serviceName"":""Foundry Models"",""productNameContains"":""Azure OpenAI"",""skuNameContains"":""4o-mini"",""priceType"":""Consumption"",""top"":50},{""label"":""GPT-4.1"",""serviceName"":""Foundry Models"",""productNameContains"":""Azure OpenAI"",""skuNameContains"":""4.1"",""priceType"":""Consumption"",""top"":50}]
Foundry sections mix deployment types and residency zones in one result set. Read the returned skuName/meterName rows and quote the variant asked for — real-time Standard Global unless stated otherwise — rather than the cheapest row. For a model input/output comparison, identify both rates for each requested model in this first batch. Do not issue another batch merely to isolate exact names already returned. The capped FACETS list is vocabulary guidance, not a list of every delivered row.

Known-good database filters (East US example, not default quote inputs):
- SQL GP Gen5 compute: serviceName='SQL Database', armSkuName='SQLDB_GP_Compute_Gen5_8', productNameContains='Single/Elastic Pool General Purpose - Compute Gen5', meterNameContains='vCore'. Choose the ordinary `vCore` row, not `Zone Redundancy vCore`.
- SQL GP storage: serviceName='SQL Database', productNameContains='Single/Elastic Pool General Purpose - Storage', skuNameContains='General Purpose', meterNameContains='Data Stored'. Choose the non-Free paid row.
- Cosmos provisioned throughput: serviceName='Azure Cosmos DB', productNameContains='Azure Cosmos DB', skuNameContains='RUs', meterNameContains='100 RU/s'.
- Cosmos storage: serviceName='Azure Cosmos DB', productNameContains='Azure Cosmos DB', skuNameContains='RUs', meterNameContains='Data Stored'.
- PostgreSQL Flexible 8-vCore: serviceName='Azure Database for PostgreSQL', armSkuName='Standard_D8ds_v5'. Storage: productNameContains='Flex Server Storage', meterNameContains='Storage Data Stored'.

For one SKU across several regions, use ONE GetAzureRetailPricing call with comma-separated armRegionName instead. For storage tiers sharing one service/region, use ONE broad GetAzureRetailPricing call (for example meterNameContains='LRS'). Never re-query a batch result in the same turn unless a requested rate remains missing or unresolved."
            + "\n\n" + QuoteInputGuidance + "\n\n" + ReturnedRateGuidance);
    }

    private static async Task<string> GetAzureRetailPricingBatch(
        [Description("JSON array of 2-8 independently filtered pricing queries. Each object: label, serviceName, and optional armRegionName, armSkuName, priceType, meterNameContains, productNameContains, skuNameContains, currencyCode, rank, top. Supply only requested service/SKU/region filters, deduplicate identical combinations, and keep enough rows for every required meter. Reuse matching returned rates; catalogue-level partial/ambiguous status alone does not require another batch. " + QuoteInputGuidance)] string queriesJson)
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
        output.AppendLine("AUTHORITATIVE RETAIL API RESULT. Reuse resolved prices directly without shell/search or a pricing-page fetch.");
        output.AppendLine(ReturnedRateGuidance);
        foreach (var result in results)
        {
            output.AppendLine().Append("=== ").Append(result.Label).AppendLine(" ===");
            output.AppendLine(result.Result);
        }
        return output.ToString();
    }

    // Cap the payload so the CLI keeps the result inline: past its limit it spills
    // to a temp file and the model spends a `view` round-trip per chunk.
    internal static string CompactBatchResult(string result, bool paginationComplete = true, bool widened = false, IReadOnlyList<string>? requestedRegions = null, int? topPerVariant = null)
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
            static string Tier(JsonElement item) =>
                item.TryGetProperty("tierMinimumUnits", out var tier) && tier.ValueKind == JsonValueKind.Number && tier.TryGetDecimal(out var minimum)
                    ? minimum.ToString(CultureInfo.InvariantCulture) : "";
            static bool HasPrice(double price) => double.IsFinite(price) && price >= 0 && price != double.MaxValue;
            var billingCurrency = doc.RootElement.TryGetProperty("BillingCurrency", out var billing) && billing.ValueKind == JsonValueKind.String
                ? billing.GetString() ?? "" : "";
            string Currency(JsonElement item) => string.IsNullOrEmpty(Str(item, "currencyCode")) ? billingCurrency : Str(item, "currencyCode");

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
                    Clean(Str(item, "type")), Clean(Str(item, "reservationTerm")), Clean(savings), Tier(item), Clean(Currency(item)));
                var variant = string.Join('\t', Str(item, "armSkuName"), Str(item, "productName"), Str(item, "skuName"), Str(item, "meterName"),
                    Str(item, "unitOfMeasure"), Str(item, "type"), Str(item, "reservationTerm"), Tier(item), Currency(item));
                return (Price: price, Meter: Clean(Str(item, "meterName")), Region: Clean(Str(item, "armRegionName")), Variant: variant, Line: line);
            })
            .GroupBy(row => row.Line, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

            // Catalogue truncation need not make a fully observed, uniform variant
            // ambiguous. Derive its coverage before the cross-meter output cap.
            var variantCoverage = rows.GroupBy(row => row.Variant, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => new
                {
                    Prices = group.Where(row => HasPrice(row.Price)).Select(row => row.Price).Distinct().Count(),
                    Regions = group.Select(row => row.Region).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    SourceComplete = paginationComplete && group.All(row => HasPrice(row.Price))
                }, StringComparer.Ordinal);

            // Round-robin across meterName groups. A flat cheapest-first cut buries
            // the ordinary on-demand meter under every Spot/Low-Priority row and the
            // model then quotes Spot as the headline price.
            var targetRows = topPerVariant is { } requestedTop
                ? rows.GroupBy(row => row.Variant, StringComparer.Ordinal)
                    .SelectMany(group => group.OrderBy(row => row.Price).ThenBy(row => row.Region, StringComparer.Ordinal)
                        .GroupBy(row => row.Region, StringComparer.OrdinalIgnoreCase).Select(region => region.First())
                        .Take(Math.Clamp(requestedTop, 1, 100))).ToList()
                : rows;
            var byMeter = targetRows
                .GroupBy(row => row.Meter, StringComparer.Ordinal)
                .Select(group => group.OrderBy(row => row.Price).ToList())
                .OrderBy(group => group[0].Price)
                .ToList();
            var selected = new List<(double Price, string Meter, string Region, string Variant, string Line)>();
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
            var complete = paginationComplete && selected.Count == targetRows.Count;
            var deliveredRegions = selected.Select(row => row.Region).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var coveredRegions = (topPerVariant is null ? selected : rows).Select(row => row.Region).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missingRegions = (requestedRegions ?? []).Where(region => !coveredRegions.Contains(region)).ToArray();
            var variants = items.EnumerateArray().Select(item => new
            {
                armSku = Str(item, "armSkuName"),
                product = Str(item, "productName"),
                sku = Str(item, "skuName"),
                meter = Str(item, "meterName"),
                unit = Str(item, "unitOfMeasure"),
                type = Str(item, "type"),
                term = Str(item, "reservationTerm"),
                tierMinimumUnits = Tier(item),
                currency = Currency(item)
            }).Distinct().Count();
            output.AppendLine("RESOLUTION " + JsonSerializer.Serialize(new
            {
                status = !complete || missingRegions.Length > 0 ? "partial" : rows.Count == 0 ? "missing" : widened || variants > 1 ? "ambiguous" : "exact",
                complete = complete && missingRegions.Length == 0,
                fetchedRows = rows.Count,
                deliveredRows = selected.Count,
                detailsComplete = selected.Count == rows.Count,
                topPerVariant,
                rankingComplete = topPerVariant is not null ? complete : (bool?)null,
                paginationComplete,
                vocabularyWidened = widened,
                variants,
                deliveredRegions,
                missingRequestedRegions = missingRegions,
                instruction = ReturnedRateGuidance
            }));
            output.Append(BuildFacets(items));
            if (variants > 1)
                output.AppendLine($"These rows span {variants} price variants. Match each requested rate to its exact SKU, meter, product, tier, unit and purchase type; different variants are not interchangeable.");
            output.AppendLine(topPerVariant is { } topCount
                ? $"{rows.Count} distinct candidates; showing up to {topCount} cheapest regions per matching price variant. Ranking is global only when paginationComplete and rankingComplete are true."
                : rows.Count <= selected.Count
                ? $"{rows.Count} distinct row(s), cheapest first within each meter."
                : $"{rows.Count} distinct rows; showing {selected.Count} spread across meters, cheapest first within each. Reuse matching requested rates; narrow only missing or unresolved rates using live facets.");
            output.AppendLine("Variant coverage uses all fetched rows before projection: observedVariantPrices counts retailPrice values, not savings-plan rates. observedVariantRegions counts fetched regions. variantSourceComplete=false means missing pages or invalid prices; never infer uniform pricing from it.");
            output.AppendLine("retailPrice\tarmRegionName\tarmSkuName\tproductName\tskuName\tmeterName\tunitOfMeasure\ttype\treservationTerm\tsavingsPlan(term:price)\ttierMinimumUnits\tcurrencyCode\tobservedVariantPrices\tobservedVariantRegions\tvariantSourceComplete");
            foreach (var row in selected.OrderBy(r => r.Meter, StringComparer.Ordinal).ThenBy(r => r.Price))
            {
                var coverage = variantCoverage[row.Variant];
                output.Append(row.Line).Append('\t').Append(coverage.Prices).Append('\t').Append(coverage.Regions)
                    .Append('\t').AppendLine(coverage.SourceComplete ? "true" : "false");
            }
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
                if (property.Name is not ("serviceName" or "armRegionName" or "armSkuName" or "productName" or "skuName"
                    or "meterName" or "unitOfMeasure" or "type" or "reservationTerm" or "currencyCode")
                    || property.Value.ValueKind != JsonValueKind.String) continue;
                var text = property.Value.GetString();
                if (string.IsNullOrEmpty(text) || text.Length > 120) continue;
                if (!values.TryGetValue(property.Name, out var set))
                    values[property.Name] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add(text);
            }
        }

        var output = new StringBuilder("FACETS (live distinct values — filter with these exact strings):\n");
        foreach (var (field, set) in values.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var truncated = set.Count > MaxFacetValues;
            output.Append("  ").Append(field).Append(" (");
            output.Append(truncated ? $"{set.Count}, showing {MaxFacetValues}" : set.Count.ToString(CultureInfo.InvariantCulture));
            output.Append("): ");
            output.AppendLine(string.Join(" | ", set.Take(MaxFacetValues)));
        }
        return output.ToString();
    }

    private static async Task<string> GetAzureRetailPricing(
        [Description("Required service filter, e.g. 'Virtual Machines', 'Storage', 'SQL Database', 'Foundry Models'. Combine with the requested SKU, region and purchase-type filters instead of retrieving an entire service catalogue.")] string serviceName,
        [Description("ARM region (lowercase, no spaces), e.g. 'eastus', 'westeurope'. Pass a COMMA-SEPARATED LIST to compare regions in ONE call, e.g. 'eastus,westeurope,swedencentral' — always do this instead of calling the tool once per region. Empty = all regions. For a fixed-region quote, clarify a missing region rather than choosing an example region.")] string? armRegionName = null,
        [Description("Exact ARM SKU filter, e.g. 'Standard_D4s_v5'. Supply the requested SKU when known; omit only for a requested cross-SKU comparison or a service without ARM SKU identifiers.")] string? armSkuName = null,
        [Description("Price type: 'Consumption' (PAYG), 'Reservation' (1y/3y RI), 'DevTestConsumption'. Empty = all.")] string? priceType = null,
        [Description("Substring match on meterName, e.g. 'Spot' or 'LRS'. Empty = no meter filter.")] string? meterNameContains = null,
        [Description("Substring match on productName, e.g. 'GPT' / 'Llama' / 'Phi' for Foundry Models, or 'Premium SSD' for storage. Foundry productName is a family bucket — use 'GPT' not 'gpt-4'. Empty = no product filter.")] string? productNameContains = null,
        [Description("Substring match on skuName, e.g. '8 vCore', 'RUs', 'GPT-4o Inp Gl'. Use with productNameContains when the product is a broad family. Empty = no SKU-name filter.")] string? skuNameContains = null,
        [Description("Currency code (default 'USD'). Supported: USD, EUR, GBP, JPY, NOK, etc.")] string? currencyCode = null,
        [Description("Set to 'cheapest' to rank all fetched candidates before retaining top regions per compatible price variant. A global ranking requires complete pagination. Empty = a bounded cross-meter projection.")] string? rank = null,
        [Description("With rank='cheapest', maximum regions per compatible price variant (default 50, max 100). This is an output limit, not a source download limit; the API does not support $top. Ordinary lookups retain up to 200 rows across meters.")] int top = 50)
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
        if (!string.IsNullOrWhiteSpace(armSkuName)) filters.Add(SkuFilter(armSkuName, false));
        if (!string.IsNullOrWhiteSpace(priceType)) filters.Add($"priceType eq '{Esc(priceType.Trim())}'");
        if (!string.IsNullOrWhiteSpace(meterNameContains)) filters.Add($"contains(meterName, '{Esc(meterNameContains.Trim())}')");
        if (!string.IsNullOrWhiteSpace(productNameContains)) filters.Add($"contains(productName, '{Esc(productNameContains.Trim())}')");
        if (!string.IsNullOrWhiteSpace(skuNameContains)) filters.Add($"contains(skuName, '{Esc(skuNameContains.Trim())}')");

        var vocabularyFilterCount = new[] { meterNameContains, productNameContains, skuNameContains }
            .Count(value => !string.IsNullOrWhiteSpace(value));
        var structuralFilters = filters.Take(filters.Count - vocabularyFilterCount).ToList();

        var filter = string.Join(" and ", filters);
        var url = BuildRetailUrl(filter, currencyCode);

        using var activity = HttpHelper.Telemetry.StartActivity("GetAzureRetailPricing");
        activity?.SetTag("pricing.service", serviceName);
        activity?.SetTag("pricing.region", armRegionName ?? "any");
        activity?.SetTag("pricing.sku", armSkuName ?? "any");
        activity?.SetTag("pricing.top", top);

        var firstPage = await FetchRetailPage(url, activity);
        var vocabularyDropped = false;
        var skuFieldResolved = false;
        if (RowCount(firstPage) == 0 && !string.IsNullOrWhiteSpace(armSkuName))
        {
            var alternate = filters.Select(value => value == SkuFilter(armSkuName, false) ? SkuFilter(armSkuName, true) : value).ToList();
            var alternateFilter = string.Join(" and ", alternate);
            var alternatePage = await FetchRetailPage(BuildRetailUrl(alternateFilter, currencyCode), activity);
            if (RowCount(alternatePage) > 0)
            {
                firstPage = alternatePage;
                filters = alternate;
                structuralFilters = filters.Take(filters.Count - vocabularyFilterCount).ToList();
                filter = alternateFilter;
                skuFieldResolved = true;
            }
        }

        // Meter/product/SKU names are not derivable from the SKU the caller knows
        // (Standard_ND96asr_v4 meters as ND96asr_A100_v4), so a guess that matches
        // nothing must widen to the structural filter and let the facets teach the
        // real values — never hand back an empty table.
        if (vocabularyFilterCount > 0 && structuralFilters.Count > 0 && RowCount(firstPage) == 0)
        {
            var wideFilter = string.Join(" and ", structuralFilters);
            var widePage = await FetchRetailPage(BuildRetailUrl(wideFilter, currencyCode), activity);
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
        if (!paginate)
        {
            try
            {
                using var firstDocument = JsonDocument.Parse(body);
                paginationComplete = !firstDocument.RootElement.TryGetProperty("NextPageLink", out var next)
                    || next.ValueKind == JsonValueKind.Null || string.IsNullOrWhiteSpace(next.GetString());
            }
            catch (JsonException) { paginationComplete = false; }
        }

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
                    {
                        paginationComplete = false;
                        break;
                    }
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

                if (allItems.Count > maxItems || (allItems.Count == maxItems && !string.IsNullOrWhiteSpace(nextLink)))
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

        var header = $"HTTP {firstPage.Status} {firstPage.StatusText}\nQuery: {filter} (currency={currencyCode})\nRetrieved UTC: {DateTimeOffset.UtcNow:o}\nPages: {pageCount}; paginationComplete={paginationComplete}; fetchedRows={bodyRowCount}\n";
        if (requestedRegions.Count > 0)
            header += missingRegions.Length == 0
                ? $"Fetched region coverage: {requestedRegions.Count}/{requestedRegions.Count}; see RESOLUTION for delivered coverage.\n"
                : $"Fetched region coverage incomplete: missing {string.Join(", ", missingRegions)}.\n";
        if (skuFieldResolved) header += "SKU matched the live skuName field rather than armSkuName; the exact supplied value was retained.\n";
        if (vocabularyDropped)
            header += "NOTE: your meter/product/SKU-name filter matched 0 rows and was dropped. "
                + "These are all rows for the service/region/SKU; pick the right one using the facet values below.\n";

        // Always project + facet. Raw JSON leaves the model no vocabulary to
        // self-correct a wrong filter with, and a full page spills to a temp file.
        return header + CompactBatchResult(header + body, paginationComplete, vocabularyDropped, requestedRegions,
            string.Equals(rank?.Trim(), "cheapest", StringComparison.OrdinalIgnoreCase) ? top : null);
    }

    private static async Task<RetailPage> FetchRetailPage(string url, System.Diagnostics.Activity? activity)
    {
        var cancellationToken = ToolExecutionContext.Current?.CancellationToken ?? CancellationToken.None;
        const int maxAttempts = 4;
        HttpResponseMessage response = null!;
        string body = "";
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "FinOps-Dashboard/1.0");
            response = await Http.SendAsync(request, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);

            if ((int)response.StatusCode != 429 && (int)response.StatusCode < 500) break;
            if (attempt == maxAttempts - 1) break;

            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds
                          ?? Math.Min(Math.Pow(2, attempt + 1) + Random.Shared.NextDouble(), 30);
            var waitSeconds = Math.Max(1, retryAfter);
            activity?.SetTag($"pricing.retry_{attempt}", $"{(int)response.StatusCode}, waiting {waitSeconds:F0}s");
            var turnKey = System.Diagnostics.Activity.Current?.GetBaggageItem("finops.turn.id");
            if (turnKey is not null && HttpHelper.RetryReporters.TryGetValue(turnKey, out var report))
            {
                try { await report(new(attempt + 1, waitSeconds, url, "pricing", (int)response.StatusCode)); }
                catch (Exception emitEx)
                {
                    HttpHelper.Logger?.LogWarning(emitEx,
                        "SSE cooling_down emit failed for pricing attempt={Attempt}", attempt + 1);
                }
            }
            response.Dispose();
            await Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken);
        }

        using (response) return new RetailPage((int)response.StatusCode, response.StatusCode.ToString(), body);
    }

    internal static string SkuFilter(string value, bool displayName) => $"{(displayName ? "skuName" : "armSkuName")} eq '{Esc(value.Trim())}'";

    internal static string BuildRetailUrl(string filter, string currency) =>
        $"https://prices.azure.com/api/retail/prices?api-version=2023-01-01-preview&currencyCode={Uri.EscapeDataString(currency)}&$filter={Uri.EscapeDataString(filter)}";

    // OData single-quote escape: ' → ''
    private static string Esc(string s) => s.Replace("'", "''");
}
