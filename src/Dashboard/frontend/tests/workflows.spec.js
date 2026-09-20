import { expect, test } from "@playwright/test";

const sessionId = "synthetic-conversation";
const artifactId = "11111111111111111111111111111111";
const operationId = "22222222222222222222222222222222";
const change = {
  operationId,
  method: "PATCH",
  target: "/synthetic/resource/with-a-long-name-for-mobile-layout",
  body: '{"tags":{"Owner":"Synthetic team"}}',
  status: "awaitingApproval",
  costImpact:
    "Review the configuration and potential charges before approving.",
};

async function arrange(page, chatEvents, history = { messages: [] }) {
  const requests = [];
  const errors = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await page.addInitScript(() => {
    if (window.top === window) localStorage.setItem("addons-tour-shown", "1");
  });
  await page.route("**/auth/me", (route) =>
    route.fulfill({
      json: { id: 101, login: "synthetic-user", name: "Synthetic user" },
    }),
  );
  await page.route("**/auth/azure/**", (route) =>
    route.fulfill({ json: { connected: false, tenants: [] } }),
  );
  await page.route("**/api/**", async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === "/api/chat") {
      requests.push(route.request().postDataJSON());
      const payload = [{ type: "session", id: sessionId }, ...chatEvents];
      return route.fulfill({
        contentType: "text/event-stream",
        body:
          payload
            .map((event) => `data: ${JSON.stringify(event)}\n\n`)
            .join("") + "data: [DONE]\n\n",
      });
    }
    if (path === "/api/version")
      return route.fulfill({
        json: { sha: "test", build: "test", branch: "test" },
      });
    if (path === "/api/config") return route.fulfill({ json: {} });
    if (path === "/api/models")
      return route.fulfill({ json: { models: [], defaultModel: "synthetic" } });
    if (path === "/api/sessions/new")
      return route.fulfill({ json: { sessionId } });
    if (path === "/api/sessions")
      return route.fulfill({
        json: { sessions: [], currentSessionId: sessionId },
      });
    if (path.endsWith("/messages")) return route.fulfill({ json: history });
    if (path.endsWith("/active"))
      return route.fulfill({ json: { active: false } });
    if (path === "/api/jobs")
      return route.fulfill({ json: { jobs: [], entraRequired: true } });
    if (path.endsWith("/approve"))
      return route.fulfill({
        json: {
          result: {
            status: "accepted",
            nextAction: "Check the operation for terminal state.",
          },
        },
      });
    if (path.endsWith("/reject"))
      return route.fulfill({ json: { rejected: true } });
    if (path.startsWith("/api/download/"))
      return route.fulfill({
        contentType:
          "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        headers: {
          "content-disposition": 'attachment; filename="synthetic.xlsx"',
        },
        body: "synthetic-test-file",
      });
    return route.fulfill({ json: {} });
  });
  await page.goto("/");
  await expect(page.locator("textarea")).toBeEnabled();
  return { requests, errors };
}

async function send(page, prompt = "make an Excel file") {
  await expect(page.locator(".action-btn--stop")).toHaveCount(0);
  await expect(page.locator("textarea")).toBeEnabled();
  await page.locator("textarea").fill(prompt);
  await page.locator("textarea").press("Enter");
  await expect(page.locator(".action-btn--stop")).toHaveCount(0);
}

test("top bar links to the owner LinkedIn profile", async ({ page }) => {
  const { errors } = await arrange(page, []);
  const link = page.getByRole("link", {
    name: "Contact Ali Reza Farahnak on LinkedIn",
  });
  await expect(link).toBeVisible();
  await expect(link).toHaveAttribute(
    "href",
    "https://www.linkedin.com/in/alirezafarahnak/",
  );
  await expect(link).toHaveAttribute("target", "_blank");
  await expect(link).toHaveAttribute("rel", "noopener noreferrer");
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBeTruthy();
  expect(errors).toEqual([]);
});

test("short follow-up renders a real spreadsheet download without HTML preview", async ({
  page,
}, testInfo) => {
  const { requests, errors } = await arrange(page, [
    { type: "delta", content: "The requested workbook is ready." },
    {
      type: "html_ready",
      fileId: artifactId,
      fileName: "synthetic.xlsx",
      slideCount: "2 rows (XLSX)",
    },
  ]);
  await send(page);
  const link = page.locator('a[download="synthetic.xlsx"]').last();
  await expect(link).toBeVisible();
  await expect(link).toHaveAttribute(
    "href",
    `/api/download/file/${artifactId}`,
  );
  await expect(page.locator(".html-deck-card-btn--preview")).toHaveCount(0);
  expect(requests[0].prompt).toBe("make an Excel file");
  expect(Array.isArray(requests[0].fileIds)).toBeTruthy();
  await page.screenshot({
    path: testInfo.outputPath("download.png"),
    animations: "disabled",
  });
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBeTruthy();
  expect(errors).toEqual([]);
});

test("complete final message replaces partial deltas after a throttled detail query", async ({
  page,
}, testInfo) => {
  const answer =
    "The requested service breakdown could not be retrieved because Azure Cost Management is throttling requests.";
  const { requests, errors } = await arrange(page, [
    {
      type: "tool_start",
      tool: "QueryAzure",
      id: "synthetic-cost-query",
      args: "{}",
    },
    {
      type: "tool_done",
      tool: "QueryAzure",
      id: "synthetic-cost-query",
      success: true,
      result: "HTTP 429 TooManyRequests\nRetry later.",
    },
    { type: "delta", content: "# **" },
    { type: "message", content: answer },
  ]);
  await send(page, "I need detailed breakdown");
  await expect(page.getByText(answer, { exact: true })).toBeVisible();
  expect(requests).toHaveLength(1);
  expect(errors).toEqual([]);
  await page.screenshot({
    path: testInfo.outputPath("cost-detail-final-message.png"),
    animations: "disabled",
  });
});

