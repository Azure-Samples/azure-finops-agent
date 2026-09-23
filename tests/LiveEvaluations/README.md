# Live AI Deployment Gate

Production and test-slot deployment both require the `live-evaluations` job to succeed. Local and CI runs use the same explicitly selected **20 distinct questions**, and all 20 must pass. No percentage threshold, retry-until-green, skip flag, or `continue-on-error` is used. One failed, missing, malformed, wrong-revision or timed-out case blocks deployment. A subset, an arbitrary replacement set of 20, or changed case contracts cannot satisfy the gate.

## Coverage

The full catalog imports every exported sidebar/pricing prompt and every job-template prompt directly from the frontend, deduplicating identical questions and retaining their origins. It adds H200 Spot and English-calculator incident cases. The live gate selects 20 named cases from that catalog rather than running its entire 124-question inventory. These are **curated representative question types**, not a verified ranking of the 20 most-asked production questions. The read-only history analyzer is separate; its output must be authorized, anonymized and reviewed before it becomes regression data.

The selection covers Crawl maturity; current-month cost by service, subscription and resource; forecasts and budgets; Advisor; inventory and tags; Microsoft 365 licenses and Copilot usage; chargeback; VM, storage, database, application and model pricing; an idle-resource script for review; H200 Spot quota; and deterministic English-language arithmetic. Questions needing missing attachments or a prior turn are not selected. The broader catalog remains available for coverage checks; backend, Python, frontend unit and browser regressions are not reduced.

Each case uses a fresh process and synthetic conversation, the candidate's real `ChatEndpoints`, `CopilotSessionFactory`, protected tools and SDK/CLI, live Azure APIs, and real model inference. SSE supplies tool outcomes and latency; the owner-checked transcript endpoint must replay the exact decoded question and every completed answer message, not merely contain the question. A separate strict-schema model call judges the final answer against the question and tool evidence. Deterministic failures override the judge. Malformed judge output retains the execution diagnostics and fails the case. The suite continues recording other cases after an answer fails, but never deploys that candidate.

The evaluator shares the application's reported-tool-failure classifier rather than treating SDK callback completion as success. Empty payloads and explicit failure/cancellation in nested evidence fail deterministically. Unknown, partial or awaiting-approval evidence is not automatically a tool execution failure: the judge must still establish whether the answer satisfies the requested task and accurately states those limitations.

The test-only in-memory host supplies tokens from the dedicated CI identity; it is **not** a deployed-browser OAuth or delegated-consent test. No evaluation authentication endpoint is added to the production app. Job-template cases test chat/tool routing, not timer execution. Follow-up/upload templates that lack an actual prior conversation or fixture can only test honest clarification, not execution of the missing scenario. Image startup, browser UI and delegated sign-in need their separate regression coverage.

## Required GitHub Configuration

Create a protected GitHub environment named `ai-evaluation`, limited to trusted deployment branches. Configure these repository or environment secrets without printing their values:

| Secret                       | Purpose                                   |
| ---------------------------- | ----------------------------------------- |
| `AZURE_EVAL_CLIENT_ID`       | Dedicated evaluation application identity |
| `AZURE_EVAL_TENANT_ID`       | Evaluation tenant                         |
| `AZURE_EVAL_SUBSCRIPTION_ID` | Azure CLI default evaluation subscription |

Secrets may live on the `ai-evaluation` environment or the repository. They are optional only in the reusable workflow's caller declaration so environment-scoped secrets can resolve on the job. The job's configuration check reports every missing value before login or inference; absence never skips or passes the gate. An unconfigured environment is a setup failure, not evidence that the agent passed or failed its questions.

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

GitHub run summaries/artifacts may be public. Never evaluate against customer subscriptions or real customer directories. Reports redact recognizable credentials, resource IDs, URLs, GUIDs, emails and IPs, but cannot reliably discover arbitrary names or financial details. With `synthetic`, answers and judge rationale are published. With `internal-test` (maintainer-owned test tenants whose resources are not synthetic), the summary, `results.json` and per-case files carry only questions, tool names/outcomes, timings, pass/fail and deterministic failure reasons; answers, error text and judge rationale are withheld, including local runs. Raw per-case captures are staged in a unique ignored directory outside published output; only classification-safe projections reach artifacts. Handled interruption and failure paths clean up that exact private directory. Raw tool payloads and private reasoning are never published.

## Validate a Branch Without Deploying

Manually dispatch the existing feature workflow with `deploy=false` (the default):

```bash
gh workflow run feature.yml --ref YOUR_BRANCH -f deploy=false
```

This runs both regression and live-evaluation gates at that branch revision and skips the deployment job regardless of the verdict. Validation-only dispatches use a separate concurrency group so they cannot cancel a test-slot deployment. A manual deployment requires `deploy=true` and both successful gates. Automatic pushes retain their existing deployment behavior; when publishing a candidate solely for a validation-only dispatch, use a `[skip ci]` commit message to prevent the push-triggered deployment workflow, then run the command above. Never interpret the skipped push run as validation.

## Workflow Report

The run summary lists each question, tool count, duration, pass/fail and reason, with expandable tool names and first-token timing; for `synthetic` data it also shows the judge explanation and escaped answer. Long answer previews are shortened only in the summary; the full redacted answer remains in the seven-day `live-ai-evaluations-<sha>-<attempt>` artifact. Structured JSON binds every result to the case ID, exact question, candidate SHA and suite hash. Setup failures and missing results are explicitly failed.

