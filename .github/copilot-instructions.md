<!-- last refreshed: 2026-09-15 -->

# Azure FinOps Agent — Copilot Instructions

## Purpose

Azure FinOps Agent is an open-source Azure sample and delivery accelerator. It combines conversational AI with Azure Cost Management, ARM, Resource Graph, Advisor, Microsoft Graph, Log Analytics, public pricing, file analysis, visualizations, remediation scripts, and scheduled jobs.

It is designed for customers to deploy into **their own tenant and subscription**. Never commit maintainer or customer tenant IDs, subscription IDs, resource IDs, generated resource names, app IDs, user principal names, email addresses, IP addresses, connection strings, or deployment credentials.

## Stack

- Backend: .NET 10 minimal API in `src/Dashboard`
- Frontend: Vue 3 + Vite + ECharts in `src/Dashboard/frontend`
- Agent runtime: GitHub Copilot SDK with Azure OpenAI BYOK
- Default model: `gpt-5.6-luna`, version `2026-07-09`, using the Responses API. Existing-account reuse requires that deployment to exist and the app identity to have account-scoped inference access. Verify available model-specific quota; deleting a different model does not free Luna quota.
- Authentication: anonymous chat plus optional multi-tenant Entra OAuth
- Hosting: Linux container on Azure App Service
- Infrastructure: `azure.yaml` + Bicep under `infra`
- Observability: OpenTelemetry + Application Insights

The SDK and bundled Copilot CLI are one compatibility unit. Let the installed `GitHub.Copilot.SDK` package supply `CopilotCliVersion`; do not retain an override from an older SDK. Before accepting an SDK bump, verify its exact platform CLI packages are published. A stale runtime can pass text/tool tests while breaking protocol features such as native image attachments.

## Core architecture

- A shared `CopilotClient` manages per-user `CopilotSession` instances.
- Session state is persisted under `COPILOT_HOME`; Entra users are isolated by OID and anonymous users by generated user ID.
- One `SemaphoreSlim` gate per user serializes session create/resume/replay. Do not bypass it: warmup and transcript replay otherwise race into `Session ... is already tracked`.
- One active turn per session is enforced by `ChatEndpoints`; scheduled jobs use the same turn gate.
- SSE streams deltas, reasoning, timing, tools, charts, generated files, scores, cooldowns, busy/errors, and completion.
- The backend continues a turn after browser disconnect and persists the answer. The frontend reconciles against the server turn gate and transcript.
- OAuth access tokens stay in memory. Only the encrypted refresh-token identity record is persisted.
- The Azure OpenAI provider uses `BearerTokenProvider`; keep token refresh callback-based rather than baking a static token into sessions.
- `RuntimePolicy` applies custom-tool allowlists on create and resume. Built-ins, MCP, tool search, cross-session memory, and logged-in CLI credentials are disabled; never reintroduce `ApproveAll`.
- `ProtectedTool` binds owner, session, and admitted SDK tool-call id inside the callback. Host cancellation and tool leases keep the gate held until execution actually stops. Only provably undispatched input failures release without an SDK terminal event.
- Gates, cooldowns, and registries are process-local. Run one active app instance; shared files are persistence, not distributed coordination.

## Security invariants

The agent can read and apply approved non-destructive changes, but it never deletes Azure resources.

- `DELETE` is blocked centrally for Azure and Graph pass-through tools.
- Azure `POST` is restricted to the read-only allowlist in `AzureQueryTools`; action endpoints such as start, restart, deallocate, power-off, and reservation return are blocked.
- ARM `PUT` and `PATCH` first create an owner/session-bound proposal. Only the explicit application approval endpoint may execute the exact stored method, URL and canonical body under the user's RBAC. A prompt cannot approve a write.
- Destructive recommendations must use `GenerateScript` so the user reviews and runs them.
- Every session, job, upload, generated artifact, and transcript endpoint must enforce per-user ownership.
- Standard add-on consent tiers are read-only. Graph writes require separately granted write scopes.
- Never log or return bearer tokens, refresh tokens, secrets, authorization headers, or connection strings.
- Screen recognizable credentials before chat/job/tool dispatch; redact tool returns and SSE data. CLI content capture is off. This does not scrub historical transcripts or detect every unlabelled secret or image pixel.