test("tool validation errors remain failures when the SDK callback succeeded", async ({ page }) => {
  const { requests, errors } = await arrange(page, [
    { type: "tool_start", tool: "CalculateCost", id: "invalid-calculation", args: "{}" },
    { type: "tool_done", tool: "CalculateCost", id: "invalid-calculation", success: true, result: "Error: Every line requires label and unit." },
    { type: "message", content: "The calculation input was rejected." },
  ]);
  await send(page, "Calculate the monthly estimate");
  await expect(page.getByText("The calculation input was rejected.", { exact: true })).toBeVisible();
  await expect(page.locator(".st-icon--ok")).toHaveCount(0);
  await expect(page.locator(".st-icon--fail")).toHaveCount(1);
  expect(requests).toHaveLength(1);
  expect(errors).toEqual([]);
});

for (const outcome of ["failed", "cancelled", "accepted", "partial", "succeeded"]) {
  test(`bulk request ${outcome} is not confused with SDK success`, async ({ page }, testInfo) => {
    const complete = outcome === "succeeded";
    const result = {
      total: 2,
      succeeded: outcome === "partial" || complete ? 2 : 1,
      failed: outcome === "failed" ? 1 : 0,
      cancelled: outcome === "cancelled" ? 1 : 0,
      pending: outcome === "accepted" ? 1 : 0,
      unattempted: 0,
      stopped: outcome === "failed",
      complete,
      results: [
        { index: 0, status: 200, outcome: "succeeded", partial: false, body: { cost: 12 } },
        {
          index: 1,
          status: outcome === "failed" ? 429 : outcome === "cancelled" ? 0 : outcome === "accepted" ? 202 : 200,
          outcome: outcome === "partial" ? "succeeded" : outcome,
          partial: outcome === "partial",
        },
      ],
    };
    const answer = complete ? "Both scoped reads completed." : "The batch did not fully complete.";
    const { requests, errors } = await arrange(page, [
      { type: "tool_start", tool: "BulkAzureRequest", id: "synthetic-bulk", args: '{"requests":[{},{}]}' },
      { type: "tool_done", tool: "BulkAzureRequest", id: "synthetic-bulk", success: true, result: JSON.stringify(result) },
      { type: "message", content: answer },
    ]);
    await send(page, "Read both cost scopes");
    await expect(page.getByText(answer, { exact: true })).toBeVisible();
    await expect(page.locator(".st-icon--ok")).toHaveCount(complete ? 1 : 0);
    await expect(page.locator(".st-icon--fail")).toHaveCount(complete ? 0 : 1);
    if (testInfo.project.name === "desktop")
      await expect(page.locator(complete ? ".st-icon--ok" : ".st-icon--fail")).toBeVisible();
    expect(requests).toHaveLength(1);
    expect(errors).toEqual([]);
  });
}

test("later follow-up messages do not replace an already visible cost table", async ({
  page,
}, testInfo) => {
  const answer =
    "Synthetic seven-day costs:\n\n| Service | Cost |\n| --- | ---: |\n| Compute | USD 40 |\n| Storage | USD 10 |\n| Total | USD 50 |";
  await page.addInitScript(
    ({ session, answer }) => {
      const originalFetch = window.fetch.bind(window);
      window.fetch = (input, options) => {
        if (
          new URL(typeof input === "string" ? input : input.url, location.href)
            .pathname !== "/api/chat"
        )
          return originalFetch(input, options);
        const encoder = new TextEncoder();
        return Promise.resolve(
          new Response(
            new ReadableStream({
              start(controller) {
                const emit = (event) =>
                  controller.enqueue(
                    encoder.encode(`data: ${JSON.stringify(event)}\n\n`),
                  );
                emit({ type: "session", id: session });
                emit({
                  type: "delta",
                  messageId: "answer-message",
                  content: answer,
                });
                emit({
                  type: "message",
                  messageId: "answer-message",
                  content: answer,
                });
                window.__finishFollowUp = () => {
                  emit({
                    type: "tool_start",
                    tool: "SuggestFollowUp",
                    id: "follow-up-call",
                    args: "{}",
                  });
                  emit({
                    type: "tool_done",
                    tool: "SuggestFollowUp",
                    id: "follow-up-call",
                    success: true,
                    result: JSON.stringify({
                      label: "Explore storage",
                      prompt: "Show the synthetic storage breakdown",
                    }),
                  });
                  const followUp =
                    "[Explore compute](prompt:Show the synthetic compute breakdown)";
                  emit({
                    type: "delta",
                    messageId: "follow-up-message",
                    content: followUp,
                  });
                  emit({
                    type: "message",
                    messageId: "follow-up-message",
                    content: followUp,
                  });
                  controller.enqueue(encoder.encode("data: [DONE]\n\n"));
                  controller.close();
                };
              },
            }),
            { headers: { "content-type": "text/event-stream" } },
          ),
        );
      };
    },
    { session: sessionId, answer },
  );
  const { errors } = await arrange(page, []);
  await page
    .locator("textarea")
    .fill("What did I spend in the last seven days?");
  await page.locator("textarea").press("Enter");
  await expect(
    page.getByRole("cell", { name: "USD 50", exact: true }),
  ).toBeVisible();
  await page.evaluate(() => window.__finishFollowUp());
  await expect(page.locator(".action-btn--stop")).toHaveCount(0);
  await expect(
    page.getByRole("cell", { name: "USD 50", exact: true }),
  ).toBeVisible();
  await expect(
    page.getByRole("cell", { name: "Compute", exact: true }),
  ).toHaveCount(1);
  await expect(
    page.getByText("Explore compute", { exact: true }),
  ).toBeVisible();
  await expect(
    page.getByText("Explore storage", { exact: true }),
  ).toBeVisible();
  expect(errors).toEqual([]);
  await page.screenshot({
    path: testInfo.outputPath("cost-table-after-follow-up.png"),
    animations: "disabled",
  });
});