Cases are sequential to avoid parallel tenant-throttled Cost Management queries. A shared concurrency group prevents simultaneous suites targeting the same fixture environment. Each case has a ten-minute agent budget, five-minute judge budget and an outer process-tree timeout. SIGINT/SIGTERM stop the suite, prevent further cases from starting, and terminate only its owned child process trees. The workflow has a five-hour suite deadline; incomplete suites block deployment. No failed case is automatically retried to manufacture a passing run.

## Local Validation

```bash
node --test tests/LiveEvaluations/*.test.mjs
dotnet test tests/Dashboard.Tests/Dashboard.Tests.csproj --filter FullyQualifiedName~EvaluationGateTests
dotnet build tests/LiveEvaluations/LiveEvaluations.csproj --configuration Release
```

After setting the same evaluation variables locally and signing in with the dedicated authorized evaluation identity, commit the candidate, rebuild it, set `EVAL_EXPECTED_SHA` to that full commit SHA and run `node tests/LiveEvaluations/suite.mjs` from the repository root. The suite verifies actual Git HEAD plus tracked and nonignored untracked application/evaluator/workflow sources and inherited build configuration; unrelated local files and solution/documentation edits do not invalidate a project-based evaluation. The runner verifies that both its own binary and the Dashboard binary were built at the expected revision. Rebuild after every new candidate commit; assigning a new SHA to an old DLL cannot satisfy the gate. Do not set `GITHUB_ACTIONS=true` locally. Single-question developer probes still use `EVAL_QUESTION` with `dotnet run --project tests/LiveEvaluations/LiveEvaluations.csproj`; those probes **cannot** satisfy the deployment gate.

For explicitly authorized local maintainer-lab testing, the existing `az login` user session can supply credentials without creating a client secret. Set `EVAL_TENANT_ID` to the lab tenant and `EVAL_SUBSCRIPTION_IDS` to only approved subscriptions in that tenant; the model endpoint must belong to that tenant too. The first evaluation subscription selects its cached CLI identity for resource tools, candidate inference and the judge, so no global `az account set` is necessary. A tenant-only CLI request can select the wrong cached user when multiple lab accounts are signed in; Azure CLI does not accept tenant and subscription together. The test host supplies this explicit credential through an internal factory overload; normal application credential selection is unchanged. Use `internal-test` for nonsynthetic lab data and keep settings outside tracked files. Verify that the selected subscription belongs to `EVAL_TENANT_ID` and check actual read/inference access: issuing a token alone is not proof of permissions. Never export the CLI token cache or upload personal tokens to GitHub; workflow runs still require their dedicated read-only OIDC identity.

Use the workstation's approved NuGet and Python package sources rather than adding machine-specific feeds to the repository. If report/file helpers use a virtual environment, set `FINOPS_PYTHON` to that environment's absolute Python executable path before starting the evaluator. The child evaluation processes inherit it; selecting an interpreter only in the editor does not configure the helper processes.

### Optional private local diagnostics

For an explicitly authorized local run, set `EVAL_PRIVATE_DIAGNOSTICS_DIRECTORY` to an **existing absolute directory outside the repository and published output**. The paths must not overlap in either direction, including through symlinks or Windows junctions. Relative, missing, inaccessible, repository-contained and artifact-contained destinations fail before any case starts. This option is rejected in CI (`GITHUB_ACTIONS`, `CI` or `TF_BUILD` enabled); do not pass it to workflows.

For example, after configuring the dedicated evaluation identity, candidate revision and binaries:

```powershell
$diagnostics = Join-Path $env:LOCALAPPDATA 'AzureFinOps\LiveEvaluationDiagnostics'
New-Item -ItemType Directory -Force -Path $diagnostics | Out-Null
$env:EVAL_PRIVATE_DIAGNOSTICS_DIRECTORY = $diagnostics
$env:EVAL_DATA_CLASSIFICATION = 'internal-test'
node tests\LiveEvaluations\suite.mjs
```

Each attempt creates a unique `live-evaluations-*` child directory. It retains the evaluator's **already-redacted failed-case** JSON, including judge rationale, answer/error text and failed-tool details, plus any unfinished `.json.tmp` capture. Passing-case captures are removed. A final `diagnostics.json` records the completed-case count, loaded failed-case results and the host failure, including handled interruption. It does not collect process output, environment values, credentials or CLI token caches. Incomplete captures are diagnostic evidence only, never accepted case results.

Use an access-restricted local folder, not a shared or synchronized directory. New run directories are owner-only on POSIX and inherit the parent directory's access controls on Windows; the final diagnostics file uses owner-only permissions where supported. These files can still contain sensitive tenant names or financial information. **Do not commit, upload, or publish them.** Retained runs are not automatically deleted; review and remove only the diagnostic run directories you own when no longer needed.

Public summaries and artifacts still obey `EVAL_DATA_CLASSIFICATION`: enabling private diagnostics does not publish internal-test answers or rationale. Neither classification publishes raw failed-tool arguments/details or undeclared private fields. Retention setup/write/cleanup failures visibly fail the run, even if all 20 case verdicts passed. The pinned questions, original rubrics, tool/time limits and unanimous acceptance rule are unchanged; this option adds neither retries nor a diagnostic-subset bypass. With the variable unset or empty, the existing disposable-capture cleanup remains unchanged. Clear the option with `Remove-Item Env:EVAL_PRIVATE_DIAGNOSTICS_DIRECTORY` after local diagnosis.

Fix a failed case by inspecting its source evidence and reproduction, repairing the owning code or contract, and rerunning the full candidate suite. Review intentional rubric changes separately. No automatic code rewrite, permission escalation or rubric relaxation occurs in the deployment workflow.
