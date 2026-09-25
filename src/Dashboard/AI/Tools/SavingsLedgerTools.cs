using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using AzureFinOps.Dashboard.Auth;
using Microsoft.Extensions.AI;

namespace AzureFinOps.Dashboard.AI.Tools;

/// <summary>
/// Persistent per-user savings ledger — the system of record for the money
/// this agent finds, actions, and VERIFIES. Most FinOps tooling stops at
/// "we found $X of savings"; the ledger closes the loop: proposed → executed
/// → verified (measured against actual Cost Management data after the fix).
///
/// Stored as one JSON file per user under $COPILOT_HOME/ledger/{userId}.json —
/// the same persistent Azure Files mount as chat history and identities, so
/// it survives restarts and deploys. userId is stable per Entra OID.
/// </summary>
public sealed class SavingsLedgerTools
{
    private static readonly string CopilotHome =
        Environment.GetEnvironmentVariable("COPILOT_HOME")
        ?? Path.Combine(Path.GetTempPath(), "copilot");

    private static readonly object FileLock = new();

    private readonly UserTokens _tokens;

    public SavingsLedgerTools(UserTokens tokens) => _tokens = tokens;

    private string LedgerPath => Path.Combine(CopilotHome, "ledger", $"{_tokens.UserId}.json");