test("an empty model result is visibly recoverable rather than silent success", async ({
  page,
}, testInfo) => {
  const message =
    "The model finished without an answer or generated result. Your request was not fulfilled. Review pending operations before retrying a change.";
  const { requests, errors } = await arrange(page, [
    { type: "error", code: "empty_result", message },
  ]);
  await send(page, "Create the requested synthetic deck");
  await expect(page.getByText(message, { exact: false })).toBeVisible();
  await expect(page.locator("textarea")).toBeEnabled();
  await expect(page.locator(".action-btn--stop")).toHaveCount(0);
  expect(requests).toHaveLength(1);
  expect(errors).toEqual([]);
  await page.screenshot({
    path: testInfo.outputPath("empty-result.png"),
    animations: "disabled",
  });
});

test("reloading a failed terminal turn does not claim the answer is still generating", async ({ page }) => {
  await arrange(page, []);
  await page.route("**/auth/azure/status", route => route.fulfill({
    json: { connected: true, tenants: [], subscriptions: [] },
  }));
  await page.route("**/api/sessions", route => route.fulfill({
    json: { sessions: [{ id: sessionId, summary: "Synthetic failed request", modified: new Date().toISOString() }], currentSessionId: sessionId },
  }));
  await page.route(`**/api/sessions/${sessionId}/messages`, route => route.fulfill({
    json: { messages: [{ role: "user", content: "Synthetic failed request" }] },
  }));
  await page.route(`**/api/sessions/${sessionId}/outcomes`, route => route.fulfill({
    json: { outcomes: [{ status: "error", startedUtc: "2026-01-01T00:00:00Z", completedUtc: "2026-01-01T00:00:05Z" }] },
  }));
  await page.evaluate(session => sessionStorage.setItem("finops_last_session", session), sessionId);
  await page.reload({ waitUntil: "domcontentloaded" });
  await expect(page.getByRole("alert")).toContainText("This turn has ended and is no longer generating.");
  await expect(page.getByText("Reconnecting — your last answer is still being generated", { exact: false })).toHaveCount(0);
  await expect(page.locator("textarea")).toBeEnabled();
});

test("a restored model failure keeps its reason and lets the user edit without resending", async ({ page }, testInfo) => {
  const prompt = "Show spending for the last seven days";
  const { requests, errors } = await arrange(page, [], {
    messages: [
      { role: "user", content: prompt },
      {
        role: "system",
        terminalStatus: "error",
        content: "Authentication failed with provider at https://synthetic.invalid/openai/v1/ (HTTP 401). Check your COPILOT_PROVIDER_API_KEY.",
      },
    ],
  });
  await page.route("**/auth/azure/status", route => route.fulfill({
    json: { connected: true, tenants: [], subscriptions: [] },
  }));
  await page.route("**/api/sessions", route => route.fulfill({
    json: { sessions: [{ id: sessionId, summary: prompt, modified: new Date().toISOString() }], currentSessionId: sessionId },
  }));
  await page.evaluate(session => sessionStorage.setItem("finops_last_session", session), sessionId);
  await page.reload({ waitUntil: "domcontentloaded" });

  const failure = page.getByRole("alert");
  await expect(failure).toContainText("AI model access is blocked");
  await expect(failure).toContainText("HTTP 401");
  await expect(failure).toContainText("tenant sign-in");
  await expect(failure).not.toContainText("synthetic.invalid");
  await expect(failure).not.toContainText("COPILOT_PROVIDER_API_KEY");
  await expect(page.getByText("Reconnecting", { exact: false })).toHaveCount(0);
  await expect(page.locator(".action-btn--stop")).toHaveCount(0);
  await failure.getByRole("button", { name: "Edit saved question" }).click();
  await expect(page.locator("textarea")).toHaveValue(prompt);
  await expect(page.locator("textarea")).toBeFocused();
  expect(requests).toHaveLength(0);
  expect(errors).toEqual([]);
  await page.screenshot({ path: testInfo.outputPath("restored-model-failure.png") });
});

test("a live failure preserves partial answers and never overwrites a new draft", async ({ page }) => {
  const prompt = "Explain the synthetic costs";
  const table = "| Service | Cost |\n|---|---|\n| Synthetic compute | USD 25 |";
  const { requests, errors } = await arrange(page, [
    { type: "message", messageId: "partial-answer", content: table },
    { type: "error", message: "Authentication failed with provider (HTTP 401)" },
  ]);
  await send(page, prompt);
  await expect(page.locator(".message-text table")).toContainText("USD 25");
  const failure = page.getByRole("alert");
  await expect(failure).toContainText("AI model access is blocked");
  await expect(page.locator(".streaming-cursor")).toHaveCount(0);
  await page.locator("textarea").fill("Keep my new draft");
  await expect(failure.getByRole("button", { name: "Edit saved question" })).toBeDisabled();
  await expect(page.locator("textarea")).toHaveValue("Keep my new draft");
  await page.locator("textarea").fill("");
  await failure.getByRole("button", { name: "Edit saved question" }).click();
  await expect(page.locator("textarea")).toHaveValue(prompt);
  expect(requests).toHaveLength(1);
  expect(errors).toEqual([]);
});

