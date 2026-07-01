using AzureFinOps.Dashboard.AI;
using AzureFinOps.Dashboard.Observability;

namespace AzureFinOps.Dashboard.Auth;

/// <summary>
/// Background service that evicts per-user state (CopilotSession, UserTokens, tool list)
/// when the user has been inactive for a configurable period, and deletes Copilot
/// session-state directories on disk older than <see cref="PersistedSessionTtl"/>.
/// Without this, the in-memory dictionaries grow unbounded as anonymous visitors
/// accumulate (eventually OOM'ing the container) and the persistent /home mount
/// fills up with abandoned sessions.
/// </summary>
public sealed class UserStateJanitor : BackgroundService
{
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<long, DateTimeOffset> LastSeenUtc = new();

    private static readonly TimeSpan IdleThreshold = TimeSpan.FromHours(1);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PersistedSessionTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan TtlSweepInterval = TimeSpan.FromHours(6);

    private readonly AiTelemetry _telemetry;
    private readonly CopilotSessionFactory _copilotFactory;
    private readonly ILogger<UserStateJanitor> _logger;
    private DateTimeOffset _nextTtlSweep = DateTimeOffset.UtcNow.Add(TtlSweepInterval);

    public UserStateJanitor(AiTelemetry telemetry, CopilotSessionFactory copilotFactory, ILogger<UserStateJanitor> logger)
    {
        _telemetry = telemetry;
        _copilotFactory = copilotFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Sweep(); }
            catch (Exception ex) { _logger.LogWarning(ex, "UserStateJanitor sweep failed"); }

            if (DateTimeOffset.UtcNow >= _nextTtlSweep)
            {
                try { await TtlSweep(stoppingToken); }
                catch (Exception ex) { _logger.LogWarning(ex, "UserStateJanitor TTL sweep failed"); }
                _nextTtlSweep = DateTimeOffset.UtcNow.Add(TtlSweepInterval);
            }

            try { await Task.Delay(SweepInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task Sweep()
    {
        var cutoff = DateTimeOffset.UtcNow - IdleThreshold;
        var evicted = 0;
        foreach (var (userId, lastSeen) in LastSeenUtc)
        {
            if (lastSeen >= cutoff) continue;

            LastSeenUtc.TryRemove(userId, out _);
            _telemetry.UserTokens.TryRemove(userId, out _);
            _telemetry.UserTools.TryRemove(userId, out _);
            _telemetry.CurrentSessionId.TryRemove(userId, out _);

            // Dispose any live sessions owned by this user. The on-disk session
            // state is preserved — the user can resume on next login.
            var owned = _telemetry.LiveSessions
                .Where(kv => kv.Value.UserId == userId)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var sid in owned)
            {
                if (_telemetry.LiveSessions.TryRemove(sid, out var live))
                {
                    _telemetry.ActiveSessions.Add(-1);
                    try { await live.Session.DisposeAsync(); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose Copilot session {SessionId} for user {UserId}", sid, userId); }
                }
            }
            evicted++;
        }

        if (evicted > 0)
            _logger.LogInformation("UserStateJanitor evicted {Count} idle user(s); active={Active}",
                evicted, LastSeenUtc.Count);
    }

    /// <summary>
    /// Deletes Copilot session-state directories on disk that haven't been
    /// modified in <see cref="PersistedSessionTtl"/> days. Runs every
    /// <see cref="TtlSweepInterval"/>.
    /// </summary>
    private async Task TtlSweep(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - PersistedSessionTtl;
        // Scoped list — only sessions under our managed user/anon roots, so we
        // never delete sessions that belong to another container instance
        // sharing the same /home Azure Files mount or unrelated SDK state.
        var all = await _copilotFactory.ListAllManagedSessionsAsync(ct);
        var deleted = 0;
        foreach (var meta in all)
        {
            if (meta.ModifiedTime >= cutoff) continue;
            try
            {
                await _copilotFactory.DeleteSessionByIdAsync(meta.SessionId, ct);
                deleted++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TTL sweep failed to delete session {SessionId}", meta.SessionId);
            }
        }
        if (deleted > 0)
            _logger.LogInformation("UserStateJanitor TTL sweep deleted {Count} session(s) older than {Days}d",
                deleted, PersistedSessionTtl.TotalDays);
    }
}
