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

    public IReadOnlyList<ScheduledJob> ForUser(long userId, string? entraOid) =>
        _jobs.Values
            .Where(j => j.UserId == userId || (!string.IsNullOrEmpty(entraOid) && j.EntraOid == entraOid))
            .OrderByDescending(j => j.CreatedUtc)
            .ToList();

    public IReadOnlyList<ScheduledJob> DueJobs(DateTimeOffset now) =>
        _jobs.Values.Where(j => j.Enabled && j.NextRunUtc <= now).ToList();

    public ScheduledJob? Get(string id) => _jobs.TryGetValue(id, out var j) ? j : null;

    public int EnabledCountForUser(long userId, string entraOid) =>
        _jobs.Values.Count(j => j.Enabled && (j.UserId == userId || j.EntraOid == entraOid));

    public void Add(ScheduledJob job)
    {
        _jobs[job.Id] = job;
        Save();
    }

    public void Remove(string id)
    {
        if (_jobs.TryRemove(id, out _)) Save();
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
            foreach (var j in list) _jobs[j.Id] = j;
            _logger.LogInformation("JobStore: loaded {Count} scheduled job(s)", list.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JobStore: load failed — starting empty");
        }
    }
}