test("compact navigation is fully hidden when closed and aligned below the header when open", async ({ page }) => {
  const { errors } = await arrange(page, []);
  await page.setViewportSize({ width: 855, height: 700 });
  const navigation = page.locator(".sidebar");
  const menu = page.locator(".portal-burger");
  await expect(navigation).toBeHidden();
  await expect(menu).toHaveAttribute("aria-expanded", "false");
  await menu.click();
  await expect(navigation).toBeVisible();
  await expect(menu).toHaveAttribute("aria-expanded", "true");
  const header = await page.locator(".portal-header").boundingBox();
  const sidebar = await navigation.boundingBox();
  expect(Math.abs(sidebar.y - (header.y + header.height))).toBeLessThan(1);
  await page.getByRole("button", { name: "Close navigation menu" }).click();
  await expect(navigation).toBeHidden();
  await menu.click();
  await page.keyboard.press("Escape");
  await expect(navigation).toBeHidden();
  await expect(menu).toBeFocused();
  await expect(page.locator(".tools-sidebar")).toBeHidden();
  expect(errors).toEqual([]);
});

async function arrangeRequestProgress(page, event = {}) {
  await page.addInitScript(
    ({ session, event }) => {
      const originalFetch = window.fetch.bind(window);
      window.fetch = (input, options) => {
        if (
          new URL(typeof input === "string" ? input : input.url, location.href)
            .pathname !== "/api/chat"
        )
          return originalFetch(input, options);
        window.__progressRequestCount = (window.__progressRequestCount || 0) + 1;
        const encoder = new TextEncoder();
        return Promise.resolve(
          new Response(
            new ReadableStream({
              start(controller) {
                const emit = (data) =>
                  controller.enqueue(
                    encoder.encode(`data: ${JSON.stringify(data)}\n\n`),
                  );
                emit({ type: "session", id: session });
                emit({
                  type: "tool_start",
                  tool: "QueryAzure",
                  id: "synthetic-cost-query",
                  args: JSON.stringify({ path: "/providers/Microsoft.CostManagement/query" }),
                });
                const progress = {
                  type: "cooling_down",
                  tool: "azure",
                  url: "/providers/Microsoft.CostManagement/query",
                  attempt: 1,
                  status: 429,
                  toolCallId: "synthetic-cost-query",
                  waitSeconds: 37,
                  willRetry: true,
                  ...event,
                  retryAtUtc: event.retryAtUtc || new Date(Date.now() + (event.waitSeconds ?? 37) * 1000).toISOString(),
                };
                window.__progressDeadline = progress.retryAtUtc;
                emit(progress);
                window.__emitRequestProgress = (update) => emit({ ...progress, ...update });
                window.__finishCostRetry = (status = 200) => {
                  emit({
                    type: "tool_done",
                    tool: "QueryAzure",
                    id: "synthetic-cost-query",
                    success: true,
                    result: `HTTP ${status}\n{}`,
                  });
                  emit({
                    type: "message",
                    content: status === 200
                      ? "The resource breakdown is available after retrying."
                      : "The requested resource costs are still unavailable; no detail amounts were inferred.",
                  });
                  controller.enqueue(encoder.encode("data: [DONE]\n\n"));
                  controller.close();
                };
              },
            }),
            { headers: { "content-type": "text/event-stream" } },
          ),
        );
      };
    },
    { session: sessionId, event },
  );
  return arrange(page, []);
}

async function startRequestProgress(page) {
  await expect(page.locator("textarea")).toBeEnabled();
  await expect(page.locator(".action-btn--stop")).toHaveCount(0);
  await page.locator("textarea").fill("I need detailed breakdown");
  await page.locator("textarea").press("Enter");
  await expect(page.getByRole("group", { name: "Request progress", exact: true })).toBeVisible();
}

test("assistant avatar mark replaces both AI circles and keeps completed replies static", async ({ page }, testInfo) => {
  const browserErrors = [];
  page.on("console", (message) => { if (message.type() === "error") browserErrors.push(message.text()); });
  page.on("requestfailed", (request) => browserErrors.push(request.failure()?.errorText));
  await page.emulateMedia({ reducedMotion: "no-preference" });
  const { errors } = await arrangeRequestProgress(page);
  await startRequestProgress(page);
  const active = page.getByRole("img", { name: "Azure FinOps assistant, working", exact: true });
  const historical = page.getByRole("img", { name: "Azure FinOps assistant", exact: true });
  await expect(active).toBeVisible();
  await expect(active.locator("svg")).toHaveAttribute("aria-hidden", "true");
  await expect(active.locator("svg")).toHaveAttribute("focusable", "false");
  await expect(active.locator(".assistant-avatar-orbit")).not.toHaveCSS("animation-name", "none");
  await expect(active).toHaveCSS("width", "32px");
  await expect(active).toHaveCSS("height", "32px");
  await expect(page.locator(".ai-avatar")).toHaveCount(0);
  await expect(active.locator("img, image, text")).toHaveCount(0);
  await page.evaluate(() => window.__finishCostRetry());
  await expect(active).toHaveCount(0);
  await expect(historical).toHaveCount(1);
  await expect(historical.locator(".assistant-avatar-orbit")).toHaveCSS("animation-name", "none");
  expect(await historical.evaluate((element) => element.getAnimations({ subtree: true }).length)).toBe(0);
  await startRequestProgress(page);
  await expect(historical).toHaveCount(1);
  await expect(active).toBeVisible();
  await expect(historical.locator(".assistant-avatar-orbit")).toHaveCSS("animation-name", "none");
  await expect(active.locator(".assistant-avatar-orbit")).not.toHaveCSS("animation-name", "none");
  const gradients = await page.locator(".assistant-avatar linearGradient").evaluateAll((elements) => elements.map((element) => element.id));
  expect(gradients).toHaveLength(4);
  expect(new Set(gradients).size).toBe(gradients.length);
  await page.screenshot({
    path: testInfo.outputPath("assistant-avatar-history-and-thinking.png"),
    animations: "disabled",
  });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  await page.evaluate(() => window.__finishCostRetry());
  await expect(active).toHaveCount(0);
  await expect(historical).toHaveCount(2);
  expect(await historical.evaluateAll((elements) => elements.every((element) => element.getAnimations({ subtree: true }).length === 0))).toBeTruthy();
  expect(errors).toEqual([]);
  expect(browserErrors).toEqual([]);
});

