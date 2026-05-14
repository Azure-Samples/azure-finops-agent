---
agent: agent
description: "Build, run, and interactively debug the Azure FinOps Agent locally inside the VS Code Insiders integrated Playwright browser"
---

## Local Debug (VS Code Insiders Playwright flow)

You are starting an interactive local debug session for the Azure FinOps Agent. The browser used MUST be the VS Code Insiders **integrated** browser pane — never a system browser.

### 0. Environment + tools check (do this first, before anything else)

The required Playwright browser tools are deferred. Load them in TWO `tool_search` calls (a single query won't surface all of them):

1. `tool_search` for `"playwright browser navigate screenshot read page type"` — gives `navigate_page`, `screenshot_page`, `read_page`, `type_in_page`, `hover_element`, `run_playwright_code`.
2. `tool_search` for `"open browser page url click element handle dialog"` — gives `open_browser_page`, `click_element`, `handle_dialog`, `drag_element`, `navigate_page` again.

If either search returns nothing, STOP and tell the user: *"This prompt requires the VS Code Insiders built-in Playwright browser tools. Open this workspace in VS Code Insiders and re-run the prompt."* Do not fall back to a system browser.

### 1. Build the Vue frontend to `wwwroot/`

Use absolute paths — the user's PowerShell profile may rewrite relative `cd` arguments and break a chained `cd ../../`:

```pwsh
Set-Location C:\repos\azure-finops-agent-azsamples\src\Dashboard\frontend
npm install
npm run build
```

### 2. Start the .NET backend on port 5000

> **CRITICAL**: set `ASPNETCORE_ENVIRONMENT=Development` first. Without it, ASP.NET Core loads `appsettings.Production.json` and the OAuth `redirect_uri` will mismatch.

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

### 4. Open the app in the VS Code integrated Playwright browser

- Call `open_browser_page` with `url=http://localhost:5000`. Save the returned `pageId`.
- Call `screenshot_page` so the user can see the chat UI is up.
- The shared-page label in VS Code may stay as `"Untitled (about:blank)"` even after navigation — that's a known stale label. Trust `read_page` / `screenshot_page`, NOT the tab title.

### 5. Hand off to the user for sign-in

Post this message verbatim:

> The local app is running and open in the VS Code browser pane. Please click **Connect Azure** (and any add-on consent buttons you want enabled), complete the Microsoft sign-in flow, and reply **"logged in"** when you're back on the chat screen. I'll wait.

Rules:
- Do NOT click `Connect Azure` yourself — credential entry stays with the user.
- Do NOT call `vscode_askQuestions` for this step.
- The Microsoft sign-in usually opens in a separate window; once it returns to `localhost:5000`, the cookie is shared with the integrated browser process and the shared tab will show the connected sidebar on the next `read_page`.

### 6. After the user confirms sign-in

- Call `read_page` first (cheap structured snapshot). Confirm the connected state by checking that the sidebar contains buttons named `Crawl Visibility & Baseline`, `Walk Optimization & Governance`, `Run Scale & Accountability`. If they are missing, call `navigate_page type=reload` and re-read.
- Then call `screenshot_page` for a visual confirmation.
- Ask the user (free-form) what they want to test, with these suggested options:
  - Score Crawl / Walk / Run maturity
  - A specific FinOps prompt they paste in
  - A UI flow (sidebar collapse, deck generation, file upload, maturity-card chevron expand/collapse, etc.)
  - A specific tool (e.g. `QueryAzure`, `RenderChart`, `GenerateHtmlPresentation`)

### 6a. Driving the chat UI — gotchas

- **Sidebar buttons can intercept pointer events** during the initial sidebar mount/scroll. If `click_element` on a sidebar button times out with `intercepts pointer events`, fall back to `run_playwright_code` and call `.click({ force: true })` on the same selector, OR scroll the sidebar with `page.locator('.sidebar-scroll').evaluate(el => el.scrollTop = 0)` and retry.
- For chat input: `type_in_page` into the textbox `Ask a question about your data`, then `click_element` on the send button.
- After submission, the agent streams. Poll with `read_page` every few seconds to see the assistant reply, tool calls in the right rail, and any rendered chart. For long flows (maturity scoring), expect 30–90 s end-to-end.
- For each meaningful step capture a `screenshot_page` so the user can audit visually.
- If a tool fails, query App Insights with the KQL pattern in `.github/copilot-instructions.md` instead of guessing the cause.

### 7. Cleanup

- Do NOT kill the backend or close the browser tab on your own. Leave both running until the user explicitly says they're done.
- When the user says they're done, kill the backend terminal (use `kill_terminal` with the saved id from step 2) and confirm. Leave the browser pane open unless they ask otherwise.
