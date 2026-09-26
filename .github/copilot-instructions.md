<!-- last refreshed: 2026-09-25 -->

# Azure FinOps Agent — Copilot Instructions

## Purpose

Azure FinOps Agent is an open-source Azure sample and delivery accelerator. It combines conversational AI with Azure Cost Management, ARM, Resource Graph, Advisor, Microsoft Graph, Log Analytics, public pricing, file analysis, visualizations, remediation scripts, and scheduled jobs.

It is designed for customers to deploy into **their own tenant and subscription**. Never commit maintainer or customer tenant IDs, subscription IDs, resource IDs, generated resource names, app IDs, user principal names, email addresses, IP addresses, connection strings, or deployment credentials.

## Stack

- Backend: .NET 10 minimal API in `src/Dashboard`
- Frontend: Vue 3 + Vite + ECharts in `src/Dashboard/frontend`
- Agent runtime: GitHub Copilot SDK with Azure OpenAI BYOK
- Default model: `gpt-6-luna`, version `2026-09-22`, using the Responses API. Existing-account reuse requires that deployment to exist and the app identity to have account-scoped inference access. Verify available model-specific quota; deleting a different model does not free Luna quota.
- Authentication: anonymous chat plus optional multi-tenant Entra OAuth
- Hosting: Linux container on Azure App Service
- Infrastructure: `azure.yaml` + Bicep under `infra`
- Observability: OpenTelemetry + Application Insights

The SDK and bundled Copilot CLI are one compatibility unit. Let the installed `GitHub.Copilot.SDK` package supply `CopilotCliVersion`; do not retain an override from an older SDK. Before accepting an SDK bump, verify its exact platform CLI packages are published. A stale runtime can pass text/tool tests while breaking protocol features such as native image attachments.

## Core architecture

- A shared `CopilotClient` manages per-user `CopilotSession` instances.
- Session state is persisted under `COPILOT_HOME`; Entra users are isolated by the validated `tid + oid` pair and anonymous users by generated user ID. Never treat OID alone as globally unique. An OID-only legacy workdir is admissible only when its encrypted identity record attests the same pair.
- One `SemaphoreSlim` gate per user serializes session create/resume/replay. Do not bypass it: warmup and transcript replay otherwise race into `Session ... is already tracked`.
- One active turn per session is enforced by `ChatEndpoints`; scheduled jobs use the same turn gate.
- SSE streams deltas, reasoning, timing, tools, charts, generated files, scores, cooldowns, busy/errors, and completion.
- The backend continues a turn after browser disconnect and persists the answer. The frontend reconciles against the server turn gate and transcript.
- OAuth access tokens stay in memory. Only the encrypted refresh-token identity record is persisted.
- The Azure OpenAI provider uses `BearerTokenProvider`; keep token refresh callback-based rather than baking a static token into sessions.
- `RuntimePolicy` applies custom-tool allowlists on create and resume. Built-ins, MCP, tool search, cross-session memory, and logged-in CLI credentials are disabled; never reintroduce `ApproveAll`.
- `ProtectedTool` binds owner, session, and admitted SDK tool-call id inside the callback. Host cancellation and tool leases keep the gate held until execution actually stops. Only provably undispatched input failures release without an SDK terminal event.
- SDK tool callbacks can overtake queued session events. Await exact call-id admission with bounded cancellation-aware waiting; never assume `ToolExecutionStart` handlers have run before the callback, bypass admission, or re-admit a consumed call id.
- SDK large-output file substitution stays disabled. Large read-only JSON may use `ToolResultStore` and `QueryToolResult`: retain the complete redacted source, discover schema dynamically, and enforce exact owner/session lookup. IDs expire after 30 minutes/restart. Smaller successful evidence objects stay inline with a root `_resultQuery` annotation so exact totals and comparisons use `QueryToolResult`; keep that annotation additive so existing parsers still read the JSON. Query paging never upgrades source freshness or coverage, and local queries never count as fresh scheduled-job evidence. Keep approval/chart/score/artifact control messages inline; never expose host paths or evaluate model code.
- Empty `QueryToolResult` select/groupBy objects mean omitted projection/grouping. Ungrouped aggregates remain available in `totals` even with `limit=0`; grouped amounts, particularly different currencies, never become an unrequested grand total.
- Retained-result handles use 22-character Base64url encoding of 128 cryptographically random bits. Copy them exactly, including case; never shorten, normalize, fuzzy-match or search other results to repair an invalid handle. Owner/session checks, redaction and expiry remain mandatory.
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

The agent is deliberately minimal: tools are thin, host-enforced pass-throughs and the model authors every API call. Do not reintroduce host-built composite tools, host-computed headlines/tables/summaries, or per-scenario KQL/REST builders to fix an evaluation; improve the prompt or tool description instead so the model investigates and calls the right API itself.

