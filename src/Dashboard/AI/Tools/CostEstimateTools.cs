using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;

using AzureFinOps.Dashboard.Infrastructure;

namespace AzureFinOps.Dashboard.AI.Tools;

/// <summary>
/// Deterministic token-cost calculator. The LLM is unreliable at multi-row, multi-step
/// arithmetic — left to compute monthly costs in prose it routinely produces a summary
/// table that disagrees with its own step-by-step (and silently blends two different
/// token assumptions). This tool does the math in C# so every figure reconciles, and
/// returns a ready-made per-model breakdown the model must echo verbatim.
/// </summary>
public static class CostEstimateTools
{
    public static IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(EstimateTokenCost, "EstimateTokenCost",
            """
            Deterministic token-cost calculator for monthly or volume LLM estimates and model comparisons; use it instead of computing token costs yourself.
            Pass per-1M-token rates from GetAzureRetailPricing for only the requested models (with their deployment tier, region and currency) and one shared set of token assumptions, then report the returned figures verbatim as an estimate, not measured billing.
            Label each model with the pricing basis it was priced on, for example "gpt-4o, Global Standard".
            """);
    }

    internal static string EstimateTokenCost(
        [Description(@"JSON array of only the requested 1-20 models/variants, not an unfiltered price catalogue. Include label, per-1M rates and known provenance. Schema: [{""label"":""a nano model, Global Standard"",""inputPricePer1M"":0.20,""outputPricePer1M"":1.25,""cachedInputPricePer1M"":0.02}]. cachedInputPricePer1M is optional when not modeling cache hits. Rates come from filtered GetAzureRetailPricing results.")] string modelsJson,
        [Description("Average input (prompt) tokens per conversation/request. Applies to ALL models. e.g. '1500'.")] string inputTokensPerConversation,
        [Description("Average output (completion) tokens per conversation/request. Applies to ALL models. e.g. '500'.")] string outputTokensPerConversation,
        [Description("Number of conversations/requests per month. Applies to ALL models. e.g. '8000'.")] string conversationsPerMonth,
        [Description("Optional: average CACHED input tokens per conversation, billed at cachedInputPricePer1M. Default '0' (no caching). These are billed at the cached rate IN ADDITION TO inputTokensPerConversation at the full rate — do not double-count: set inputTokensPerConversation to the non-cached portion if you split them.")] string? cachedInputTokensPerConversation = null,
        [Description("Currency label for display only (default 'USD'). Pass the same currency you fetched the rates in.")] string? currency = null)
    {
        if (string.IsNullOrWhiteSpace(modelsJson))
            return "Error: modelsJson is required — a JSON array of {label, inputPricePer1M, outputPricePer1M, cachedInputPricePer1M?}.";

        if (!TryNum(inputTokensPerConversation, out var inPerConvo) || inPerConvo < 0)
            return $"Error: inputTokensPerConversation '{inputTokensPerConversation}' is not a valid non-negative number.";
        if (!TryNum(outputTokensPerConversation, out var outPerConvo) || outPerConvo < 0)
            return $"Error: outputTokensPerConversation '{outputTokensPerConversation}' is not a valid non-negative number.";
        if (!TryNum(conversationsPerMonth, out var convos) || convos < 0)
            return $"Error: conversationsPerMonth '{conversationsPerMonth}' is not a valid non-negative number.";
        var cachedPerConvo = 0d;
        if (!string.IsNullOrWhiteSpace(cachedInputTokensPerConversation)
            && (!TryNum(cachedInputTokensPerConversation, out cachedPerConvo) || cachedPerConvo < 0))
            return "Error: cachedInputTokensPerConversation must be a finite non-negative number using a decimal point.";

        var cur = string.IsNullOrWhiteSpace(currency) ? "USD" : currency.Trim().ToUpperInvariant();

        JsonElement modelsRoot;
        try { modelsRoot = JsonDocument.Parse(modelsJson).RootElement; }
        catch (JsonException jex) { return $"Error: modelsJson is not valid JSON — {jex.Message}"; }
        if (modelsRoot.ValueKind != JsonValueKind.Array)
            return "Error: modelsJson must be a JSON array of model objects.";
        if (modelsRoot.GetArrayLength() is < 1 or > 20)
            return "Error: provide between 1 and 20 models.";

        using var activity = HttpHelper.Telemetry.StartActivity("EstimateTokenCost");

        var monthlyInputTokens = inPerConvo * convos;
        var monthlyOutputTokens = outPerConvo * convos;
        var monthlyCachedTokens = cachedPerConvo * convos;
        if (!double.IsFinite(monthlyInputTokens) || !double.IsFinite(monthlyOutputTokens) || !double.IsFinite(monthlyCachedTokens))
            return "Error: token volume is too large.";

        var models = new List<object>();
        foreach (var m in modelsRoot.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object) return "Error: every model must be an object.";

            var label = GetString(m, "label");
            var inputRate = GetNullableDouble(m, "inputPricePer1M");
            var outputRate = GetNullableDouble(m, "outputPricePer1M");
            var cachedRate = GetNullableDouble(m, "cachedInputPricePer1M");
            if (string.IsNullOrWhiteSpace(label) || inputRate is null or < 0 || outputRate is null or < 0
                || cachedRate is < 0 || cachedPerConvo > 0 && cachedRate is null)
                return "Error: each labelled model requires finite non-negative input/output rates and a cached rate when cache tokens are used. Missing rates are unknown, not zero.";
            if (m.TryGetProperty("cachedInputPricePer1M", out var cacheValue) && cacheValue.ValueKind != JsonValueKind.Null && cachedRate is null)
                return "Error: cached input rate is invalid.";
            var inRate = inputRate.Value;
            var outRate = outputRate.Value;
            var rateCurrency = GetString(m, "currency");
            if (rateCurrency is not null && !rateCurrency.Equals(cur, StringComparison.OrdinalIgnoreCase))
                return "Error: model rates use different currencies; convert with an explicit exchange-rate source first.";

            // Round each component to cents, then derive the total from the ROUNDED
            // components so the printed parts always sum exactly to the printed total.
            var inputCost = Round2(monthlyInputTokens / 1_000_000d * inRate);
            var outputCost = Round2(monthlyOutputTokens / 1_000_000d * outRate);
            var cachedCost = cachedRate is double cr ? Round2(monthlyCachedTokens / 1_000_000d * cr) : 0d;
            var total = Round2(inputCost + outputCost + cachedCost);
            if (!double.IsFinite(total)) return "Error: calculated cost exceeds the supported numeric range.";
            var perConvo = convos > 0 ? total / convos : 0d;

            var breakdown =
                $"input {Fmt(monthlyInputTokens)} tok / 1M × {Money(inRate, cur)} = {Money(inputCost, cur)}; " +
                $"output {Fmt(monthlyOutputTokens)} tok / 1M × {Money(outRate, cur)} = {Money(outputCost, cur)}" +
                (cachedRate is double crr
                    ? $"; cached {Fmt(monthlyCachedTokens)} tok / 1M × {Money(crr, cur)} = {Money(cachedCost, cur)}"
                    : "") +
                $"; total = {Money(total, cur)}/mo";

            models.Add(new
            {
                label,
                evidence = new
                {
                    source = GetString(m, "source") ?? "user_or_model_supplied_rate_unverified",
                    rateId = GetString(m, "rateId"),
                    region = GetString(m, "region"),
                    deploymentTier = GetString(m, "deploymentTier"),
                    dataAsOfUtc = GetString(m, "dataAsOfUtc"),
                    rateUnit = "currency per 1 million tokens",
                    currency = cur,
                    confidence = GetString(m, "source") is null ? "unverified_rate" : "source_claimed_not_independently_verified"
                },
                inputPricePer1M = inRate,
                outputPricePer1M = outRate,
                cachedInputPricePer1M = cachedRate,
                inputCost,
                outputCost,
                cachedCost,
                totalMonthlyCost = total,
                perConversationCost = Math.Round(perConvo, 6),
                breakdown
            });
        }

        if (models.Count == 0)
            return "Error: no valid model objects found in modelsJson. Each must be an object with inputPricePer1M and outputPricePer1M.";

        var result = new
        {
            estimateId = Guid.NewGuid().ToString("N"),
            status = "hypothetical_estimate",
            isMeasuredBill = false,
            assumptions = new
            {
                inputTokensPerConversation = inPerConvo,
                outputTokensPerConversation = outPerConvo,
                cachedInputTokensPerConversation = cachedPerConvo,
                conversationsPerMonth = convos,
                monthlyInputTokens,
                monthlyOutputTokens,
                monthlyCachedTokens,
                currency = cur,
                note = "Every model below uses THESE exact assumptions. State them once in your reply."
            },
            models,
            instructions =
                "Arithmetic is reconciled, but rate/source validity and billing freshness are not established by this calculator. " +
                "Do not replace this estimate with an extrapolated portal value unless scope, dates, deployment, token categories and cache treatment reconcile. " +
                "In your reply, the headline, the summary table, and the step-by-step MUST all equal these numbers — " +
                "use the per-model 'breakdown' string for the step-by-step. Do NOT recompute, do NOT blend a different " +
                "token assumption, and always state the pricing basis (e.g. Global Standard) for each model.",
            utc = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture)
        };

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    internal static bool TryNum(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text) || text.Contains(',') || text.Contains('_')) return false;
        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    private static string? GetString(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? GetNullableDouble(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d)) return d;
        if (v.ValueKind == JsonValueKind.String && TryNum(v.GetString(), out var sd)) return sd;
        return null;
    }

    private static double Round2(double v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    private static string Fmt(double tokens) => tokens.ToString("#,##0", CultureInfo.InvariantCulture);

    private static string Money(double v, string currency)
    {
        var symbol = currency == "USD" ? "$" : currency == "EUR" ? "€" : currency == "GBP" ? "£" : "";
        // Show more precision for sub-cent per-1M rates (e.g. $0.02), 2 decimals for costs.
        var formatted = v < 1 && v != 0 && v == Math.Round(v, 4)
            ? v.ToString("0.####", CultureInfo.InvariantCulture)
            : v.ToString("0.00", CultureInfo.InvariantCulture);
        return symbol.Length > 0 ? $"{symbol}{formatted}" : $"{formatted} {currency}";
    }
}
