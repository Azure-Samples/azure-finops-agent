# Live AI Deployment Gate

Production and test-slot deployment both require the `live-evaluations` job to succeed. Local and CI runs use the same explicitly selected **24 distinct questions**, and all 24 must pass. No percentage threshold, retry-until-green, skip flag, or `continue-on-error` is used. One failed, missing, malformed, wrong-revision or timed-out case blocks deployment. A subset, an arbitrary replacement set of 24, or changed case contracts cannot satisfy the gate.

The live suite calls the model in-process with the **evaluation identity**, so it cannot prove the deployed app's own managed identity, settings or network path. After each deployment, [deployment-smoke.mjs](../../infra/scripts/deployment-smoke.mjs) therefore sends one real anonymous chat turn through the deployed URL and requires a completed answer echoing a per-run verification code. An error event, empty or incomplete answer, or a missing code fails the deployment after three attempts. The workflow then restores the previously recorded image and the three model settings. Published failure reasons redact ids, URLs, hosts and emails.

## Coverage

The full catalog imports every exported sidebar/pricing prompt and every job-template prompt directly from the frontend, deduplicating identical questions and retaining their origins. It adds H200 Spot and English calculation incident cases, the newest-models token-price question, which is evaluation-only since it left the sidebar, and four regressions from the 2026-10-07 audit of real sessions. Those four append a case-specific expectation to the shared rubric, so the judge grades the exact behavior that was fixed. The live gate selects 24 named cases from that catalog rather than running its entire 127-question inventory. These are **curated representative question types**, not a verified ranking of the 24 most-asked production questions. The read-only history analyzer is separate; its output must be authorized, anonymized and reviewed before it becomes regression data.

The selection covers Crawl maturity; current-month cost by service, subscription and resource; forecasts and budgets; Advisor; inventory and tags; Microsoft 365 licenses and Copilot usage; chargeback; VM, storage, database, application and newest-model token pricing; an idle-resource script for review; H200 Spot quota; deterministic English-language arithmetic; and the four audit regressions: a documentation answer from the Microsoft Learn tools (`microsoft_docs_search` required, and scored on efficiency like every case, so a duplicate web search fails it), a script that keeps every value the user stated (`GenerateScript` required), an omitted Key Vault `enablePurgeProtection` read as off rather than unknown, and an honest Intune access limit that offers an administrator script instead of a dead end. Questions needing missing attachments or a prior turn are not selected. The broader catalog remains available for coverage checks; backend, Python, frontend unit and browser regressions are not reduced.

Each case uses a fresh process and synthetic conversation, the candidate's real `ChatEndpoints`, `AgentSessionFactory`, protected tools and Agent Framework runtime, live Azure APIs, and real model inference. SSE supplies tool outcomes and latency; the owner-checked transcript endpoint must replay the exact decoded question and every completed answer message, not merely contain the question. A separate strict-schema model call judges the final answer against the question and tool evidence. Deterministic failures override the judge. Malformed judge output retains the execution diagnostics and fails the case. The suite continues recording other cases after an answer fails, but never deploys that candidate.

The evaluator shares the application's reported-tool-failure classifier rather than treating function-call completion as success. Empty payloads and explicit failure/cancellation in nested evidence fail deterministically. Unknown, partial or awaiting-approval evidence is not automatically a tool execution failure: the judge must still establish whether the answer satisfies the requested task and accurately states those limitations.

A well-formed negative judge verdict is reported as a rejected answer, not missing or malformed output. Every judge flag must still be a boolean, `accepted`, `grounded` and `complete` must all be true, `avoidableCalls` and `avoidableRounds` must be integers from 0 to the session's tool-call count, `avoidableSeconds` must be a finite non-negative number, the host-computed efficiency score must be at least 3, and the reason must be nonempty to pass. This diagnostic distinction does not relax acceptance or publish private judge rationale.

The judge grades materiality, not polish. An answer that is correct in substance and delivers every core requested result and rubric requirement is accepted even when its wording, ordering, formatting or depth could be better, a secondary detail or optional caveat is missing, or figures are rounded to their displayed precision. `complete` is false only when a core requested result or rubric requirement is missing, and `grounded` is false only when a material claim the user would act on (a figure, resource, price, saving, recommendation or conclusion) is wrong or unsupported. The efficiency floor and the case time and tool budgets are unchanged, so a correct but slow session still fails.

