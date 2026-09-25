using System.Text.Json;
using AzureFinOps.Dashboard.AI;
using AzureFinOps.Dashboard.Infrastructure;

namespace AzureFinOps.Dashboard.Observability;

internal sealed class TurnOutcomeStore
{
    internal sealed record Outcome(string RequestId, long Owner, string SessionId, string Status, string Fulfillment,
        DateTimeOffset StartedUtc, DateTimeOffset? CompletedUtc, long DurationMs, int AnswerCharacters,
        int ToolsCompleted, int ToolsFailed, string[] ArtifactIds, bool Scheduled);
    internal static TurnOutcomeStore Default { get; } = new(Path.Combine(
        Environment.GetEnvironmentVariable("COPILOT_HOME") ?? Path.Combine(Path.GetTempPath(), "copilot"), "turn-outcomes"));
    private readonly object _sync = new();
    private readonly string _root;
    private DateTimeOffset _lastCleanupUtc;

    internal TurnOutcomeStore(string root)
    {
        _root = Path.GetFullPath(root);
        Cleanup(recoverInterrupted: true);
    }

    internal void Start(TurnExecution turn) => Save(new(turn.RequestId, turn.UserId, turn.SessionId, "running", "not_evaluated",
        turn.StartedAt, null, 0, 0, 0, 0, [], turn.IsScheduled));

    internal void Complete(TurnExecution turn)
    {
        var evidence = turn.ToolEvidence.ToArray();
        var status = turn.CancellationReason ?? (!turn.HasUserOutput ? "empty" : turn.ToolsFailed > 0 || evidence.Any(item => !item.Success || !item.Fresh || item.Partial) ? "partial" : "completed");
        var now = DateTimeOffset.UtcNow;
        Save(new(turn.RequestId, turn.UserId, turn.SessionId, status, turn.JobOutcome?.Status ?? "not_evaluated",
            turn.StartedAt, now, (long)(now - turn.StartedAt).TotalMilliseconds, turn.AnswerCharacters,
            turn.ToolsCompleted, turn.ToolsFailed, turn.ArtifactIds.Distinct().ToArray(), turn.IsScheduled));
        using var activity = HttpHelper.Telemetry.StartActivity("turn.completed", System.Diagnostics.ActivityKind.Internal);
        activity?.SetTag("request.id", turn.RequestId);
        activity?.SetTag("session.id", turn.SessionId);
        activity?.SetTag("turn.status", status);
        activity?.SetTag("turn.fulfillment", turn.JobOutcome?.Status ?? "not_evaluated");
        activity?.SetTag("turn.duration_ms", (now - turn.StartedAt).TotalMilliseconds);
        activity?.SetTag("turn.tool_count", turn.ToolsCompleted);
        activity?.SetTag("turn.tool_failures", turn.ToolsFailed);
        activity?.SetTag("turn.answer_characters", turn.AnswerCharacters);
    }

    internal IReadOnlyList<Outcome> ForSession(long owner, string sessionId)
    {
        lock (_sync)
        {
            Cleanup();
            if (!Directory.Exists(_root)) return [];
            var outcomes = new List<Outcome>();
            foreach (var file in Directory.EnumerateFiles(_root, "*.json"))
            {
                try
                {
                    var outcome = JsonSerializer.Deserialize<Outcome>(File.ReadAllText(file));
                    if (outcome?.Owner == owner && outcome.SessionId == sessionId) outcomes.Add(outcome);
                }
                catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { }
            }
            return outcomes.OrderBy(outcome => outcome.StartedUtc).ToArray();
        }
    }

    private void Save(Outcome outcome)
    {
        lock (_sync)
        {
            try
            {
                Cleanup();
                Directory.CreateDirectory(_root);
                var path = Path.Combine(_root, outcome.RequestId + ".json");
                File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(outcome));
                File.Move(path + ".tmp", path, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                HttpHelper.Logger?.LogError("Turn outcome persistence failed: {ErrorType}", exception.GetType().Name);
            }
        }
    }

    private void Cleanup(bool recoverInterrupted = false)
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            if (!recoverInterrupted && now - _lastCleanupUtc < TimeSpan.FromHours(1)) return;
            _lastCleanupUtc = now;
            if (!Directory.Exists(_root)) return;
            foreach (var file in Directory.EnumerateFiles(_root, "*.json"))
            {
                try
                {
                    var outcome = JsonSerializer.Deserialize<Outcome>(File.ReadAllText(file));
                    if (outcome is null || !Guid.TryParseExact(outcome.RequestId, "N", out _)
                        || Path.GetFileNameWithoutExtension(file) != outcome.RequestId) continue;
                    if ((outcome.CompletedUtc ?? outcome.StartedUtc) < now.AddDays(-30)) File.Delete(file);
                    else if (recoverInterrupted && outcome.Status == "running"
                        && !TurnExecution.Active.Values.Any(turn => turn.RequestId == outcome.RequestId))
                        Save(outcome with { Status = "interrupted", Fulfillment = "not_evaluated", CompletedUtc = now,
                            DurationMs = (long)(now - outcome.StartedUtc).TotalMilliseconds });
                }
                catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
                {
                    HttpHelper.Logger?.LogWarning("Turn outcome cleanup failed: {ErrorType}", exception.GetType().Name);
                }
            }
        }
    }
}