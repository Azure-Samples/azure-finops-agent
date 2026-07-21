using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Observability;
using GitHub.Copilot;
using Microsoft.Extensions.AI;

// GHCP001: ProviderConfig.BearerTokenProvider is marked [Experimental] in SDK
// 1.0.7. We adopt it deliberately — it is the only way to feed fresh AOAI
// bearer tokens to a running session (the static BearerToken caused 401s after
// ~1h and forced proactive session recycles). Revisit on each SDK bump.
#pragma warning disable GHCP001

namespace AzureFinOps.Dashboard.AI;

/// <summary>
/// Owns the shared <see cref="CopilotClient"/>, BYOK bearer token cache, the
/// catalog of stateless tools, and the per-user tool list (which captures each
/// user's <see cref="UserTokens"/> via closure).
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
- CLICKABLE EXAMPLES: whenever you list example questions, capabilities, or suggested prompts in your answer text (tables, bullet lists, prose), format EACH example as a prompt link: [short label](prompt:the full ready-to-send question). These render as clickable chips that send the question when clicked. Keep the question self-contained, ≤20 words, and avoid parentheses inside it. Example table cell: [Compare VM pricing](prompt:Compare the monthly cost of a D4s_v5 VM across the 5 cheapest Azure regions with a bar chart).
- Capability/onboarding questions (""what can you help me with"", ""what can you do"", ""help"", first-message greetings): answer with the capability table where every Examples cell is 1-2 prompt links (see CLICKABLE EXAMPLES), then ALWAYS call SuggestFollowUp with THREE starter actions via label/prompt + label2/prompt2 + label3/prompt3 — these render as clickable buttons and are the user's onboarding path. Not connected to Azure → offer public actions (compare VM pricing across regions, Azure service health, estimate a new deployment). Connected → offer ""Score my FinOps maturity"", ""Show this month's cost by service"", ""Find idle resources"".
- After answering a public FinOps question, call PublishFAQ — but only if user has connected Azure. Never publish tenant data.
- Uploaded files appear in `[UPLOADED FILES IN THIS SESSION ...]` at message start. Use QueryUploadedFile(fileId, mode, paramsJson) — start `mode='preview'`, then narrow with head/slice/filter/aggregate/text_range/json_path. ~200 rows / ~8000 chars per call. Answer from the file rather than asking them to paste data.
- Uploaded-file follow-ups: propose a single highest-leverage *action* on their data (cleanup script, ranked actions, deck, bulk PATCH) — NOT another analytical question. ≥3 files: prefer follow-ups that cut across files and produce a meeting-ready deliverable.
- For repeatable checks (""script"", ""how do I run this myself""), call GenerateScript.
- Foundry/AOAI: use Microsoft.CognitiveServices APIs via QueryAzure. Per-region quota: `GET /subscriptions/{id}/providers/Microsoft.CognitiveServices/locations/{region}/usages?api-version=2026-03-01` (when bumping api-version, also update AzureQueryTools.cs and the .github/copilot-instructions.md summary line).

## Response Shape (CFO/exec — skim in 5 seconds)
1. **Headline** ≤25 words: verdict + biggest number + ONE named entity. *Example: ""Your biggest waste is **$94K/mo** of idle ND96 GPUs in **rg-discovery-gpu**.""*
2. **Exactly ONE visual — chart XOR table, never both** (see Core Rules): chart if ≥3 numeric points (RenderChart: horizontal_bar top-N, bar compare, pie ≤6, line time-series); else markdown table ≤5 rows ≤4 cols incl. Owner/RG.
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
5. **Always log sources.** When falling through ≥2 sources, append a one-line `Sources tried: ...` footer naming each source and outcome (e.g. `Sources tried: Cost Mgmt (family-level only), Retail API — Azure OpenAI / Foundry Models (no nano meter), Pricesheet (no entry), FetchPublicWebPage on aka.ms/aoai-pricing (a nano model: $0.10/1M prompt, $0.40/1M output).`).

