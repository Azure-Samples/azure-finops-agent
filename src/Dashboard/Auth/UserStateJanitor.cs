using AzureFinOps.Dashboard.Observability;

namespace AzureFinOps.Dashboard.Auth;

/// <summary>
/// Background service that evicts per-user runtime state (UserTokens, tool
/// closures) when the user has been inactive for <see cref="IdleThreshold"/>.
/// Without this, the in-memory dictionaries grow unbounded as anonymous
/// visitors accumulate (eventually OOM'ing the container).
///
/// Chat history is stored only in the browser (IndexedDB) — there is no
/// server-side session state to sweep.
/// </summary>
public sealed class UserStateJanitor : BackgroundService
{
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<long, DateTimeOffset> LastSeenUtc = new();

    private static readonly TimeSpan IdleThreshold = TimeSpan.FromHours(1);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(10);

    private readonly AiTelemetry _telemetry;
    private readonly ILogger<UserStateJanitor> _logger;

    public UserStateJanitor(AiTelemetry telemetry, ILogger<UserStateJanitor> logger)
    {
        _telemetry = telemetry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { Sweep(); }
            catch (Exception ex) { _logger.LogWarning(ex, "UserStateJanitor sweep failed"); }

            try { await Task.Delay(SweepInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void Sweep()
    {
        var cutoff = DateTimeOffset.UtcNow - IdleThreshold;
        var evicted = 0;
        foreach (var (userId, lastSeen) in LastSeenUtc)
        {
            if (lastSeen >= cutoff) continue;

            LastSeenUtc.TryRemove(userId, out _);
            _telemetry.UserTokens.TryRemove(userId, out _);
            _telemetry.UserTools.TryRemove(userId, out _);
            evicted++;
        }

        if (evicted > 0)
            _logger.LogInformation("UserStateJanitor evicted {Count} idle user(s); active={Active}",
                evicted, LastSeenUtc.Count);
    }
}