The judge receives the whole end-to-end session, not only the answer: the agent model and reasoning effort, total and first-token seconds, tool calls against the case's tool and duration budgets, failed calls, sequential rounds (groups of overlapping calls), peak concurrency, tool wall time versus model time, service throttle notices, and every call in start order with its round, start/end offsets, duration, host-classified success, arguments and result. Using its knowledge of the Azure, Microsoft Graph and Log Analytics APIs, it decides what an expert would call for the exact question and counts the avoidable calls, sequential rounds and seconds, naming each avoidable call by tool order. Clear waste counts: a third failed or rejected call or one left uncorrected, repeated requests (including a failed request resent unchanged), independent reads split into sequential rounds instead of one round of parallel calls, paging raw data when the API can filter, group or aggregate server-side, more than one documentation or specification lookup for the same stable API or command (a search plus reading one of its results is one lookup), or unrelated reads. The agent is meant to research and self-correct, so up to two failed calls it corrects with later successful calls to the same tool, and one extra call to learn what to do (documentation, specification, api-version, schema or provider lookup), are not waste; the deterministic gate likewise allows two corrected failures and rejects a third or uncorrected one, and the judge still rejects an answer that rests on failed evidence. Sequential Cost Management query/forecast calls (host-serialized), waits after throttle notices, `QueryAzure` calls with `query` and no `url` (local calculations that never call the service), one repeat of a request with `query` after it returned only its schema (the host stores nothing between calls, so that repeat is how a large response is cropped), structurally filtered Retail Prices sets narrowed locally, required verification reads and configured reasoning time are not waste either. The judge reports counts rather than a score, because a model-chosen score drifted from its own anchors (run 36475817429 scored three redundant documentation lookups, about 30 of 162 seconds, as 2). The host derives the 1-5 score deterministically: 5 with no waste; 4 with one or two avoidable calls or rounds; 3 with more waste that still leaves the session reasonably fast; 2 when the waste roughly doubled the session (avoidable calls or seconds at least 0.75 times the necessary remainder); 1 when it at least tripled it (twice the remainder). A single avoidable call or round that added under a tenth of the elapsed time stays 4: one hosted web search inside a model response doubles a one-call answer's call count without making the user wait (run 36915469250 failed an accepted, grounded and complete Azure OpenAI price comparison at 2 for one 1.36-second search). A score of 3 or higher passes. Efficiency never compensates for a wrong or incomplete answer. Published results add per-tool durations, the round/concurrency/tool-versus-model timeline, the computed efficiency flag and score, and the three waste counts; the rationale stays withheld for `internal-test` data.

The judge compares each requested metric across the whole answer and visible outputs, including separately labelled columns, without silently correcting an answer's values from the source. Inclusive/exclusive date conversions must denote the same interval. Graph [enabled prepaid units](https://learn.microsoft.com/graph/api/resources/licenseunitsdetail?view=graph-rest-1.0) are inventory, not invoice-confirmed paid quantities; assignments are not activity. Available inventory counts remain required even when purchase quantities or contract costs are unknown. Conversely, supplied billing quantities/rates cannot be omitted as unknown, and an unavailable required activity report still leaves that task incomplete. The one maintainer-approved exception is zero seats: when the product's report was requested and is unavailable, and inventory shows zero enabled and zero assigned seats, the per-seat answers (0 of 0 active or inactive, no licensed non-users, no inactive-seat waste) are determinate, provided the answer attributes them to zero seats, discloses the unavailable report, leaves unlicensed-user activity unknown and claims no price or spend. Apart from that exception, these evidence distinctions do not change the case questions, rubrics, required reads or acceptance checks.

The test-only in-memory host supplies tokens from the dedicated CI identity; it is **not** a deployed-browser OAuth or delegated-consent test. No evaluation authentication endpoint is added to the production app. Job-template cases test chat/tool routing, not timer execution. Follow-up/upload templates that lack an actual prior conversation or fixture can only test honest clarification, not execution of the missing scenario. Image startup, browser UI and delegated sign-in need their separate regression coverage.

## Required GitHub Configuration

Create a protected GitHub environment named `ai-evaluation`, limited to trusted deployment branches. Configure these repository or environment secrets without printing their values:

| Secret                       | Purpose                                   |
| ---------------------------- | ----------------------------------------- |
| `AZURE_EVAL_CLIENT_ID`       | Dedicated evaluation application identity |
| `AZURE_EVAL_TENANT_ID`       | Evaluation tenant                         |
| `AZURE_EVAL_SUBSCRIPTION_ID` | Azure CLI default evaluation subscription |
| `EVAL_MODEL_ENDPOINT`       | Foundry project endpoint (`https://{account}.services.ai.azure.com/api/projects/{project}`); keep coordinates masked |

Secrets may live on the `ai-evaluation` environment or the repository. They are optional only in the reusable workflow's caller declaration so environment-scoped secrets can resolve on the job. The job's configuration check reports every missing value before login or inference; absence never skips or passes the gate. An unconfigured environment is a setup failure, not evidence that the agent passed or failed its questions.

Configure these environment/repository variables:

