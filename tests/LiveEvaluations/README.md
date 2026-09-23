# Live AI Deployment Gate

Production and test-slot deployment both require the `live-evaluations` job to succeed. No percentage threshold, retry-until-green, skip flag, or `continue-on-error` is used. One failed, missing, malformed, wrong-revision or timed-out case blocks deployment. A suite with fewer than 100 distinct questions also fails.

## Coverage

The suite imports every exported sidebar/pricing prompt and every job-template prompt directly from the frontend, deduplicating identical questions and retaining their origins. It adds H200 Spot and English-calculator incident cases. These are **template and incident coverage**, not a verified ranking of the 100 most-used production questions. The read-only history analyzer is separate; its output must be anonymized and reviewed before it becomes regression data.

Each case uses a fresh process and synthetic conversation, the candidate's real `ChatEndpoints`, `CopilotSessionFactory`, protected tools and SDK/CLI, live Azure APIs, and real model inference. SSE supplies tool outcomes and latency; the owner-checked transcript endpoint verifies replay. A separate strict-schema model call judges the final answer against the question and tool evidence. Deterministic failures override the judge. The suite continues recording other cases after an answer fails, but never deploys that candidate.

The test-only in-memory host supplies tokens from the dedicated CI identity; it is **not** a deployed-browser OAuth or delegated-consent test. No evaluation authentication endpoint is added to the production app. Job-template cases test chat/tool routing, not timer execution. Follow-up/upload templates that lack an actual prior conversation or fixture can only test honest clarification, not execution of the missing scenario. Image startup, browser UI and delegated sign-in need their separate regression coverage.

## Required GitHub Configuration

Create a protected GitHub environment named `ai-evaluation`, limited to trusted deployment branches. Configure these repository or environment secrets without printing their values:

| Secret                       | Purpose                                   |
| ---------------------------- | ----------------------------------------- |
| `AZURE_EVAL_CLIENT_ID`       | Dedicated evaluation application identity |
| `AZURE_EVAL_TENANT_ID`       | Evaluation tenant                         |
| `AZURE_EVAL_SUBSCRIPTION_ID` | Azure CLI default evaluation subscription |

Configure these environment/repository variables:

| Variable                   | Value                                                                                     |
| -------------------------- | ----------------------------------------------------------------------------------------- |
| `EVAL_MODEL_ENDPOINT`      | Azure OpenAI-compatible inference endpoint                                                |
| `EVAL_MODEL`               | Candidate deployment name, matching the intended production model                         |
| `EVAL_JUDGE_MODEL`         | Deployment for the independent structured-output judge                                    |
| `EVAL_SUBSCRIPTION_IDS`    | Comma-separated IDs for 1-10 approved test subscriptions                                  |
| `EVAL_DATA_CLASSIFICATION` | `synthetic` (answers published) or `internal-test` (verdicts only); never customer data   |
| `EVAL_TOKEN_RESOURCES`     | `azure,graph,loganalytics,storage` as required by the suite; Azure ARM is always included |

Use workload federation with audience `api://AzureADTokenExchange` and the environment-scoped subject `repo:OWNER/REPOSITORY:environment:ai-evaluation`. The runner renews OIDC before each case, so long runs do not depend on an expired initial assertion. No client secret is needed. Do not reuse or expand the production deployment identity's privileges.

The evaluation principal needs account-scoped model inference and only the read permissions needed in the isolated test scopes. Provision representative budgets, resources, policies, billing and licensing data independently. Graph reports and billing-account APIs may require additional explicitly approved **read-only** permissions beyond subscription Reader. A token being issued does not prove an API permission. Missing scopes, consent, data access or model deployment fail the gate; they are never silently skipped or presented as empty datasets.

GitHub run summaries/artifacts may be public. Never evaluate against customer subscriptions or real customer directories. Reports redact recognizable credentials, resource IDs, URLs, GUIDs, emails and IPs, but cannot reliably discover arbitrary names or financial details. Classification is required locally as well as in CI. With `synthetic`, answers and judge rationale are published. With `internal-test` (maintainer-owned test tenants whose resources are not synthetic), the summary, `results.json` and per-case files carry only questions, tool names/outcomes, timings, pass/fail and deterministic failure reasons; answers, error text and judge rationale are withheld. Raw runner results and partial writes use a private temporary directory outside the artifact directory; only the public projection is copied into reports. Previous generated reports are cleared before each invocation, including setup failures. Raw tool payloads and private reasoning are never published.

## Workflow Report

The run summary lists each question, tool count, duration, pass/fail and reason, with expandable tool names and first-token timing; for `synthetic` data it also shows the judge explanation and escaped answer. Long answer previews are shortened only in the summary; the full redacted answer remains in the seven-day `live-ai-evaluations-<sha>-<attempt>` artifact. Structured JSON binds every result to the case ID, exact question, candidate SHA and suite hash. Setup failures and missing results are explicitly failed.

Cases are sequential to avoid parallel tenant-throttled Cost Management queries. A shared concurrency group prevents simultaneous suites targeting the same fixture environment. Each case has a ten-minute agent budget, five-minute judge budget and an outer process-tree timeout. The workflow has a five-hour suite deadline; incomplete suites block deployment. Cancellation kills the active case process tree, interrupts inter-case waits, and prevents subsequent cases from starting. Completed evidence is retained when replay or judging fails; such failures still block the gate. Replay verification compares parsed persisted user and assistant messages, not a substring of serialized JSON. No failed case is automatically retried to manufacture a passing run.

## Local Validation

```bash
node --test tests/LiveEvaluations/suite.test.mjs
dotnet test tests/Dashboard.Tests/Dashboard.Tests.csproj --filter FullyQualifiedName~EvaluationGateTests
dotnet build tests/LiveEvaluations/LiveEvaluations.csproj --configuration Release
```

After setting the same evaluation variables locally and signing in with an authorized read-only identity, set `EVAL_EXPECTED_SHA` to the candidate's full commit SHA and run `node tests/LiveEvaluations/suite.mjs`. Do not set `GITHUB_ACTIONS=true` locally. Single-question developer probes still use `EVAL_QUESTION` with `dotnet run --project tests/LiveEvaluations/LiveEvaluations.csproj`; those probes **cannot** satisfy the deployment gate.

## Failure-to-Retest Loop

Every report includes `repair-plan.json`, bound to the candidate SHA and suite hash. It lists failed or missing case IDs, their questions, deterministic checks, failure phase (`setup`, `turn`, `replay`, or `judge`), and a targeted next action. The handoff contains no answers, raw errors or judge rationale, so it can be used with `internal-test` reports.

1. Inspect the handoff and authorized source evidence. Repair setup/access prerequisites using only the dedicated evaluation environment; do not expand production permissions.
2. For local reproduction, set `EVAL_ONLY_IDS` to the relevant comma-separated IDs from `retestCaseIds`. Unknown IDs fail immediately. Any filtered run is diagnostic-only and exits nonzero even if 100 or more selected cases pass; CI rejects this filter.
3. Repair the owning code or contract and add a regression. Review intentional rubric changes separately.
4. Clear `EVAL_ONLY_IDS`, rebuild the candidate, and rerun the entire suite on the new revision. Previous passes, subsets, and repair plans never authorize deployment.

This is a review-driven repair loop, not an automatic code rewrite or retry-until-green system. No permission escalation, rubric relaxation or automatic deployment occurs in the evaluation runner.
