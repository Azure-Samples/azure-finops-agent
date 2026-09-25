using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using AzureFinOps.Dashboard.Infrastructure;
using Microsoft.Extensions.AI;

namespace AzureFinOps.Dashboard.AI.Tools;

public static class CostCalculationTools
{
    public static IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(CalculateCost, "CalculateCost",
            "Deterministic non-token cost arithmetic for storage, backup, VM estimates and spend run-rates. " +
            "Use after obtaining the exact rates and assumptions; quote the returned total, tax and annualization instead of recomputing in prose. " +
            "Each line is quantity / unitsPerRate * unitPrice * multiplier. Only supplied numbers are calculated; no source, price tier, capacity, retention, FX or discount is invented. " +
            "For run-rate extrapolation use observed spend as quantity, unitPrice=1, unitsPerRate=elapsed days and multiplier=days in the requested period. " +
            "For growth, calculate each requested period with its own explicit quantities; an exit-month annualization is not cumulative annual spend. " +
            "Preserve decimal TB/GB versus binary TiB/GiB and the provider's billing unit. Lines inherit the explicitly supplied currency; any explicit line currency must match it. " +
            "Line amounts retain precision; subtotal, discount and tax are rounded to cents, and displayed components reconcile to total. This is an estimate, not measured billing.");
        yield return AIFunctionFactory.Create(CompareAmounts, "CompareAmounts",
            "Deterministic comparison arithmetic for verified amounts in one unit: difference, percent change versus a baseline, and each item's share of the total. " +
            "Use for month-over-month change, savings percentages, coverage/utilization shares and ranked percentages instead of computing them in prose; quote the returned values exactly. " +
            "Copy complete source amounts into valid JSON, preferably as decimal-point strings to retain precision. Never send ellipses, question marks, truncated display values or unfinished expressions. Retrieve an unavailable exact amount with QueryToolResult before comparing it; do not guess or repair missing digits. " +
            "The shared unit must be a short label of at most 40 characters, such as USD. Put dates, cost type and assumptions in the answer, not in unit. " +
            "A zero or missing baseline yields a null percent change, never infinity or 100%. Values are not converted between currencies or units, and the result does not establish source validity or completeness.");
    }

    internal static string CompareAmounts(
        [Description("Complete valid JSON array of 1-50 items: label (1-100 characters), current, optional baseline. Numbers or exact decimal-point strings. Example: [{\"label\":\"Sub A\",\"current\":\"2825.36\",\"baseline\":\"74.23\"}]. No ellipses, question marks, comments or unfinished expressions. Use only verified complete values from one unit, period basis and currency.")] string itemsJson,
        [Description("Shared currency or unit label, 1-40 characters, such as USD, EUR, hours or vCPU. Do not include dates, cost type or comparison assumptions; state those in the answer.")] string unit)
    {
        if (string.IsNullOrWhiteSpace(unit) || unit.Length > 40) return "Error: Provide one shared unit of at most 40 characters.";
        if (string.IsNullOrWhiteSpace(itemsJson) || itemsJson.Length > 30_000) return "Error: Provide a bounded JSON array of 1-50 items.";
        try
        {
            using var document = JsonDocument.Parse(itemsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() is < 1 or > 50)
                return "Error: Provide 1-50 items.";
            var items = new List<(string Label, decimal Current, decimal? Baseline)>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var label = item.ValueKind == JsonValueKind.Object ? Text(item, "label") : null;
                if (string.IsNullOrWhiteSpace(label) || label.Length > 100 || !Number(item, "current", out var current))
                    return "Error: Every item requires a label and a numeric current value.";
                decimal? baseline = null;
                if (item.TryGetProperty("baseline", out var baselineValue) && baselineValue.ValueKind != JsonValueKind.Null)
                {
                    if (!Number(item, "baseline", out var parsed)) return "Error: baseline must be numeric when supplied.";
                    baseline = parsed;
                }
                items.Add((label, current, baseline));
            }
            var total = items.Sum(item => item.Current);
            var baselineTotal = items.All(item => item.Baseline is not null) ? items.Sum(item => item.Baseline!.Value) : (decimal?)null;
            decimal? Percent(decimal current, decimal? baseline) =>
                baseline is null or 0 ? null : Math.Round((current - baseline.Value) / Math.Abs(baseline.Value) * 100m, 2, MidpointRounding.AwayFromZero);
            return JsonSerializer.Serialize(new
            {
                unit,
                total,
                baselineTotal,
                totalDifference = baselineTotal is null ? (decimal?)null : total - baselineTotal.Value,
                totalPercentChange = Percent(total, baselineTotal),
                items = items.Select(item => new
                {
                    label = item.Label,
                    current = item.Current,
                    baseline = item.Baseline,
                    difference = item.Baseline is null ? (decimal?)null : item.Current - item.Baseline.Value,
                    percentChange = Percent(item.Current, item.Baseline),
                    sharePercent = total == 0 ? (decimal?)null : Math.Round(item.Current / total * 100m, 2, MidpointRounding.AwayFromZero)
                }),
                instructions = "Quote these exact values. percentChange is null when the baseline is zero or missing; say the change is not meaningful rather than inventing a percentage."
            });
        }
        catch (JsonException) { return "Error: Items must be valid JSON with decimal-point numeric values."; }
        catch (OverflowException) { return "Error: The requested arithmetic exceeds the supported decimal range."; }
    }

    internal static string CalculateCost(
        [Description("JSON array of 1-50 verified line items: label, quantity, unitPrice, unit; optional currency inherits the explicit top-level currency, unitsPerRate=1 and multiplier=1. Example: [{\"label\":\"Units\",\"quantity\":5,\"unitPrice\":5,\"unit\":\"unit\"}]. Numbers or decimal-point strings are accepted. Include only the requested period/scope; never use unknown rates as zero.")] string lineItemsJson,
        [Description("Required three-letter source currency shared by every line, such as USD, EUR or CAD. Lines may omit currency to inherit this value; an explicit different currency is rejected. No implicit currency conversion.")] string currency,
        [Description("Basis of the quantities, such as month, year1 or 30-day run-rate. Use month only for a fixed monthly estimate; its annualizedTotal is not a growth projection.")] string period,
        [Description("Explicit supported discount percentage, 0-100. Default 0; do not invent negotiated discounts.")] string discountPercent = "0",
        [Description("Explicit tax percentage applied after discount, 0-100. Default 0; do not infer taxes from currency.")] string taxPercent = "0")
    {
        using var activity = HttpHelper.Telemetry.StartActivity("CalculateCost");
        string Error(string message)
        {
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, "Invalid cost calculation");
            return "Error: " + message;
        }
        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3 || !currency.All(char.IsAsciiLetter) || string.IsNullOrWhiteSpace(period) || period.Length > 60
            || !TryDecimal(discountPercent, out var discountRate) || discountRate is < 0 or > 100
            || !TryDecimal(taxPercent, out var taxRate) || taxRate is < 0 or > 100)
            return Error("Provide one source currency, an explicit period and decimal-point discount/tax percentages between 0 and 100.");
        currency = currency.ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(lineItemsJson) || lineItemsJson.Length > 30_000)
            return Error("Provide a bounded JSON array of 1-50 line items.");
        try
        {
            using var document = JsonDocument.Parse(lineItemsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() is < 1 or > 50)
                return Error("Provide 1-50 line items. To total more source rows, use QueryToolResult sum aggregates.");
            var lines = new List<object>();
            decimal rawSubtotal = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    return Error("Every line item must be an object.");
                var label = Text(item, "label");
                var unit = Text(item, "unit");
                if (string.IsNullOrWhiteSpace(label) || label.Length > 100 || string.IsNullOrWhiteSpace(unit) || unit.Length > 60
                    || !Number(item, "quantity", out var quantity) || quantity < 0
                    || !Number(item, "unitPrice", out var unitPrice) || unitPrice < 0)
                    return Error("Every line requires label, unit, non-negative quantity and a known non-negative unitPrice.");
                if (item.TryGetProperty("currency", out var lineCurrency)
                    && (lineCurrency.ValueKind != JsonValueKind.String
                        || !currency.Equals(lineCurrency.GetString(), StringComparison.OrdinalIgnoreCase)))
                    return Error("An explicit line currency must match the top-level currency. Omit line currency to inherit that declared value; no conversion is performed.");
                decimal unitsPerRate = 1, multiplier = 1;
                if (item.TryGetProperty("unitsPerRate", out _) && (!Number(item, "unitsPerRate", out unitsPerRate) || unitsPerRate <= 0)
                    || item.TryGetProperty("multiplier", out _) && (!Number(item, "multiplier", out multiplier) || multiplier < 0))
                    return Error("unitsPerRate must be positive and multiplier non-negative.");
                var amount = quantity / unitsPerRate * unitPrice * multiplier;
                rawSubtotal += amount;
                lines.Add(new { label, quantity, unitPrice, unit, currency, unitsPerRate, multiplier, amount });
            }
            var subtotal = Round(rawSubtotal);
            var discountAmount = Round(subtotal * discountRate / 100m);
            var afterDiscount = subtotal - discountAmount;
            var taxAmount = Round(afterDiscount * taxRate / 100m);
            var total = afterDiscount + taxAmount;
            return JsonSerializer.Serialize(new
            {
                status = "hypothetical_estimate",
                isMeasuredBill = false,
                currency,
                period,
                lines,
                rawSubtotal,
                subtotal,
                discountPercent = discountRate,
                discountAmount,
                taxPercent = taxRate,
                taxAmount,
                total,
                annualizedTotal = period.Equals("month", StringComparison.OrdinalIgnoreCase) ? Round(total * 12m) : (decimal?)null,
                instructions = "Reuse these exact inputs and totals in headlines, tables and charts. Recalculate when scope, quantity, growth, days, unit, tax or discount changes. Source validity and service feasibility are not established by this arithmetic."
            });
        }
        catch (JsonException) { return Error("Line items must be valid JSON with decimal-point numeric values."); }
        catch (OverflowException) { return Error("The requested arithmetic exceeds the supported decimal range."); }
    }

    private static string? Text(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Number(JsonElement item, string property, out decimal number)
    {
        number = 0;
        if (!item.TryGetProperty(property, out var value)) return false;
        return value.ValueKind == JsonValueKind.Number ? value.TryGetDecimal(out number)
            : value.ValueKind == JsonValueKind.String && TryDecimal(value.GetString(), out number);
    }

    private static bool TryDecimal(string? text, out decimal number) =>
        decimal.TryParse(text?.Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out number);

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
