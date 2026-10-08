# Agent Reliability Contracts

These are the execution, evidence, retention and verification contracts the host enforces today. Change history, including how each contract was found, is in [CHANGELOG.md](../CHANGELOG.md). Passing checks do not claim that every future model answer is correct.

## Turn Execution

- One Agent Framework agent serves every conversation. Each turn receives the owner's tool instances, built from that user's tokens, so ownership is bound in the tools rather than checked by a wrapper. Agent Framework's `FunctionInvokingChatClient` invokes them and runs independent calls concurrently.
- One turn runs per conversation. Stop cancels the run's token, and the turn gate stays held until the run reports a terminal state. A browser disconnect does not cancel the turn; the answer is persisted and the browser reconciles against the server gate and transcript.
- The response chain advances only after a successful run, so a stopped or failed turn leaves the previous response ID and no unanswered tool calls. An expired chain (`previous response` 400/404) resets to a fresh model context while the visible transcript remains.
- A long chain is compacted: when a completed turn's largest model input exceeds 100k tokens, the next turn starts a fresh model context with a bounded recap of the visible questions and answers (tool results are left out, so the model queries again for figures the answers do not show), the files earlier turns generated or the user attached, and the code of the latest generated script. Every fresh context receives the connection and uploaded-file context again. In production, calls above about 180k input tokens waited 10-17 s for their first token, against about 1 s below 100k.
- Tool errors reach the model with their message so it can correct the call. A tool result that reports `Error:` is a failure even when the function call itself returned.
- The SDK retries HTTP 408/429/5xx with backoff. A model rate limit that still fails the run is shown as a plain busy notice, never the provider text naming the deployment, region and quota.
- A truly empty terminal turn emits a recoverable `empty_result` error. Explicit Stop and structured chart, score or artifact output are not empty results.

## Cost Management Throttling

- Cost Management `/query` and `/forecast` are serialized per tenant. The host honors the longest standard, Cost Management or Consumption retry header, waits 60 seconds when none is given, and retries once when the service delay is at most five minutes; longer waits are returned intact.
- After a final 429 the turn makes no further Cost Management calls, even after the deadline passes. New turns can wait out an existing short cooldown.
- Cooldown SSE events carry `retryAtUtc` and `willRetry`. The UI shows waiting and exhausted states in the chat and the execution sidebar and labels a terminal 429 as throttled, never as success.
- Cached responses are bound to the credential and the canonical request, are annotated as cached, and are never presented as freshly measured billing data.
- More than two grouping dimensions are rejected before dispatch.

## Source And Evidence Semantics

- A budget `currentSpend` value is a periodically evaluated snapshot. Retrieval time is not billing data time. Preserve `_finops` and `sourceEvidence`.
- Retail pricing results are raw Retail Prices API rows with `retrievedAtUtc`, `pages` and `complete`. Missing rates stay unknown, `complete=false` means partial catalogue coverage, and `query` arithmetic is an estimate, not a bill or a commercial rate.
- Missing access (401/403), failed reads (400, a 404 for a wrong path or type) and unqueried scopes are unknown, never zero. A successful read is evidence: an optional property it omits is not set (its documented default), and a 404 whose error code names the missing item (not a bare NotFound) shows it is absent.
- Quota headroom and SKU availability are necessary evidence, not guaranteed capacity. Missing Spot eviction history is unknown, not zero risk. A past successful deployment does not guarantee a new allocation, and a current restriction does not invalidate historical success. Placement results with missing or `DataNotFound` scores keep coverage partial.
- Effective Azure Policy enforcement is not inferred from assignment names or empty parameter searches.
- Connectivity is probed from an existing Azure VM through Network Watcher, never from the app host.
- Durable turn `status` describes execution. Normal chat `fulfillment` is `not_evaluated`; scheduled runs store the validated `ReportJobOutcome`. Neither an idle turn nor HTTP 200 proves the user's objective was met.

## Writes And Approvals

ARM PUT/PATCH runs only through `ApplyAzureChange`, which Agent Framework holds for approval. The UI shows the exact method, URL and body, and only an explicit approval of that request on the next turn runs it; any other message rejects every held change, and a stale approval is refused before dispatch. HTTP 201/202 is accepted, not complete: the model polls the returned async URL. Unknown writes are never retried blindly, and scheduled jobs cannot purchase capacity without the same approval. `DELETE` is never sent.

## Persistence And Retention