| Variable                   | Value                                                                                     |
| -------------------------- | ----------------------------------------------------------------------------------------- |
| `EVAL_MODEL`               | Candidate deployment name, matching the intended production model                         |
| `EVAL_REASONING_EFFORT`    | Candidate reasoning effort, also applied to the feature slot after successful evaluation |
| `EVAL_WEB_SEARCH`          | Optional `true`/`false` for the model's hosted web search; defaults to the app default (on), which is what deploys |
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

Cases run in three concurrent lanes so the suite does not wait on one case at a time, while never issuing parallel tenant-throttled Cost Management queries itself. Every case that may query Cost Management shares one sequential lane; only curated cases that never need it (Advisor, Resource Graph, Graph and quota reads in one lane; public pricing and calculation in another) run beside it, and any unlisted case defaults to the Cost Management lane. Within each lane the suite waits `EVAL_CASE_PAUSE_SECONDS` (default 20) between cases, or until the latest service `retryAtUtc` any case reported, whichever is later (capped at six minutes). In CI each lane renews its OIDC login into its own temporary Azure CLI profile, removed when the suite ends, so one lane's `az login` never rewrites the profile another lane's running case reads. A shared concurrency group prevents simultaneous suites targeting the same fixture environment. Each case has a ten-minute agent budget, five-minute judge budget and an outer process-tree timeout. SIGINT/SIGTERM or a broken lane stop the suite, prevent further cases from starting in any lane, and terminate only its owned child process trees. The workflow has a five-hour suite deadline; incomplete suites block deployment. A failed case is re-run only when the app reported a final service throttle (`cooling_down` with `willRetry=false`) during it: it runs once more, unchanged, after the reported deadline and at least one full minute after the refusal, since a retry-after covers only the exhausted window of Cost Management's per-tenant quotas (12 QPU per 10 seconds, 60 per minute, 600 per hour). Results publish `attempts` and throttle counts. Any other failure, or a second failure, is final. With `EVAL_FAIL_FAST=true` (set by the feature-branch workflow through the reusable workflow's `fail-fast` input) no lane starts another case after the first final case failure, and the skipped remainder is reported as a suite failure; the gate still needs all 24 cases to pass, so a fail-fast run can never be accepted with fewer cases. Production and direct dispatches run every case. Both deployment workflows start the live suite alongside the credential-free regressions rather than after them; deployment still requires both to succeed.

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

Public summaries and artifacts still obey `EVAL_DATA_CLASSIFICATION`: enabling private diagnostics does not publish internal-test answers or rationale. Neither classification publishes raw failed-tool arguments/details, `toolDetails`, `failure` or undeclared private fields. Retention setup/write/cleanup failures visibly fail the run, even if all 24 case verdicts passed. The pinned questions, original rubrics, tool/time limits and unanimous acceptance rule are unchanged; this option adds neither retries nor a diagnostic-subset bypass. With the variable unset or empty, the existing disposable-capture cleanup remains unchanged. Clear the option with `Remove-Item Env:EVAL_PRIVATE_DIAGNOSTICS_DIRECTORY` after local diagnosis.

### Encrypted CI diagnostics

CI withholds internal-test answers and judge rationale from its public artifacts. To learn why a CI case failed without reproducing it, a maintainer can have the suite encrypt the same failed-case records the local option retains, including judge rationale, answer, gate failures, bounded tool details and any unfinished capture, to a certificate whose private key only they hold. The records exist only in the runner's disposable capture directory until they are encrypted; the plaintext is never written to the output. The artifact gains one `private-diagnostics.enc.json` when a case or the suite fails. It uses RSA-OAEP-256 to wrap a random AES-256-GCM key, so the file is unreadable and tamper-evident without the private key. Passing runs write no file.

On Windows with PowerShell 7, create the certificate once. Its RSA 4096 private key is non-exportable in `Cert:\CurrentUser\My`. Then store the public PEM as the repository variable (not a secret):

```powershell
./tests/LiveEvaluations/New-DiagnosticsCertificate.ps1 | gh variable set EVAL_DIAGNOSTICS_CERT
```

After a failed run, download and decrypt its diagnostics in the same Windows profile:

```powershell
./tests/LiveEvaluations/Unprotect-Diagnostics.ps1 -RunId <run-id>
```

The script prints each failed case's verdict, judge rationale, gate failures and answer, and removes the downloaded artifacts. `-OutFile` saves the full decrypted JSON; treat it like local private diagnostics. A repository variable is not inherited by forks, so a fork running its own tenant never encrypts to the upstream maintainer's key. The suite rejects a private key, a non-RSA key or one below 3072 bits before any case starts. With the variable unset, nothing changes. Losing the certificate only makes older artifacts unreadable; create a new one and replace the variable.

Fix a failed case by inspecting its source evidence and reproduction, repairing the owning code or contract, and rerunning the full candidate suite. Review intentional rubric changes separately. No automatic code rewrite, permission escalation or rubric relaxation occurs in the deployment workflow.