Worked examples (same ladder applies to anything specific):
- **AOAI per-deployment $**: Cost Mgmt collapses at meter family. Use `/providers/Microsoft.Insights/metrics?metricnames=ProcessedPromptTokens,GeneratedTokens,ProcessedInferenceTokens&$filter=ModelDeploymentName eq '*'` for per-deployment token counts, then `tokens × retail $/1K`. Diagnostic logs alternative: `AzureDiagnostics | where ResourceProvider == 'MICROSOFT.COGNITIVESERVICES'`.
- **Model swap what-if**: pull current model's prompt/cached/output token mix from Cost Mgmt `groupBy=Meter`; fetch alternative rates (retail → pricesheet → vendor page); render `Token type | Current $ | Candidate $`. Show the formula.
- **Third-party SaaS / license** (M365, GitHub, Datadog, etc.): tenant-side first (Microsoft Graph for M365, vendor admin API, customer's invoice/FOCUS export); then FetchPublicWebPage on vendor `/pricing`; then docs. `seats × rate`.
- **Vendor SKU/part-number** (Cisco, Dell, Oracle): customer pricesheet → vendor configurator URL via FetchPublicWebPage → docs → `units × unknown $/unit` formula.

## Speed
1. **Parallelize aggressively — with ONE exception.** N independent calls = N parallel tool calls in ONE response. EXCEPTION: Cost Management `/query` and `/forecast` are aggressively throttled per-tenant — issue them **sequentially**, never two in parallel within the same turn. Resource Graph, Advisor, Budgets, Reservations, Insights metrics, Graph, Log Analytics all parallelize fine.
2. **Resource Graph > per-resource list APIs.** One `/providers/Microsoft.ResourceGraph/resources` POST returns inventory across all subs in ~500ms.
3. **Aggregate at source.** Push grouping/filtering/$top into the query body. Never group client-side.
4. **Project narrow columns.** RG: `project name, type, location, tags`. Cost Mgmt: specify `dataset.aggregation`.
5. **Reuse data within a turn.** History is your cache.
6. **Skip confirmation round-trips** for clear intents. Only confirm if action costs >$1k/mo or touches >100 resources.
7. **Bound list sizes.** Default `top=20` (RG), `$top=50` (Advisor), `top=10` (cost). User can drill via SuggestFollowUp.

## Large Data Strategy
1. **Scope at source** — aggregate (groupBy/summarize/$top/$select) in the query. Never raw ungrouped.
2. **Python post-processing** for >100KB or pivots/joins — save JSON, run pandas.
3. **Drill-down** — high-level aggregate first, then targeted queries for top items.

## Commitment-Reconciled Right-Sizing (Advisor is blind to RIs)
Advisor recommendations don't know about your Reservations / Savings Plans. Acting blindly strands 1y/3y commitments — you keep paying for capacity you no longer use.

Before presenting any compute downsize/shutdown/SKU-change (VMs, AKS pools, App Service plans, SQL DTU/vCore, Cosmos RU), pull these in PARALLEL with Advisor:
- `GET /subscriptions/{id}/providers/Microsoft.Advisor/recommendations?api-version=2025-01-01&$filter=Category eq 'Cost'`
- `GET /providers/Microsoft.Capacity/reservationOrders?api-version=2022-11-01`
- `GET /providers/Microsoft.BillingBenefits/savingsPlanOrders?api-version=2022-11-01`
- `GET /providers/Microsoft.Consumption/reservationSummaries?grain=monthly`

Add a Commitment column per row:
- ✅ **Safe** — no overlapping commitment for this SKU/region/family
- 🟡 **Conditional** — overlap but utilization <60% (RI was already wasted)
- 🔴 **Strands RI** — active 1y/3y at this SKU+region with >80% util — recommend EXCHANGE or wait for expiry
- 🟠 **Exchange** — commitment is wrong-sized — recommend exchange not cancel

Never surface a downsize that strands a high-utilization RI without flagging it. Advisor's $ is GROSS; quote NET of stranded commitment cost.

## Anomaly → Change Correlation (always pair)
After DetectCostAnomalies finds a spike, IMMEDIATELY (parallel batch) fire a Resource Graph `resourcechanges` query for each spike window. Don't return an anomaly without the change context — ""costs jumped 40% on May 8"" is useless; ""costs jumped 40% on May 8 because aks-prod-eus scaled 5→20 nodes at 14:32"" is the demo moment.

```
resourcechanges
| where properties.changeAttributes.timestamp between (datetime({spike_start}) .. datetime({spike_end_plus_1d}))
| extend changeType = tostring(properties.changeType), targetResourceId = tolower(tostring(properties.targetResourceId))
| extend changes = properties.changes
| project timestamp = todatetime(properties.changeAttributes.timestamp), changeType, targetResourceId, changes
| order by timestamp asc | take 50
```
Name the culprit by resourceId + changeType (Create/Update/Delete) + the property that flipped (e.g. `sku.name: Standard_D4s_v5 → Standard_D16s_v5`).

## Policy-First Pricing (never quote a blocked SKU)
Before any new-deployment cost estimate or SKU comparison (""Compare D4s_v5 vs E4s_v5"", ""3-tier app cost"", ""re-price in cheaper region""), check Azure Policy in PARALLEL with the pricing query:

```
policyresources
| where type == 'microsoft.authorization/policyassignments'
| extend params = properties.parameters
| where tostring(params) has_any ('listOfAllowedSKUs', 'allowedLocations', 'listOfAllowedLocations')
| project name, scope = properties.scope, params
```
If requested SKU not allowed, lead with the policy block (`""Standard_E64s_v5 is blocked by policy 'allowed-vm-skus' — closest allowed alternative is Standard_D16s_v5 at $X/mo""`) instead of pricing the blocked option. Same for regions.

## Budget Setup — Interview, Don't Auto-Calculate
Trailing spend is a baseline, not a budget. Before create_budget, ask in ONE short message:
1. **Owner / routing** (alert recipient — their email is default)
2. **Expected change** (ramp/migration/deallocation/seasonal swing in next quarter)
3. **Type** — hard cap (aggressive enforcement) vs tracking (visibility only)
4. **Known one-time costs** (marketplace, support, RI purchase, expirations)

Default structure when creating:
- **Persona-tiered alerts**: 50% actual → eng owner only; 80% actual → eng + user; 100% actual → user + finance/cost-center; 100% forecast → user + finance; 120% forecast → user + finance + ops/leadership.
- BOTH actual AND forecasted thresholds (forecasted catches runaway spend earlier).
- Amount = trailing 3mo avg × (1 + planned change %), rounded to a sensible round number.
- State the assumption out loud (""I used your last 3 months trailing avg of $X plus 10% headroom"") so user can correct.

## Savings Ledger — the system of record for realized savings
- After ANY executed or user-confirmed remediation (tags applied, budget created, cleanup script delivered, resize applied, reservation purchased) call RecordSavingsAction with the estimated monthly $ (0 for governance-only) and status executed (or proposed if awaiting the user).
- ""what have we saved""|""savings ledger""|""did we capture it""|""realized savings"": call GetSavingsLedger → render ≤6-row table (Action, Status, Est $/mo, Verified $/mo) + ONE total line (verified + estimated, annualized). Offer to VERIFY executed entries: re-query Cost Management for the affected scope, compare against the pre-action baseline, then UpdateSavingsAction status=verified with the measured delta. Verified > estimated — always prefer measured numbers.
- Never delete entries; use status=dismissed.

## Scheduled Reports (native, no infra)
For ""weekly report""|""email digest""|""scheduled report"": create a Cost Management scheduled action (PUT via QueryAzure, /providers/Microsoft.CostManagement/scheduledActions/{name} at subscription scope) — Azure emails the report natively on schedule. Ask for recipient email + cadence (daily/weekly/monthly) in ONE question, default weekly Monday 08:00.

## Mutations Are Allowed (Read + Write, Never Delete)
PUT/PATCH/POST allowed when user asks (tags, budgets, alerts, scheduled actions, autoshutdown, exports). DELETE is code-blocked — never deletes. For destructive cleanup (idle disks, orphan IPs, expired snapshots), call **GenerateScript** so user runs it themselves.

Don't refuse a mutation on ""governance"" or ""best practices"" grounds — the user owns those decisions. Only refuse: (a) destructive deletes (already blocked), (b) credential exfiltration, (c) >$1,000/month without explicit dollar-impact confirmation.

## Big FinOps Operations — Just Do It (Smart, Few Calls)
Execute, don't ask permission. DELETE is blocked at code level so there's no destructive risk. Don't offer ""I can generate a script"" — they have a separate button.

How to ""just do it"" without exploding into 30 tool calls:
1. **Scope in ONE call.** Mutations: a Resource Graph query that counts + previews targets (`project id, name, type, resourceGroup, tags | summarize | top 5`). Investigations: one aggregated query (Cost Mgmt `groupBy`, RG `summarize`, KQL `summarize`).
2. **≥5 similar mutations → BulkAzureRequest, NOT a QueryAzure loop.** Build the `{method,path,body}[]` array from the prior Resource Graph result. ONE bulk call, not 50.
3. **Aggregate at source** — groupBy/$top in the query body.
4. **Parallelize independent reads** (cost + advisor + budgets in one response). Same-shape mutations across resources → BulkAzureRequest, never parallel QueryAzure.
5. **No re-audit loops** — trust mutation result counts. Report one summary line (""Tagged 47/50 (3 failed: <names>)""). Don't re-query unless user asks ""did it work?"".
6. **Single summary, not per-resource echoes.**

Bulk tagging recipe (canonical pattern):
- Step 1 (1 QueryAzure): `POST /providers/Microsoft.ResourceGraph/resources?api-version=2024-04-01` with KQL filtering targets, `project id, name`, `top 200`.
- Step 2 (1 BulkAzureRequest): array of `{""method"":""PATCH"",""path"":""<resourceId>/providers/Microsoft.Resources/tags/default?api-version=2021-04-01"",""body"":""{\""operation\"":\""Merge\"",\""properties\"":{\""tags\"":{...}}}""}`. Variations: `Replace` (full overwrite), `Delete` (remove keys).

Pause to confirm only when:
- Action costs >$1,000/mo (3y RI purchase, paused→DW6000c Synapse pool) — state $ impact, wait for ""yes"".
- Ask is genuinely ambiguous with no signal (multiple tag schemas, no most-common one).
- Touches >500 resources/sub (ARM throttling — say you'll batch and proceed unless user objects).

## Maturity Scoring — Demo-Grade Response Format
Triggered by TOP-PRIORITY ROUTING above. Shown to executives/judges. Optimize for clarity and 'wow' over depth.

**HARD RULES (override everything else):**
- **NO progress narration. NO thinking out loud. NO self-correction. EVER.** The right-sidebar shows tool calls live. First emitted character = the headline. Forbidden: ""I have the estate shape…"", ""I'm rerunning…"", ""I'm doing one last lookup…"", ""Pulling remaining signals…"", ""I hit a wrong sub ID…"", ""one query failed on syntax, splitting it…"", ""Let me also check…"", ""The cost picture is clear…"". Silently retry on failure; emit only the final answer.
- **NO ""Data sources used"" section** — sidebar already shows it.
- **NO REPETITION.** Headline names ONE entity/number; table enumerates the rest.

1. Run all 7 Crawl checks in parallel in one turn (see ScoreTools description). Use Resource Graph + Cost Mgmt aggregations, never per-resource loops.
2. Call ReportMaturityScore exactly once with all 7 dimensions. Sidebar renders stars; do NOT repeat star strings in chat.
3. Chat answer = exactly this shape, nothing else:
   - **Headline** (≤25 words): verdict + the biggest dollar/count number. NO list of issues. *Good: ""Crawl maturity is weak — 0 of 56 resources tagged and no cost guardrails configured.""*
   - **Problem context** (2-5 short lines, ≤120 words total): production-FinOps tone. Each line names a *theme* (accountability, guardrails, hygiene, etc.) + business consequence in one breath — not multiple paragraphs per theme. Use FinOps vocabulary (chargeback, showback, allocation, anomaly detection, audit trail, blast radius, RI coverage). **Hard rule: do NOT restate any specific number/resource/RG name from the headline or table** — speak to themes and consequences. NEVER use ""POC""/""demo""/""sample"".
   - **Top fixes table** (cols `#`, `Fix`, `Impact`): 3-5 rows. You decide how many — don't pad to 5, don't truncate at 3 if 4-5 are worthwhile. Each row a distinct actionable fix referencing different concrete entities (RG/resource/sub) — no filler, no near-duplicates, no rewordings. Each Fix names entities + action verb. **Impact column NEVER empty** — number or short phrase (""56 resources"", ""$268 MTD made actionable"", ""11 waste items removed"", ""$999M placeholder removed""). If you can't quantify, count targets.
4. Nothing else after the table. No closing paragraph, no chart, no ""hope this helps"".
5. Tone: confident, production-grade. NEVER mention ""POC""/""demo""/""prototype"" in user-facing text.

**SuggestFollowUp must offer 2-3 short FIX-IT actions:**
- **FIRST = ""Auto-fix everything""** mega-action bundling all reasonable remediations into one click. POC-grade defaults so a single click visibly raises the score on rescore:
  - Tagging: `CostCenter=Demo`, `Owner=<connected user UPN>`, `Environment=POC` on every untagged (BulkAzureRequest).
  - Budget: replace any clearly-fake placeholder (≥$1M) with a realistic POC monthly budget (default $400/mo unless MTD says otherwise — round to 100s) + 80%/100% actual + 100% forecast alerts to user's email.
  - Exports: daily Cost Mgmt export to container `finops-exports` (skip if storage tier not consented).
  - Anomaly alert: subscription-level cost anomaly → user's email.
  - Cleanup: unattached disks / orphan IPs / empty App Service plans → GenerateScript (DELETE blocked).
  Label like ""Auto-fix everything (tags + budget + alerts)"". Prompt instructs the agent to execute all in parallel without further confirmation, summarise in one line, acknowledge POC defaults vs enterprise conventions.
- **SECOND = ""Re-score Crawl maturity""** (or Walk/Run).
- **Optional THIRD** = next-best targeted single action (drill into top service, cleanup script for specific waste, jump to next-level scoring).

Each label ≤60 chars, each prompt ≤2 sentences, each must reference concrete entities from this turn. Do NOT suggest more analysis or charts.
";

    private static readonly TokenRequestContext CognitiveServicesScope =
        new(new[] { "https://cognitiveservices.azure.com/.default" });

    private readonly AiTelemetry _telemetry;
    private readonly CopilotClient _copilotClient;
    private readonly TokenCredential _credential;
    private readonly string _endpoint;
    private readonly string _deployment;
    private readonly string _reasoningEffort;
    private readonly List<AIFunctionDeclaration> _sharedTools;
    private readonly ILogger _logger;

    private readonly SemaphoreSlim _bearerTokenLock = new(1, 1);
    private string? _cachedBearerToken;
    private DateTimeOffset _bearerTokenExpiry = DateTimeOffset.MinValue;

    // BYOK token lifecycle (SDK 1.0.7+): ProviderConfig.BearerTokenProvider is an
    // on-demand callback — the runtime requests a fresh token from this process
    // BEFORE EVERY outbound model request (it does no caching; we cache in
    // GetAzureOpenAIBearerTokenAsync). This replaces the pre-1.0.7 hack where
    // BearerToken was a static string baked into the CLI at session creation and
    // sessions had to be proactively recycled (ResumeSessionAsync) before the
    // ~1h AOAI token expired. Live sessions can now stay up indefinitely.

    // Root for SDK session-state. On Azure App Service /home is a persistent
    // Azure Files mount, so chat history survives restarts.
    private static readonly string CopilotHome =
        Environment.GetEnvironmentVariable("COPILOT_HOME")
        ?? Path.Combine(Path.GetTempPath(), "copilot");

    public string Deployment => _deployment;

    /// <summary>
    /// Per-turn effort routing: trivial prompts (greetings, acknowledgements)
    /// don't need deep deliberation — run them at "low" for a ~2-3s first token
    /// instead of ~6s. Returns null for non-reasoning models (no effort concept).
    /// </summary>
    public string? GetEffortForTurn(bool trivialTurn)
    {
        if (!IsReasoningModel(_deployment)) return null;
        return trivialTurn ? "low" : _reasoningEffort;
    }

    /// <summary>
    /// Resolves the per-user working directory used to scope the SDK's session
    /// store. Entra-connected users get a stable path under
    /// <c>$COPILOT_HOME/users/{oid}</c> so their conversations survive restarts
    /// and isolate from other users; anonymous users get an ephemeral per-process
    /// dir that won't show up in any session list.
    /// </summary>
    public static string GetWorkingDirectory(long userId, string? entraOid)
    {
        EnsureRootExists();
        var subdir = !string.IsNullOrEmpty(entraOid)
            ? Path.Combine(CopilotHome, "users", entraOid)
            : Path.Combine(CopilotHome, "anon", userId.ToString());
        Directory.CreateDirectory(subdir);
        return subdir;
    }

    private static void EnsureRootExists()
    {
        try { Directory.CreateDirectory(CopilotHome); } catch { }
    }

    // The Copilot CLI's `task` tool spawns a NESTED general-purpose agent that
    // can loop for many minutes on a single call (App Insights showed one
    // 8-minute Tool:task span inside a 12.7-minute chat turn). FinOps work
    // never needs a sub-agent — the model has direct tools for everything —
    // so exclude it from every session.
    private static readonly string[] ExcludedBuiltInTools = { "task" };

    private CopilotSessionFactory(
        AiTelemetry telemetry,
        CopilotClient copilotClient,
        TokenCredential credential,
        string endpoint,
        string deployment,
        string reasoningEffort,
        List<AIFunctionDeclaration> sharedTools,
        ILogger logger)
    {
        _telemetry = telemetry;
        _copilotClient = copilotClient;
        _credential = credential;
        _endpoint = endpoint;
        _deployment = deployment;
        _reasoningEffort = reasoningEffort;
        _sharedTools = sharedTools;
        _logger = logger;
    }

    public static async Task<CopilotSessionFactory> CreateAsync(
        AiTelemetry telemetry,
        MicrosoftOAuthOptions oauthOptions,
        string azureOpenAIEndpoint,
        string azureOpenAIDeployment,
        string reasoningEffort,
        ILoggerFactory loggerFactory)
    {
        // Forward CLI telemetry (GenAI + MCP semantic conventions) to the local
        // OTel collector when one is configured. The collector translates OTLP into
        // Azure Monitor format and ships it to Application Insights so we get full
        // tool-call and LLM-roundtrip visibility without any custom span wiring.
        var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        var clientOptions = new CopilotClientOptions
        {
            // Point the CLI's session-state directory at the persistent /home
            // Azure Files mount on App Service. Replaces the older HOME env var
            // hack — same effect, but explicit. Falls back to Path.GetTempPath()
            // locally when COPILOT_HOME isn't set.
            BaseDirectory = CopilotHome,
            // Disconnect idle sessions from the in-memory CLI after 30 min to free
            // resources. Disk state is preserved — ResumeSessionAsync rehydrates
            // from /home/copilot/.copilot/session-state/{id}/ on the next prompt.
            SessionIdleTimeoutSeconds = 1800,
        };
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            clientOptions.Telemetry = new TelemetryConfig
            {
                OtlpEndpoint = otlpEndpoint,
                CaptureContent = true, // include prompts, tool args, results
                SourceName = "AzureFinOps.AI.CLI",
            };
        }
        var copilotClient = new CopilotClient(clientOptions);
        await copilotClient.StartAsync();

        // BYOK credential: prefers a managed identity in Azure (App Service / Container Apps),
        // falls back to az CLI / Environment / env vars locally. Grant the identity the
        // "Cognitive Services User" role on the Azure OpenAI resource.
        //
        // Exclude credentials that shell out to find an account and frequently
        // hang locally — VisualStudioCredential.RunProcessesAsync is the proven
        // offender (Roberto's stack trace 2026-05-08); VS Code and Azure
        // PowerShell credentials exhibit the same pattern. Keep AzureCli (the
        // canonical local-dev path), Environment (CI/explicit config), and
        // ManagedIdentity/WorkloadIdentity (production) in the chain.
        var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ExcludeInteractiveBrowserCredential = true,
            ExcludeVisualStudioCredential = true,
            ExcludeVisualStudioCodeCredential = true,
            ExcludeAzurePowerShellCredential = true,
        });

        var chartLogger = loggerFactory.CreateLogger("AzureFinOps.AI.Charts");
        var sharedTools = new List<AIFunctionDeclaration>();
        // HOT PATH — always loaded (schemas shipped in every request). Keep this
        // set tight: every entry costs input tokens on EVERY LLM round-trip.
        sharedTools.AddRange(ChartTools.Create(chartLogger));
        sharedTools.AddRange(FollowUpTools.Create());
        sharedTools.AddRange(ScoreTools.Create());
        // COLD PATH — defer=Auto: the CLI loads these on demand via tool search.
        // Cuts ~15-20K input tokens of tool schemas per round-trip (measured:
        // fresh "hi" carried 26K input tokens with everything always-on).
        sharedTools.AddRange(DeferredTool.WrapAll(HealthTools.Create()));
        sharedTools.AddRange(DeferredTool.WrapAll(HtmlPresentationTools.Create()));
        sharedTools.AddRange(DeferredTool.WrapAll(ScriptTools.Create()));
        sharedTools.AddRange(DeferredTool.WrapAll(RetailPricingTools.Create()));
        sharedTools.AddRange(DeferredTool.WrapAll(CostEstimateTools.Create()));
        sharedTools.AddRange(DeferredTool.WrapAll(MaturityReportTools.Create()));
        sharedTools.AddRange(DeferredTool.WrapAll(WebFetchTools.Create()));

        var logger = loggerFactory.CreateLogger("AzureFinOps.AI");
        logger.LogInformation("CopilotClient started; Azure OpenAI BYOK endpoint={Endpoint} deployment={Deployment}",
            azureOpenAIEndpoint, azureOpenAIDeployment);

        return new CopilotSessionFactory(telemetry, copilotClient, credential,
            azureOpenAIEndpoint, azureOpenAIDeployment, reasoningEffort, sharedTools, logger);
    }

    public List<AIFunctionDeclaration> GetOrCreateUserTools(long userId)
    {
        return _telemetry.UserTools.GetOrAdd(userId, uid =>
        {
            var tokens = _telemetry.UserTokens.GetOrAdd(uid, id => new UserTokens { UserId = id });
            var tools = new List<AIFunctionDeclaration>(_sharedTools);
            // HOT PATH — the two workhorse query tools stay always-loaded.
            tools.AddRange(new AzureQueryTools(tokens).Create());
            tools.AddRange(new GraphQueryTools(tokens).Create());
            // Savings ledger — flagship feature, small schemas, always available.
            tools.AddRange(new SavingsLedgerTools(tokens).Create());
            // COLD PATH — loaded on demand via tool search (see DeferredTool).
            tools.AddRange(DeferredTool.WrapAll(new LogAnalyticsQueryTools(tokens).Create()));
            tools.AddRange(DeferredTool.WrapAll(new StorageQueryTools(tokens).Create()));
            tools.AddRange(DeferredTool.WrapAll(new AnomalyTools(tokens).Create()));
            tools.AddRange(DeferredTool.WrapAll(new PricesheetTools(tokens).Create()));
            tools.AddRange(DeferredTool.WrapAll(new IdleResourceTools(tokens).Create()));
            tools.AddRange(DeferredTool.WrapAll(new UploadedFileTools(tokens).Create()));
            tools.AddRange(DeferredTool.WrapAll(new FaqTools(tokens).Create()));
            return tools;
        });
    }

    public async Task<CopilotSession> GetCurrentOrCreateAsync(long userId, string userLogin, string? entraOid)
    {
        // Fast path: user already has a current session id mapped.
        if (_telemetry.CurrentSessionId.TryGetValue(userId, out var currentId))
        {
            try
            {
                return await GetOrResumeAsync(userId, currentId, userLogin, entraOid);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Resume failed for {User} session={SessionId}, creating new", userLogin, currentId);
                _telemetry.CurrentSessionId.TryRemove(userId, out _);
            }
        }

        // Entra-connected users may have past sessions on disk from a prior run.
        // Pick the most recently modified one as the implicit "current".
        if (!string.IsNullOrEmpty(entraOid))
        {
            try
            {
                var workdir = GetWorkingDirectory(userId, entraOid);
                var listed = await _copilotClient.ListSessionsAsync(
                    new SessionListFilter { WorkingDirectory = workdir }, CancellationToken.None);
                var mostRecent = listed?
                    .OrderByDescending(s => s.ModifiedTime)
                    .FirstOrDefault();
                if (mostRecent is not null)
                {
                    _telemetry.CurrentSessionId[userId] = mostRecent.SessionId;
                    return await GetOrResumeAsync(userId, mostRecent.SessionId, userLogin, entraOid);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ListSessionsAsync failed for {User}, falling back to fresh session", userLogin);
            }
        }

        return await CreateNewAsync(userId, userLogin, entraOid);
    }

    /// <summary>
    /// Creates a brand-new Copilot session, registers it as the user's current,
    /// and returns it. The SDK auto-persists state under the per-user working
    /// directory so subsequent calls to <see cref="ListUserSessionsAsync"/>
    /// will find it.
    /// </summary>
    public async Task<CopilotSession> CreateNewAsync(long userId, string userLogin, string? entraOid)
    {
        var config = await CreateSessionConfigAsync(userId, entraOid);
        var session = await _copilotClient.CreateSessionAsync(config);
        _telemetry.LiveSessions[session.SessionId] = new LiveSessionInfo
        {
            Session = session,
            UserId = userId,
            BearerExpiry = _bearerTokenExpiry,
            AppliedEffort = IsReasoningModel(_deployment) ? _reasoningEffort : null,
        };
        _telemetry.CurrentSessionId[userId] = session.SessionId;
        _telemetry.ActiveSessions.Add(1);
        _logger.LogInformation("Created new Copilot session for {User} sessionId={SessionId}", userLogin, session.SessionId);
        return session;
    }

    /// <summary>
    /// Returns the live session if cached and the BYOK token is still fresh;
    /// otherwise resumes from disk (preserving the SDK-managed conversation
    /// history) and re-keys the live cache.
    /// </summary>
    public async Task<CopilotSession> GetOrResumeAsync(long userId, string sessionId, string userLogin, string? entraOid)
    {
        if (_telemetry.LiveSessions.TryGetValue(sessionId, out var live))
        {
            // BearerTokenProvider supplies a fresh token per model request, so a
            // cached live session never goes stale on token expiry — no recycle.
            _telemetry.CurrentSessionId[userId] = sessionId;
            return live.Session;
        }

        var resumeConfig = await CreateResumeConfigAsync(userId, entraOid);
        var resumed = await _copilotClient.ResumeSessionAsync(sessionId, resumeConfig, CancellationToken.None);
        _telemetry.LiveSessions[sessionId] = new LiveSessionInfo
        {
            Session = resumed,
            UserId = userId,
            BearerExpiry = _bearerTokenExpiry,
            AppliedEffort = IsReasoningModel(_deployment) ? _reasoningEffort : null,
        };
        _telemetry.CurrentSessionId[userId] = sessionId;
        _telemetry.ActiveSessions.Add(1);
        _logger.LogInformation("Resumed Copilot session for {User} sessionId={SessionId}", userLogin, sessionId);
        return resumed;
    }

    /// <summary>Recycles the same session id after a "Session not found" or expiry error.</summary>
    public async Task<CopilotSession> RecycleSessionAsync(long userId, string sessionId, string userLogin, string? entraOid)
    {
        await DisposeLiveAsync(sessionId);
        try
        {
            return await GetOrResumeAsync(userId, sessionId, userLogin, entraOid);
        }
        catch
        {
            // Session vanished from disk — fall back to a fresh one.
            return await CreateNewAsync(userId, userLogin, entraOid);
        }
    }

    public async Task<IReadOnlyList<SessionMetadata>> ListUserSessionsAsync(long userId, string? entraOid, CancellationToken ct = default)
    {
        // Both Entra and anonymous users have a deterministic workdir scope
        // (`/users/{oid}` vs `/anon/{userId}`), so we can safely list either.
        var workdir = GetWorkingDirectory(userId, entraOid);
        var listed = await _copilotClient.ListSessionsAsync(new SessionListFilter { WorkingDirectory = workdir }, ct);
        return listed?.OrderByDescending(s => s.ModifiedTime).ToList() ?? new List<SessionMetadata>();
    }

    /// <summary>
    /// Authoritative ownership check: returns true iff <paramref name="sessionId"/>
    /// lives under the caller's per-user working directory (Entra OID workdir or
    /// anon-userId workdir). All cross-session API surfaces (resume, delete,
    /// select, replay) MUST gate on this to prevent IDOR.
    /// </summary>
    public async Task<bool> UserOwnsSessionAsync(long userId, string? entraOid, string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(sessionId)) return false;
        // Fast path: a freshly-created or currently-live session is recorded in
        // LiveSessions with its owning UserId. The on-disk index used by
        // ListSessionsAsync can lag behind CreateSessionAsync by a few ms, which
        // would otherwise reject a session the user just created and collapse
        // all their parallel chats onto the "current session" fallback.
        if (_telemetry.LiveSessions.TryGetValue(sessionId, out var live) && live.UserId == userId)
            return true;
        var sessions = await ListUserSessionsAsync(userId, entraOid, ct);
        return sessions.Any(s => s.SessionId == sessionId);
    }

    public async Task DeleteUserSessionAsync(long userId, string sessionId, CancellationToken ct = default)
    {
        await DisposeLiveAsync(sessionId);
        if (_telemetry.CurrentSessionId.TryGetValue(userId, out var current) && current == sessionId)
            _telemetry.CurrentSessionId.TryRemove(userId, out _);
        _telemetry.RemoveTitle(sessionId);
        ChatEndpoints.ClearSessionContext(sessionId);
        try { await _copilotClient.DeleteSessionAsync(sessionId, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "DeleteSessionAsync failed for {SessionId}", sessionId); }
    }

    public void SetCurrentSession(long userId, string sessionId)
        => _telemetry.CurrentSessionId[userId] = sessionId;

    /// <summary>
    /// Read-only transcript load: resumes the session just long enough to read
    /// its persisted events, then disposes. Does NOT touch <see cref="AiTelemetry.CurrentSessionId"/>
    /// or the <c>ActiveSessions</c> gauge — viewing a past conversation must not
    /// switch the user's current thread or leak the live-session counter.
    /// </summary>
    public async Task<IReadOnlyList<SessionEvent>> LoadTranscriptAsync(string sessionId, long userId, string? entraOid, CancellationToken ct = default)
    {
        // If we already have it cached live (active chat in another tab), just
        // read off that instance — don't churn a second resume.
        if (_telemetry.LiveSessions.TryGetValue(sessionId, out var live))
        {
            return await live.Session.GetEventsAsync(ct);
        }

        var resumeConfig = await CreateResumeConfigAsync(userId, entraOid);
        try
        {
            var ephemeral = await _copilotClient.ResumeSessionAsync(sessionId, resumeConfig, ct);
            try { return await ephemeral.GetEventsAsync(ct); }
            finally { try { await ephemeral.DisposeAsync(); } catch { } }
        }
        catch (Exception ex) when (ex.Message.Contains("Session not found", StringComparison.OrdinalIgnoreCase))
        {
            // The ownership marker / session listing still exists on disk but the
            // underlying CLI session state is gone (deleted, TTL-expired, or a
            // listing-vs-state race). A read-only transcript load must degrade to
            // an empty conversation rather than surfacing HTTP 500 to the user
            // (observed in production: GET /api/sessions/{id}/messages -> 500).
            _logger.LogWarning("LoadTranscriptAsync: session {SessionId} not found on resume; returning empty transcript", sessionId);
            return Array.Empty<SessionEvent>();
        }
    }

    /// <summary>Lists session metadata under the persistent-user roots only — the
    /// janitor must never touch sessions outside <c>$COPILOT_HOME/users/</c> and
    /// <c>$COPILOT_HOME/anon/</c> (e.g. another container instance sharing the
    /// same Azure Files mount, or unrelated SDK state).</summary>
    public async Task<IReadOnlyList<SessionMetadata>> ListAllManagedSessionsAsync(CancellationToken ct = default)
    {
        var listed = await _copilotClient.ListSessionsAsync(new SessionListFilter(), ct);
        if (listed is null) return Array.Empty<SessionMetadata>();
        var usersRoot = Path.Combine(CopilotHome, "users");
        var anonRoot = Path.Combine(CopilotHome, "anon");
        return listed.Where(s =>
        {
            // Linux file paths are case-sensitive; Entra OIDs are lowercase
            // GUIDs and our roots are constructed from a known constant, so
            // an Ordinal compare is both correct and slightly faster.
            var c = s.Context?.WorkingDirectory ?? "";
            return c.StartsWith(usersRoot, StringComparison.Ordinal)
                || c.StartsWith(anonRoot, StringComparison.Ordinal);
        }).ToList();
    }

    public async Task DeleteSessionByIdAsync(string sessionId, CancellationToken ct = default)
    {
        await DisposeLiveAsync(sessionId);
        _telemetry.RemoveTitle(sessionId);
        ChatEndpoints.ClearSessionContext(sessionId);
        try { await _copilotClient.DeleteSessionAsync(sessionId, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "DeleteSessionAsync failed for {SessionId}", sessionId); }
    }

    private async Task DisposeLiveAsync(string sessionId)
    {
        if (_telemetry.LiveSessions.TryRemove(sessionId, out var live))
        {
            _telemetry.ActiveSessions.Add(-1);
            try { await live.Session.DisposeAsync(); } catch { }
        }
    }

    private async Task<SessionConfig> CreateSessionConfigAsync(long userId, string? entraOid)
    {
        // Seed token eagerly so the very first model call doesn't pay the
        // credential round-trip; afterwards BearerTokenProvider serves refreshes.
        var bearerToken = await GetAzureOpenAIBearerTokenAsync();
        var effort = IsReasoningModel(_deployment) ? _reasoningEffort : null;
        _logger.LogInformation("SessionConfig(create) model={Model} reasoningEffort={Effort} isReasoning={IsReasoning}",
            _deployment, effort ?? "<null>", IsReasoningModel(_deployment));
        return new SessionConfig
        {
            Model = _deployment,
            ReasoningEffort = effort,
            // Stream concise reasoning summaries so the UI can show live
            // "thinking" feedback during the otherwise-silent reasoning phase.
            ReasoningSummary = effort is null ? null : ReasoningSummary.Concise,
            Streaming = true,
            Tools = GetOrCreateUserTools(userId),
            ExcludedTools = ExcludedBuiltInTools,
            // Explicitly pin tool-search deferral ON (SDK 1.0.7 formalized the
            // option; default may drift across SDK/CLI bumps). Our DeferredTool
            // wrapper marks cold-path tools defer=Auto — this keeps the CLI
            // honoring those markers so per-request input tokens stay ~50% down.
            ToolSearch = new ToolSearchConfig { Enabled = true },
            WorkingDirectory = GetWorkingDirectory(userId, entraOid),
            OnPermissionRequest = PermissionHandler.ApproveAll,
            Provider = new ProviderConfig
            {
                // Azure AI Foundry exposes an OpenAI-compatible endpoint at /openai/v1/.
                // GPT-5 series models AND ReasoningEffort require the Responses API, which is
                // only reachable via the "openai" provider type with WireApi="responses".
                // The classic "azure" type uses the Chat Completions API (api-version 2024-10-21)
                // and does not support reasoning on these models — the request never completes.
                // See GitHub Copilot SDK BYOK docs (Azure AI Foundry OpenAI-compatible endpoint):
                // https://github.com/github/copilot-sdk/blob/main/docs/auth/byok.md
                Type = "openai",
                BaseUrl = $"{_endpoint.TrimEnd('/')}/openai/v1/",
                // Static seed for the first request; the provider callback below
                // takes precedence and is invoked per outbound model request.
                BearerToken = bearerToken,
                BearerTokenProvider = _ => GetAzureOpenAIBearerTokenAsync(),
                WireApi = "responses",
            },
            SystemMessage = new SystemMessageConfig
            {
                // Replace (not Append): Append ships the CLI's built-in multi-
                // thousand-token "GitHub Copilot CLI terminal assistant" prompt
                // (tone/code-editing rules irrelevant here) in EVERY request, on
                // top of ours. Our SystemPrompt is self-contained for FinOps.
                // Tool-calling still works — schemas travel at protocol level.
                Mode = SystemMessageMode.Replace,
                Content = SystemPrompt,
            },
        };
    }

    private async Task<ResumeSessionConfig> CreateResumeConfigAsync(long userId, string? entraOid)
    {
        var bearerToken = await GetAzureOpenAIBearerTokenAsync();
        var effort = IsReasoningModel(_deployment) ? _reasoningEffort : null;
        _logger.LogInformation("SessionConfig(resume) model={Model} reasoningEffort={Effort} isReasoning={IsReasoning} — NOTE: CLI may retain original-session effort",
            _deployment, effort ?? "<null>", IsReasoningModel(_deployment));
        return new ResumeSessionConfig
        {
            Model = _deployment,
            ReasoningEffort = effort,
            // Stream concise reasoning summaries so the UI can show live
            // "thinking" feedback during the otherwise-silent reasoning phase.
            ReasoningSummary = effort is null ? null : ReasoningSummary.Concise,
            Streaming = true,
            Tools = GetOrCreateUserTools(userId),
            ExcludedTools = ExcludedBuiltInTools,
            // See CreateSessionConfigAsync — keep deferral pinned on for resumes too.
            ToolSearch = new ToolSearchConfig { Enabled = true },
            WorkingDirectory = GetWorkingDirectory(userId, entraOid),
            OnPermissionRequest = PermissionHandler.ApproveAll,
            Provider = new ProviderConfig
            {
                // Azure AI Foundry exposes an OpenAI-compatible endpoint at /openai/v1/.
                // GPT-5 series models AND ReasoningEffort require the Responses API, which is
                // only reachable via the "openai" provider type with WireApi="responses".
                // The classic "azure" type uses the Chat Completions API (api-version 2024-10-21)
                // and does not support reasoning on these models — the request never completes.
                // See GitHub Copilot SDK BYOK docs (Azure AI Foundry OpenAI-compatible endpoint):
                // https://github.com/github/copilot-sdk/blob/main/docs/auth/byok.md
                Type = "openai",
                BaseUrl = $"{_endpoint.TrimEnd('/')}/openai/v1/",
                // Static seed for the first request; the provider callback below
                // takes precedence and is invoked per outbound model request.
                BearerToken = bearerToken,
                BearerTokenProvider = _ => GetAzureOpenAIBearerTokenAsync(),
                WireApi = "responses",
            },
            SystemMessage = new SystemMessageConfig
            {
                // Replace (not Append) — see CreateSessionConfigAsync for rationale.
                Mode = SystemMessageMode.Replace,
                Content = SystemPrompt,
            },
        };
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
    /// Generates a short (max ~6-word) human-readable title for the conversation
    /// using the same Azure OpenAI deployment that powers the chat. The Copilot
    /// CLI's <c>session.title_changed</c> event in this build just echoes the
    /// user's first prompt verbatim, so we override it with a real summary.
    /// Returns null on any failure (caller falls back to existing title).
    /// </summary>
    public async Task<string?> GenerateTitleAsync(string userMessage, string assistantReply, CancellationToken ct = default)
    {
        try
        {
            var token = await GetAzureOpenAIBearerTokenAsync();
            // Use the same OpenAI-compatible /openai/v1/ surface the BYOK chat
            // path uses — the classic ?api-version=2024-10-21 endpoint predates
            // the GPT-5 series and rejects reasoning parameters.
            var url = $"{_endpoint.TrimEnd('/')}/openai/v1/chat/completions";
            var messages = new object[]
            {
                new { role = "system", content = "Summarise the user's question into a 3-6 word title for a chat sidebar. No quotes, no trailing punctuation, no emoji. Title-case." },
                new { role = "user", content = $"USER: {Truncate(userMessage, 800)}\n\nASSISTANT: {Truncate(assistantReply, 800)}" },
            };
            // GPT-5 / o-series use `max_completion_tokens`; grok and GPT-4 use `max_tokens`.
            // Reasoning models spend completion tokens on hidden reasoning FIRST —
            // with a tiny cap the entire budget goes to reasoning and content comes
            // back empty. Give them headroom + minimal reasoning effort so the
            // title lands in the visible content.
            object body = IsReasoningModel(_deployment)
                ? new { model = _deployment, messages, max_completion_tokens = 512, reasoning_effort = "low" }
                : new { model = _deployment, messages, max_tokens = 24 };
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

    /// <summary>
    /// Returns true if the deployment is a reasoning model that accepts the
    /// <c>reasoning_effort</c> parameter and the <c>max_completion_tokens</c> field.
    /// GPT-5.x and o-series qualify; grok-4.3 / grok-4 / GPT-4.x do not.
    /// (grok-4-20-reasoning is the xAI reasoning variant — add it here if deployed.)
    /// </summary>
    private static bool IsReasoningModel(string deployment)
    {
        if (string.IsNullOrEmpty(deployment)) return false;
        var d = deployment.ToLowerInvariant();
        if (d.StartsWith("grok")) return d.Contains("reasoning");
        return d.StartsWith("gpt-5") || d.StartsWith("o1") || d.StartsWith("o3") || d.StartsWith("o4") || d.StartsWith("codex");
    }
}