### Untrusted-input rules

Treat every model-authored tool argument as attacker-controlled: it is shaped by uploaded file contents, fetched pages, Azure resource names, and tags.

- A file path, blob name, URL, or command fragment must never come from a tool argument. The host resolves it from a per-user registry keyed by an opaque id.
- When a free-form JSON parameter bag is merged into a host-built request, reject host-owned keys explicitly. Merging the model's object last silently lets it override them.
- Filesystem readers take a base directory from the host, resolve the candidate with `realpath`, and require containment. Fail closed when the root is absent; an existence check is not a containment check.
- Error paths must not echo the requested path or resource id back to the model — that is a probing oracle.
- Anything reaching `v-html` is entity-escaped first, at the single entry point. Escaping only some subfragments (table cells, attributes) is not enough.
- Model-supplied chart/render options are allow-listed and stripped of DOM and HTML sinks before they reach a renderer.

## Authentication

OAuth tiers are resource-specific and delegated:

| Tier           | Resource               | Scopes                                                   |
| -------------- | ---------------------- | -------------------------------------------------------- |
| `base`         | Azure Resource Manager | `user_impersonation`                                     |
| `licenses`     | Microsoft Graph        | `User.Read`, `Organization.Read.All`, `Reports.Read.All` |
| `chargeback`   | Microsoft Graph        | `User.Read`, `User.Read.All`, `Group.Read.All`           |
| `loganalytics` | Log Analytics          | `Data.Read`                                              |
| `storage`      | Azure Storage          | `user_impersonation`                                     |

`tier=all` walks only the remaining add-on tiers through separate user-scoped consent screens. Do not combine cross-resource scopes or replace this with tenant-wide admin consent.

Disconnect/revoke/logout differ intentionally:

- Disconnect clears live/session tokens and the browser identity cookie, but retains the encrypted identity record for explicit reconnect.
- Revoke clears the cookie and encrypted identity record and forces fresh consent next time.
- Logout clears Entra identity and immediately assigns a new anonymous chat identity.

Before manually testing a fresh consent flow, revoke existing grants for the test app in the selected test tenant. Use placeholders or local configuration—never commit real values.

## Tool patterns

