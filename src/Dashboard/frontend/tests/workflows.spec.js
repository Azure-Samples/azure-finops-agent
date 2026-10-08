import { expect, test } from "@playwright/test";
import { pricingSections } from "../src/data/sidebarCategories.js";

const sessionId = "synthetic-conversation";
const artifactId = "11111111111111111111111111111111";
const requestId = "synthetic-approval-request";
const change = {
  requestId,
  tool: "ApplyAzureChange",
  method: "PATCH",
  target: "/synthetic/resource/with-a-long-name-for-mobile-layout",
  body: '{"tags":{"Owner":"Synthetic team"}}',
  status: "awaitingApproval",
};
const firstQuestion = pricingSections[0].prompts[0];

async function arrange(
  page,
  chatEvents,
  history = { messages: [] },
  options = {},
) {
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
  await page.route("**/auth/azure/**", (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === "/auth/azure/status" && options.azureConnected) {
      return route.fulfill({
        json: {
          connected: true,
          user: { email: "synthetic@example.test" },
          subscriptions: [],
          managementGroups: [],
          apis: [],
        },
      });
    }
    return route.fulfill({ json: { connected: false, tenants: [] } });
  });
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
        json: options.version || { sha: "test", build: "test", branch: "test" },
      });
    if (path === "/api/config") return route.fulfill({ json: {} });
    if (path === "/api/models")
      return route.fulfill({ json: { models: [], defaultModel: "synthetic" } });
    if (path === "/api/sessions/new")
      return route.fulfill({ json: { sessionId } });
    if (
      route.request().method() === "DELETE" &&
      path.startsWith("/api/sessions/") &&
      options.deleteSession
    )
      return options.deleteSession(route);
    if (path === "/api/sessions")
      return route.fulfill({
        json: {
          sessions: options.sessions || [],
          currentSessionId: options.currentSessionId || sessionId,
        },
      });
    if (path.endsWith("/messages")) return route.fulfill({ json: history });
    if (path.endsWith("/active"))
      return route.fulfill({
        json: options.active ? options.active() : { active: false },
      });
    if (path === "/api/chat/stop" && options.stopTurn)
      return options.stopTurn(route);
    if (path === "/api/jobs")
      return route.fulfill({
        json: { jobs: options.jobs || [], entraRequired: !options.jobs },
      });
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

test("top bar shows the Open source link and the build without repeating the product name or a personal contact link", async ({ page }) => {
  const { errors } = await arrange(page, [], undefined, {
    version: { sha: "abc1234", build: "158", branch: "main" },
  });
  const wide = page.viewportSize().width > 600;
  // The start page already names the product.
  await expect(page.locator(".portal-header")).not.toContainText("Azure FinOps Agent");
  const link = page.locator(
    '.portal-trustline-link[href="https://github.com/Azure-Samples/azure-finops-agent"]',
  );
  await expect(link).toBeVisible();
  await expect(link).toHaveAttribute("target", "_blank");
  // Phones keep the icon; the label shows wherever it fits.
  if (wide) await expect(link.getByText("Open source")).toBeVisible();
  const badge = page.locator(".portal-build-badge");
  await expect(badge).toBeVisible();
  await expect(badge).not.toHaveClass(/portal-build-badge--preview/);
  await expect(badge).toContainText("Build 158");
  if (wide) await expect(badge).toContainText("main");
  await expect(page.locator('a[href*="linkedin.com"]')).toHaveCount(0);
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBeTruthy();
  expect(errors).toEqual([]);
});

test("a preview branch highlights its build in the top bar", async ({ page }) => {
  await arrange(page, [], undefined, {
    version: { sha: "abc1234", build: "42", branch: "feature-x" },
  });
  const badge = page.locator(".portal-build-badge--preview");
  await expect(badge).toBeVisible();
  await expect(badge).toContainText("Build 42");
});

