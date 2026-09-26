# Live AI Deployment Gate

Production and test-slot deployment both require the `live-evaluations` job to succeed. Local and CI runs use the same explicitly selected **20 distinct questions**, and all 20 must pass. No percentage threshold, retry-until-green, skip flag, or `continue-on-error` is used. One failed, missing, malformed, wrong-revision or timed-out case blocks deployment. A subset, an arbitrary replacement set of 20, or changed case contracts cannot satisfy the gate.

## Coverage

The full catalog imports every exported sidebar/pricing prompt and every job-template prompt directly from the frontend, deduplicating identical questions and retaining their origins. It adds H200 Spot and English-calculator incident cases. The live gate selects 20 named cases from that catalog rather than running its entire 124-question inventory. These are **curated representative question types**, not a verified ranking of the 20 most-asked production questions. The read-only history analyzer is separate; its output must be authorized, anonymized and reviewed before it becomes regression data.

The selection covers Crawl maturity; current-month cost by service, subscription and resource; forecasts and budgets; Advisor; inventory and tags; Microsoft 365 licenses and Copilot usage; chargeback; VM, storage, database, application and model pricing; an idle-resource script for review; H200 Spot quota; and deterministic English-language arithmetic. Questions needing missing attachments or a prior turn are not selected. The broader catalog remains available for coverage checks; backend, Python, frontend unit and browser regressions are not reduced.

Each case uses a fresh process and synthetic conversation, the candidate's real `ChatEndpoints`, `CopilotSessionFactory`, protected tools and SDK/CLI, live Azure APIs, and real model inference. SSE supplies tool outcomes and latency; the owner-checked transcript endpoint must replay the exact decoded question and every completed answer message, not merely contain the question. A separate strict-schema model call judges the final answer against the question and tool evidence. Deterministic failures override the judge. Malformed judge output retains the execution diagnostics and fails the case. The suite continues recording other cases after an answer fails, but never deploys that candidate.

The evaluator shares the application's reported-tool-failure classifier rather than treating SDK callback completion as success. Empty payloads and explicit failure/cancellation in nested evidence fail deterministically. Unknown, partial or awaiting-approval evidence is not automatically a tool execution failure: the judge must still establish whether the answer satisfies the requested task and accurately states those limitations.

A well-formed negative judge verdict is reported as a rejected answer, not missing or malformed output. Every judge flag must still be a boolean, `accepted`, `grounded`, `complete` and `efficient` must all be true, `efficiencyScore` must be an integer from 1 to 5 and at least 3, and the reason must be nonempty to pass. This diagnostic distinction does not relax acceptance or publish private judge rationale.

The judge receives the whole end-to-end session, not only the answer: the agent model and reasoning effort, total and first-token seconds, tool calls against the case's tool and duration budgets, failed calls, sequential rounds (groups of overlapping calls), peak concurrency, tool wall time versus model time, service throttle notices, and every call in start order with its round, start/end offsets, duration, host-classified success, arguments and result. Using its knowledge of the Azure, Microsoft Graph and Log Analytics APIs, it decides what an expert would call for the exact question and grades efficiency from 1 (severe waste) to 5 (near-minimal). Clear waste fails the case: failed or rejected calls, repeated requests, independent reads split into sequential rounds instead of one batch, paging raw data when the API can filter, group or aggregate server-side, needless documentation lookups for stable APIs, or unrelated reads. Sequential Cost Management query/forecast calls (host-serialized), waits after throttle notices, a needed discovery call, local `QueryToolResult` reads of retained results (including one schema inspection and exact arithmetic), structurally filtered Retail Prices sets narrowed locally, required verification reads and configured reasoning time are not waste. The score anchors are objective: 4 allows one or two avoidable calls or rounds, 3 allows more that still leave the session reasonably fast, and 2 means a failed call or avoidable work that roughly doubled elapsed time or service calls against an expert's path. Efficiency never compensates for a wrong or incomplete answer. Published results add per-tool durations, the round/concurrency/tool-versus-model timeline and the efficiency flag and score; the rationale stays withheld for `internal-test` data.

The judge compares each requested metric across the whole answer and visible outputs, including separately labelled columns, without silently correcting an answer's values from the source. Inclusive/exclusive date conversions must denote the same interval. Graph [enabled prepaid units](https://learn.microsoft.com/graph/api/resources/licenseunitsdetail?view=graph-rest-1.0) are inventory, not invoice-confirmed paid quantities; assignments are not activity. Available inventory counts remain required even when purchase quantities or contract costs are unknown. Conversely, supplied billing quantities/rates cannot be omitted as unknown, and an unavailable required activity report still leaves that task incomplete. The one maintainer-approved exception is zero seats: when the product's report was requested and is unavailable, and inventory shows zero enabled and zero assigned seats, the per-seat answers (0 of 0 active or inactive, no licensed non-users, no inactive-seat waste) are determinate, provided the answer attributes them to zero seats, discloses the unavailable report, leaves unlicensed-user activity unknown and claims no price or spend. Apart from that exception, these evidence distinctions do not change the case questions, rubrics, required reads or acceptance checks.

