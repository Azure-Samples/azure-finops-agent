# Changelog

All notable changes to **Azure FinOps Agent** are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed
- `RouteHandlerAnalyzer` AD0001 NullReferenceException at startup — removed vestigial `(Delegate)` cast in `ChatEndpoints.cs`.
- `DefaultAzureCredential` 2-5s per-message hang on `VisualStudioCredential.RunProcessesAsync` — excluded VS / VS Code / Interactive / AzurePowerShell credential providers in `CopilotSessionFactory`.

### Changed
- Added ambiguous-affirmative intent-binding rule to the Copilot SystemPrompt — "yes/go ahead/proceed" now resolves against the most recent in-chat offer instead of the loudest queued sidebar action.

### Added

- (placeholder)

### Removed

- (placeholder)

## [0.1.0] - 2026-05-05

Initial public release.

### Added

- Azure FinOps Agent reference architecture targeting Azure App Service (Linux container).
- GitHub Copilot SDK 0.3.0 backend with shared `CopilotClient` and per-user `CopilotSession`.
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

[Unreleased]: https://github.com/Azure-Samples/azure-finops-agent/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/Azure-Samples/azure-finops-agent/releases/tag/v0.1.0