test("navigation exposes one New chat and the start page offers the starter questions", async ({
  page,
}, testInfo) => {
  const { requests, errors } = await arrange(page, [
    { type: "message", content: "Synthetic pricing answer." },
  ]);
  const newChat = page.getByRole("button", { name: "New chat" });
  await expect(page.locator(".portal-new-chat")).toHaveCount(0);

  if (testInfo.project.name === "desktop") {
    await expect(newChat).toHaveCount(1);
    await expect(page.locator(".sidebar-new-chat")).toBeVisible();
    await page.locator(".portal-burger").click();
    await expect(newChat).toHaveCount(0);
    await page.locator(".portal-burger").click();
    await expect(newChat).toHaveCount(1);
  } else {
    await expect(newChat).toHaveCount(0);
    await page.locator(".portal-burger").click();
    await expect(newChat).toHaveCount(1);
    await expect(page.locator(".sidebar-new-chat")).toBeVisible();
  }

  // Signed out, the menu is New chat and Connect Azure: no prompt library, no
  // empty chat history and no tenant box (Switch tenant changes it later).
  await expect(page.locator("#sidebar-prompts")).toHaveCount(0);
  await expect(page.locator("#sidebar-chat-history")).toHaveCount(0);
  await expect(page.locator(".tenant-input")).toHaveCount(0);
  await expect(
    page.locator("#chat-navigation").getByRole("button", { name: "Connect Azure" }),
  ).toBeVisible();
  if (testInfo.project.name === "mobile")
    await page.locator(".portal-burger").click();

  // The start page keeps the brand title and shows each section's five
  // questions as cards; phones show three, with More showing the rest in place.
  await expect(page.locator(".hero-title")).toHaveText("Azure FinOps Agent");
  await expect(page.locator(".starters-heading--section")).toHaveText(
    pricingSections.map((section) => section.label),
  );
  const mobile = testInfo.project.name === "mobile";
  const visibleLabels = (key) =>
    page
      .locator(`#starters-${key} .starter-question:visible`)
      .allTextContents()
      .then((labels) => labels.map((label) => label.trim()));
  const shown = mobile ? 3 : 5;
  for (const section of pricingSections)
    expect(await visibleLabels(section.key)).toEqual(
      section.prompts.slice(0, shown).map((p) => p.label),
    );
  await expect(page.getByText("Browse all prompts")).toHaveCount(0);
  await expect(page.locator(".input-notice")).toHaveText(
    "AI-generated answers can be wrong. Check important figures.",
  );
  await page.screenshot({
    path: testInfo.outputPath("start-page-starters.png"),
    animations: "disabled",
  });

  const more = page.locator(
    `#starters-${pricingSections[0].key} .starter-more`,
  );
  if (shown < pricingSections[0].prompts.length) {
    await expect(more).toHaveText("More questions");
    await expect(more).toHaveAttribute("aria-expanded", "false");
    await more.click();
    await expect(more).toHaveText("Fewer questions");
    await expect(more).toHaveAttribute("aria-expanded", "true");
    expect(await visibleLabels(pricingSections[0].key)).toEqual(
      pricingSections[0].prompts.map((p) => p.label),
    );
    await more.click();
    await expect(more).toHaveText("More questions");
    expect((await visibleLabels(pricingSections[0].key)).length).toBe(shown);
  } else await expect(more).toBeHidden();

  await page
    .locator(`#starters-${pricingSections[0].key} .starter-question`)
    .first()
    .click();
  await expect.poll(() => requests.length).toBe(1);
  expect(requests[0].prompt).toBe(firstQuestion.prompt);
  expect(errors).toEqual([]);
});
test("signed in, jobs and chats sit in the right rail and the maturity levels in the navigation", async ({
  page,
}, testInfo) => {
  const conversation = {
    id: "synthetic-chat",
    summary: "Quarterly cost review",
    modified: new Date().toISOString(),
    started: new Date().toISOString(),
  };
  const { requests, errors } = await arrange(
    page,
    [{ type: "message", content: "Synthetic Crawl answer." }],
    { messages: [] },
    {
      azureConnected: true,
      sessions: [conversation],
      currentSessionId: "other-session",
      jobs: [
        {
          id: "synthetic-job",
          name: "Daily cost digest",
          enabled: true,
          lastStatus: "ok",
          intervalMinutes: 1440,
          nextRunUtc: "2099-01-01T00:00:00Z",
        },
      ],
    },
  );
  const mobile = testInfo.project.name === "mobile";
  const navigation = page.locator("#chat-navigation");
  const rail = page.locator(".tools-sidebar");

  // The start page shows the question sections only; the maturity levels live
  // in the navigation with their stars.
  await expect(page.locator(".starters-heading--section")).toHaveText(
    pricingSections.map((section) => section.label),
  );
  await expect(page.getByText("Score your FinOps maturity")).toHaveCount(0);

  if (mobile) await page.locator(".portal-burger").click();
  const levels = navigation.locator(".maturity-card");
  await expect(levels).toHaveCount(3);
  await expect(levels.locator(".maturity-card-label")).toHaveText(["Crawl", "Walk", "Run"]);
  await expect(levels.first().locator(".maturity-card-stars")).toHaveAttribute(
    "aria-label",
    "Not scored",
  );

  const top = async (locator) => (await locator.boundingBox()).y;
  const promptsToggle = navigation.getByRole("button", { name: "Prompts" });
  const holder = mobile ? navigation : rail;
  const newJob = holder.getByRole("button", { name: "New job" });
  const chats = holder.locator("#sidebar-chat-history");
  await expect(newJob).toBeVisible();
  await expect(chats.locator(".session-row-title")).toHaveText(["Quarterly cost review"]);
  await expect(page.getByText("No jobs yet", { exact: false })).toHaveCount(0);
  if (mobile) {
    // Phones keep everything in the menu: New chat, jobs, maturity, prompts, chats.
    await expect(rail).toBeHidden();
    expect(await top(newJob)).toBeGreaterThan(await top(page.locator(".sidebar-new-chat")));
    expect(await top(levels.first())).toBeGreaterThan(await top(newJob));
    expect(await top(promptsToggle)).toBeGreaterThan(await top(levels.last()));
    expect(await top(chats)).toBeGreaterThan(await top(promptsToggle));
  } else {
    // Wide screens: your jobs and chats on the right, things to start on the left.
    await expect(rail).toBeVisible();
    await expect(navigation.getByRole("button", { name: "New job" })).toHaveCount(0);
    await expect(navigation.locator("#sidebar-chat-history")).toHaveCount(0);
    expect(await top(chats)).toBeGreaterThan(await top(newJob));
    expect(await top(promptsToggle)).toBeGreaterThan(await top(levels.last()));
  }

  // One "⋯" menu per job instead of four hover icons.
  await holder.getByRole("button", { name: "Actions for Daily cost digest" }).click();
  const menu = page.getByRole("menu", { name: "Actions for Daily cost digest" });
  await expect(menu).toBeVisible();
  await expect(menu.getByRole("menuitem")).toHaveText(["Run now", "Edit", "Delete"]);
  await expect(menu.getByRole("menuitemcheckbox")).toHaveText("Pause");
  await page.keyboard.press("Escape");
  await expect(menu).toHaveCount(0);

  // Prompts start open on wide screens and closed in the phone menu.
  await expect(promptsToggle).toHaveAttribute(
    "aria-expanded",
    mobile ? "false" : "true",
  );
  if (mobile) await promptsToggle.click();
  const crawlPrompts = navigation
    .locator(".prompt-group-toggle")
    .filter({ hasText: /^Crawl/ });
  await expect(crawlPrompts).toHaveAttribute("aria-expanded", "false");
  await crawlPrompts.click();
  await expect(crawlPrompts).toHaveAttribute("aria-expanded", "true");
  await page.screenshot({
    path: testInfo.outputPath("navigation-signed-in.png"),
    animations: "disabled",
  });

  // A maturity level scores itself with one click.
  await levels.first().click();
  await expect.poll(() => requests.length).toBe(1);
  expect(requests[0].prompt).toContain("Crawl");
  expect(errors).toEqual([]);
});
test("execution sidebar is empty-chat hidden and opens for tool activity", async ({
  page,
}, testInfo) => {
  const { errors } = await arrange(page, [
    {
      type: "tool_start",
      tool: "QueryAzure",
      id: "synthetic-tool",
      args: "{}",
    },
    {
      type: "tool_done",
      tool: "QueryAzure",
      id: "synthetic-tool",
      success: true,
      result: "{}",
    },
    { type: "message", content: "Synthetic answer." },
  ]);
  await expect(page.locator(".tools-sidebar")).toBeHidden();
  await send(page, "Show my costs");
  const evidence = page.locator(".answer-evidence");
  await expect(evidence).toHaveText("1 call");
  if (testInfo.project.name === "desktop") {
    await expect(page.locator(".tools-sidebar")).toBeVisible();
    await expect(page.locator(".tools-sidebar-title")).toHaveText(
      "Agent activity",
    );
    await expect(page.locator(".tools-sidebar-status")).toHaveCount(0);
    // The rail stays while it has something to show; there is nothing to hide.
    await expect(page.getByRole("button", { name: "Hide agent activity" })).toHaveCount(0);
    await expect(page.getByRole("button", { name: "Agent activity", exact: true })).toHaveCount(0);
    // An answer's call count opens its question's calls in the rail.
    await page.locator(".st-group-head").first().click();
    await expect(page.locator(".st-group--open")).toHaveCount(0);
    await evidence.click();
    await expect(page.locator(".tools-sidebar .st-group--open")).toHaveCount(1);
  } else {
    // Phones have no rail; the answer's call count opens it over the chat.
    await expect(page.locator(".tools-sidebar")).toBeHidden();
    await expect(page.getByRole("button", { name: "Agent activity", exact: true })).toHaveCount(0);
    await evidence.click();
    const sheet = page.locator(".tools-sidebar--overlay");
    await expect(sheet).toBeVisible();
    await expect(sheet.locator(".st-group--open")).toHaveCount(1);
    await expect(page.getByRole("button", { name: "Hide agent activity" })).toBeFocused();
    await page.keyboard.press("Escape");
    await expect(page.locator(".tools-sidebar")).toBeHidden();
    await expect(evidence).toBeFocused();
    await evidence.click();
    // Tapping the dimmed strip beside the sheet closes it too.
    await page
      .getByRole("button", { name: "Close agent activity" })
      .click({ position: { x: 8, y: 200 } });
    await expect(page.locator(".tools-sidebar")).toBeHidden();
  }
  expect(errors).toEqual([]);
});

test("conversation deletion stays stable until the server confirms it", async ({
  page,
}, testInfo) => {
  let deleteAttempts = 0;
  let releaseFailedDelete;
  const failedDelete = new Promise((resolve) => {
    releaseFailedDelete = resolve;
  });
  const conversation = {
    id: "conversation-to-delete",
    summary: "Quarterly cost review",
    modified: "2026-09-22T12:00:00Z",
    started: "2026-09-22T11:00:00Z",
  };
  const { errors } = await arrange(
    page,
    [],
    { messages: [] },
    {
      azureConnected: true,
      sessions: [conversation],
      currentSessionId: conversation.id,
      deleteSession: async (route) => {
        deleteAttempts++;
        if (deleteAttempts === 1) {
          await failedDelete;
          return route.fulfill({ status: 500, json: { error: "synthetic" } });
        }
        return route.fulfill({ status: 204 });
      },
    },
  );

  if (testInfo.project.name === "mobile")
    await page.locator(".portal-burger").click();

  const row = page.locator(
    `.session-row[data-session-id="${conversation.id}"]`,
  );
  await expect(row).toBeVisible();
  await row.getByRole("button", { name: "Delete conversation" }).click();
  await expect(
    row.getByRole("button", { name: "Deleting conversation" }),
  ).toBeDisabled();
  await expect(row).toHaveCount(1);

  releaseFailedDelete();
  await expect(page.getByRole("alert")).toHaveText(
    "Couldn't delete the conversation. Try again.",
  );
  await expect(row).toHaveCount(1);
  await page.screenshot({
    path: testInfo.outputPath("conversation-delete-error.png"),
    animations: "disabled",
  });
  await row.getByRole("button", { name: "Delete conversation" }).click();
  await expect(row).toHaveCount(0);
  await expect(page.locator("#sidebar-chat-history")).toHaveCount(0);
  expect(deleteAttempts).toBe(2);
  expect(errors).toEqual([]);
});

