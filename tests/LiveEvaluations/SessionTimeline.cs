namespace LiveEvaluations;

/// <summary>One tool call placed on the run clock. Offsets are null when the stream carried no timing for the call.</summary>
public sealed record TimelineCall(int Order, int Round, ToolResult Tool, bool Succeeded, double? StartSeconds, double? EndSeconds, double? DurationSeconds);

/// <summary>
/// End-to-end shape of one evaluated turn: how long it took, how the tool calls were sequenced and overlapped,
/// and how much of the wall clock was tool execution versus model reasoning and answer generation.
/// </summary>
public sealed record SessionTimeline(double TotalSeconds, double? FirstTokenSeconds, int ToolCalls, int FailedToolCalls, int Rounds,
    int MaxConcurrentTools, double ToolWallSeconds, double ModelSeconds, TimelineCall[] Calls)
{
    public static SessionTimeline From(RunCapture run)
    {
        static double Seconds(long milliseconds) => Math.Round(milliseconds / 1000d, 2);
        static bool Timed(ToolResult tool) => tool.StartedMs >= 0 && tool.CompletedMs >= tool.StartedMs;

        var ordered = run.Tools.Select((tool, index) => (Tool: tool, Index: index))
            .OrderBy(item => Timed(item.Tool) ? 0 : 1).ThenBy(item => item.Tool.StartedMs).ThenBy(item => item.Index)
            .ToArray();
        var calls = new List<TimelineCall>(ordered.Length);
        var round = 0;
        var roundEnd = long.MinValue;
        long wall = 0, spanStart = -1, spanEnd = -1;
        foreach (var (tool, _) in ordered)
        {
            if (!Timed(tool))
            {
                calls.Add(new(calls.Count + 1, 0, tool, EvaluationGate.ToolSucceeded(tool), null, null, null));
                continue;
            }
            // A round is a group of calls that overlap in time; a call starting after every earlier call finished begins a new round.
            if (tool.StartedMs >= roundEnd) round++;
            roundEnd = Math.Max(roundEnd, tool.CompletedMs);
            if (spanStart < 0 || tool.StartedMs > spanEnd)
            {
                if (spanStart >= 0) wall += spanEnd - spanStart;
                (spanStart, spanEnd) = (tool.StartedMs, tool.CompletedMs);
            }
            else spanEnd = Math.Max(spanEnd, tool.CompletedMs);
            calls.Add(new(calls.Count + 1, round, tool, EvaluationGate.ToolSucceeded(tool),
                Seconds(tool.StartedMs), Seconds(tool.CompletedMs), Seconds(tool.CompletedMs - tool.StartedMs)));
        }
        if (spanStart >= 0) wall += spanEnd - spanStart;

        var concurrent = 0;
        var maxConcurrent = 0;
        foreach (var (_, delta) in ordered.Where(item => Timed(item.Tool))
            .SelectMany(item => new[] { (At: item.Tool.StartedMs, Delta: 1), (At: item.Tool.CompletedMs, Delta: -1) })
            .OrderBy(change => change.At).ThenBy(change => change.Delta))
            maxConcurrent = Math.Max(maxConcurrent, concurrent += delta);

        var total = Math.Max(0, run.DurationMs);
        return new(Seconds(total), run.FirstTokenMs is { } first ? Seconds(first) : null, run.Tools.Length,
            calls.Count(call => !call.Succeeded), round, maxConcurrent, Seconds(wall), Seconds(Math.Max(0, total - wall)), calls.ToArray());
    }
}
