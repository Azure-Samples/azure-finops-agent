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

For Crawl-level requests (or anything similar), evaluate ALL 7 dimensions below via QueryAzure and score each 0-5 with a one-line `detail` citing concrete numbers. Don't ask which to score — score them all.

Crawl dimensions (id slug — label — what to check):
  1. budgets — 'Budgets & thresholds' — Cost Mgmt budgets: count, amounts, notification config. Flag unrealistic (≥$1M placeholders) and missing alerts.
  2. tagging — 'Tagging for accountability' — Resource Graph: total resources + % carrying CostCenter, Owner, Environment (exact key names). Flag inconsistent casing ('department' vs 'Department') and placeholder values ('unassigned', 'unknown').
  3. exports — 'Cost data exports' — list Cost Mgmt exports. Score 0 if none.
  4. alerts — 'Cost alerts & scheduled actions' — list anomaly alerts + scheduled actions. Score 0 if none.
  5. policy — 'Governance guardrails' — management-group policy assignments enforcing FinOps tagging or cost controls at sub scope.
  6. waste — 'Waste identification & cleanup' — RG counts of unattached disks, orphaned public IPs, empty App Service plans, empty resource groups.
  7. visibility — 'Cost visibility & ownership' — MTD spend grouped by RG and by top services.

Return scores array: id=slug, label=exact name above, score=0-5, detail=one-line reason with numbers.")]
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
