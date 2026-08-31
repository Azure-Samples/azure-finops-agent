---
mode: agent
description: "Exhaustive Playwright regression of the Azure FinOps Agent — every endpoint, tool, SSE event, session/stop/recovery path, jobs lifecycle, upload, download, security gate and latency budget. Encodes the browser gotchas that make this app hostile to naive automation."
---

# Extensive UI Browser Test

Drive the **whole application** with the VS Code integrated browser tools, measure response times, and fix what is broken. Read `/memories/repo/finops-agent-debugging.md` first — it is the running catalog of past bugs and test gotchas.

Default target is **production** (`https://azure-finops-agent.com`). If I say "local", follow `debug-local.prompt.md` first.

**Ground rules**

- Never report a scenario as passing on a measurement you know is unreliable. Re-measure with an assertion that cannot be faked by a timing artifact.
- If you were wrong earlier in the run, say so plainly and correct the record.
- Fix what you find, rebuild, redeploy if needed, then **re-run the affected scenario** — a fix is not done until it is re-tested.
- Never touch credential fields. For anything needing sign-in, hand back to me.

## 0. Preflight — do not skip

1. **Load the browser tools.** Two `tool_search` calls: `"playwright browser navigate screenshot read page type"` and `"open browser page url click element handle dialog"`. If either returns nothing, STOP and say the test must run in VS Code Insiders. Never fall back to a system browser, never ask me to click through it manually.
2. **Confirm what is deployed.** `GET /api/version` → `sha`, `build`, `started`. After a deploy the container takes ~60–90s to roll over and **testing before rollover silently tests the OLD build**. Cross-check against the log, which is authoritative:
   ```
   AppTraces | where TimeGenerated > ago(30m)
   | where Message has "Application started" or Message has "Application is shutting down"
   | project TimeGenerated, Message | order by TimeGenerated desc
   ```
   (`started` was a per-request `DateTime.UtcNow` until 2026-08-31 — if it ever tracks "now" again, that regression is back.)
3. **Record the baseline**: `git log --oneline -5`, plus which commits are built-but-undeployed. State this in the final report.
4. **If local**: `wwwroot/` must exist BEFORE `dotnet run` — ASP.NET caches `WebRootPath` at startup, so starting without it 404s every asset for the life of the process (`The WebRootPath was not found`). `azd deploy` prunes `obj/`, `wwwroot/` and `frontend/node_modules`, so after a deploy: re-seed the Copilot CLI cache, `npm ci`, `npm run build`, THEN start.

## 1. Browser gotchas — this app breaks naive Playwright

All confirmed on this app. The naive approach costs a cycle every time.

| Symptom | Cause | Do this instead |
| --- | --- | --- |
| `locator.click` times out "waiting for element to be stable" | infinite CSS animations | `page.evaluate(() => [...document.querySelectorAll('button')].find(b => /Label/.test(b.textContent)).click())` |
| `type_in_page` / `fill` fails "element is not visible" | `textarea.input-field` has `offsetWidth 0` | native setter + events (below) |
| Clicking "the last enabled button" sends a canned prompt | that is the **Script** button | select Stop by **`.action-btn--stop`** only |
| `page.context().clearCookies()` → "Method not found" | unsupported in integrated browser | expire per-name via `document.cookie` |
| `Illegal invocation` wrapping fetch | unbound native | `const of = window.fetch.bind(window)` |
| ResizeObserver / rAF / smooth scroll never fire | integrated browser reports `document.hidden === true` always | rely on reactive watchers; gate smooth scroll on `visibilityState` |
| Turn "completes" in 3s | you sent while warmup was in flight | wait for `composer enabled && !stopBtn` before every send |
| `Execution context was destroyed` | a navigation (often my sign-in) landed mid-evaluate | re-open the page, re-install instrumentation, resume |
| Page id "not found" | the page was closed | `open_browser_page` again; instrumentation does NOT survive |

Canonical helpers — reuse verbatim:

