---
agent: agent
description: "Audit 100 individual user conversations end to end, identify new root causes of degraded task completion, and fix the ten strongest evidence-backed blockers locally."
---

# Investigate AI session completion

Evaluate whether users actually solved their problems, not whether requests returned HTTP 200, tools finished, or the SDK became idle. Investigate prompts, tool descriptions and schemas, integrations, API contracts, evidence quality, follow-up behavior, artifacts, latency, and recovery.

Default: **100 distinct populated user conversations**, newest first, within the selected deployment's retained history. Honor any user-specified owner, date range, sample size, or analysis-only constraint. A conversation is the sampling unit, not a request, model iteration, tool call, or repeated snapshot.

Read the repository instructions and relevant prior findings in `docs/agent-reliability.md` and `CHANGELOG.md`. Read `/memories/repo/finops-agent-debugging.md` if available. Do not present an already-fixed historical issue as a new current defect.

## 1. Confirm deployment and evidence access

1. Show the active Azure account without tokens or user identity. Resolve the intended deployment from `azd env get-values`, GitHub Actions configuration, or explicit user input. Ask when the target is ambiguous; never change account context or select another deployment just to fill the sample.
2. Discover the app, Application Insights component, `workspaceResourceId`, and workspace `customerId` in that confirmed scope. Resolve the deployed revision using `/api/version`; compare it with the local revision and relevant fix dates.
3. Query `AppRequests`, `AppDependencies`, `AppTraces`, and `AppExceptions` in Log Analytics. Confirm ingestion is active before interpreting empty results. Use a bounded time window and source-side aggregation. HTTP failure counts are a starting point, not an answer-quality score.
4. Establish authorization to read conversation content. Prefer the existing owner-checked session endpoints for a user who has shared their authenticated session. For an explicitly authorized deployment-wide audit, existing operator access to App Service storage is an alternative. Do not impersonate users, forge cookies, weaken ownership checks, grant permissions, or enable new credentials.
5. Never print tokens, connection strings, identity stores, authorization headers, deployment coordinates, or raw customer data. Keep temporary evidence outside the repository; redact credentials and pseudonymize owners, conversations, and tenant resources before writing or delegating it. Exclude private model reasoning.

For authenticated browser access, use only the exact shared tab and `run_playwright_code`, as required by the repository instructions. Do not open a replacement signed-in or anonymous tab.

## 2. Prove extraction of ONE user's individual session first

Before collecting the cohort:

1. Read `SessionEndpoints`, `CopilotSessionFactory`, and `TurnOutcomeStore` to establish current ownership and persistence contracts. Discover a session through the authorized owner's session list rather than guessing its identifier.
2. Obtain its entire retained user/assistant transcript, available tool requests/results, and durable outcomes. Preserve turn order and the link between each tool request and completion. The session's final answer alone is insufficient.
3. Verify the owner association and distinguish actual user messages from injected connection/file context. Preserve relevant context as evidence, but do not count it as another user request.
4. Record the user's goal, source scope and period, observed tool sequence, errors/retries, final answer or artifact, and whether the requested outcome was achieved. Explicitly distinguish execution completion from business fulfillment.
5. Verify extraction coverage and completeness before scaling up.

### Authorized operator storage alternative

- Resolve `COPILOT_HOME` from configuration. Inspect the actual SDK layout rather than assuming a version-specific directory. Managed ownership comes from the session working directory under the configured `users` or `anon` root, not from a session identifier alone.
- Use GET-only SCM/Kudu reads with an existing Microsoft Entra operator token held in memory. Never fetch publishing profiles, identity/token files, secrets, uploads unrelated to the question, or a whole storage-root archive.
- Read only the needed session metadata, `events.jsonl`, and owner-bound turn outcomes. Session directories can contain only workspace scaffolding: directory count is not conversation count. Do not call every missing event file a failed user interaction.
- If retained SDK SQLite message indexes are available, they may recover historical user/assistant rows. Use a stable read-only copy with the necessary WAL state; verify the relevant tables and snapshot consistency. Never repair or modify the live database.
- Label index-only histories **message-only coverage** when tool events, exact timing, terminal status, or earlier context are unavailable. Deduplicate overlapping event-log/index sessions, checkpoint snapshots and exact replay duplicates without deleting genuine repeated user requests. Do not count index rows as separate sessions.
- Do not resume or replay live agent sessions merely to audit them, execute recorded tools, renew user tokens, or issue additional tenant queries to reconstruct missing historical evidence.

## 3. Select and account for 100 sessions

Build a pseudonymous sampling manifest with owner alias, session alias, source, first/last interaction time, deployed-version evidence, retained user-turn count, tool-evidence coverage and completion state.

- Exclude empty provisioning/warmup records. Identify explicit smoke tests, probes, and replay artifacts separately from real user tasks; do not silently use them to inflate customer coverage.
- Select 100 unique eligible conversations using a reproducible rule. Include successes as controls, not only exceptions. Report concentration by owner, topic, version and date so one user's repeated attempts cannot masquerade as a population-wide rate.
- If the requested recent window contains too few sessions, expand only within the authorized deployment and retention boundary when the user has not fixed the window. State the expansion.
- If fewer than 100 suitable records are accessible, audit all available and state the exact shortfall. Never substitute 100 turns or metadata-only directories and claim 100 conversations.
- Report separate denominators for full-event, message-only, incomplete and unevaluable records. Missing telemetry or an unanswered retained row is an evidence gap until correlated; it is not automatically an application defect.
- Recheck that selected records did not change during capture. Do not classify an active turn as abandoned because collection ended first.

