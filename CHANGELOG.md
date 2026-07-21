# Changelog

All notable changes to **Azure FinOps Agent** are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Paste or upload screenshots into the chat (vision input).** Images (PNG/JPG/JPEG/GIF/WebP, ≤ 20 MB) can now be attached via the file picker, drag-drop, or pasted straight from the clipboard (Win+Shift+S → Ctrl+V) into the input box. Pasted clipboard images are auto-named `screenshot-<timestamp>.png` and shown as a thumbnail chip. Images bypass the Python analysis helper entirely — they ride along as **native vision attachments** (`MessageOptions.Attachments` → `AttachmentFile` with MIME type) so the model literally sees the screenshot (verified: it read the exact dollar amount off a pasted test image). Each image is consumed by the message it's sent with (delisted server-side after send, chip removed client-side); data files (CSV/XLSX/…) still persist across turns via `QueryUploadedFile`. CSP `img-src` gained `blob:` for the local thumbnail previews.

### Changed

- **GitHub Copilot SDK 1.0.6 → 1.0.7** with adoption of two new APIs. (1) **`ProviderConfig.BearerTokenProvider`** — the BYOK Azure OpenAI token is now supplied by an on-demand callback the runtime invokes before every model request, replacing the pre-1.0.7 static-token-baked-at-session-creation approach and its ~1h proactive session-recycle workaround (live sessions can now stay up indefinitely; the expiry-based recycle in `GetOrResumeAsync` was removed). (2) **`SessionConfig.ToolSearch = { Enabled = true }`** — explicitly pins tool-search deferral on so the existing `DeferredTool` (`defer=Auto`) cold-path tools keep their ~50% per-request input-token savings across SDK/CLI bumps. The bundled CLI runtime is pinned to `@github/copilot` **1.0.70** (`<CopilotCliVersion>`) because the SDK's default 1.0.71 is unpublished on npm (only 1.0.71-0 prerelease exists). Also bumped `Microsoft.IdentityModel.*` 8.19.1→8.19.2 and `OpenTelemetry*` 1.16.0→1.17.0. Verified live: a plain turn (session+deltas+done, ~3s) and a 2-tool pricing turn (toolStart=2/toolDone=2, streamed, ~14s) both complete on the upgraded stack.

### Fixed

