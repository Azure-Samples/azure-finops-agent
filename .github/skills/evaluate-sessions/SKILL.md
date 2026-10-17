---
name: evaluate-sessions
description: "Export the deployment's real user conversations, rate each one from 0 to 10, find the ten most important issues and their root causes in the code, then ask which to fix. Use when asked to evaluate, rate or score user sessions, audit answer quality or task completion, or find why users aren't getting good answers."
---

# Evaluate user sessions

Judge whether users got what they asked for, not whether requests returned HTTP 200 or turns went idle. The result is a 0-10 score for every conversation, the ten most important issues with verified root causes in the code, and a question to the user about which to fix. Change no code before the user answers.

Read the repository instructions and the prior findings in `docs/agent-reliability.md` and `CHANGELOG.md` first: a cause already fixed in the current code is not a new finding.

## 1. Confirm the deployment

- Show the active Azure account (name and type only). The target is the default azd environment (`azd env get-values`) unless the user names another; ask when it is ambiguous, and never switch account context to fill a sample.
- Read the deployed revision from `/api/version` on the app URL and compare it with the local revision, so each finding is attributed to the code that produced it.
- Never print tokens, connection strings, identity files, authorization headers, deployment coordinates or raw customer data.

## 2. Export the conversations (about 20 seconds)

```powershell
python .github/skills/evaluate-sessions/scripts/export_sessions.py --out <session workspace>/sessions
```

One JSON record per conversation of the last 30 days (`--days`, `--latest N`), with, per turn: the user's own words and the context the host injected, each tool call with its arguments, success, duration, error and a result excerpt, the answers and visible outputs, approval prompts, errors, the turn status, agent timing and tokens, whether the browser rendered the answer live, and the user's 👍/👎. The script reads the turn records as one zip, the transcripts through Kudu and the telemetry with four Log Analytics queries; all reads are GET-only or queries.

It leaves out, and counts in `summary.json`:

- the maintainer's own use: conversations whose browser telemetry came from Denmark, plus that browser's and that owner's other conversations (`--exclude-country`, repeatable; `--no-country-filter` keeps all). Server requests only see the App Service region, so the country comes from the browser's own telemetry; a conversation whose browser blocked telemetry is kept without `clientCountries`;
- the post-deployment smoke check (`--include-probes` keeps it) and conversations their users deleted;
- records over `--max-chars` (300,000 characters, about 75,000 tokens): chats kept open for days with too many turns for one judge call. Report how many turns they hold.

`--diagnostics` (about a minute) adds what root-cause analysis needs and rating does not: the stored model context from the Foundry project Responses API (the exact system prompt, tool definitions and every model input item, kept about 30 days), each model call and tool span, server warnings and exceptions per turn, and every browser event. Application Insights holds no message content for the current runtime, so the Responses API is the only source of the system prompt and model-side items. Conversations of the retired Copilot SDK runtime are not exported.

Report conversations, turns, distinct owners, countries and every exclusion.

## 3. Rate every conversation (about 4 minutes for 80)

```powershell
python .github/skills/evaluate-sessions/scripts/judge_sessions.py --export <session workspace>/sessions
```

The judge is the app's own model deployment, called through the Responses API with `store=false` and a fixed rubric; it writes `ratings.jsonl` and `ratings-summary.json`, and a rerun rates only what is missing. The deployment's quota is shared with live users: keep `--workers` at 10 or less.

| Score | Meaning |
| --- | --- |
| 10 | Every request answered correctly, grounded in tool evidence, complete (requested chart, script, file or change delivered), no visible error, reasonable time |
| 8-9 | Correct and complete with minor issues: slow, avoidable calls, too long, a missing optional caveat |
| 6-7 | Mostly right, a secondary part missing or one correction needed; or a real blocker (permission, consent, sign-in, unsupported offer) explained truthfully with the steps to fix it |
| 4-5 | Partly helpful: a core part missing, the wrong scope or period, or the user had to repeat themselves |
| 2-3 | Mostly unhelpful: wrong or unsupported figures, failed tools not recovered, a refusal of something it could do |
| 0-1 | No usable answer: an error, an empty or stopped turn, or a harmful or wrong action |

Each verdict also has a classification (`fulfilled`, `partial`, `blocked_expected`, `failed`, `not_evaluable`) and every problem that cost points, with its turn, category, severity and evidence. Report the distribution, mean, classifications and problem categories.