test("signed in, chats are grouped by day and searchable once the list is long", async ({
  page,
}, testInfo) => {
  const now = Date.now();
  const at = (days) => new Date(now - days * 86400000).toISOString();
  const sessions = [
    ["s1", "Why did my VM costs rise?", 0],
    ["s2", "Cheapest region for a D4s v5", 1],
    ["s3", "Budget guard setup", 3],
    ["s4", "Reservation review", 20],
    ["s5", "Storage tier savings", 25],
    ["s6", "Tag coverage report", 30],
  ].map(([id, summary, days]) => ({ id, summary, modified: at(days), started: at(days) }));
  const { errors } = await arrange(page, [], { messages: [] }, {
    azureConnected: true,
    sessions,
    currentSessionId: "s1",
  });
  if (testInfo.project.name === "mobile")
    await page.locator(".portal-burger").click();

  const history = page.locator("#sidebar-chat-history");
  await expect(history.locator(".session-group-label")).toHaveText([
    "Today",
    "Yesterday",
    "Previous 7 days",
    "Older",
  ]);
  await expect(history.locator(".session-row-title")).toHaveText(
    sessions.map((s) => s.summary),
  );
  const search = history.getByRole("searchbox", { name: "Search chats" });
  await search.fill("REGION");
  await expect(history.locator(".session-row-title")).toHaveText([
    "Cheapest region for a D4s v5",
  ]);
  await expect(history.locator(".session-group-label")).toHaveText(["Yesterday"]);
  await search.fill("nothing like this");
  await expect(history.locator(".session-row")).toHaveCount(0);
  await expect(history.locator(".chat-search-empty")).toHaveText(
    'No chats match "nothing like this"',
  );
  expect(errors).toEqual([]);
});

test("the latest answer offers a deck and a script, and the message box keeps only Attach and Send", async ({
  page,
}, testInfo) => {
  await page.context().grantPermissions(["clipboard-read", "clipboard-write"]);
  const { requests, errors } = await arrange(page, [
    { type: "message", content: "Synthetic answer with recommendations." },
  ]);
  const actions = page.locator(".answer-actions");
  await expect(actions).toHaveCount(0);
  await expect(page.locator(".question-edit")).toHaveCount(0);

  await send(page, "Synthetic question");
  await expect(page.getByText("Synthetic answer with recommendations.")).toBeVisible();
  await expect(actions).toHaveCount(1);
  await expect(actions.getByRole("button")).toHaveText([
    "Copy",
    "",
    "",
    "Make a deck",
    "Write a script",
  ]);
  const composer = page.locator(".input-area");
  for (const name of ["Clear chat", "Presentation", "Script"])
    await expect(composer.getByRole("button", { name })).toHaveCount(0);
  await expect(composer.getByRole("button", { name: "Attach" })).toBeVisible();
  await page.screenshot({
    path: testInfo.outputPath("answer-actions.png"),
    animations: "disabled",
  });

  // Copy takes the answer as written and confirms it briefly.
  await actions.getByRole("button", { name: "Copy" }).click();
  await expect(actions.getByRole("button", { name: "Copied" })).toBeVisible();
  expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(
    "Synthetic answer with recommendations.",
  );

  // A rating is saved against the question it answers; pressing it again clears it.
  const good = actions.getByRole("button", { name: "Good answer" });
  const rated = page.waitForRequest(
    (request) => request.method() === "POST" && request.url().endsWith(`/api/sessions/${sessionId}/feedback`),
  );
  await good.click();
  expect((await rated).postDataJSON()).toEqual({ turn: 1, rating: "up" });
  await expect(good).toHaveAttribute("aria-pressed", "true");
  await expect(actions.getByRole("button", { name: "Poor answer" })).toHaveAttribute("aria-pressed", "false");
  const cleared = page.waitForRequest(
    (request) => request.method() === "POST" && request.url().endsWith("/feedback"),
  );
  await good.click();
  expect((await cleared).postDataJSON()).toEqual({ turn: 1, rating: "none" });
  await expect(good).toHaveAttribute("aria-pressed", "false");

  // Edit puts the latest question back in the message box without sending it.
  await page.locator(".question-edit").click();
  await expect(page.locator("textarea")).toHaveValue("Synthetic question");
  expect(requests).toHaveLength(1);
  await page.locator("textarea").fill("");

  await actions.getByRole("button", { name: "Write a script" }).click();
  await expect.poll(() => requests.length).toBe(2);
  expect(requests[1].prompt).toContain("generate an Azure CLI script");
  // Every answer can be copied and rated; the deck and script follow only the latest.
  await expect(actions).toHaveCount(2);
  await expect(actions.first().getByRole("button")).toHaveText(["Copy", "", ""]);
  await expect(actions.last().getByRole("button")).toHaveText([
    "Copy",
    "",
    "",
    "Make a deck",
    "Write a script",
  ]);
  await expect(page.locator(".question-edit")).toHaveCount(1);
  expect(errors).toEqual([]);
});