All state lives under host-configured `AGENT_HOME`; filesystem paths are never accepted from model arguments.

| Data | Retention |
| --- | --- |
| Pending uploads | 30 minutes |
| Bound uploads | 24-hour sliding expiry, maximum seven days from creation; active reads hold leases |
| Removed uploads | Tombstoned for 30 minutes to allow an outstanding read |
| Generated artifacts | 24 hours, exact owner check; expired and ownerless payloads are swept |
| Pending change approvals | In the conversation's `session.json` until the next turn answers them |
| Tool responses | Not stored; cropped in memory within the turn (the cost cache above is the only exception) |
| Turn outcomes | 30 days; interrupted running records are reconciled on startup |
| Conversation history | Until explicit delete or 30 days without changes; the model's stored responses are retained by the service for about 30 days |

Run one active application instance. Turn gates, cooldowns and upload leases are process-local, and a shared persistent volume is not a distributed lock service. Anonymous continuity depends on the browser keeping its application identity.

## Verification Gates

- `validate.yml` runs credential-free backend, Python, frontend unit and rendered desktop/mobile regressions, builds the Linux image, checks non-root startup and offline telemetry shutdown, and rejects fixable critical runtime vulnerabilities.
- `live-evaluations.yml` runs the same 23 representative questions against real inference and Azure tools, with a separate judge. All 23 must pass before either deployment workflow can deploy. See [tests/LiveEvaluations/README.md](../tests/LiveEvaluations/README.md).
- After deploying, both workflows send a real anonymous chat through the deployed URL with the deployed identity. On failure or cancellation they restore the previous image and model settings.

See [contributor instructions](../CONTRIBUTING.md#regression-tests) for local commands.

## Resolved Findings

Each of these has a regression test or an enforced contract; do not report them as new defects without reproducing them on the current build.

- A complete assistant `message` replaced by an earlier partial stream, and terminal 429s shown as success.
- GPU SKUs missed because large Compute SKU catalogues were truncated before filtering, and regional records collapsed into one.
- Unknown or partial availability reported as "unavailable everywhere".
- Ownerless or expired artifact downloads, volatile uploads, and file queries that let a model argument choose a host path.
- Short follow-up requests routed to a tool-less reply.
- Duplicate exception telemetry for handled transcript faults.
- Unsupported Microsoft Graph query options and stale Copilot report routes.
- Mental arithmetic, mixed currencies and mixed report cohorts in answers; totals, shares and rankings now come from `query`.

## Known Limitations

- An upstream content filter rejected one benign Spot-resilience wording (HTTP 400 `content_filter`, medium hate). Application instructions cannot override an upstream rejection; no filter was relaxed and no automatic moderation retry exists.
- The live gate fails closed when the model endpoint is unavailable to the evaluation identity. On 2026-09-29 the Sweden Central Foundry endpoint returned HTTP 500 `Unable to get resource information` and 408 timeouts to callers outside Azure for several hours while the app's managed identity kept working; no deployment happened until it recovered.
- A fresh consent flow needs a signed-in browser in a test tenant; automated fixtures do not prove external consent.
- No billable ARM mutation is used as a test. Real allocation, inherited policy and role-specific failures need controlled deployment checks.
- Recognizing and refusing credentials is left to the model; users must not paste secrets. Rotating previously disclosed credentials is an operator action.
- Hosted web search duplicates documentation reads on gpt-6.1-sol. In 8 local runs on 2026-10-08 it web-searched a Key Vault documentation question every time, and half of those answers never called Microsoft Learn, although the prompt forbids web-searching documentation. Earlier runs showed `site:learn.microsoft.com` searches after a Learn read; asking for `microsoft_docs_fetch` instead stopped web-search page opens of Learn pages, not the searches, and a note in the Learn result did not help either (2 of 6 runs still searched). gpt-6-luna, which Microsoft lists with the same built-in tools, used only `microsoft_docs_search` in 4 of 4 runs with the same prompt and tools. The web search tool's documented filters limit which domains a search returns, not whether the model searches, and host code that withheld the tool was tried and removed (maintainer decision: use the platform's tool as is). Web search stays on by maintainer decision, so the documentation regression case stays in the evaluation catalog but out of the live gate, where the judge rightly scores the extra search as waste. In 30 local runs on 2026-10-07, Responses `text.verbosity=low` made no measurable difference (110 against 111 words on average), so it is not sent.