Then spot-check the judge: read the five lowest-rated and three highest-rated conversations yourself, chronologically, and confirm or correct each verdict. Report corrections; a systematic disagreement is a finding about the rubric or the export, never a reason to edit ratings silently: fix `judge_sessions.py` or `export_sessions.py`, re-export and re-judge before the analysis. The first run found four such errors, now handled: aliased GUIDs read as wrong built-in role IDs, KQL `contains` assumed to be case-sensitive, consent buttons the user saw missing from the record, and a turn the user stopped after seven seconds scored as a failure.

## 4. Find the root causes

Work from `ratings-summary.json` (problem categories and the lowest-rated conversations) and the problems in `ratings.jsonl`:

1. Cluster problems by causal mechanism, not by category name, endpoint or repeated occurrence. Count affected conversations and owners: one user's repeated attempts are not a population rate.
2. For each cluster, read the affected turns (tool arguments, errors, answers) and trace the cause in the current code: the system prompt and per-turn tool selection (`src/Dashboard/AI/AgentSessionFactory.cs`), tool descriptions and host rules (`src/Dashboard/AI/Tools/`), the chat pipeline (`ChatEndpoints`, `AgentConversation`) and the frontend when delivery failed. Use a `--diagnostics` export for the exact system prompt, tool definitions, model inputs and server exceptions the turn had.
3. Verify API facts against first-party documentation or specifications (the Microsoft Learn tools, `Azure/azure-rest-api-specs`) and record the source URLs; never infer support from REST conventions.
4. For slow answers, run `python .github/skills/evaluate-sessions/scripts/latency_report.py --export <diagnostics export> [--since <date>]`. It splits model time (about 90% of answer time) by round type (evidence reads, calculation-only rounds, documentation or web lookups, files, the final answer), attributes written tokens (about 8 seconds per 1,000) to what was written, and lists the slowest turns round by round. Every extra model round costs several seconds before any reasoning or writing, so fewer rounds and fewer retyped figures are the main levers.
5. Check whether the cause is still present: compare with `CHANGELOG.md`, local changes and the revision deployed when the affected sessions ran. Mark still present, fixed but not deployed, and already fixed separately.
6. Label each as a verified cause or a hypothesis. Include non-code gaps (missing consent, an unsupported API, no data source) and state what is needed; do not invent an integration.

Keep it bounded: inspect detailed payloads only to verify a claim or a failure, and do not expand into a general code audit.

## 5. Report the ten issues

Rank by user impact (points lost across affected conversations), frequency, causal confidence and fixability. Do not pad: report fewer than ten when the evidence supports fewer.

| # | Issue (what users experienced) | Conversations affected (mean score) | Root cause (file links, confidence) | Proposed fix | Regression check |
| --- | --- | --- | --- | --- | --- |

Use the export's aliases; quote no user identities, credentials or tenant resource names.

## 6. Ask which issues to fix

Use the ask_user tool: a multi-select of the issues (recommended ones selected by default), with "analysis only" as a valid answer. Stop until the user answers.

## 7. Fix the selected issues

- Prefer precise shared validation, correct schemas and authoritative endpoint guidance over more prompt paragraphs; keep the agent minimal (no host-built composite tools or per-scenario recipes) as the repository instructions require.
- Keep the system prompt, tool and parameter descriptions, frontend prompts, documentation and tests consistent wherever a changed contract appears.
- Never remove a user filter, broaden scope, turn unknown into zero, report a fallback as complete, or relax ownership checks, write approvals, credential protection, destructive-operation limits or throttling to make a rating improve.
- Add credential-free regression tests with synthetic data that reproduce each failure; never commit transcripts, identifiers or deployment coordinates.
- Update `CHANGELOG.md`, `docs/tool-catalog.md` and the repository instructions when their contracts change. Run the focused tests and the backend build, and build and render-test the frontend when it changed.
- Do not deploy, push or rerun production conversations without a separate explicit instruction. Report what was fixed, what remains, and the test results.

## Data handling

- Read-only access only: Kudu GETs of the turn-record folder, transcripts and `session.json`; Log Analytics queries; Responses API GETs; and judge calls with `store=false`. Never fetch identity files, publishing profiles, uploads or the storage root; never impersonate users, replay conversations, run recorded tools or renew user tokens.
- Exports stay in the session workspace, outside the repository. Records are pseudonymised (owners, sessions, GUIDs, e-mail addresses, response IDs) and credentials redacted, but tenant resource names remain: treat them as customer data. Private model reasoning is never exported.
