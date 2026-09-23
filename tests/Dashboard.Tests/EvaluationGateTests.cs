using LiveEvaluations;
using Azure.Core;
using System.Net;
using System.Net.Http.Json;
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
    public async Task JudgeSeparatesInventoryFromBillingWithoutDroppingKnownEvidence()
    {
        using var handler = new JudgeRequestHandler();
        using var http = new HttpClient(handler);
        var judge = new JudgeClient(http, new JudgeCredential(), new Uri("https://example.openai.azure.com/"), "test-model");
        var run = Success with
        {
            HostContext = "Synthetic connection context",
            VisibleOutputs = ["Synthetic chart"],
            Tools = [new("QueryGraph", true, "{\"prepaidUnits\":{\"enabled\":20},\"consumedUnits\":5}", null,
                "{\"url\":\"https://graph.microsoft.com/v1.0/subscribedSkus\"}")]
        };

        Assert.Equal(Accepted, await judge.AssessAsync(Scenario, run, CancellationToken.None));
        using var request = JsonDocument.Parse(handler.RequestJson);
        var root = request.RootElement;
        var instructions = root.GetProperty("input")[0].GetProperty("content").GetString()!;
        Assert.Contains("Compare every requested metric across the whole answer", instructions);
        Assert.Contains("do not silently correct them from the evidence", instructions);
        Assert.Contains("valid only when they denote the same interval", instructions);
        Assert.Contains("Moving an exclusive-end label to the previous included day changes the interval", instructions);
        Assert.Contains("not verified purchased or paid quantities", instructions);
        Assert.Contains("Require every available inventory and assignment count", instructions);
        Assert.Contains("Reject unsupported paid-seat or actual-waste claims", instructions);
        Assert.Contains("If invoice or contract evidence supplies a purchased quantity or rate, require it", instructions);
        Assert.Contains("unavailable required activity report still leaves an activity task incomplete", instructions);
        Assert.Contains("Reject invented facts", instructions);
        Assert.Equal("xhigh", root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.True(root.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean());

        using var evidence = JsonDocument.Parse(root.GetProperty("input")[1].GetProperty("content").GetString()!);
        Assert.Equal(Scenario.Question, evidence.RootElement.GetProperty("question").GetString());
        Assert.Equal(Scenario.Rubric, evidence.RootElement.GetProperty("rubric").GetString());
        Assert.Equal(run.Answer, evidence.RootElement.GetProperty("Answer").GetString());
        Assert.Equal(run.HostContext, evidence.RootElement.GetProperty("hostContext").GetString());
        Assert.Equal(run.VisibleOutputs[0], evidence.RootElement.GetProperty("visibleOutputs")[0].GetString());
        Assert.Equal(run.Tools[0].Result, evidence.RootElement.GetProperty("Tools")[0].GetProperty("Result").GetString());
        Assert.Equal(run.Tools[0].Arguments, evidence.RootElement.GetProperty("Tools")[0].GetProperty("Arguments").GetString());
    }

    [Theory]
    [InlineData("replay", false)]
    [InlineData("judge", true)]
    public void BoundaryFailuresKeepCompletedExecutionAndNeverPass(string phase, bool transcriptVerified)
    {
        var state = new EvaluationRunState
        {
            Phase = phase,
            Terminal = true,
            TranscriptVerified = transcriptVerified,
            DurationMs = 1234,
            FirstTokenMs = 321
        };
        state.Tools.AddRange(Success.Tools);
        state.Answers.Add("message", Success.Answer);
        state.VisibleOutputs.Add("{\"type\":\"chart\"}");
        state.RecordFailure(new HttpRequestException("Synthetic transport failure"));

        var capture = state.Capture();
        Assert.Equal(Success.Answer, capture.Answer);
        Assert.Equal(Success.Tools, capture.Tools);
        Assert.True(capture.Terminal);
        Assert.Equal(1234, capture.DurationMs);
        Assert.Equal(321, capture.FirstTokenMs);
        Assert.Single(capture.VisibleOutputs!);
        Assert.Equal(transcriptVerified, state.TranscriptVerified);
        Assert.Equal(phase, state.Failure!.Phase);
        Assert.Equal(nameof(HttpRequestException), state.Failure.Type);
        Assert.False(EvaluationGate.Assess(Scenario, capture, Accepted).Accepted);
    }

    [Fact]
    public void InterruptedToolsRemainIncompleteWhenTheStreamFails()
    {
        var state = new EvaluationRunState { Phase = "chat" };
        state.PendingTools.Add("synthetic-pending-call");
        state.RecordFailure(new OperationCanceledException("Synthetic deadline"));
        Assert.False(state.Capture().Terminal);
        Assert.Contains("One or more tools have no terminal result.", state.Capture().Errors);
        Assert.False(EvaluationGate.Assess(Scenario, state.Capture(), Accepted).Accepted);
    }

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

    private sealed class JudgeCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("synthetic-test-token", DateTimeOffset.MaxValue);

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class JudgeRequestHandler : HttpMessageHandler
    {
        public string RequestJson { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/openai/v1/responses", request.RequestUri!.AbsolutePath);
            RequestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    status = "completed",
                    output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = Accepted } } } }
                })
            };
        }
    }
}