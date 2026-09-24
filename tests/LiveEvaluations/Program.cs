using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Core;
using Azure.Identity;
using AzureFinOps.Dashboard.AI;
using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Endpoints;
using AzureFinOps.Dashboard.Jobs;
using AzureFinOps.Dashboard.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LiveEvaluations;

internal static class Program
{
    private static async Task<int> Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "finops-live-eval-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("COPILOT_HOME", root);
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", null);
        var state = new EvaluationRunState();
        try { return await RunAsync(state); }
        catch (Exception exception)
        {
            state.RecordFailure(exception);
            Console.Error.WriteLine($"Live evaluation {state.Phase} failed: {exception.GetType().Name}");
            await SaveCapturedResultAsync(state, new Verdict(false, ["Live evaluation did not complete."]));
            return 1;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task<int> RunAsync(EvaluationRunState state)
    {
        var expectedSha = Environment.GetEnvironmentVariable("EVAL_EXPECTED_SHA");
        if (expectedSha is not null && !EvaluationGate.MatchesCandidateRevision(expectedSha,
                typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                typeof(CopilotSessionFactory).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion))
            throw new InvalidOperationException("Candidate binaries do not match EVAL_EXPECTED_SHA; rebuild the candidate.");
        var endpoint = Environment.GetEnvironmentVariable("EVAL_MODEL_ENDPOINT") ?? throw new InvalidOperationException("Model endpoint is required.");
        var model = Environment.GetEnvironmentVariable("EVAL_MODEL") ?? "gpt-6-luna";
        var question = Environment.GetEnvironmentVariable("EVAL_QUESTION") ?? throw new InvalidOperationException("Question is required.");
        var maxDurationSeconds = int.TryParse(Environment.GetEnvironmentVariable("EVAL_MAX_DURATION_SECONDS"), out var seconds) ? seconds : 600;
        var maxToolCalls = int.TryParse(Environment.GetEnvironmentVariable("EVAL_MAX_TOOL_CALLS"), out var toolLimit) ? toolLimit : 30;
        if (maxDurationSeconds is < 1 or > 1200 || maxToolCalls is < 1 or > 100) throw new InvalidOperationException("Invalid evaluation limits.");
        var subscriptions = (Environment.GetEnvironmentVariable("EVAL_SUBSCRIPTION_IDS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (subscriptions.Length is < 1 or > 10 || subscriptions.Any(subscription => !Guid.TryParse(subscription, out _)))
            throw new InvalidOperationException("Provide 1-10 evaluation subscription IDs.");
        state.Subscriptions = subscriptions;
        var scopes = subscriptions.Select((subscription, index) => new { id = subscription, name = "Evaluation subscription " + (index + 1) }).ToArray();
        var ownerOid = Guid.NewGuid().ToString();
        var tenant = Environment.GetEnvironmentVariable("EVAL_TENANT_ID")?.Trim() ?? "";
        if (!Guid.TryParse(tenant, out _)) throw new InvalidOperationException("Provide the evaluation tenant ID.");
        var owner = PersistentIdentity.DeriveUserId(tenant, ownerOid);
        // The subscription selects its cached CLI user; tenant-only selection uses the default CLI user.
        // The default 13-second CLI timeout is shorter than a slow az startup on some hosts and
        // turned transient CLI latency into failed evaluations.
        TokenCredential credential = new AzureCliCredential(new AzureCliCredentialOptions
        {
            Subscription = subscriptions[0],
            ProcessTimeout = TimeSpan.FromSeconds(60)
        });
        var resourceScopes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["azure"] = "https://management.azure.com/.default",
            ["graph"] = "https://graph.microsoft.com/.default",
            ["loganalytics"] = "https://api.loganalytics.io/.default",
            ["storage"] = "https://storage.azure.com/.default"
        };
        var requestedResources = (Environment.GetEnvironmentVariable("EVAL_TOKEN_RESOURCES") ?? "azure")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Append("azure").Distinct().ToArray();
        if (requestedResources.Any(resource => !resourceScopes.ContainsKey(resource))) throw new InvalidOperationException("Unknown evaluation token resource.");
        var telemetry = new AiTelemetry();
        var options = new MicrosoftOAuthOptions();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSession();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddHttpClient();
        await using var app = builder.Build();
        var logging = app.Services.GetRequiredService<ILoggerFactory>();
        var logger = logging.CreateLogger("LiveEvaluation");
        var identity = new PersistentIdentity(app.Services.GetRequiredService<IDataProtectionProvider>(), logging.CreateLogger<PersistentIdentity>());
        var tokens = new SessionTokenStore(options, new EntraClientCredentials(options, logging.CreateLogger<EntraClientCredentials>()), identity, logging.CreateLogger<SessionTokenStore>());
        await using var factory = await CopilotSessionFactory.CreateAsync(credential, telemetry, identity, options, endpoint, model,
            Environment.GetEnvironmentVariable("EVAL_REASONING_EFFORT") ?? "xhigh", logging, tenant);
        app.UseSession();
        app.Use(async (context, next) =>
        {
            context.Session.SetString("user", JsonSerializer.Serialize(new { id = owner, login = "synthetic-live-evaluation" }));
            context.Session.SetString("azure_user", JsonSerializer.Serialize(new { objectId = ownerOid, tenantId = tenant }));
            var phase = state.Phase;
            foreach (var resource in requestedResources)
            {
                state.Phase = $"credential:{resource}";
                var token = await credential.GetTokenAsync(new TokenRequestContext([resourceScopes[resource]]), context.RequestAborted);
                context.Session.SetString(resource + "_token", token.Token);
                context.Session.SetString(resource + "_token_expiry", token.ExpiresOn.ToString("o"));
            }
            state.Phase = phase;
            context.Session.SetString("azure_scope_context", JsonSerializer.Serialize(new
            {
                ownerObjectId = ownerOid,
                ownerTenantId = tenant,
                subscriptions = scopes
            }));
            await next(context);
        });
        app.MapChatEndpoints(factory, tokens, telemetry, logger);
        app.MapSessionEndpoints(factory, telemetry, new JobStore(logger), logger);
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.Timeout = TimeSpan.FromSeconds(maxDurationSeconds);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(maxDurationSeconds));
        state.Phase = "chat";
        state.Clock.Restart();
        var started = state.Clock;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat") { Content = JsonContent.Create(new { prompt = question }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
        var tools = state.Tools;
        var errors = state.Errors;
        var pendingTools = state.PendingTools;
        var completedTools = new HashSet<string>(StringComparer.Ordinal);
        var answers = state.Answers;
        var visible = state.VisibleOutputs;
        var toolArguments = new Dictionary<string, string>(StringComparer.Ordinal);
        string? sessionId = null;
        while (await reader.ReadLineAsync(deadline.Token) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            if (line == "data: [DONE]") { state.Terminal = true; break; }
            using var document = JsonDocument.Parse(line[6..]);
            var item = document.RootElement;
            var type = Text(item, "type");
            if (type == "session") sessionId = Text(item, "id");
            if (type is "delta" or "message" && Text(item, "content").Length > 0) state.FirstTokenMs ??= started.ElapsedMilliseconds;
            if (type == "message" && !string.IsNullOrWhiteSpace(Text(item, "content"))) answers[Text(item, "messageId")] = Text(item, "content");
            if (type is "error" or "busy") errors.Add(Text(item, "message"));
            if (type is "chart" or "maturity_score" or "follow_up" or "html_ready" or "script_ready" or "approval_required" && visible.Count < 20)
                visible.Add(line[6..].Length <= 200000 ? line[6..] : JsonSerializer.Serialize(new { type, omitted = "Visible payload exceeds the judge budget", characters = line.Length - 6 }));
            if (type == "tool_start")
            {
                var id = Text(item, "id");
                toolArguments[id] = Text(item, "args");
                if (id.Length == 0 || !pendingTools.Add(id) || completedTools.Contains(id)) errors.Add("Invalid or duplicate tool start.");
                if (pendingTools.Count + tools.Count > maxToolCalls) throw new InvalidOperationException("Tool-call budget exceeded.");
                Console.WriteLine(JsonSerializer.Serialize(new { type, tool = Text(item, "tool"), elapsedMs = started.ElapsedMilliseconds }));
            }
            if (type == "tool_done")
            {
                var id = Text(item, "id");
                if (!pendingTools.Remove(id) || !completedTools.Add(id)) errors.Add("Tool completion did not match exactly one start.");
                var result = new ToolResult(Text(item, "tool"), item.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True, Text(item, "result"), Text(item, "error"),
                    toolArguments.GetValueOrDefault(id, ""));
                tools.Add(result);
                Console.WriteLine(JsonSerializer.Serialize(new { type, result.Name, Success = EvaluationGate.ToolSucceeded(result), elapsedMs = started.ElapsedMilliseconds }));
            }
        }
        if (pendingTools.Count > 0) errors.Add("One or more tools have no terminal result.");
        var answer = string.Join("\n\n", answers.Values);
        state.DurationMs = started.ElapsedMilliseconds;
        state.Phase = "replay";
        if (sessionId is not null && state.Terminal)
        {
            using var transcript = await client.GetAsync($"/api/sessions/{sessionId}/messages", deadline.Token);
            state.TranscriptVerified = transcript.IsSuccessStatusCode
                && EvaluationGate.TranscriptMatches(await transcript.Content.ReadAsStringAsync(deadline.Token), question, answer);
        }
        if (!state.TranscriptVerified) errors.Add("Persisted transcript did not match the completed question and answer.");
        state.HostContext = $"Connected Azure APIs: {string.Join(", ", requestedResources)}. Connected subscriptions ({scopes.Length}): {JsonSerializer.Serialize(scopes)}. Evaluation run clock UTC (host time only; it is not a source data or budget evaluation timestamp): {DateTimeOffset.UtcNow:O}.";
        var capture = state.Capture();
        using var judgeHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var scenario = new EvaluationCase(Environment.GetEnvironmentVariable("EVAL_CASE_ID") ?? "live-probe", question,
            Environment.GetEnvironmentVariable("EVAL_RUBRIC") ?? "Fulfil the question using actual tool evidence; preserve full scope and explicit unknowns. Do not confuse pricing with availability, quota with capacity, or tool completion with task success. Match the user's language.",
            (Environment.GetEnvironmentVariable("EVAL_REQUIRED_TOOLS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries),
            (Environment.GetEnvironmentVariable("EVAL_FORBIDDEN_TOOLS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries),
            MaxToolCalls: maxToolCalls, MaxDurationSeconds: maxDurationSeconds);
        using var judgeDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        state.Phase = "judge";
        var judgement = await new JudgeClient(judgeHttp, credential, new Uri(endpoint), Environment.GetEnvironmentVariable("EVAL_JUDGE_MODEL") ?? model)
            .AssessAsync(scenario, capture, judgeDeadline.Token);
        var verdict = EvaluationGate.Assess(scenario, capture, judgement);
        await SaveCapturedResultAsync(state, verdict);
        state.Phase = "cleanup";
        return verdict.Accepted && state.TranscriptVerified ? 0 : 1;
    }

    private static async Task SaveCapturedResultAsync(EvaluationRunState state, Verdict verdict)
    {
        var capture = state.Capture();
        var tools = state.Tools;
        var subscriptions = state.Subscriptions;
        string Bounded(string text, int limit)
        {
            var redacted = Redact(text, subscriptions);
            return redacted.Length <= limit ? redacted : redacted[..limit];
        }
        await SaveResultAsync(new
        {
            id = Environment.GetEnvironmentVariable("EVAL_CASE_ID") ?? "live-probe",
            question = Environment.GetEnvironmentVariable("EVAL_QUESTION") ?? "",
            sha = Environment.GetEnvironmentVariable("EVAL_EXPECTED_SHA"),
            suiteHash = Environment.GetEnvironmentVariable("EVAL_SUITE_HASH"),
            terminal = capture.Terminal,
            durationMs = capture.DurationMs,
            firstTokenMs = capture.FirstTokenMs,
            toolCount = tools.Count,
            tools = tools.Select(tool => new
            {
                name = tool.Name,
                success = EvaluationGate.ToolSucceeded(tool)
            }),
            failedTools = tools.Count(tool => !EvaluationGate.ToolSucceeded(tool)),
            failedToolDetails = tools.Where(tool => !EvaluationGate.ToolSucceeded(tool)).Select(tool => new
            {
                name = tool.Name,
                arguments = Bounded(tool.Arguments, 6000),
                detail = Bounded(((tool.Error ?? "") + " " + tool.Result).Trim(), 1500)
            }),
            toolDetails = tools.Select(tool =>
            {
                var result = Redact(tool.Result, subscriptions);
                return new
                {
                    name = tool.Name,
                    success = EvaluationGate.ToolSucceeded(tool),
                    arguments = Bounded(tool.Arguments, 6000),
                    result = result.Length <= 12000 ? result : result[..12000],
                    resultTruncated = result.Length > 12000,
                    error = Bounded(tool.Error ?? "", 4000)
                };
            }),
            failure = state.Failure is { } failure ? new
            {
                phase = failure.Phase,
                type = failure.Type,
                detail = Bounded(failure.Detail, 4000)
            } : null,
            transcriptVerified = state.TranscriptVerified,
            accepted = verdict.Accepted && state.TranscriptVerified && state.Failure is null,
            reasons = verdict.Reasons.Select(reason => Redact(reason, subscriptions)),
            judge = verdict.Judge is { } judge ? new
            {
                accepted = judge.Accepted,
                grounded = judge.Grounded,
                complete = judge.Complete,
                reason = Redact(judge.Reason, subscriptions)
            } : null,
            answer = Redact(capture.Answer, subscriptions),
            errors = capture.Errors.Select(error => Redact(error, subscriptions))
        });
    }

    private static async Task SaveResultAsync(object result)
    {
        var json = JsonSerializer.Serialize(result);
        var path = Environment.GetEnvironmentVariable("EVAL_RESULT_PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await File.WriteAllTextAsync(path + ".tmp", json);
            File.Move(path + ".tmp", path, true);
        }
        else Console.WriteLine(json);
    }

    private static string Text(JsonElement item, string property) => item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static string Redact(string text, string[] subscriptions)
    {
        text = ReportRedaction.Apply(text);
        foreach (var subscription in subscriptions) text = text.Replace(subscription, "[subscription]", StringComparison.OrdinalIgnoreCase);
        text = Regex.Replace(text, @"\b[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}\b", "[id]", RegexOptions.IgnoreCase);
        return Regex.Replace(text, @"[\w.+-]+@[\w.-]+\.[a-z]+", "[email]", RegexOptions.IgnoreCase);
    }
}