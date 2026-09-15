# Agent Reliability Contracts And Verification

This change set addresses the twenty findings from the conversation and telemetry review. Execution checks are not a blanket claim that every future model answer is correct. Tests use synthetic data unless explicitly described as a live local-model workflow.

## Backlog Coverage

| Finding | Implemented control | Regression coverage |
| --- | --- | --- |
| FIN-01: unrestricted execution | Custom-only runtime policy on create/resume, per-invocation admission, non-root image | Permission tests and real Windows/Linux CLI protocol |
| FIN-02: credential exposure | Pre-dispatch screening, structured return/SSE redaction, content capture disabled | Credential/redaction tests; historical cleanup remains operator-owned |
| FIN-03: ownerless downloads | Explicit owner-bound persistent artifact registry and fail-closed endpoints | Owner/missing/expired/restart tests |
| FIN-04: short requests lose tools | Only standalone greetings take the no-tool path | Routing tests and live short XLSX follow-up |
| FIN-05: scheduled idle equals success | Structured host-validated scoped evidence, repeated-failure/goal pause, context compaction | Freshness/scope/outcome tests and real CLI compaction |
| FIN-06: work continues after timeout | Host cancellation, admitted tool leases, terminal confirmation and quarantine | Actual CLI abort and gate-race tests |
| FIN-07: throttling and stale reuse | Tenant serialization, credential/request cache, full retry deadline, sticky turn block | Cooldown/cache/isolation tests |
| FIN-08: invented file downloads | Real bounded CSV/XLSX/HTML exporters and registered downloads | Python render tests, browser cards, live XLSX bytes/cells |
| FIN-09: missing transcript recovery | Cached/uncached paths share recovery; unavailable history has an explicit error | Recovery and cancellation tests |
| FIN-10: selector/path conflict | `jsonPath` is separate from host-owned filesystem `path` | Host-key and Python containment tests |
| FIN-11: incomplete file aggregation | Structured filters, multi-column groups, aggregates, sorting and coverage totals | Python and host serialization tests; live filtered CSV totals |
| FIN-12: volatile uploads | Persistent conversation binding, hashes, expiry and reader leases; reset preserves files | Restart/isolation/expiry tests and desktop/mobile reset |
| FIN-13: unsupported feasibility claims | Separate quota, SKU/zone restrictions, placement likelihood and VM-origin connectivity | Diagnostic parsing/coverage tests; no allocation guarantee |
| FIN-14: discarded bulk results | Indexed per-item bodies, cancellation/unattempted counts, explicit pending/partial state | Bulk GET/operation/cancellation tests |
| FIN-15: lost pricing variants | Exact SKU-field fallback, meter/region projection, resolution/coverage metadata | Synthetic pagination, vocabulary and region tests |
| FIN-16: fabricated zero rates | Deterministic validated arithmetic with explicit assumptions and rate provenance | Missing/nonfinite/locale/currency tests and live synthetic estimate |
| FIN-17: accepted writes called complete | Persist intent, exact approval, async polling and prerequisite history; suppress unknown duplicates | Approval/fingerprint/async/restart tests |
| FIN-18: wrong reconnect guidance | Resource-specific structured consent actions with same-origin allowlist | Consent-contract and desktop/mobile URL tests |
| FIN-19: unknown becomes zero | Nullable unknown/N/A scores and observed-only averages; no empty-RG billable-waste penalty | Maturity/control tests and UI build checks |
| FIN-20: missing fulfillment diagnostics | Durable redacted outcomes, interrupted-run reconciliation, corrected host sampling, one browser exception path | Outcome and browser telemetry tests; original notification crash not reproduced |

## Source And Execution Semantics

- A budget `currentSpend` value is a periodically evaluated snapshot. Retrieval time is not billing data time. Preserve `_finops` and aggregate `sourceEvidence`; cached results during cooldown are not fresh measurements.
- Pricing `RESOLUTION` distinguishes exact, ambiguous, partial and missing coverage. Missing rates remain unknown. A calculator result is an estimate, not a measured bill or proof of a commercial rate.
- Quota headroom and SKU availability are necessary evidence, not guaranteed capacity. Effective inherited Azure Policy and successful allocation are not inferred. Connectivity is probed from a specified existing Azure VM, not the app host.
- ARM PUT/PATCH first returns an approval proposal. Approve requires the exact stored request and explicit cost acknowledgement. HTTP 202, in-progress and unknown outcomes require polling, not a retry of the write. Background jobs cannot purchase capacity without the same approval.
- `ReportJobOutcome` is checked against host-observed source calls. A later read replaces earlier evidence only for identical canonical arguments. Unknown/partial scope must be reported as such. This does not independently prove that a model chose every scope implied by an arbitrary natural-language request.
- Durable turn `status` describes execution. Normal chat `fulfillment` is `not_evaluated`; scheduled fulfillment stores the validated run outcome. Neither SDK idle nor HTTP 200 proves the user's business objective was met.

