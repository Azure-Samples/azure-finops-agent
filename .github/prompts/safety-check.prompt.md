---
description: "Audit read-only security enforcement and OAuth permissions for customer deployment"
---

Perform a complete security audit of this agent to verify it is strictly read-only. This is designed for customers cloning this project who want to independently verify the safety guarantees before deploying to their Azure tenant.

### 1. HTTP Method Enforcement

Scan `AzureQueryTools.cs` and confirm:

- `DELETE` is centrally blocked. `GET` is read-only. `PUT`/`PATCH` create owner-bound approval proposals only and never apply directly.
- ARM `POST` requests are validated by the read-only allowlist in `ValidateReadOnlyPostPath`, plus `ValidateConnectivityBody` for Network Watcher `connectivityCheck`.
- List every allowlisted POST pattern and verify each is a read-only query/report/calculation/diagnostic endpoint.
- Verify `ResolveTarget` and `Classify` route credentials by exact host and reject unsafe URLs without echoing them.
- Search for any code path that could bypass the allowlist or host routing (e.g. direct ARM `HttpClient` usage outside the guarded helpers).

### 2. Host-routed Graph, Log Analytics, Storage, Retail and Public Reads

Scan `AzureQueryTools.cs` and confirm:

- Microsoft Graph is restricted to `https://graph.microsoft.com/v1.0/...` or `/beta/...` and uses only the delegated Graph token for that exact host.
- Log Analytics and Application Insights are restricted to `/v1/...` on their exact hosts and only `GET`/`POST`.
- Blob Storage is restricted to `GET` on `{account}.blob.core.windows.net`, uses the storage token only for that exact host, and applies bounded reads/listing caps.
- Retail Prices are public `GET` only on `prices.azure.com/api/retail/prices`.

Scan `PublicWebReader.cs` and confirm:

- Public reads never send tokens, disable proxy/cookies, block literal private/loopback/link-local/metadata hosts before DNS, and connect only to public IPs after DNS and redirects.
- JSON/XML/CSV shaping does not execute code and XML DTDs are prohibited in `ResponseShaper.cs`.

### 3. OAuth Scopes

Scan `Program.cs` for all OAuth scopes requested during authentication. List every scope and confirm:

- All Microsoft Graph scopes are `.Read` variants (not `.ReadWrite`).
- Log Analytics scope is `Data.Read` (not `Data.ReadWrite`).
- ARM scope is `user_impersonation` — document that this is the only delegated scope ARM offers, and that read-only is enforced at the code level.

### 4. Setup Script

Scan `setup-entra-app.ps1` and confirm:

- All API permissions added are read-only (delegated `Scope` type, not application `Role` type).
- No admin-consent-required write permissions are configured.

### 5. Other Tools

Scan all remaining tool files (`ChartTools.cs`, `FaqTools.cs`, `FollowUpTools.cs`, `HtmlPresentationTools.cs`, `MaturityReportTools.cs`, `ReportTools.cs`, `SavingsLedgerTools.cs`, `UploadedFileTools.cs`) and confirm:

- No tool makes authenticated HTTP calls to Azure management APIs.
- Any external HTTP calls (e.g. RSS feeds, IndexNow) do not use Azure tokens.

### 6. HttpHelper

Scan `HttpHelper.cs` and confirm:

- It does not impose or bypass any method restrictions — it's a transport layer.
- Each tool controls what HTTP method is passed to it.

### 7. Token Context

Scan `TokenContext.cs` and confirm:

- Tokens are stored per-user with `volatile` fields for thread safety.
- No shared/global tokens that could leak across user sessions.

### Output

Print a summary table:

| Tool              | Methods Allowed | Write Capability | Scope |
| ----------------- | --------------- | ---------------- | ----- |
| QueryAzure        | ...             | ...              | ...   |
| GetOperationStatus | ...            | ...              | ...   |
| QueryUploadedFile | ...             | ...              | ...   |
| (etc.)            | ...             | ...              | ...   |

Then print the full list of OAuth scopes with their access level (read/write).

Flag any findings that deviate from read-only. If everything passes, confirm: **"All tools are verified read-only. Safe for customer deployment."**