- Evidence tools: `QueryAzure`/`BulkAzureRequest` (ARM, including Cost Management, Resource Graph, Advisor, Consumption, Billing, Compute and Policy), `QueryGraph`, `QueryLogAnalytics`, `GetAzureRetailPricing`, `FetchPublicWebPage` (always loaded), plus storage, upload, pricesheet and operation readers. Host code owns only security, scope, throttling, pagination, retention and redaction rules.
- Descriptions tell the model to look up rather than guess: `GET /subscriptions/{id}/providers/{namespace}?api-version=2021-04-01` for live apiVersions, the official OpenAPI specs in [Azure/azure-rest-api-specs](https://github.com/Azure/azure-rest-api-specs) (browse through the api.github.com contents API, read through raw.githubusercontent.com with `grepFor`), [microsoftgraph/msgraph-metadata](https://github.com/microsoftgraph/msgraph-metadata) and learn.microsoft.com. A failed call fails the evaluation, so verification comes before the call.
- The system prompt keeps a short list of API facts that prevent common failed calls (Cost Management grouping limits, Resource Graph scope/KQL rules, tenant-scope reservation listing). Keep it short and factual; do not grow it into per-question recipes.
- `GenerateScript` is always loaded. When the user requests Azure CLI or PowerShell code, call it directly with the complete executable code in `scriptContent`; it packages an owner-bound artifact and never executes the code. Script delivery is a proposed savings action, never an executed change. See [the tool and prompt catalog](../docs/tool-catalog.md) for registration flags, inputs and limits; update it when these contracts change.
- Tools return raw API JSON. Large read-only results are retained for `QueryToolResult`; totals, counts, shares, rankings and comparisons over three or more values come from `QueryToolResult`, `CalculateCost`, `CompareAmounts` or `EstimateTokenCost`, never mental arithmetic.
- Prefer string parameters; SDK coercion of numeric arguments can be unreliable. `ProtectedTool` converts JSON numbers/booleans supplied for string-only parameters to strings before binding. A nullable C# parameter is not optional in the emitted schema: give optional inputs real defaults and test requiredness plus omitted-argument invocation.
- `CalculateCost` requires one explicit top-level source currency and rejects different currencies. `CompareAmounts` takes complete verified decimal strings. Calculators establish arithmetic, not price validity or deployability. Surface tool `Error:` results as failures even when SDK execution succeeded.
- `PublishFAQ` must be explicitly requested. Preserve host authentication/moderation checks and never describe pending review as publication.
- Public web reads honor the host cancellation token and one deadline through body reading, and report transport failure explicitly.
- Reuse one HTTP client/session where applicable; do not create clients per request. Tools generally do not catch API exceptions internally.
- Single and bulk Resource Graph POST bodies require one explicit nonempty `subscriptions` or `managementGroups` array. Cost Management, Consumption and PolicyInsights paths require a scope prefix; tenant-root `reservationSummaries` is rejected in favour of discovered reservation-order or billing scopes.
- Parallelize independent calls except Cost Management `/query` and `/forecast`, which are tenant-throttled. The host forces any cost-containing batch sequential, retries once after a service delay of at most five minutes, keeps `retryAtUtc`/`willRetry` in cooldown SSE events, and stops after a final 429 for the rest of the turn. Cost Management permits at most two grouping dimensions; the host rejects more before dispatch.
- Preserve `_finops`/`sourceEvidence`, retry deadlines and partial coverage. The cost cache is credential/request-bound; never label cached or unknown-age billing data as freshly measured.
- Retained-result handles are exact opaque IDs with exact owner/session lookup; never repair a typo by searching for a similar ID.
- `QueryUploadedFile` uses `jsonPath` for JSON selectors and supports 1-50 output columns after filtering/aggregation/sorting; host `path`, `kind` and `mode` remain reserved. XLSX `workbook` returns all sheets' shapes and numeric summaries in one call.
- `GetSavingsLedger` supports status/category/literal scopeContains filters and string limit/offset paging (default 50, maximum 200, limit=0 for totals); totals cover all matches before paging.

### Retail pricing

- `GetAzureRetailPricing` takes a model-authored OData `filter`, optional `currencyCode` and `maxPages` (1-10). The host pins `https://prices.azure.com/api/retail/prices`, follows only same-origin `NextPageLink`s, retries 429/5xx and returns raw `Items` with `retrievedAtUtc`, `pages` and `complete`. Never send `$top`.
- Compare several regions or SKUs with `or` in one filter and run independent lookups in parallel. Meter, product and SKU names are not derivable from ARM SKUs; the model starts from structural fields and reads returned values instead of guessing.
- The prompt keeps pricing quality rules: clarify material configuration (OS/license, tier, fixed-region region) unless assumptions were authorized, default to on-demand, compare only within one product/meter and volume band, carry qualifiers into labels, and quote the retrieval time, which is not a price-effective date.

### Maturity scoring

- Crawl, Walk, Run and Playbook all use `ReportMaturityScore`. The model gathers the evidence for every dimension defined in that tool's description and submits all of them once.
- Unknown and not-applicable scores are null, never zero, and are excluded from the displayed denominator. An empty resource group is not billable waste. Do not claim effective policy enforcement from assignment-name matches. Advisor alternatives overlap and are not additive.

### Compute and operations

- Spot/GPU questions are answered with QueryAzure: Compute usages for quota, Compute SKUs for regional and zone restrictions, the read-only Spot `placementScores/spot/generate` POST, and Resource Graph `SpotResources` for historical eviction rates. Quota, placement scores and prices are not capacity guarantees; missing history is unknown, not zero risk.
- Treat ordinary Spot eviction and resilience questions as infrastructure questions. Provider content-filter errors are separate from tool failures; inspect structured filter metadata, including `error.content_filters.content_filter_results`, without logging prompts or credentials. Do not silently rewrite user text or automatically retry around moderation. Do not change filter settings autonomously merely to make a test pass.
- **Scoped maintainer exception - filters only:** An explicit maintainer request authorizes supported Azure filter and content-filter configuration changes, including custom RAI policies, configurable severity thresholds, and Bicep policy configuration. Confirm the target deployment and exact policy differences, preserve platform-required protections, avoid unintended changes to shared policies, and verify the effective configuration. Deployment still requires explicit instruction. This allowance does not extend to authentication, authorization/RBAC, ownership checks, write approvals, secret protection, destructive-operation restrictions, or any other security control.
- `CheckVmConnectivity` probes from an existing Azure VM via an existing Network Watcher (an action POST that QueryAzure blocks). Never substitute connectivity from the app host.
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
- Ownership is an exact tenant-ID, object-ID, and pair-derived user-ID match. Legacy jobs without a tenant binding stay disabled; never fall back to OID-only or user-ID-only ownership.
- Limits: 3 active jobs per user; custom cadence 1–43200 minutes; sub-daily expiry 7 days; daily or slower expiry 90 days; 5 consecutive failures auto-pause.
- Resume is cap-checked exactly like create.
- Every job owns one dedicated run-log session; it is hidden from Conversations while the job exists and reappears when the job is deleted.
- Run logs have no composer. They expose Build deck, Summarize runs, and Edit job.
- Create+run-now, explicit run-now, and edit+run-now all call `watchJobRunAndOpen`.
- Templates: capacity check, reserve when available, 1-minute test, daily digest, anomaly watch, budget guard, idle sweep, Advisor watch, and retry last question.

## Frontend invariants

- At 900px and below, the left navigation is an overlay and the right execution sidebar is hidden.
- Conversation deletion is one click with no confirmation step. The row stays visible and disabled while the server responds. Only confirmed SDK deletion clears client state; active turns return a conflict and failures remain visible for retry.
- Closed navigation must be invisible and inert. Keep the compact overlay aligned to the actual header, dismissible with Escape/backdrop, and return focus to its toggle.
- Auto-scroll follows only while near the bottom. User scroll-up must never be overridden.
- Hidden browser tabs suspend ResizeObserver, animation frames, transitions, and smooth scrolling. Keep reactive watcher fallbacks.
- Cooldown and assistant-brand motion must respect reduced motion and pause when hidden. Completed replies stay static; wait progress is never task-completion progress, and live countdown changes must not repeatedly interrupt screen readers.
- Do not rewrite punctuation in streamed model text. Identifiers such as hostnames, versions, and Azure resource names must remain byte-for-byte intact.
- A complete assistant `message` event replaces partial streamed deltas and cancels queued text animation. Having received one delta is not a reason to discard the authoritative final message. Cost cooldown notices remain visible in the chat on mobile; terminal throttling must not appear as an ongoing or successful retry.
- Scope that replacement to the SDK message ID, not the entire user turn. A separate follow-up message must append to, never erase, a completed cost answer. Keep per-stream text state isolated and preserve errors, empty-message handling and replay consistency.
- Do not delay the primary answer for an optional follow-up tool call. A single simple next question can use the existing escaped `prompt:` link; structured multiple actions still use `SuggestFollowUp`.
- Replay redacted SDK failures as explicit terminal notices, not assistant answers or vague reconnect pills. Live and restored errors share the same presentation, preserve partial results, and let the user edit the saved question without automatic resend or draft loss. Model inference authorization is separate from tenant sign-in.
- Render retry progress from server deadlines and eligibility, with a reactive clock and exact tool-call association. Do not show a frozen original wait, a hardcoded retry limit, HTTP 0 as throttling, or SDK-success/HTTP-failure as a green success.
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
- Both deployment workflows also require `live-evaluations.yml`. Local and CI runs execute the same explicit set of 20 representative template/incident questions against real inference and Azure tools; all 20 must pass. Keep the complete template catalog available for coverage, but never accept a subset, arbitrary replacement set or changed contracts. This is curated coverage, not a verified popularity ranking. Any failed, missing, timed-out, wrong-revision or invalid verdict blocks deployment. Use the dedicated evaluation identity/environment for CI and only explicitly authorized maintainer-lab identity/scopes locally, never production credentials or customer data. `EVAL_DATA_CLASSIFICATION=synthetic` publishes answers; `internal-test` (maintainer-owned test tenants) publishes only questions, tool outcomes, timings and pass/fail, withholding answers, error text and judge rationale from summaries and artifacts. The judge sees tool arguments, visible structured outputs and the host's connection context alongside the answer. See `tests/LiveEvaluations/README.md` for configuration and test-host versus delegated-browser coverage. Do not skip unavailable prerequisites or relax rubrics to make a run pass. The suite waits out reported service cooldowns between cases and re-runs a case once, unchanged, only when it failed after a final service throttle; every other failure is final.
- Live gate runs require clean candidate application/evaluator/workflow sources, inherited build configuration, and matching evaluator/Dashboard assembly revisions. Replay must preserve the decoded question and every completed answer, not just echo the question. The evaluator shares the runtime's reported-tool-failure classifier; a positive judge cannot override failed execution. Private case captures stay outside published output, internal-test withholding applies locally too, and cancellation stops subsequent cases and cleans only owned process trees/captures. Environment-scoped evaluation secrets resolve on the job and remain mandatory at preflight. Manual `feature.yml` dispatch defaults to validation only (`deploy=false`); only an explicit `deploy=true` permits a manual test-slot deployment after both gates pass.
- Explicitly authorized local maintainer-lab evaluations may use the existing Azure CLI user session. Select its cached identity with the first approved evaluation subscription, verify that scope belongs to `EVAL_TENANT_ID`, and use that credential consistently for tools, candidate inference and the judge. Tenant-only CLI selection can target the wrong cached user, and CLI tenant/subscription options are mutually exclusive. Keep coordinates outside tracked files and never copy personal CLI tokens/cache into CI. GitHub evaluations still require their separate read-only OIDC identity; token issuance alone does not establish API access.
- Frontend: `npm run build` under `src/Dashboard/frontend`
- Local failed-evaluation diagnostics may be retained only with explicit `EVAL_PRIVATE_DIAGNOSTICS_DIRECTORY` outside the repository and published output (including symlink targets); CI rejects the option. Delete passing captures and never publish raw failed-tool details in either data classification.
- Preserve captured execution on credential, replay and judge exceptions. Private diagnostics include bounded redacted successful/failed tool details and failure phase, never reset completed work to an empty zero-duration record. Neither synthetic nor internal-test publication includes those private fields.
- Distinguish a valid negative judge verdict from malformed/missing judge output in gate diagnostics. Both fail the gate; acceptance still requires all three boolean judge flags to be true and a nonempty reason, without publishing private rationale.
- The judge checks every requested metric across all answer columns and visible outputs. Enabled prepaid inventory is not invoice-confirmed paid quantity, and assignments are not activity. Require all available counts and any supplied billing evidence, reject unsupported paid/waste claims, and keep missing required activity reports incomplete. The one maintainer-approved exception: a requested but unavailable report for a product with zero enabled and zero assigned seats makes the per-seat answers determinate (0 of 0, no licensed non-users, no inactive-seat waste) when the answer attributes them to zero seats and leaves unlicensed-user activity unknown. Do not change case rubrics to repair a judge's source-metric confusion.
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

Feature deployments reuse the configured existing shared TEST slot, never create a slot per branch or swap production. Validate its exact Azure resource ID and actual hostname before writes; reject blank/production slots and mismatched verification URLs. Serialize settings/image writes without cancelling in-flight updates. Merge only the three evaluated model settings with `az webapp config appsettings set --slot`, preserving unrelated settings, and verify effective model settings plus full SHA/build/branch. Existing slot-configuration/registry permissions (no ARM deployment permission) and slot inference access are prerequisites, not grants the workflow may add.

The protected evaluation endpoint comes from the `EVAL_MODEL_ENDPOINT` secret, never a plain variable. Successful evaluation exports the model, reasoning effort, full candidate SHA and normalized endpoint SHA-256 fingerprint. Feature deployment uses those values without model defaults and compares its endpoint secret locally before Azure login or writes; never expose raw endpoint coordinates or silently retarget inference.

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