- `GenerateScript` is always loaded. When the user requests Azure CLI or PowerShell code, call it directly with the complete executable code in `scriptContent`; it packages an owner-bound artifact and never executes the code. Tenant-specific scripts require scoped evidence first.
- Script delivery is a proposed savings action, never an executed change. Generic code examples do not create ledger entries. See [the tool and prompt catalog](../docs/tool-catalog.md) for registration flags, inputs, limits and prompt sources; update it when these contracts change.
- Tools fetch data and return compact raw API JSON unless a bounded projection is explicitly required for performance.
- XLSX `workbook` inspection returns every sheet's shape, columns, and bounded numeric summaries in one call; reuse it instead of making a second aggregate call when the requested metric is already present.
- Prefer string parameters; SDK coercion of numeric arguments can be unreliable.
- Use `CalculateCost` for non-token estimates/run-rates and `EstimateTokenCost` for token math. Keep the source currency, billing unit and scenario assumptions; an annualized exit-month cost is not cumulative annual spend. Calculators establish arithmetic, not price validity or deployability.
- A nullable C# parameter is not optional in the emitted tool schema. Give documented optional inputs actual defaults and test both schema requiredness and invocation with omitted arguments.
- Graph query options are endpoint-specific: `subscribedSkus` accepts only `$select`; Copilot usage functions do not accept `$filter`, `$top` or `$select`. Prefer current `/v1.0/copilot/reports/` routes and preserve CSV versus beta JSON, report version/period, source refresh date and licensed-user coverage.
- Use `GetCopilotUsage` for bounded activity counts and inactive-user lists. Count the full report before host-side filtering/paging, retain unknown activity and report dates, and never subtract current assignments from an older aggregate usage cohort. Per-page responses remain bounded even with long names; follow nextOffset only while the report date and totals agree.
- Reservation utilization requires a discovered billing-account/profile or reservation-order/reservation scope. Never use tenant-root `reservationSummaries`, cycle versions to repair a missing scope, or call unavailable commitment evidence proof of safe net savings.
- Reuse one `CosmosClient`/HTTP client/session where applicable; do not create clients per request.
- Tools generally do not catch API exceptions internally. Handle failures at system boundaries and let telemetry capture dependency failures.
- Push aggregation, filtering, grouping, projection, and limits into the source API using only options supported by that endpoint. Aggregate before limiting rows; retain totals, pagination and partial coverage. Repeat this requirement in each broad query tool's description and parameter metadata so the model does not fetch an unfiltered collection and rely on response trimming. Output caps and post-download web filtering are not source-side download limits.
- Apply purpose-specific payload guidance to every tool: exact IDs for single-object tools, declared full scope for assessments, and compact verified inputs for renderers/calculators/state updates. Do not add generic REST filters to tools that cannot support them or silently narrow an explicit full-result request.
- `GetSavingsLedger` supports status/category/literal scopeContains filters and string limit/offset paging. Default 50 entries, maximum 200, limit=0 for totals only. Compute totals over all matches before paging and preserve both totalsComplete and detail complete/nextOffset. These filters never change the owner boundary.
- Parallelize independent calls, except Cost Management `/query` and `/forecast`, which are tenant-throttled.
- Never issue multiple Cost Management query calls in parallel. After a final 429, stop querying that service for the turn.
- Cost query/forecast reads automatically retry once after a full service delay of at most five minutes. New requests may wait for an existing short cooldown; no deadline is shortened, and missing retry headers default to 60 seconds. Keep `retryAtUtc` and `willRetry` in cooldown SSE events, including host-blocked requests. A final same-turn block remains in force even after its timestamp passes.
- Cost Management permits at most two grouping dimensions. For resource/model detail, start with ResourceId plus Meter at subscription scope, or SubscriptionId plus ResourceId at management-group scope. Do not issue a preliminary totals-only request or add resource-group as a third grouping. Preserve billed detail versus activity/inventory distinctions.
- Preserve `_finops`/`sourceEvidence`, full retry deadlines, and explicit partial coverage through projections. The cache is credential/request-bound; never label cached or unknown-age billing data as freshly measured.
- `QueryUploadedFile` uses `jsonPath` for JSON selectors. Preserve structured arrays in `paramsJson`; host `path`, `kind`, and `mode` remain reserved. Query mode supports 1-50 distinct output names in columns[], applied after filtering/aggregation/sorting and before response paging. Retain row counts, totals, null groups and invalid-numeric counts through projection.

### Cross-subscription cost

Use `QueryCostsAcrossSubscriptions` exactly once for totals-only all-subscription questions. Detailed resource/model questions start with valid grouped detail; derive totals from those rows when coverage is complete.

- For the current calendar month, it reads unfiltered monthly-budget `currentSpend` concurrently. Strict guards require current-month dates, monthly Cost budgets, empty filters, agreeing duplicate budgets, and one currency.
- Budget snapshots are evaluated periodically and may lag billing. State that caveat; retrieval time is not a source data timestamp and a reported total is not a finalized bill.
- For other periods, it tries one management-group aggregate query and then the minimum sequential subscription fallback.
- Do not list subscriptions again; connection status already provides the available scopes.

### Crawl maturity

Use `GetCrawlMaturityEvidence` exactly once for explicit Crawl scoring.

- It runs budget/current-spend, required-tag, exports, alert/scheduled-action, policy, common-waste, and empty-resource-group checks concurrently.
- It computes and persists all seven scores and returns follow-up actions.
- `ChatEndpoints` emits `maturity_score` and `follow_up` directly.
- Do not call `QueryAzure`, `FindIdleResources`, `ReportMaturityScore`, or `SuggestFollowUp` in the same Crawl turn.
- Walk, Run, and Playbook continue to use `ReportMaturityScore`.
- Unknown and not-applicable scores are null, never zero, and are excluded from the displayed denominator. An empty resource group is not itself billable waste. Do not claim effective policy enforcement from assignment-name matches.