Keep the audit bounded: make one chronological pass through each session's messages and record its assessment immediately. Deduplicate exact retained snapshots and inspect detailed tool payloads only where needed to verify a claim or failure. Do not repeatedly dump large successful catalogues, follow inaccessible offload paths, or expand into a general codebase audit. Delegated batches must return their coverage and strongest findings before pursuing optional hypotheses; incomplete evidence is a reportable limitation, not a reason for open-ended research.

## 4. Evaluate EACH complete conversation

Read every retained request and answer chronologically, including short confirmations and corrections. For each requested objective, assess:

| Dimension | Questions |
| --- | --- |
| Intent and continuity | Did it answer the actual question, preserve dates/scope/constraints, and act on the correct prior offer? Did the user have to repeat the request? |
| Source validity | Were endpoint, API version, method, scope, supported query options, dimensions, permissions and response format correct? |
| Tool contract | Did optional inputs become required? Were argument coercion, schemas, units and parameter names consistent with descriptions? |
| Evidence and calculations | Were claims supported by observed results, with correct totals, currencies, units, pricing tiers, pagination, freshness and coverage? |
| Recovery | Did it correct invalid input, honor full retry deadlines, stop after final throttling, and distinguish missing access from an empty result? |
| Fulfillment | Did it deliver the requested analysis, script, file, chart or approved operation, rather than offer to do it later? Are artifacts real and owner-bound? |
| Practical usability | Is the answer actionable and proportional? Did irrelevant scoring, extra follow-up calls, repeated clarification, latency or UI/transcript loss block progress? |

Use `fulfilled`, `partial`, `blocked_expected`, `failed`, or `not_evaluable`, with a short rationale and exact pseudonymous turn/event references. A truthful permission/capacity limitation can be `blocked_expected`; it is not evidence that the model should bypass a control.

For tool failures, inspect returned HTTP/error/partial metadata as well as SDK success flags. Correlate host, CLI and HTTP spans; many dependency rows can represent retries or nested spans from one operation, not independent failed user requests.

## 5. Establish the ten strongest NEW root causes

Cluster symptoms by causal mechanism, not by individual endpoint or repeated occurrence. For each candidate:

1. Identify affected goals and sessions; include a contrasting successful session when available.
2. Trace the failure to current source, emitted tool schema, runtime behavior, or a missing integration. Distinguish **observed failure**, **verified cause**, and **hypothesis**.
3. Verify API claims against current first-party documentation/specifications. Record supported scopes, query options, response format, permissions and source URLs; do not simply bump every version or infer support from generic REST conventions.
4. Compare the affected deployed version with existing fixes and local changes. Mark historical-only, already-fixed and unverified recurrence separately.
5. Rank by user impact, affected-session frequency, causal confidence and fixability. Do not dilute genuine blockers with cosmetic issues or manufacture ten findings if fewer are supported.

Required findings table:

| Rank | User goal blocked | Evidence and affected-session count | Root cause and confidence | Proposed fix | Regression/acceptance check |
| --- | --- | --- | --- | --- | --- |

Include non-code gaps when relevant, such as an unsupported report source, missing consent, missing input, API coverage or unavailable historical billing data. State exactly what is needed; do not invent an integration or promise access the application does not have.

## 6. Address confirmed blockers locally

Unless the user requested analysis only, implement the strongest supported fixes:

- Prefer precise shared preflight validation, correct schemas and authoritative endpoint-specific guidance over more contradictory prompt paragraphs.
- Keep system prompt, tool/parameter descriptions, frontend-injected prompts, documentation and tests consistent wherever the changed contract appears.
- Reuse existing helpers and patterns. Do not silently remove a user filter, broaden scope, replace requested billed cost with inventory, convert unknown values to zero, or report a fallback as complete.
- Preserve custom-tool permissions, per-user/session ownership, exact write approvals, credential protections, destructive-operation restrictions and throttling rules. Do not relax safety settings to make an evaluation pass.
- Add credential-free regression cases with synthetic data that reproduce the observed failure and verify the actual required output. Never commit real transcripts, user identifiers or deployment coordinates.
- Update `CHANGELOG.md`, `docs/tool-catalog.md` and repository instructions when their contracts change.
- Run focused tests and the backend build; build and render-test the frontend if touched. Test schemas/invocation behavior for tool changes, not just description substrings.
- Do not deploy, push, rerun production model conversations, or perform a live post-deploy check without separate explicit instruction.

## 7. Deliver a verifiable result

Report:

1. How one owner-bound session was obtained safely.
2. Exact sample size, dates, distinct owners, version mix and full-event versus message-only coverage; any shortfall, probes, missing context or sampling bias.
3. Fulfillment results and the ten prioritized causes, with anonymized evidence and first-party API references.
4. What was fixed, what was already fixed, and what remains blocked or unproven.
5. Tests/build results and remaining live-verification limits.

Keep the final response free of discovered deployment coordinates and customer identities. Clean up raw/index copies and temporary credentials; retain only the necessary redacted audit results in the session workspace. End with the state of local changes and explicitly state whether deployment occurred.