test("a maturity score updates the navigation's level, not the answer", async ({
  page,
}, testInfo) => {
  const scores = [
    { id: "visibility", label: "Cost visibility", score: 4, detail: "Cost data reviewed monthly." },
    { id: "tagging", label: "Tagging", score: 2, detail: "Owner tags on 40% of resources." },
    { id: "budgets", label: "Budgets", score: null, status: "unknown", detail: "Budget reader role missing." },
  ];
  const { errors } = await arrange(page, [
    {
      type: "tool_start",
      tool: "ReportMaturityScore",
      id: "score-call",
      args: JSON.stringify({ level: "crawl", scores }),
    },
    { type: "tool_done", tool: "ReportMaturityScore", id: "score-call", success: true, result: "{}" },
    { type: "maturity_score", level: "crawl", scores },
    { type: "message", content: "Your Crawl maturity is 3 of 5." },
  ], { messages: [] }, { azureConnected: true });
  await send(page, "Score my Crawl maturity");
  await expect(page.locator(".message-text").last()).toContainText(
    "Your Crawl maturity is 3 of 5.",
  );
  // The navigation's Crawl row holds the score; the answer does not repeat it.
  await expect(page.locator(".ai-content .assessment-row")).toHaveCount(0);
  if (testInfo.project.name === "mobile")
    await page.locator(".portal-burger").click();
  const crawl = page.locator("#chat-navigation .maturity-card").first();
  await expect(crawl.locator(".maturity-card-stars")).toHaveAttribute(
    "aria-label",
    "3 out of 5",
  );
  await expect(crawl.locator(".maturity-card-cta")).toHaveText("Re-score");
  expect(errors).toEqual([]);
});
test("execution sidebar groups calls under each question with a summary", async ({
  page,
}, testInfo) => {
  test.skip(
    testInfo.project.name !== "desktop",
    "The execution sidebar is hidden on compact layouts.",
  );
  const prices = JSON.stringify({
    url: "https://prices.azure.com/api/retail/prices?$filter=serviceName eq 'Foundry Models'",
  });
  await page.addInitScript((sid) => {
    sessionStorage.setItem("finops_last_session", sid);
  }, sessionId);
  const { errors } = await arrange(page, [], {
    messages: [
      { role: "user", content: "Compare the newest GPT-6 prices" },
      {
        role: "assistant",
        content: "| Model | Input USD/1M |\n|---|--:|\n| 6-sol | 2 |",
        toolCalls: [
          { id: "price-1", name: "QueryAzure", args: prices, result: "HTTP 200\n{}", success: true, durationMs: 66100 },
        ],
      },
      { role: "user", content: "Show the latest published benchmarks" },
      {
        role: "assistant",
        content: "Synthetic benchmark answer.",
        toolCalls: [
          { id: "search-1", name: "web_search", args: JSON.stringify({ queries: ["GPT-6 benchmarks"] }), result: "{}", success: true, durationMs: 1300 },
          { id: "page-1", name: "QueryAzure", args: JSON.stringify({ url: "https://leaderboard.example.test/models" }), result: "HTTP 404\nNot found", success: false, durationMs: 197 },
          { id: "price-2", name: "QueryAzure", args: prices, result: "HTTP 200\n{}", success: true, durationMs: 95 },
        ],
      },
    ],
  });

  const groups = page.locator(".st-group");
  await expect(groups).toHaveCount(2);
  const latest = groups.nth(0);
  await expect(latest.locator(".st-group-q")).toHaveText("Q2");
  await expect(latest.locator(".st-group-text")).toHaveText(
    "Show the latest published benchmarks",
  );
  await expect(latest.locator(".st-group-head")).toHaveAttribute(
    "aria-expanded",
    "true",
  );
  await expect(latest.locator(".st-group-meta")).toContainText("3 calls");
  await expect(latest.locator(".st-group-meta")).toContainText("1 failed");
  await expect(latest.locator(".st-kind")).toHaveText([
    "Web search 1",
    "Web page 1",
    "Pricing 1",
  ]);
  await expect(latest.locator(".st-row")).toHaveCount(3);
  await expect(latest.locator(".st-row").first()).toContainText(
    "Web search · GPT-6 benchmarks",
  );
  await expect(latest.locator(".st-row").first()).toContainText("1.3s");

  const earlier = groups.nth(1);
  await expect(earlier.locator(".st-group-q")).toHaveText("Q1");
  await expect(earlier.locator(".st-group-head")).toHaveAttribute(
    "aria-expanded",
    "false",
  );
  await expect(earlier.locator(".st-row")).toBeHidden();
  await earlier.locator(".st-group-head").click();
  await expect(earlier.locator(".st-group-head")).toHaveAttribute(
    "aria-expanded",
    "true",
  );
  await expect(earlier.locator(".st-row")).toContainText("66.1s");
  await expect(page.locator('[data-msg-index="0"]')).toBeInViewport();
  await page.screenshot({
    path: testInfo.outputPath("execution-groups.png"),
    animations: "disabled",
  });
  expect(errors).toEqual([]);
});

test("answer tables keep labels left, figures right and every column reachable", async ({
  page,
}, testInfo) => {
  const table = [
    "Benchmarks retrieved for the newest priced Foundry models.",
    "",
    "| Model | Quality (index) | Output speed tok/s | Latency s | Global Standard USD/1M input / cached / output |",
    "|---|--:|--:|--:|---|",
    "| GPT-6 Astra | 53 | 54 | 341.88 | 10 / 1 / 50 |",
    "| `6-luna` | 38 | 131 | 128.78 | 0.1 / 0.01 / 0.5 |",
    "| Grok 4.6 | unknown | unknown | unknown | 2 / 0.5 / 10 |",
    "| DeepSeek V4 Pro | 36 | 107 | 1.75 | 1.74 / 0.14 / 3.48 |",
  ].join("\n");
  const { errors } = await arrange(page, [{ type: "message", content: table }]);
  await send(page, "Show the latest benchmarks");
  const wrap = page.locator(".message-text .wt-wrap");
  await expect(wrap).toBeVisible();

  const firstCell = wrap.locator("tbody tr").first().locator("td").first();
  await expect(firstCell).toHaveText("GPT-6 Astra");
  await expect(firstCell).not.toHaveCSS("text-align", "right");
  await expect(firstCell).toHaveCSS("position", "sticky");
  await expect(
    wrap.locator("tbody tr").first().locator("td").nth(1),
  ).toHaveCSS("text-align", "right");
  const identifier = wrap.locator("code.wt-id", { hasText: "6-luna" });
  const box = await identifier.boundingBox();
  expect(box.height).toBeLessThan(30);

  const lastHeader = wrap.locator("th").last();
  await lastHeader.scrollIntoViewIfNeeded();
  await expect(lastHeader).toBeInViewport();
  if (testInfo.project.name === "desktop") {
    const fits = await wrap.evaluate((el) => el.scrollWidth <= el.clientWidth + 1);
    expect(fits).toBeTruthy();
  }
  expect(
    await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth),
  ).toBeTruthy();
  await page.screenshot({
    path: testInfo.outputPath("answer-table.png"),
    animations: "disabled",
  });
  expect(errors).toEqual([]);
});

test("a conversation reloaded mid-answer offers Stop and records the stop", async ({
  page,
}, testInfo) => {
  let running = true;
  let stops = 0;
  const startedUtc = new Date(Date.now() - 95_000).toISOString();
  await page.addInitScript((sid) => {
    sessionStorage.setItem("finops_last_session", sid);
  }, sessionId);
  const { errors } = await arrange(
    page,
    [],
    { messages: [{ role: "user", content: "Run a Crawl assessment" }] },
    {
      active: () =>
        running
          ? { active: true, startedUtc, toolsCompleted: 14, scheduled: false }
          : { active: false },
      stopTurn: (route) => {
        stops++;
        running = false;
        return route.fulfill({
          json: { stopped: true, alreadyCompleted: false, abortPending: false },
        });
      },
    },
  );

  const stop = page.locator(".action-btn--stop");
  await expect(stop).toBeVisible();
  const notice = page.locator(".session-notice");
  await expect(notice).toContainText("14 tool calls finished");
  await expect(notice).toContainText("Press Stop to cancel it.");
  await page.screenshot({
    path: testInfo.outputPath("reloaded-turn-stop.png"),
    animations: "disabled",
  });
  await stop.click();
  await expect(
    page.getByText("You stopped this response before it finished.", {
      exact: true,
    }),
  ).toHaveCount(1);
  await expect(stop).toHaveCount(0);
  await expect(page.locator("textarea")).toBeEnabled();
  await expect(notice).toHaveCount(0);
  // The recovery poller must not add a second marker on a later tick.
  await page.waitForTimeout(4500);
  await expect(
    page.getByText("You stopped this response before it finished.", {
      exact: true,
    }),
  ).toHaveCount(1);
  expect(stops).toBe(1);
  expect(errors).toEqual([]);
});

