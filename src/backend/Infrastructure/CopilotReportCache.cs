using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AzureFinOps.Dashboard.AI;
using Microsoft.Extensions.Caching.Memory;

namespace AzureFinOps.Dashboard.Infrastructure;

/// <summary>
/// Keeps Microsoft 365 Copilot cost management's completed-day usage reports (per user, group and service) for
/// 15 minutes per credential and request, so another card or a breakdown that reads the same days does not send them
/// again: the service throttles a run of these reads with a 30-second Retry-After (18 November 2026). A completed day's
/// report can still change while processing catches up, so a reused result is labelled cached with its retrieval time
/// and never counts as fresh evidence, and scheduled runs always read the source.
/// </summary>
internal static partial class CopilotReportCache
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private const string Freshness =
        "Completed-day Copilot report reused from an identical read in the last 15 minutes; retrievedAtUtc is when Microsoft Graph returned it, and processing can still update the day.";

    private static readonly MemoryCache Cache = new(new MemoryCacheOptions { SizeLimit = 8 * 1024 * 1024 });

    private sealed record Entry(string Result, DateTimeOffset RetrievedUtc);

    /// <summary>The cache key for a completed-day report read with this credential, or null for any other request.</summary>
    internal static string? KeyFor(string token, Uri uri, int maxPages, bool timestamp)
    {
        if (!CompletedDayReport().IsMatch(uri.AbsolutePath) || !CompletedDaySelector().IsMatch(uri.Query)) return null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            token + "\n" + uri.AbsoluteUri + "\n" + maxPages + "\n" + timestamp)));
    }

    /// <summary>The cached result labelled as cached, or null when there is none, the run is scheduled, or the result
    /// cannot carry the label.</summary>
    internal static string? TryGet(string key)
    {
        if (IsScheduledRun() || !Cache.TryGetValue<Entry>(key, out var entry) || entry is null) return null;
        return CostQueryCoordinator.WithCacheStatus(entry.Result, "cached", entry.RetrievedUtc, default, Freshness);
    }

    internal static void Store(string key, string result, DateTimeOffset retrievedUtc)
    {
        if (!result.StartsWith("HTTP 200 ", StringComparison.Ordinal) || result.Length > 262144) return;
        Cache.Set(key, new Entry(result, retrievedUtc), new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = Lifetime,
            Size = result.Length * 2L + key.Length * 2L
        });
    }

    private static bool IsScheduledRun() =>
        ToolExecutionContext.Current?.SessionId is { } sessionId
        && TurnExecution.Active.TryGetValue(sessionId, out var turn) && turn.IsScheduled;

    [GeneratedRegex(@"^/(v1\.0|beta)/copilot/costManagement/usageReports/(userUsageRecords|groupUsageRecords|serviceUsageRecords)(/[^/]+)?/?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CompletedDayReport();

    [GeneratedRegex(@"[?&]startDate=[^&]+.*&endDate=|[?&]endDate=[^&]+.*&startDate=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CompletedDaySelector();
}