```js
const el = () => document.querySelector('textarea.input-field');
const stopBtn = () => document.querySelector('.action-btn--stop');
const txt = () => document.querySelector('.messages')?.innerText || '';
const wait = async (fn, ms) => { const t = Date.now(); while (Date.now() - t < ms) { if (fn()) return true; await new Promise(r => setTimeout(r, 200)); } return false; };
const send = async (p) => {
  await wait(() => el() && !el().disabled && !stopBtn(), 90000);   // never send into a busy composer
  const e = el();
  Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(e, p);
  e.dispatchEvent(new Event('input', { bubbles: true }));          // syncs Vue v-model
  e.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true, cancelable: true }));
  return wait(() => !!stopBtn(), 40000);
};
```

## 2. Measure from the SSE, never from pixels

Rendered text lags the stream by the typing animation, and `.messages` innerText baselines are unreliable on the first turn (hero → messages swap) — that artifact has produced false `answered: false` readings. Tee the app's own stream:

```js
const of = window.fetch.bind(window);
window.__ev = [];
window.fetch = async (...a) => {
  const url = typeof a[0] === 'string' ? a[0] : a[0]?.url;
  const r = await of(...a);
  if (url?.includes('/api/chat') && !url.includes('warmup') && r.body) {
    const [x, y] = r.body.tee();
    (async () => { const rd = y.getReader(), d = new TextDecoder(); let buf = '';
      while (1) { const { done, value } = await rd.read(); if (done) break; buf += d.decode(value, { stream: true }); let i;
        while ((i = buf.indexOf('\n\n')) >= 0) { const line = buf.slice(0, i).replace(/^data:\s*/, ''); buf = buf.slice(i + 2);
          if (!line) continue; if (line === '[DONE]') { window.__ev.push({ t: Date.now(), type: 'DONE' }); continue; }
          try { const j = JSON.parse(line); window.__ev.push({ t: Date.now(), type: j.type, tool: j.tool }); } catch {} } } })();
    return new Response(x, { status: r.status, statusText: r.statusText, headers: r.headers });
  }
  return r;
};
```

Per turn record: **time to first `delta`** (true TTFT), time to `[DONE]`, ordered **tool sequence**, tool count. The tool sequence explains slowness — each tool call is a full model round-trip (~2–5s) while the tools themselves usually execute in <2s.

**Every SSE type must be observed at least once across the run**, and none may reach the UI unhandled:
`session`, `session_title`, `timing`, `reasoning`, `message`, `delta`, `tool_start`, `tool_done`, `chart`, `maturity_score`, `html_ready`, `script_ready`, `cooling_down`, `busy`, `error`.

## 3. Latency budget

Assert against these. Anything over budget must be explained by its tool sequence, not hand-waved.

| Class | Example | Budget (TTFT) |
| --- | --- | --- |
| Trivial | `hi`, `thanks` | ≤ 3s, **0 tools** |
| Single-fact public | one SKU in one region | ≤ 8s |
| Multi-region compare | 3 regions | ≤ 20s, **exactly 1** `GetAzureRetailPricing` |
| Tenant query | MTD spend | ≤ 25s |
| Deck / script generation | — | ≤ 60s |
| Maturity score | Crawl | ≤ 120s |

Cross-check the run against production truth:
```
AppRequests | where TimeGenerated > ago(2h) and Name has "/api/chat"
| summarize n=count(), p50=percentile(DurationMs,50), p95=percentile(DurationMs,95), maxMs=max(DurationMs)
```

## 4. Anonymous surfaces

Install `page.on('console'|'pageerror'|'requestfailed')` capture first. Assert **zero** console errors, page errors and non-bot failed requests for the whole run (404s on `*.php`, `/wp-admin/*`, `/.env` are bot scans — ignore).

