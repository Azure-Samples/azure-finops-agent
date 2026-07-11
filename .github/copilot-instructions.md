# Azure FinOps Agent - Copilot Instructions

<!-- last refreshed: 2026-05-14 — see CHANGELOG [Unreleased] for the corresponding tool/description edits -->

## Pre-Test Checklist

Before asking the user to test OAuth consent flows locally or in production, **always revoke existing consent grants first** so the Microsoft consent screen appears fresh:

```powershell
# Revoke all consent grants for the app registration
$spId = az ad sp show --id "<YOUR_APP_REGISTRATION_ID>" --query "id" -o tsv 2>$null
$grants = az rest --method GET --url "https://graph.microsoft.com/v1.0/servicePrincipals/$spId/oauth2PermissionGrants" -o json 2>$null | ConvertFrom-Json
foreach ($g in $grants.value) { Write-Host "Deleting grant $($g.id)"; az rest --method DELETE --url "https://graph.microsoft.com/v1.0/oauth2PermissionGrants/$($g.id)" 2>&1 }
Write-Host "All consent grants revoked — user will see fresh consent screens"
```

This ensures the incremental consent flow works as expected and the user sees the real Microsoft Entra ID consent UI for each tier.

Always check if you are logged into the right tenant + subscription before running Azure CLI commands. Each developer maintains their own values locally — never commit real values to this file. See README 'Running Locally' for setup.

- **Tenant**: `<YOUR_TENANT_ID>` (`<YOUR_TENANT_DOMAIN>.onmicrosoft.com`)
- **Subscription**: `<YOUR_SUBSCRIPTION_NAME>` — `<YOUR_SUBSCRIPTION_ID>`
- **Account**: `<YOUR_ADMIN_UPN>`

If `az account show` returns a different tenant, run:

```powershell
az login --tenant <YOUR_TENANT_ID>
az account set --subscription <YOUR_SUBSCRIPTION_ID>
```

## Project Purpose

Read all docs here:
https://github.com/github/copilot-sdk/blob/main/docs/getting-started.md

https://github.com/github/copilot-sdk/blob/main/docs/setup/azure-managed-identity.md

https://github.com/github/copilot-sdk/blob/main/dotnet/README.md

Azure FinOps Agent is an AI-powered agentic solution for FinOps and InfraOps on Azure. It serves as a reference architecture and delivery accelerator (VBD) for Microsoft Customer Success teams to help Azure customers optimize cloud costs.

The agent acts as a frontend on top of Azure Cost Management, Billing, ARM REST APIs, and Microsoft Graph APIs to:

- **Simulate potential cost outcomes** — model scenarios and forecast Azure spend under different configurations.
- **Surface immediate cost-optimization actions** — identify low-hanging fruit such as idle resources, oversized VMs, and unattached disks.
- **Provide actionable FinOps insights** — deliver data-driven recommendations aligned with the FinOps Framework.

## Tech Stack

- **Backend**: .NET 10 minimal API (`src/Dashboard/`)
- **Frontend**: Vue 3 + Vite SPA (`src/Dashboard/frontend/`) with ECharts for data visualization
- **AI**: GitHub Copilot SDK (`GitHub.Copilot.SDK` 1.0.5) with BYOK (Bring Your Own Key) using Azure OpenAI via Entra ID bearer tokens. Sessions managed via `CopilotClient` / `CopilotSession`. Reasoning effort set to `xhigh`. The Copilot CLI provides built-in tools (file operations, bash, grep, glob, web fetch, memory) — custom tools handle Azure-specific APIs. Multi-session per user: each user can keep many conversations, listed in the right sidebar; the SDK auto-disconnects idle sessions after 30 min (`SessionIdleTimeoutSeconds = 1800`) while preserving on-disk state for resume.
- **Auth**: Auto-assigned anonymous sessions (no login required for chat); Microsoft Entra ID OAuth (multi-tenant) for Azure ARM, Microsoft Graph, and Log Analytics APIs
- **Data Sources**: Azure Retail Prices API (no auth), Azure Service Health (no auth), Azure Cost Management APIs, Microsoft Graph APIs, Azure Monitor / Log Analytics APIs, ECharts visualization
- **Observability**: OpenTelemetry end-to-end. The .NET app uses `UseAzureMonitor()` (auto-instruments HttpClient, ASP.NET Core, custom `ActivitySource("AzureFinOps.AI")` + `Meter("AzureFinOps.AI")`). The Copilot CLI subprocess emits OTLP via the SDK's built-in `TelemetryConfig` (GenAI + MCP semantic conventions — every tool call, LLM round-trip, prompt, tool args, result, token usage). Both feeds reach Application Insights via an in-container **OpenTelemetry Collector** (`otel/opentelemetry-collector-contrib`) using the `azuremonitor` exporter — config at `src/Dashboard/otel-collector-config.yaml`, launched by `entrypoint.sh` before the .NET app. Trace context (W3C `traceparent`) is auto-propagated SDK→CLI so Application Map shows one continuous transaction. Custom metrics (`finops.chat.requests`, `finops.tool.calls`, `finops.sessions.active`, etc.) keep flowing through the .NET exporter. Frontend telemetry in `frontend/src/main.js` captures page views, failed browser dependencies, uncaught JS errors, unhandled promise rejections, Vue component errors, and CSP violations. Third-party correlation headers are excluded for `cdn.jsdelivr.net` and `js.monitor.azure.com`.
- **Deployment**: Azure App Service (Linux, P0v3 Premium) via Docker container image from Azure Container Registry (ACR). Multi-stage Dockerfile bakes Python 3, pip packages (pandas, numpy, openpyxl, pdfminer.six, pyarrow), and CLI tools into the image — no runtime install needed.
- **Container Registry**: Azure Container Registry (`crfinopsagent.azurecr.io`) — Basic SKU, admin credentials, images built via `az acr build`
- **Container App (staging)**: `finops-agent-container.azurewebsites.net` — Docker container on same P0v3 plan, used for testing before swapping to production
- **Custom Domain**: `https://azure-finops-agent.com` (Azure DNS → Azure App Service with managed SSL; `www.` redirects to bare domain via middleware)
- **License**: MIT

