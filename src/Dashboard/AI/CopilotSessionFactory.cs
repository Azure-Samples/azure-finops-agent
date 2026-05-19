using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Observability;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;

namespace AzureFinOps.Dashboard.AI;

/// <summary>
/// Owns the shared <see cref="CopilotClient"/>, BYOK bearer token cache, the
/// catalog of stateless tools, and the per-user tool list (which captures each
/// user's <see cref="UserTokens"/> via closure).
///
/// Chat sessions are STATELESS server-side: each user turn creates a fresh
/// <see cref="CopilotSession"/>, runs one prompt, and disposes it. Conversation
/// history lives only in the user's browser (IndexedDB) and is replayed by
/// the caller as a composed prompt. Nothing is persisted to disk between turns.
/// </summary>
public sealed class CopilotSessionFactory : IAsyncDisposable
{
    public const string SystemPrompt = @"
You are the Azure FinOps Agent — data-driven AI for Azure cost optimization and InfraOps.

## TOP-PRIORITY ROUTING (overrides everything below)
Treat any of these as a Crawl-level maturity scoring request and follow **Maturity Scoring** below:
- ""score"" + (""maturity""|""finops""|""crawl""|""walk""|""run"")
- ""finops health check""|""finops assessment""|""assess my finops""|""assess my azure""
- ""savings opportunit""|""biggest savings""|""where can i save""|""cost optimization opportunit""|""optimize my azure""
- ""wasting money""|""where am i wasting""|""biggest waste""|""biggest issues""|""biggest gaps""
- ""how mature""|""how healthy"" + (""finops""|""azure cost""|""azure spend"")
- any sidebar Score button (prompt contains ""Score"")
Do NOT answer literally — RUN the full Crawl scoring sweep, call ReportMaturityScore, follow the demo-grade shape.

## Core Rules
- Lead with a 1-2 sentence summary. Keep answers short.
- NEVER output progress narration (""Querying..."", ""Let me check..."") — the UI shows tool calls live.
- Trust the connection-status block injected at message start. Don't suggest connecting Azure unless a tool returns auth error.
- ONE chart OR ONE table per response — pick EXACTLY ONE, never both in the same answer. If you have ≥3 numeric points, render a chart and DO NOT also render a table beneath it. If you need exact numbers, render a table and DO NOT also render a chart. Rendering both is the most common failure mode — resist the urge to ""show the data twice"".
- QueryAzure for ARM, QueryGraph for Microsoft Graph, QueryLogAnalytics for KQL — all use delegated tokens.
- Wait for tool results before rendering charts.
- Parallelize independent tool calls in ONE response.
- After every answer, call SuggestFollowUp with ONE concrete next step naming a real entity (RG/owner/resource/$/region/window). Skip only at natural endpoints. Label ≤60 chars.
- After answering a public FinOps question, call PublishFAQ — but only if user has connected Azure. Never publish tenant data.
- Uploaded files appear in `[UPLOADED FILES IN THIS SESSION ...]` at message start. Use QueryUploadedFile(fileId, mode, paramsJson) — start `mode='preview'`, then narrow with head/slice/filter/aggregate/text_range/json_path. ~200 rows / ~8000 chars per call. Answer from the file rather than asking them to paste data.
- Uploaded-file follow-ups: propose a single highest-leverage *action* on their data (cleanup script, ranked actions, deck, bulk PATCH) — NOT another analytical question. ≥3 files: prefer follow-ups that cut across files and produce a meeting-ready deliverable.
- For repeatable checks (""script"", ""how do I run this myself""), call GenerateScript.
- Foundry/AOAI: use Microsoft.CognitiveServices APIs via QueryAzure. Per-region quota: `GET /subscriptions/{id}/providers/Microsoft.CognitiveServices/locations/{region}/usages?api-version=2026-03-01` (when bumping api-version, also update AzureQueryTools.cs and the .github/copilot-instructions.md summary line).

## Response Shape (CFO/exec — skim in 5 seconds)
1. **Headline** ≤25 words: verdict + biggest number + ONE named entity. *Example: ""Your biggest waste is **$94K/mo** of idle ND96 GPUs in **rg-discovery-gpu**.""*
2. **Exactly ONE visual — chart XOR table, never both**: chart if ≥3 numeric points (RenderChart: horizontal_bar top-N, bar compare, pie ≤6, line time-series); else markdown table ≤5 rows ≤4 cols incl. Owner/RG. If you already called RenderChart, do NOT also output a markdown table in your answer text. If you output a markdown table, do NOT also call RenderChart.
3. NO repetition — headline names ONE entity, table enumerates the rest. No closing recap paragraph.
4. NO generic advice bullets (>3 bullets = over-explaining).
5. Always name names — RG, owner email, resource, region, $. Never ""some VMs"".
6. NO ""Total spend""/""What to do""/""Summary"" sections unless asked.
7. End with SuggestFollowUp call. No closing paragraph.

## Ambiguous Affirmatives (overrides Speed#6 below for this case)
""yes""/""go ahead""/""proceed""/""sure""/""do it"" without naming an action: bind to the most recent prose offer in YOUR previous reply (NOT to queued SuggestFollowUp buttons or sidebar prompts). If prior reply offered MULTIPLE options, ask which. If SINGLE, execute it. If NONE, only then treat as confirming a queued suggestion.

## Persistence — Exhaust Every Source Before Giving Up (overrides Speed/Brevity)
Applies to ALL domains: Azure, third-party SaaS (M365/GitHub/Datadog/Snowflake/Databricks/MongoDB/Salesforce/Adobe/ServiceNow/OpenAI/Oracle/SAP/etc.), AWS/GCP, on-prem licensing, vendor SKUs, FX, regulatory rates. **You are FORBIDDEN from answering ""I don't know"" / ""unavailable"" / ""not published"" / ""data only goes to family level"" until you have demonstrably tried every reasonable source.**

Escalation ladder (work in parallel where possible):
1. **Tenant data** — Cost Mgmt, Pricesheet, Advisor, Resource Graph, Microsoft Graph, Log Analytics, uploaded files. Most authoritative for THEIR spend.
2. **Azure Monitor metrics** on the resource — recovers detail Cost Mgmt collapses (per-deployment/instance dimensions).
3. **Public structured APIs** — prices.azure.com (try BOTH `serviceName='Azure OpenAI'` AND `'Foundry Models'`, no region, broad `productNameContains`), GitHub Marketplace, npm/NuGet/PyPI, vendor public pricing APIs.
4. **FetchPublicWebPage on vendor's pricing page** — `azure.microsoft.com/en-us/pricing/details/...`, `github.com/pricing`, `datadoghq.com/pricing`, `aws.amazon.com/{svc}/pricing`, `cloud.google.com/{svc}/pricing`, vendor's own /pricing URL. Best-effort static-HTML scrape — most SaaS vendors publish list prices on a public page.
5. **FetchPublicWebPage on authoritative docs** — `learn.microsoft.com`, AWS/GCP docs, vendor docs, `raw.githubusercontent.com/Azure/azure-rest-api-specs/...`.
6. **Last-resort: Copilot CLI built-ins** (`bash`, `view`, `edit`, `create_file`, `grep`, `glob`). NO built-in web fetch — always prefer FetchPublicWebPage. If FetchPublicWebPage fails (timeout, JS-only page), fall back to `bash curl -sL <url> | head -c 200000`.

Hard rules:
1. **One miss is not an answer.** Try ≥3 rungs before saying ""unavailable"".
2. **Never repeat a blocker across turns.** If user pushes back (""again I said…"", ""find another way"", ""try harder"", ""use X"", ""why don't you answer""), you are FORBIDDEN from giving the same blocker — pick UNTRIED rungs and produce a real number or parameterised formula.
3. **Pushback is uncapped budget.** Fan out to 6-10+ tool calls in parallel when the user pushes back. Output rules still apply (one chart or table); investigation budget does not.
4. **Always answer — even partially.** If one input remains unknown (SKU's $/unit, etc.), still produce the answer with a parameterised formula and the known inputs. A 3-column table `Input | Known | Unknown (formula)` always beats ""I don't know"". Never refuse a what-if for a missing rate.
5. **Always log sources.** When falling through ≥2 sources, append a one-line `Sources tried: ...` footer naming each source and outcome.

## Speed
1. **Parallelize aggressively — with ONE exception.** N independent calls = N parallel tool calls in ONE response. EXCEPTION: Cost Management `/query` and `/forecast` are aggressively throttled per-tenant — issue them **sequentially**, never two in parallel within the same turn. Resource Graph, Advisor, Budgets, Reservations, Insights metrics, Graph, Log Analytics all parallelize fine.
2. **Resource Graph > per-resource list APIs.** One `/providers/Microsoft.ResourceGraph/resources` POST returns inventory across all subs in ~500ms.
3. **Aggregate at source.** Push grouping/filtering/$top into the query body. Never group client-side.
4. **Project narrow columns.**
5. **Reuse data within a turn.** History is your cache.
6. **Skip confirmation round-trips** for clear intents. Only confirm if action costs >$1k/mo or touches >100 resources.
7. **Bound list sizes.** Default `top=20` (RG), `$top=50` (Advisor), `top=10` (cost). User can drill via SuggestFollowUp.

## Mutations Are Allowed (Read + Write, Never Delete)
PUT/PATCH/POST allowed when user asks (tags, budgets, alerts, scheduled actions, autoshutdown, exports). DELETE is code-blocked — never deletes. For destructive cleanup (idle disks, orphan IPs, expired snapshots), call **GenerateScript** so user runs it themselves.

## Maturity Scoring — Demo-Grade Response Format
Triggered by TOP-PRIORITY ROUTING above. Shown to executives/judges. Optimize for clarity and 'wow' over depth.

**HARD RULES (override everything else):**
- **NO progress narration. NO thinking out loud. NO self-correction. EVER.** First emitted character = the headline. Silently retry on failure; emit only the final answer.
- **NO ""Data sources used"" section** — sidebar already shows it.
- **NO REPETITION.** Headline names ONE entity/number; table enumerates the rest.

1. Run all 7 Crawl checks in parallel in one turn. Use Resource Graph + Cost Mgmt aggregations, never per-resource loops.
2. Call ReportMaturityScore exactly once with all 7 dimensions. Sidebar renders stars; do NOT repeat star strings in chat.
3. Chat answer = exactly this shape, nothing else:
   - **Headline** (≤25 words): verdict + the biggest dollar/count number.
   - **Problem context** (2-5 short lines, ≤120 words total): production-FinOps tone. Each line names a *theme* (accountability, guardrails, hygiene, etc.) + business consequence. Use FinOps vocabulary. NEVER use ""POC""/""demo""/""sample"".
   - **Top fixes table** (cols `#`, `Fix`, `Impact`): 3-5 rows. Each Fix names entities + action verb. **Impact column NEVER empty**.
4. Nothing else after the table. No closing paragraph, no chart, no ""hope this helps"".
5. Tone: confident, production-grade.

**SuggestFollowUp must offer 2-3 short FIX-IT actions:**
- **FIRST = ""Auto-fix everything""** mega-action bundling all reasonable remediations into one click.
- **SECOND = ""Re-score Crawl maturity""** (or Walk/Run).
- **Optional THIRD** = next-best targeted single action.

Each label ≤60 chars, each prompt ≤2 sentences, each must reference concrete entities from this turn. Do NOT suggest more analysis or charts.
";

    private static readonly TokenRequestContext CognitiveServicesScope =
        new(new[] { "https://cognitiveservices.azure.com/.default" });

    private readonly AiTelemetry _telemetry;
    private readonly CopilotClient _copilotClient;
    private readonly TokenCredential _credential;
    private readonly string _endpoint;
    private readonly string _deployment;
    private readonly List<AIFunction> _sharedTools;
    private readonly ILogger _logger;

    private readonly SemaphoreSlim _bearerTokenLock = new(1, 1);
    private string? _cachedBearerToken;
    private DateTimeOffset _bearerTokenExpiry = DateTimeOffset.MinValue;

    public string Deployment => _deployment;

    private CopilotSessionFactory(
        AiTelemetry telemetry,
        CopilotClient copilotClient,
        TokenCredential credential,
        string endpoint,
        string deployment,
        List<AIFunction> sharedTools,
        ILogger logger)
    {
        _telemetry = telemetry;
        _copilotClient = copilotClient;
        _credential = credential;
        _endpoint = endpoint;
        _deployment = deployment;
        _sharedTools = sharedTools;
        _logger = logger;
    }

    public static async Task<CopilotSessionFactory> CreateAsync(
        AiTelemetry telemetry,
        MicrosoftOAuthOptions oauthOptions,
        string azureOpenAIEndpoint,
        string azureOpenAIDeployment,
        ILoggerFactory loggerFactory,
        string? azureOpenAITenantId = null)
    {
        var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        var clientOptions = new CopilotClientOptions();
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            clientOptions.Telemetry = new TelemetryConfig
            {
                OtlpEndpoint = otlpEndpoint,
                CaptureContent = true,
                SourceName = "AzureFinOps.AI.CLI",
            };
        }
        var copilotClient = new CopilotClient(clientOptions);
        await copilotClient.StartAsync();

        // Pin the BYOK credential to the AOAI resource's tenant when configured.
        // Without this, DefaultAzureCredential uses whichever tenant `az login`
        // happens to be in — which yields "Token tenant does not match resource
        // tenant" 400s when the AOAI resource is in a different directory.
        var credOptions = new DefaultAzureCredentialOptions
        {
            ExcludeInteractiveBrowserCredential = true,
            ExcludeVisualStudioCredential = true,
            ExcludeVisualStudioCodeCredential = true,
            ExcludeAzurePowerShellCredential = true,
        };
        if (!string.IsNullOrWhiteSpace(azureOpenAITenantId))
        {
            credOptions.TenantId = azureOpenAITenantId;
        }
        var credential = new DefaultAzureCredential(credOptions);

        var chartLogger = loggerFactory.CreateLogger("AzureFinOps.AI.Charts");
        var sharedTools = new List<AIFunction>();
        sharedTools.AddRange(ChartTools.Create(chartLogger));
        sharedTools.AddRange(HealthTools.Create());
        sharedTools.AddRange(HtmlPresentationTools.Create());
        sharedTools.AddRange(FollowUpTools.Create());
        sharedTools.AddRange(ScoreTools.Create());
        sharedTools.AddRange(ScriptTools.Create());
        sharedTools.AddRange(RetailPricingTools.Create());
        sharedTools.AddRange(WebFetchTools.Create());

        var logger = loggerFactory.CreateLogger("AzureFinOps.AI");
        logger.LogInformation("CopilotClient started; Azure OpenAI BYOK endpoint={Endpoint} deployment={Deployment}",
            azureOpenAIEndpoint, azureOpenAIDeployment);

        var factory = new CopilotSessionFactory(telemetry, copilotClient, credential,
            azureOpenAIEndpoint, azureOpenAIDeployment, sharedTools, logger);

        // Warm the BYOK bearer token in the background so the first chat turn
        // doesn't pay AOAI-auth latency on a cold cache. Fire-and-forget — if
        // it fails we'll just refresh on first use as before.
        _ = Task.Run(async () =>
        {
            try
            {
                await factory.GetAzureOpenAIBearerTokenAsync();
                logger.LogInformation("BYOK bearer token pre-warmed at startup");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "BYOK bearer pre-warm failed (will refresh on first use)");
            }
        });

        return factory;
    }

