using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

using AzureFinOps.Dashboard.Infrastructure;

namespace AzureFinOps.Dashboard.AI.Tools;

/// <summary>
/// Thin pass-through to the public Azure Retail Prices API. The model authors the OData filter;
/// the host pins the origin, follows pagination and retries throttling.
/// </summary>
public static class RetailPricingTools
{
    private const string ApiBase = "https://prices.azure.com/api/retail/prices";
    private const int MaxAttempts = 4;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly record struct RetailPage(int Status, string Reason, string Body)
    {
        public bool Ok => Status is >= 200 and < 300;
    }

    public static IEnumerable<AIFunction> Create() =>
    [
        AIFunctionFactory.Create(GetAzureRetailPricing, nameof(GetAzureRetailPricing), """
            Public Azure Retail Prices API (no auth): pay-as-you-go, Spot, reservation and savings-plan list prices for every Azure service.
            You write the OData $filter. Fields and operators (eq, and, or, contains(field,'x')) are documented at
            https://learn.microsoft.com/rest/api/cost-management/retail-prices/azure-retail-prices; read it when unsure.
            Values are case-sensitive; armRegionName is lowercase (eastus); Azure OpenAI and other Foundry models use serviceName 'Foundry Models'.
            Filter as narrowly as the question allows, compare regions or SKUs with 'or' in one call, and run independent lookups in parallel.
            Meter, product and SKU names are not derivable from ARM SKU names: when unsure, start with structural fields
            (serviceName, armRegionName, armSkuName, priceType) and read the returned values instead of guessing.
            Zero Items means the filter matched nothing, not a zero price. Consumption also contains Spot and Low Priority meters;
            tierMinimumUnits marks volume bands. Large results are retained for QueryToolResult.
            Quote each rate with its productName, meterName, unitOfMeasure, currency and retrievedAtUtc.
            """),
    ];

    private static async Task<string> GetAzureRetailPricing(
        [Description("OData $filter, e.g. \"serviceName eq 'Virtual Machines' and armSkuName eq 'Standard_D4s_v5' and (armRegionName eq 'eastus' or armRegionName eq 'westeurope') and priceType eq 'Consumption'\".")] string filter,
        [Description("ISO currency code. Default USD.")] string currencyCode = "USD",
        [Description("Maximum 1,000-row pages to follow, 1-10. Default 5.")] string maxPages = "5")
    {
        if (string.IsNullOrWhiteSpace(filter))
            return "Error: filter is required; the unfiltered catalogue has millions of rows.";
        var currency = currencyCode is { Length: > 0 } ? currencyCode.Trim().ToUpperInvariant() : "USD";
        if (currency is not { Length: 3 } || !currency.All(char.IsAsciiLetter))
            return "Error: currencyCode must be a three-letter ISO code.";
        var pageLimit = int.TryParse(maxPages, out var parsed) ? Math.Clamp(parsed, 1, 10) : 5;
        filter = filter.Trim();

        using var activity = HttpHelper.Telemetry.StartActivity(nameof(GetAzureRetailPricing));
        JsonArray items = [];
        var url = BuildRetailUrl(filter, currency);
        var pages = 0;
        for (; url is not null && pages < pageLimit; pages++)
        {
            var page = await FetchRetailPage(url, activity);
            if (!page.Ok)
                return $"Error: Retail Prices API returned HTTP {page.Status} {page.Reason}. {page.Body[..Math.Min(page.Body.Length, 800)]}";
            var root = JsonNode.Parse(page.Body);
            if (root?["Items"] is JsonArray pageItems)
                foreach (var item in pageItems.ToArray())
                    items.Add(item?.DeepClone());
            url = root?["NextPageLink"]?.GetValue<string>() is { } next
                && next.StartsWith(ApiBase, StringComparison.OrdinalIgnoreCase) ? next : null;
        }
        activity?.SetTag("pricing.pages", pages);
        activity?.SetTag("pricing.rows", items.Count);

        return new JsonObject
        {
            ["source"] = "Azure Retail Prices API",
            ["retrievedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["filter"] = filter,
            ["currencyCode"] = currency,
            ["pages"] = pages,
            ["complete"] = url is null,
            ["count"] = items.Count,
            ["Items"] = items,
        }.ToJsonString();
    }

    private static async Task<RetailPage> FetchRetailPage(string url, Activity? activity)
    {
        var cancellationToken = ToolExecutionContext.Current?.CancellationToken ?? CancellationToken.None;
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("FinOps-Dashboard/1.0");
            using var response = await Http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var status = (int)response.StatusCode;
            if (status is not (429 or >= 500) || attempt == MaxAttempts)
                return new(status, response.ReasonPhrase ?? response.StatusCode.ToString(), body);

            var waitSeconds = Math.Max(1, response.Headers.RetryAfter?.Delta?.TotalSeconds
                ?? Math.Min(Math.Pow(2, attempt) + Random.Shared.NextDouble(), 30));
            activity?.SetTag($"pricing.retry_{attempt}", $"{status}, waiting {waitSeconds:F0}s");
            if (Activity.Current?.GetBaggageItem("finops.turn.id") is { } turnKey
                && HttpHelper.RetryReporters.TryGetValue(turnKey, out var report))
            {
                try { await report(new(attempt, waitSeconds, url, "pricing", status)); }
                catch (Exception ex) { HttpHelper.Logger?.LogWarning(ex, "SSE cooling_down emit failed for pricing attempt={Attempt}", attempt); }
            }
            await Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken);
        }
    }

    internal static string BuildRetailUrl(string filter, string currency) =>
        $"{ApiBase}?api-version=2023-01-01-preview&currencyCode={Uri.EscapeDataString(currency)}&$filter={Uri.EscapeDataString(filter)}";
}