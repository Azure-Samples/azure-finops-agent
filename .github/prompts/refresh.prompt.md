---
description: Refresh tool descriptions, system prompts, and docs against the latest upstream Azure REST API specs and live production telemetry.
---

# Refresh agent knowledge

Goal: every embedded fact in this repo (tool `[Description]` strings, the system prompt in `CopilotSessionFactory.cs`, `.github/copilot-instructions.md`, README, and `docs/*.md`) must match the **current** Azure REST API surface and the latest Microsoft Learn guidance. This prompt intentionally stores **no** API knowledge — discover everything fresh each run.

---

## Phase 1 — Inventory what the repo currently claims

Scan `src/Dashboard/AI/Tools/*.cs`, `src/Dashboard/AI/CopilotSessionFactory.cs`, `src/Dashboard/AI/ChatEndpoints.cs`, `.github/copilot-instructions.md`, README, and `docs/*.md`. Extract every:

- Azure resource provider + api-version + endpoint path.
- Microsoft Graph endpoint and version (`/v1.0` vs `/beta`).
- Closed-enum value list (dimensions, granularities, filter fields, SKU families, regions).
- Templated URL placeholder (e.g. `{scope}`, `{subId}`, `{billingAccountId}`).
- Header name, error code, status code, or quota number quoted as fact.

Output: a single flat **claims table** — one row per claim, with file + line + the literal claim text.

---

## Phase 2 — Pull ground truth from upstream

For every claim in the table, look up the truth from authoritative sources (do not rely on memory). Use the tools available in this environment:

- `github_repo` / `github_text_search` against `Azure/azure-rest-api-specs`, `Azure/azure-sdk-for-net`, `microsoftgraph/msgraph-metadata`.
- `mcp_microsoftdocs_microsoft_docs_search` + `mcp_microsoftdocs_microsoft_docs_fetch` for Microsoft Learn REST reference, throttling/limits pages, and deprecation notices.
- Azure MCP tools (`mcp_azure_*`) for any provider that has a dedicated server.

For each row in the claims table, mark: **keep** / **bump-version** / **delete** / **rename** / **add-replacement**. Note the source URL.

---

## Phase 3 — Mine 5 days of production telemetry

Application Insights App ID: **`89a08d0e-fb6e-4273-8a94-470699c7cfb2`** (Azure CLI is already authenticated).

Run targeted KQL over the last 5 days to find what production has actually been getting wrong. At minimum:

- Failed `dependencies` to `management.azure.com` and `graph.microsoft.com` — group by `name`, `resultCode`. Look for 400 / 404 / 410 / 429.
- Recent `traces` and `exceptions` with `severityLevel >= 3`, plus any message containing `Invalid`, `BadRequest`, `Deprecated`, `throttle`, or `429`.
- Tool failure rates and p95 durations from `Tool done:` log lines.

Synthesize a **production failure inventory**: symptom → frequency → root-cause hypothesis → which claim row(s) to fix. Every recurring failure must produce at least one Phase 4 edit, or be explicitly tagged "infra bug, not a prompt issue".

---

## Phase 4 — Reconcile and rewrite

Apply the deltas from Phase 2 + Phase 3 to the source files. While editing, enforce these efficiency / correctness rules — they encode every production lesson learned:

1. **Templated URLs need grammar blocks.** Any `{placeholder}` in a description MUST list every legal expansion plus one ✓ correct example and one ✗ wrong example.
2. **Closed enums are exhaustive or escape-hatched.** End partial lists with "see GET .../dimensions for the full list" (or equivalent discovery endpoint).
3. **Distinguish response columns from request dimensions.** Anywhere grouping/filtering is described, name what is a valid request value vs. what only appears in the response.
4. **Throttle-aware language.** For Cost Management, Resource Graph, Graph reports, and any endpoint with documented per-tenant quotas: "do not call in parallel; the agent retries 429 silently up to 5×".
5. **Deprecated APIs lead with the replacement.** Mention the old path only as `DEPRECATED:` after the replacement.
6. **One canonical name per concept.** Pick one spelling (e.g. dimension names, service names) and grep every tool to enforce it.
7. **Async LRO endpoints must say so.** Any endpoint returning 202 + `Location` header must include polling instructions.
8. **Dedupe across tools.** The same fact in two tool descriptions wastes LLM context every turn — keep it in the most specific tool, cross-reference the others.
9. **Delete dead references.** Anything pointing at endpoints, files, or features that no longer exist.
10. **Code-level guard > description rule.** When telemetry shows the LLM repeatedly violating a description rule (e.g. omitting `{scope}`), add a preflight check in the tool method that returns HTTP 400 with the corrective grammar. Keep the description rule too — the guard is belt + braces and gives a clean error instead of a confusing upstream 4xx.
11. **Verbatim-string escape audit.** C# `@"..."` tool descriptions break silently when an embedded `"` is not doubled (`""`). Before declaring Phase 4 done, scan every `.cs` in scope for unbalanced quote counts inside `@"..."` blocks — a single rogue `"Daily"` (should be `""Daily""`) breaks the build with thousands of errors that all point at later, unrelated lines.
12. **Verify every batched edit landed.** Multi-replace tools can silently skip an edit when `oldString` does not match exactly (e.g. whitespace drift). After each batch, grep for both the removed and added text to confirm the diff is what you intended — do not trust the success message alone.
13. **Refresh marker.** Add or update `<!-- last refreshed: yyyy-mm-dd -->` at the top of each touched `.md` file and a one-line `CHANGELOG.md` entry.

Files in scope: every `.cs` under `src/Dashboard/AI/`, plus `.github/copilot-instructions.md`, `README.md`, `CHANGELOG.md`, and every file under `docs/`.

---

## Phase 5 — Build, validate live, and report

1. **Build:** `dotnet build src/Dashboard/Dashboard.csproj -warnaserror`. Must succeed. If it fails inside an `@"..."` description with cascading errors at unrelated line numbers, suspect an unescaped `"` (Phase 4 rule 11) before chasing the reported line.
2. **Static audit:** grep every claim row from Phase 1 — confirm the new spelling/version/path is now consistent across the repo.
3. **Live api-version probe:** for every provider + api-version that survived Phase 4, fire one cheap read-only call against the live API (Azure CLI bearer for ARM, separate auth for Graph and Log Analytics). Acceptable: `200/204/400 MissingRegistrationForResourceProvider/401/403`. **Fail the refresh** on `400 InvalidApiVersion`, `404 InvalidResourceType`, or `410 Gone` — that provider's row must be redone. When a provider returns `MissingRegistrationForResourceProvider`, document a fallback endpoint in the description rather than treating the api-version as broken.
4. **Final markdown summary:**
   - Phase 1 claims table.
   - Phase 2 keep/bump/delete/rename/add decisions with source URLs.
   - Phase 3 production failure inventory + which edits address each.
   - Phase 4 file-by-file diff bullets.
   - Phase 5 build status + static audit + live probe matrix.

Do NOT push or open a PR. The deliverable is the working tree changes plus the final summary; the human reviews and ships.