- **Chat input box now grows with multi-line content.** The auto-grow logic set the textarea height from `scrollHeight`, but `.input-field { flex: 1 }` inside the column `.input-wrapper` (flex-basis:0) made the flex layout ignore that inline height and collapse the field to one line. Changed to `flex: 0 0 auto` (width preserved via the wrapper's `stretch`), so the field expands 24px→400px then scrolls (`overflow-y:auto`). Verified live: 1 line=24px, 8 lines=180px, 60 lines=400px+scroll, cleared=24px.
- **Entra account switch no longer leaks the previous account's state.** Signing in with a different Microsoft account on the same browser session used to hand the new account the previous account's ARM/Graph/Log-Analytics/Storage tokens, consent tiers, and active conversation (the anon→Entra in-memory migration also ran on Entra→Entra switches, and per-resource session tokens were never purged). The OAuth callback now detects the OID change, skips the migration, purges every token/tier belonging to the previous account, and repoints (or drops) the `finops_id` identity cookie so hydration can't resurrect the old identity.
- **Base-tier users no longer fire doomed token exchanges on every message.** Every chat request attempted refresh-token exchanges for Graph, Log Analytics, and Storage even when the user only consented to base ARM — each a guaranteed HTTP 400 at `login.microsoftonline.com` (production telemetry: 18/21 token calls failing) plus up to three wasted Entra round-trips of first-token latency per message. `SessionTokenStore` now gates refresh attempts on the consented-tier list (all add-on tiers are recorded in `graph_tier` at consent time, incl. loganalytics/storage) and applies a 15-minute backoff after a failed add-on exchange. Failed exchanges also log the AADSTS `error`/`error_description` instead of silently returning null. _Note: users who consented to Log Analytics/Storage before this change need to re-click the add-on button once (their persisted consent list predates the tier markers)._
- **Streaming no longer freezes when the browser tab is backgrounded.** The typewriter effect drained text via `requestAnimationFrame`, which browsers fully suspend in hidden tabs — the answer looked stalled and crawled at ~600 chars/s after refocus. Text now renders synchronously while the tab is hidden and the pending animation queue is flushed on `visibilitychange`.
- **Opening a past conversation whose CLI state is gone returns an empty transcript instead of HTTP 500** (`GET /api/sessions/{id}/messages` threw `RemoteRpcException: Session not found` when the session listing and on-disk state disagreed).

### Changed

- **GitHub Copilot SDK 1.0.5 → 1.0.6** — picks up the newer `@github/copilot` runtime and the .NET `CopilotClient.DisposeAsync` graceful-shutdown fix. The CLI runtime is pinned to `@github/copilot` **1.0.68** in `Dashboard.csproj` (`<CopilotCliVersion>`) because the SDK's default `1.0.69` is not published on npm (only `1.0.69-0/-1/-2` prereleases exist) — left unpinned, the build-time `DownloadFile` 404s **both** locally and in the ACR/Docker build. Revisit when a later CLI final is published and the SDK bumps.
- **Default reasoning effort `high` → `medium`** (`AzureOpenAI:ReasoningEffort`) — GPT-5.6 at `medium` roughly halves time-to-first-token (the dominant first-response latency) while preserving tool-orchestration and format-following quality. Trivial turns still auto-route to `low`; set `AzureOpenAI__ReasoningEffort=high` for a max-depth demo.
- **System prompt — removed a duplicate chart-XOR-table restatement** in the Response Shape section (the rule still stands forcefully in Core Rules), trimming per-turn input tokens with no behavior change.

### Added

- **Session pre-warming (`POST /api/chat/warmup`)** — the chat UI fires this once when the user's identity resolves, creating/resuming the Copilot session (system-prompt + tool-schema upload, ~300 ms) off the critical path so the first prompt hits the live-session fast path instead of paying session-creation latency. Fire-and-forget and idempotent-cheap on repeat calls.
- **Clickable prompt chips in AI answers** — the model marks suggested questions with `[label](prompt:full question)`; the chat renderer turns them into styled chips (inside tables, lists, and prose) that send the underlying question on click. The capability table's Examples column is now fully interactive.
- **Per-turn reasoning-effort routing** — a conservative classifier (≤60 chars, no FinOps/data keywords) runs greetings/acknowledgements at `low` effort (~2–3 s first token) via `SetModelAsync`, while real questions keep the configured default. Applied effort is tracked per live session to skip redundant RPCs.
- **Live reasoning panel & thinking animation** — streaming `reasoning` SSE events render as a multi-row rolling "Thinking" panel; the block cursor was replaced with animated gradient dots.
- **Turn gate + refresh re-attach** — one running turn per session (concurrent sends get a friendly busy notice); `GET /api/sessions/{id}/active` lets the frontend re-attach after a refresh and auto-load the finished answer.

### Fixed

- **Stale-session recovery for chat turns — no more hard error _or_ silent hang.** Two related gaps in the SSE chat path when the CLI had evicted a session between turns (the first confirmed via stack trace at `ChatEndpoints.cs`): **(1)** the per-turn effort switch (`session.model.switchTo`) ran _before_ the streaming subscriptions and threw `Session not found` uncaught, surfacing a raw error to the user; **(2)** the `SendAsync` recycle reassigned the session handle but left the SSE subscriptions bound to the _dead_ one, so a recycled turn generated server-side yet never streamed to the browser. Both now recover: the effort switch recycles **before** subscriptions are wired, and the `SendAsync` path detaches and **rebinds** the subscriptions (`WireHandlers`) onto the live session, re-announcing the session id so the frontend keeps streaming into the right conversation. Verified: happy-path streaming (text, tool calls, chart, first-event timing, completion) is fully regression-clean through the refactored subscription wiring; the recovery branches are correct-by-construction and reuse that same proven wiring, but the underlying staleness is a non-deterministic race (idle-disconnected sessions transparently auto-reconnect from disk) that could not be force-fired in re-tests.
- **Sidebar titles no longer leak prompt scaffolding** — when the SDK truncated a long first prompt mid-`[CONTEXT: …]` block, `CleanSummary`/`StripContextPrefix` surfaced the raw injected context (e.g. "[CONTEXT: User is NOT…") as the conversation title. Truncated context blocks are now discarded entirely.
- **Title generation returned empty on reasoning models** — `GenerateTitleAsync` capped `max_completion_tokens` at 24, which GPT-5-series models consume entirely on hidden reasoning. Now uses the modern `/openai/v1/chat/completions` surface with `reasoning_effort: "low"` and 512-token headroom (verified: 8 completion tokens, 0 reasoning tokens).
- **Capability questions now end with clickable starter actions** — "what can you help me with?" answers previously rendered a static table with no follow-up chips. The system prompt now mandates three `SuggestFollowUp` starter actions (public pricing actions when not connected; scoring/cost/idle actions when connected).

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
