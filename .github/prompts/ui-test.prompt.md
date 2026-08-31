---
mode: agent
description: "Drive an exhaustive Playwright UI regression of the Azure FinOps Agent — every surface, every session/stop/recovery path, jobs lifecycle, and per-turn latency. Encodes the browser gotchas that make this app hostile to naive automation."
---

# Extensive UI Browser Test

Drive the **whole UI** with the VS Code integrated browser tools, measure response times, and fix anything broken. Read `/memories/repo/finops-agent-debugging.md` first — it holds the running catalog of past UI bugs and test gotchas.

Default target is **production** (`https://azure-finops-agent.com`). If I say "local", follow `debug-local.prompt.md` first.

## 0. Preflight — do not skip

1. **Load the browser tools.** Two `tool_search` calls: `"playwright browser navigate screenshot read page type"` and `"open browser page url click element handle dialog"`. If either returns nothing, STOP and tell me the test must run in VS Code Insiders. Never fall back to a system browser and never ask me to click through it manually.
2. **Confirm what is deployed.** `GET /api/version` → check `started`. After any deploy, the container takes ~60-90s to roll over. **Testing before the rollover silently tests the OLD build** — this has produced false "still broken" conclusions. Re-check `started` is recent before believing any result.
3. **If testing locally**, `wwwroot/` must exist BEFORE `dotnet run`. ASP.NET caches `WebRootPath` at startup; start with it missing and every asset 404s for the life of the process (`The WebRootPath was not found` in the log). `azd deploy` prunes `obj/`, `wwwroot/` and `frontend/node_modules`, so after a deploy: re-seed the Copilot CLI cache, `npm ci`, `npm run build`, THEN start.

## 1. Browser gotchas — this app breaks naive Playwright

These are all confirmed on this app. Using the naive approach wastes a cycle every time.

| Symptom | Cause | Do this instead |
| --- | --- | --- |
| `locator.click` times out "waiting for element to be stable" | infinite CSS animations | `page.evaluate(() => [...document.querySelectorAll('button')].find(b => /Label/.test(b.textContent)).click())` |
| `type_in_page` / `fill` fails "element is not visible" | `textarea.input-field` has `offsetWidth 0` | native setter + events (below) |
| Clicking "the last enabled button" sends a canned prompt | that's the **Script** button | select Stop by **`.action-btn--stop`** only |
| `page.context().clearCookies()` → "Method not found" | unsupported in integrated browser | expire via `document.cookie` per-name |
| `Illegal invocation` when wrapping fetch | unbound native | `const of = window.fetch.bind(window)` |
| ResizeObserver / rAF / smooth-scroll never fire | integrated browser reports `document.hidden === true` always | rely on reactive watchers; gate smooth scroll on `visibilityState` |
| Turn "completes" in 3s | you sent while a warmup turn was in flight | wait for `composer enabled && !stopBtn` before every send |

Canonical send + wait helpers to reuse:

```js
const el = () => document.querySelector('textarea.input-field');
const stopBtn = () => document.querySelector('.action-btn--stop');
const wait = async (fn, ms) => { const t = Date.now(); while (Date.now() - t < ms) { if (fn()) return true; await new Promise(r => setTimeout(r, 200)); } return false; };
const send = async (p) => {
  await wait(() => el() && !el().disabled && !stopBtn(), 90000);      // never send into a busy composer
  const e = el();
  Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(e, p);
  e.dispatchEvent(new Event('input', { bubbles: true }));             // syncs Vue v-model
  e.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true, cancelable: true }));
  return wait(() => !!stopBtn(), 40000);
};
```

## 2. Measure latency from the SSE, never from pixels

Visible text lags the stream by the typing animation, and `.messages` innerText baselines are unreliable on the first turn (hero → messages swap). Tee the app's own stream and timestamp the events:

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

Report per turn: **time to first `delta`** (true TTFT), time to `[DONE]`, the ordered **tool sequence**, and the tool count. The tool sequence is what explains slowness — each tool call is a full model round-trip (~2-5s), while the tools themselves usually execute in <2s.

## 3. Anonymous surfaces (no login)

Assert **zero** console errors, page errors and non-bot failed requests throughout (`page.on('console'|'pageerror'|'requestfailed')`).

