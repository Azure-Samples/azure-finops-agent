using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Infrastructure;
using Microsoft.Extensions.AI;

namespace AzureFinOps.Dashboard.AI.Tools;

/// <summary>
/// Deterministic tag-coverage audit. The host discovers the exact tag keys in
/// scope, matches requested tags case- and separator-insensitively, and builds
/// the aggregate Resource Graph query itself, so the model never authors tag KQL.
/// </summary>
public sealed partial class TagCoverageTools(UserTokens tokens)
{
    internal const string ResourceGraphFreshness =
        "Resource Graph is an indexed inventory and may lag resource changes. Its source data-as-of timestamp and indexing delay are unknown; retrievedAtUtc records completion of this inventory read, not source freshness.";

    internal static readonly string[] PlaceholderValues =
        ["unassigned", "unknown", "n/a", "na", "none", "tbd", "-", "null", "todo"];

    private const string ArgUrl =
        "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2024-04-01";

    private const string KeyDiscoveryQuery =
        "resources | where isnotempty(tags) | mv-expand bagexpansion=array tags limit 400 | extend tagKey=tostring(tags[0]) | where isnotempty(tagKey) | summarize resources=count() by tagKey | order by resources desc";

    private const int MaxTags = 10;
    private const int MaxVariantsPerTag = 20;
    private const int MaxRows = 5000;

