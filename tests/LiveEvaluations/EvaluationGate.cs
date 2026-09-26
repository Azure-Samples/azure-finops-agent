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

public sealed record JudgeVerdict(bool Accepted, bool Grounded, bool Complete, bool Efficient, int EfficiencyScore, string Reason);

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
        if (!ProtectedTool.InspectEvidence(text).Success
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
        if (run.Tools.Any(tool => !ToolSucceeded(tool))) reasons.Add("At least one tool failed.");
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
            var efficiencyScore = 0;
            if (verdict.ValueKind != JsonValueKind.Object || verdict.EnumerateObject().Count() != 6
                || !verdict.TryGetProperty("accepted", out var accepted) || accepted.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !verdict.TryGetProperty("grounded", out var grounded) || grounded.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !verdict.TryGetProperty("complete", out var complete) || complete.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !verdict.TryGetProperty("efficient", out var efficient) || efficient.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !verdict.TryGetProperty("efficiencyScore", out var score) || score.ValueKind != JsonValueKind.Number
                || !score.TryGetInt32(out efficiencyScore) || efficiencyScore is < 1 or > 5
                || !verdict.TryGetProperty("reason", out var reason) || reason.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(reason.GetString())) reasons.Add("Judge response did not satisfy the verdict schema.");
            else
            {
                judge = new(accepted.GetBoolean(), grounded.GetBoolean(), complete.GetBoolean(), efficient.GetBoolean(), efficiencyScore, reason.GetString()!);
                var qualityRejected = !judge.Accepted || !judge.Grounded || !judge.Complete;
                if (qualityRejected) reasons.Add("Judge rejected the result: " + judge.Reason);
                // Scores 1-2 mean clear waste even if the flag disagrees; an inconsistent verdict never passes.
                if (!judge.Efficient || judge.EfficiencyScore < MinimumEfficiencyScore)
                    reasons.Add($"Judge rated the session inefficient ({judge.EfficiencyScore}/5)" + (qualityRejected ? "." : ": " + judge.Reason));
            }
        }
        catch (JsonException) { reasons.Add("Judge response was not valid JSON."); }
        return new(reasons.Count == 0, reasons.ToArray(), judge);
    }

    public const int MinimumEfficiencyScore = 3;
}