test("a send that bounces as busy attaches to the running turn", async ({
  page,
}) => {
  let running = true;
  const { errors } = await arrange(
    page,
    [
      {
        type: "busy",
        sessionId,
        message: "I'm still working on your previous question — one moment.",
      },
    ],
    { messages: [] },
    {
      active: () =>
        running
          ? {
              active: true,
              startedUtc: new Date().toISOString(),
              toolsCompleted: 2,
              scheduled: false,
            }
          : { active: false },
      stopTurn: (route) => {
        running = false;
        return route.fulfill({ json: { stopped: true } });
      },
    },
  );
  await page.locator("textarea").fill("What did we spend last month?");
  await page.locator("textarea").press("Enter");
  const stop = page.locator(".action-btn--stop");
  await expect(stop).toBeVisible();
  await expect(page.locator(".session-notice")).toContainText(
    "2 tool calls finished",
  );
  await expect(page.locator("textarea")).toHaveValue(
    "What did we spend last month?",
  );
  await stop.click();
  await expect(stop).toHaveCount(0);
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

test("an ARM template is labelled as a template rather than a Bash script", async ({
  page,
}) => {
  const { errors } = await arrange(page, [
    { type: "message", content: "The ARM template is ready." },
    {
      type: "script_ready",
      fileId: artifactId,
      fileName: "synthetic-template.json",
      lineCount: 12,
      language: "arm",
      description: "Synthetic ARM template",
      content: '{ "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#", "contentVersion": "1.0.0.0", "resources": [] }',
    },
  ]);
  await send(page, "give me the ARM template file");
  const meta = page.locator(".script-meta").last();
  await expect(meta).toHaveText(/12 lines\s*·\s*ARM template/);
  await expect(page.locator(".script-filename").last()).toHaveText(
    "synthetic-template.json",
  );
  await expect(page.getByText("Bash", { exact: true })).toHaveCount(0);
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

test("tool validation errors remain failures when the SDK callback succeeded", async ({
  page,
}) => {
  const { requests, errors } = await arrange(page, [
    {
      type: "tool_start",
      tool: "QueryAzure",
      id: "invalid-calculation",
      args: '{"query":"new { monthly = nope * 730 }"}',
    },
    {
      type: "tool_done",
      tool: "QueryAzure",
      id: "invalid-calculation",
      success: true,
      result: "Error: the calculation failed: Unknown identifier 'nope'",
    },
    { type: "message", content: "The calculation input was rejected." },
  ]);
  await send(page, "Calculate the monthly estimate");
  await expect(
    page.getByText("The calculation input was rejected.", { exact: true }),
  ).toBeVisible();
  await expect(page.locator(".st-icon--ok")).toHaveCount(0);
  await expect(page.locator(".st-icon--fail")).toHaveCount(1);
  expect(requests).toHaveLength(1);
  expect(errors).toEqual([]);
});

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

test("reloading a failed terminal turn does not claim the answer is still generating", async ({
  page,
}) => {
  await arrange(page, []);
  await page.route("**/auth/azure/status", (route) =>
    route.fulfill({
      json: { connected: true, tenants: [], subscriptions: [] },
    }),
  );
  await page.route("**/api/sessions", (route) =>
    route.fulfill({
      json: {
        sessions: [
          {
            id: sessionId,
            summary: "Synthetic failed request",
            modified: new Date().toISOString(),
          },
        ],
        currentSessionId: sessionId,
      },
    }),
  );
  await page.route(`**/api/sessions/${sessionId}/messages`, (route) =>
    route.fulfill({
      json: {
        messages: [{ role: "user", content: "Synthetic failed request" }],
      },
    }),
  );
  await page.route(`**/api/sessions/${sessionId}/outcomes`, (route) =>
    route.fulfill({
      json: {
        outcomes: [
          {
            status: "error",
            startedUtc: "2026-01-01T00:00:00Z",
            completedUtc: "2026-01-01T00:00:05Z",
          },
        ],
      },
    }),
  );
  await page.evaluate(
    (session) => sessionStorage.setItem("finops_last_session", session),
    sessionId,
  );
  await page.reload({ waitUntil: "domcontentloaded" });
  await expect(page.getByRole("alert")).toContainText(
    "This turn has ended and is no longer generating.",
  );
  await expect(
    page.getByText("Reconnecting — your last answer is still being generated", {
      exact: false,
    }),
  ).toHaveCount(0);
  await expect(page.locator("textarea")).toBeEnabled();
});

test("a restored model failure keeps its reason and lets the user edit without resending", async ({
  page,
}, testInfo) => {
  const prompt = "Show spending for the last seven days";
  const { requests, errors } = await arrange(page, [], {
    messages: [
      { role: "user", content: prompt },
      {
        role: "system",
        terminalStatus: "error",
        content:
          "Authentication failed with provider at https://synthetic.invalid/openai/v1/ (HTTP 401). Check your COPILOT_PROVIDER_API_KEY.",
      },
    ],
  });
  await page.route("**/auth/azure/status", (route) =>
    route.fulfill({
      json: { connected: true, tenants: [], subscriptions: [] },
    }),
  );
  await page.route("**/api/sessions", (route) =>
    route.fulfill({
      json: {
        sessions: [
          {
            id: sessionId,
            summary: prompt,
            modified: new Date().toISOString(),
          },
        ],
        currentSessionId: sessionId,
      },
    }),
  );
  await page.evaluate(
    (session) => sessionStorage.setItem("finops_last_session", session),
    sessionId,
  );
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
  await page.screenshot({
    path: testInfo.outputPath("restored-model-failure.png"),
  });
});

test("a live failure preserves partial answers and never overwrites a new draft", async ({
  page,
}) => {
  const prompt = "Explain the synthetic costs";
  const table = "| Service | Cost |\n|---|---|\n| Synthetic compute | USD 25 |";
  const { requests, errors } = await arrange(page, [
    { type: "message", messageId: "partial-answer", content: table },
    {
      type: "error",
      message: "Authentication failed with provider (HTTP 401)",
    },
  ]);
  await send(page, prompt);
  await expect(page.locator(".message-text table")).toContainText("USD 25");
  const failure = page.getByRole("alert");
  await expect(failure).toContainText("AI model access is blocked");
  await expect(page.locator(".streaming-cursor")).toHaveCount(0);
  await page.locator("textarea").fill("Keep my new draft");
  await expect(
    failure.getByRole("button", { name: "Edit saved question" }),
  ).toBeDisabled();
  await expect(page.locator("textarea")).toHaveValue("Keep my new draft");
  await page.locator("textarea").fill("");
  await failure.getByRole("button", { name: "Edit saved question" }).click();
  await expect(page.locator("textarea")).toHaveValue(prompt);
  expect(requests).toHaveLength(1);
  expect(errors).toEqual([]);
});

test("compact navigation is fully hidden when closed and aligned below the header when open", async ({
  page,
}) => {
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
        window.__progressRequestCount =
          (window.__progressRequestCount || 0) + 1;
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
                  args: JSON.stringify({
                    path: "/providers/Microsoft.CostManagement/query",
                  }),
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
                  retryAtUtc:
                    event.retryAtUtc ||
                    new Date(
                      Date.now() + (event.waitSeconds ?? 37) * 1000,
                    ).toISOString(),
                };
                window.__progressDeadline = progress.retryAtUtc;
                emit(progress);
                window.__emitRequestProgress = (update) =>
                  emit({ ...progress, ...update });
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
                    content:
                      status === 200
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
  await expect(
    page.getByRole("group", { name: "Request progress", exact: true }),
  ).toBeVisible();
}

// Scripted SSE turn: tests push events one at a time through
// window.__activity.emit() and end the turn with window.__activity.finish().
async function arrangeActivityStream(page) {
  await page.addInitScript((session) => {
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
              const emit = (data) =>
                controller.enqueue(
                  encoder.encode(`data: ${JSON.stringify(data)}\n\n`),
                );
              emit({ type: "session", id: session });
              window.__activity = {
                emit,
                finish(content) {
                  if (content) emit({ type: "message", content });
                  controller.enqueue(encoder.encode("data: [DONE]\n\n"));
                  controller.close();
                },
              };
            },
          }),
          { headers: { "content-type": "text/event-stream" } },
        ),
      );
    };
  }, sessionId);
  return arrange(page, []);
}

async function startActivityTurn(page, prompt = "Why did my costs rise?") {
  await expect(page.locator("textarea")).toBeEnabled();
  await expect(page.locator(".action-btn--stop")).toHaveCount(0);
  await page.locator("textarea").fill(prompt);
  await page.locator("textarea").press("Enter");
  await expect(page.locator(".action-btn--stop")).toBeVisible();
  await page.waitForFunction(() => !!window.__activity);
}

