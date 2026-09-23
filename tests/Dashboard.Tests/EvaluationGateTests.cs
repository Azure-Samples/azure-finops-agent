using LiveEvaluations;
using System.Text.Json;

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
    [InlineData("{\"ok\":false,\"error\":\"Missing access\"}")]
    [InlineData("{\"results\":[{\"success\":false}]}")]
    [InlineData("{\"source\":{\"Success\":false}}")]
    [InlineData("HTTP 403 Forbidden\n{}")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"status\":\"failed\"}")]
    [InlineData("{\"results\":[{\"outcome\":\"cancelled\"}]}")]
    [InlineData("{\"_finops\":{\"ok\":false}}")]
    [InlineData("{\"sourceEvidence\":{\"error\":\"Missing access\"}}")]
    [InlineData("HTTP 200 OK\nCurrent UTC time: 2026-09-23T12:00:00Z\n{\"ok\":false}")]
    public void SdkSuccessDoesNotHideAReportedToolFailure(string body)
    {
        var run = Success with { Tools = [new("GetCrawlMaturityEvidence", true, body, null)] };
        Assert.False(EvaluationGate.Assess(Scenario, run, Accepted).Accepted);
    }

    [Theory]
    [InlineData("{\"status\":\"unknown\",\"quota\":null}")]
    [InlineData("{\"status\":\"awaitingApproval\"}")]
    [InlineData("{\"complete\":false,\"partial\":true}")]
    public void UnknownOrPendingEvidenceStillRequiresTheJudgeRatherThanBeingAnExecutionFailure(string body) =>
        Assert.True(EvaluationGate.ToolSucceeded(new("CheckComputeFeasibility", true, body, null)));

    private static readonly EvaluationCase Scenario = new("crawl", "Assess Crawl maturity.", "Ground every score in evidence.",
        ["GetCrawlMaturityEvidence"], ["QueryAzure"]);
    private static readonly RunCapture Success = new("Evidence-backed answer", [new("GetCrawlMaturityEvidence", true, "{}", null)],
        true, [], 1000, 100);
    private const string Accepted = """{"accepted":true,"grounded":true,"complete":true,"reason":"Supported by the supplied evidence."}""";

    [Fact]
    public void CompleteRunWithValidJudgeVerdictPasses() => Assert.True(EvaluationGate.Assess(Scenario, Success, Accepted).Accepted);

    [Fact]
    public void CandidateRequiresBothRunnerAndApplicationBuiltAtTheExpectedRevision()
    {
        var sha = new string('a', 40);
        var version = "1.0.0+" + sha;
        Assert.True(EvaluationGate.MatchesCandidateRevision(sha, version, version));
        Assert.False(EvaluationGate.MatchesCandidateRevision(sha, version, "1.0.0+" + new string('b', 40)));
        Assert.False(EvaluationGate.MatchesCandidateRevision(sha, "1.0.0", version));
        Assert.False(EvaluationGate.MatchesCandidateRevision(sha, version, null));
        Assert.False(EvaluationGate.MatchesCandidateRevision(sha));
        Assert.False(EvaluationGate.MatchesCandidateRevision("not-a-sha", version, version));
        Assert.False(EvaluationGate.MatchesCandidateRevision(new string('z', 40), "1.0.0+" + new string('z', 40)));
    }

    [Fact]
    public void ReplayRequiresTheExactDecodedQuestionAndEveryCompletedAnswerMessage()
    {
        const string question = "What does \"USD\" mean for 2 < 3?";
        const string answer = "First answer.\n\nA separate follow-up.";
        var transcript = JsonSerializer.Serialize(new
        {
            messages = new[]
            {
                new { role = "user", content = question },
                new { role = "assistant", content = "" },
                new { role = "assistant", content = "First answer." },
                new { role = "assistant", content = "A separate follow-up." }
            }
        });
        Assert.True(EvaluationGate.TranscriptMatches(transcript, question, answer));
        Assert.False(EvaluationGate.TranscriptMatches(transcript, question, "First answer."));
        Assert.False(EvaluationGate.TranscriptMatches(transcript, "A different question", answer));
    }

    [Theory]
    [InlineData("{\"messages\":[{\"role\":\"user\",\"content\":\"question\"}]}")]
    [InlineData("{\"messages\":[{\"role\":\"user\",\"content\":\"question\"},{\"role\":\"assistant\",\"content\":\"partial\"}]}")]
    [InlineData("{\"messages\":[{\"role\":\"assistant\",\"content\":\"answer\"},{\"role\":\"user\",\"content\":\"question\"}]}")]
    [InlineData("{\"messages\":[{\"role\":\"user\",\"content\":\"question\"},{\"role\":\"user\",\"content\":\"question\"},{\"role\":\"assistant\",\"content\":\"answer\"}]}")]
    [InlineData("{\"messages\":[{\"role\":\"user\",\"content\":\"question\"},{\"role\":\"assistant\",\"content\":\"answer\"},{\"role\":\"system\",\"content\":\"failure\",\"terminalStatus\":\"error\"}]}")]
    [InlineData("{\"messages\":[{\"role\":\"user\",\"content\":\"question\"},null]}")]
    [InlineData("{\"messages\":[{\"role\":\"user\",\"content\":\"question\"},{\"role\":\"assistant\",\"content\":42}]}")]
    [InlineData("{\"messages\":\"question answer\"}")]
    [InlineData("{\"error\":\"question answer\"}")]
    [InlineData("[\"question\",\"answer\"]")]
    [InlineData("not json")]
    public void MissingIncompleteOrMalformedReplayCannotPass(string transcript) =>
        Assert.False(EvaluationGate.TranscriptMatches(transcript, "question", "answer"));

    [Fact]
    public void InvalidJudgeRetainsExecutionDiagnosticsWithoutAParsedVerdict()
    {
        var verdict = EvaluationGate.Assess(Scenario, Success with { Errors = ["Replay failed."] }, "{}");
        Assert.False(verdict.Accepted);
        Assert.Null(verdict.Judge);
        Assert.Contains("The run emitted errors.", verdict.Reasons);
        Assert.Contains("Judge response did not satisfy the verdict schema.", verdict.Reasons);
        Assert.NotNull(EvaluationGate.Assess(Scenario, Success, Accepted).Judge);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("incomplete")]
    [InlineData("failed-tool")]
    [InlineData("validation-error")]
    [InlineData("missing-tool")]
    [InlineData("forbidden-tool")]
    [InlineData("timeout")]
    [InlineData("invalid-duration")]
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
            "invalid-duration" => Success with { DurationMs = -1 },
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
}