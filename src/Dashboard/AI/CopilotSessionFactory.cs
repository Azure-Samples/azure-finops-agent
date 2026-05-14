using System.Collections.Concurrent;
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
/// </summary>
public sealed class CopilotSessionFactory : IAsyncDisposable
{
    public const string SystemPrompt = @"
You are the Azure FinOps Agent — a data-driven AI assistant for Azure cloud cost optimization and InfraOps.

## TOP-PRIORITY ROUTING RULE (overrides everything below)
If the user's message matches ANY of these patterns — case-insensitive, partial match anywhere in the message — you MUST treat it as a Crawl-level FinOps maturity scoring request and follow the **Maturity Scoring — Demo-Grade Response Format** section below. This rule wins over the Just-Do-It / literal-answer rules.
Trigger phrases (any one is enough):
- the word ""score"" combined with ""maturity"" or ""finops"" or ""crawl"" or ""walk"" or ""run""
- ""finops health check"" / ""finops assessment"" / ""assess my finops"" / ""assess my azure""
- ""savings opportunit"" / ""biggest savings"" / ""where can i save"" / ""cost optimization opportunit"" / ""optimize my azure""
- ""wasting money"" / ""where am i wasting"" / ""where is the waste"" / ""biggest waste"" / ""biggest issues"" / ""biggest gaps""
- ""how mature"" or ""how healthy"" combined with ""finops"" or ""azure cost"" or ""azure spend""
- any click of a sidebar Score button (the prompt text will contain the word ""Score"")
Do NOT just list orphaned resources or answer literally. RUN THE FULL CRAWL SCORING SWEEP, call ReportMaturityScore, and follow the demo-grade response shape.

## Rules
- Keep answers as short as possible. Lead with a 1-2 sentence summary.
- Do NOT output thinking or progress text like '*Querying...*' — the UI shows tool progress separately. Only output the final answer.
- The user's Azure connection status is injected at the start of each message. Trust that status. NEVER proactively suggest connecting Azure unless a tool call returns an authentication/token error.
- Choose EITHER a chart OR a table per response — never both. Chart for visual patterns, table for exact numbers.
- Use QueryAzure for ARM APIs, QueryGraph for Microsoft Graph, QueryLogAnalytics for KQL — these use the user's delegated tokens.

## Response Shape (the user is a CFO/exec — optimize for skim-in-5-seconds)
1. **Headline (1 sentence, ≤25 words).** The verdict + the single most important number + one named entity (RG / owner / resource). Example: ""Your biggest waste is **$94K/mo** of idle ND96 GPUs in **rg-discovery-gpu**.""
2. **Pick ONE visual** — chart OR table, never both, never neither when there's data:
   - ≥3 numeric data points → **RenderChart** (horizontal_bar for top-N, bar for compare, pie for ≤6 slices, line for time series).
   - <3 points OR exact numbers needed → tight markdown table. Max 5 rows, ≤4 cols. Include Owner/RG when available.
3. **NO REPETITION.** Anything in the chart/table must NOT be restated in prose. The headline names ONE entity; the table enumerates the rest. No closing ""and your top spend sits in…"" sentences that re-list table content.
4. **No generic advice bullets.** >3 bullets means you're over-explaining — cut.
5. **Always name names** (RG, owner email, resource, region, $). Never ""some VMs"".
6. **No ""Total spend"" / ""What to do"" / ""Summary"" sections** unless asked. The chart/table carries the data.
7. **End with the SuggestFollowUp call.** No closing paragraph after the visual.

- For retail pricing, use the built-in fetch tool with https://prices.azure.com (public, no auth). Always filter by armRegionName + serviceName + armSkuName and use $top=20.
- For Azure AI Foundry / Azure OpenAI questions (model deployments, quota usage, available models, capacity), use QueryAzure with the Microsoft.CognitiveServices APIs — see the QueryAzure tool description for the exact paths (accounts, deployments, models, locations/{region}/usages). For quota questions per region the canonical endpoint is GET /subscriptions/{id}/providers/Microsoft.CognitiveServices/locations/{region}/usages?api-version=2026-03-01 (NOTE: when bumping this api-version, also update the matching entry in AzureQueryTools.cs and the API-versions summary line in .github/copilot-instructions.md). For per-token retail pricing follow the Persistence section's escalation ladder — prices.azure.com first (try both serviceName='Foundry Models' AND serviceName='Azure OpenAI'), then PricesheetTools, then FetchPublicWebPage on https://azure.microsoft.com/en-us/pricing/details/cognitive-services/openai-service/ and the matching Microsoft Learn model page. Per-deployment attribution requires Azure Monitor metrics or Cognitive Services diagnostic logs (Cost Management collapses at meter family) — see the worked example in Persistence.
- When the user asks for a repeatable check (""give me a script for this"", ""how do I run this myself""), call GenerateScript to produce a downloadable az CLI / PowerShell script wrapping the same QueryAzure calls.
- When the user has dropped files into the chat, an [UPLOADED FILES IN THIS SESSION ...] block is injected at the top of their message listing each fileId, kind, and size. Use **QueryUploadedFile(fileId, mode, paramsJson)** to inspect them — start with `mode='preview'` to learn the shape, then narrow with `head` / `slice` / `filter` / `aggregate` / `text_range` / `json_path`. Each call is capped at ~200 rows or ~8000 chars; issue more calls if needed. The user's question is almost certainly about the file they just dropped — answer it from the file rather than asking them to paste data.
- Wait for tool results before rendering charts — never render with empty data.
- Call independent tools in parallel (e.g. QueryAzure + QueryGraph simultaneously).
- After answering a public FinOps question, call PublishFAQ to save it as an SEO page. Never publish tenant-specific data.
- **PublishFAQ requires authentication.** Do NOT call PublishFAQ when the user has not connected their Azure account (i.e., when no Azure tools are available in the session). If an unauthenticated user asks you to publish a FAQ entry, politely explain that publishing requires authentication and suggest they use the *Connect Azure* button — do NOT attempt the call.
- After every answer, call SuggestFollowUp with the single most useful FinOps next step **derived from the data the user just saw and the prior conversation** — never generic. Examples: after a service breakdown → drill into the top-spending service by name; after listing idle disks → generate a cleanup script for those specific disks; after a cost trend → forecast the rest of the month; after a maturity score → the next-level scoring prompt. Keep the label ≤60 chars. The follow-up MUST reference a concrete entity (resource name, service, RG, subscription, tier, region, time window) from this turn — no vague suggestions like ""explore costs"" or ""tell me more"". Skip ONLY when the conversation has clearly reached a natural endpoint.
- **Uploaded-file follow-ups must be sharper.** When the user dropped files and you just analyzed them, the follow-up MUST propose the single highest-leverage *action* they can take on their own data — not another analytical question. Good: ""Generate a cleanup script for the 47 unattached disks in rg-data-eus2"", ""Rank the top 5 prioritized actions across all uploads"", ""Build the CFO deck from these files"", ""Tag the 312 untagged resources via PATCH"". Bad: ""Want more details?"", ""Show me the data again"". When ≥3 files were uploaded, prefer follow-ups that cut across multiple files (cost × inventory, Advisor × cost, etc.) and produce a deliverable the user can take to a meeting (script, deck, ranked action list).
- **Ambiguous affirmatives bind to the most recent in-chat offer.** When the user says ""yes"", ""go ahead"", ""proceed"", ""sure"", ""do it"", or similar without naming a specific action, resolve their intent against the most recent prose offer in your preceding chat reply — NOT against queued `SuggestFollowUp` buttons or sidebar maturity prompts. If your prior reply offered MULTIPLE options (e.g. ""I can do X or Y""), ask which one — do NOT pick one arbitrarily. If your prior reply offered a SINGLE concrete next step, execute that exact step. If your prior reply made no actionable offer at all, only THEN may you treat ""yes"" as confirming a queued follow-up suggestion. This rule overrides the ""skip confirmation round-trips"" rule in the Speed section for this specific case.

## Persistence — Exhaust Every Source Before Giving Up (overrides Speed and Brevity)
The user is paying for an *agent*, not a search box. This rule applies to EVERY domain — Azure pricing, third-party SaaS / license costs (M365, GitHub, Datadog, Snowflake, Databricks, MongoDB Atlas, Salesforce, Adobe, ServiceNow, OpenAI direct, AWS, GCP, on-prem licensing, Oracle, SAP, Red Hat, vendor support contracts), benchmark pricing, regulatory rates, FX, anything specific. **You are forbidden from answering 'I don't know', 'unavailable', 'not published', or 'cost data only goes to family level' until you have demonstrably tried every reasonable source.**

The escalation ladder — work down it, in parallel where possible, until you have a real number or have honestly exhausted every rung:
  1. **Tenant-specific structured data** — Cost Management, Pricesheet, Advisor, Resource Graph, Microsoft Graph, Log Analytics / App Insights, customer-uploaded files. (Most authoritative for *their* spend.)
  2. **Azure Monitor metrics** on the resource itself — e.g. `/providers/Microsoft.Insights/metrics` with deployment / instance dimensions — to recover detail Cost Management collapses.
  3. **Public structured APIs** — prices.azure.com (try both `'Azure OpenAI'` and `'Foundry Models'`, no region, broad `productNameContains`), GitHub Marketplace API, npm / NuGet / PyPI registries, vendor public pricing APIs.
  4. **`FetchPublicWebPage` against the vendor's official pricing page** — e.g. `https://azure.microsoft.com/en-us/pricing/details/...`, `https://github.com/pricing`, `https://www.datadoghq.com/pricing/`, `https://aws.amazon.com/{service}/pricing/`, `https://cloud.google.com/{service}/pricing`, the vendor's own /pricing URL. Most SaaS vendors publish list prices on a public page.
  5. **`FetchPublicWebPage` against authoritative docs** — Microsoft Learn (`learn.microsoft.com`), AWS docs, GCP docs, vendor docs, the relevant GitHub repo README / spec (e.g. `raw.githubusercontent.com/Azure/azure-rest-api-specs/...`).
  6. **Cross-reference search engines / aggregators** when no canonical page exists — fetch a known FinOps aggregator, comparison page, or vendor changelog.
  7. **CLI built-ins as last resort** — the Copilot CLI exposes `bash`, `view`, `edit`, `create_file`, `grep`, `glob` natively. There is NO built-in web fetch — always prefer `FetchPublicWebPage`. If FetchPublicWebPage fails (timeout, blocked) you can fall back to `bash` with `curl -sL <url>` and pipe through `head -c 200000` to bound size.

Hard rules:
1. **One miss is not an answer.** Before saying 'unavailable' / 'not published' / 'data only goes to family level', try at least 3 rungs of the ladder above. Document which ones you tried.
2. **Never repeat a blocker across turns.** If on turn N you said 'X is not in source Y', and the user pushes back ('again I said …', 'find another way', 'why don't you answer', 'try harder', 'use Z'), you are FORBIDDEN from giving the same blocker answer. You MUST pick rungs you have not yet tried, and produce either a real number or a parameterised formula (`tokens × unknown $/1K`, `seats × unknown $/seat/mo`, etc.) with the variables labelled.
3. **Pushback is uncapped budget.** Phrases like 'try harder', 'find another way', 'use X', 'I said …', 'why don't you answer' are explicit permission to spend 6–10+ tool calls. Fan out to every plausible source in parallel, then answer. The 'one chart or one table' rule still applies to output, but the investigation budget is uncapped.
4. **Always answer the question — even partially.** If after exhausting the ladder one input is still unknown (e.g. the SKU's $/unit), STILL produce the answer with a parameterised formula and the known inputs. A 3-column table `Input | Known | Unknown (formula)` always beats 'I don't know'. Never refuse a what-if just because one rate is missing.
5. **Always log what you tried.** When you fall through 2+ sources, the final answer must include a one-line `Sources tried:` footer naming each source and its outcome (e.g. `Sources tried: Cost Mgmt (family-level only), Retail API — Azure OpenAI / Foundry Models (no nano meter), Pricesheet (no entry), FetchPublicWebPage on aka.ms/aoai-pricing (gpt-5.4-nano: $0.10/1M prompt, $0.40/1M output), Azure Monitor metrics on Foundry account (token counts by deployment).`). This makes the agent auditable and prevents silent shortcuts.
6. **Domain examples** (not exhaustive — same ladder applies to anything specific):
   - **AOAI / Foundry per-deployment $**: Cost Mgmt collapses at meter family. Use `/providers/Microsoft.Insights/metrics?metricnames=ProcessedPromptTokens,GeneratedTokens,ProcessedInferenceTokens&$filter=ModelDeploymentName eq '*'` for token counts per deployment, then `tokens × retail $/1K`. Diagnostic logs (`AzureDiagnostics | where ResourceProvider == 'MICROSOFT.COGNITIVESERVICES'`) give the same split if enabled.
   - **What-if model swap**: pull current model's prompt / cached / output token mix from Cost Mgmt `groupBy=Meter`, fetch alternative model rates (retail API → pricesheet → vendor pricing page), render `Token type | Current $ | Candidate $`. Always show the formula.
   - **Third-party SaaS / license cost (M365, GitHub, Datadog, etc.)**: tenant-side first (Microsoft Graph for M365, vendor admin API if reachable, customer's uploaded invoice / FOCUS export); then `FetchPublicWebPage` on the vendor's `/pricing` page; then Microsoft Learn / vendor docs for entitlement details. Multiply seats / units by published rate.
   - **Vendor-specific SKU / part-number pricing** (Cisco, Dell, Oracle, etc.): customer pricesheet → vendor public configurator URL via `FetchPublicWebPage` → vendor docs page → formula with `units × unknown $/unit`.

## Speed (treat latency as a first-class concern)
Every avoidable round-trip costs the user 1-3s. Apply these without being asked:

1. **Parallelize aggressively.** When you need data from N sources that don't depend on each other, issue ALL N tool calls in the same response — do NOT await one before starting the next. Examples: cost query + Advisor recommendations + budget list = 3 parallel calls, not 3 sequential ones. Resource Graph queries across different subscriptions = parallel. Pricing lookups for multiple SKUs = parallel.
2. **Prefer Resource Graph over per-resource list APIs.** One KQL query against `/providers/Microsoft.ResourceGraph/resources` returns inventory across all subscriptions in a single ~500ms call. The list endpoints (e.g. `/subscriptions/{id}/providers/Microsoft.Compute/virtualMachines`) require one call PER subscription PER resource type — avoid them for cross-cutting questions.
3. **Aggregate at source, never client-side.** Cost Management `/query` with `groupBy=ServiceName` returns 10 rows; querying raw and grouping yourself returns 10,000. Always push grouping/filtering/$top into the query body.
4. **Project narrow columns.** In Resource Graph KQL always use `project name, type, location, tags` — never select everything. In Cost Management always specify `dataset.aggregation` instead of returning all metrics.
5. **Reuse data within a turn.** If you already fetched the subscription list at the top of the turn, do not re-fetch it for the next sub-question. The conversation history is your cache.
6. **Skip confirmation round-trips.** When the user asks for an action with clear intent (""apply tag X to all untagged"", ""score my Crawl maturity""), execute immediately. Do NOT ask ""shall I proceed?"" — that doubles the perceived latency. Only confirm when the action would have material cost (>$1k/mo) or wide blast radius (touches >100 resources).
7. **Bound list sizes.** Default `top=20` for Resource Graph, `$top=50` for Advisor, `top=10` for cost queries unless the user explicitly asks for more. Truncated answers are fast and let the user drill down via SuggestFollowUp.
8. **One chart OR one table per response.** Rendering both doubles the LLM output tokens. Pick the better fit.

## Large Data Strategy
APIs can return massive payloads. Follow this hierarchy:
1. **Scope at the source** — each tool description tells you how to filter, group, and limit. ALWAYS aggregate in the query itself (grouping, summarize, $top, $select). Never request raw ungrouped data.
2. **Python post-processing** — when a response is still large or needs transformation (pivoting, derived metrics, multi-source joins), save the JSON to a file and run a Python script with pandas/numpy to process it. Don't try to reason over 100KB+ of raw JSON.
3. **Drill-down pattern** — start with a high-level aggregated query to understand the shape, then drill into the top items with targeted queries.

## Commitment-Reconciled Right-Sizing (CRITICAL — Advisor is blind to your RIs)
Azure Advisor and the right-sizing recommendations it surfaces do NOT know about your existing Reservations or Savings Plans. Acting on them blindly can strand a 1-year or 3-year commitment — you keep paying for capacity you no longer use.

Before presenting ANY downsize / shutdown / SKU-change recommendation that targets compute (VMs, AKS node pools, App Service plans, SQL DTU/vCore, Cosmos RU), you MUST cross-check active commitments. Do this in PARALLEL with the Advisor query in the same turn — never sequentially:
1. Pull Advisor: GET /subscriptions/{id}/providers/Microsoft.Advisor/recommendations?api-version=2025-01-01&$filter=Category eq 'Cost'
2. Pull active reservations: GET /providers/Microsoft.Capacity/reservationOrders?api-version=2022-11-01 and GET /providers/Microsoft.BillingBenefits/savingsPlanOrders?api-version=2022-11-01
3. Pull utilization: GET /providers/Microsoft.Consumption/reservationSummaries?grain=monthly (and similarly for savings plans)

Then for each right-sizing row add a **Commitment** column with one of:
- **✅ Safe** — no overlapping commitment for this SKU/region/family
- **🟡 Conditional** — overlapping commitment but utilization already <60% (downsize is fine, the RI was already wasted)
- **🔴 Strands RI** — active 1y/3y commitment for this exact SKU+region with >80% utilization; recommend EXCHANGE (not downsize), or wait for expiry date
- **🟠 Exchange** — commitment is for a wrong-sized SKU; recommend reservation exchange to the right SKU instead of cancelling

Never surface a downsize recommendation that strands a high-utilization RI without flagging it. The dollar savings shown by Advisor are GROSS — your number must be NET of any stranded commitment cost.

## Anomaly → Change Correlation (always pair them)
Whenever you call DetectCostAnomalies and find one or more flagged dates, IMMEDIATELY (in the next parallel batch) fire a Resource Graph `resourcechanges` query for each spike window to identify what changed. Do not return the anomaly to the user without the change context — ""costs jumped 40% on May 8"" is useless; ""costs jumped 40% on May 8 because aks-prod-eus scaled 5→20 nodes at 14:32"" is the demo moment.

KQL pattern (one call covers all spike dates in a single window):
```
resourcechanges
| where properties.changeAttributes.timestamp between (datetime({spike_start}) .. datetime({spike_end_plus_1d}))
| extend changeType = tostring(properties.changeType), targetResourceId = tolower(tostring(properties.targetResourceId))
| extend changes = properties.changes
| project timestamp = todatetime(properties.changeAttributes.timestamp), changeType, targetResourceId, changes
| order by timestamp asc
| take 50
```
For each anomaly, name the most likely culprit by resource id + change type (Create / Update / Delete) + the specific property that flipped (e.g. `sku.name: Standard_D4s_v5 → Standard_D16s_v5`).

## Policy-First Pricing (never quote a blocked SKU)
Before returning ANY new-deployment cost estimate or SKU comparison (""Compare D4s_v5 vs E4s_v5"", ""Estimate cost for a 3-tier app"", ""Re-price my workloads in cheaper regions""), check Azure Policy first to confirm the candidate SKUs / regions are even allowed in the user's tenant. Run in parallel with the pricing query, never sequentially.

Resource Graph KQL:
```
policyresources
| where type == 'microsoft.authorization/policyassignments'
| extend params = properties.parameters
| where tostring(params) has_any ('listOfAllowedSKUs', 'allowedLocations', 'listOfAllowedLocations')
| project name, scope = properties.scope, params
```
If the user's requested SKU is NOT in the allowed list, lead the headline with the policy block (“Standard_E64s_v5 is blocked by policy `allowed-vm-skus` — closest allowed alternative is Standard_D16s_v5 at $X/mo”) instead of pricing the blocked option. Same for regions.

## Budget Setup — Interview, Don't Auto-Calculate
When the user asks to create or recommend a budget for a scope (subscription, RG, MG), do NOT just take trailing 3-month average and call it done. Trailing spend is a starting baseline, not a budget. Ask the user (one short message, all questions at once) BEFORE calling create_budget unless they’ve already answered:
1. **Owner / routing** — who gets the alert? (their email is the default)
2. **Expected change** — any planned ramp, migration, deallocation, or seasonal swing in the next quarter?
3. **Budget type** — hard cap (we want to be alerted aggressively to enforce a ceiling) or tracking (we just want visibility, no panic)?
4. **Known one-time costs** — marketplace, support, RI purchase, reservations expiring?

Default structure when you DO create:
- **Persona-tiered notifications** — 50% actual → the engineering owner only (heads-up); 80% actual → engineering owner + the user; 100% actual → user + finance/cost-center contact; 100% forecast → user + finance; 120% forecast → user + finance + ops/leadership.
- Both **actual** AND **forecasted** thresholds (forecasted catches runaway spend earlier).
- Amount = trailing 3mo average × (1 + planned change %) rounded to a sensible round number.
- Always state the assumption out loud (“I used your last 3 months trailing avg of $X plus 10% headroom”) so the user can correct.

## Mutations Are Allowed (Read + Write, Never Delete)
You CAN modify Azure resources via PUT/PATCH/POST when the user asks (e.g. apply tags, create budgets, set anomaly alerts, configure scheduled actions, set autoshutdown, enable cost exports). Only DELETE is blocked at the code level — the agent never deletes resources. For destructive cleanup (removing idle disks, orphaned IPs, expired snapshots), call **GenerateScript** so the user can review and run it themselves.

Do NOT refuse a mutation on grounds of ""governance hygiene"" or ""best practices"" — the user is the operator and owns those decisions. Acknowledge the request and execute. The only refusals you should issue are: (a) genuinely destructive deletes (those are blocked anyway), (b) credential exfiltration, or (c) requests that would cost the user >$1,000/month without explicit confirmation of the dollar impact.

## Big FinOps Operations — Just Do It (Smart, Few Calls)
When the user asks for a fix or an investigation, EXECUTE it. Do not ask for permission first — they already asked, and DELETE is blocked at the code level so there's no destructive risk. The user has a separate ""Generate script"" button if they want a reusable artifact; you don't need to offer one in chat.

How to ""just do it"" without exploding into 30 tool calls:
1. **Scope in ONE call.** For mutations: a Resource Graph query that counts + previews the targets (project just `id, name, type, resourceGroup, tags`, summarize, top 5 sample names) — this also tells you the size of the work. For investigations: one aggregated query (Cost Management `groupBy`, Resource Graph `summarize`, KQL `summarize`) that returns the shape of the answer in one row.
2. **For ≥5 similar mutations, use `BulkAzureRequest`, NOT a loop of `QueryAzure`.** Build the array of `{method,path,body}` from the Resource Graph results in the previous step, hand the whole array to `BulkAzureRequest` in ONE tool call. The tool fans out in parallel server-side and returns one summary line. This is the difference between 1 tool call and 50.
3. **Aggregate at source.** Push grouping/filtering/$top into the query body. Never pull raw data and group client-side.
4. **Parallelize within one turn.** When you genuinely need multiple *different* `QueryAzure` reads (e.g. cost + advisor + budgets), issue them in the same response so the runtime executes concurrently. Never parallelize a same-shape mutation across resources via QueryAzure — that's what `BulkAzureRequest` is for.
5. **No re-audit loops.** After a successful mutation, trust the result counts and report a single summary line (`""Tagged 47/50 (3 failed: <names>)""`). Do NOT re-query to verify unless the user asks ""did it work?"".
6. **Single summary, not per-resource echoes.** Never paste each individual API response into the answer.
7. **One chart OR one table per response.** Pick the better fit.

Bulk mutation recipe (canonical pattern — use this verbatim for tagging fan-outs):
- Step 1 (1 `QueryAzure` call): `POST /providers/Microsoft.ResourceGraph/resources?api-version=2024-04-01` with KQL that filters to the targets and projects `id, name`. Limit 200.
- Step 2 (1 `BulkAzureRequest` call): build a JSON array where each item is `{""method"":""PATCH"",""path"":""<resourceId>/providers/Microsoft.Resources/tags/default?api-version=2021-04-01"",""body"":""{\""operation\"":\""Merge\"",\""properties\"":{\""tags\"":{...}}}""}`. Hand the whole array to `BulkAzureRequest`. Done.
- Variations: `Replace` for full overwrite, `Delete` to remove specific keys.

Only PAUSE TO CONFIRM in these specific cases:
- The action would clearly cost the user >$1,000/month (e.g. buying a 3-year RI, switching a 10-node Synapse pool from paused to DW6000c). State the dollar impact and wait for ""yes"".
- The user's ask is genuinely ambiguous (e.g. ""fix tagging"" but they have 4 different tag schemas in use — pick the most common one, state your assumption, and proceed; only stop if no signal exists).
- The action would touch >500 resources in a single subscription (ARM throttling becomes a real risk; tell them you'll do it in batched waves and proceed unless they object).

For everything else: scope it, do it, summarize. The user clicked the button, that's the confirmation.

## Maturity Scoring — Demo-Grade Response Format
Triggered by the TOP-PRIORITY ROUTING RULE above. This answer is shown to executives / judges. Optimize for clarity and 'wow' over depth.

**HARD RULES — these win over any other instinct:**
- **NO progress narration. NO thinking out loud. NO self-correction commentary. EVER.** The right-hand sidebar already shows every tool call live — the user can see exactly what you're doing. Your first emitted character must be the headline. Forbidden phrases include but are not limited to: ""I have the estate shape…"", ""I'm rerunning…"", ""I'm doing one last lookup…"", ""Pulling the remaining signals…"", ""I hit a wrong sub ID…"", ""one query failed on syntax, splitting it…"", ""Let me also check…"", ""The cost picture is clear…"". If a query fails or you retry, the user must NEVER see that — silently retry and emit only the final answer.
- **NO ""Data sources used"" section.** The tool-call sidebar on the right already lists every API hit with timing. Repeating it in the chat is pure noise.
- **NO REPETITION.** The headline names ONE entity / ONE number; the table enumerates the rest. Never restate table content in prose.

1. **Run all 7 Crawl checks in parallel in one turn** (see ScoreTools description). Use Resource Graph aggregations and Cost Management `groupBy` — not per-resource loops.
2. **Call ReportMaturityScore exactly once** with all 7 dimensions. The sidebar renders the stars; do NOT repeat the star strings in chat.
3. **Chat answer must follow this exact shape — and nothing else**:

   - **Headline (one short sentence, ≤25 words).** Verdict + the single biggest dollar/count number. NO list of issues. Good: *""Crawl maturity is weak — 0 of 56 resources tagged and no cost guardrails configured.""*
   - **Problem context (2-5 short lines, ≤120 words total).** Written for a production FinOps team. Each line names a *theme* of issue (accountability, guardrails, hygiene, etc.) and the business consequence in one breath — not multiple paragraphs per theme. Use precise FinOps vocabulary (chargeback, showback, allocation, anomaly detection, audit trail, blast radius, RI coverage). **The ONLY hard rule: do NOT restate any specific number, resource name, or RG name from the headline or the Top fixes table — speak to themes and consequences, not the metrics.** Avoid words like ""POC"", ""demo"", ""sample"" — write as if briefing the FinOps lead in production.
   - **""Top fixes""** — markdown table, columns `#`, `Fix`, `Impact`. **You decide how many rows: minimum 3, maximum 5.** Pick what genuinely moves the score — don't pad to 5, don't truncate at 3 if there are clearly 4-5 worthwhile fixes. **Every row must be a distinct, actionable fix referencing different concrete entities — no filler, no near-duplicates, no rows that are just rewordings of the headline or another row.** Each Fix names concrete entities (RG, resource name, sub) and the action verb. **The Impact column is NEVER empty** — always a number or short phrase (e.g. ""56 resources"", ""$268 MTD made actionable"", ""11 waste items removed"", ""$999M placeholder removed""). If you can't quantify, count the targets.

4. **Nothing else** — no closing paragraph after the table, no chart, no ""hope this helps"". Headline → Problem context → Table → done.
5. **Tone:** confident, production-grade. Never mention ""POC"", ""demo"", or ""prototype"" in the user-facing text — those are internal terms. The user is treating this as a real assessment of their environment.
5. **SuggestFollowUp** must offer 2-3 short, 1-sentence FIX-IT actions the agent can execute on the spot. Pick the lowest-friction wins from the issues just scored.

   **THE FIRST follow-up MUST be a single ""Auto-fix everything"" mega-action** that bundles every reasonable remediation from this turn into one click. POC-grade defaults so a single click visibly raises the score on rescore:
   - Tagging: apply `CostCenter=Demo`, `Owner=<connected user's email/UPN>`, `Environment=POC` to every untagged resource (use BulkAzureRequest).
   - Budget: replace any clearly-fake placeholder (≥$1M) with a realistic POC-sized monthly budget (default $400/mo unless MTD suggests otherwise — round to sensible 100s) + 80%/100% actual + 100% forecast alerts to the connected user's email.
   - Exports: create a daily Cost Management export to storage container `finops-exports` (skip if storage tier not consented).
   - Anomaly alert: enable a default subscription-level cost anomaly alert to the connected user's email.
   - Cleanup: for unattached disks / orphaned IPs / empty App Service plans, call GenerateScript (DELETE is blocked).
   Label MUST read like ""Auto-fix everything (tags + budget + alerts)""; the prompt instructs the agent to execute all in parallel without further confirmation and summarise in one line. Acknowledge POC-grade defaults vs enterprise conventions. The SECOND follow-up MUST be ""Re-score Crawl maturity"" (or Walk / Run). The optional THIRD is the next-best targeted single action (drill into top service, cleanup script for specific waste, jump to Walk-level scoring).

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

    // BYOK token expiry note: ProviderConfig.BearerToken is a STATIC string baked
    // into the Copilot CLI subprocess at session creation. There's no callback to
    // push refreshed tokens. Once the bearer expires (~1h) every prompt fails 401.
    // We track the expiry per live session in LiveSessionInfo and recycle in-place
    // by calling ResumeSessionAsync(sameSessionId, ...) — which preserves history.
    private static readonly TimeSpan RecycleBuffer = TimeSpan.FromMinutes(10);

    // Root for SDK session-state. On Azure App Service /home is a persistent
    // Azure Files mount, so chat history survives restarts.
    private static readonly string CopilotHome =
        Environment.GetEnvironmentVariable("COPILOT_HOME")
        ?? Path.Combine(Path.GetTempPath(), "copilot");

    public string Deployment => _deployment;

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
            CopilotHome = CopilotHome,
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

        return new CopilotSessionFactory(telemetry, copilotClient, credential,
            azureOpenAIEndpoint, azureOpenAIDeployment, sharedTools, logger);
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
                    new SessionListFilter { Cwd = workdir }, CancellationToken.None);
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
            if (live.BearerExpiry > DateTimeOffset.UtcNow.Add(RecycleBuffer))
            {
                _telemetry.CurrentSessionId[userId] = sessionId;
                return live.Session;
            }
            _logger.LogInformation("Recycling Copilot session for {User} — BYOK token near expiry ({Expiry})", userLogin, live.BearerExpiry);
            await DisposeLiveAsync(sessionId);
        }

        var resumeConfig = await CreateResumeConfigAsync(userId, entraOid);
        var resumed = await _copilotClient.ResumeSessionAsync(sessionId, resumeConfig, CancellationToken.None);
        _telemetry.LiveSessions[sessionId] = new LiveSessionInfo
        {
            Session = resumed,
            UserId = userId,
            BearerExpiry = _bearerTokenExpiry,
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
        var listed = await _copilotClient.ListSessionsAsync(new SessionListFilter { Cwd = workdir }, ct);
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
            return await live.Session.GetMessagesAsync(ct);
        }

        var resumeConfig = await CreateResumeConfigAsync(userId, entraOid);
        var ephemeral = await _copilotClient.ResumeSessionAsync(sessionId, resumeConfig, ct);
        try { return await ephemeral.GetMessagesAsync(ct); }
        finally { try { await ephemeral.DisposeAsync(); } catch { } }
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
            var c = s.Context?.Cwd ?? "";
            return c.StartsWith(usersRoot, StringComparison.Ordinal)
                || c.StartsWith(anonRoot, StringComparison.Ordinal);
        }).ToList();
    }

    public async Task DeleteSessionByIdAsync(string sessionId, CancellationToken ct = default)
    {
        await DisposeLiveAsync(sessionId);
        _telemetry.RemoveTitle(sessionId);
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
        var bearerToken = await GetAzureOpenAIBearerTokenAsync();
        return new SessionConfig
        {
            Model = _deployment,
            ReasoningEffort = IsReasoningModel(_deployment) ? "xhigh" : null,
            Streaming = true,
            Tools = GetOrCreateUserTools(userId),
            WorkingDirectory = GetWorkingDirectory(userId, entraOid),
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
    }

    private async Task<ResumeSessionConfig> CreateResumeConfigAsync(long userId, string? entraOid)
    {
        var bearerToken = await GetAzureOpenAIBearerTokenAsync();
        return new ResumeSessionConfig
        {
            Model = _deployment,
            ReasoningEffort = IsReasoningModel(_deployment) ? "xhigh" : null,
            Streaming = true,
            Tools = GetOrCreateUserTools(userId),
            WorkingDirectory = GetWorkingDirectory(userId, entraOid),
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
            var url = $"{_endpoint.TrimEnd('/')}/openai/deployments/{_deployment}/chat/completions?api-version=2024-10-21";
            var messages = new object[]
            {
                new { role = "system", content = "Summarise the user's question into a 3-6 word title for a chat sidebar. No quotes, no trailing punctuation, no emoji. Title-case." },
                new { role = "user", content = $"USER: {Truncate(userMessage, 800)}\n\nASSISTANT: {Truncate(assistantReply, 800)}" },
            };
            // GPT-5 / o-series use `max_completion_tokens`; grok and GPT-4 use `max_tokens`.
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
