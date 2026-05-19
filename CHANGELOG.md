# Changelog

All notable changes to **Azure FinOps Agent** are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed (breaking)

- **Chat sessions moved entirely to the browser.** The server no longer persists conversation history, titles, or "current session" state. The Copilot SDK runs in one-shot mode: each `/api/chat` request creates a fresh `CopilotSession`, replays the browser-provided history as a composed prompt, streams the answer, and disposes. Conversations live in IndexedDB via `idb` (see `src/Dashboard/frontend/src/lib/sessionStore.js`) and are scoped to the user's browser only — switching device or clearing site data removes them.
- `/api/chat` request body now accepts `{ prompt, history: [{ role, content }] }`. The previous `sessionId` field is gone.

### Removed

- `Endpoints/SessionEndpoints.cs` and all `/api/sessions/*` routes (`GET`, `POST /new`, `POST /{id}/select`, `DELETE /{id}`, `GET /{id}/messages`).
- `/api/chat/reset` endpoint.
- `AiTelemetry.LiveSessions`, `CurrentSessionId`, `SessionTitles`, `LoadTitles`, `SaveTitle`, and the on-disk `session-titles.json`.
- `LiveSessionInfo` class.
- `CopilotSessionFactory` resume / recycle / IDOR-guard surface: `GetOrCreateSessionAsync`, `GetOrResumeAsync`, `RecycleSessionAsync`, `CreateNewAsync`, `ListUserSessionsAsync`, `UserOwnsSessionAsync`, `LoadTranscriptAsync`, `DeleteUserSessionAsync`, `DeleteSessionByIdAsync`, `ListAllManagedSessionsAsync`, `GetWorkingDirectory`, `SetCurrentSession`, BYOK bearer-expiry recycling. Replaced by a single `CreateOneShotAsync` per-turn factory method.
- `UserStateJanitor`'s 30-day TTL sweep and dependency on `CopilotSessionFactory`. Janitor now only evicts idle in-memory `UserTokens` / tool closures.
- `ENV COPILOT_HOME=/home/copilot` in the Dockerfile and the corresponding `mkdir -p` in `entrypoint.sh`. The CLI uses its default ephemeral state directory; the App Service `/home` mount is still used for `dataprotection-keys` (auth cookies) but no longer for chat.

### Added

- `src/Dashboard/frontend/src/lib/sessionStore.js` — IndexedDB-backed conversation store (sessions + messages stores, `bySession` index), with `createSession`, `appendMessage`, `getMessages`, `getHistoryForReplay`, `deleteSession`, `renameSession`, plus `exportAll` / `importAll` helpers for JSON backup/restore.

### Security

- Chat content is no longer stored on the server. Cross-user IDOR on conversation history is architecturally impossible (the server has no history to leak). GDPR "right to be forgotten" for chat is a one-click browser action.

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