1. **Landing** — hero, tagline, six feature cards, left rail (`Pricing & Estimates` + Crawl/Walk/Run), composer with Clear/Presentation/Script **disabled** and send disabled.
2. **Trivial turns** — `hi`, `hello`, `thanks`, `ok`. ONE round-trip: no tools, no tables, no bullet lists, one sentence. Regression here means `TrivialTurnDirective` stopped applying.
3. **All 14 starter prompts** in `Pricing & Estimates` — click each, assert an answer commits and no console error. These are the primary entry point.
4. **Follow-up chips** — click the offered chip; must run in the SAME conversation.
5. **Prompt chips** (`.prompt-chip`) inside answers route correctly.
6. **Multi-region** — "Compare X in A, B and C" → **exactly 1** `GetAzureRetailPricing`.
7. **Arithmetic** — TCO with known inputs; verify independently (rate × hours × N). A wrong total is a real bug.
8. **Foundry pricing trap** — ask for GPT model pricing; the answer must quote Standard+Global, not the minimum row across Batch/DataZone/cached variants.
9. **Service health** — `GetAzureServiceHealth` (no auth) returns and renders.
10. **Charts** — bar, line, pie and a world map all render; resize and confirm they re-layout.
11. **Composer actions** — Attach, Clear, Presentation, Script enable/disable correctly; Clear empties the view.
12. **Uploads** — drag-drop a CSV and an XLSX (`onDrop`), paste an image (`ClipboardEvent` + `DataTransfer`), then ask about each. Data files must route to `QueryUploadedFile`; images go to vision. `GET /api/uploads` lists, `DELETE /api/uploads/{fileId}` removes.
13. **Downloads** — generate a script and a deck. Card renders, preview shows, `/api/download/script/{id}` and `/api/download/html/{id}` return **200**. A **fabricated** fileId must return 404/403, never 200.
14. **SEO** — `/faq`, `/faq/{slug}`, `/sitemap.xml`, `/slides`, `/slide` return 200; `X-Robots-Tag: noindex` present on `*.azurewebsites.net`, ABSENT on the custom domain.
15. **Responsive** — 1400px, 901px, 899px, 600px. At ≤900px the left rail becomes a dismissible overlay and the right rail hides. Assert no axis-label overlap (measure `getBoundingClientRect`) and the chart SVG uses the full width.
16. **Sticky autoscroll** — during a long stream scroll up: following stops and the "Latest" pill appears; clicking it resumes following.

## 5. Session, stop and recovery — historically the most fragile area

| Scenario | Expected |
| --- | --- |
| Stop mid-turn | marker **"You stopped this response before it finished."**, no `■`/`⏹` glyph, key in `sessionStorage.finops_stopped_turns` |
| Stop → tab return (`visibilitychange` + `focus` ×6) | **NO** "Reconnecting" notice; marker survives |
| Stop → reload | same wording; **not** downgraded to "No answer was generated" |
| Stop → send immediately | answers normally; no `busy`, no "finished without an answer" |
| Marker placement | a marker must NEVER appear with no question above it |
| First turn, fresh anonymous user | answer **commits** — broke before via `isActiveView()`; run **3×** |
| Reload mid-transcript | transcript restored; no "Connection lost" |
| New chat mid-stream | in-flight turn must NOT paint into the new view |
| Switch session mid-stream | background turn keeps running, dot pulses, returning shows the full answer |
| Two turns, same session | second rejected with `busy`, composer restored |
| `POST /api/chat/stop` | `{stopped:true}`; next prompt answers in ~1s |
| `POST /api/chat/reset` | clears context without killing the conversation |

## 6. Entra surfaces — ask me to sign in

Click **Connect Azure** via JS, then STOP and ask me to sign in. Poll `/auth/azure/status` until `connected:true`.

1. **Identity** — `/auth/me` and `/auth/azure/status` return the right user, subscriptions, management groups and `apis` list.
2. **Tenant picker** — `/auth/azure/tenants`; the Tenant ID box routes to a specific tenant.
3. **Conversations pane** — create, switch, delete, reload persistence, per-pane collapse (all three panes), `N saved` count correct, job run-logs HIDDEN from this list.
4. **Consent tiers** — each add-on (`licenses`, `chargeback`, `loganalytics`, `storage`) triggers its OWN consent screen; `graphTier` accumulates; the "grant all remaining" chain walks tiers in sequence. After each, the matching tool works (`QueryGraph`, `QueryLogAnalytics`, `ListCostExportBlobs`).
5. **Real tenant queries** — MTD spend, top resources, Advisor, budgets, tagging coverage, reservations, `FindIdleResources`, `DetectCostAnomalies`, `StartPricesheetDownload` + `GetPricesheetStatus`. Verify at least one figure against `az` directly.
6. **Maturity scoring** — click Score for Crawl; assert the `maturity_score` SSE arrives and sidebar stars update; `GetScoreHistory` returns the prior score.
7. **Savings ledger** — `RecordSavingsAction` → `UpdateSavingsAction` → `GetSavingsLedger` round-trips.
8. **Security gates** (all must be refused):
   - a DELETE through `QueryAzure` → blocked in code
   - a mutating POST (`/deallocate`, `/start`) → 403
   - another user's session id on `/api/sessions/{id}/messages` → 403/404, never someone else's transcript
   - answer text containing `<img onerror=...>` renders **literally**; `window.__xss` stays undefined
