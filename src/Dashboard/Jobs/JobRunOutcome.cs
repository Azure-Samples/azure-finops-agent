using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using AzureFinOps.Dashboard.AI;
using AzureFinOps.Dashboard.Infrastructure;
using Microsoft.Extensions.AI;

namespace AzureFinOps.Dashboard.Jobs;

internal sealed record JobRunOutcome(string Status, string Summary, string[] EvidenceTools,
    DateTimeOffset? DataAsOfUtc, DateTimeOffset? NextEligibleRunUtc)
{
    internal bool Succeeded => Status is "completed" or "unchanged" or "goal_achieved";
    internal static JobRunOutcome Failed(string summary) => new("failed", SensitiveContent.Redact(summary), [], null, null);

    internal static JobRunOutcome Validate(JobRunOutcome? reported, TurnExecution turn, string answer, DateTimeOffset now)
    {
        if (turn.CancellationToken.IsCancellationRequested) return Failed("Run cancelled; partial operations may require review.");
        if (string.IsNullOrWhiteSpace(answer)) return Failed("The run returned no answer.");
        if (reported is null) return Failed("No structured run outcome was reported. The answer is not verified as successful.");
        if (!reported.Succeeded) return reported;
        if (reported.EvidenceTools.Length == 0) return Failed("The run did not cite fresh tool evidence.");
        var evidence = turn.ToolEvidence.ToArray().Select((item, index) => (Item: item, Key: item.ScopeKey ?? "unscoped:" + index))
            .GroupBy(entry => (entry.Item.Name, entry.Key)).Select(group => group.Last().Item).ToArray();
        if (reported.EvidenceTools.Any(name => !evidence.Any(item => item.Name == name)
            || evidence.Where(item => item.Name == name).Any(item => !item.Success || !item.Fresh || item.Partial)))
            return Failed("The reported success lacks complete fresh evidence from every cited tool.");
        if (reported.DataAsOfUtc is { } observed && (observed > now.AddMinutes(1) || observed < turn.StartedAt.AddMinutes(-5)))
            return Failed("The reported evidence timestamp is stale or in the future.");
        if (turn.CostQueriesBlocked && reported.EvidenceTools.Any(name => name.Contains("Cost", StringComparison.OrdinalIgnoreCase)))
            return new("blocked", "Cost data could not be refreshed. The run is not a fresh success.", reported.EvidenceTools, null, turn.NextEligibleCostQueryUtc);
        return reported;
    }

    internal static void Apply(ScheduledJob job, JobRunOutcome outcome, DateTimeOffset now)
    {
        job.LastRunUtc = now;
        job.RunCount++;
        job.LastStatus = outcome.Status;
        job.LastSummary = outcome.Summary[..Math.Min(outcome.Summary.Length, 500)];
        job.LastDataAsOfUtc = outcome.DataAsOfUtc;
        job.LastEvidenceTools = outcome.EvidenceTools;
        job.ConsecutiveFailures = outcome.Succeeded ? 0 : job.ConsecutiveFailures + 1;
        if (outcome.Status == "goal_achieved" || job.ConsecutiveFailures >= 5) job.Enabled = false;
        if (outcome.NextEligibleRunUtc is { } next && next > job.NextRunUtc)
            job.NextRunUtc = next < job.ExpiresUtc ? next : job.ExpiresUtc;
    }
}

internal sealed class JobOutcomeTools(long owner)
{
    internal IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(ReportJobOutcome, "ReportJobOutcome",
            "For scheduled runs only: report one terminal outcome after evidence tools finish. Supply a concise scoped summary and only the exact evidence tool names, not raw responses or prior transcripts. Do not omit failed or unattempted parts of the declared scope to claim success. Success requires current, complete evidence; stale history is not evidence. Call once, then give the concise human-readable answer. A verified goal_achieved result pauses the schedule.");
    }

    private string ReportJobOutcome(
        [Description("completed, unchanged, goal_achieved, blocked, partial, or failed")] string status,
        [Description("Concise scoped factual result or blocker, at most 1000 characters. Preserve partial coverage; no raw responses, transcripts or credentials.")] string summary,
        [Description("JSON array of at most 50 exact source tool names actually used for this run's evidence, not their arguments or result bodies. Retain the full declared scope; empty for a blocked or failed run.")] string evidenceToolsJson,
        [Description("UTC timestamp supplied by the data source, not retrieval time. Omit when unavailable.")] string? dataAsOfUtc = null,
        [Description("UTC retry deadline from a tool, or the next useful source refresh time. Omit when unknown.")] string? nextEligibleRunUtc = null)
    {
        var sessionId = ToolExecutionContext.Current?.SessionId;
        if (sessionId is null || !TurnExecution.Active.TryGetValue(sessionId, out var turn) || turn.UserId != owner || !turn.IsScheduled)
            return "Error: no scheduled run is active.";
        if (status is not ("completed" or "unchanged" or "goal_achieved" or "blocked" or "partial" or "failed"))
            return "Error: invalid outcome status.";
        if (string.IsNullOrWhiteSpace(summary) || summary.Length > 1000 || SensitiveContent.ContainsSecret(summary))
            return "Error: provide a concise result without credentials.";
        string[] evidence;
        try { evidence = JsonSerializer.Deserialize<string[]>(evidenceToolsJson) ?? []; }
        catch (JsonException) { return "Error: evidenceToolsJson must be a JSON string array."; }
        if (evidence.Length > 50 || evidence.Any(string.IsNullOrWhiteSpace)) return "Error: invalid evidence tool list.";
        if (!TryTime(dataAsOfUtc, out var dataTime) || !TryTime(nextEligibleRunUtc, out var retryTime))
            return "Error: timestamps must be ISO 8601 UTC values.";
        turn.JobOutcome = new(status, summary, evidence.Distinct(StringComparer.Ordinal).ToArray(), dataTime, retryTime);
        return "Scheduled outcome recorded for host validation.";
    }

    private static bool TryTime(string? value, out DateTimeOffset? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp)) return false;
        parsed = timestamp.ToUniversalTime();
        return true;
    }
}