const emitActivity = (page, data) =>
  page.evaluate((event) => window.__activity.emit(event), data);

test("activity row narrates tools and reasoning, then gives way to the answer", async ({
  page,
}, testInfo) => {
  const browserErrors = [];
  page.on("console", (message) => {
    if (message.type() === "error") browserErrors.push(message.text());
  });
  page.on("requestfailed", (request) =>
    browserErrors.push(request.failure()?.errorText),
  );
  await page.emulateMedia({ reducedMotion: "no-preference" });
  const { errors } = await arrangeActivityStream(page);
  await startActivityTurn(page);
  const activity = page.locator(".stream-activity");
  const label = activity.locator(".activity-label");
  const dots = activity.getByRole("img", { name: "Working", exact: true });

  await expect(label).toHaveText("Thinking");
  await expect(dots).toBeVisible();
  await expect(dots.locator("i").first()).toHaveCSS(
    "animation-name",
    /^activity-pulse/,
  );
  await expect(dots.locator("i").first()).toHaveCSS(
    "animation-play-state",
    "running",
  );
  await expect(label).toHaveCSS("font-family", /Google Sans Flex/);
  await expect(page.locator(".assistant-avatar, .streaming-cursor")).toHaveCount(
    0,
  );
  await expect(activity.locator("[aria-live]")).toHaveCount(0);
  const stopBox = await page.locator(".action-btn--stop").boundingBox();
  expect(stopBox.x + stopBox.width).toBeLessThanOrEqual(
    page.viewportSize().width,
  );

  await emitActivity(page, {
    type: "tool_start",
    tool: "QueryAzure",
    id: "cost",
    args: JSON.stringify({
      url: "/subscriptions/00000000-0000-0000-0000-000000000000/providers/Microsoft.CostManagement/query?api-version=2025-03-01",
    }),
  });
  await expect(label).toHaveText("Querying Cost Management");
  await emitActivity(page, {
    type: "tool_start",
    tool: "QueryAzure",
    id: "price",
    args: {
      url: "https://prices.azure.com/api/retail/prices?$filter=armSkuName eq 'Standard_D4s_v5'",
    },
  });
  await expect(label).toHaveText(
    "Checking retail prices for Standard_D4s_v5 and 1 more",
  );
  for (const id of ["cost", "price"])
    await emitActivity(page, {
      type: "tool_done",
      tool: "QueryAzure",
      id,
      success: true,
      result: "HTTP 200\n{}",
    });

  await emitActivity(page, {
    type: "reasoning",
    content:
      "**Comparing month over month**\n\nCompute grew after the new scale set.",
  });
  await expect(label).toHaveText("Comparing month over month");
  const thinking = page.locator(".ai-row--live .thinking");
  const toggle = thinking.getByRole("button", { name: "Thinking" });
  await expect(toggle).toHaveAttribute("aria-expanded", "true");
  await expect(page.locator(".reasoning-panel")).toContainText(
    "Compute grew after the new scale set.",
  );
  await expect(page.locator(".reasoning-panel strong")).toHaveText(
    "Comparing month over month",
  );
  await toggle.click();
  await expect(toggle).toHaveAttribute("aria-expanded", "false");
  await expect(page.locator(".reasoning-panel")).toHaveCount(0);
  await toggle.click();
  await expect(toggle).toHaveAttribute("aria-expanded", "true");
  await page.screenshot({
    path: testInfo.outputPath("activity-thinking.png"),
    animations: "disabled",
  });

  await emitActivity(page, { type: "delta", content: "Costs rose 12% " });
  await expect(page.locator(".message-row--ai .message-text")).toContainText(
    "Costs rose 12%",
  );
  await expect(activity).toHaveCount(0);
  // Once answer text starts, live reasoning swoops closed to its toggle.
  await expect(toggle).toHaveAttribute("aria-expanded", "false");
  await expect(page.locator(".reasoning-panel")).toHaveCount(0);
  await toggle.click();
  await expect(toggle).toHaveAttribute("aria-expanded", "true");
  await expect(page.locator(".reasoning-panel")).toContainText(
    "Compute grew after the new scale set.",
  );
  const thinkingBox = await thinking.boundingBox();
  const answerBox = await page
    .locator(".ai-row--live .message-text")
    .boundingBox();
  expect(thinkingBox.y).toBeLessThan(answerBox.y);

  await emitActivity(page, {
    type: "tool_start",
    tool: "RenderChart",
    id: "chart",
    args: {},
  });
  await expect(label).toHaveText("Drawing the chart");
  const textBox = await page
    .locator(".ai-row--live .message-text")
    .boundingBox();
  const activityBox = await activity.boundingBox();
  expect(activityBox.y).toBeGreaterThan(textBox.y);
  await page.screenshot({
    path: testInfo.outputPath("activity-after-text.png"),
    animations: "disabled",
  });
  await emitActivity(page, {
    type: "tool_done",
    tool: "RenderChart",
    id: "chart",
    success: true,
    result: "{}",
  });
  await page.evaluate(() =>
    window.__activity.finish("Costs rose 12% because compute grew."),
  );
  await expect(page.locator(".action-btn--stop")).toHaveCount(0);
  await expect(page.locator(".activity")).toHaveCount(0);
  const reply = page.locator(".message-row--ai .ai-row").last();
  await expect(reply).toContainText("Costs rose 12% because compute grew.");
  const finalToggle = reply.locator(".thinking .reasoning-toggle");
  await expect(finalToggle).toHaveAttribute("aria-expanded", "false");
  await expect(reply.locator(".thinking .reasoning-panel")).toHaveCount(0);
  await finalToggle.click();
  await expect(reply.locator(".thinking .reasoning-panel")).toContainText(
    "Compute grew after the new scale set.",
  );
  await expect
    .poll(() =>
      reply.evaluate(
        (element) => element.getAnimations({ subtree: true }).length,
      ),
    )
    .toBe(0);
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBeTruthy();
  expect(errors).toEqual([]);
  expect(browserErrors).toEqual([]);
});

