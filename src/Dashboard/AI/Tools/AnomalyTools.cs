using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;

using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Infrastructure;

namespace AzureFinOps.Dashboard.AI.Tools;

/// <summary>
/// Cost anomaly detection — fetches daily costs from Cost Management and flags
/// days that deviate >2 standard deviations from the rolling baseline mean.
/// Returns structured JSON the LLM can summarize.
/// </summary>
public class AnomalyTools
{
    private readonly UserTokens _tokens;

    public AnomalyTools(UserTokens tokens) => _tokens = tokens;

    public IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(DetectCostAnomalies, "DetectCostAnomalies",
            @"Detects cost anomalies (spikes/drops) in a subscription's recent daily spend using statistical baselining (z-score over rolling window).

Use when the user asks about:
- 'Why did costs spike?'
- 'Are there any cost anomalies?'
- 'Did anything unusual happen in our spending last week?'
- 'Investigate cost increase'

Returns JSON with:
- baseline_mean, baseline_stddev, threshold (mean + 2*stddev)
- anomalies[]: dates where cost > threshold, with magnitude and grouping breakdown
- summary: human-readable explanation

After calling, drill into anomalous dates with QueryAzure (Cost Management /query grouped by ResourceGroupName or ServiceName for the specific date range) to find the root cause.");
    }

