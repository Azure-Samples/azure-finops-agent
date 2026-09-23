using System.Diagnostics;
using System.Net.Http.Json;
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
        try { return await RunAsync(); }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Live evaluation failed: " + exception.GetType().Name);
            await SaveResultAsync(new
            {
                id = Environment.GetEnvironmentVariable("EVAL_CASE_ID") ?? "live-probe",
                question = Environment.GetEnvironmentVariable("EVAL_QUESTION") ?? "",
                sha = Environment.GetEnvironmentVariable("EVAL_EXPECTED_SHA"),
                suiteHash = Environment.GetEnvironmentVariable("EVAL_SUITE_HASH"),
                accepted = false,
                terminal = false,
                transcriptVerified = false,
                durationMs = 0,
                firstTokenMs = (long?)null,
                toolCount = 0,
                tools = Array.Empty<object>(),
                answer = "",
                errors = new[] { exception.GetType().Name },
                reasons = new[] { "Live evaluation did not complete." },
                judge = (object?)null
            });
            return 1;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task<int> RunAsync()
    {
        var endpoint = Environment.GetEnvironmentVariable("EVAL_MODEL_ENDPOINT") ?? throw new InvalidOperationException("Model endpoint is required.");
        var model = Environment.GetEnvironmentVariable("EVAL_MODEL") ?? "gpt-6-luna";
        var question = Environment.GetEnvironmentVariable("EVAL_QUESTION") ?? throw new InvalidOperationException("Question is required.");
        var maxDurationSeconds = int.TryParse(Environment.GetEnvironmentVariable("EVAL_MAX_DURATION_SECONDS"), out var seconds) ? seconds : 600;
        var maxToolCalls = int.TryParse(Environment.GetEnvironmentVariable("EVAL_MAX_TOOL_CALLS"), out var toolLimit) ? toolLimit : 30;
        if (maxDurationSeconds is < 1 or > 1200 || maxToolCalls is < 1 or > 100) throw new InvalidOperationException("Invalid evaluation limits.");
        var subscriptions = (Environment.GetEnvironmentVariable("EVAL_SUBSCRIPTION_IDS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (subscriptions.Length is < 1 or > 10 || subscriptions.Any(subscription => !Guid.TryParse(subscription, out _)))
            throw new InvalidOperationException("Provide 1-10 evaluation subscription IDs.");
        var scopes = subscriptions.Select((subscription, index) => new { id = subscription, name = "Evaluation subscription " + (index + 1) }).ToArray();
        var ownerOid = Guid.NewGuid().ToString();
        var tenant = Environment.GetEnvironmentVariable("EVAL_TENANT_ID") ?? "";
        var owner = PersistentIdentity.DeriveUserId(tenant, ownerOid);
        TokenCredential credential = new AzureCliCredential();
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
        await using var factory = await CopilotSessionFactory.CreateAsync(telemetry, identity, options, endpoint, model,
            Environment.GetEnvironmentVariable("EVAL_REASONING_EFFORT") ?? "xhigh", logging, tenant);
        app.UseSession();
        app.Use(async (context, next) =>
        {
            context.Session.SetString("user", JsonSerializer.Serialize(new { id = owner, login = "synthetic-live-evaluation" }));
            context.Session.SetString("azure_user", JsonSerializer.Serialize(new { objectId = ownerOid, tenantId = tenant }));
            foreach (var resource in requestedResources)
            {
                var token = await credential.GetTokenAsync(new TokenRequestContext([resourceScopes[resource]]), context.RequestAborted);
                context.Session.SetString(resource + "_token", token.Token);
                context.Session.SetString(resource + "_token_expiry", token.ExpiresOn.ToString("o"));
            }
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
        var started = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat") { Content = JsonContent.Create(new { prompt = question }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
        var tools = new List<ToolResult>();
        var errors = new List<string>();
        var pendingTools = new HashSet<string>(StringComparer.Ordinal);
        var completedTools = new HashSet<string>(StringComparer.Ordinal);
        var answers = new Dictionary<string, string>();
        var visible = new List<string>();
        var toolArguments = new Dictionary<string, string>(StringComparer.Ordinal);
        long? firstToken = null;
        string? sessionId = null;
        var terminal = false;
        while (await reader.ReadLineAsync(deadline.Token) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            if (line == "data: [DONE]") { terminal = true; break; }
            using var document = JsonDocument.Parse(line[6..]);
            var item = document.RootElement;
            var type = Text(item, "type");
            if (type == "session") sessionId = Text(item, "id");
            if (type is "delta" or "message" && Text(item, "content").Length > 0) firstToken ??= started.ElapsedMilliseconds;
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
                Console.WriteLine(JsonSerializer.Serialize(new { type, result.Name, result.Success, elapsedMs = started.ElapsedMilliseconds }));
            }
        }
        if (pendingTools.Count > 0) errors.Add("One or more tools have no terminal result.");
        var capture = new RunCapture(string.Join("\n\n", answers.Values), tools.ToArray(), terminal, errors.ToArray(), started.ElapsedMilliseconds, firstToken, visible.ToArray(),
            $"Connected Azure APIs: {string.Join(", ", requestedResources)}. Connected subscriptions ({scopes.Length}): {JsonSerializer.Serialize(scopes)}. Evaluation run clock UTC (host time only; it is not a source data or budget evaluation timestamp): {DateTimeOffset.UtcNow:O}.");
        var transcriptVerified = false;
        if (sessionId is not null && terminal)
        {
            using var transcript = await client.GetAsync($"/api/sessions/{sessionId}/messages", deadline.Token);
            transcriptVerified = transcript.IsSuccessStatusCode && (await transcript.Content.ReadAsStringAsync(deadline.Token)).Contains(question, StringComparison.Ordinal);
        }
        using var judgeHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var scenario = new EvaluationCase(Environment.GetEnvironmentVariable("EVAL_CASE_ID") ?? "live-probe", question,
            Environment.GetEnvironmentVariable("EVAL_RUBRIC") ?? "Fulfil the question using actual tool evidence; preserve full scope and explicit unknowns. Do not confuse pricing with availability, quota with capacity, or tool completion with task success. Match the user's language.",
            (Environment.GetEnvironmentVariable("EVAL_REQUIRED_TOOLS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries),
            (Environment.GetEnvironmentVariable("EVAL_FORBIDDEN_TOOLS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries),
            MaxToolCalls: maxToolCalls, MaxDurationSeconds: maxDurationSeconds);
        using var judgeDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var judgement = await new JudgeClient(judgeHttp, credential, new Uri(endpoint), Environment.GetEnvironmentVariable("EVAL_JUDGE_MODEL") ?? model)
            .AssessAsync(scenario, capture, judgeDeadline.Token);
        var verdict = EvaluationGate.Assess(scenario, capture, judgement);
        using var judgementDocument = JsonDocument.Parse(judgement);
        await SaveResultAsync(new
        {
            id = scenario.Id,
            question,
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
                arguments = Redact(tool.Arguments.Length <= 6000 ? tool.Arguments : tool.Arguments[..6000], subscriptions),
                detail = Redact(((tool.Error ?? "") + " " + (tool.Result.Length <= 1500 ? tool.Result : tool.Result[..1500])).Trim(), subscriptions)
            }),
            transcriptVerified,
            accepted = verdict.Accepted && transcriptVerified,
            reasons = verdict.Reasons.Select(reason => Redact(reason, subscriptions)),
            judge = new
            {
                accepted = judgementDocument.RootElement.GetProperty("accepted").GetBoolean(),
                grounded = judgementDocument.RootElement.GetProperty("grounded").GetBoolean(),
                complete = judgementDocument.RootElement.GetProperty("complete").GetBoolean(),
                reason = Redact(judgementDocument.RootElement.GetProperty("reason").GetString() ?? "", subscriptions)
            },
            answer = Redact(capture.Answer, subscriptions),
            errors = errors.Select(error => Redact(error, subscriptions))
        });
        return verdict.Accepted && transcriptVerified ? 0 : 1;
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