1. **Landing** — hero, six feature cards, sidebar categories, composer disabled state.
2. **Trivial turns** — `hi`, `hello`, `thanks`. Must answer in ONE round-trip: **no tool calls, no tables, no bullet lists**, one sentence. Expect ~1-3s. Regression here means `TrivialTurnDirective` stopped being applied.
3. **Sidebar starter prompts** — click several in `Pricing & Estimates` (they are the primary entry point). Verify a chart renders and the figures are sane.
4. **Follow-up chips** — click the chip the agent offers; it must run in the SAME conversation.
5. **Multi-region pricing** — "Compare X in region A, B and C". Assert **exactly ONE** `GetAzureRetailPricing` call (multi-region takes a comma-separated list). More than one is a regression.
6. **Arithmetic accuracy** — ask a TCO question with known inputs and verify the maths independently (e.g. rate × 26,280h × N VMs). The model does this inline; a wrong total is a real bug.
7. **Charts + tables** render; **follow-up chips** appear; **prompt chips** (`.prompt-chip`) inside answers route correctly.
8. **Composer actions** — Attach, Clear, Presentation, Script enable/disable correctly.
9. **Downloads** — generate a script and a deck; assert the card renders and the download endpoint returns 200.
10. **Responsive** — resize to 899px and 901px. At ≤900px the left sidebar becomes an overlay and the right sidebar hides. Assert no label overlap and the chart SVG uses the full width.

## 4. Session, stop and recovery — the historically fragile part

Run each and assert the exact expected state:

| Scenario | Expected |
| --- | --- |
| Stop mid-turn | marker **"You stopped this response before it finished."**, no `■`/`⏹` glyph, registry key in `sessionStorage.finops_stopped_turns` |
| Stop → tab return (`visibilitychange` + `focus` ×6) | **NO** "Reconnecting" notice; marker survives |
| Stop → reload | same wording survives; **not** downgraded to "No answer was generated" |
| Stop → send immediately | answers normally; no `busy`, no "finished without an answer" |
| Marker placement | a marker must NEVER appear with no question above it |
| First turn, fresh anonymous user | answer **commits** — this broke before via `isActiveView()`; run it 3× |
| Reload mid-transcript | transcript restored, no "Connection lost" |
| New chat / switch session | in-flight turn does not paint into the new view |

## 5. Entra surfaces — ask me to log in

Click **Connect Azure** (via JS), then STOP and ask me to sign in. **Never touch credential fields.** Poll `/auth/azure/status` until `connected: true`, then continue.

1. **Conversations pane** — create, switch, rename, delete, reload persistence, per-pane collapse.
2. **Consent tiers** — each add-on row triggers its own consent screen; `graph_tier` accumulates.
3. **Real subscription queries** — cost MTD, Advisor, tagging, budgets. Verify figures against `az` where cheap.
4. **Maturity scoring** — click a Score button, assert stars update in the sidebar header.
5. **Scheduled jobs — full lifecycle** (this is the part most worth testing):
   - Create via template and via custom cadence (1-43200 min); cadence pills sync the number input.
   - "Run immediately" auto-opens the run conversation.
   - Run log view: **no composer**, job bar shows Build deck / Summarize runs / Edit job.
   - **Navigate away, browse other conversations, come back** — runs must have continued.
   - **Reload the page** — runs must still be landing.
   - Edit (pencil) prefills; cancel must NOT leak values into a new job.
   - Pause shows "paused" with no fake countdown; resume is cap-checked.
   - 3-active cap enforced on create AND on resume.
   - Delete is optimistic; deleting a job un-hides its run log in Conversations.
   - Verify continuation from telemetry, not just the UI:
     ```
     AppTraces | where TimeGenerated > ago(2h) | where Message startswith "Job "
     | project TimeGenerated, Message | order by TimeGenerated desc
     ```
     Expect a steady cadence and `status=ok`. A `status=error` immediately after `Application is shutting down` is a deploy interrupting a run, not a job failure.

## 6. Report

Produce a table of **every turn**: prompt, TTFT, total, tool sequence, tool count, pass/fail. Then:

- Every console/page error with the reproducing step.
- p50/p95 across turns, and for anything >20s the tool sequence that explains it.
- Cross-check against production: `AppRequests | where Name has "/api/chat" | summarize p50=percentile(DurationMs,50), p95=percentile(DurationMs,95)`.

**Fix what you find**, then re-run the affected scenario. Do not report a scenario as passing on a measurement you know is unreliable — re-measure with an assertion that cannot be faked by a timing artifact. If you were wrong earlier in the run, say so plainly and correct the record.
