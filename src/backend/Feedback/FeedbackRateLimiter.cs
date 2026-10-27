namespace AzureFinOps.Dashboard.Feedback;

/// <summary>
/// Limits how many issues the feedback form creates: per user (3 in 10 minutes, 10 a day) and for the
/// whole process (60 an hour), so one browser cannot flood the repository. Process-local like the
/// host's other gates.
/// </summary>
public sealed class FeedbackRateLimiter(TimeProvider time)
{
    internal static readonly (int Count, TimeSpan Window)[] PerUser = [(3, TimeSpan.FromMinutes(10)), (10, TimeSpan.FromDays(1))];
    internal static readonly (int Count, TimeSpan Window) PerProcess = (60, TimeSpan.FromHours(1));
    private const int SweepAbove = 10_000;

    private readonly Dictionary<long, List<DateTimeOffset>> _users = [];
    private readonly List<DateTimeOffset> _all = [];
    private readonly Lock _lock = new();

    /// <summary>Counts one issue for the user when every limit allows it; otherwise returns the wait.</summary>
    public bool TryAcquire(long userId, out TimeSpan retryAfter)
    {
        lock (_lock)
        {
            var now = time.GetUtcNow();
            var longest = PerUser.Max(limit => limit.Window);
            if (!_users.TryGetValue(userId, out var mine)) mine = [];
            mine.RemoveAll(at => now - at >= longest);
            _all.RemoveAll(at => now - at >= PerProcess.Window);

            retryAfter = Wait(_all, PerProcess.Count, PerProcess.Window, now);
            foreach (var (count, window) in PerUser)
            {
                var wait = Wait(mine, count, window, now);
                if (wait > retryAfter) retryAfter = wait;
            }
            if (retryAfter > TimeSpan.Zero) return false;

            mine.Add(now);
            _all.Add(now);
            _users[userId] = mine;
            if (_users.Count > SweepAbove)
                foreach (var stale in _users.Where(user => user.Value.All(at => now - at >= longest)).Select(user => user.Key).ToList())
                    _users.Remove(stale);
            return true;
        }
    }

    // Times are kept in arrival order, so the oldest of the last `count` decides when a slot frees up.
    private static TimeSpan Wait(List<DateTimeOffset> times, int count, TimeSpan window, DateTimeOffset now)
    {
        var recent = times.Where(at => now - at < window).ToList();
        return recent.Count < count ? TimeSpan.Zero : recent[^count] + window - now;
    }
}
