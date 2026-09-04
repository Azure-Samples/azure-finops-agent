---
agent: agent
description: "Build, run, and interactively debug the Azure FinOps Agent locally inside the VS Code Insiders integrated Playwright browser"
---

## Local Debug (VS Code Insiders Playwright flow)

You are starting an interactive local debug session for the Azure FinOps Agent. The browser used MUST be the VS Code Insiders **integrated** browser pane — never a system browser.

> **Authenticated-tab invariant:** drive the page only with `run_playwright_code`. Once the user shares a signed-in tab, keep its exact `pageId` for the rest of the run. Never call `open_browser_page`, `navigate_page`, `read_page`, `click_element`, `type_in_page`, any `mcp_playwright_browser_*` tool, or another browser helper: it may silently create or attach to a different anonymous page. If sharing is lost, ask the user to re-share the same tab instead of opening one.

### 0. Environment + Playwright check (do this first, before anything else)

Use one harmless `run_playwright_code` call on the exact `pageId` from the user's shared-page attachment and return `page.url()` plus `page.title()`. If the call is unavailable or reports `Page not found`, STOP and ask the user to re-share that same tab. Do not call any other browser tool, discover a replacement page, or fall back to a system browser.

### 1. Build the Vue frontend to `wwwroot/`

Use absolute paths — the user's PowerShell profile may rewrite relative `cd` arguments and break a chained `cd ../../`:

```pwsh
Set-Location C:\repos\azure-finops-agent-azsamples\src\Dashboard\frontend
npm install
npm run build
```

### 2. Start the .NET backend on port 5000

> **CRITICAL**: set `ASPNETCORE_ENVIRONMENT=Development` first. Without it, ASP.NET Core loads `appsettings.Production.json` and the OAuth `redirect_uri` will mismatch.

Before startup, verify Azure CLI can mint the provider token for the locally configured Azure OpenAI tenant. If token acquisition reports `AADSTS90072` while `az account list` already contains enabled subscriptions in that tenant, select one of those subscriptions with `az account set --subscription <id>` and retry the token request before asking for another login. Azure CLI chooses the identity associated with its selected subscription; changing only `TenantId` is insufficient when another tenant is currently default. Resolve tenant/subscription from local configuration and cached account metadata—never add deployment coordinates to this prompt.

Run as an **async** terminal (the server stays alive while you drive the browser):

```pwsh
Set-Location C:\repos\azure-finops-agent-azsamples\src\Dashboard
$env:ASPNETCORE_ENVIRONMENT="Development"
dotnet run --project Dashboard.csproj --urls "http://localhost:5000"
```

Wait until the log shows `Now listening on: http://localhost:5000`. If startup fails, surface the exact error and stop.

### 3. Smoke-check `/api/version`

From a sync terminal:

```pwsh
(Invoke-WebRequest -Uri http://localhost:5000/api/version -UseBasicParsing).StatusCode
```

Must return `200`. If not, dump the last 30 lines of the backend terminal and stop.

### 4. Use the shared app tab in the VS Code integrated Playwright browser

- Ask the user to open `http://localhost:5000` in the integrated browser and share that tab, then save its exact `pageId`.
- Use `run_playwright_code` against only that id for DOM assertions, interaction, navigation, and screenshots.
- The shared-page label in VS Code may stay as `"Untitled (about:blank)"` even after navigation — that's a known stale label. Trust `page.url()` and DOM assertions inside Playwright, not the tab title.

### 5. Hand off to the user for sign-in

Post this message verbatim:

> The local app is running and open in the VS Code browser pane. Please click **Connect Azure** (and any add-on consent buttons you want enabled), complete the Microsoft sign-in flow, and reply **"logged in"** when you're back on the chat screen. I'll wait.

Rules:

- Do NOT click `Connect Azure` yourself — credential entry stays with the user.
- Do NOT call `vscode_askQuestions` for this step.
- The Microsoft sign-in usually opens in a separate window; once it returns to `localhost:5000`, the pinned shared tab will show the connected sidebar on the next Playwright DOM assertion.

### 6. After the user confirms sign-in

- Use `run_playwright_code` on the same pinned page id. Confirm `/auth/azure/status` returns `connected:true` and the sidebar contains buttons named `Crawl Visibility & Baseline`, `Walk Optimization & Governance`, `Run Scale & Accountability`. If they are missing, call `page.reload()` in that same Playwright page and assert again.
- Capture visual evidence with `page.screenshot()` when needed; do not switch browser contexts.
- Ask the user (free-form) what they want to test, with these suggested options:
  - Score Crawl / Walk / Run maturity
  - A specific FinOps prompt they paste in
  - A UI flow (sidebar collapse, deck generation, file upload, maturity-card chevron expand/collapse, etc.)
  - A specific tool (e.g. `QueryAzure`, `RenderChart`, `GenerateHtmlPresentation`)

### 6a. Driving the chat UI — gotchas

- **Sidebar buttons can intercept pointer events** during the initial sidebar mount/scroll. Use `page.evaluate()` to invoke the matching button's native `.click()`, or scroll `.sidebar-scroll` in the page before retrying.
- For chat input, use the native `HTMLTextAreaElement.prototype.value` setter plus `input` and Enter keyboard events inside `page.evaluate()`; do not use `fill()` or another browser helper.
- After submission, inspect the DOM and the instrumented SSE stream through short `run_playwright_code` calls. For long flows (maturity scoring), expect 30–90 s end-to-end.
- For visual evidence use `page.screenshot()` inside `run_playwright_code`, saving only when a screenshot is needed.
- If a tool fails, query App Insights with the KQL pattern in `.github/copilot-instructions.md` instead of guessing the cause.

### 7. Cleanup

- Do NOT kill the backend or close the browser tab on your own. Leave both running until the user explicitly says they're done.
- When the user says they're done, kill the backend terminal (use `kill_terminal` with the saved id from step 2) and confirm. Leave the browser pane open unless they ask otherwise.