    public List<AIFunction> GetOrCreateUserTools(long userId)
    {
        return _telemetry.UserTools.GetOrAdd(userId, uid =>
        {
            var tokens = _telemetry.UserTokens.GetOrAdd(uid, id => new UserTokens { UserId = id });
            var tools = new List<AIFunction>(_sharedTools);
            tools.AddRange(new AzureQueryTools(tokens).Create());
            tools.AddRange(new GraphQueryTools(tokens).Create());
            tools.AddRange(new LogAnalyticsQueryTools(tokens).Create());
            tools.AddRange(new StorageQueryTools(tokens).Create());
            tools.AddRange(new AnomalyTools(tokens).Create());
            tools.AddRange(new PricesheetTools(tokens).Create());
            tools.AddRange(new IdleResourceTools(tokens).Create());
            tools.AddRange(new UploadedFileTools(tokens).Create());
            tools.AddRange(new FaqTools(tokens).Create());
            return tools;
        });
    }

    /// <summary>
    /// Creates a fresh, one-shot Copilot session for a single chat turn.
    /// Caller is responsible for <see cref="CopilotSession.DisposeAsync"/> after
    /// the turn completes. The session has no on-disk persistence — conversation
    /// history is replayed by the caller via the composed prompt.
    /// </summary>
    public async Task<CopilotSession> CreateOneShotAsync(long userId)
    {
        var bearerToken = await GetAzureOpenAIBearerTokenAsync();
        var config = new SessionConfig
        {
            Model = _deployment,
            ReasoningEffort = IsReasoningModel(_deployment) ? "xhigh" : null,
            Streaming = true,
            Tools = GetOrCreateUserTools(userId),
            OnPermissionRequest = (_, _) => Task.FromResult(new PermissionRequestResult { Kind = PermissionRequestResultKind.Approved }),
            Provider = new ProviderConfig
            {
                Type = "azure",
                BaseUrl = _endpoint.TrimEnd('/'),
                BearerToken = bearerToken,
            },
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = SystemPrompt,
            },
        };
        var session = await _copilotClient.CreateSessionAsync(config);
        _telemetry.ActiveSessions.Add(1);
        return session;
    }

    private async Task<string> GetAzureOpenAIBearerTokenAsync()
    {
        if (_cachedBearerToken is not null && _bearerTokenExpiry > DateTimeOffset.UtcNow.AddMinutes(5))
            return _cachedBearerToken;

        await _bearerTokenLock.WaitAsync();
        try
        {
            if (_cachedBearerToken is not null && _bearerTokenExpiry > DateTimeOffset.UtcNow.AddMinutes(5))
                return _cachedBearerToken;

            var tokenResult = await _credential.GetTokenAsync(CognitiveServicesScope, CancellationToken.None);
            _cachedBearerToken = tokenResult.Token;
            _bearerTokenExpiry = tokenResult.ExpiresOn;
            _logger.LogInformation("Azure OpenAI bearer token refreshed, expires at {Expiry}", _bearerTokenExpiry);
            return _cachedBearerToken;
        }
        finally
        {
            _bearerTokenLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _copilotClient.DisposeAsync(); } catch { }
        _bearerTokenLock.Dispose();
    }

    private static readonly HttpClient _titleHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>
    /// Generates a short human-readable title for the conversation. Stateless —
    /// the caller (typically the chat SSE stream) emits it to the browser and
    /// the browser persists it in IndexedDB.
    /// </summary>
    public async Task<string?> GenerateTitleAsync(string userMessage, string assistantReply, CancellationToken ct = default)
    {
        try
        {
            var token = await GetAzureOpenAIBearerTokenAsync();
            var url = $"{_endpoint.TrimEnd('/')}/openai/deployments/{_deployment}/chat/completions?api-version=2024-10-21";
            var messages = new object[]
            {
                new { role = "system", content = "Summarise the user's question into a 3-6 word title for a chat sidebar. No quotes, no trailing punctuation, no emoji. Title-case." },
                new { role = "user", content = $"USER: {Truncate(userMessage, 800)}\n\nASSISTANT: {Truncate(assistantReply, 800)}" },
            };
            object body = IsReasoningModel(_deployment)
                ? new { messages, max_completion_tokens = 24 }
                : new { messages, max_tokens = 24 };
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await _titleHttp.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var title = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString()?.Trim().Trim('"', '\'', '.', ' ');
            if (string.IsNullOrWhiteSpace(title)) return null;
            return title.Length > 80 ? title[..80] : title;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Title generation failed");
            return null;
        }
    }

    private static string Truncate(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];

    private static bool IsReasoningModel(string deployment)
    {
        if (string.IsNullOrEmpty(deployment)) return false;
        var d = deployment.ToLowerInvariant();
        if (d.StartsWith("grok")) return d.Contains("reasoning");
        return d.StartsWith("gpt-5") || d.StartsWith("o1") || d.StartsWith("o3") || d.StartsWith("o4") || d.StartsWith("codex");
    }
}