### Retail pricing

- One filter combination: one `GetAzureRetailPricing` call.
- Two or more independent combinations: one `GetAzureRetailPricingBatch` call.
- One SKU across regions uses one comma-separated region request.
- Cheapest-region requests rank all fetched candidates within compatible variants before applying top-N. Preserve rankingComplete versus detailsComplete; incomplete source pagination cannot establish a global winner.
- Keep tierMinimumUnits and currency in projected retail rows and separate volume bands when ranking. Do not apply a high-volume storage rate to a small dataset or multiply a whole-SKU rate by its included cores again.
- Reuse returned rows; do not invoke shell tools to reparse usable pricing results.
- The tool holds no per-SKU domain knowledge. Every response carries a `FACETS` block of live distinct field values, and rows are grouped by `meterName`, cheapest-first within each meter.
- Meter, product and SKU names are not derivable from the ARM SKU (`Standard_ND96asr_v4` meters as `ND96asr_A100_v4`). When such a filter matches zero rows the tool drops it, re-queries on the structural filter alone, and says so — it must never return an empty table.
- `priceType='Consumption'` includes Spot and Low Priority. Comparisons stay within one `meterName`, and answers default to the ordinary on-demand meter unless another variant was requested.
- Foundry model comparisons must use the intended deployment tier/zone and must not silently choose Batch, cached, or Data Zone rows when Standard Global was requested.
- Preserve `RESOLUTION` status and coverage. One targeted refinement is allowed for ambiguous/partial results. Missing prices stay unknown; the deterministic calculator rejects ambiguous decimal separators, missing rates and mixed currencies.

### Compute and operations

- `CheckComputeFeasibility` separates regional/family/Spot quota, SKU and zone restrictions, catalogue coverage, placement likelihood, and historical Spot eviction-rate bands. It is not a capacity guarantee or effective policy validation.
- Filter requested Compute SKU records before applying retained-item limits, follow pagination, and select the record for the requested region. Large catalogues can put every GPU record beyond a generic collection limit. Preserve `catalogueCoverage` and per-region unknowns; HTTP 200 is not proof of complete coverage.
- `LowPriorityCapable`, an existing successful Spot deployment, a current placement restriction, historical eviction risk and retail price are different evidence. Missing history is unknown, not zero risk. Cached placement scores are not fresh measurements; absent or `DataNotFound` scores are incomplete. A policy-name/parameter search does not validate effective policy.
- Treat ordinary Spot eviction and resilience questions as infrastructure questions. Provider content-filter errors are separate from tool failures; inspect structured filter metadata, including `error.content_filters.content_filter_results`, without logging prompts or credentials. Do not silently rewrite user text or automatically retry around moderation. Do not change filter settings autonomously merely to make a test pass.
- **Scoped maintainer exception - filters only:** An explicit maintainer request authorizes supported Azure filter and content-filter configuration changes, including custom RAI policies, configurable severity thresholds, and Bicep policy configuration. Confirm the target deployment and exact policy differences, preserve platform-required protections, avoid unintended changes to shared policies, and verify the effective configuration. Deployment still requires explicit instruction. This allowance does not extend to authentication, authorization/RBAC, ownership checks, write approvals, secret protection, destructive-operation restrictions, or any other security control.
- `CheckVmConnectivity` probes from an existing Azure VM via an existing Network Watcher. Never substitute connectivity from the app host.
- `OperationStore` records intent before dispatch and preserves asynchronous state, polling URLs, retry deadlines and prerequisites. Use `GetOperationStatus`/`ListOperationResults`; HTTP 202 is not success and unknown writes must not be retried blindly.

### Charts and generated files

- One response contains one chart or one table, not both.
- Generated script/deck markers are converted into structured SSE events.
- Download endpoints require an authenticated session and owner match.
- Expired artifacts render an expired state rather than a dead link.
- Owner-bound artifacts persist for 24 hours. `GenerateDataReport` creates real CSV/XLSX/escaped HTML with row-count checks and spreadsheet formula neutralization; never invent sandbox links.
- Upload ids bind to one conversation. Pending uploads expire in 30 minutes; bound uploads have a 24-hour sliding lease, capped at seven days. Active readers hold leases. Starting another conversation must not delist the previous conversation's files.