## Persistence And Retention

All registries live under host-configured `COPILOT_HOME`; filesystem paths are not accepted from model arguments.

| Data | Retention |
| --- | --- |
| Pending uploads | 30 minutes |
| Bound uploads | 24-hour sliding expiry, maximum seven days from creation; active reads hold leases |
| Removed uploads/native-image handoff | Tombstoned for 30 minutes to allow an outstanding read |
| Generated artifacts | 24 hours, exact owner check; expired/ownerless payloads swept |
| Unapproved write proposals | 30 minutes, then payload removed and state marked expired |
| Terminal operation records | Seven days after last update |
| Unresolved operations | Retained for reconciliation and duplicate-write protection; not silently expired |
| Turn outcomes | 30 days; interrupted running records reconciled on startup |
| Conversation history | Existing explicit-delete/30-day idle policy |

Use a single active application instance. Per-session admission, cooldowns, upload leases and approval locks are process-local. A shared persistent volume is not a distributed lock service. Anonymous continuity still depends on retaining the browser's application identity.

## Verification Gates

The implementation passed 128 backend tests, 12 Python tests, four frontend unit tests and ten desktop/mobile browser cases locally. Linux ACR validation ran the suites, built the complete image and verified non-root storage access, persisted-artifact cold start, CLI startup, graceful shutdown, offline telemetry behavior, and delivery of a host request burst plus an OTLP span to a local ingestion receiver. No image was published by these validation runs.

The permanent suites are under `tests/Dashboard.Tests`, `tests/test_*.py`, `tests/frontend`, and `src/Dashboard/frontend/tests`. See [contributor instructions](../CONTRIBUTING.md#regression-tests) for commands.

The real CLI suite uses a local synthetic Responses API and exercises native images, tool metadata, cancellation, retained history, compaction and resume. The build-only `tests/linux-validation.yml` ACR task has no image-push or deployment step. The shared GitHub validation workflow additionally runs rendered desktop/mobile tests before either deployment workflow receives deployment credentials.

Local-model acceptance scenarios used only synthetic inputs: a USD 7.40 token estimate; four CSV rows filtered to three AI rows with January/February totals of 30 each; and a short request producing an actual XLSX with those values. Downloads were checked as bytes and workbook cells, not just links.

A live HTML report retained both requested rows, downloaded successfully and rendered in an isolated preview. Restart testing caught and fixed artifact-registry static initialization with existing files. Stop released the server gate and a subsequent greeting completed; the stopped and completed outcomes were persisted. One Linux runtime completion timeout did not reproduce on rerun; the test remains enabled with provider/event diagnostics and its original timeout.

## Remaining Operational Verification

- Collector 0.160.0 retains one fixable high finding, `CVE-2026-79921` in `github.com/rabbitmq/amqp091-go` 1.12.0. The app config enables only OTLP receivers and Azure Monitor export, not AMQP/RabbitMQ. Keep this as a dependency follow-up; do not describe the entire image as vulnerability-free. The runtime scan found no critical findings; CI blocks fixable critical findings. The editor also reports vulnerabilities in the separate Node build-stage image, which is not shipped in the final runtime.
- A fresh authenticated consent flow needs the deployment owner's shared signed-in tab and selected test tenant. Automated browser fixtures do not prove that external consent was granted.
- No billable ARM mutation was used as a test. Real allocation, inherited policy, managed-identity behavior and resource-specific role failures require controlled deployment checks.
- The historical browser notification-manager exception did not reproduce in the installed SDK callback tests or built browser bundle. Redaction, duplicate-capture prevention and failure containment are implemented; a causal upstream fix is not claimed.
- Credential detection is heuristic. Historical transcript/upload cleanup and rotation of previously disclosed credentials are separate operator actions, not accomplished by this code change.