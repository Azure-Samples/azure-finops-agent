using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;

namespace LiveEvaluations;

public sealed record EvaluationCase(string Id, string Question, string Rubric, string[] RequiredTools,
    string[] ForbiddenTools, int MaxToolCalls = 20, int MaxDurationSeconds = 180);

public sealed record ToolResult(string Name, bool Success, string Result, string? Error, string Arguments = "",
    long StartedMs = -1, long CompletedMs = -1);

public sealed record RunCapture(string Answer, ToolResult[] Tools, bool Terminal, string[] Errors,
    long DurationMs, long? FirstTokenMs, string[]? VisibleOutputs = null, string HostContext = "",
    int ThrottleNotices = 0, string AgentProfile = "");

public sealed record JudgeVerdict(bool Accepted, bool Grounded, bool Complete, bool Efficient, int EfficiencyScore, string Reason,
    int AvoidableCalls = 0, int AvoidableRounds = 0, double AvoidableSeconds = 0);

public sealed record Verdict(bool Accepted, string[] Reasons, JudgeVerdict? Judge = null);

public static class EvaluationGate
{
    public static bool MatchesCandidateRevision(string expectedSha, params string?[] informationalVersions)
    {
        if (expectedSha.Length != 40 || !expectedSha.All(char.IsAsciiHexDigit) || informationalVersions.Length == 0)
            return false;
        return informationalVersions.All(version => version is not null && version.IndexOf('+') is var separator
            && separator > 0 && string.Equals(version[(separator + 1)..], expectedSha, StringComparison.OrdinalIgnoreCase));
    }

    public static bool TranscriptMatches(string transcriptJson, string question, string answer)
    {
        if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(answer)) return false;
        try
        {
            using var document = JsonDocument.Parse(transcriptJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("messages", out var messages)
                || messages.ValueKind != JsonValueKind.Array) return false;
            var foundQuestion = false;
            var answers = new List<string>();
            foreach (var message in messages.EnumerateArray())
            {
                if (message.ValueKind != JsonValueKind.Object
                    || !message.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String
                    || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
                    return false;
                if (role.GetString() == "user")
                {
                    if (foundQuestion || content.GetString() != question) return false;
                    foundQuestion = true;
                }
                else if (role.GetString() == "assistant" && foundQuestion)
                {
                    if (!string.IsNullOrWhiteSpace(content.GetString())) answers.Add(content.GetString()!);
                }
                else return false;
            }
            return foundQuestion && string.Equals(string.Join("\n\n", answers), answer, StringComparison.Ordinal);
        }
        catch (JsonException) { return false; }
    }

    public static bool ToolSucceeded(ToolResult tool)
    {
        if (!tool.Success || !string.IsNullOrWhiteSpace(tool.Error)) return false;
        var text = tool.Result.TrimStart();
        if (!EvidenceInspector.Inspect(text).Success
            || text.Contains("Output too large to read at once", StringComparison.OrdinalIgnoreCase)) return false;
        if (text.StartsWith("HTTP ", StringComparison.Ordinal) && text.IndexOf('\n') is var end && end >= 0) text = text[(end + 1)..];
        try
        {
            using var document = JsonDocument.Parse(text);
            return !Failed(document.RootElement);
        }
        catch (JsonException) { return true; }
    }

    private static bool Failed(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().Any(Failed);
        if (value.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in value.EnumerateObject())
        {
            if (property.Name is "ok" or "success" && property.Value.ValueKind == JsonValueKind.False) return true;
            if (property.Name == "error" && property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) return true;
            if (property.Name is "results" or "body" or "result" && Failed(property.Value)) return true;
            if (property.Name == "source" && property.Value.ValueKind == JsonValueKind.Object
                && property.Value.TryGetProperty("Success", out var success) && success.ValueKind == JsonValueKind.False) return true;
        }
        return false;
    }

