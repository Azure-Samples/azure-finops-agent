# Changelog

All notable changes to **Azure FinOps Agent** are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **`EstimateTokenCost` tool (`CostEstimateTools`)** — deterministic C# calculator for monthly/volume LLM token costs. The agent looks up per-1M rates, then delegates the arithmetic here so the headline, summary table, and step-by-step always reconcile (components are summed in code and a ready-made per-model `breakdown` string is returned). Fixes inconsistent monthly totals where the table disagreed with the step-by-step or two token assumptions were blended.
- **Expanded FinOps maturity framework** — `ReportMaturityScore` now enumerates all ~19 capabilities across Crawl (7), Walk (6: reservations & savings plans, right-sizing, dev/test scheduling, tag policy enforcement, hybrid benefit & licensing, storage & lifecycle), and Run (6: executive reporting, chargeback readiness, unit economics, anomaly detection, cost allocation & MG governance, AI/GPU cost) — each with explicit "what to check" guidance, mandatory evidence numbers, and per-subscription spread.
- **Deep maturity report pattern** — `GenerateHtmlPresentation` gained a "FinOps Maturity Assessment Report" structure (score banner → exec summary → domain scores → all-capability chart → per-capability evidence tables → per-subscription summary → Crawl→Walk→Run roadmap → data-source appendix) for assessments that need full depth instead of a 5-slide summary.

### Changed

- **Default model `gpt-5.4` → `gpt-5.6-sol`** (version `2026-07-09`, GlobalStandard) with **Priority processing** (`properties.serviceTier: 'Priority'`) — faster time-to-first-token. The azd Bicep (`infra/modules/aoai.bicep`) now sets `serviceTier` (new `serviceTier` param, default `Priority`).
- **Reasoning effort is now configurable** via `AzureOpenAI:ReasoningEffort` (default **`high`**, was hardcoded `xhigh`). Production telemetry showed single `xhigh` LLM round-trips taking 8+ minutes — the dominant cause of slow chats. Set `AzureOpenAI__ReasoningEffort=xhigh` to opt back in.
- **Excluded the Copilot CLI `task` sub-agent tool** from all sessions (`SessionConfig.ExcludedTools`) — a single `task` call spawned a nested agent that looped for 8 minutes in production. FinOps work uses direct tools only.
- **Upgraded GitHub Copilot SDK `1.0.0-beta.4` → `1.0.5`** — migrated to the GA API surface: namespace `GitHub.Copilot.SDK` → `GitHub.Copilot`; `CopilotClientOptions.CopilotHome` → `BaseDirectory`; `OnPermissionRequest` now uses `PermissionHandler.ApproveAll` (replaces `PermissionRequestResult`); `CopilotSession.GetMessagesAsync` → `GetEventsAsync`; `SessionListFilter.Cwd` and `SessionMetadata.Context.Cwd` → `WorkingDirectory`; tool lists widened from `List<AIFunction>` to `List<AIFunctionDeclaration>` (`SessionConfig.Tools` is now `ICollection<AIFunctionDeclaration>`). Behaviour-preserving — streaming and session persistence unchanged.
- **Dependency updates** — `Microsoft.IdentityModel.JsonWebTokens` + `Microsoft.IdentityModel.Protocols.OpenIdConnect` `8.18.0` → `8.19.1`; `OpenTelemetry` + `OpenTelemetry.Api` `1.15.3` → `1.16.0`.
- **`GetAzureRetailPricing` Foundry guidance** — the tool description now decodes `skuName` token-by-token (Direction / Zone / Deployment / Context tier) and makes stating the pricing basis (Standard vs Batch, Global vs Data Zone vs Regional, region, currency) mandatory, so model prices are no longer mis-reported by silently picking the cheapest Batch/cached/Data-Zone row.


## [0.2.0] - 2026-05-14

Persistent sessions, hardened auth, richer chat UX.

### Added

- **Persistent multi-session chat** — Conversations survive browser close, page refresh, container restarts, and slot swaps. Each user keeps a sidebar list of past conversations they can resume. Powered by GitHub Copilot SDK 1.0.0-beta.3 with on-disk session state on the App Service `/home` mount. New `SessionEndpoints.cs` exposes `GET/POST/DELETE /api/sessions`.
- **FinOps maturity scoring UI** — Crawl / Walk / Run sidebar now ships with score buttons, a playbook section, collapsible maturity cards, and interactive star ratings updated by the agent.
- **Analyze button in the chat input** — One-click "find cost waste & recommend actions" that also picks up any attached files.
- **EA / MCA pricesheet support** — Sidebar prompts for downloading negotiated pricesheets and running commitment-aware analysis.
- **`WebFetchTools`** — Agent can pull from public web pages (Azure docs, blogs, pricing) to ground answers.
- Code-level scope-prefix preflight in `QueryAzure` — rejects bare `/providers/Microsoft.CostManagement/...` (and `/Consumption/budgets`, `/PolicyInsights/policyStates`) calls with HTTP 400 + a corrective grammar message instead of a confusing 404 from ARM.
- `=== CRITICAL: SCOPE-PREFIXED ENDPOINTS ===` section hoisted to the top of the `QueryAzure` tool description.
- Microsoft Graph: promoted `/v1.0/reports/getMicrosoft365CopilotUsageUserDetail`, `getMicrosoft365CopilotUserCountSummary`, and `/v1.0/deviceManagement/managedDevices` as primary paths (now GA); `/beta/` paths kept as fallbacks.
- Inline script preview in chat with syntax highlighting, copy button, expand/collapse, and download.
- JSON syntax highlighting in the tool inspector popover.
- Pie chart total subtitle.
- New `docs/architecture-and-security.md` and a "How it works" section in the README.
- Demo options for users without an Azure tenant.

