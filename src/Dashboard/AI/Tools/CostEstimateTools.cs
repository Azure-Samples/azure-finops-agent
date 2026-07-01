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
            @"DETERMINISTIC token-cost calculator. ALWAYS use this for ANY monthly/volume LLM cost estimate or model-vs-model price comparison — NEVER compute token costs in your head or in prose. Doing the math yourself is the #1 source of wrong totals (summary table disagreeing with the step-by-step, or two different token assumptions blended in one answer).

WORKFLOW: 1) look up per-1M-token rates with GetAzureRetailPricing (Standard + Global unless asked otherwise); 2) pass those rates plus ONE shared set of token assumptions here; 3) report the returned numbers VERBATIM.

The result is authoritative and already reconciled — input + output + cached costs sum exactly to totalMonthlyCost, and a ready-made 'breakdown' string is provided per model. In your reply, EVERY figure (headline, summary table, AND step-by-step) MUST equal these numbers exactly. Do NOT recompute or round differently. State the token assumption ONCE and reuse it for every model. If the user gives two scenarios (e.g. 1500/500 vs 100/400), call this tool ONCE PER scenario and keep them clearly separated — never mix.

Always label each model with the pricing basis you priced it on (e.g. 'gpt-5.4-nano, Global Standard') so the estimate states what it is based on.");
    }

    private static string EstimateTokenCost(
        [Description(@"JSON array of models to price, each with a label and per-1M-token rates (USD or the currency you pass). Schema: [{""label"":""gpt-5.4-nano, Global Standard"",""inputPricePer1M"":0.20,""outputPricePer1M"":1.25,""cachedInputPricePer1M"":0.02}]. cachedInputPricePer1M is optional (omit if not modeling cache hits). Rates come from GetAzureRetailPricing.")] string modelsJson,
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
        TryNum(cachedInputTokensPerConversation, out var cachedPerConvo);
        if (cachedPerConvo < 0) cachedPerConvo = 0;

        var cur = string.IsNullOrWhiteSpace(currency) ? "USD" : currency.Trim().ToUpperInvariant();

        JsonElement modelsRoot;
        try { modelsRoot = JsonDocument.Parse(modelsJson).RootElement; }
        catch (JsonException jex) { return $"Error: modelsJson is not valid JSON — {jex.Message}"; }
        if (modelsRoot.ValueKind != JsonValueKind.Array)
            return "Error: modelsJson must be a JSON array of model objects.";

        using var activity = HttpHelper.Telemetry.StartActivity("EstimateTokenCost");

        var monthlyInputTokens = inPerConvo * convos;
        var monthlyOutputTokens = outPerConvo * convos;
        var monthlyCachedTokens = cachedPerConvo * convos;

        var models = new List<object>();
        foreach (var m in modelsRoot.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object) continue;

            var label = GetString(m, "label") ?? "(unlabeled model)";
            var inRate = GetDouble(m, "inputPricePer1M");
            var outRate = GetDouble(m, "outputPricePer1M");
            var cachedRate = GetNullableDouble(m, "cachedInputPricePer1M");

            // Round each component to cents, then derive the total from the ROUNDED
            // components so the printed parts always sum exactly to the printed total.
            var inputCost = Round2(monthlyInputTokens / 1_000_000d * inRate);
            var outputCost = Round2(monthlyOutputTokens / 1_000_000d * outRate);
            var cachedCost = cachedRate is double cr ? Round2(monthlyCachedTokens / 1_000_000d * cr) : 0d;
            var total = Round2(inputCost + outputCost + cachedCost);
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
                "These totals are authoritative and already reconciled (components sum to totalMonthlyCost). " +
                "In your reply, the headline, the summary table, and the step-by-step MUST all equal these numbers — " +
                "use the per-model 'breakdown' string for the step-by-step. Do NOT recompute, do NOT blend a different " +
                "token assumption, and always state the pricing basis (e.g. Global Standard) for each model.",
            utc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
        };

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    private static bool TryNum(string? s, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        // Forgive thousands separators, underscores, currency symbols and whitespace.
        var cleaned = s.Replace(",", "").Replace("_", "").Replace("$", "").Replace("€", "").Replace("£", "").Trim();
        return double.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    private static string? GetString(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double GetDouble(JsonElement e, string prop) => GetNullableDouble(e, prop) ?? 0d;

    private static double? GetNullableDouble(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
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