test("activity motion respects reduced motion and hidden tabs", async ({
  page,
}, testInfo) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  const { errors } = await arrangeActivityStream(page);
  await startActivityTurn(page);
  await emitActivity(page, {
    type: "tool_start",
    tool: "QueryAzure",
    id: "graph",
    args: {
      url: "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2024-04-01",
    },
  });
  const activity = page.locator(".stream-activity");
  const dot = activity.locator(".activity-dots i").first();
  const label = activity.locator(".activity-label");
  await expect(label).toHaveText(/^Querying /);
  await expect(dot).toHaveCSS("animation-name", "none");
  await expect(label).toHaveCSS("animation-name", "none");
  expect(
    await activity.evaluate(
      (element) => element.getAnimations({ subtree: true }).length,
    ),
  ).toBe(0);
  await page.screenshot({
    path: testInfo.outputPath("activity-reduced-motion.png"),
    animations: "disabled",
  });

  await page.emulateMedia({ reducedMotion: "no-preference" });
  await expect(dot).toHaveCSS("animation-name", /^activity-pulse/);
  await expect(dot).toHaveCSS("animation-duration", "1.2s");
  await expect(dot).toHaveCSS("animation-play-state", "running");
  const setHidden = (hidden) =>
    page.evaluate((value) => {
      Object.defineProperty(document, "hidden", {
        configurable: true,
        get: () => value,
      });
      document.dispatchEvent(new Event("visibilitychange"));
    }, hidden);
  await setHidden(true);
  await expect(activity).toHaveClass(/activity--paused/);
  await expect(dot).toHaveCSS("animation-play-state", "paused");
  expect(
    await activity.evaluate((element) =>
      element
        .getAnimations({ subtree: true })
        .every((animation) => animation.playState === "paused"),
    ),
  ).toBeTruthy();
  await setHidden(false);
  await expect(activity).not.toHaveClass(/activity--paused/);
  await expect(dot).toHaveCSS("animation-play-state", "running");

  await emitActivity(page, {
    type: "tool_done",
    tool: "QueryAzure",
    id: "graph",
    success: true,
    result: "HTTP 200\n{}",
  });
  await page.evaluate(() => window.__activity.finish("42 resources found."));
  await expect(page.locator(".action-btn--stop")).toHaveCount(0);
  await expect(page.locator(".activity")).toHaveCount(0);
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBeTruthy();
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
      args: JSON.stringify({
        path: "/providers/Microsoft.CostManagement/query",
      }),
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
  const card = page.getByRole("group", {
    name: "Request progress",
    exact: true,
  });
  await expect(page.getByText(answer, { exact: true })).toBeVisible();
  await expect(card).toBeVisible();
  await expect(card).toHaveAttribute("data-phase", "stopped");
  await expect(card).toContainText(
    "No further automatic retries for this turn. Retry after",
  );
  await expect(card.locator("time")).toHaveAttribute(
    "datetime",
    "2026-01-01T00:05:00.000Z",
  );
  await expect(card.getByRole("progressbar")).toHaveCount(0);
  await expect(card.locator(".request-progress-cloud")).toHaveCSS(
    "animation-name",
    "none",
  );
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
  await expect(card).toContainText(
    "You can submit a new request when this turn finishes.",
  );
  await expect(card.locator(".request-progress-countdown")).toHaveCount(0);
  await expect(card).not.toContainText("retrying automatically");
  await expect(card).not.toContainText("Waiting for the retry response");
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBeTruthy();
  expect(errors).toEqual([]);
});

test("cost retry shows its deadline while waiting and clears after the final answer", async ({
  page,
}, testInfo) => {
  await page.clock.install();
  const { errors } = await arrangeRequestProgress(page);
  await page.clock.pauseAt(await page.evaluate(() => Date.now() + 1000));
  await startRequestProgress(page);
  const card = page.getByRole("group", {
    name: "Request progress",
    exact: true,
  });
  const announcement = card.getByRole("status");
  const deadline = await page.evaluate(() => window.__progressDeadline);
  await expect(card).toContainText("then retrying automatically");
  await expect(card).toContainText(
    "Azure rate-limits its shared billing service. We honor its retry deadline.",
  );
  await expect(card.locator("time")).toHaveAttribute("datetime", deadline);
  await expect(card.locator(".request-progress-countdown-value")).toHaveText(
    "37s",
  );
  await expect(card.getByRole("progressbar")).toHaveAttribute(
    "aria-valuenow",
    "0",
  );
  const initialAnnouncement = await announcement.textContent();
  await page.clock.runFor(2000);
  await expect(card.locator(".request-progress-countdown-value")).toHaveText(
    "35s",
  );
  await expect(card.getByRole("progressbar")).toHaveAttribute(
    "aria-valuenow",
    "5",
  );
  await expect(announcement).toHaveText(initialAnnouncement);
  await expect(card.locator(".request-progress-timing")).toHaveAttribute(
    "aria-live",
    "off",
  );
  await expect(page.locator(".action-btn--stop")).toBeVisible();
  if (testInfo.project.name === "desktop") {
    await expect(page.locator(".st-row--cooler .st-time")).toHaveText("35s");
    await expect(page.locator(".st-row--cooler")).toHaveCSS(
      "animation-name",
      "none",
    );
    await expect(page.locator(".st-row--cooler animateTransform")).toHaveCount(
      0,
    );
  }
  await page.screenshot({
    path: testInfo.outputPath("cost-retry-waiting.png"),
    animations: "disabled",
  });
  await page.clock.runFor(35000);
  await expect(card).toHaveAttribute("data-phase", "retry-wait");
  await expect(announcement).toHaveText("Waiting for the retry response");
  await expect(card.locator(".request-progress-countdown-value")).toHaveText(
    "0s",
  );
  await expect(card.getByRole("progressbar")).toHaveCount(0);
  await expect(page.locator(".st-icon--ok")).toHaveCount(0);
  await expect(page.locator(".action-btn--stop")).toBeVisible();
  if (testInfo.project.name === "desktop")
    await expect(page.locator(".st-row--cooler .st-time")).toHaveText(
      "Waiting",
    );
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBeTruthy();
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

test("cost retry countdown advances with the real shared clock", async ({
  page,
}) => {
  const { errors } = await arrangeRequestProgress(page);
  await startRequestProgress(page);
  const card = page.getByRole("group", {
    name: "Request progress",
    exact: true,
  });
  const countdown = card.locator(".request-progress-countdown-value");
  const initial = Number.parseInt(await countdown.textContent(), 10);
  const deadline = await card.locator("time").getAttribute("datetime");
  expect(initial).toBeGreaterThan(30);
  await expect
    .poll(async () => Number.parseInt(await countdown.textContent(), 10))
    .toBeLessThan(initial);
  await expect(card.locator("time")).toHaveAttribute("datetime", deadline);
  expect(
    Number(await card.getByRole("progressbar").getAttribute("aria-valuenow")),
  ).toBeGreaterThan(0);
  expect(await page.evaluate(() => window.__progressRequestCount)).toBe(1);
  await page.evaluate(() => window.__finishCostRetry());
  await expect(card).toHaveCount(0);
  expect(errors).toEqual([]);
});

for (const status of [503, 0]) {
  test(`request progress distinguishes HTTP ${status || "no status"} from billing throttling`, async ({
    page,
  }, testInfo) => {
    const { errors } = await arrangeRequestProgress(page, {
      status,
      waitSeconds: 20,
    });
    await startRequestProgress(page);
    const card = page.getByRole("group", {
      name: "Request progress",
      exact: true,
    });
    await expect(card).toHaveAttribute(
      "data-phase",
      status ? "cooldown" : "slow",
    );
    await expect(card).not.toContainText("Azure rate-limits");
    await expect(card).not.toContainText("HTTP 0");
    if (status) {
      await expect(card).toContainText("temporarily unavailable (HTTP 503)");
      await expect(card.getByRole("progressbar")).toBeVisible();
    } else {
      await expect(card).toContainText("The request is still running.");
      await expect(card).toContainText("No retry countdown has been supplied");
      await expect(card.getByRole("progressbar")).toHaveCount(0);
      await expect(
        card.locator(".request-progress-countdown, time"),
      ).toHaveCount(0);
      if (testInfo.project.name === "desktop")
        await expect(page.locator(".st-row--cooler .st-time")).toHaveText(
          "Waiting",
        );
    }
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth,
      ),
    ).toBeTruthy();
    await page.screenshot({
      path: testInfo.outputPath(`request-progress-${status}.png`),
      animations: "disabled",
    });
    await page.evaluate(
      (status) => window.__finishCostRetry(status || 200),
      status,
    );
    await expect(card).toHaveCount(0);
    if (status) await expect(page.locator(".st-icon--ok")).toHaveCount(0);
    expect(errors).toEqual([]);
  });
}

test("request progress honors an extended deadline without restarting requests", async ({
  page,
}) => {
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
  const card = page.getByRole("group", {
    name: "Request progress",
    exact: true,
  });
  await expect(card.locator("time")).toHaveAttribute("datetime", deadline);
  await expect(card.locator(".request-progress-countdown-value")).toHaveText(
    "2m 00s",
  );
  await expect(card.getByRole("progressbar")).toHaveAttribute(
    "aria-valuenow",
    "0",
  );
  await page.clock.runFor(2000);
  await expect(card.locator(".request-progress-countdown-value")).toHaveText(
    "1m 58s",
  );
  await expect(card.locator("time")).toHaveAttribute("datetime", deadline);
  expect(await page.evaluate(() => window.__progressRequestCount)).toBe(1);
  await page.evaluate(() => window.__finishCostRetry());
  await expect(card).toHaveCount(0);
  expect(errors).toEqual([]);
});