### Changed

- Upgraded to GitHub Copilot SDK **1.0.0-beta.3** with new session lifecycle.
- Tool calls and charts are scoped per-session — switching conversations no longer mixes up the right sidebar.
- Mid-turn "thinking" narration is cleared before the final answer streams in.
- "Top 3 fixes" renders as a markdown table with clearer impact details.
- Ambiguous-affirmative intent-binding rule in the Copilot SystemPrompt — "yes / go ahead / proceed" now resolves against the most recent in-chat offer instead of the loudest queued sidebar action.
- Local dev secrets migrated to `dotnet user-secrets`; app fails fast on missing `AzureOpenAI:Endpoint`.

### Fixed

- `RouteHandlerAnalyzer` AD0001 NullReferenceException at startup — removed vestigial `(Delegate)` cast in `ChatEndpoints.cs` (.NET 10).
- `DefaultAzureCredential` 2-5s per-message hang on `VisualStudioCredential.RunProcessesAsync` — excluded VS / VS Code / Interactive / AzurePowerShell credential providers in `CopilotSessionFactory`.
- `Microsoft.Migrate` resource type corrected from `migrateProjects` to `assessmentProjects` (was returning 404 InvalidResourceType).
- `Microsoft.Quota` endpoint now documents `MissingRegistrationForResourceProvider` failure mode + fallback to `Microsoft.Compute/locations/{region}/usages`.
- HTTP 429 retry hardening with proper `Retry-After` handling.
- Long-running sessions no longer fail with HTTP 401 mid-turn — proactive BYOK bearer recycling and background tenant-token refresh.
- Bar chart rendering bug.
- Duplicate AI avatar when resuming a still-streaming session.

### Removed

- `ScheduleTools` — folded into existing flows.

### Security

- Federated managed identity for Entra OAuth, ID token validation, nonce, and redirect URI allowlists.
- `PublishFAQ` is now auth-gated — anonymous users can chat but cannot publish public FAQ pages.
- Hardened Content Security Policy on the `/slides` route.

## [0.1.0] - 2026-05-05

Initial public release.

### Added

- Azure FinOps Agent reference architecture targeting Azure App Service (Linux container).
- GitHub Copilot SDK 1.0.0-beta.3 backend with shared `CopilotClient` and per-user `CopilotSession`. Multi-session per user with on-disk persistence at `{CopilotHome}/.copilot/session-state/` (mapped to App Service `/home` Azure Files mount). Idle sessions auto-disconnect after 30 min via `SessionIdleTimeoutSeconds`; resume rehydrates on next prompt with a fresh BYOK bearer token.
- Azure OpenAI BYOK using **system-assigned managed identity** (`DefaultAzureCredential`) — no client secret needed for AOAI.
- Microsoft Entra ID multi-tenant OAuth with **incremental consent**:
  - Base tier: Azure ARM (`user_impersonation`)
  - License Optimization: `Organization.Read.All`, `Reports.Read.All`
  - Cost Allocation: `User.Read.All`, `Group.Read.All`
  - Log Analytics: `Data.Read`
  - Cost Exports: Azure Storage `user_impersonation`
- Custom AI tools: `QueryAzure`, `BulkAzureRequest`, `QueryGraph`, `QueryLogAnalytics`, `ListCostExportBlobs`, `ReadCostExportBlob`, `RetailPricing`, `GetAzureServiceHealth`, `RenderChart`, `RenderAdvancedChart`, `GenerateHtmlPresentation`, `GenerateScript`, `ReportMaturityScore`, `SuggestFollowUp`, `PublishFAQ`, `QueryUploadedFile`, `IdleResource*`, `Anomaly*`, `Schedule*`.
- **Read + write, never delete**: `DELETE` is blocked at the HTTP helper layer; destructive cleanup goes through `GenerateScript` so the user reviews before running.
- FinOps Maturity Framework UI (Crawl / Walk / Run) with per-level scoring.
- Server-Sent Events streaming (text deltas, tool start/done, charts, scripts, slides, scores).
- File uploads (CSV/TSV/JSON/TXT/XLSX/PDF/Parquet) with Python-backed inspection.
- OpenTelemetry end-to-end: .NET app + Copilot CLI subprocess → in-container OTel collector → Azure Application Insights.
- CI/CD via GitHub Actions: OIDC-based Azure login, ACR Buildx, App Service restart.

### Security

- HSTS, CSP, X-Frame-Options, X-Content-Type-Options, Referrer-Policy, Permissions-Policy.
- PKCE on the OAuth code flow.
- Origin/Referer CSRF check on every state-changing request.
- Absolute 8h session lifetime, 1h idle timeout.
- Crypto-random session user IDs; `SameSite=Lax`, `Secure`, `HttpOnly` cookies.
- DataProtection keys persisted to `/home/dataprotection-keys` to survive container restarts.

[Unreleased]: https://github.com/Azure-Samples/azure-finops-agent/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/Azure-Samples/azure-finops-agent/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/Azure-Samples/azure-finops-agent/releases/tag/v0.1.0
