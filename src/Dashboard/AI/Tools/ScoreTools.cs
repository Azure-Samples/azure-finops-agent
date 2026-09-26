using System.ComponentModel;
using System.Text.Json;
using AzureFinOps.Dashboard.Auth;
using Microsoft.Extensions.AI;

namespace AzureFinOps.Dashboard.AI.Tools;

public sealed class ScoreTools
{
    private const int MaxHistoryEntries = 100;
    private static readonly string CopilotHome =
        Environment.GetEnvironmentVariable("COPILOT_HOME")
        ?? Path.Combine(Path.GetTempPath(), "copilot");
    private static readonly Lock _fileLock = new();
    private static int _legacyHistoryChecked;

    private readonly UserTokens _tokens;

    public ScoreTools(UserTokens tokens)
    {
        _tokens = tokens;
        DeleteLegacySharedHistory();
    }

    private string ScoreFile => Path.Combine(CopilotHome, "scores", $"{_tokens.UserId}.json");

    public IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(ReportMaturityScore);
        yield return AIFunctionFactory.Create(GetScoreHistory);
    }

    [Description(@"Report FinOps maturity scores after evaluating a level (crawl, walk, run, or playbook). Call AFTER querying APIs and computing scores. Each dimension must include status=observed|unknown|notApplicable. Observed dimensions get 0-5; unknown and notApplicable get score=null and a reason. Missing permission is unknown, not zero. An absent workload makes workload-specific controls notApplicable. Auto-saved to history for trend analysis.

Evaluate ALL the dimensions for the requested level via QueryAzure (ARM, and Microsoft Graph or Log Analytics URLs where relevant) and score each 0-5 with a one-line `detail`. Don't ask which to score — score them all. Plan the whole level first: fetch its independent evidence in one QueryAzure requests batch (Cost Management query/forecast reads stay separate and sequential), then read each retained result path once with QueryToolResult; a level should need about 15 tool calls, not 30.

DATA SCOPING: use filtered source aggregates for the requested level and subscription scope, not raw resource lists. Submit all dimensions of that level with concise evidence; do not drop unknown or low-scoring dimensions to reduce payload.

EVIDENCE IS MANDATORY: each observed `detail` cites concrete counts, %, cost or other measured evidence. Unknown/notApplicable dimensions give a reason without invented numbers. Preserve per-subscription differences when relevant instead of treating a sample as the whole estate.

CRAWL — Visibility & Baseline (id slug — label — what to check):
  1. budgets — 'Budgets & thresholds' — Cost Mgmt budgets: count, amounts, notification config. Flag unrealistic (≥$1M placeholders) and missing alerts.
  2. tagging — 'Tagging for accountability' — Resource Graph: total resources + % carrying CostCenter, Owner, Environment (exact key names). Flag inconsistent casing ('department' vs 'Department') and placeholder values ('unassigned', 'unknown').
  3. exports — 'Cost data exports' — list Cost Mgmt exports (Microsoft.CostManagement/exports). Score 0 if none.
  4. alerts — 'Cost alerts & scheduled actions' — list Microsoft.CostManagement/scheduledActions (anomaly alerts are kind InsightAlert). Score 0 if none.
  5. policy — 'Governance guardrails' — management-group/subscription policy assignments enforcing FinOps tagging or cost controls.
  6. waste — 'Waste identification & cleanup' — counts of unattached disks, orphaned public IPs, empty App Service plans, empty resource groups.
  7. visibility — 'Cost visibility & ownership' — MTD spend grouped by RG and by top services.

WALK — Optimization & Governance (id slug — label — what to check):
  1. commitments — 'Reservations & Savings Plans' — RI/SP coverage % and utilization; Advisor RI/SP recommendations + their $ savings. Score 0 if no commitments and recommendations are being ignored.
  2. rightsizing — 'Right-sizing' — Advisor cost right-sizing/SKU recommendations: count + estimated $ savings; underutilized VMs/disks.
  3. devtest — 'Dev/Test scheduling' — auto-shutdown / start-stop schedules on non-prod VMs; count of non-prod VMs running 24x7 with no schedule.
  4. tagpolicy — 'Tag policy enforcement' — Azure Policy assignments that require/append/deny on tags (effects: Require, Modify, Deny) and their compliance %.
  5. ahub — 'Hybrid Benefit & licensing' — Windows/SQL Azure Hybrid Benefit applied vs eligible; SQL license type; reserved capacity for licensing.
  6. storageopt — 'Storage & lifecycle optimization' — blob lifecycle management policies, access-tier distribution (Hot/Cool/Archive), stale snapshots, premium disks on deallocated VMs.

RUN — Scale & Accountability (id slug — label — what to check):
  1. execreporting — 'Executive reporting & forecasting' — Cost Mgmt views, scheduled exports feeding BI, forecast usage, budget/anomaly trendlines for leadership.
  2. chargeback — 'Chargeback / showback readiness' — % of spend attributable to a cost owner via CostCenter/Owner tags or subscription/MG mapping; cost allocation rules configured.
  3. uniteconomics — 'Unit economics' — feasibility of cost-per-unit metrics (cost/customer, cost/transaction) from tags + meters; presence of business dimensions on resources.
  4. anomaly — 'Anomaly detection' — cost anomaly alerts at subscription/resource scope: count + routing/recipients. Score 0 if none.
  5. allocation — 'Cost allocation & MG governance' — management-group hierarchy depth, policy at MG scope, subscription-to-team mapping, Cost Mgmt cost-allocation rules.
  6. aicost — 'AI / GPU & emerging cost' — Azure OpenAI/Foundry spend, GPU VM/AKS spend, PTU vs PAYG mix; flag uncommitted GPU/AI spend. Carbon optional.

Return scores array: id=slug, label=exact name above, status=observed|unknown|notApplicable, score=0-5 for observed or null otherwise, detail=concise evidence or the specific reason evidence is unavailable.")]
    private string ReportMaturityScore(
        [Description("Level: 'crawl', 'walk', 'run', or 'playbook'")] string level,
        [Description(@"JSON array of all requested level dimensions with concise filtered evidence, not raw resource lists. Example: [{""id"":""tagging"",""label"":""Tagging"",""status"":""observed"",""score"":3,""detail"":""45% of resources tagged""}]. Include unknown/notApplicable dimensions with score=null and a reason.")] string scores)
    {
        if (level is not ("crawl" or "walk" or "run" or "playbook")) return "Error: invalid maturity level.";
        var normalized = NormalizeScores(scores);
        if (normalized is null) return "Error: scores require id, label, detail, status and an observed score from 0 to 5, or null for unknown/notApplicable.";
        SaveScore(level, normalized);
        return $"__MATURITY_SCORE__:{level}:{normalized}";
    }

    internal static string? NormalizeScores(string scores)
    {
        try
        {
            using var document = ParseArray(scores);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() is < 1 or > 30) return null;
            var normalized = new List<object>();
            var identifiers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var id = item.GetProperty("id").GetString();
                var label = item.GetProperty("label").GetString();
                var detail = item.GetProperty("detail").GetString();
                var status = item.GetProperty("status").GetString();
                if (string.IsNullOrWhiteSpace(id) || !identifiers.Add(id) || string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(detail)) return null;
                int? score = null;
                if (status == "observed")
                {
                    if (!item.GetProperty("score").TryGetInt32(out var value) || value is < 0 or > 5) return null;
                    score = value;
                }
                else if (status is not ("unknown" or "notApplicable")) return null;
                normalized.Add(new { id, label, score, detail, status });
            }
            return JsonSerializer.Serialize(normalized);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    // Models writing a long JSON-in-a-string argument sometimes garble only its closing brackets:
    // a stray closing brace of the surrounding call object after the array, or a dropped final ].
    // Each repair must still parse as a complete array of the submitted objects.
    private static JsonDocument ParseArray(string text)
    {
        try { return JsonDocument.Parse(text); }
        catch (JsonException)
        {
            var trimmed = text.Trim();
            var end = trimmed.Length;
            while (end > 0 && trimmed[end - 1] == '}') end--;
            if (trimmed.Length - end is 1 or 2 && end > 0 && trimmed[end - 1] == ']') return JsonDocument.Parse(trimmed[..end]);
            if (trimmed.StartsWith('[') && trimmed.EndsWith('}')) return JsonDocument.Parse(trimmed + "]");
            throw;
        }
    }

    /// <summary>Persists a score produced by a consolidated evidence tool.
    /// Keeps the on-disk format identical to <see cref="ReportMaturityScore"/>
    /// without requiring another LLM/tool round trip.</summary>
    internal void SaveScore(string level, string scores)
    {
        try
        {
            var entry = new ScoreHistoryEntry
            {
                Timestamp = DateTimeOffset.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                Level = level.ToLowerInvariant(),
                Scores = scores
            };

            lock (_fileLock)
            {
                var history = LoadHistory();
                history.Add(entry);
                if (history.Count > MaxHistoryEntries)
                    history = history[^MaxHistoryEntries..];
                Directory.CreateDirectory(Path.GetDirectoryName(ScoreFile)!);
                File.WriteAllText(ScoreFile, JsonSerializer.Serialize(history));
            }
        }
        catch { /* non-critical — don't break scoring if persistence fails */ }
    }

    [Description(@"Retrieve the owner's stored maturity history for trends. Pass the requested level filter instead of loading all levels; omit only for an explicit cross-level overview. The host filters at most 100 retained assessments, not live Azure resources. Reuse one response for the comparison rather than fetching per date. No date-range or arbitrary row-limit parameter is supported, and retained history is not an unlimited audit trail.")]
    private string GetScoreHistory(
        [Description("Filter by the requested level: crawl, walk, run or playbook. Omit only for cross-level history; repeated calls for individual dates are unnecessary.")] string? level = null)
    {
        List<ScoreHistoryEntry> history;
        lock (_fileLock)
        {
            history = LoadHistory();
        }

        if (!string.IsNullOrWhiteSpace(level))
            history = history.Where(h => h.Level.Equals(level.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

        if (history.Count == 0)
            return "No score history found. Run a maturity scoring first to establish a baseline.";

        return JsonSerializer.Serialize(history);
    }

    private List<ScoreHistoryEntry> LoadHistory()
    {
        try
        {
            if (File.Exists(ScoreFile))
            {
                var json = File.ReadAllText(ScoreFile);
                return JsonSerializer.Deserialize<List<ScoreHistoryEntry>>(json) ?? [];
            }
        }
        catch { }
        return [];
    }

    private static void DeleteLegacySharedHistory()
    {
        if (Interlocked.Exchange(ref _legacyHistoryChecked, 1) != 0) return;

        try
        {
            var legacyRoot = Environment.GetEnvironmentVariable("HOME") ?? Path.GetTempPath();
            var legacyFile = Path.Combine(legacyRoot, "finops-agent-scores", "score-history.json");
            if (File.Exists(legacyFile)) File.Delete(legacyFile);
        }
        catch
        {
            // The legacy file is never read. A failed cleanup cannot expose it
            // through this tool, so score reporting should remain available.
        }
    }

    private class ScoreHistoryEntry
    {
        public string Timestamp { get; set; } = "";
        public string Level { get; set; } = "";
        public string Scores { get; set; } = "";
    }
}