test("assistant avatar thinking motion respects reduced motion and hidden tabs", async ({ page }, testInfo) => {
  await page.clock.install();
  await page.emulateMedia({ reducedMotion: "reduce" });
  const { errors } = await arrangeRequestProgress(page);
  await page.clock.pauseAt(await page.evaluate(() => Date.now() + 1000));
  await startRequestProgress(page);
  const avatar = page.getByRole("img", { name: "Azure FinOps assistant, working", exact: true });
  const orbit = avatar.locator(".assistant-avatar-orbit");
  await expect(avatar).toBeVisible();
  await expect(orbit).toHaveCSS("animation-name", "none");
  expect(await avatar.evaluate((element) => element.getAnimations({ subtree: true }).length)).toBe(0);
  await page.screenshot({
    path: testInfo.outputPath("assistant-avatar-reduced-motion.png"),
    animations: "disabled",
  });
  await page.emulateMedia({ reducedMotion: "no-preference" });
  await expect(orbit).not.toHaveCSS("animation-name", "none");
  await expect(orbit).toHaveCSS("animation-duration", "12s");
  await expect(orbit).toHaveCSS("animation-play-state", "running");
  await page.evaluate(() => {
    Object.defineProperty(document, "hidden", { configurable: true, get: () => true });
    document.dispatchEvent(new Event("visibilitychange"));
  });
  await expect(avatar).toHaveClass(/assistant-avatar--paused/);
  await expect(orbit).toHaveCSS("animation-play-state", "paused");
  await expect(orbit).toHaveCSS("transition-duration", "0s");
  expect(await avatar.evaluate((element) => element.getAnimations({ subtree: true }).every((animation) => animation.playState === "paused"))).toBeTruthy();
  await page.clock.runFor(2000);
  await expect(avatar).toHaveCSS("width", "32px");
  await expect(avatar).toHaveCSS("height", "32px");
  await page.evaluate(() => {
    Object.defineProperty(document, "hidden", { configurable: true, get: () => false });
    document.dispatchEvent(new Event("visibilitychange"));
  });
  await expect(avatar).not.toHaveClass(/assistant-avatar--paused/);
  await expect(orbit).toHaveCSS("animation-play-state", "running");
  await page.emulateMedia({ reducedMotion: "reduce" });
  await expect(orbit).toHaveCSS("animation-name", "none");
  await page.evaluate(() => window.__finishCostRetry());
  await expect(avatar).toHaveCount(0);
  await page.emulateMedia({ reducedMotion: "no-preference" });
  await expect(page.locator(".assistant-avatar-orbit")).toHaveCSS("animation-name", "none");
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  expect(errors).toEqual([]);
});

test("cost cooldown remains visible after automatic retries are exhausted", async ({
  page,
}, testInfo) => {
  await page.clock.install({ time: new Date("2026-01-01T00:00:00Z") });
  const answer =
    "The requested resource costs are still unavailable; no detail amounts were inferred.";
  const { errors } = await arrange(page, [
    {
      type: "tool_start",
      tool: "QueryAzure",
      id: "synthetic-cost-query",
      args: JSON.stringify({ path: "/providers/Microsoft.CostManagement/query" }),
    },
    {
      type: "cooling_down",
      tool: "azure",
      url: "/providers/Microsoft.CostManagement/query",
      attempt: 2,
      status: 429,
      waitSeconds: 300,
      retryAtUtc: "2026-01-01T00:05:00.000Z",
      willRetry: false,
    },
    {
      type: "tool_done",
      tool: "QueryAzure",
      id: "synthetic-cost-query",
      success: true,
      result: "HTTP 429 TooManyRequests\nRetry later.",
    },
    { type: "message", content: answer },
  ]);
  await page.clock.pauseAt(await page.evaluate(() => Date.now() + 1000));
  await send(page, "I need detailed breakdown");
  const card = page.getByRole("group", { name: "Request progress", exact: true });
  await expect(page.getByText(answer, { exact: true })).toBeVisible();
  await expect(card).toBeVisible();
  await expect(card).toHaveAttribute("data-phase", "stopped");
  await expect(card).toContainText("No further automatic retries for this turn. Retry after");
  await expect(card.locator("time")).toHaveAttribute("datetime", "2026-01-01T00:05:00.000Z");
  await expect(card.getByRole("progressbar")).toHaveCount(0);
  await expect(card.locator(".request-progress-cloud")).toHaveCSS("animation-name", "none");
  await expect(page.locator("textarea")).toBeEnabled();
  if (testInfo.project.name === "desktop") {
    await expect(
      page.locator(".st-name").filter({ hasText: "Throttled (HTTP 429)" }),
    ).toBeVisible();
    await expect(page.locator(".st-icon--ok")).toHaveCount(0);
  }
  await page.screenshot({
    path: testInfo.outputPath("cost-cooldown-final.png"),
    animations: "disabled",
  });
  await page.clock.fastForward(300000);
  await expect(card).toHaveAttribute("data-phase", "stopped");
  await expect(card).toContainText("You can submit a new request when this turn finishes.");
  await expect(card.locator(".request-progress-countdown")).toHaveCount(0);
  await expect(card).not.toContainText("retrying automatically");
  await expect(card).not.toContainText("Waiting for the retry response");
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  expect(errors).toEqual([]);
});