test("request progress disables reduced motion and pauses in hidden tabs", async ({
  page,
}, testInfo) => {
  await page.clock.install();
  await page.emulateMedia({ reducedMotion: "no-preference" });
  const { errors } = await arrangeRequestProgress(page);
  await page.clock.pauseAt(await page.evaluate(() => Date.now() + 1000));
  await startRequestProgress(page);
  const card = page.getByRole("group", {
    name: "Request progress",
    exact: true,
  });
  const cloud = card.locator(".request-progress-cloud");
  const fill = card.locator(".request-progress-fill");
  const deadline = await card.locator("time").getAttribute("datetime");
  await expect(cloud).not.toHaveCSS("animation-name", "none");
  await expect(cloud).toHaveCSS("animation-play-state", "running");
  await expect(fill).toHaveCSS("transition-duration", "0.25s");
  await page.emulateMedia({ reducedMotion: "reduce" });
  await expect(cloud).toHaveCSS("animation-name", "none");
  await expect(fill).toHaveCSS("transition-duration", "0s");
  expect(
    await card.evaluate(
      (element) => element.getAnimations({ subtree: true }).length,
    ),
  ).toBe(0);
  await page.screenshot({
    path: testInfo.outputPath("request-progress-reduced-motion.png"),
    animations: "disabled",
  });
  await page.emulateMedia({ reducedMotion: "no-preference" });
  await page.evaluate(() => {
    Object.defineProperty(document, "hidden", {
      configurable: true,
      get: () => true,
    });
    document.dispatchEvent(new Event("visibilitychange"));
  });
  await expect(card).toHaveClass(/request-progress--paused/);
  await expect(cloud).toHaveCSS("animation-play-state", "paused");
  await expect(fill).toHaveCSS("transition-duration", "0s");
  expect(
    await card.evaluate((element) =>
      element
        .getAnimations({ subtree: true })
        .every((animation) => animation.playState === "paused"),
    ),
  ).toBeTruthy();
  await page.clock.setSystemTime(await page.evaluate(() => Date.now() + 60000));
  await page.evaluate(() => {
    Object.defineProperty(document, "hidden", {
      configurable: true,
      get: () => false,
    });
    document.dispatchEvent(new Event("visibilitychange"));
  });
  await expect(card).not.toHaveClass(/request-progress--paused/);
  await expect(cloud).toHaveCSS("animation-play-state", "running");
  await expect(card).toHaveAttribute("data-phase", "retry-wait");
  await expect(card.locator(".request-progress-countdown-value")).toHaveText(
    "0s",
  );
  await expect(card.locator("time")).toHaveAttribute("datetime", deadline);
  await expect(card.getByRole("progressbar")).toHaveCount(0);
  expect(await page.evaluate(() => window.__progressRequestCount)).toBe(1);
  await page.evaluate(() => window.__finishCostRetry());
  await expect(card).toHaveCount(0);
  expect(errors).toEqual([]);
});

test("request progress terminal stop stays still while the tool completes", async ({
  page,
}, testInfo) => {
  await page.clock.install();
  const { errors } = await arrangeRequestProgress(page, {
    status: 503,
    willRetry: false,
    waitSeconds: 5,
  });
  await page.clock.pauseAt(await page.evaluate(() => Date.now() + 1000));
  await startRequestProgress(page);
  const card = page.getByRole("group", {
    name: "Request progress",
    exact: true,
  });
  await expect(card).toHaveAttribute("data-phase", "stopped");
  await expect(card.getByRole("progressbar")).toHaveCount(0);
  await expect(card.locator(".request-progress-cloud")).toHaveCSS(
    "animation-name",
    "none",
  );
  if (testInfo.project.name === "desktop")
    await expect(page.locator(".st-row--cooler .st-time")).toHaveText(
      "Stopped",
    );
  await page.clock.runFor(6000);
  await expect(card).toHaveAttribute("data-phase", "stopped");
  await expect(card).not.toContainText("retrying automatically");
  await expect(card).not.toContainText("Waiting for the retry response");
  await expect(page.locator(".st-icon--ok")).toHaveCount(0);
  await page.evaluate(() => window.__finishCostRetry(503));
  await expect(page.locator(".action-btn--stop")).toHaveCount(0);
  await expect(card).toHaveAttribute("data-phase", "stopped");
  await expect(
    page.getByText(
      "The requested resource costs are still unavailable; no detail amounts were inferred.",
      { exact: true },
    ),
  ).toBeVisible();
  expect(errors).toEqual([]);
});

test("request progress safely renders service text without HTML", async ({
  page,
}) => {
  const { errors } = await arrange(page, []);
  await page.route("**/api/chat", (route) =>
    route.fulfill({
      contentType: "text/event-stream",
      body:
        [
          { type: "session", id: sessionId },
          {
            type: "cooling_down",
            status: 503,
            waitSeconds: 20,
            willRetry: false,
            tool: '<img src=x onerror="window.__progressInjected=true">',
          },
          { type: "message", content: "The request could not complete." },
        ]
          .map((event) => `data: ${JSON.stringify(event)}\n\n`)
          .join("") + "data: [DONE]\n\n",
    }),
  );
  await send(page, "Read the request status");
  const card = page.getByRole("group", {
    name: "Request progress",
    exact: true,
  });
  await expect(card).toContainText(
    '<img src=x onerror="window.__progressInjected=true">',
  );
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

test("approval requires explicit acknowledgement and answers the held call with the next turn", async ({
  page,
}, testInfo) => {
  const { errors, requests } = await arrange(page, [
    { type: "approval_required", change },
    {
      type: "delta",
      content: "The change is awaiting your review. No write was sent.",
    },
  ]);
  await send(page, "Apply this tag");
  // A plain-language line derived from the exact request sits above it.
  await expect(page.locator(".change-review-summary")).toHaveText(
    "Update tags on with-a-long-name-for-mobile-layout",
  );
  await expect(page.locator(".change-review-target")).toHaveText(change.target);
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
  await approve.click();
  await expect(page.locator(".change-review summary")).toContainText(
    "approved",
  );
  expect(requests).toHaveLength(2);
  expect(requests[0].approval).toBeUndefined();
  expect(requests[1].approval).toEqual({ requestId, approved: true });
  expect(requests[1].prompt).toBe(`Approved: PATCH ${change.target}`);
  await expect(approve).toHaveCount(0);
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBeTruthy();
  expect(errors).toEqual([]);
});

test("a new message instead of a decision rejects the held change", async ({
  page,
}) => {
  const { errors, requests } = await arrange(page, [
    { type: "approval_required", change },
    { type: "delta", content: "The change is awaiting your review." },
  ]);
  await send(page, "Apply this tag");
  await expect(page.locator(".change-review summary")).toContainText(
    "awaiting your approval",
  );
  await send(page, "Actually, show my costs instead");
  expect(requests[1].approval).toBeUndefined();
  await expect(page.locator(".change-review summary")).toContainText(
    "not applied",
  );
  await expect(
    page.getByRole("button", { name: "Approve change", exact: true }),
  ).toHaveCount(0);
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
  const newChat = page.locator(".sidebar-new-chat");
  if (!(await newChat.isVisible())) await page.locator(".portal-burger").click();
  await newChat.click();
  await expect(page.locator("textarea")).toBeEnabled();
  await expect(page.locator(".change-review")).toHaveCount(0);
  expect(removedFiles).toEqual([]);
  expect(errors).toEqual([]);
});