    public static Verdict Assess(EvaluationCase scenario, RunCapture run, string judgeJson)
    {
        var reasons = new List<string>();
        if (!run.Terminal) reasons.Add("The turn did not complete.");
        if (string.IsNullOrWhiteSpace(run.Answer)) reasons.Add("The final answer is empty.");
        if (run.Errors.Length > 0) reasons.Add("The run emitted errors.");
        var failedCalls = run.Tools.Select((tool, index) => (tool, index)).Where(call => !ToolSucceeded(call.tool)).ToArray();
        if (failedCalls.Length > AllowedCorrectedToolFailures)
            reasons.Add($"{failedCalls.Length} tool calls failed; at most {AllowedCorrectedToolFailures} corrected failure is allowed.");
        else if (failedCalls.Any(call => !run.Tools.Skip(call.index + 1).Any(later => later.Name == call.tool.Name && ToolSucceeded(later))))
            reasons.Add("A failed tool call was never corrected by a later successful call to that tool.");
        if (run.Tools.Any(tool => tool.Result.Contains("Output too large to read at once", StringComparison.OrdinalIgnoreCase)))
            reasons.Add("Required tool evidence was offloaded and unavailable to the model.");
        if (run.Tools.Length > scenario.MaxToolCalls) reasons.Add("The tool-call budget was exceeded.");
        if (run.DurationMs < 0 || run.DurationMs > scenario.MaxDurationSeconds * 1000L) reasons.Add("The run duration was invalid or over budget.");
        foreach (var name in scenario.RequiredTools)
            if (!run.Tools.Any(tool => tool.Name == name)) reasons.Add($"Required tool was not called: {name}.");
        foreach (var name in scenario.ForbiddenTools)
            if (run.Tools.Any(tool => tool.Name == name)) reasons.Add($"Forbidden tool was called: {name}.");
        JudgeVerdict? judge = null;
        try
        {
            using var document = JsonDocument.Parse(judgeJson);
            var verdict = document.RootElement;
            int avoidableCalls = 0, avoidableRounds = 0;
            double avoidableSeconds = 0;
            if (verdict.ValueKind != JsonValueKind.Object || verdict.EnumerateObject().Count() != 7
                || !verdict.TryGetProperty("accepted", out var accepted) || accepted.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !verdict.TryGetProperty("grounded", out var grounded) || grounded.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !verdict.TryGetProperty("complete", out var complete) || complete.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !verdict.TryGetProperty("avoidableCalls", out var calls) || calls.ValueKind != JsonValueKind.Number
                || !calls.TryGetInt32(out avoidableCalls) || avoidableCalls < 0 || avoidableCalls > run.Tools.Length
                || !verdict.TryGetProperty("avoidableRounds", out var rounds) || rounds.ValueKind != JsonValueKind.Number
                || !rounds.TryGetInt32(out avoidableRounds) || avoidableRounds < 0 || avoidableRounds > run.Tools.Length
                || !verdict.TryGetProperty("avoidableSeconds", out var seconds) || seconds.ValueKind != JsonValueKind.Number
                || !seconds.TryGetDouble(out avoidableSeconds) || !double.IsFinite(avoidableSeconds) || avoidableSeconds < 0
                || !verdict.TryGetProperty("reason", out var reason) || reason.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(reason.GetString())) reasons.Add("Judge response did not satisfy the verdict schema.");
            else
            {
                var efficiencyScore = EfficiencyScore(avoidableCalls, avoidableRounds, avoidableSeconds,
                    run.Tools.Length, Math.Max(run.DurationMs, 0) / 1000.0);
                judge = new(accepted.GetBoolean(), grounded.GetBoolean(), complete.GetBoolean(),
                    efficiencyScore >= MinimumEfficiencyScore, efficiencyScore, reason.GetString()!,
                    avoidableCalls, avoidableRounds, avoidableSeconds);
                var qualityRejected = !judge.Accepted || !judge.Grounded || !judge.Complete;
                if (qualityRejected) reasons.Add("Judge rejected the result: " + judge.Reason);
                if (!judge.Efficient)
                    reasons.Add($"Judge rated the session inefficient ({judge.EfficiencyScore}/5)" + (qualityRejected ? "." : ": " + judge.Reason));
            }
        }
        catch (JsonException) { reasons.Add("Judge response was not valid JSON."); }
        return new(reasons.Count == 0, reasons.ToArray(), judge);
    }

    // The judge counts the waste; the rubric's anchors turn it into a score, so the same waste
    // always gets the same severity instead of depending on how harshly one verdict reads it.
    public static int EfficiencyScore(int avoidableCalls, int avoidableRounds, double avoidableSeconds,
        int toolCalls, double totalSeconds)
    {
        var waste = Math.Max(avoidableCalls, avoidableRounds);
        if (waste == 0) return 5;
        var addedSeconds = Math.Clamp(avoidableSeconds, 0, Math.Max(totalSeconds, 0));
        var growth = Math.Max((double)avoidableCalls / Math.Max(toolCalls - avoidableCalls, 1),
            addedSeconds / Math.Max(totalSeconds - addedSeconds, 1));
        if (growth >= 2) return 1;
        if (growth >= RoughlyDoubledGrowth) return 2;
        return waste <= 2 ? 4 : 3;
    }

    public const int MinimumEfficiencyScore = 3;

    // "Roughly doubled": the waste added at least three quarters of an expert's calls or elapsed time.
    public const double RoughlyDoubledGrowth = 0.75;

    // The agent researches: it may learn from one failed call when a later call to the same
    // tool succeeds. The judge still rejects an answer that rests on failed evidence.
    public const int AllowedCorrectedToolFailures = 1;
}