test("cost retry shows its deadline while waiting and clears after the final answer", async ({
  page,
}, testInfo) => {
  await page.clock.install();
  const { errors } = await arrangeRequestProgress(page);
  await page.clock.pauseAt(await page.evaluate(() => Date.now() + 1000));
  await startRequestProgress(page);
  const card = page.getByRole("group", { name: "Request progress", exact: true });
  const announcement = card.getByRole("status");
  const deadline = await page.evaluate(() => window.__progressDeadline);
  await expect(card).toContainText("then retrying automatically");
  await expect(card).toContainText("Azure rate-limits its shared billing service. We honor its retry deadline.");
  await expect(card.locator("time")).toHaveAttribute("datetime", deadline);
  await expect(card.locator(".request-progress-countdown-value")).toHaveText("37s");
  await expect(card.getByRole("progressbar")).toHaveAttribute("aria-valuenow", "0");
  const initialAnnouncement = await announcement.textContent();
  await page.clock.runFor(2000);
  await expect(card.locator(".request-progress-countdown-value")).toHaveText("35s");
  await expect(card.getByRole("progressbar")).toHaveAttribute("aria-valuenow", "5");
  await expect(announcement).toHaveText(initialAnnouncement);
  await expect(card.locator(".request-progress-timing")).toHaveAttribute("aria-live", "off");
  await expect(page.locator(".action-btn--stop")).toBeVisible();
  if (testInfo.project.name === "desktop") {
    await expect(page.locator(".st-row--cooler .st-time")).toHaveText("35s");
    await expect(page.locator(".st-row--cooler")).toHaveCSS("animation-name", "none");
    await expect(page.locator(".st-row--cooler animateTransform")).toHaveCount(0);
  }
  await page.screenshot({
    path: testInfo.outputPath("cost-retry-waiting.png"),
    animations: "disabled",
  });
  await page.clock.runFor(35000);
  await expect(card).toHaveAttribute("data-phase", "retry-wait");
  await expect(announcement).toHaveText("Waiting for the retry response");
  await expect(card.locator(".request-progress-countdown-value")).toHaveText("0s");
  await expect(card.getByRole("progressbar")).toHaveCount(0);
  await expect(page.locator(".st-icon--ok")).toHaveCount(0);
  await expect(page.locator(".action-btn--stop")).toBeVisible();
  if (testInfo.project.name === "desktop")
    await expect(page.locator(".st-row--cooler .st-time")).toHaveText("Waiting");
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  await page.screenshot({
    path: testInfo.outputPath("cost-retry-response-pending.png"),
    animations: "disabled",
  });
  await page.evaluate(() => window.__finishCostRetry());
  await expect(
    page.getByText("The resource breakdown is available after retrying.", {
      exact: true,
    }),
  ).toBeVisible();
  await expect(card).toHaveCount(0);
  await expect(page.locator(".system-notice")).toHaveCount(0);
  await expect(page.locator(".action-btn--stop")).toHaveCount(0);
  expect(await page.evaluate(() => window.__progressRequestCount)).toBe(1);
  expect(errors).toEqual([]);
});

test("cost retry countdown advances with the real shared clock", async ({ page }) => {
  const { errors } = await arrangeRequestProgress(page);
  await startRequestProgress(page);
  const card = page.getByRole("group", { name: "Request progress", exact: true });
  const countdown = card.locator(".request-progress-countdown-value");
  const initial = Number.parseInt(await countdown.textContent(), 10);
  const deadline = await card.locator("time").getAttribute("datetime");
  expect(initial).toBeGreaterThan(30);
  await expect.poll(async () => Number.parseInt(await countdown.textContent(), 10)).toBeLessThan(initial);
  await expect(card.locator("time")).toHaveAttribute("datetime", deadline);
  expect(Number(await card.getByRole("progressbar").getAttribute("aria-valuenow"))).toBeGreaterThan(0);
  expect(await page.evaluate(() => window.__progressRequestCount)).toBe(1);
  await page.evaluate(() => window.__finishCostRetry());
  await expect(card).toHaveCount(0);
  expect(errors).toEqual([]);
});

for (const status of [503, 0]) {
  test(`request progress distinguishes HTTP ${status || "no status"} from billing throttling`, async ({ page }, testInfo) => {
    const { errors } = await arrangeRequestProgress(page, { status, waitSeconds: 20 });
    await startRequestProgress(page);
    const card = page.getByRole("group", { name: "Request progress", exact: true });
    await expect(card).toHaveAttribute("data-phase", status ? "cooldown" : "slow");
    await expect(card).not.toContainText("Azure rate-limits");
    await expect(card).not.toContainText("HTTP 0");
    if (status) {
      await expect(card).toContainText("temporarily unavailable (HTTP 503)");
      await expect(card.getByRole("progressbar")).toBeVisible();
    } else {
      await expect(card).toContainText("The request is still running.");
      await expect(card).toContainText("No retry countdown has been supplied");
      await expect(card.getByRole("progressbar")).toHaveCount(0);
      await expect(card.locator(".request-progress-countdown, time")).toHaveCount(0);
      if (testInfo.project.name === "desktop")
        await expect(page.locator(".st-row--cooler .st-time")).toHaveText("Waiting");
    }
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
    await page.screenshot({
      path: testInfo.outputPath(`request-progress-${status}.png`),
      animations: "disabled",
    });
    await page.evaluate((status) => window.__finishCostRetry(status || 200), status);
    await expect(card).toHaveCount(0);
    if (status)
      await expect(page.locator(".st-icon--ok")).toHaveCount(0);
    expect(errors).toEqual([]);
  });
}