## Azure Icon Assets

- `Icons/` and `docs/assets/Icons/` are **gitignored** — they contain the full Azure Architecture icon pack (~1,370 SVGs) downloaded from [Microsoft](https://learn.microsoft.com/azure/architecture/icons/) for local use only.
- `azure-icons-catalog.drawio` is a generated draw.io catalog of all icons (with embedded base64 SVGs) — also gitignored.
- If a README, doc, or diagram needs a specific icon, copy it into `assets/` (which IS versioned, minus its `Icons/` subfolder).

## Project Structure

```
src/Dashboard/
├── Program.cs              # .NET backend: auth (Microsoft Entra ID), SSE chat endpoint, models, version
├── Dashboard.csproj        # .NET 10 project, GitHub.Copilot.SDK + Azure.Identity + Microsoft.Extensions.AI
├── Tools/
│   ├── AzureQueryTools.cs  # QueryAzure — read-only Azure ARM REST API queries (GET + allowlisted POST only)
│   ├── ChartTools.cs       # RenderChart + RenderAdvancedChart — ECharts visualization (bar, line, pie, scatter, funnel, world maps, heatmaps, treemaps, radar, gauge)
│   ├── FaqTools.cs         # PublishFAQ — dynamically publishes useful Q&As as SEO pages, pings IndexNow
│   ├── FollowUpTools.cs    # SuggestFollowUp — suggests clickable follow-up actions in the UI
│   ├── GraphQueryTools.cs  # QueryGraph — calls Microsoft Graph API using user's delegated token
│   ├── HealthTools.cs      # GetAzureServiceHealth — Azure Status RSS feed (no auth required)
│   ├── HttpHelper.cs       # Shared HTTP helper — retry on 429, response formatting, telemetry
│   ├── LogAnalyticsQueryTools.cs # QueryLogAnalytics — runs KQL queries against Log Analytics workspaces or App Insights
│   ├── HtmlPresentationTools.cs # GenerateHtmlPresentation — generates an executive HTML deck (one self-contained .html file) with Chart.js charts, keyboard nav (←/→/↑/↓), animated slides
│   ├── ScoreTools.cs       # ReportMaturityScore — LLM reports FinOps maturity scores (0-5) per Crawl/Walk/Run level
│   ├── ScriptTools.cs      # GenerateScript — generates downloadable Azure CLI/PowerShell scripts from FinOps recommendations
│   └── UploadedFileTools.cs # QueryUploadedFile — inspect/query files (CSV/TSV/JSON/TXT/XLSX/PDF/Parquet) the user dropped into chat (no Azure consent needed). Backed by AI/Tools/Resources/file_inspect.py (pandas/openpyxl/pyarrow/pdfminer).
│   └── TokenContext.cs     # UserTokens — per-user mutable token holder with volatile fields for concurrent access
├── Dockerfile              # Multi-stage Docker build (node:22 + dotnet/sdk:10.0 + dotnet/aspnet:10.0 + Python 3 + OTel collector)
├── entrypoint.sh           # Container entrypoint — starts OTel collector in background, then exec dotnet
├── otel-collector-config.yaml # OpenTelemetry Collector config — bridges OTLP from Copilot CLI → Application Insights via azuremonitor exporter
├── .dockerignore           # Excludes bin/, obj/, node_modules/, wwwroot/ from Docker context
├── client/
│   ├── index.html          # SPA entry point
│   ├── package.json        # Vue 3, Vite, ECharts
│   ├── vite.config.js      # Dev proxy to :5000, builds to ../wwwroot
│   └── src/
│       ├── main.js
│       ├── App.vue          # Always renders Dashboard — handles auth state, login/logout
│       └── components/
│           ├── Dashboard.vue     # Layout shell
│           └── ChatView.vue      # Full chat UI: left sidebar (Crawl/Walk/Run maturity categories with scoring), center chat, right sidebar (tool calls), ECharts
├── appsettings.json              # Base config (empty secrets — safe to commit)
├── appsettings.Local.json        # Local dev secrets (GITIGNORED)
├── appsettings.Production.json   # Production secrets (GITIGNORED)
├── setup-entra-app.ps1           # Entra ID app registration setup (creates app, permissions, secret)
├── .gitignore                    # Excludes secrets, node_modules, bin/obj, wwwroot, publish
└── wwwroot/                      # Built Vue frontend (generated by `npm run build`)
```

## Architecture Principles

- The solution follows an **agentic architecture** where the AI agent orchestrates calls to multiple data sources and tools to answer user questions.
- GitHub Copilot SDK manages the AI session lifecycle — a shared `CopilotClient` spawns the CLI process, per-user `CopilotSession` instances maintain conversation history.
- The Copilot SDK provides built-in tools (file operations, bash, grep, glob, web fetch, memory) — custom tools handle Azure-specific APIs and unique UI features.
- Custom tools are registered as `AIFunction` instances via `AIFunctionFactory.Create` and passed to `CopilotSession` via `SessionConfig.Tools`. Shared (stateless) tools are created once; per-user tools (requiring tokens) are created per user via closure over `UserTokens`.
- BYOK authentication: `DefaultAzureCredential` generates bearer tokens for `https://cognitiveservices.azure.com/.default`, passed to the Copilot SDK via `ProviderConfig.BearerToken`. Tokens are cached and auto-refreshed before expiry.
- **BYOK token lifecycle (critical)**: `ProviderConfig.BearerToken` is a **static string baked into the Copilot CLI subprocess at session creation** — there is no callback/delegate to push refreshed tokens into a running session. AOAI tokens last ~1h, so any `CopilotSession` older than its token's expiry will fail every prompt with `HTTP 401 Authentication failed with provider`. `CopilotSessionFactory` mitigates this by tracking `LiveSessionInfo.BearerExpiry` (per live session, stored inside the `LiveSessions` dict in `AiTelemetry`) and **proactively recycling via `ResumeSessionAsync` with a fresh token** in `GetOrCreateSessionAsync` when `expiry - now < 10 min` — preserves history. If you ever swap providers or change the BYOK credential type, preserve this proactive-recycle pattern — refreshing only the cached token string is NOT enough.
- **Session persistence**: `CopilotClientOptions.CopilotHome` is set to `/home/copilot` (App Service `/home` Azure Files mount). The CLI writes session state to `{CopilotHome}/.copilot/session-state/{sessionId}/` so chat history survives container restarts, redeploys, scale-up, and slot swaps. Per-user isolation: each session's `WorkingDirectory` is `{CopilotHome}/users/{entraOid}` (or `/anon/{userId}` for anonymous), and `ListSessionsAsync` filters by `Cwd` so users only see their own.
- **Session idle disconnect**: `CopilotClientOptions.SessionIdleTimeoutSeconds = 1800` releases the in-memory CLI side after 30 min idle. Disk state is preserved; the next prompt rehydrates via `ResumeSessionAsync(sessionId, fresh-bearer)`.
- The backend streams responses via **Server-Sent Events (SSE)** using session event subscriptions (`session.On()`) — deltas, tool starts/completions, chart events, session-id sync, errors.
- The Vue frontend consumes SSE and renders streaming text (character-by-character animation), tool call status in the sidebar, intent text next to the AI avatar, ECharts visualizations inline, and a vertical-split right sidebar (Agent on top, past Conversations on bottom).
- Data should be retrieved from APIs at runtime — the agent stores dynamic FAQ entries to disk for SEO but no user data.
- **Session management endpoints**: `GET /api/sessions` lists the user's past conversations (filtered by `Cwd`); `POST /api/sessions/new` creates a new one; `DELETE /api/sessions/{id}` removes it. The chat SSE endpoint accepts an optional `sessionId` to resume a specific conversation. `UserStateJanitor` background service deletes sessions older than 30 days.
- **Exception handling**: Tools do NOT use try/catch internally. The Copilot CLI handles tool errors and returns them to the LLM. `UseAzureMonitor()` auto-instruments all `HttpClient` calls as dependency telemetry in App Insights.
- The solution is designed for **customer deployment** — customers deploy it in their own Azure subscription.

## Security Model (Read + Write, Never Delete)

This agent allows reads and writes but **cannot delete** Azure resources. This is enforced at multiple layers:

### Code-Level Enforcement (AzureQueryTools.cs / HttpHelper.cs)

- **HTTP method whitelist**: `GET`, `POST`, `PUT`, and `PATCH` are accepted. `DELETE` is rejected at the `HttpHelper` layer — the agent never deletes resources.
- **Destructive cleanup pattern**: For removals (idle disks, orphaned IPs, expired snapshots), the agent calls `GenerateScript` instead, so the user reviews and runs the deletion themselves.
- **GraphQueryTools**: GET only — no write methods.
- **LogAnalyticsQueryTools**: POST to query APIs only — KQL read operations.

### OAuth Scopes (Minimal Permissions)

| Tier                 | Scope                                       | Access Level                     | Notes                                                                       |
| -------------------- | ------------------------------------------- | -------------------------------- | --------------------------------------------------------------------------- |
| Base (ARM)           | `user_impersonation`                        | Delegated — inherits user's RBAC | Writes/deletes governed by user's RBAC; DELETE additionally blocked in code |
| License Optimization | `Organization.Read.All`, `Reports.Read.All` | Read-only by scope definition    | Cannot modify org or report data                                            |
| Cost Allocation      | `User.Read.All`, `Group.Read.All`           | Read-only by scope definition    | Cannot modify users or groups                                               |
| Log Analytics        | `Data.Read`                                 | Read-only by scope definition    | Cannot modify workspace data                                                |
| Cost Exports         | `user_impersonation` (Azure Storage)        | Delegated — inherits user's RBAC | Customer storage account; read-only enforced via SDK calls (Get blob only)  |

**Note on `user_impersonation`**: ARM's only delegated scope is `user_impersonation`, so the user's RBAC role is the ultimate boundary. The code-level DELETE block is a hard guarantee on top of that.

### Recommended RBAC for Connected Users

Pick the RBAC role that matches how much you want the agent to do:

- **Reader** / **Cost Management Reader** / **Billing Reader** — read-only; agent can analyze and generate scripts but cannot apply changes.
- **Contributor** / **Cost Management Contributor** — agent can apply tags, create budgets, set anomaly alerts, configure scheduled actions, autoshutdown, exports, etc. (DELETE is still blocked in code.)

## Tool Development Patterns (Critical Learnings)

When creating tools for the agent:

- **Return raw API JSON** — do NOT manually parse/deserialize API responses. Let the LLM interpret the raw JSON. Manual parsing is fragile and breaks on nullable fields or schema changes.
- **Use `string` parameters** (not `double` or typed values) — the SDK's type coercion can fail. Accept strings and let .NET interpolate them into URLs.
- **Keep tools simple** — fetch data and return it. Add UTC timestamp context if useful. Avoid complex transformations.
- **Example pattern** (proven working):
  ```csharp
  private static async Task<string> GetData(
      [Description("...")] string param1,
      [Description("...")] string param2)
  {
      var url = $"https://api.example.com?p1={param1}&p2={param2}";
      var json = await Http.GetStringAsync(url);
      return $"Current UTC time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}\n{json}";
  }
  ```
- **ChartTools** (`RenderChart` / `RenderAdvancedChart`) returns a serialized JSON object with chart config — the frontend detects `tool_done` for `RenderChart` or `RenderAdvancedChart` and emits a separate `chart` SSE event. `RenderAdvancedChart` accepts raw ECharts option JSON for world maps, heatmaps, treemaps, radar, gauge, etc.
- **FaqTools** (`PublishFAQ`) dynamically publishes useful public Q&As as SEO-indexable HTML pages at `/faq/{slug}`. Entries are stored in a JSON file on disk and auto-submitted to IndexNow for Bing indexing. The sitemap at `/sitemap.xml` is dynamically generated to include both static and community FAQ entries.
- **HtmlPresentationTools** (`GenerateHtmlPresentation`) generates a self-contained HTML executive deck. Pure C#, no Python. Chart.js (CDN) for charts; keyboard nav (←/→ slides, ↑ fullscreen, ↓ exit), click zones, dot nav, progress bar, swipe. Layouts: `title`, `kpi`, `chart`, `content`, `two_column`, `maturity` (single-state or before/after), `alerts` (color-coded findings), `table` (auto-bars + status tags), `closing` (CTA). Charts: bar, horizontal_bar, line, pie, doughnut, waterfall. Returns a `__HTML_READY__:{fileId}:{fileName}:{slideCount}` marker. The SSE handler emits an `html_ready` event, and the frontend shows a compact download card. Files are served via `/api/download/html/{fileId}` (with `?inline=true` for in-browser viewing) and auto-cleaned after 30 minutes. Default deck pattern is **current-state**: title → kpi → alerts → chart → table → maturity → closing.
- **AzureQueryTools** (`QueryAzure`) is a **read-only** tool that queries Azure ARM REST APIs (GET and allowlisted POST only) using the user's delegated token from `UserTokens.AzureToken`. Returns raw JSON for the LLM to interpret. **Security**: PUT, PATCH, and DELETE methods are rejected at the code level. POST requests are restricted to an allowlist of known read-only endpoints (`/query`, `/forecast`, `/resources`, `/generateCostDetailsReport`, `/generateReservationDetailsReport`, `/calculatePrice`, `/calculateExchange`, `/validatePurchase`, `/carbonEmissionReports`, `/getEntities`, `/summarize`). Mutating POST actions (e.g., `/deallocate`, `/start`, `/restart`, `/return`) are blocked with HTTP 403. Covers Cost Management (queries, forecasts, cost details report, reservation details report, exports, scheduled actions, views), Budgets, Billing, Consumption (pricesheets, reservation summaries/recommendations/transactions, lots, credits, balances, charges), Reservations, Savings Plans, Advisor, Resource Graph, Monitor, Activity Log, Compute/VMs/VMSS, AKS, Network (ExpressRoute, VPN, public IPs, App Gateways, NAT Gateways), Storage, SQL, SQL Managed Instances, App Service, Azure ML (workspaces, compute instances, GPU clusters, endpoints), Databricks (workspaces, pricing tiers), Cosmos DB (accounts, throughput/RU analysis), Redis Cache, Data Factory, Synapse (SQL pools, Spark pools), Container Apps, Resource Health, Defender for Cloud (security assessments, secure scores), RBAC (role assignments), Locks, Quota, Carbon, Policy/PolicyInsights, Management Groups, Tags, Migrate, and Support. Note: Consumption usageDetails/marketplaces are deprecated — prefer Cost Details API or Exports. Consumption reservationDetails is deprecated — prefer generateReservationDetailsReport (Microsoft.CostManagement). **Latest API versions** are embedded in the tool description and include: CostManagement 2025-03-01, Consumption 2025-07-01, Billing 2024-04-01, Capacity 2022-11-01, BillingBenefits 2022-11-01, Advisor 2025-01-01, ResourceGraph 2024-04-01, Insights/metrics 2024-02-01, Compute VMs 2025-11-01, Compute Disks 2025-01-02, Compute SKUs 2021-07-01, ContainerService 2026-02-01, Network core 2025-07-01 (gateways/firewall/expressRoute 2025-09-01), Storage 2025-08-01, Sql 2025-01-01, Web 2025-05-01, OperationalInsights 2025-07-01, MachineLearningServices 2026-03-01, CognitiveServices 2026-03-01, Databricks 2026-01-01, DocumentDB 2025-10-15, Cache 2024-11-01, DataFactory 2018-06-01, Synapse 2023-05-01, App 2026-01-01, ResourceHealth 2025-05-01, Security 2025-05-04 (assessments)/2020-01-01 (secureScores), Authorization/RBAC 2022-04-01, Authorization/Policy 2025-11-01, Authorization/Locks 2020-05-01, PolicyInsights 2024-10-01, Management 2023-04-01, Resources 2023-07-01 (subscriptions 2022-12-01), Quota 2025-09-01, Carbon 2025-04-01, Migrate 2024-01-15 (note: resource type is `assessmentProjects`, NOT `migrateProjects`), Support 2024-04-01. **Spot/GPU Quota**: Spot vCPU quota is a single regional bucket called `lowPriorityCores` (not per VM family). H100 standard quotas are per-family: `standardNDSH100v5Family`, `StandardNCadsH100v5Family`. The Quota RP scope is `/subscriptions/{subId}/providers/Microsoft.Compute/locations/{region}/providers/Microsoft.Quota/quotas`.
- **GraphQueryTools** (`QueryGraph`) is **read-only** — GET only. calls Microsoft Graph API using `TokenContext.GraphToken`. Used for license inventory, M365 usage reports (Exchange, Teams, OneDrive, SharePoint), M365 Copilot seat usage, M365 app-level usage, Intune device management, directory objects, org structure for FinOps chargebacks.
- **LogAnalyticsQueryTools** (`QueryLogAnalytics`) is **read-only** — runs KQL queries (POST to query API only) against Log Analytics workspaces or App Insights using `TokenContext.LogAnalyticsToken`. Used for VM/container metrics, diagnostics, cost attribution (AzureActivity table), and ingestion cost analysis.
- **TokenContext** (`TokenContext.cs`) provides per-user mutable token storage via `UserTokens` — one instance per user in a `ConcurrentDictionary<long, UserTokens>`. Token fields use `volatile` for cross-thread visibility. A `SemaphoreSlim RefreshLock` serializes token refresh operations within a user session. `UserTokens` instances are passed to tool constructors via closure, so tools always read the latest tokens via direct reference.
- **ScoreTools** (`ReportMaturityScore`) enables the LLM to report FinOps maturity scores after evaluating a level. The LLM uses `QueryAzure` to check dimensions (tagging, orphaned resources, Advisor, budgets, reservations, right-sizing, etc.), then calls `ReportMaturityScore` with a level (`crawl`, `walk`, `run`) and a JSON array of scores (0-5 per dimension with one-line reasons). Returns a `__MATURITY_SCORE__:{level}:{scoresJson}` marker. The SSE handler emits a `maturity_score` event, and the frontend updates star ratings in the sidebar category headers.
- **ScriptTools** (`GenerateScript`) generates downloadable Azure CLI or PowerShell scripts from FinOps recommendations discussed in the conversation. The LLM MUST first analyze the environment, find actionable recommendations, and confirm with the user before calling this tool — if no recommendations exist yet, it tells the user instead of generating empty scripts. Scripts include safety features (dry-run mode, confirmation prompts, --what-if). Returns a `__SCRIPT_READY__:{fileId}:{fileName}:{lineCount}:{language}:{description}` marker. The SSE handler emits a `script_ready` event (including the script content for inline preview), and the frontend shows a code block with syntax-highlighted content, a copy button, and a download button. Files are served via `/api/download/script/{fileId}` and auto-cleaned after 30 minutes.

## FinOps Maturity Framework (Crawl / Walk / Run)

The sidebar organizes prompts into three maturity levels aligned with the FinOps Foundation framework:

- **Crawl** (Visibility & Baseline) — Cost visibility, tagging audit, orphaned resource cleanup, Advisor recommendations, budget alerts, resource inventory. Horizontal — broad coverage.
- **Walk** (Optimization & Governance) — Reservations & savings plans, right-sizing, dev/test savings, policy compliance, AHUB, storage optimization, workload-specific optimizations. Horizontal — executing optimizations broadly.
- **Run** (Scale & Accountability) — Executive reporting, chargeback/showback, unit economics, cost allocation, anomaly detection, license optimization, AI/GPU cost analysis, carbon emissions, management group governance. Vertical — per-department ownership.

Each level has a **"Score" prompt** as the first item. When clicked, the LLM uses `QueryAzure` to check each dimension, scores them 0-5, and calls `ReportMaturityScore`. The sidebar shows star ratings (★★★☆☆) per level. Users can re-score anytime by clicking "Score" again.

A **Pricing & Estimates** category (no Azure login required) provides public pricing comparisons and cost estimations.

## Authentication

No login is required to use the chat. Users are auto-assigned an anonymous session identity on first request.

## Microsoft Entra ID OAuth Setup

A single **multi-tenant** Microsoft Entra ID app registration (`Azure FinOps Agent`) is shared between local development and production — both use the same ClientId/ClientSecret. The OAuth flow exchanges tokens for four separate resources:

- **Azure ARM token** (`https://management.azure.com/user_impersonation`) — for Cost Management, Billing, Advisor, Resource Graph, Monitor, etc.
- **Microsoft Graph token** (explicit granular scopes via incremental consent) — for license inventory, M365 usage reports, directory objects, org structure for chargeback
- **Log Analytics token** (`https://api.loganalytics.io/Data.Read`) — for KQL queries against Log Analytics and App Insights
- **Azure Storage token** (`https://storage.azure.com/user_impersonation`) — for reading Cost Management export blobs from the customer's Storage Account

**Security**: Incremental consent — the app requests minimal permissions upfront (ARM only) and adds Graph/Log Analytics scopes only when the user explicitly opts in via separate consent flows. Each addon tier shows a dedicated Microsoft Entra ID consent screen. All token exchanges use explicit scopes (not `.default`) to prevent silent permission creep.

**Consent Tiers** (each triggers a separate Microsoft Entra ID consent screen — all delegated, read-only):

| Tier value       | Button                             | Scopes                                                                                                                                                                                                                                                                                                                                       | Resource          |
| ---------------- | ---------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------- |
| `base` (default) | **Connect Azure**                  | `user_impersonation`                                                                                                                                                                                                                                                                                                                         | Azure ARM         |
| `licenses`       | **+ License Optimization**         | `User.Read`, `Organization.Read.All`, `Reports.Read.All`                                                                                                                                                                                                                                                                                     | Microsoft Graph   |
| `chargeback`     | **+ Cost Allocation**              | `User.Read`, `User.Read.All`, `Group.Read.All`                                                                                                                                                                                                                                                                                               | Microsoft Graph   |
| `loganalytics`   | **+ Log Analytics**                | `Data.Read`                                                                                                                                                                                                                                                                                                                                  | Log Analytics API |
| `storage`        | **+ Cost Exports**                 | `user_impersonation`                                                                                                                                                                                                                                                                                                                         | Azure Storage     |
| `all` (chain)    | **🛡 Grant all remaining add-ons** | _Walks the user through every not-yet-consented add-on tier in sequence._ Each remaining tier's consent screen appears one after another; the callback redirects to the next pending tier until done. Entra v2 will not combine cross-resource scopes into one consent for a non-admin delegated flow, so sequential is the correct pattern. | (chain)           |

The single source of truth for tier → scopes mapping is `GetScopesForTier()` in `Program.cs`. The `tier=all` chain logic lives in the `/auth/microsoft` handler (builds `auth_chain` session list) and the callback (consumes one chain entry per redirect).

The flow:

1. User clicks "Connect Azure" → `/auth/microsoft?tier=base` → Microsoft login → ARM token
2. After connecting, sidebar shows 4 opt-in scope rows + a secondary "Grant all remaining add-ons" admin shortcut
3. Single-tier buttons redirect to `/auth/microsoft?tier=<tier>` → Microsoft consent screen (only for that tier's scopes)
4. "Grant all" button redirects to `/auth/microsoft?tier=all` → chains through every remaining tier automatically
5. Callback exchanges the auth code for tier-specific tokens and stores in session
6. Per-request, tokens are refreshed and set on the user's `UserTokens` instance before tool execution

Config in `appsettings.json`:

```json
"Microsoft": {
    "ClientId": "",
    "ClientSecret": "",
    "TenantId": "common"
}
```

For Azure App Service: `Microsoft__ClientId`, `Microsoft__ClientSecret`, `Microsoft__TenantId`

### Secrets Management

- `appsettings.Local.json` — local dev secrets (gitignored), **only loaded in Development** (`builder.Environment.IsDevelopment()` guard in `Program.cs`)
- `appsettings.Production.json` — production secrets (gitignored)
- `appsettings.json` — base config with empty placeholders (committed)
- For Azure App Service: secrets are set as app settings via `az webapp config appsettings set` (encrypted at rest)
- Environment variable format: `Microsoft__ClientId`, `Microsoft__ClientSecret`, `Microsoft__TenantId`, `AzureOpenAI__Endpoint`, `AzureOpenAI__DeploymentName` (.NET auto-maps `__` to config sections)
- **Critical**: Never load `appsettings.Local.json` unconditionally — it will override production env vars on Azure

## Running Locally

> See **[README.md § Running Locally](../README.md#running-locally)** for the full, copy-pasteable setup guide — prerequisites, `dotnet user-secrets` commands for all config keys, frontend build, and run instructions.

Local dev secrets are managed via **`dotnet user-secrets`** (not `appsettings.Local.json`). Secrets live outside the repo in your OS user profile and cannot be accidentally committed. `AzureOpenAI:Endpoint` is the only fail-fast key — the app throws on startup if it is missing.

Prompt shortcuts are available in `.github/prompts/`:

- `debug-local.prompt.md` for non-container local debugging
- `debug-local-docker.prompt.md` for Docker-based local debugging

## Testing Protocol (Local UI Tests)

When the user asks for "testing", "test the change", "verify it works", or any equivalent — and the test would benefit from driving the live UI — you MUST follow this exact protocol:

1. **Check that the VS Code integrated Playwright browser tools are loadable.** Run two `tool_search` calls:
   - `"playwright browser navigate screenshot read page type"` (yields `navigate_page`, `screenshot_page`, `read_page`, `type_in_page`, `hover_element`, `run_playwright_code`)
   - `"open browser page url click element handle dialog"` (yields `open_browser_page`, `click_element`, `handle_dialog`, `drag_element`)
   If either search returns nothing, STOP and tell the user the test must run inside VS Code Insiders. Do not fall back to a system browser, do not ask the user to test manually.
2. **Check whether a local backend is already running** at `http://localhost:5000` via `(Invoke-WebRequest -Uri http://localhost:5000/api/version -UseBasicParsing).StatusCode`. If 200, reuse it; if not, follow `.github/prompts/debug-local.prompt.md` to bring it up.
3. **Confirm a browser page is shared** with you (look for the `Browser Pages` attachment in the conversation context). If none, call `open_browser_page url=http://localhost:5000` and capture the `pageId`.
4. **Run the test against the live page** — `read_page` for state, `click_element` / `type_in_page` for actions, `screenshot_page` after each meaningful step. Verify the change actually took effect in the rendered DOM, not just in the source.
5. **Only after the live test passes** (or fails with a captured screenshot + reproduction steps) should you report the result. Never declare a UI change "tested" based on `npm run build` succeeding alone — the build only proves it compiles.

Skip this protocol only when the change has no UI surface (pure backend refactor, doc edits, infra-only changes).

## Deploying to Azure

> **Do not deploy from the developer's machine without explicit instruction.** Deployment is the repo owner's decision. The agent must never run `az acr build`, `az webapp restart`, `azd up`, or any equivalent command on its own initiative. If the user asks to deploy, point them at the manual checklist in `.github/prompts/deploy.prompt.md` and let them invoke it. Code changes ship via the user's own deploy step (or CI/CD), never via an agent push.

The production app runs as a Docker container on Azure App Service, fed from Azure Container Registry (`crfinopsagent.azurecr.io`). Reference setup:

- Multi-stage Dockerfile: node:22 (frontend) → dotnet/sdk:10.0 (build) → dotnet/aspnet:10.0 (runtime + Python 3 + pip packages)
- All Python dependencies (pandas, numpy, openpyxl, pdfminer.six, pyarrow) baked into the image
- Build context is ~76 KB (clean `bin/`, `obj/`, `node_modules/` before building, or rely on `.dockerignore`)
- ACR: `crfinopsagent.azurecr.io` (Basic SKU, admin enabled)
- Container app: `finops-agent-container` on `ASP-rgfinopsagent-b74f` P0v3 plan

### Customer-facing `azd up` path

For external users deploying into their own subscription, the repo ships with `azure.yaml` + `infra/` (Bicep) + `infra/scripts/` (PowerShell hooks). A single `azd up` provisions everything: RG, Log Analytics + App Insights, ACR (admin disabled, MI-based pull), Azure AI Foundry (`AIServices`) account + model deployment, App Service Plan + Web App with system-assigned MI, role assignments (`AcrPull`, `Cognitive Services OpenAI User`), and the multi-tenant Entra app registration (via `setup-entra-app.ps1 -OutputJson` invoked from the preprovision hook). The postdeploy hook runs `az acr build` so users do not need a local Docker daemon. A `postdown` hook deletes the azd-created Entra app on `azd down` (BYO apps via `AZURE_ENTRA_APP_ID` are left untouched) so nothing is orphaned. See README "Deploy to Azure (`azd up`)" for the full UX. **Note:** the production owner deployment (`crfinopsagent` / `finops-agent-container`) predates this and is unaffected — `azd up` always creates new resources under the chosen `azd env` name.

## Code Conventions

- Use clean, well-structured C# for the .NET backend following Microsoft coding conventions.
- Use modern JavaScript patterns for the frontend (Vue 3 Composition API with `<script setup>`).
- Keep API endpoints RESTful and well-documented.
- Include proper error handling for API calls (rate limiting, authentication failures, etc.) at the **system boundary** (e.g., the `/api/chat` endpoint). Tools themselves should NOT use try/catch — the Copilot CLI and OpenTelemetry handle exception logging centrally.
- All Azure resource interactions should use managed identity where possible.
- Use `.NET 10` APIs — e.g., `KnownIPNetworks` not deprecated `KnownNetworks`.
- Use `CopilotClient` / `CopilotSession` from the `GitHub.Copilot` namespace (NuGet package `GitHub.Copilot.SDK`).

## Key Terminology

- **FinOps**: Cloud financial management discipline combining finance, technology, and business to optimize cloud spend.
- **InfraOps**: Infrastructure operations — managing and optimizing cloud infrastructure.
- **VBD**: Value-Based Delivery — a Microsoft Customer Success engagement model.
- **MCAPS**: Microsoft Customer & Partner Solutions — the Microsoft org that delivers customer success.

## Debugging with App Insights via Azure CLI

When debugging production issues, use `az monitor app-insights query` to run KQL queries directly against Application Insights — no portal needed. The Azure CLI is already authenticated, so this works immediately.

**App Insights App ID**: `89a08d0e-fb6e-4273-8a94-470699c7cfb2`

Common queries:

```powershell
# Recent errors (traces with severityLevel >= 3)
az monitor app-insights query --app "89a08d0e-fb6e-4273-8a94-470699c7cfb2" --analytics-query "traces | where timestamp > ago(1h) and severityLevel >= 3 | order by timestamp desc | take 20 | project timestamp, message, severityLevel"

# Token/auth issues
az monitor app-insights query --app "89a08d0e-fb6e-4273-8a94-470699c7cfb2" --analytics-query "traces | where timestamp > ago(1h) and message contains 'token' | order by timestamp desc | take 10 | project timestamp, message"

# All recent traces
az monitor app-insights query --app "89a08d0e-fb6e-4273-8a94-470699c7cfb2" --analytics-query "traces | where timestamp > ago(30m) | order by timestamp desc | take 50 | project timestamp, message, severityLevel"

# Failed requests
az monitor app-insights query --app "89a08d0e-fb6e-4273-8a94-470699c7cfb2" --analytics-query "requests | where timestamp > ago(1h) and success == false | order by timestamp desc | take 20 | project timestamp, name, resultCode, duration"
```

Always prefer this over downloading log files or tailing logs — it's faster, structured, and queryable.

## Playwright for Portal Operations

When you need to perform actions on Azure portals, GitHub, or the Microsoft Open Source Management portal (e.g., elevating JIT permissions, configuring repo settings, managing team membership), use **Playwright MCP** to navigate and interact with the portal UI directly from VS Code. This is especially useful for:

- Elevating JIT admin permissions on the repo via the Open Source portal.
- Managing GitHub team membership or repo settings.
- Configuring Azure App Service settings, custom domains, or SSL bindings.
- Any portal-based operation that cannot be done via CLI or API.

Always use Playwright when portal interaction is required rather than asking the user to do it manually.

## Custom Domain Setup

The production app is available at `https://azure-finops-agent.com` (canonical) and `https://www.azure-finops-agent.com` (redirects to bare domain).

- **Domain Registrar**: Namecheap (domain registration + WHOIS privacy only)
- **DNS Provider**: Azure DNS (zone: `azure-finops-agent.com` in `rg-finops-agent`)
  - Migrated from Namecheap BasicDNS to Azure DNS for better corporate proxy trust scoring (shared `registrar-servers.com` nameservers are low-trust)
- **Azure DNS Nameservers**: `ns1-04.azure-dns.com`, `ns2-04.azure-dns.net`, `ns3-04.azure-dns.org`, `ns4-04.azure-dns.info`
- **DNS Records** (managed in Azure DNS zone):
  - `A` `@` → `52.228.84.33` (App Service IP)
  - `CNAME` `www` → `finops-agent-container.azurewebsites.net`
  - `TXT` `asuid` → Azure domain verification token
  - `TXT` `_dmarc` → `v=DMARC1; p=none; rua=mailto:<YOUR_DMARC_CONTACT_EMAIL>` (domain reputation)
- **Security Headers** (in `Program.cs`): HSTS (1 year, preload), X-Content-Type-Options, X-Frame-Options, Referrer-Policy, Permissions-Policy, Content-Security-Policy, HTTPS redirection
- **SSL**: Azure Managed Certificates (auto-renewed) bound via SNI for both `www` and root domain
- **Microsoft Entra ID callbacks**: `https://azure-finops-agent.com/auth/microsoft/callback`, `https://www.azure-finops-agent.com/auth/microsoft/callback`, `http://localhost:5000/auth/microsoft/callback`, `https://finops-agent-container.azurewebsites.net/auth/microsoft/callback`

## Self-Maintenance

Whenever code changes, new files, or documentation updates are made in this repository, **always update this instructions file** to reflect the current state of the project. This includes:

- New or removed components, services, or folders.
- Changes to the tech stack or architecture.
- New conventions, patterns, or dependencies introduced.
- Updates to team members or ownership.

This file must always be kept up to date so that Copilot has accurate context about the project.

## Repository Context

- This repo lives in the `Azure-Samples` GitHub organization.
- It is an open-source sample/accelerator and VBD delivery asset, not a Microsoft product.
- It follows the same README/repo structure as other Azure-Samples solution accelerators (e.g., `chat-with-your-data-solution-accelerator`, `azure-search-openai-demo`).
- Standard files: `README.md`, `CONTRIBUTING.md`, `SECURITY.md`, `SUPPORT.md`, `CODE_OF_CONDUCT.md`, `LICENSE`.
- Direct owners: Anders Ravnholt, Klaus Gjelstrup Nielsen, Ali Farahnak, Abishek Narayan.

## Future Improvements

### SEO & Community FAQ Growth

- The `PublishFAQ` tool dynamically creates SEO pages from useful public Q&As. Over time, the `/faq` section grows organically as users ask FinOps questions, creating a knowledge base that drives organic search traffic.
- Consider adding a moderation layer to review dynamically published FAQs before they go live.