## Scheduled jobs

- Jobs are Entra-only and use delegated refresh tokens.
- Every run must report `ReportJobOutcome`. Success requires host-observed complete fresh evidence for every cited request scope, plus a nonempty answer. SDK idle alone is not business success; store the validated outcome, not the model's claim.
- Compact model context after every 20 completed runs while preserving the durable transcript. Pause on unverifiable compaction or a verified `goal_achieved`. Scheduled ARM changes still require explicit UI approval; no unattended capacity purchase.
- Ownership is exact OID match; never fall back to a derived user-ID match.
- Limits: 3 active jobs per user; custom cadence 1–43200 minutes; sub-daily expiry 7 days; daily or slower expiry 90 days; 5 consecutive failures auto-pause.
- Resume is cap-checked exactly like create.
- Every job owns one dedicated run-log session; it is hidden from Conversations while the job exists and reappears when the job is deleted.
- Run logs have no composer. They expose Build deck, Summarize runs, and Edit job.
- Create+run-now, explicit run-now, and edit+run-now all call `watchJobRunAndOpen`.
- Templates: capacity check, reserve when available, 1-minute test, daily digest, anomaly watch, budget guard, idle sweep, Advisor watch, and retry last question.

## Frontend invariants

- At 900px and below, the left navigation is an overlay and the right execution sidebar is hidden.
- Auto-scroll follows only while near the bottom. User scroll-up must never be overridden.
- Hidden browser tabs suspend ResizeObserver, animation frames, transitions, and smooth scrolling. Keep reactive watcher fallbacks.
- Do not rewrite punctuation in streamed model text. Identifiers such as hostnames, versions, and Azure resource names must remain byte-for-byte intact.
- A complete assistant `message` event replaces partial streamed deltas and cancels queued text animation. Having received one delta is not a reason to discard the authoritative final message. Cost cooldown notices remain visible in the chat on mobile; terminal throttling must not appear as an ongoing or successful retry.
- Escape all model/tool-influenced text before `v-html` transformations.
- Only the explicit Stop action marks a response as stopped; an arbitrary `AbortError` is recoverable transport failure.
- Attachment callbacks must update chips by stable `uid`, never by array index. Wait for uploads before sending, delist files whose chips were removed in flight, and revoke blob thumbnail URLs only after Vue unmounts them.
- Generated HTML previews must stay in a sandboxed iframe without `allow-same-origin`; model-produced deck scripts must never inherit access to application cookies, storage, DOM, or authenticated APIs.

## Code conventions

- Follow Microsoft C# conventions and modern Vue Composition API patterns.
- Preserve public APIs unless the task requires a change.
- Keep API endpoints RESTful and ownership-checked.
- Use current stable Azure API versions. Document intentional older versions where a service has not adopted the newest family version.
- Prefer managed identity and OIDC over client secrets.
- Keep unrelated formatting out of functional changes.
- Update `CHANGELOG.md` and this file whenever architecture, tools, security boundaries, dependencies, or project structure change.

## Local development

Secrets use .NET User Secrets; never commit local settings.

```powershell
cd src/Dashboard/frontend
npm ci
npm run build

cd ..
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --urls http://localhost:5000
```

The frontend must be built before backend startup so `wwwroot` exists when ASP.NET resolves `WebRootPath`.

## Testing

