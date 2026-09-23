using System.Text.Json;
using AzureFinOps.Dashboard.Endpoints;
using GitHub.Copilot;
using LiveEvaluations;

namespace Dashboard.Tests;

public sealed class EvaluationGateTests
{
    [Fact]
    public void WorkflowReportsRedactCredentialsAndResourceCoordinates()
    {
        var text = "Bearer synthetic-secret api_key=synthetic-key https://example.test/?sig=synthetic-sas /subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/synthetic-private 192.0.2.1";
        var redacted = ReportRedaction.Apply(text);
        Assert.DoesNotContain("synthetic-secret", redacted);
        Assert.DoesNotContain("synthetic-key", redacted);
        Assert.DoesNotContain("synthetic-sas", redacted);
        Assert.DoesNotContain("synthetic-private", redacted);
        Assert.DoesNotContain("192.0.2.1", redacted);
    }

    [Theory]
    [InlineData(1500)]
    [InlineData(6000)]
    public void DiagnosticTruncationCannotSplitCredentialsBeforeRedaction(int limit)
    {
        var token = "eyJ" + new string('a', 20) + "." + new string('b', 20) + "." + new string('c', 20);
        var diagnostic = new string('x', limit - 8) + " " + token + " " + new string('y', limit);

        var result = EvaluationGate.RedactAndTruncate(diagnostic, limit, ReportRedaction.Apply);

        Assert.DoesNotContain("eyJ", result);
        Assert.DoesNotContain(token, result);
        Assert.Equal(ReportRedaction.Apply(diagnostic)[..limit] + "\n[truncated]", result);
    }

    [Fact]
    public void DiagnosticLimitsApplyAfterFullRedactionIncludingTheErrorText()
    {
        var diagnostic = "api_key=" + new string('s', 7000) + " synthetic result";
        var result = EvaluationGate.RedactAndTruncate(diagnostic, 1500, ReportRedaction.Apply);

        Assert.Equal("api_key=[redacted] synthetic result", result);
        Assert.DoesNotContain("[truncated]", result);
    }

    [Theory]
    [InlineData("Synthetic successful text output.")]
    [InlineData("name,cost\nCompute,25")]
    [InlineData("__SCRIPT_READY__:synthetic-id:example.ps1:3:powershell:Example")]
    public void SuccessfulNonJsonToolOutputRemainsSupported(string output) =>
        Assert.True(EvaluationGate.ToolSucceeded(new("SyntheticTool", true, output, null)));

    [Theory]
    [InlineData("{\"ok\":false,\"error\":\"Missing access\"}")]
    [InlineData("{\"results\":[{\"success\":false}]}")]
    [InlineData("{\"source\":{\"Success\":false}}")]
    [InlineData("HTTP 403 Forbidden\n{}")]
    public void SdkSuccessDoesNotHideAReportedToolFailure(string body)
    {
        var run = Success with { Tools = [new("GetCrawlMaturityEvidence", true, body, null)] };
        Assert.False(EvaluationGate.Assess(Scenario, run, Accepted).Accepted);
    }

    private static readonly EvaluationCase Scenario = new("crawl", "Assess Crawl maturity.", "Ground every score in evidence.",
        ["GetCrawlMaturityEvidence"], ["QueryAzure"]);
    private static readonly RunCapture Success = new("Evidence-backed answer", [new("GetCrawlMaturityEvidence", true, "{}", null)],
        true, [], 1000, 100);
    private const string Accepted = """{"accepted":true,"grounded":true,"complete":true,"reason":"Supported by the supplied evidence."}""";

    [Fact]
    public void CompleteRunWithValidJudgeVerdictPasses() => Assert.True(EvaluationGate.Assess(Scenario, Success, Accepted).Accepted);