    public IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(GetTagCoverage, "GetTagCoverage",
            "Tag compliance / tag coverage audit in ONE call. Use this instead of QueryAzure Resource Graph KQL whenever the user asks what share of resources carry given tags " +
            "(for example cost-center, environment, owner) or which resource groups have the worst tag coverage. " +
            "The host discovers every tag key actually present in scope, matches each requested tag case- and separator-insensitively (cost-center = CostCenter = cost_center), " +
            "then runs one host-built Resource Graph aggregate. It returns per-tag and all-tags coverage over every resource, the exact matched key variants, placeholder-value counts, " +
            "and resource groups ranked worst-first with totals. Coverage counts tags on the resources themselves; resource-group and subscription tags are not inherited unless a policy copies them. " +
            "Values such as unassigned/unknown/n/a/none/tbd are counted as placeholders, not as tagged. Similarly named but unmatched keys (for example ownerEmail) are listed, not counted. " +
            "Resource Graph is an indexed inventory: state that it may lag changes and that its source data-as-of timestamp is unknown. " +
            "Pass the full subscriptions array from the connection context; do not follow up with QueryAzure to recount the same tags.");
    }

    private async Task<string> GetTagCoverage(
        [Description("JSON array of subscription ids or {id,name} objects from the connection context covering the full requested scope (max 500).")] string subscriptionsJson,
        [Description("Comma-separated tag names to audit; default \"CostCenter,Environment,Owner\". Matching ignores case, spaces, hyphens, underscores and dots. Max 10.")] string tagKeys = "CostCenter,Environment,Owner",
        [Description("Worst-covered resource groups to return, 0-100; default 10. Totals always cover every resource group.")] string topResourceGroups = "10")
    {
        using var span = HttpHelper.Telemetry.StartActivity("GetTagCoverage");
        var (subscriptions, subscriptionError) = ParseSubscriptions(subscriptionsJson);
        var (tags, tagError) = ParseTagKeys(tagKeys);
        var topValid = int.TryParse(topResourceGroups?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var top)
            && top is >= 0 and <= 100;
        var error = subscriptionError ?? tagError
            ?? (subscriptions.Count == 0 ? "No valid subscription IDs were supplied." : null)
            ?? (topValid ? null : "topResourceGroups must be an integer from 0 to 100.");
        if (error is not null)
        {
            span?.SetStatus(ActivityStatusCode.Error, "Invalid tag coverage input");
            return $"HTTP 400 BadRequest\n{error} No request was sent.";
        }

        var token = tokens.AzureToken;
        if (string.IsNullOrEmpty(token))
            return HttpHelper.TokenMissing("AzureToken", span, "tag_coverage");

        var discovery = await RunResourceGraph(token, subscriptions, KeyDiscoveryQuery, span, "arg.tag_keys");
        if (discovery.Failure is not null) return discovery.Failure;
        var keyCounts = discovery.Rows
            .Select(row => (Key: row.TryGetProperty("tagKey", out var k) ? k.GetString() : null,
                Count: row.TryGetProperty("resources", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt64(out var n) ? n : 0))
            .Where(item => !string.IsNullOrEmpty(item.Key))
            .Select(item => (item.Key!, item.Count))
            .ToList();

        var matches = MatchTagKeys(tags, keyCounts);
        var coverage = await RunResourceGraph(token, subscriptions, BuildCoverageQuery(matches), span, "arg.tag_coverage");
        if (coverage.Failure is not null) return coverage.Failure;

        return Summarize(subscriptions, matches, coverage.Rows, top,
            discovery.Truncated || coverage.Truncated,
            DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
    }

    internal sealed record TagMatch(string Tag, string[] Keys, string[] UnqueryableKeys, string[] SimilarKeys);

    internal static string NormalizeKey(string key) =>
        new(key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    internal static (List<string> Tags, string? Error) ParseTagKeys(string? tagKeys)
    {
        var tags = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in (tagKeys ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = NormalizeKey(raw);
            if (raw.Length > 128 || normalized.Length == 0)
                return (tags, "Each tag name must be 1-128 characters and contain a letter or digit.");
            if (seen.Add(normalized)) tags.Add(raw);
        }
        if (tags.Count == 0) return (tags, "Supply at least one tag name in tagKeys.");
        if (tags.Count > MaxTags) return (tags, $"tagKeys supports at most {MaxTags} tag names.");
        return (tags, null);
    }

    internal static List<TagMatch> MatchTagKeys(IReadOnlyList<string> tags, IReadOnlyList<(string Key, long Count)> discovered)
    {
        var result = new List<TagMatch>();
        foreach (var tag in tags)
        {
            var target = NormalizeKey(tag);
            var exact = discovered.Where(d => NormalizeKey(d.Key) == target).Select(d => d.Key).ToList();
            var similar = discovered
                .Where(d => NormalizeKey(d.Key) is var n && n != target && n.Length > 0
                    && (n.Contains(target, StringComparison.Ordinal) || target.Contains(n, StringComparison.Ordinal)))
                .Select(d => d.Key).Take(10).ToArray();
            var safe = exact.Where(IsQueryableKey).Take(MaxVariantsPerTag).ToArray();
            var unsafeKeys = exact.Where(k => !IsQueryableKey(k)).Concat(exact.Where(IsQueryableKey).Skip(MaxVariantsPerTag))
                .Select(k => k.Length > 64 ? k[..64] : k).Take(10).ToArray();
            result.Add(new TagMatch(tag, safe, unsafeKeys, similar));
        }
        return result;
    }

    // Tag keys come from customer data; only a conservative literal-safe charset is ever embedded in KQL.
    internal static bool IsQueryableKey(string key) => SafeKeyPattern().IsMatch(key);

    [GeneratedRegex(@"^[A-Za-z0-9 _\-.:/@]{1,128}$")]
    private static partial Regex SafeKeyPattern();

    internal static string BuildCoverageQuery(IReadOnlyList<TagMatch> matches)
    {
        var placeholders = string.Join(",", PlaceholderValues.Select(v => $"'{v}'"));
        var sb = new StringBuilder("resources");
        for (var i = 0; i < matches.Count; i++)
        {
            var keys = matches[i].Keys;
            if (keys.Length == 0)
            {
                sb.Append($" | extend h{i}=0, p{i}=0");
                continue;
            }
            var values = string.Join(", ", keys.Select((k, j) => $"v{i}_{j}=tolower(trim(' ', tostring(tags['{k}'])))"));
            var tagged = string.Join(" or ", keys.Select((_, j) => $"(isnotempty(v{i}_{j}) and v{i}_{j} !in ({placeholders}))"));
            var placeholder = string.Join(" or ", keys.Select((_, j) => $"(v{i}_{j} in ({placeholders}))"));
            sb.Append($" | extend {values} | extend h{i}=iff({tagged},1,0) | extend p{i}=iff(h{i}==0 and ({placeholder}),1,0)");
        }
        var all = matches.Count == 0 ? "0" : string.Join(" and ", Enumerable.Range(0, matches.Count).Select(i => $"h{i}==1"));
        sb.Append($" | extend hAll=iff({all},1,0) | summarize resources=count()");
        for (var i = 0; i < matches.Count; i++) sb.Append($", h{i}=sum(h{i}), p{i}=sum(p{i})");
        sb.Append(", hAll=sum(hAll) by subscriptionId, resourceGroup=tolower(resourceGroup) | order by resources desc");
        return sb.ToString();
    }

    internal static string Summarize(
        IReadOnlyList<(string Id, string Name)> subscriptions,
        IReadOnlyList<TagMatch> matches,
        IReadOnlyList<JsonElement> rows,
        int top,
        bool truncated,
        string retrievedAtUtc)
    {
        static long Get(JsonElement row, string name) =>
            row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : 0;
        static double Pct(long part, long total) => total == 0 ? 0 : Math.Round(100.0 * part / total, 1);

        var names = subscriptions.ToDictionary(s => s.Id, s => s.Name, StringComparer.OrdinalIgnoreCase);
        var groups = rows.Select(row =>
        {
            var resources = Get(row, "resources");
            var perTag = matches.Select((m, i) => (m.Tag, Tagged: Get(row, $"h{i}"))).ToArray();
            var subscriptionId = row.TryGetProperty("subscriptionId", out var s) ? s.GetString() ?? "" : "";
            return new
            {
                subscriptionId,
                subscriptionName = names.GetValueOrDefault(subscriptionId, subscriptionId),
                resourceGroup = row.TryGetProperty("resourceGroup", out var g) ? g.GetString() ?? "" : "",
                resources,
                allTags = Get(row, "hAll"),
                allTagsPercent = Pct(Get(row, "hAll"), resources),
                meanTagPercent = perTag.Length == 0 ? 0 : Math.Round(perTag.Average(t => Pct(t.Tagged, resources)), 1),
                perTag = perTag.ToDictionary(t => t.Tag, t => new { tagged = t.Tagged, percent = Pct(t.Tagged, resources) })
            };
        }).ToList();

        var total = groups.Sum(g => g.resources);
        var allTagged = groups.Sum(g => g.allTags);
        var ranked = groups
            .OrderBy(g => g.allTagsPercent).ThenBy(g => g.meanTagPercent).ThenByDescending(g => g.resources)
            .ThenBy(g => g.resourceGroup, StringComparer.Ordinal).ToList();
        var unqueryable = matches.Any(m => m.UnqueryableKeys.Length > 0);
        var complete = !truncated && !unqueryable;

        return JsonSerializer.Serialize(new
        {
            source = "Azure Resource Graph resources table (host-built aggregate)",
            retrievedAtUtc,
            dataAsOfUtc = (string?)null,
            freshness = ResourceGraphFreshness,
            complete,
            coverageNote = complete
                ? "Totals cover every resource Resource Graph returned for the requested subscriptions."
                : truncated
                    ? "Resource Graph results exceeded the safety limit; totals are partial."
                    : "Some matching tag keys contain characters the host will not embed in a query; coverage for those tags may be understated.",
            scope = new
            {
                requestedSubscriptions = subscriptions.Count,
                subscriptionsWithResources = groups.Select(g => g.subscriptionId).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            },
            method = "A resource counts as tagged when any matched key variant has a non-empty value that is not a placeholder (" +
                string.Join(", ", PlaceholderValues) + "). Only tags on the resource itself are counted.",
            totals = new
            {
                resources = total,
                resourceGroups = groups.Count,
                allTagsResources = allTagged,
                allTagsPercent = Pct(allTagged, total),
                resourceGroupsWithZeroAllTagsCoverage = groups.Count(g => g.allTags == 0)
            },
            tags = matches.Select((m, i) => new
            {
                tag = m.Tag,
                matchedKeys = m.Keys,
                taggedResources = groups.Sum(g => g.perTag[m.Tag].tagged),
                percent = Pct(groups.Sum(g => g.perTag[m.Tag].tagged), total),
                placeholderResources = rows.Sum(r => Get(r, $"p{i}")),
                untaggedResources = total - groups.Sum(g => g.perTag[m.Tag].tagged),
                unqueryableKeys = m.UnqueryableKeys,
                similarUnmatchedKeys = m.SimilarKeys
            }),
            resourceGroupsWorstFirst = new
            {
                ranking = "allTagsPercent ascending, then mean per-tag percent ascending, then resource count descending",
                returned = Math.Min(top, ranked.Count),
                total = ranked.Count,
                items = ranked.Take(top).Select(g => new
                {
                    g.subscriptionName,
                    g.subscriptionId,
                    g.resourceGroup,
                    g.resources,
                    g.allTags,
                    g.allTagsPercent,
                    g.perTag
                })
            }
        });
    }

    private sealed record GraphResult(List<JsonElement> Rows, bool Truncated, string? Failure);

    private static async Task<GraphResult> RunResourceGraph(
        string token, IReadOnlyList<(string Id, string Name)> subscriptions, string query, Activity? span, string telemetry)
    {
        var rows = new List<JsonElement>();
        var ids = subscriptions.Select(s => s.Id).ToArray();
        string? skipToken = null;
        for (var page = 0; page < 10; page++)
        {
            var options = new Dictionary<string, object?> { ["resultFormat"] = "objectArray", ["$top"] = 1000 };
            if (!string.IsNullOrWhiteSpace(skipToken)) options["$skipToken"] = skipToken;
            var response = await HttpHelper.SendWithRetryAsync(ArgUrl, token, span, telemetry,
                method: HttpMethod.Post, jsonBody: JsonSerializer.Serialize(new { subscriptions = ids, query, options }));
            if (!response.StartsWith("HTTP 200 ", StringComparison.Ordinal))
                return new GraphResult(rows, false, response);

            using var doc = JsonDocument.Parse(response[(response.IndexOf('\n') + 1)..]);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return new GraphResult(rows, false, "HTTP 502 BadGateway\nResource Graph response did not contain an objectArray data set.");
            rows.AddRange(data.EnumerateArray().Select(row => row.Clone()));
            var resultTruncated = doc.RootElement.TryGetProperty("resultTruncated", out var t)
                && string.Equals(t.ToString(), "true", StringComparison.OrdinalIgnoreCase);
            if (rows.Count > MaxRows || resultTruncated) return new GraphResult(rows.Take(MaxRows).ToList(), true, null);
            skipToken = doc.RootElement.TryGetProperty("$skipToken", out var s) ? s.GetString() : null;
            if (string.IsNullOrWhiteSpace(skipToken)) return new GraphResult(rows, false, null);
        }
        return new GraphResult(rows, true, null);
    }

    internal static (List<(string Id, string Name)> Subscriptions, string? Error) ParseSubscriptions(string? json)
    {
        var scopes = new List<(string Id, string Name)>();
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return (scopes, "subscriptionsJson must be a JSON array.");
            if (doc.RootElement.GetArrayLength() > 500)
                return (scopes, "subscriptionsJson supports at most 500 entries; split larger estates into explicit scopes.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var rawId = item.ValueKind == JsonValueKind.String ? item.GetString()
                    : item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                rawId = rawId?.Trim();
                if (rawId?.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase) == true)
                    rawId = rawId["/subscriptions/".Length..].Trim('/');
                if (!Guid.TryParse(rawId, out var id) || !seen.Add(id.ToString())) continue;
                var name = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var nameEl)
                    && nameEl.ValueKind == JsonValueKind.String ? nameEl.GetString() : null;
                scopes.Add((id.ToString(), string.IsNullOrWhiteSpace(name) ? id.ToString() : name!));
            }
            return (scopes, null);
        }
        catch (JsonException ex)
        {
            return (scopes, $"Invalid subscriptionsJson: {ex.Message}");
        }
    }
}