- Backend: `dotnet build src/Dashboard/Dashboard.csproj --no-restore`
- Regressions: `dotnet test tests/Dashboard.Tests/Dashboard.Tests.csproj`; Python `python -m unittest discover -s tests -p "test_*.py"`; frontend `npm run test` and `npm run test:browser`.
- `validate.yml` runs credential-free regressions, desktop/mobile browsers and Linux image smoke checks before either deployment workflow can run. Exact SDK/CLI protocol tests must not be skipped on Linux.
- Frontend: `npm run build` under `src/Dashboard/frontend`
- Always verify the rendered UI for UI changes; a successful build is not a browser test.
- Measure latency from the app's SSE stream, not rendered pixels.
- Before every send, wait for the composer to be enabled and for the Stop button to be absent.
- Select Stop only by `.action-btn--stop`.
- For authenticated UI runs, pin the exact tab the user shared and use `run_playwright_code` exclusively. Do not use high-level browser helpers or the separate `mcp_playwright_browser_*` surface. Never open or navigate a replacement tab; if shared access is lost, ask the user to re-share the same signed-in tab.
- Check console errors, page errors, failed requests, tool sequence/count, TTFT, total time, and persisted transcript.
- After edits, verify disk state with `git status --short`; save all editor buffers before building.

## Deployment

Pin every external GitHub Action to a full 40-character commit SHA with a release-version comment. Verify the SHA against the upstream release, and keep `cooldown.default-days: 7` in the `github-actions` Dependabot entry. Do not replace pins with mutable tags during workflow edits.

Customer deployment uses `azd up` and generates names from the selected environment. Never put deployment coordinates in tracked files.

Maintainer CI workflows read deployment settings from GitHub repository variables and secrets:

- Production variables: `PROD_ACR_NAME`, `PROD_ACR_LOGIN_SERVER`, `PROD_CONTAINER_IMAGE`, `PROD_WEBAPP_NAME`, `PROD_RESOURCE_GROUP`, `PROD_VERIFY_URL`
- Test variables: corresponding `TEST_*` names plus `TEST_SLOT_NAME`
- OIDC secrets: `AZURE_*` for test and `AZURE_PROD_*` for production

Production OIDC must be branch-scoped to `main` and least-privileged: `AcrPush` on the target registry and `Website Contributor` on the target web app. App Service pulls images with its own managed identity and `AcrPull`.

Do not deploy without explicit user instruction. When instructed, validate builds, diff, secrets, account context, workflow configuration, and target version before pushing.

## Observability

- Host traces use `SamplingRatio = 1` with `TracesPerSecond = null`; the rate limit otherwise overrides ratio sampling. Keep host, CLI collector, and browser telemetry changes distinct.
- `ApiExceptionHandling` owns the correlated exception log for handled HTTP faults. Suppress the duplicate ASP.NET exception-handler diagnostic, retain the request metric's `error.type`, and keep framework diagnostics for faults after response start. Expected cancellation stays trace-only. Transcript ownership is rechecked under the user gate; a conflicting live owner fails closed without an exception-driven 500.
- Collector 0.161.0 uses `azure_monitor` with loopback OTLP receivers. Validate config, local ingestion and shutdown after collector upgrades; do not suppress shipped-image vulnerabilities merely because a bundled component is not configured.
- Owner-bound turn outcomes persist for 30 days and reconcile interrupted records on startup. Normal chat fulfillment stays `not_evaluated`; a completed HTTP request or SDK turn is not proof the user's objective was met.
- Record SDK tool rejections that occur before the protected callback, without double-counting acquired callbacks. A nonempty fallback answer after a failed tool is not clean execution; include failed non-evidence tools in partial outcome classification.
- Truly empty terminal turns emit a recoverable `empty_result` error. Explicit cancellation and structured chart/score/artifact outputs are not empty results; outcome fulfillment remains `not_evaluated`.
- Use `investigate-ai-sessions.prompt.md` for end-to-end completion audits: prove authorized owner-bound extraction first, sample distinct populated conversations rather than turns or warmups, and disclose full-event versus retained-message-only coverage. Keep historical customer transcripts and deployment coordinates out of source and regression fixtures.
- Browser exception capture has one bounded, redacted, deduplicated reporting path. Do not claim the historical notification-manager exception is fixed without reproducing its trigger.

Discover Application Insights and Log Analytics identifiers from `azd env get-values`, Azure Resource Graph, or the deployed resource group. Never hardcode an application ID, workspace ID, subscription, or resource group in prompts or instructions.

For workspace-based Application Insights, query the Log Analytics `AppExceptions`, `AppTraces`, `AppRequests`, and `AppDependencies` tables. Confirm telemetry pipeline activity before interpreting an empty exception result.