    [Theory]
    [InlineData("empty")]
    [InlineData("incomplete")]
    [InlineData("failed-tool")]
    [InlineData("validation-error")]
    [InlineData("missing-tool")]
    [InlineData("forbidden-tool")]
    [InlineData("timeout")]
    [InlineData("run-error")]
    public void PositiveJudgeCannotOverrideExecutionFailure(string failure)
    {
        var run = failure switch
        {
            "empty" => Success with { Answer = "" },
            "incomplete" => Success with { Terminal = false },
            "failed-tool" => Success with { Tools = [new("GetCrawlMaturityEvidence", false, "", "Tool execution failed")] },
            "validation-error" => Success with { Tools = [new("GetCrawlMaturityEvidence", true, "Error: invalid input", null)] },
            "missing-tool" => Success with { Tools = [] },
            "forbidden-tool" => Success with { Tools = [..Success.Tools, new("QueryAzure", true, "{}", null)] },
            "timeout" => Success with { DurationMs = 181000 },
            _ => Success with { Errors = ["Transport failed"] }
        };
        Assert.False(EvaluationGate.Assess(Scenario, run, Accepted).Accepted);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"accepted\":true}")]
    [InlineData("{\"accepted\":true,\"grounded\":false,\"complete\":true,\"reason\":\"Missing evidence\"}")]
    [InlineData("{\"accepted\":\"true\",\"grounded\":true,\"complete\":true,\"reason\":\"Invalid type\"}")]
    [InlineData("{\"accepted\":true,\"grounded\":true,\"complete\":true,\"reason\":\"\"}")]
    public void MissingMalformedOrNegativeJudgeFailsClosed(string judge) => Assert.False(EvaluationGate.Assess(Scenario, Success, judge).Accepted);

    [Fact]
    public void TranscriptVerificationUsesDecodedExactTextAndCoalescedAssistantMessages()
    {
        const string question = "What's the cost of \"Compute\" in 日本語?";
        const string firstAnswer = "The \"Compute\" cost is USD 25.\nIt isn't a finalized bill.";
        const string followUp = "Would you like a breakdown?";
        var messages = SessionEndpoints.BuildTranscript([
            new UserMessageEvent { Data = new() { Content = "[Synthetic connection context]\n" + question } },
            new AssistantMessageEvent { Data = new() { MessageId = "answer", Content = firstAnswer } },
            new AssistantMessageEvent { Data = new() { MessageId = "follow-up", Content = followUp } }
        ], 101);
        var transcript = JsonSerializer.Serialize(new { messages, pendingChanges = Array.Empty<object>() });

        Assert.DoesNotContain(question, transcript);
        Assert.True(EvaluationGate.VerifyTranscript(transcript, question, firstAnswer + "\n\n" + followUp));
        Assert.False(EvaluationGate.VerifyTranscript(transcript, question, firstAnswer));
        Assert.False(EvaluationGate.VerifyTranscript(transcript, question, followUp));
    }

    [Theory]
    [InlineData("missing-answer")]
    [InlineData("empty-answer")]
    [InlineData("changed-answer")]
    [InlineData("truncated-answer")]
    [InlineData("changed-question")]
    [InlineData("question-substring")]
    [InlineData("question-in-assistant")]
    [InlineData("answer-in-system")]
    [InlineData("answer-before-question")]
    [InlineData("answer-from-another-turn")]
    [InlineData("terminal-error")]
    public void TranscriptMustPersistTheExactQuestionAndAnswerInTheSameTurn(string failure)
    {
        (string Role, string Content) user = ("user", Scenario.Question);
        (string Role, string Content) assistant = ("assistant", Success.Answer);
        (string Role, string Content)[] messages = failure switch
        {
            "missing-answer" => [user],
            "empty-answer" => [user, ("assistant", "")],
            "changed-answer" => [user, ("assistant", Success.Answer.ToUpperInvariant())],
            "truncated-answer" => [user, ("assistant", Success.Answer[..5])],
            "changed-question" => [("user", Scenario.Question.ToUpperInvariant()), assistant],
            "question-substring" => [("user", "Prefix " + Scenario.Question), assistant],
            "question-in-assistant" => [("assistant", Scenario.Question), assistant],
            "answer-in-system" => [user, ("system", Success.Answer)],
            "answer-before-question" => [assistant, user],
            "answer-from-another-turn" => [user, ("user", "A different question"), assistant],
            _ => [user, assistant, ("system", "Synthetic terminal error")]
        };
        var transcript = JsonSerializer.Serialize(new
        {
            messages = messages.Select(message => new { role = message.Role, content = message.Content })
        });

        Assert.False(EvaluationGate.VerifyTranscript(transcript, Scenario.Question, Success.Answer));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"messages\":null}")]
    [InlineData("{\"messages\":{}}")]
    [InlineData("{\"messages\":[null,{}]}")]
    [InlineData("{\"messages\":[{\"role\":1,\"content\":true},{}]}")]
    public void MalformedTranscriptFailsClosed(string transcript) =>
        Assert.False(EvaluationGate.VerifyTranscript(transcript, Scenario.Question, Success.Answer));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void EmptyExpectedAnswerCannotVerifyAQuestionOnlyReplay(string answer)
    {
        var transcript = JsonSerializer.Serialize(new
        {
            messages = new[] { new { role = "user", content = Scenario.Question }, new { role = "assistant", content = answer } }
        });
        Assert.False(EvaluationGate.VerifyTranscript(transcript, Scenario.Question, answer));
    }

    [Fact]
    public async Task CompletedEvaluationRetainsAValidatedJudge()
    {
        var result = await EvaluationGate.CompleteAsync(Scenario, Success,
            () => Task.FromResult(true), () => Task.FromResult(Accepted));

        Assert.True(result.Verdict.Accepted);
        Assert.True(result.TranscriptVerified);
        Assert.Null(result.FailurePhase);
        Assert.NotNull(result.Judge);
        Assert.True(result.Judge.Accepted);
        Assert.True(result.Judge.Grounded);
        Assert.True(result.Judge.Complete);
        Assert.Same(Success, result.Capture);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json with synthetic-private-payload")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"refusal\":\"synthetic-private-payload\"}")]
    [InlineData("{\"accepted\":true}")]
    [InlineData("{\"accepted\":\"true\",\"grounded\":true,\"complete\":true,\"reason\":\"synthetic-private-payload\"}")]
    [InlineData("{\"accepted\":true,\"grounded\":true,\"complete\":true,\"reason\":\"\"}")]
    [InlineData("{\"accepted\":true,\"grounded\":true,\"complete\":true,\"reason\":\"Valid\",\"extra\":\"synthetic-private-payload\"}")]
    [InlineData("{\"accepted\":true,\"accepted\":true,\"complete\":true,\"reason\":\"synthetic-private-payload\"}")]
    public async Task MalformedOrRefusedJudgePreservesCompletedCaptureAndFailsClosed(string judge)
    {
        var capture = Success with { VisibleOutputs = ["synthetic chart"], HostContext = "synthetic host context" };
        var result = await EvaluationGate.CompleteAsync(Scenario, capture,
            () => Task.FromResult(true), () => Task.FromResult(judge));

        Assert.False(result.Verdict.Accepted);
        Assert.True(result.TranscriptVerified);
        Assert.Equal("judge", result.FailurePhase);
        Assert.Null(result.Judge);
        Assert.Same(capture, result.Capture);
        Assert.DoesNotContain("synthetic-private-payload", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JudgeFailureOrCancellationPreservesTheCompletedTurn(bool cancelled)
    {
        var capture = Success with
        {
            Tools = [.. Success.Tools, new("SyntheticTool", false, "Synthetic failed result", "Synthetic tool error")],
            Errors = ["Synthetic SSE error"],
            VisibleOutputs = ["Synthetic chart"]
        };
        var result = await EvaluationGate.CompleteAsync(Scenario, capture, () => Task.FromResult(true),
            () => Task.FromException<string>(cancelled
                ? new OperationCanceledException("synthetic-private-payload")
                : new HttpRequestException("synthetic-private-payload")));

        Assert.False(result.Verdict.Accepted);
        Assert.True(result.TranscriptVerified);
        Assert.Equal("judge", result.FailurePhase);
        Assert.Null(result.Judge);
        Assert.Same(capture, result.Capture);
        Assert.True(result.Capture.Terminal);
        Assert.Equal(Success.Answer, result.Capture.Answer);
        Assert.Equal(2, result.Capture.Tools.Length);
        Assert.Equal(1000, result.Capture.DurationMs);
        Assert.Equal(100, result.Capture.FirstTokenMs);
        Assert.Equal(capture.Errors, result.Capture.Errors);
        Assert.DoesNotContain("synthetic-private-payload", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplayFailurePreservesCaptureWithoutCallingTheJudge(bool throws)
    {
        var judgeCalls = 0;
        var result = await EvaluationGate.CompleteAsync(Scenario, Success,
            () => throws ? Task.FromException<bool>(new JsonException("synthetic-private-payload")) : Task.FromResult(false),
            () => { judgeCalls++; return Task.FromResult(Accepted); });

        Assert.False(result.Verdict.Accepted);
        Assert.False(result.TranscriptVerified);
        Assert.Equal("replay", result.FailurePhase);
        Assert.Null(result.Judge);
        Assert.Same(Success, result.Capture);
        Assert.Equal(0, judgeCalls);
        Assert.DoesNotContain("synthetic-private-payload", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task ValidJudgeRejectionIsRetainedWithoutDiscardingTheTurn()
    {
        const string rejected = """{"accepted":false,"grounded":true,"complete":false,"reason":"Missing scoped evidence."}""";
        var result = await EvaluationGate.CompleteAsync(Scenario, Success,
            () => Task.FromResult(true), () => Task.FromResult(rejected));

        Assert.False(result.Verdict.Accepted);
        Assert.Equal("judge", result.FailurePhase);
        Assert.NotNull(result.Judge);
        Assert.False(result.Judge.Accepted);
        Assert.Equal("Missing scoped evidence.", result.Judge.Reason);
        Assert.Same(Success, result.Capture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingTerminalOrAnswerIsATurnFailure(bool emptyAnswer)
    {
        var capture = emptyAnswer ? Success with { Answer = "" } : Success with { Terminal = false };
        var result = await EvaluationGate.CompleteAsync(Scenario, capture,
            () => throw new InvalidOperationException("Replay must not run."),
            () => throw new InvalidOperationException("Judge must not run."));

        Assert.False(result.Verdict.Accepted);
        Assert.False(result.TranscriptVerified);
        Assert.Equal("turn", result.FailurePhase);
        Assert.Null(result.Judge);
        Assert.Same(capture, result.Capture);
    }

    [Fact]
    public async Task PositiveJudgeCannotOverrideAToolFailureInTheCompletedResult()
    {
        var capture = Success with { Tools = [new("GetCrawlMaturityEvidence", true, "Error: synthetic failure", null)] };
        var result = await EvaluationGate.CompleteAsync(Scenario, capture,
            () => Task.FromResult(true), () => Task.FromResult(Accepted));

        Assert.False(result.Verdict.Accepted);
        Assert.True(result.TranscriptVerified);
        Assert.Equal("turn", result.FailurePhase);
        Assert.NotNull(result.Judge);
        Assert.True(result.Judge.Accepted);
        Assert.Same(capture, result.Capture);
    }
}