    public IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(RecordSavingsAction, "RecordSavingsAction",
            "Records one evidenced FinOps remediation with only its affected scope and a concise action title, not raw tool responses or unrelated resources. A delivered script or pending write is proposed, not executed. Record executed only after the host confirms success or the user confirms applying the change. Generic code examples are not savings actions. Returns the entry id.");
        yield return AIFunctionFactory.Create(UpdateSavingsAction, "UpdateSavingsAction",
            "Update one exact savings ledger entry by its returned id; reuse a known id instead of fetching the full ledger again. If discovery is needed, use GetSavingsLedger filters and a small detail limit. Advance proposed/executed/verified/dismissed status or attach verified monthly savings only after re-measuring that action's scope with filtered cost data.");
        yield return AIFunctionFactory.Create(GetSavingsLedger, "GetSavingsLedger",
            "Query the owner's savings ledger with optional status, category and literal scopeContains filters. The host filters stored entries and computes all matching totals before paging; these are host-side controls, not Azure queries. Default 50 detail entries, maximum 200 per page, newest first; use limit='0' for totals only. Follow nextOffset only for requested detail, and preserve sourceEntries, matchedEntries, complete and totalsComplete. A limited page is not the full ledger. Use for 'what have we saved', specific remediation categories or exec reporting; reuse returned totals instead of listing every entry.");
    }

    private Task<string> RecordSavingsAction(
        [Description("Short action title, e.g. 'Deleted 12 unattached disks in rg-dev'")] string title,
        [Description("Category: cleanup | rightsizing | commitment | tagging | budget | licensing | scheduling | other")] string category,
        [Description("Estimated monthly savings in USD (0 for governance-only actions)")] string estimatedMonthlyUsd,
        [Description("Only the exact affected scope: subscription id, resource group or comma-separated resource ids. Exclude unrelated resources and raw API payloads; do not broaden a selected-resource action to its whole subscription.")] string scope,
        [Description("Initial status: proposed for a delivered remediation script or pending change; executed only after confirmed application. Script generation alone is never execution.")] string status)
    {
        var entry = new LedgerEntry(
            Id: Guid.NewGuid().ToString("N")[..8],
            CreatedUtc: DateTime.UtcNow,
            Title: title,
            Category: category,
            Scope: scope,
            EstimatedMonthlyUsd: ParseUsd(estimatedMonthlyUsd),
            VerifiedMonthlyUsd: null,
            Status: string.Equals(status, "executed", StringComparison.OrdinalIgnoreCase) ? "executed" : "proposed",
            UpdatedUtc: DateTime.UtcNow);

        var entries = Load();
        entries.Add(entry);
        Save(entries);
        return Task.FromResult($"Recorded ledger entry {entry.Id}: '{title}' ({entry.Status}, est ${entry.EstimatedMonthlyUsd:N0}/mo). Ledger now has {entries.Count} entries.");
    }

    private Task<string> UpdateSavingsAction(
        [Description("Exact ledger entry id returned by RecordSavingsAction or a filtered GetSavingsLedger lookup; reuse it without repeatedly listing all entries.")] string id,
        [Description("New status: proposed | executed | verified | dismissed")] string status,
        [Description("Verified monthly savings in USD measured from actual cost data (optional — omit or pass empty string to leave unchanged)")] string verifiedMonthlyUsd = "")
    {
        var entries = Load();
        var idx = entries.FindIndex(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
            return Task.FromResult($"Error: no ledger entry with id '{id}'. Call GetSavingsLedger to list entries.");

        var e = entries[idx];
        entries[idx] = e with
        {
            Status = status.ToLowerInvariant(),
            VerifiedMonthlyUsd = string.IsNullOrWhiteSpace(verifiedMonthlyUsd) ? e.VerifiedMonthlyUsd : ParseUsd(verifiedMonthlyUsd),
            UpdatedUtc = DateTime.UtcNow,
        };
        Save(entries);
        var v = entries[idx].VerifiedMonthlyUsd;
        return Task.FromResult($"Updated {id} → {entries[idx].Status}{(v is null ? "" : $", verified ${v:N0}/mo")}.");
    }

    private Task<string> GetSavingsLedger(
        [Description("Optional status filter: proposed, executed, verified or dismissed. Omit for all statuses.")] string? status = null,
        [Description("Optional category filter: cleanup, rightsizing, commitment, tagging, budget, licensing, scheduling or other.")] string? category = null,
        [Description("Optional case-insensitive literal substring filter of the saved scope, such as a subscription or resource group; not a regex or an authorization scope.")] string? scopeContains = null,
        [Description("Detail-entry limit as a string, 0-200, default 50. Use 0 for totals only; totals always cover all matching entries before this limit.")] string limit = "50",
        [Description("Nonnegative detail offset as a string, default 0. Use the returned nextOffset only when more detail is requested; paging does not change matching totals.")] string offset = "0")
        => Task.FromResult(FilterLedger(Load(), status, category, scopeContains, limit, offset));

    internal static string FilterLedger(IReadOnlyList<LedgerEntry> allEntries, string? status = null,
        string? category = null, string? scopeContains = null, string limit = "50", string offset = "0")
    {
        status = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToLowerInvariant();
        category = string.IsNullOrWhiteSpace(category) ? null : category.Trim().ToLowerInvariant();
        scopeContains = string.IsNullOrWhiteSpace(scopeContains) ? null : scopeContains.Trim();
        if (status is not (null or "proposed" or "executed" or "verified" or "dismissed")
            || category is not (null or "cleanup" or "rightsizing" or "commitment" or "tagging" or "budget" or "licensing" or "scheduling" or "other")
            || scopeContains?.Length > 500
            || !int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out var pageSize) || pageSize is < 0 or > 200
            || !int.TryParse(offset, NumberStyles.None, CultureInfo.InvariantCulture, out var pageOffset) || pageOffset < 0)
            return "Error: use valid ledger filters, a limit from 0 to 200 and a nonnegative offset; scopeContains supports at most 500 characters.";

        var matched = allEntries.Where(entry => (status is null || string.Equals(entry.Status, status, StringComparison.OrdinalIgnoreCase))
            && (category is null || string.Equals(entry.Category, category, StringComparison.OrdinalIgnoreCase))
            && (scopeContains is null || entry.Scope.Contains(scopeContains, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(entry => entry.CreatedUtc).ThenBy(entry => entry.Id, StringComparer.Ordinal).ToArray();
        var entries = matched.Skip(pageOffset).Take(pageSize).ToArray();
        var estTotal = matched.Where(entry => entry.Status is not "dismissed").Sum(entry => entry.EstimatedMonthlyUsd);
        var verTotal = matched.Where(entry => entry.Status == "verified").Sum(entry => entry.VerifiedMonthlyUsd ?? 0);
        var summary = new
        {
            generatedUtc = DateTime.UtcNow,
            filters = new { status, category, scopeContains },
            sourceEntries = allEntries.Count,
            matchedEntries = matched.Length,
            returnedEntries = entries.Length,
            offset = pageOffset,
            limit = pageSize,
            nextOffset = pageSize > 0 && pageOffset + entries.Length < matched.Length ? pageOffset + entries.Length : (int?)null,
            complete = pageOffset == 0 && entries.Length == matched.Length,
            totalsComplete = true,
            totals = new
            {
                estimatedMonthlyUsd = estTotal,
                verifiedMonthlyUsd = verTotal,
                estimatedAnnualUsd = estTotal * 12,
                verifiedAnnualUsd = verTotal * 12,
                entries = matched.Length,
                verified = matched.Count(entry => entry.Status == "verified"),
                executed = matched.Count(entry => entry.Status == "executed"),
                proposed = matched.Count(entry => entry.Status == "proposed"),
            },
            entries,
        };
        return JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
    }

    private List<LedgerEntry> Load()
    {
        lock (FileLock)
        {
            if (!File.Exists(LedgerPath)) return new List<LedgerEntry>();
            var json = File.ReadAllText(LedgerPath);
            return JsonSerializer.Deserialize<List<LedgerEntry>>(json) ?? new List<LedgerEntry>();
        }
    }

    private void Save(List<LedgerEntry> entries)
    {
        lock (FileLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LedgerPath)!);
            File.WriteAllText(LedgerPath, JsonSerializer.Serialize(entries));
        }
    }

    private static double ParseUsd(string s)
    {
        var cleaned = new string((s ?? "").Where(c => char.IsDigit(c) || c is '.' or '-').ToArray());
        return double.TryParse(cleaned, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    internal sealed record LedgerEntry(
        string Id,
        DateTime CreatedUtc,
        string Title,
        string Category,
        string Scope,
        double EstimatedMonthlyUsd,
        double? VerifiedMonthlyUsd,
        string Status,
        DateTime UpdatedUtc);
}
