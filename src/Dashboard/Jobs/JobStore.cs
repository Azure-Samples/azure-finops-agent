using System.Collections.Concurrent;
using System.Text.Json;

namespace AzureFinOps.Dashboard.Jobs;

/// <summary>
/// A user-defined scheduled job: a prompt that runs on a cadence inside its own
/// dedicated Copilot session. The session doubles as the job's run history —
/// each run appends a turn, so opening the job in the UI shows every past
/// answer (text, charts, tables) through the normal transcript replay.
/// </summary>
public sealed class ScheduledJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public long UserId { get; set; }
    /// <summary>Durable owner identity — jobs are Entra-only (background auth
    /// needs the persisted refresh token, which anonymous users don't have).</summary>
    public string EntraOid { get; set; } = "";
    public string EntraTenantId { get; set; } = "";
    public string UserLogin { get; set; } = "";
    public string Name { get; set; } = "";
    public string Prompt { get; set; } = "";
    /// <summary>Cadence in minutes. Allowed: 15, 60, 1440 (daily), 10080 (weekly).</summary>
    public int IntervalMinutes { get; set; } = 1440;
    /// <summary>The dedicated Copilot session backing this job. Created on first run.</summary>
    public string? SessionId { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Hard stop: sub-daily jobs expire after 7 days (capacity hunts are
    /// short-lived), daily/weekly after 90. Expired jobs are disabled, not deleted.</summary>
    public DateTimeOffset ExpiresUtc { get; set; }
    public DateTimeOffset NextRunUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastRunUtc { get; set; }
    /// <summary>ok | error | auth_expired | busy | expired — last run outcome.</summary>
    public string? LastStatus { get; set; }
    /// <summary>First ~200 chars of the last answer, for the sidebar tooltip.</summary>
    public string? LastSummary { get; set; }
    public DateTimeOffset? LastDataAsOfUtc { get; set; }
    public string[] LastEvidenceTools { get; set; } = [];
    public int LastCompactedRun { get; set; }
    public int RunCount { get; set; }
    public int ConsecutiveFailures { get; set; }
}

/// <summary>
/// Persisted store for scheduled jobs — single JSON file under COPILOT_HOME
/// (the App Service /home Azure Files mount), same durability story as session
/// state and titles. All mutations go through <see cref="Save"/> which
/// serializes under a lock; reads are lock-free off the ConcurrentDictionary.
/// </summary>
public sealed class JobStore
{
    private static readonly string JobsFile = Path.Combine(
        Environment.GetEnvironmentVariable("COPILOT_HOME") ?? Path.Combine(Path.GetTempPath(), "copilot"),
        "scheduled-jobs.json");

    private readonly ConcurrentDictionary<string, ScheduledJob> _jobs = new();
    private readonly object _saveLock = new();
    private readonly ILogger _logger;

    public JobStore(ILogger logger)
    {
        _logger = logger;
        Load();
    }

    public IReadOnlyList<ScheduledJob> ForUser(
        long userId, string? entraTenantId, string? entraOid) =>
        _jobs.Values
            .Where(j => j.UserId == userId
                && !string.IsNullOrEmpty(entraTenantId)
                && !string.IsNullOrEmpty(entraOid)
                && string.Equals(j.EntraTenantId, entraTenantId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(j.EntraOid, entraOid, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(j => j.CreatedUtc)
            .ToList();

    public IReadOnlyList<ScheduledJob> DueJobs(DateTimeOffset now) =>
        _jobs.Values.Where(j => j.Enabled && j.NextRunUtc <= now).ToList();

    public ScheduledJob? Get(string id) => _jobs.TryGetValue(id, out var j) ? j : null;

    public int EnabledCountForUser(long userId, string entraTenantId, string entraOid) =>
        _jobs.Values.Count(j =>
            j.Enabled && j.UserId == userId
            && string.Equals(j.EntraTenantId, entraTenantId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(j.EntraOid, entraOid, StringComparison.OrdinalIgnoreCase));

    public void Add(ScheduledJob job)
    {
        _jobs[job.Id] = job;
        Save();
    }

    public void Remove(string id)
    {
        if (_jobs.TryRemove(id, out _)) Save();
    }

    /// <summary>Detaches any job pointing at a deleted conversation. Keeps the
    /// invariant that a job's SessionId always refers to a live session (or is
    /// null) — otherwise opening the job shows a dead "couldn't load history"
    /// view until the next run happens to replace the id.</summary>
    public void DetachSession(string sessionId)
    {
        var changed = false;
        foreach (var j in _jobs.Values)
        {
            if (j.SessionId == sessionId)
            {
                j.SessionId = null;
                changed = true;
            }
        }
        if (changed) Save();
    }

    /// <summary>Persist after mutating a job instance in place.</summary>
    public void Save()
    {
        lock (_saveLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(JobsFile)!);
                File.WriteAllText(JobsFile, JsonSerializer.Serialize(_jobs.Values.ToList(),
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "JobStore: persist failed");
            }
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(JobsFile)) return;
            var list = JsonSerializer.Deserialize<List<ScheduledJob>>(File.ReadAllText(JobsFile));
            if (list is null) return;
            var disabledLegacyJobs = 0;
            foreach (var j in list)
            {
                if (string.IsNullOrWhiteSpace(j.EntraTenantId))
                {
                    j.Enabled = false;
                    j.LastStatus = "auth_expired";
                    j.LastSummary = "Reconnect Azure and recreate this job to restore tenant-bound ownership.";
                    disabledLegacyJobs++;
                }
                _jobs[j.Id] = j;
            }
            _logger.LogInformation("JobStore: loaded {Count} scheduled job(s)", list.Count);
            if (disabledLegacyJobs > 0)
            {
                _logger.LogWarning(
                    "JobStore: disabled {Count} legacy job(s) without tenant-bound ownership",
                    disabledLegacyJobs);
                Save();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JobStore: load failed — starting empty");
        }
    }
}
