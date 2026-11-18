---
name: update-apis
description: "Audit and update API tool descriptions against latest Microsoft docs. Use when asked to check for deprecated Azure/Graph APIs, update tool descriptions, or verify API versions used by the agent's tools."
---

Review all API tool descriptions in this project against the latest Microsoft documentation. Check for deprecated endpoints, new API versions, and missing operations. Query App Insights for recent tool call errors. Update tool descriptions and copilot instructions accordingly, then build to verify.

Search deeply for all apis though Microsoft docs, all relevant github repo and any other relevant tools to find latest and greatest information.

### Rules

- **Never remove a working API** unless Microsoft has fully retired it — add deprecation notes instead.
- **Keep tool descriptions concise** — the LLM has limited context; use terse format like `GET /{scope}/.../endpoint — purpose`.
- **Prefer current API versions** — use `2025-03-01` for Cost Details, `2024-08-01` for Consumption, `v1.0` for Graph (not beta, unless the endpoint is beta-only).
- **Do not add APIs that require new auth scopes** we haven't configured — our tokens cover ARM (`management.azure.com/user_impersonation`), Graph (`User.Read`, `User.Read.All`, `Organization.Read.All`, `Group.Read.All`, `Reports.Read.All`), Copilot cost management on Graph beta (`User.ReadBasic.All` for the names of the users in its records, `LicenseAssignment.Read.All` and `Group.ReadBasic.All` for their Microsoft Copilot licenses and the groups' names, `CopilotCostManagement.Read.All`, `CopilotCostManagement-UserData.Read.All`, `CopilotCostManagement-Policy.Read.All`, `CopilotCostManagement-Assignment.Read.All`, `CopilotCostManagement-CreditReq.Read.All`, its own token), Log Analytics (`api.loganalytics.io/Data.Read`) and Storage (`storage.azure.com/user_impersonation`). All of these are read-only; the only write scopes are the separately granted `copilotchanges` tier's (`CopilotCostManagement-Policy.ReadWrite.All`, `-Assignment.ReadWrite.All`, `-CreditReq.ReadWrite.All`), used only by approved `ApplyAzureChange` calls.
- **All tools are read-only** — do not document write operations (PUT, PATCH, DELETE) in tool descriptions. POST is restricted to an allowlist of query/report endpoints in `AzureQueryTools.cs`.
- **Always build after changes** to catch string escaping issues in C# verbatim strings.