test("request progress honors an extended deadline without restarting requests", async ({ page }) => {
  await page.clock.install();
  const { errors } = await arrangeRequestProgress(page);
  await page.clock.pauseAt(await page.evaluate(() => Date.now() + 1000));
  await startRequestProgress(page);
  await page.clock.runFor(2000);
  const deadline = await page.evaluate(() => {
    const retryAtUtc = new Date(Date.now() + 120000).toISOString();
    window.__emitRequestProgress({ retryAtUtc, waitSeconds: 1 });
    return retryAtUtc;
  });
  const card = page.getByRole("group", { name: "Request progress", exact: true });
  await expect(card.locator("time")).toHaveAttribute("datetime", deadline);
  await expect(card.locator(".request-progress-countdown-value")).toHaveText("2m 00s");
  await expect(card.getByRole("progressbar")).toHaveAttribute("aria-valuenow", "0");
  await page.clock.runFor(2000);
  await expect(card.locator(".request-progress-countdown-value")).toHaveText("1m 58s");
  await expect(card.locator("time")).toHaveAttribute("datetime", deadline);
  expect(await page.evaluate(() => window.__progressRequestCount)).toBe(1);
  await page.evaluate(() => window.__finishCostRetry());
  await expect(card).toHaveCount(0);
  expect(errors).toEqual([]);
});

test("request progress disables reduced motion and pauses in hidden tabs", async ({ page }, testInfo) => {
  await page.clock.install();
  await page.emulateMedia({ reducedMotion: "no-preference" });
  const { errors } = await arrangeRequestProgress(page);
  await page.clock.pauseAt(await page.evaluate(() => Date.now() + 1000));
  await startRequestProgress(page);
  const card = page.getByRole("group", { name: "Request progress", exact: true });
  const cloud = card.locator(".request-progress-cloud");
  const fill = card.locator(".request-progress-fill");
  const deadline = await card.locator("time").getAttribute("datetime");
  await expect(cloud).not.toHaveCSS("animation-name", "none");
  await expect(cloud).toHaveCSS("animation-play-state", "running");
  await expect(fill).toHaveCSS("transition-duration", "0.25s");
  await page.emulateMedia({ reducedMotion: "reduce" });
  await expect(cloud).toHaveCSS("animation-name", "none");
  await expect(fill).toHaveCSS("transition-duration", "0s");
  expect(await card.evaluate((element) => element.getAnimations({ subtree: true }).length)).toBe(0);
  await page.screenshot({
    path: testInfo.outputPath("request-progress-reduced-motion.png"),
    animations: "disabled",
  });
  await page.emulateMedia({ reducedMotion: "no-preference" });
  await page.evaluate(() => {
    Object.defineProperty(document, "hidden", { configurable: true, get: () => true });
    document.dispatchEvent(new Event("visibilitychange"));
  });
  await expect(card).toHaveClass(/request-progress--paused/);
  await expect(cloud).toHaveCSS("animation-play-state", "paused");
  await expect(fill).toHaveCSS("transition-duration", "0s");
  expect(await card.evaluate((element) => element.getAnimations({ subtree: true }).every((animation) => animation.playState === "paused"))).toBeTruthy();
  await page.clock.setSystemTime(await page.evaluate(() => Date.now() + 60000));
  await page.evaluate(() => {
    Object.defineProperty(document, "hidden", { configurable: true, get: () => false });
    document.dispatchEvent(new Event("visibilitychange"));
  });
  await expect(card).not.toHaveClass(/request-progress--paused/);
  await expect(cloud).toHaveCSS("animation-play-state", "running");
  await expect(card).toHaveAttribute("data-phase", "retry-wait");
  await expect(card.locator(".request-progress-countdown-value")).toHaveText("0s");
  await expect(card.locator("time")).toHaveAttribute("datetime", deadline);
  await expect(card.getByRole("progressbar")).toHaveCount(0);
  expect(await page.evaluate(() => window.__progressRequestCount)).toBe(1);
  await page.evaluate(() => window.__finishCostRetry());
  await expect(card).toHaveCount(0);
  expect(errors).toEqual([]);
});

test("request progress terminal stop stays still while the tool completes", async ({ page }, testInfo) => {
  await page.clock.install();
  const { errors } = await arrangeRequestProgress(page, { status: 503, willRetry: false, waitSeconds: 5 });
  await page.clock.pauseAt(await page.evaluate(() => Date.now() + 1000));
  await startRequestProgress(page);
  const card = page.getByRole("group", { name: "Request progress", exact: true });
  await expect(card).toHaveAttribute("data-phase", "stopped");
  await expect(card.getByRole("progressbar")).toHaveCount(0);
  await expect(card.locator(".request-progress-cloud")).toHaveCSS("animation-name", "none");
  if (testInfo.project.name === "desktop")
    await expect(page.locator(".st-row--cooler .st-time")).toHaveText("Stopped");
  await page.clock.runFor(6000);
  await expect(card).toHaveAttribute("data-phase", "stopped");
  await expect(card).not.toContainText("retrying automatically");
  await expect(card).not.toContainText("Waiting for the retry response");
  await expect(page.locator(".st-icon--ok")).toHaveCount(0);
  await page.evaluate(() => window.__finishCostRetry(503));
  await expect(page.locator(".action-btn--stop")).toHaveCount(0);
  await expect(card).toHaveAttribute("data-phase", "stopped");
  await expect(page.getByText("The requested resource costs are still unavailable; no detail amounts were inferred.", { exact: true })).toBeVisible();
  expect(errors).toEqual([]);
});

