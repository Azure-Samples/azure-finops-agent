using System.Text.Json;

namespace LiveEvaluations;

public sealed record EvaluationCase(string Id, string Question, string Rubric, string[] RequiredTools,
    string[] ForbiddenTools, int MaxToolCalls = 20, int MaxDurationSeconds = 180);

public sealed record ToolResult(string Name, bool Success, string Result, string? Error, string Arguments = "");

public sealed record RunCapture(string Answer, ToolResult[] Tools, bool Terminal, string[] Errors,
    long DurationMs, long? FirstTokenMs, string[]? VisibleOutputs = null, string HostContext = "");

public sealed record Verdict(bool Accepted, string[] Reasons);

public sealed record JudgeVerdict(bool Accepted, bool Grounded, bool Complete, string Reason);

public sealed record EvaluationResult(RunCapture Capture, Verdict Verdict, bool TranscriptVerified,
    JudgeVerdict? Judge, string? FailurePhase);

public static class EvaluationGate
{
    public static string RedactAndTruncate(string text, int maxCharacters, Func<string, string> redact)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCharacters);
        var redacted = redact(text);
        return redacted.Length <= maxCharacters ? redacted : redacted[..maxCharacters] + "\n[truncated]";
    }

    public static bool VerifyTranscript(string transcriptJson, string question, string answer)
    {
        if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(answer)) return false;
        try
        {
            using var document = JsonDocument.Parse(transcriptJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("messages", out var messages)
                || messages.ValueKind != JsonValueKind.Array) return false;
            // Each evaluation starts a new conversation. BuildTranscript coalesces all
            // assistant messages in that turn with two newlines, without message IDs.
            if (messages.GetArrayLength() != 2) return false;
            return MessageMatches(messages[0], "user", question)
                && MessageMatches(messages[1], "assistant", answer);
        }
        catch (JsonException) { return false; }
    }

    private static bool MessageMatches(JsonElement message, string role, string content) =>
        message.ValueKind == JsonValueKind.Object
        && message.TryGetProperty("role", out var actualRole) && actualRole.ValueKind == JsonValueKind.String
        && actualRole.GetString() == role
        && message.TryGetProperty("content", out var actualContent) && actualContent.ValueKind == JsonValueKind.String
        && string.Equals(actualContent.GetString(), content, StringComparison.Ordinal);

    public static async Task<EvaluationResult> CompleteAsync(EvaluationCase scenario, RunCapture capture,
        Func<Task<bool>> verifyTranscript, Func<Task<string>> judge)
    {
        if (!capture.Terminal || string.IsNullOrWhiteSpace(capture.Answer)) return Failed(capture, "turn");
        var transcriptVerified = false;
        var phase = "replay";
        try
        {
            transcriptVerified = await verifyTranscript();
            if (!transcriptVerified) return Failed(capture, phase, transcriptVerified);
            phase = "judge";
            var judgeJson = await judge();
            var verdict = Assess(scenario, capture, judgeJson);
            var parsed = ParseJudge(judgeJson, out _);
            var failurePhase = verdict.Accepted ? null
                : parsed is null || AssessExecution(scenario, capture).Count == 0 ? "judge" : "turn";
            return new(capture, verdict, transcriptVerified, parsed,
                failurePhase);
        }
        catch (Exception)
        {
            return Failed(capture, phase, transcriptVerified);
        }
    }

    public static EvaluationResult Failed(RunCapture capture, string phase, bool transcriptVerified = false) =>
        new(capture, new(false, [phase switch
        {
            "setup" => "Evaluation setup did not complete.",
            "turn" => "The evaluation turn did not complete.",
            "replay" => "The persisted question and answer could not be verified.",
            _ => "Judge evaluation did not complete."
        }]), transcriptVerified, null, phase);

    public static bool ToolSucceeded(ToolResult tool)
    {
        if (!tool.Success || !string.IsNullOrWhiteSpace(tool.Error)) return false;
        var text = tool.Result.TrimStart();
        if (text.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) || text.StartsWith("HTTP 4", StringComparison.Ordinal)
            || text.StartsWith("HTTP 5", StringComparison.Ordinal) || text.Contains("Output too large to read at once", StringComparison.OrdinalIgnoreCase)) return false;
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
        var reasons = AssessExecution(scenario, run);
        var judge = ParseJudge(judgeJson, out var error);
        if (judge is null) reasons.Add(error!);
        else if (!judge.Accepted || !judge.Grounded || !judge.Complete)
            reasons.Add("Judge rejected the result: " + judge.Reason);
        return new(reasons.Count == 0, reasons.ToArray());
    }

    private static List<string> AssessExecution(EvaluationCase scenario, RunCapture run)
    {
        var reasons = new List<string>();
        if (!run.Terminal) reasons.Add("The turn did not complete.");
        if (string.IsNullOrWhiteSpace(run.Answer)) reasons.Add("The final answer is empty.");
        if (run.Errors.Length > 0) reasons.Add("The run emitted errors.");
        if (run.Tools.Any(tool => !ToolSucceeded(tool))) reasons.Add("At least one tool failed.");
        if (run.Tools.Any(tool => tool.Result.Contains("Output too large to read at once", StringComparison.OrdinalIgnoreCase)))
            reasons.Add("Required tool evidence was offloaded and unavailable to the model.");
        if (run.Tools.Length > scenario.MaxToolCalls) reasons.Add("The tool-call budget was exceeded.");
        if (run.DurationMs > scenario.MaxDurationSeconds * 1000L) reasons.Add("The time budget was exceeded.");
        foreach (var name in scenario.RequiredTools)
            if (!run.Tools.Any(tool => tool.Name == name)) reasons.Add($"Required tool was not called: {name}.");
        foreach (var name in scenario.ForbiddenTools)
            if (run.Tools.Any(tool => tool.Name == name)) reasons.Add($"Forbidden tool was called: {name}.");
        return reasons;
    }

    private static JudgeVerdict? ParseJudge(string judgeJson, out string? error)
    {
        error = null;
        try
        {
            using var document = JsonDocument.Parse(judgeJson);
            var verdict = document.RootElement;
            if (verdict.ValueKind != JsonValueKind.Object || verdict.EnumerateObject().Count() != 4
                || !verdict.TryGetProperty("accepted", out var accepted) || accepted.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !verdict.TryGetProperty("grounded", out var grounded) || grounded.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !verdict.TryGetProperty("complete", out var complete) || complete.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !verdict.TryGetProperty("reason", out var reason) || reason.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(reason.GetString()))
            {
                error = "Judge response did not satisfy the verdict schema.";
                return null;
            }
            return new(accepted.GetBoolean(), grounded.GetBoolean(), complete.GetBoolean(), reason.GetString()!);
        }
        catch (JsonException)
        {
            error = "Judge response was not valid JSON.";
            return null;
        }
    }
}