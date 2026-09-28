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
    private const string Accepted = """{"accepted":true,"grounded":true,"complete":true,"avoidableCalls":0,"avoidableRounds":0,"avoidableSeconds":0,"reason":"Supported by the supplied evidence."}""";

    [Fact]
    public void CompleteRunWithValidJudgeVerdictPasses() => Assert.True(EvaluationGate.Assess(Scenario, Success, Accepted).Accepted);

    [Fact]
    public void OneFailedCallCorrectedByALaterSuccessfulCallPasses()
    {
        var run = Success with { Tools = [new("GetCrawlMaturityEvidence", true, "HTTP 400 BadRequest\n{\"error\":{\"code\":\"InvalidApiVersion\"}}", null),
            new("GetCrawlMaturityEvidence", true, "{}", null)] };
        Assert.True(EvaluationGate.Assess(Scenario, run, Accepted).Accepted);
    }

    [Theory]
    [InlineData("second-failure")]
    [InlineData("never-corrected")]
    [InlineData("other-tool-succeeded")]
    public void MoreThanOneOrAnUncorrectedFailedCallFails(string shape)
    {
        ToolResult failed = new("GetCrawlMaturityEvidence", false, "", "Tool execution failed");
        ToolResult ok = new("GetCrawlMaturityEvidence", true, "{}", null);
        var run = Success with
        {
            Tools = shape switch
            {
                "second-failure" => [failed, ok, failed, ok],
                "never-corrected" => [ok, failed],
                _ => [failed, new("SuggestFollowUp", true, "{}", null)],
            }
        };
        var verdict = EvaluationGate.Assess(Scenario, run, Accepted);
        Assert.False(verdict.Accepted);
        Assert.Contains(verdict.Reasons, reason => reason.Contains("failed", StringComparison.Ordinal));
    }

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
        Assert.Contains("The single exception is zero seats", instructions);
        Assert.Contains("was actually requested and is unavailable", instructions);
        Assert.Contains("Reject invented facts", instructions);
        Assert.Equal("xhigh", root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.True(root.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean());

        using var evidence = JsonDocument.Parse(root.GetProperty("input")[2].GetProperty("content").GetString()!);
        Assert.Equal(Scenario.Question, evidence.RootElement.GetProperty("question").GetString());
        Assert.Equal(Scenario.Rubric, evidence.RootElement.GetProperty("rubric").GetString());
        Assert.Equal(run.Answer, evidence.RootElement.GetProperty("answer").GetString());
        Assert.Equal(run.HostContext, evidence.RootElement.GetProperty("hostContext").GetString());
        Assert.Equal(run.VisibleOutputs[0], evidence.RootElement.GetProperty("visibleOutputs")[0].GetString());
        Assert.Equal(run.Tools[0].Result, evidence.RootElement.GetProperty("tools")[0].GetProperty("result").GetString());
        Assert.Equal(run.Tools[0].Arguments, evidence.RootElement.GetProperty("tools")[0].GetProperty("arguments").GetString());
    }

    [Fact]
    public async Task JudgeReceivesTheEndToEndTimelineAndGradesEfficiency()
    {
        using var handler = new JudgeRequestHandler();
        using var http = new HttpClient(handler);
        var judge = new JudgeClient(http, new JudgeCredential(), new Uri("https://example.openai.azure.com/"), "test-model");
        // Two overlapping reads (round 1), a later read (round 2) and a gap of model time between and after them.
        var run = Success with
        {
            DurationMs = 10000,
            FirstTokenMs = 7500,
            ThrottleNotices = 1,
            AgentProfile = "synthetic-model, reasoning effort xhigh",
            Tools =
            [
                new("QueryAzure", true, "{}", null, "{\"url\":\"/a\"}", 1000, 3000),
                new("QueryAzure", true, "HTTP 403 Forbidden\n{}", null, "{\"url\":\"/b\"}", 1500, 2500),
                new("QueryAzure", true, "{}", null, "{\"sql\":\"SELECT 1\"}", 4000, 5000)
            ]
        };
        await judge.AssessAsync(Scenario with { MaxToolCalls = 12, MaxDurationSeconds = 240 }, run, CancellationToken.None);
        using var request = JsonDocument.Parse(handler.RequestJson);
        var root = request.RootElement;
        var efficiency = root.GetProperty("input")[1].GetProperty("content").GetString()!;
        Assert.Contains("whole end-to-end session for efficiency", efficiency);
        Assert.Contains("Cost Management query and forecast calls running one after another", efficiency);
        Assert.Contains("The host derives the 1-5 efficiency score from these counts", efficiency);
        Assert.Contains("a search plus reading one of its results is one lookup", efficiency);
        var schema = root.GetProperty("text").GetProperty("format").GetProperty("schema");
        Assert.Equal(["accepted", "grounded", "complete", "avoidableCalls", "avoidableRounds", "avoidableSeconds", "reason"],
            schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.False(schema.GetProperty("properties").TryGetProperty("efficiencyScore", out _));

        using var evidence = JsonDocument.Parse(root.GetProperty("input")[2].GetProperty("content").GetString()!);
        var session = evidence.RootElement.GetProperty("session");
        Assert.Equal("synthetic-model, reasoning effort xhigh", session.GetProperty("agent").GetString());
        Assert.Equal(10, session.GetProperty("totalSeconds").GetDouble());
        Assert.Equal(7.5, session.GetProperty("firstTokenSeconds").GetDouble());
        Assert.Equal(3, session.GetProperty("toolCalls").GetInt32());
        Assert.Equal(1, session.GetProperty("failedToolCalls").GetInt32());
        Assert.Equal(2, session.GetProperty("rounds").GetInt32());
        Assert.Equal(2, session.GetProperty("maxConcurrentTools").GetInt32());
        Assert.Equal(3, session.GetProperty("toolWallSeconds").GetDouble());
        Assert.Equal(7, session.GetProperty("modelSeconds").GetDouble());
        Assert.Equal(1, session.GetProperty("serviceThrottleNotices").GetInt32());
        Assert.Equal(12, session.GetProperty("budgets").GetProperty("maxToolCalls").GetInt32());
        Assert.Equal(240, session.GetProperty("budgets").GetProperty("maxDurationSeconds").GetInt32());
        var tools = evidence.RootElement.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal([1, 2, 3], tools.Select(tool => tool.GetProperty("order").GetInt32()));
        Assert.Equal([1, 1, 2], tools.Select(tool => tool.GetProperty("round").GetInt32()));
        Assert.Equal(["/a", "/b", "SELECT 1"], tools.Select(tool => JsonDocument.Parse(tool.GetProperty("arguments").GetString()!).RootElement.EnumerateObject().First().Value.GetString()));
        Assert.Equal([true, false, true], tools.Select(tool => tool.GetProperty("succeeded").GetBoolean()));
        Assert.Equal(1.5, tools[1].GetProperty("startSeconds").GetDouble());
        Assert.Equal(2.5, tools[1].GetProperty("endSeconds").GetDouble());
        Assert.Equal(1, tools[1].GetProperty("durationSeconds").GetDouble());
    }

    [Fact]
    public void TimelineOrdersCallsByStartAndKeepsUntimedCallsVisible()
    {
        var timeline = SessionTimeline.From(Success with
        {
            DurationMs = 5000,
            Tools =
            [
                new("Later", true, "{}", null, "", 3000, 4000),
                new("Untimed", true, "{}", null),
                new("First", true, "{}", null, "", 500, 1000),
                new("Adjacent", true, "{}", null, "", 1000, 2000)
            ]
        });
        Assert.Equal(["First", "Adjacent", "Later", "Untimed"], timeline.Calls.Select(call => call.Tool.Name));
        Assert.Equal([1, 2, 3, 0], timeline.Calls.Select(call => call.Round));
        Assert.Equal(3, timeline.Rounds);
        Assert.Equal(1, timeline.MaxConcurrentTools);
        Assert.Equal(2.5, timeline.ToolWallSeconds);
        Assert.Equal(2.5, timeline.ModelSeconds);
        Assert.Null(timeline.Calls[3].StartSeconds);
        Assert.Equal(4, timeline.ToolCalls);
    }

    // A 12-call, 160-second session, like the Idle sweep that four documentation lookups failed.
    private static readonly RunCapture TwelveCalls = Success with
    {
        DurationMs = 160000,
        Tools = [..Enumerable.Repeat(new ToolResult("GetCrawlMaturityEvidence", true, "{}", null), 12)]
    };

    [Theory]
    [InlineData(0, 0, 0, 5)]
    [InlineData(2, 2, 20, 4)]
    [InlineData(3, 3, 40, 3)]
    [InlineData(5, 5, 60, 3)]
    [InlineData(6, 5, 50, 2)]
    [InlineData(3, 3, 80, 2)]
    [InlineData(8, 8, 60, 1)]
    public void TheHostScoresTheJudgesCountedWasteWithTheRubricAnchors(int calls, int rounds, double seconds, int score)
    {
        var verdict = EvaluationGate.Assess(Scenario, TwelveCalls,
            $$"""{"accepted":true,"grounded":true,"complete":true,"avoidableCalls":{{calls}},"avoidableRounds":{{rounds}},"avoidableSeconds":{{seconds}},"reason":"Calls 9-12 repeated a lookup."}""");
        Assert.Equal(score, verdict.Judge!.EfficiencyScore);
        Assert.Equal(score >= EvaluationGate.MinimumEfficiencyScore, verdict.Judge.Efficient);
        Assert.Equal(score >= EvaluationGate.MinimumEfficiencyScore, verdict.Accepted);
        if (!verdict.Accepted)
            Assert.Contains($"Judge rated the session inefficient ({score}/5): Calls 9-12 repeated a lookup.", verdict.Reasons);
    }

    [Fact]
    public void WasteThatRoughlyDoublesAShortSessionFailsEvenWithFewCalls() =>
        Assert.Equal(2, EvaluationGate.EfficiencyScore(avoidableCalls: 1, avoidableRounds: 1, avoidableSeconds: 5, toolCalls: 2, totalSeconds: 20));

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
    [InlineData("{\"accepted\":true,\"grounded\":false,\"complete\":true,\"avoidableCalls\":0,\"avoidableRounds\":0,\"avoidableSeconds\":0,\"reason\":\"Missing evidence\"}")]
    [InlineData("{\"accepted\":\"true\",\"grounded\":true,\"complete\":true,\"avoidableCalls\":0,\"avoidableRounds\":0,\"avoidableSeconds\":0,\"reason\":\"Invalid type\"}")]
    [InlineData("{\"accepted\":true,\"grounded\":true,\"complete\":true,\"avoidableCalls\":0,\"avoidableRounds\":0,\"avoidableSeconds\":0,\"reason\":\"\"}")]
    [InlineData("{\"accepted\":true,\"grounded\":true,\"complete\":true,\"reason\":\"Legacy verdict without efficiency\"}")]
    [InlineData("{\"accepted\":true,\"grounded\":true,\"complete\":true,\"efficient\":true,\"efficiencyScore\":5,\"reason\":\"Judge-authored score\"}")]
    [InlineData("{\"accepted\":true,\"grounded\":true,\"complete\":true,\"avoidableCalls\":\"0\",\"avoidableRounds\":0,\"avoidableSeconds\":0,\"reason\":\"Invalid type\"}")]
    [InlineData("{\"accepted\":true,\"grounded\":true,\"complete\":true,\"avoidableCalls\":-1,\"avoidableRounds\":0,\"avoidableSeconds\":0,\"reason\":\"Negative\"}")]
    [InlineData("{\"accepted\":true,\"grounded\":true,\"complete\":true,\"avoidableCalls\":0.5,\"avoidableRounds\":0,\"avoidableSeconds\":0,\"reason\":\"Not an integer\"}")]
    [InlineData("{\"accepted\":true,\"grounded\":true,\"complete\":true,\"avoidableCalls\":2,\"avoidableRounds\":0,\"avoidableSeconds\":0,\"reason\":\"More calls than were made\"}")]
    [InlineData("{\"accepted\":true,\"grounded\":true,\"complete\":true,\"avoidableCalls\":0,\"avoidableRounds\":0,\"avoidableSeconds\":-1,\"reason\":\"Negative seconds\"}")]
    [InlineData("{\"accepted\":true,\"grounded\":true,\"complete\":true,\"avoidableCalls\":0,\"avoidableRounds\":0,\"avoidableSeconds\":0,\"efficiencyScore\":5,\"reason\":\"Extra field\"}")]
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