The test-only in-memory host supplies tokens from the dedicated CI identity; it is **not** a deployed-browser OAuth or delegated-consent test. No evaluation authentication endpoint is added to the production app. Job-template cases test chat/tool routing, not timer execution. Follow-up/upload templates that lack an actual prior conversation or fixture can only test honest clarification, not execution of the missing scenario. Image startup, browser UI and delegated sign-in need their separate regression coverage.

## Required GitHub Configuration

Create a protected GitHub environment named `ai-evaluation`, limited to trusted deployment branches. Configure these repository or environment secrets without printing their values:

| Secret                       | Purpose                                   |
| ---------------------------- | ----------------------------------------- |
| `AZURE_EVAL_CLIENT_ID`       | Dedicated evaluation application identity |
| `AZURE_EVAL_TENANT_ID`       | Evaluation tenant                         |
| `AZURE_EVAL_SUBSCRIPTION_ID` | Azure CLI default evaluation subscription |
| `EVAL_MODEL_ENDPOINT`       | Azure OpenAI-compatible inference endpoint; keep deployment coordinates masked |

Secrets may live on the `ai-evaluation` environment or the repository. They are optional only in the reusable workflow's caller declaration so environment-scoped secrets can resolve on the job. The job's configuration check reports every missing value before login or inference; absence never skips or passes the gate. An unconfigured environment is a setup failure, not evidence that the agent passed or failed its questions.

Configure these environment/repository variables:

| Variable                   | Value                                                                                     |
| -------------------------- | ----------------------------------------------------------------------------------------- |
| `EVAL_MODEL`               | Candidate deployment name, matching the intended production model                         |
| `EVAL_REASONING_EFFORT`    | Candidate reasoning effort, also applied to the feature slot after successful evaluation |
| `EVAL_JUDGE_MODEL`         | Deployment for the independent structured-output judge                                    |
| `EVAL_SUBSCRIPTION_IDS`    | Comma-separated IDs for 1-10 approved test subscriptions                                  |
| `EVAL_DATA_CLASSIFICATION` | `synthetic` (answers published) or `internal-test` (verdicts only); never customer data   |
| `EVAL_TOKEN_RESOURCES`     | `azure,graph,loganalytics,storage` as required by the suite; Azure ARM is always included |

Use workload federation with audience `api://AzureADTokenExchange` and the environment-scoped subject `repo:OWNER/REPOSITORY:environment:ai-evaluation`. Repositories that use GitHub's immutable-ID OIDC subject format present `repository_owner_id:OWNER_ID:repository_id:REPOSITORY_ID:environment:ai-evaluation` instead; add a federated credential for the exact subject shown in a failed `AADSTS700213` login. The runner renews OIDC before each case, so long runs do not depend on an expired initial assertion. No client secret is needed. Do not reuse or expand the production deployment identity's privileges.

The evaluation principal needs account-scoped model inference and only the read permissions needed in the isolated test scopes. Provision representative budgets, resources, policies, billing and licensing data independently. Graph reports and billing-account APIs may require additional explicitly approved **read-only** permissions beyond subscription Reader. A token being issued does not prove an API permission. Missing scopes, consent, data access or model deployment fail the gate; they are never silently skipped or presented as empty datasets.

GitHub run summaries/artifacts may be public. Never evaluate against customer subscriptions or real customer directories. Reports redact recognizable credentials, resource IDs, URLs, GUIDs, emails and IPs, but cannot reliably discover arbitrary names or financial details. With `synthetic`, answers and judge rationale are published. With `internal-test` (maintainer-owned test tenants whose resources are not synthetic), the summary, `results.json` and per-case files carry only questions, tool names/outcomes, timings, pass/fail and deterministic failure reasons; answers, error text and judge rationale are withheld, including local runs. Raw per-case captures are staged in a unique ignored directory outside published output; only classification-safe projections reach artifacts. Handled interruption and failure paths clean up that exact private directory. Raw tool payloads and private reasoning are never published.

## Validate a Branch Without Deploying

Manually dispatch the existing feature workflow with `deploy=false` (the default):

```bash
gh workflow run feature.yml --ref YOUR_BRANCH -f deploy=false
```

This runs both regression and live-evaluation gates at that branch revision and skips the deployment job regardless of the verdict. Validation-only dispatches use a separate concurrency group so they cannot cancel a test-slot deployment. A manual deployment requires `deploy=true` and both successful gates. Automatic pushes retain their existing deployment behavior; when publishing a candidate solely for a validation-only dispatch, use a `[skip ci]` commit message to prevent the push-triggered deployment workflow, then run the command above. Never interpret the skipped push run as validation.

