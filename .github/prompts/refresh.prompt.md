---
description: Refresh Azure API descriptions, system prompts, and docs from authoritative specifications and telemetry discovered at runtime.
---

# Refresh agent knowledge

Keep every embedded API fact current without storing customer or maintainer deployment coordinates in source.

## 1. Inventory repository claims

Scan `src/Dashboard/AI/Tools/*.cs`, `src/Dashboard/AI/CopilotSessionFactory.cs`, `src/Dashboard/AI/ChatEndpoints.cs`, `.github/copilot-instructions.md`, `README.md`, and `docs/*.md`. Build one claims table containing each:

- Azure resource provider, API version, and endpoint path
- Microsoft Graph endpoint/version
- Closed enum, quota, status code, header, and URL placeholder
- File, line, and literal claim

## 2. Verify authoritative sources

Validate each claim against current Azure REST API specifications, Microsoft Graph metadata, SDK source, and Microsoft Learn. Mark each row `keep`, `bump-version`, `delete`, `rename`, or `add-replacement`, with a source URL. Do not rely on model memory.

## 3. Discover and inspect telemetry

Do not use a hardcoded Application Insights application ID, workspace ID, subscription, resource group, or component name.

1. Show the active Azure account.
2. Resolve the intended deployment from `azd env get-values`, GitHub Actions configuration, or explicit user input.
3. List Application Insights components in the confirmed scope and select only an unambiguous target.
4. Resolve its backing workspace dynamically through `workspaceResourceId`.
5. Query the last five days of `AppExceptions`, `AppTraces`, `AppRequests`, and `AppDependencies`.
6. Group failed Azure/Graph dependencies by operation and result code; summarize exception types, tool failures, and p95 durations.

Never print or write discovered deployment identifiers into tracked files. If the target is ambiguous, stop and ask the user to choose it.

## 4. Reconcile and edit

Apply source and telemetry findings while enforcing these rules:

1. URL placeholders include legal expansions and one correct/incorrect example.
2. Closed enums are exhaustive or point to a discovery endpoint.
3. Request dimensions and response columns are distinguished.
4. Tenant-throttled APIs explicitly prohibit parallel calls.
5. Deprecated APIs lead with the replacement.
6. One canonical name is used per concept.
7. Long-running operations document polling behavior.
8. Duplicate facts are consolidated.
9. Dead references are removed.
10. Repeated model mistakes get code-level preflight guards.
11. C# verbatim description strings are checked for doubled quotes.
12. Every edit is verified on disk.
13. Touched Markdown gets a current refresh marker and `CHANGELOG.md` is updated.

## 5. Validate

- Build the backend with warnings as errors.
- Build the frontend when touched.
- Re-scan every claim from the inventory.
- Probe surviving provider/API-version pairs with cheap read-only calls in the confirmed scope.
- Fail on invalid API versions, invalid resource types, or retired endpoints.
- Report claim decisions, telemetry findings, edits, build status, and live-probe results.

Do not push, open a pull request, expose discovered identifiers, or deploy.