    private async Task<string> DetectCostAnomalies(
        [Description("Subscription ID to analyze")] string subscriptionId,
        [Description("Days of history to fetch (baseline + detection window). Default 35.")] int days = 35,
        [Description("Z-score threshold for flagging an anomaly. Default 2.0 (= ~95% confidence). Use 1.5 for more sensitive, 3.0 for stricter.")] double zThreshold = 2.0,
        [Description("Optional grouping for breakdown of anomalous days: 'ServiceName', 'ResourceGroupName', 'MeterCategory'. Default 'ServiceName'.")] string groupBy = "ServiceName")
    {
        var token = _tokens.AzureToken;
        if (string.IsNullOrEmpty(token))
            return HttpHelper.TokenMissing("AzureToken", null, "anomaly");

        if (string.IsNullOrWhiteSpace(subscriptionId))
            return "Error: subscriptionId is required.";

        days = Math.Clamp(days, 14, 90);
        zThreshold = Math.Clamp(zThreshold, 1.0, 5.0);
        if (string.IsNullOrWhiteSpace(groupBy)) groupBy = "ServiceName";

        var to = DateTime.UtcNow.Date;
        var from = to.AddDays(-days);

        // Cost Management daily query (no grouping — total daily cost for baseline)
        var dailyBody = JsonSerializer.Serialize(new
        {
            type = "ActualCost",
            timeframe = "Custom",
            timePeriod = new { from = from.ToString("yyyy-MM-dd"), to = to.ToString("yyyy-MM-dd") },
            dataset = new
            {
                granularity = "Daily",
                aggregation = new { totalCost = new { name = "Cost", function = "Sum" } }
            }
        });

        using var activity = HttpHelper.Telemetry.StartActivity("DetectCostAnomalies");
        activity?.SetTag("anomaly.subscription", subscriptionId);
        activity?.SetTag("anomaly.days", days);
        activity?.SetTag("anomaly.z_threshold", zThreshold);

        var dailyUrl = $"https://management.azure.com/subscriptions/{subscriptionId}/providers/Microsoft.CostManagement/query?api-version=2025-03-01";
        var dailyResp = await HttpHelper.SendWithRetryAsync(
            dailyUrl, token, activity, "anomaly.daily",
            method: HttpMethod.Post, jsonBody: dailyBody);

        if (!dailyResp.StartsWith("HTTP 200"))
            return $"Error fetching daily costs:\n{dailyResp[..Math.Min(dailyResp.Length, 1500)]}";

        // Parse daily costs: rows are [cost, date, currency] per CostManagement schema
        var dailyJson = dailyResp[(dailyResp.IndexOf('\n') + 1)..]; // strip "HTTP 200 OK\n"
        // strip optional "Current UTC time" line
        if (dailyJson.StartsWith("Current UTC time:")) dailyJson = dailyJson[(dailyJson.IndexOf('\n') + 1)..];

        var (series, parseErr) = ParseDailyCosts(dailyJson);
        if (parseErr is not null) return $"Error parsing cost response: {parseErr}\nRaw: {dailyJson[..Math.Min(dailyJson.Length, 800)]}";
        if (series.Count < 7) return $"Not enough data to baseline (got {series.Count} days, need >=7). Try a wider 'days' window.";

        // Compute rolling baseline: use first (days-7) days as baseline, last 7 as detection window
        var detectionDays = Math.Min(7, series.Count / 3);
        var baseline = series.Take(series.Count - detectionDays).ToList();
        var detection = series.Skip(series.Count - detectionDays).ToList();

        var mean = baseline.Average(p => p.Cost);
        var variance = baseline.Sum(p => Math.Pow(p.Cost - mean, 2)) / baseline.Count;
        var stddev = Math.Sqrt(variance);
        var threshold = mean + zThreshold * stddev;
        var lowThreshold = Math.Max(0, mean - zThreshold * stddev);

        var anomalies = new List<object>();
        foreach (var p in detection)
        {
            if (stddev < 0.01) continue; // flat baseline, can't detect
            var z = (p.Cost - mean) / stddev;
            if (Math.Abs(z) >= zThreshold)
            {
                // Drill down for this specific day
                var breakdown = await GetBreakdownForDay(token, subscriptionId, p.Date, groupBy, activity);
                anomalies.Add(new
                {
                    date = p.Date.ToString("yyyy-MM-dd"),
                    cost = Math.Round(p.Cost, 2),
                    z_score = Math.Round(z, 2),
                    deviation_pct = mean > 0.01 ? Math.Round((p.Cost - mean) / mean * 100, 1) : 0,
                    direction = z > 0 ? "spike" : "drop",
                    top_contributors = breakdown
                });
            }
        }

        var result = new
        {
            subscription_id = subscriptionId,
            window = new { from = from.ToString("yyyy-MM-dd"), to = to.ToString("yyyy-MM-dd"), baseline_days = baseline.Count, detection_days = detection.Count },
            baseline = new
            {
                mean = Math.Round(mean, 2),
                stddev = Math.Round(stddev, 2),
                z_threshold = zThreshold,
                upper_threshold = Math.Round(threshold, 2),
                lower_threshold = Math.Round(lowThreshold, 2),
            },
            anomalies_found = anomalies.Count,
            anomalies,
            recent_daily_costs = detection.Select(p => new { date = p.Date.ToString("yyyy-MM-dd"), cost = Math.Round(p.Cost, 2) }),
        };

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    private record DailyPoint(DateTime Date, double Cost);

    private static (List<DailyPoint> series, string? error) ParseDailyCosts(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("properties", out var props)) return (new(), "missing 'properties'");
            if (!props.TryGetProperty("rows", out var rows)) return (new(), "missing 'rows'");
            if (!props.TryGetProperty("columns", out var cols)) return (new(), "missing 'columns'");

            int costIdx = -1, dateIdx = -1, i = 0;
            foreach (var c in cols.EnumerateArray())
            {
                var name = c.GetProperty("name").GetString() ?? "";
                if (name.Equals("Cost", StringComparison.OrdinalIgnoreCase) || name.Equals("PreTaxCost", StringComparison.OrdinalIgnoreCase)) costIdx = i;
                if (name.Equals("UsageDate", StringComparison.OrdinalIgnoreCase) || name.Equals("BillingMonth", StringComparison.OrdinalIgnoreCase)) dateIdx = i;
                i++;
            }
            if (costIdx < 0 || dateIdx < 0) return (new(), $"could not locate Cost/UsageDate columns (got {i} columns)");

            var series = new List<DailyPoint>();
            foreach (var row in rows.EnumerateArray())
            {
                var cost = row[costIdx].GetDouble();
                var dateRaw = row[dateIdx].ValueKind == JsonValueKind.Number ? row[dateIdx].GetInt32().ToString() : row[dateIdx].GetString() ?? "";
                if (DateTime.TryParseExact(dateRaw, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var d)
                    || DateTime.TryParse(dateRaw, out d))
                {
                    series.Add(new DailyPoint(d.Date, cost));
                }
            }
            return (series.OrderBy(p => p.Date).ToList(), null);
        }
        catch (Exception ex)
        {
            return (new(), ex.Message);
        }
    }

    private static async Task<object> GetBreakdownForDay(string token, string subId, DateTime day, string groupBy, System.Diagnostics.Activity? activity)
    {
        var body = JsonSerializer.Serialize(new
        {
            type = "ActualCost",
            timeframe = "Custom",
            timePeriod = new { from = day.ToString("yyyy-MM-dd"), to = day.ToString("yyyy-MM-dd") },
            dataset = new
            {
                granularity = "None",
                aggregation = new { totalCost = new { name = "Cost", function = "Sum" } },
                grouping = new[] { new { type = "Dimension", name = groupBy } },
                sorting = new[] { new { direction = "descending", name = "Cost" } }
            }
        });
        var url = $"https://management.azure.com/subscriptions/{subId}/providers/Microsoft.CostManagement/query?api-version=2025-03-01";
        var resp = await HttpHelper.SendWithRetryAsync(url, token, activity, "anomaly.breakdown",
            method: HttpMethod.Post, jsonBody: body);

        if (!resp.StartsWith("HTTP 200")) return new { error = "could not fetch breakdown", detail = resp[..Math.Min(resp.Length, 300)] };

        var json = resp[(resp.IndexOf('\n') + 1)..];
        if (json.StartsWith("Current UTC time:")) json = json[(json.IndexOf('\n') + 1)..];

        try
        {
            using var doc = JsonDocument.Parse(json);
            var rows = doc.RootElement.GetProperty("properties").GetProperty("rows");
            var top = new List<object>();
            int n = 0;
            foreach (var row in rows.EnumerateArray())
            {
                if (n++ >= 5) break;
                top.Add(new { name = row[1].GetString() ?? "?", cost = Math.Round(row[0].GetDouble(), 2) });
            }
            return top;
        }
        catch { return new { error = "parse failed" }; }
    }
}
