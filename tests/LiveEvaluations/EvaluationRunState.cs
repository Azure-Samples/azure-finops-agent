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
    public int ThrottleNotices { get; set; }
    public bool FinalThrottle { get; set; }
    public DateTimeOffset? ThrottleRetryAtUtc { get; set; }
    public EvaluationFailure? Failure { get; private set; }

    public void RecordThrottle(DateTimeOffset? retryAtUtc, bool willRetry)
    {
        ThrottleNotices++;
        if (!willRetry) FinalThrottle = true;
        if (retryAtUtc is { } at && (ThrottleRetryAtUtc is null || at > ThrottleRetryAtUtc)) ThrottleRetryAtUtc = at;
    }

    public RunCapture Capture() => new(string.Join("\n\n", Answers.Values), Tools.ToArray(), Terminal,
        Errors.ToArray(), DurationMs ?? Clock.ElapsedMilliseconds, FirstTokenMs, VisibleOutputs.ToArray(), HostContext);

    public void RecordFailure(Exception exception)
    {
        Failure = new(Phase, exception.GetType().Name, exception.Message);
        Errors.Add($"Evaluation {Phase} failed ({exception.GetType().Name}).");
        if (PendingTools.Count > 0) Errors.Add("One or more tools have no terminal result.");
    }
}

public sealed record EvaluationFailure(string Phase, string Type, string Detail);