9. **Disconnect / revoke / logout** — `/auth/azure/disconnect`, `/auth/azure/revoke`, `/auth/logout` each clear state; the UI returns to the anonymous view and chat still works.

## 7. Scheduled jobs — full lifecycle (the part most worth testing)

1. **Create** from a template (all 9: 1-min test, Check capacity of X, Reserve X when available, Daily cost digest, Cost anomaly watch, Budget guard, Idle resource sweep, Advisor cost watch, Retry last question) and from a **custom cadence**; cadence pills sync the number input; bounds validated (1 and 43200 accepted; 0 and 43201 rejected inline AND by the API).
2. **Run immediately** auto-opens the run conversation.
3. **Run log view** — no composer; job bar shows Build deck / Summarize runs / Edit job; title reads `⚙ Job · {name}`.
4. **Continuation — the core test.** With a 1-min job running:
   - browse to another conversation, wait ≥2 cadences, come back → new runs landed
   - **reload the page** → runs still landing
   - **close the tab and reopen** → runs still landing
   - leave the browser idle ≥5 min → runs still landing, cadence unchanged
5. **Edit** (pencil) prefills; Cancel must NOT leak values into a new job; "Run now after saving" fires a run.
6. **Pause** shows "paused" with no fake countdown; **resume** is cap-checked.
7. **3-active cap** enforced on create AND on resume (pause + create×3 + resume must fail with an inline message).
8. **Delete** is optimistic; deleting a job un-hides its run log in Conversations and never strands a dead `sessionId`.
9. **Verify from telemetry, not the UI:**
   ```
   AppTraces | where TimeGenerated > ago(2h) | where Message startswith "Job "
   | project TimeGenerated, Message | order by TimeGenerated desc
   ```
   Expect steady cadence and `status=ok`; compute min/avg/max gap. A `status=error` immediately after `Application is shutting down` is a deploy interrupting a run, **not** a job failure — `JobScheduler` excludes that case.
10. **Auth durability** — jobs re-mint delegated tokens per run from the persisted refresh token. Confirm no `auth_expired` and no `Identity mismatch` over the window.

## 8. Regression watchlist — these were real, they must not come back

- Silently dropped answers when `currentSessionId` went null mid-turn (`isActiveView`, anonymous users).
- Session sprawl: warmup racing the first prompt; three sessions for one `hi`; `Session already tracked`.
- False "Reconnecting" that never converged after a Stop (`reconcileSessionAfterReturn`).
- A stopped-turn marker rendered with no question above it.
- `⏹` rendering as `■`.
- Greetings costing model → `SuggestFollowUp` → model plus a capability table.
- Cost Management pinned to a stale api-version.
- `/api/version.started` echoing "now".
- Deploy-interrupted job runs counted as real failures.
- Table vanishing when a post-answer tool fired after the final text.
- Blank page after deploy from a cached `index.html` referencing old hashed bundles.

## 9. Report

A table of **every turn**: prompt, TTFT, total, tool sequence, tool count, pass/fail. Then:

- Every console/page error with the reproducing step.
- p50/p95 across all turns vs the §3 budget; for anything over, the tool sequence that explains it.
- Endpoint coverage: which of the 39 routes were exercised, which were not, and why.
- SSE coverage: which of the 15 event types were observed.
- Every bug found, its root cause in code, the fix, and the re-test result.
- Anything still broken, stated plainly, with what you would do next.
