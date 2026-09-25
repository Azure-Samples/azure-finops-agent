using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Infrastructure;
using Microsoft.Extensions.AI;

namespace AzureFinOps.Dashboard.AI.Tools;

/// <summary>
/// Deterministic tag-based chargeback. The host resolves the real allocation-tag
/// spelling through Resource Graph, builds the grouped Cost Management queries
/// itself and computes team totals, top services and period-over-period change,
/// so the model never authors cost queries or arithmetic for this report.
/// </summary>
public sealed class ChargebackTools(UserTokens tokens)
{
    internal const int MaxSubscriptions = 20;
    internal const int MaxCandidateTags = 5;
    internal const string UntaggedLabel = "(untagged)";
    private const int MaxPages = 3;

    internal const string BillingFreshness =
        "Cost Management billing data can be delayed and is not a finalized invoice; retrievedAtUtc records when this report was read, not the billing data-as-of time.";

    public IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(GetChargebackReport, "GetChargebackReport",
            "Chargeback / showback report by an allocation tag (owner, cost center, team, department) in ONE call. " +
            "Use it instead of QueryAzure or BulkAzureRequest cost queries and Resource Graph tag KQL whenever the user asks to break down, allocate or charge back spend by a tag " +
            "with per-team totals, top services or month-over-month change. " +
            "The host discovers the tag-key spellings actually present, picks the requested candidate tag carried by the most resources (alternatives are reported), " +
            "then runs host-built ActualCost Cost Management queries grouped by that tag and ServiceName, sequentially, stopping after a final throttle: " +
            "the requested month (month-to-date for the current month) and a comparison window (the same day-of-month window of the previous month for the current month; the full previous month for a past month). " +
            "It returns per-team current and comparison spend, host-computed difference, percentChange and share, top services, untagged (unallocated) spend, totals by currency, exact inclusive dates and per-query status in results. " +
            "Present these values directly; do not recompute them with CompareAmounts or QueryToolResult and do not re-query the same costs. " +
            "Untagged spend is unallocated, not a team. percentChange is null when comparison spend is zero. " +
            "State the cost type, currency, inclusive date ranges, subscription scope, the chosen tag, and that billing data may lag and is not a finalized invoice. " +
            "If complete is false, disclose the failed or unattempted scopes instead of presenting partial totals as complete. " +
            "Pass the full subscriptions array from the connection context.");
    }

    private async Task<string> GetChargebackReport(
        [Description("JSON array of subscription ids or {id,name} objects from the connection context covering the full requested scope (max 20).")] string subscriptionsJson,
        [Description("Comma-separated candidate allocation tags in priority order; default \"CostCenter,Owner\". Matching ignores case, spaces, hyphens, underscores and dots. Max 5.")] string tagKeys = "CostCenter,Owner",
        [Description("Optional month in yyyy-MM format; default is the current UTC month (month-to-date). Past months use the full calendar month.")] string month = "",
        [Description("Top services to list per team, 1-10; default 3. Remaining services are summarized, never dropped from totals.")] string topServices = "3")
    {
        using var span = HttpHelper.Telemetry.StartActivity("GetChargebackReport");
        var (subscriptions, subscriptionError) = TagCoverageTools.ParseSubscriptions(subscriptionsJson);
        var (tags, tagError) = TagCoverageTools.ParseTagKeys(tagKeys);
        var (periods, monthError) = ResolvePeriods(month, DateOnly.FromDateTime(DateTime.UtcNow));
        var topValid = int.TryParse(topServices?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var top)
            && top is >= 1 and <= 10;
        var error = subscriptionError ?? tagError ?? monthError
            ?? (subscriptions.Count == 0 ? "No valid subscription IDs were supplied." : null)
            ?? (subscriptions.Count > MaxSubscriptions ? $"GetChargebackReport supports at most {MaxSubscriptions} subscriptions per call; split larger estates into explicit scopes." : null)
            ?? (tags.Count > MaxCandidateTags ? $"tagKeys supports at most {MaxCandidateTags} candidate allocation tags." : null)
            ?? (topValid ? null : "topServices must be an integer from 1 to 10.");
        if (error is not null)
        {
            span?.SetStatus(ActivityStatusCode.Error, "Invalid chargeback input");
            return $"HTTP 400 BadRequest\n{error} No request was sent.";
        }

        var token = tokens.AzureToken;
        if (string.IsNullOrEmpty(token))
            return HttpHelper.TokenMissing("AzureToken", span, "chargeback");

        var discovery = await TagCoverageTools.RunResourceGraph(token, subscriptions, TagCoverageTools.KeyDiscoveryQuery, span, "arg.chargeback_tag_keys");
        if (discovery.Failure is not null) return discovery.Failure;
        var keyCounts = discovery.Rows
            .Select(row => (Key: row.TryGetProperty("tagKey", out var k) ? k.GetString() : null,
                Count: row.TryGetProperty("resources", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt64(out var n) ? n : 0))
            .Where(item => !string.IsNullOrEmpty(item.Key))
            .Select(item => (item.Key!, item.Count))
            .ToList();
        var allocation = SelectAllocationKey(tags, keyCounts);
        if (allocation is null)
            return "HTTP 400 BadRequest\nNone of the requested tag names can be safely used as a Cost Management tag key. No cost query was sent.";

        var queries = new List<QueryOutcome>();
        var sourceEvidence = new List<JsonElement>();
        var throttled = false;
        foreach (var subscription in subscriptions)
        {
            foreach (var period in new[] { periods.Current, periods.Comparison })
            {
                if (throttled)
                {
                    queries.Add(new(subscription.Id, subscription.Name, period.Name, 0, 0, false, "not attempted after tenant throttle", []));
                    continue;
                }
                var outcome = await RunCostQuery(token, subscription, period, allocation.Key, span, sourceEvidence);
                queries.Add(outcome);
                throttled = outcome.Status == 429;
            }
        }

        return Summarize(subscriptions, allocation, periods, queries, sourceEvidence, throttled, top,
            discovery.Truncated, DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
    }

    internal sealed record Period(string Name, DateOnly From, DateOnly ToInclusive);

    internal sealed record Periods(Period Current, Period Comparison, string Basis);

    internal sealed record Allocation(string Tag, string Key, long TaggedResources, string[] OtherVariants, (string Tag, long Resources)[] Candidates);

    internal sealed record CostRow(string? TagValue, string Service, decimal Cost, string? Currency);

    internal sealed record QueryOutcome(string SubscriptionId, string SubscriptionName, string Period, int Status, int Pages, bool Truncated, string? Error, List<CostRow> Rows);

    internal static (Periods Periods, string? Error) ResolvePeriods(string? month, DateOnly today)
    {
        var currentMonth = new DateOnly(today.Year, today.Month, 1);
        var start = currentMonth;
        if (!string.IsNullOrWhiteSpace(month))
        {
            if (!DateOnly.TryParseExact(month.Trim() + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out start))
                return (null!, "month must use yyyy-MM format.");
            if (start > currentMonth) return (null!, "month cannot be in the future.");
            if (start < currentMonth.AddMonths(-13)) return (null!, "month must be within the last 13 months.");
        }

        var previous = start.AddMonths(-1);
        if (start == currentMonth)
        {
            var comparisonEnd = previous.AddDays(today.Day - 1);
            var previousEnd = start.AddDays(-1);
            return (new(new("current", start, today), new("comparison", previous, comparisonEnd > previousEnd ? previousEnd : comparisonEnd),
                "Month-to-date compared with the same day-of-month window of the previous month"), null);
        }
        return (new(new("current", start, start.AddMonths(1).AddDays(-1)), new("comparison", previous, start.AddDays(-1)),
            "Full calendar month compared with the full previous calendar month"), null);
    }

    internal static Allocation? SelectAllocationKey(IReadOnlyList<string> tags, IReadOnlyList<(string Key, long Count)> discovered)
    {
        var candidates = tags.Select(tag =>
        {
            var target = TagCoverageTools.NormalizeKey(tag);
            var variants = discovered
                .Where(d => TagCoverageTools.NormalizeKey(d.Key) == target && TagCoverageTools.IsQueryableKey(d.Key))
                .OrderByDescending(d => d.Count).ThenBy(d => d.Key, StringComparer.Ordinal).ToList();
            return (Tag: tag, Variants: variants, Resources: variants.Sum(v => v.Count));
        }).ToList();

        var chosen = candidates
            .Select((c, index) => (c, index))
            .OrderByDescending(x => x.c.Resources).ThenBy(x => x.index)
            .Select(x => x.c)
            .FirstOrDefault(c => c.Variants.Count > 0 || TagCoverageTools.IsQueryableKey(c.Tag));
        if (chosen.Tag is null) return null;

        // Cost Management merges tag keys case-insensitively, so only separator
        // variants remain outside the grouped key.
        var key = chosen.Variants.Count > 0 ? chosen.Variants[0].Key : chosen.Tag;
        var others = chosen.Variants
            .Where(v => !string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase))
            .Select(v => v.Key).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToArray();
        var taggedResources = chosen.Variants
            .Where(v => string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase)).Sum(v => v.Count);
        return new(chosen.Tag, key, taggedResources, others,
            candidates.Select(c => (c.Tag, c.Resources)).ToArray());
    }

    internal static string BuildQueryBody(string tagKey, Period period) => JsonSerializer.Serialize(new
    {
        type = "ActualCost",
        timeframe = "Custom",
        timePeriod = new
        {
            from = period.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            to = period.ToInclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        },
        dataset = new
        {
            granularity = "None",
            aggregation = new { totalCost = new { name = "Cost", function = "Sum" } },
            grouping = new object[]
            {
                new { type = "TagKey", name = tagKey },
                new { type = "Dimension", name = "ServiceName" }
            }
        }
    });

    private static async Task<QueryOutcome> RunCostQuery(
        string token, (string Id, string Name) subscription, Period period, string tagKey, Activity? span, List<JsonElement> sourceEvidence)
    {
        var path = $"/subscriptions/{subscription.Id}/providers/Microsoft.CostManagement/query";
        var url = $"https://management.azure.com{path}?api-version=2026-08-01";
        var body = BuildQueryBody(tagKey, period);
        var rows = new List<CostRow>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var response = await HttpHelper.SendWithRetryAsync(url, token, span, "cost.chargeback",
                method: HttpMethod.Post, jsonBody: body);
            sourceEvidence.Add(AzureQueryTools.ReadCostSourceEvidence(response));
            var status = ParseStatus(response);
            if (status != 200)
                return new(subscription.Id, subscription.Name, period.Name, status, page, false,
                    response.Length <= 400 ? response : response[..400], rows);

            var parsed = ParseCostRows(ResponseBody(response));
            if (parsed.Error is not null)
                return new(subscription.Id, subscription.Name, period.Name, 200, page, false, parsed.Error, rows);
            rows.AddRange(parsed.Rows);
            if (string.IsNullOrWhiteSpace(parsed.NextLink))
                return new(subscription.Id, subscription.Name, period.Name, 200, page, false, null, rows);
            if (!Uri.TryCreate(parsed.NextLink, UriKind.Absolute, out var next)
                || next.Scheme != Uri.UriSchemeHttps
                || !string.Equals(next.Host, "management.azure.com", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(next.AbsolutePath, path, StringComparison.OrdinalIgnoreCase))
                return new(subscription.Id, subscription.Name, period.Name, 200, page, true, null, rows);
            url = next.AbsoluteUri;
        }
        return new(subscription.Id, subscription.Name, period.Name, 200, MaxPages, true, null, rows);
    }

    internal static (List<CostRow> Rows, string? NextLink, string? Error) ParseCostRows(string body)
    {
        var rows = new List<CostRow>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var props = doc.RootElement.GetProperty("properties");
            var columns = props.GetProperty("columns").EnumerateArray()
                .Select((c, i) => (Name: c.GetProperty("name").GetString() ?? "", Index: i))
                .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Index, StringComparer.OrdinalIgnoreCase);
            var costIndex = columns.TryGetValue("Cost", out var ci) ? ci : columns.TryGetValue("PreTaxCost", out var pi) ? pi : -1;
            if (costIndex < 0 || !columns.TryGetValue("ServiceName", out var serviceIndex))
                return (rows, null, "Cost Management response did not contain Cost and ServiceName columns.");
            var tagIndex = columns.TryGetValue("TagValue", out var ti) ? ti : -1;
            var currencyIndex = columns.TryGetValue("Currency", out var cu) ? cu : -1;
            foreach (var row in props.GetProperty("rows").EnumerateArray())
            {
                var costElement = row[costIndex];
                decimal cost;
                if (costElement.ValueKind == JsonValueKind.Number && costElement.TryGetDecimal(out var number)) cost = number;
                else if (costElement.ValueKind == JsonValueKind.String
                    && decimal.TryParse(costElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var text)) cost = text;
                else return (rows, null, "Cost Management response contained a non-numeric cost.");
                rows.Add(new(
                    tagIndex >= 0 && row[tagIndex].ValueKind == JsonValueKind.String ? row[tagIndex].GetString() : null,
                    row[serviceIndex].ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(row[serviceIndex].GetString())
                        ? row[serviceIndex].GetString()! : "(unknown service)",
                    cost,
                    currencyIndex >= 0 && row[currencyIndex].ValueKind == JsonValueKind.String ? row[currencyIndex].GetString() : null));
            }
            var nextLink = props.TryGetProperty("nextLink", out var link) && link.ValueKind == JsonValueKind.String ? link.GetString() : null;
            return (rows, nextLink, null);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            return (rows, null, "Cost Management response could not be parsed.");
        }
    }

    private sealed class TeamTotals(string team, bool untagged, bool placeholder, string currency)
    {
        public string Team { get; } = team;
        public bool Untagged { get; } = untagged;
        public bool Placeholder { get; } = placeholder;
        public string Currency { get; } = currency;
        public decimal Current { get; set; }
        public decimal Comparison { get; set; }
        public Dictionary<string, decimal> Services { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal static string Summarize(
        IReadOnlyList<(string Id, string Name)> subscriptions,
        Allocation allocation,
        Periods periods,
        IReadOnlyList<QueryOutcome> queries,
        IReadOnlyList<JsonElement> sourceEvidence,
        bool throttled,
        int top,
        bool discoveryTruncated,
        string retrievedAtUtc)
    {
        var teams = new Dictionary<(string Team, string Currency), TeamTotals>();
        var missingCurrency = false;
        foreach (var query in queries.Where(q => q.Status == 200 && q.Error is null))
        {
            foreach (var row in query.Rows)
            {
                if (string.IsNullOrWhiteSpace(row.Currency))
                {
                    if (row.Cost != 0) missingCurrency = true;
                    continue;
                }
                var value = row.TagValue?.Trim();
                var untagged = string.IsNullOrEmpty(value);
                var label = untagged ? UntaggedLabel : value!;
                var key = (label.ToLowerInvariant(), row.Currency!.ToUpperInvariant());
                if (!teams.TryGetValue(key, out var totals))
                {
                    var placeholder = !untagged && TagCoverageTools.PlaceholderValues.Contains(label.ToLowerInvariant());
                    teams[key] = totals = new TeamTotals(label, untagged, placeholder, key.Item2);
                }
                if (query.Period == "current")
                {
                    totals.Current += row.Cost;
                    totals.Services[row.Service] = totals.Services.GetValueOrDefault(row.Service) + row.Cost;
                }
                else totals.Comparison += row.Cost;
            }
        }

        var failed = queries.Where(q => q.Status != 200 || q.Error is not null).ToList();
        var truncated = queries.Any(q => q.Truncated);
        var complete = failed.Count == 0 && !truncated && !missingCurrency;
        var byCurrency = teams.Values.GroupBy(t => t.Currency).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
        static decimal? Change(decimal current, decimal comparison) =>
            comparison == 0 ? null : Math.Round((current - comparison) / comparison * 100, 1, MidpointRounding.AwayFromZero);

        var retryAt = sourceEvidence
            .Select(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("retryAtUtc", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null)
            .LastOrDefault(r => r is not null);

        return JsonSerializer.Serialize(new
        {
            source = "Azure Cost Management query (host-built, grouped by allocation tag and ServiceName)",
            costType = "ActualCost",
            retrievedAtUtc,
            dataAsOfUtc = (string?)null,
            freshness = BillingFreshness,
            complete,
            throttled,
            retryAtUtc = throttled ? retryAt : null,
            coverageNote = complete
                ? "Totals cover every Cost Management row returned for the requested subscriptions and both periods."
                : "Some queries failed, were not attempted or were truncated; totals below are partial and must not be presented as complete.",
            scope = new { subscriptionCount = subscriptions.Count, subscriptions = subscriptions.Select(s => new { id = s.Id, name = s.Name }) },
            currentPeriod = new { from = periods.Current.From, to = periods.Current.ToInclusive, endInclusive = true },
            comparisonPeriod = new { from = periods.Comparison.From, to = periods.Comparison.ToInclusive, endInclusive = true },
            comparisonBasis = periods.Basis,
            allocationTag = new
            {
                requestedTag = allocation.Tag,
                costManagementTagKey = allocation.Key,
                resourcesCarryingKey = allocation.TaggedResources,
                selection = "Requested candidate tag carried by the most resources in Resource Graph; ties keep the requested order.",
                candidates = allocation.Candidates.Select(c => new { tag = c.Tag, resourcesCarryingTag = c.Resources }),
                separatorVariantsNotGrouped = allocation.OtherVariants,
                variantNote = allocation.OtherVariants.Length == 0
                    ? null
                    : "Spend tagged only with these separator variants appears as untagged in this breakdown.",
                discoveryTruncated
            },
            method = "Rows without a value for the allocation tag are unallocated (untagged). Placeholder values such as unassigned or n/a are reported as their own rows and flagged. percentChange = (current - comparison) / comparison * 100 and is null when comparison spend is zero.",
            totalsByCurrency = byCurrency.ToDictionary(g => g.Key, g =>
            {
                var current = g.Sum(t => t.Current);
                var comparison = g.Sum(t => t.Comparison);
                var untaggedCurrent = g.Where(t => t.Untagged).Sum(t => t.Current);
                return new
                {
                    current = Round(current),
                    comparison = Round(comparison),
                    difference = Round(current - comparison),
                    percentChange = Change(current, comparison),
                    untaggedCurrent = Round(untaggedCurrent),
                    untaggedSharePercent = current == 0 ? (decimal?)null : Math.Round(untaggedCurrent / current * 100, 1, MidpointRounding.AwayFromZero),
                    teams = g.Count(t => !t.Untagged)
                };
            }),
            teams = byCurrency.SelectMany(g =>
            {
                var currencyTotal = g.Sum(t => t.Current);
                return g.OrderByDescending(t => t.Current).ThenBy(t => t.Team, StringComparer.Ordinal).Select(t =>
                {
                    var ranked = t.Services.OrderByDescending(s => s.Value).ThenBy(s => s.Key, StringComparer.Ordinal).ToList();
                    return new
                    {
                        team = t.Team,
                        untagged = t.Untagged,
                        placeholder = t.Placeholder,
                        currency = t.Currency,
                        current = Round(t.Current),
                        comparison = Round(t.Comparison),
                        difference = Round(t.Current - t.Comparison),
                        percentChange = Change(t.Current, t.Comparison),
                        sharePercent = currencyTotal == 0 ? (decimal?)null : Math.Round(t.Current / currencyTotal * 100, 1, MidpointRounding.AwayFromZero),
                        topServices = ranked.Take(top).Select(s => new { service = s.Key, current = Round(s.Value) }),
                        otherServices = new { count = Math.Max(0, ranked.Count - top), current = Round(ranked.Skip(top).Sum(s => s.Value)) }
                    };
                });
            }),
            results = queries.Select(q => new
            {
                subscriptionId = q.SubscriptionId,
                subscriptionName = q.SubscriptionName,
                period = q.Period,
                status = q.Status,
                pages = q.Pages,
                truncated = q.Truncated,
                rows = q.Rows.Count,
                error = q.Error
            }),
            sourceEvidence
        });
    }

    private static int ParseStatus(string response)
    {
        var parts = response.Split('\n', 2)[0].Split(' ', 3);
        return parts.Length > 1 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var status) ? status : 0;
    }

    private static string ResponseBody(string response)
    {
        var firstNewline = response.IndexOf('\n');
        var body = firstNewline >= 0 ? response[(firstNewline + 1)..] : "";
        if (body.StartsWith("Current UTC time: ", StringComparison.Ordinal))
        {
            var timestampEnd = body.IndexOf('\n');
            body = timestampEnd >= 0 ? body[(timestampEnd + 1)..] : "";
        }
        return body;
    }
}
