using System.Diagnostics;

namespace LiveEvaluations;

public sealed class EvaluationRunState
{
    public Stopwatch Clock { get; } = Stopwatch.StartNew();
    public string Phase { get; set; } = "setup";
    public string[] Subscriptions { get; set; } = [];
    public List<ToolResult> Tools { get; } = [];
    public List<string> Errors { get; } = [];
    public HashSet<string> PendingTools { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Answers { get; } = new(StringComparer.Ordinal);
    public List<string> VisibleOutputs { get; } = [];
    public long? DurationMs { get; set; }
    public long? FirstTokenMs { get; set; }
    public bool Terminal { get; set; }
    public bool TranscriptVerified { get; set; }
    public string HostContext { get; set; } = "";
    public string AgentProfile { get; set; } = "";
    public bool SignedOut { get; set; }
    public int ThrottleNotices { get; set; }
    public bool FinalThrottle { get; set; }
    public DateTimeOffset? ThrottleRetryAtUtc { get; set; }
    public EvaluationFailure? Failure { get; private set; }

    private const int RecentEventLimit = 40;
    private const int HostWarningLimit = 40;
    private readonly object _diagnostics = new();
    private readonly Dictionary<string, int> _eventCounts = new(StringComparer.Ordinal);
    private readonly Queue<(string Type, long Ms)> _recentEvents = new();
    private readonly List<string> _hostWarnings = [];

    /// <summary>
    /// Every SSE event's type and arrival time, never its content: a case that stalls shows whether the host was still
    /// streaming (keepalive pings, reasoning) and what it last did. Private diagnostics only.
    /// </summary>
    public void RecordEvent(string type, long ms)
    {
        var name = type.Length is > 0 and <= 40 && type.All(c => c is >= 'a' and <= 'z' or '_') ? type : "other";
        lock (_diagnostics)
        {
            _eventCounts[name] = _eventCounts.GetValueOrDefault(name) + 1;
            _recentEvents.Enqueue((name, ms));
            if (_recentEvents.Count > RecentEventLimit) _recentEvents.Dequeue();
        }
    }

    /// <summary>The host's own warnings (a resent model request, a failed turn), bounded; private diagnostics only.</summary>
    public void RecordHostWarning(string warning)
    {
        lock (_diagnostics)
            if (_hostWarnings.Count < HostWarningLimit) _hostWarnings.Add(warning.Length <= 600 ? warning : warning[..600]);
    }

    public (IReadOnlyDictionary<string, int> Counts, (string Type, long Ms)[] Recent, string[] Warnings) Diagnostics()
    {
        lock (_diagnostics)
            return (new Dictionary<string, int>(_eventCounts, StringComparer.Ordinal), [.. _recentEvents], [.. _hostWarnings]);
    }

    public void RecordThrottle(DateTimeOffset? retryAtUtc, bool willRetry)
    {
        ThrottleNotices++;
        if (!willRetry) FinalThrottle = true;
        if (retryAtUtc is { } at && (ThrottleRetryAtUtc is null || at > ThrottleRetryAtUtc)) ThrottleRetryAtUtc = at;
    }

    public RunCapture Capture() => new(string.Join("\n\n", Answers.Values), Tools.ToArray(), Terminal,
        Errors.ToArray(), DurationMs ?? Clock.ElapsedMilliseconds, FirstTokenMs, VisibleOutputs.ToArray(), HostContext,
        ThrottleNotices, AgentProfile);

    public void RecordFailure(Exception exception)
    {
        Failure = new(Phase, exception.GetType().Name, exception.Message);
        Errors.Add($"Evaluation {Phase} failed ({exception.GetType().Name}).");
        if (PendingTools.Count > 0) Errors.Add("One or more tools have no terminal result.");
    }
}

public sealed record EvaluationFailure(string Phase, string Type, string Detail);
