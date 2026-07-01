using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AzureFinOps.Dashboard.AI.Tools;

public static class ScoreTools
{
    private static readonly string ScoreDir = Path.Combine(
        Environment.GetEnvironmentVariable("HOME") ?? Path.GetTempPath(), "finops-agent-scores");
    private static readonly string ScoreFile = Path.Combine(ScoreDir, "score-history.json");
    private static readonly Lock _fileLock = new();

    static ScoreTools()
    {
        Directory.CreateDirectory(ScoreDir);
    }

    public static IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(ReportMaturityScore);
        yield return AIFunctionFactory.Create(GetScoreHistory);
    }

    [Description(@"Report FinOps maturity scores after evaluating a level (crawl, walk, run, or playbook). Call AFTER querying APIs and computing scores. Each dimension gets 0-5: 0=no data, 1=critical, 2=needs work, 3=acceptable, 4=good, 5=best practice. Auto-saved to history for trend analysis.

Evaluate ALL the dimensions for the requested level via QueryAzure (and GraphQuery/LogAnalytics where relevant) and score each 0-5 with a one-line `detail`. Don't ask which to score — score them all.

EVIDENCE IS MANDATORY: every `detail` must cite the concrete numbers behind the score (counts, %, $ MTD, recommendation counts, savings estimates). A score with no number is not acceptable. When the estate spans multiple subscriptions, note the spread in `detail` (e.g. 'tagged 0% in Prod-EU, 38% in Sandbox') so the assessment reflects per-subscription reality, not one blended figure.

CRAWL — Visibility & Baseline (id slug — label — what to check):
  1. budgets — 'Budgets & thresholds' — Cost Mgmt budgets: count, amounts, notification config. Flag unrealistic (≥$1M placeholders) and missing alerts.
  2. tagging — 'Tagging for accountability' — Resource Graph: total resources + % carrying CostCenter, Owner, Environment (exact key names). Flag inconsistent casing ('department' vs 'Department') and placeholder values ('unassigned', 'unknown').
  3. exports — 'Cost data exports' — list Cost Mgmt exports. Score 0 if none.
  4. alerts — 'Cost alerts & scheduled actions' — list anomaly alerts + scheduled actions. Score 0 if none.
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

Return scores array: id=slug, label=exact name above, score=0-5, detail=one-line reason WITH numbers (and per-subscription spread when relevant).")]
    private static string ReportMaturityScore(
        [Description("Level: 'crawl', 'walk', 'run', or 'playbook'")] string level,
        [Description(@"JSON array of score objects, e.g.: [{""id"":""tagging"",""label"":""Tagging"",""score"":3,""detail"":""45% of resources tagged""}]")] string scores)
    {
        // Persist score to history file for trend analysis
        try
        {
            var entry = new ScoreHistoryEntry
            {
                Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                Level = level.ToLowerInvariant(),
                Scores = scores
            };

            lock (_fileLock)
            {
                var history = LoadHistory();
                history.Add(entry);
                File.WriteAllText(ScoreFile, JsonSerializer.Serialize(history));
            }
        }
        catch { /* non-critical — don't break scoring if persistence fails */ }

        return $"__MATURITY_SCORE__:{level}:{scores}";
    }

    [Description(@"Retrieve historical FinOps maturity scores for trend analysis (current vs previous, improvement/regression over time). Use when user asks about score trends, progress, or historical comparison.")]
    private static string GetScoreHistory(
        [Description("Optional: filter by level ('crawl', 'walk', 'run', 'playbook'). Omit to get all levels.")] string? level = null)
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

    private static List<ScoreHistoryEntry> LoadHistory()
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

    private class ScoreHistoryEntry
    {
        public string Timestamp { get; set; } = "";
        public string Level { get; set; } = "";
        public string Scores { get; set; } = "";
    }
}