After a successful run, the reusable workflow exports `model`, `reasoning_effort`,
`sha` (the full candidate commit), and `endpoint_sha256`. The endpoint hash uses
UTF-8 WHATWG URL serialization with trailing `/` characters removed: scheme and
hostname case and default HTTPS port normalize, while path case and non-default
ports remain significant. Only HTTPS endpoints without credentials, query,
fragment or whitespace are accepted. No raw endpoint is placed in job outputs.

`EVAL_MODEL_ENDPOINT` is a mandatory runtime **secret**, not a variable; there is
no plain-variable fallback. The feature deployment consumes the evaluated model
and effort without substituting defaults and requires the normalized
`AZURE_OPENAI_ENDPOINT` deployment secret to match `endpoint_sha256` before login
or writes. A different inference account requires a new evaluation, not a
silent retarget. See [the shared preview-slot contract](../../CONTRIBUTING.md#branch-naming-convention)
for existing-slot validation, permissions and post-deployment checks.

## Workflow Report

The run summary lists each question, tool count, duration, pass/fail and reason, with expandable tool names and first-token timing; for `synthetic` data it also shows the judge explanation and escaped answer. Long answer previews are shortened only in the summary; the full redacted answer remains in the seven-day `live-ai-evaluations-<sha>-<attempt>` artifact. Structured JSON binds every result to the case ID, exact question, candidate SHA and suite hash. Setup failures and missing results are explicitly failed.

Cases are sequential to avoid parallel tenant-throttled Cost Management queries. Between cases the suite waits `EVAL_CASE_PAUSE_SECONDS` (default 20) or until the latest service `retryAtUtc` the app reported, whichever is later (capped at six minutes). A shared concurrency group prevents simultaneous suites targeting the same fixture environment. Each case has a ten-minute agent budget, five-minute judge budget and an outer process-tree timeout. SIGINT/SIGTERM stop the suite, prevent further cases from starting, and terminate only its owned child process trees. The workflow has a five-hour suite deadline; incomplete suites block deployment. A failed case is re-run only when the app reported a final service throttle (`cooling_down` with `willRetry=false`) during it: it runs once more, unchanged, after the reported deadline, and results publish `attempts` and throttle counts. Any other failure, or a second failure, is final. With `EVAL_FAIL_FAST=true` (set by the feature-branch workflow through the reusable workflow's `fail-fast` input) the suite stops after the first final case failure and reports the skipped remainder as a suite failure; the gate still needs all 20 cases to pass, so a fail-fast run can never be accepted with fewer cases. Production and direct dispatches run every case.

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

Each attempt creates a unique `live-evaluations-*` child directory. It retains the evaluator's **already-redacted failed-case** JSON, including judge rationale, answer/error text and failed-tool details, plus any unfinished `.json.tmp` capture. The private `toolDetails` field retains bounded arguments/results for successful as well as failed tools, with an explicit result-truncation flag. A private `failure` field identifies setup, resource-credential, chat, replay, judge or cleanup failures without discarding the execution already captured. Redaction precedes truncation. An authentication exception after a completed answer must not be rewritten as a zero-duration, zero-tool attempt. Such failures still reject the case; diagnostic retention does not repair credentials or make a failed replay pass.

Passing-case captures are removed. A final `diagnostics.json` records the completed-case count, loaded failed-case results and the host failure, including handled interruption. It does not collect process output, environment values, credentials or CLI token caches. Incomplete captures are diagnostic evidence only, never accepted case results.

Use an access-restricted local folder, not a shared or synchronized directory. New run directories are owner-only on POSIX and inherit the parent directory's access controls on Windows; the final diagnostics file uses owner-only permissions where supported. These files can still contain sensitive tenant names or financial information. **Do not commit, upload, or publish them.** Retained runs are not automatically deleted; review and remove only the diagnostic run directories you own when no longer needed.

Public summaries and artifacts still obey `EVAL_DATA_CLASSIFICATION`: enabling private diagnostics does not publish internal-test answers or rationale. Neither classification publishes raw failed-tool arguments/details, `toolDetails`, `failure` or undeclared private fields. Retention setup/write/cleanup failures visibly fail the run, even if all 20 case verdicts passed. The pinned questions, original rubrics, tool/time limits and unanimous acceptance rule are unchanged; this option adds neither retries nor a diagnostic-subset bypass. With the variable unset or empty, the existing disposable-capture cleanup remains unchanged. Clear the option with `Remove-Item Env:EVAL_PRIVATE_DIAGNOSTICS_DIRECTORY` after local diagnosis.

Fix a failed case by inspecting its source evidence and reproduction, repairing the owning code or contract, and rerunning the full candidate suite. Review intentional rubric changes separately. No automatic code rewrite, permission escalation or rubric relaxation occurs in the deployment workflow.
