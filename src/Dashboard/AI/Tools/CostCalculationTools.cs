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
            "Preserve decimal TB/GB versus binary TiB/GiB and the provider's billing unit. Currency must agree on every line. " +
            "Line amounts retain precision; subtotal, discount and tax are rounded to cents, and displayed components reconcile to total. This is an estimate, not measured billing.");
    }

    internal static string CalculateCost(
        [Description("JSON array of 1-20 verified line items: label, quantity, unitPrice, unit, currency; optional unitsPerRate=1 and multiplier=1. Numbers or decimal-point strings are accepted. Include only the requested period/scope; never use unknown rates as zero.")] string lineItemsJson,
        [Description("Three-letter source currency shared by every line, such as USD, EUR or CAD. No implicit currency conversion.")] string currency,
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
            return Error("Provide a bounded JSON array of 1-20 line items.");
        try
        {
            using var document = JsonDocument.Parse(lineItemsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() is < 1 or > 20)
                return Error("Provide 1-20 line items.");
            var lines = new List<object>();
            decimal rawSubtotal = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    return Error("Every line item must be an object.");
                var label = Text(item, "label");
                var unit = Text(item, "unit");
                if (string.IsNullOrWhiteSpace(label) || label.Length > 100 || string.IsNullOrWhiteSpace(unit) || unit.Length > 60
                    || !currency.Equals(Text(item, "currency"), StringComparison.OrdinalIgnoreCase)
                    || !Number(item, "quantity", out var quantity) || quantity < 0
                    || !Number(item, "unitPrice", out var unitPrice) || unitPrice < 0)
                    return Error("Every labelled line requires a billing unit, matching currency, non-negative quantity and a known non-negative rate.");
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