test("request progress safely renders service text without HTML", async ({ page }) => {
  const { errors } = await arrange(page, []);
  await page.route("**/api/chat", (route) => route.fulfill({
    contentType: "text/event-stream",
    body: [
      { type: "session", id: sessionId },
      { type: "cooling_down", status: 503, waitSeconds: 20, willRetry: false, tool: '<img src=x onerror="window.__progressInjected=true">' },
      { type: "message", content: "The request could not complete." },
    ].map((event) => `data: ${JSON.stringify(event)}\n\n`).join("") + "data: [DONE]\n\n",
  }));
  await send(page, "Read the request status");
  const card = page.getByRole("group", { name: "Request progress", exact: true });
  await expect(card).toContainText('<img src=x onerror="window.__progressInjected=true">');
  await expect(card.locator("img")).toHaveCount(0);
  expect(await page.evaluate(() => window.__progressInjected)).toBeUndefined();
  expect(errors).toEqual([]);
});

test("consent actions cannot navigate to tool-supplied external URLs", async ({
  page,
}) => {
  await arrange(page, [
    {
      type: "consent_required",
      actions: [
        { label: "ignored", href: "/auth/microsoft?tier=loganalytics" },
        { label: "unsafe", href: "https://attacker.invalid/" },
      ],
    },
    { type: "delta", content: "Log Analytics consent is required." },
  ]);
  await send(page, "List the Syslog machines");
  await expect(
    page.getByRole("link", { name: "Grant Log Analytics access" }),
  ).toHaveAttribute("href", "/auth/microsoft?tier=loganalytics");
  await expect(page.locator('a[href^="https://attacker.invalid"]')).toHaveCount(
    0,
  );
});

test("HTML report preview preserves contrast and stays isolated", async ({
  page,
}, testInfo) => {
  const { errors } = await arrange(page, [
    { type: "delta", content: "The report is ready." },
    {
      type: "html_ready",
      fileId: artifactId,
      fileName: "synthetic.html",
      slideCount: "2 rows (HTML)",
    },
  ]);
  await page.route("**/api/download/html/**", (route) =>
    route.fulfill({
      contentType: "text/html",
      body: '<!doctype html><html><head><style>body{background:#f7f4ef;color:#242424;margin:24px;font:14px sans-serif}table{width:100%}</style></head><body><h1>Synthetic report</h1><label for="filter">Filter rows</label><input id="filter"><table><tbody><tr><td>Jan</td><td>30</td></tr><tr><td>Feb</td><td>30</td></tr></tbody></table><script>parent.document.body.dataset.compromised="true"</script></body></html>',
    }),
  );
  await send(page, "Make an HTML report");
  await page.locator(".html-deck-card-btn--preview").last().click();
  const iframe = page.locator(".deck-preview-frame");
  await expect(iframe).toBeVisible();
  await expect(iframe).toHaveAttribute("sandbox", "");
  const frame = page.frameLocator(".deck-preview-frame");
  await expect(frame.locator("h1")).toHaveText("Synthetic report");
  await expect(frame.locator("body")).toHaveCSS(
    "background-color",
    "rgb(247, 244, 239)",
  );
  await expect(frame.locator("body")).toHaveCSS("color", "rgb(36, 36, 36)");
  await expect(frame.locator("input, script")).toHaveCount(0);
  expect(
    await page.locator("body").getAttribute("data-compromised"),
  ).toBeNull();
  await page.screenshot({
    path: testInfo.outputPath("html-preview.png"),
    animations: "disabled",
  });
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBeTruthy();
  expect(errors).toEqual([]);
});

test("approval requires explicit acknowledgement and sends only the opaque identifier", async ({
  page,
}, testInfo) => {
  const { errors } = await arrange(page, [
    { type: "approval_required", change },
    {
      type: "delta",
      content: "The change is awaiting your review. No write was sent.",
    },
  ]);
  await send(page, "Apply this tag");
  await page.locator(".change-review summary").click();
  const approve = page.getByRole("button", {
    name: "Approve change",
    exact: true,
  });
  await expect(approve).toBeDisabled();
  await page
    .getByRole("checkbox", {
      name: "I reviewed this change and its potential charges",
    })
    .check();
  await expect(approve).toBeEnabled();
  await page.screenshot({
    path: testInfo.outputPath("approval.png"),
    animations: "disabled",
  });
  const sent = page.waitForRequest((request) =>
    request.url().endsWith(`/api/changes/${operationId}/approve`),
  );
  await approve.click();
  expect((await sent).postDataJSON()).toEqual({ acknowledgeCostImpact: true });
  await expect(page.locator(".change-review summary")).toContainText(
    "accepted",
  );
  await expect(
    page.getByRole("button", { name: "Check operation" }),
  ).toBeVisible();
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBeTruthy();
  expect(errors).toEqual([]);
});

test("new conversation does not throw or restore unrelated proposals", async ({
  page,
}) => {
  const { errors } = await arrange(page, [
    { type: "delta", content: "Synthetic answer." },
  ]);
  const removedFiles = [];
  page.on("request", (request) => {
    if (
      request.method() === "DELETE" &&
      request.url().includes("/api/uploads/")
    )
      removedFiles.push(request.url());
  });
  await page.route("**/api/upload", (route) =>
    route.fulfill({
      json: {
        files: [
          {
            ok: true,
            fileId: "111111111111",
            fileName: "synthetic.csv",
            kind: "csv",
            sizeBytes: 7,
          },
        ],
      },
    }),
  );
  await page
    .locator('input[type="file"]')
    .first()
    .setInputFiles({
      name: "synthetic.csv",
      mimeType: "text/csv",
      buffer: Buffer.from("cost\n1\n"),
    });
  await expect(
    page.getByText("synthetic.csv", { exact: true }).first(),
  ).toBeVisible();
  await send(page, "Synthetic question");
  await expect(
    page.getByText("Synthetic answer.", { exact: true }),
  ).toBeVisible();
  await page.getByTitle("Clear chat", { exact: true }).click();
  await expect(page.locator("textarea")).toBeEnabled();
  await expect(page.locator(".change-review")).toHaveCount(0);
  expect(removedFiles).toEqual([]);
  expect(errors).toEqual([]);
});
