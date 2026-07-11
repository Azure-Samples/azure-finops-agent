using System.ComponentModel;
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
            "Records a FinOps action in the user's persistent savings ledger. Call after ANY executed or proposed remediation (tags applied, budget created, cleanup script delivered, resize applied, reservation purchased). Returns the entry id.");
        yield return AIFunctionFactory.Create(UpdateSavingsAction, "UpdateSavingsAction",
            "Updates a savings ledger entry — advance its status (proposed→executed→verified|dismissed) and/or set verified monthly savings after re-measuring actual cost data.");
        yield return AIFunctionFactory.Create(GetSavingsLedger, "GetSavingsLedger",
            "Returns the user's full savings ledger with totals: estimated vs verified monthly savings, annualized impact, and per-entry status. Use when the user asks 'what have we saved', 'savings ledger', 'did we capture it', or for exec reporting.");
    }

    private Task<string> RecordSavingsAction(
        [Description("Short action title, e.g. 'Deleted 12 unattached disks in rg-dev'")] string title,
        [Description("Category: cleanup | rightsizing | commitment | tagging | budget | licensing | scheduling | other")] string category,
        [Description("Estimated monthly savings in USD (0 for governance-only actions)")] string estimatedMonthlyUsd,
        [Description("Affected scope — subscription id, resource group, or resource ids (comma-separated)")] string scope,
        [Description("Initial status: proposed | executed")] string status)
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
        [Description("Ledger entry id returned by RecordSavingsAction or listed by GetSavingsLedger")] string id,
        [Description("New status: proposed | executed | verified | dismissed")] string status,
        [Description("Verified monthly savings in USD measured from actual cost data (optional — pass empty string to leave unchanged)")] string verifiedMonthlyUsd)
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

    private Task<string> GetSavingsLedger()
    {
        var entries = Load();
        if (entries.Count == 0)
            return Task.FromResult("Savings ledger is empty. Record actions with RecordSavingsAction after executing remediations.");

        var estTotal = entries.Where(e => e.Status is not "dismissed").Sum(e => e.EstimatedMonthlyUsd);
        var verTotal = entries.Where(e => e.Status == "verified").Sum(e => e.VerifiedMonthlyUsd ?? 0);
        var summary = new
        {
            generatedUtc = DateTime.UtcNow,
            totals = new
            {
                estimatedMonthlyUsd = estTotal,
                verifiedMonthlyUsd = verTotal,
                estimatedAnnualUsd = estTotal * 12,
                verifiedAnnualUsd = verTotal * 12,
                entries = entries.Count,
                verified = entries.Count(e => e.Status == "verified"),
                executed = entries.Count(e => e.Status == "executed"),
                proposed = entries.Count(e => e.Status == "proposed"),
            },
            entries,
        };
        return Task.FromResult(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
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

    private sealed record LedgerEntry(
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
