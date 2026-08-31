<!-- last refreshed: 2026-08-31 -->

# Azure FinOps Agent — Copilot Instructions

## Purpose

Azure FinOps Agent is an open-source Azure sample and delivery accelerator. It combines conversational AI with Azure Cost Management, ARM, Resource Graph, Advisor, Microsoft Graph, Log Analytics, public pricing, file analysis, visualizations, remediation scripts, and scheduled jobs.

It is designed for customers to deploy into **their own tenant and subscription**. Never commit maintainer or customer tenant IDs, subscription IDs, resource IDs, generated resource names, app IDs, user principal names, email addresses, IP addresses, connection strings, or deployment credentials.

## Stack

- Backend: .NET 10 minimal API in `src/Dashboard`
- Frontend: Vue 3 + Vite + ECharts in `src/Dashboard/frontend`
- Agent runtime: GitHub Copilot SDK with Azure OpenAI BYOK
- Authentication: anonymous chat plus optional multi-tenant Entra OAuth
- Hosting: Linux container on Azure App Service
- Infrastructure: `azure.yaml` + Bicep under `infra`
- Observability: OpenTelemetry + Application Insights

## Core architecture

- A shared `CopilotClient` manages per-user `CopilotSession` instances.
- Session state is persisted under `COPILOT_HOME`; Entra users are isolated by OID and anonymous users by generated user ID.
- One `SemaphoreSlim` gate per user serializes session create/resume/replay. Do not bypass it: warmup and transcript replay otherwise race into `Session ... is already tracked`.
- One active turn per session is enforced by `ChatEndpoints`; scheduled jobs use the same turn gate.
- SSE streams deltas, reasoning, timing, tools, charts, generated files, scores, cooldowns, busy/errors, and completion.
- The backend continues a turn after browser disconnect and persists the answer. The frontend reconciles against the server turn gate and transcript.
- OAuth access tokens stay in memory. Only the encrypted refresh-token identity record is persisted.
- The Azure OpenAI provider uses `BearerTokenProvider`; keep token refresh callback-based rather than baking a static token into sessions.

## Security invariants

The agent can read and apply approved non-destructive changes, but it never deletes Azure resources.

- `DELETE` is blocked centrally for Azure and Graph pass-through tools.
- Azure `POST` is restricted to the read-only allowlist in `AzureQueryTools`; action endpoints such as start, restart, deallocate, power-off, and reservation return are blocked.
- `PUT` and `PATCH` remain available under the signed-in user's Azure RBAC.
- Destructive recommendations must use `GenerateScript` so the user reviews and runs them.
- Every session, job, upload, generated artifact, and transcript endpoint must enforce per-user ownership.
- Standard add-on consent tiers are read-only. Graph writes require separately granted write scopes.
- Never log or return bearer tokens, refresh tokens, secrets, authorization headers, or connection strings.

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

- Tools fetch data and return compact raw API JSON unless a bounded projection is explicitly required for performance.
- Prefer string parameters; SDK coercion of numeric arguments can be unreliable.
- Reuse one `CosmosClient`/HTTP client/session where applicable; do not create clients per request.
- Tools generally do not catch API exceptions internally. Handle failures at system boundaries and let telemetry capture dependency failures.
- Push aggregation, filtering, grouping, and limits into the source API.
- Parallelize independent calls, except Cost Management `/query` and `/forecast`, which are tenant-throttled.
- Never issue multiple Cost Management query calls in parallel. After a final 429, stop querying that service for the turn.

### Cross-subscription cost

Use `QueryCostsAcrossSubscriptions` exactly once for all-subscription totals.

- For the current calendar month, it reads unfiltered monthly-budget `currentSpend` concurrently. Strict guards require current-month dates, monthly Cost budgets, empty filters, agreeing duplicate budgets, and one currency.
- For other periods, it tries one management-group aggregate query and then the minimum sequential subscription fallback.
- Do not list subscriptions again; connection status already provides the available scopes.

### Crawl maturity

Use `GetCrawlMaturityEvidence` exactly once for explicit Crawl scoring.

- It runs budget/current-spend, required-tag, exports, alert/scheduled-action, policy, common-waste, and empty-resource-group checks concurrently.
- It computes and persists all seven scores and returns follow-up actions.
- `ChatEndpoints` emits `maturity_score` and `follow_up` directly.
- Do not call `QueryAzure`, `FindIdleResources`, `ReportMaturityScore`, or `SuggestFollowUp` in the same Crawl turn.
- Walk, Run, and Playbook continue to use `ReportMaturityScore`.

### Retail pricing

- One filter combination: one `GetAzureRetailPricing` call.
- Two or more independent combinations: one `GetAzureRetailPricingBatch` call.
- One SKU across regions uses one comma-separated region request.
- Reuse returned rows; do not invoke shell tools to reparse usable pricing results.
- Foundry model comparisons must use the intended deployment tier/zone and must not silently choose Batch, cached, or Data Zone rows when Standard Global was requested.

### Charts and generated files

- One response contains one chart or one table, not both.
- Generated script/deck markers are converted into structured SSE events.
- Download endpoints require an authenticated session and owner match.
- Expired artifacts render an expired state rather than a dead link.

## Scheduled jobs

- Jobs are Entra-only and use delegated refresh tokens.
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
- Escape all model/tool-influenced text before `v-html` transformations.
- Only the explicit Stop action marks a response as stopped; an arbitrary `AbortError` is recoverable transport failure.

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
- Frontend: `npm run build` under `src/Dashboard/frontend`
- Always verify the rendered UI for UI changes; a successful build is not a browser test.
- Measure latency from the app's SSE stream, not rendered pixels.
- Before every send, wait for the composer to be enabled and for the Stop button to be absent.
- Select Stop only by `.action-btn--stop`.
- Check console errors, page errors, failed requests, tool sequence/count, TTFT, total time, and persisted transcript.
- After edits, verify disk state with `git status --short`; save all editor buffers before building.

## Deployment

Customer deployment uses `azd up` and generates names from the selected environment. Never put deployment coordinates in tracked files.

Maintainer CI workflows read deployment settings from GitHub repository variables and secrets:

- Production variables: `PROD_ACR_NAME`, `PROD_ACR_LOGIN_SERVER`, `PROD_CONTAINER_IMAGE`, `PROD_WEBAPP_NAME`, `PROD_RESOURCE_GROUP`, `PROD_VERIFY_URL`
- Test variables: corresponding `TEST_*` names plus `TEST_SLOT_NAME`
- OIDC secrets: `AZURE_*` for test and `AZURE_PROD_*` for production

Production OIDC must be branch-scoped to `main` and least-privileged: `AcrPush` on the target registry and `Website Contributor` on the target web app. App Service pulls images with its own managed identity and `AcrPull`.

Do not deploy without explicit user instruction. When instructed, validate builds, diff, secrets, account context, workflow configuration, and target version before pushing.

## Observability

Discover Application Insights and Log Analytics identifiers from `azd env get-values`, Azure Resource Graph, or the deployed resource group. Never hardcode an application ID, workspace ID, subscription, or resource group in prompts or instructions.

For workspace-based Application Insights, query the Log Analytics `AppExceptions`, `AppTraces`, `AppRequests`, and `AppDependencies` tables. Confirm telemetry pipeline activity before interpreting an empty exception result.
