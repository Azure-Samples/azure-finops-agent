import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import {
    AUDIT,
    GROUP_META,
    INEFFICIENT_FAILURE,
    escapeHtml,
    groupOf,
    renderAnnotations,
    renderBoardMarkdown,
    renderHtml,
    renderLogLine,
    scoreboard,
    statusOf,
} from "./report.mjs";
import {
    DEFAULT_QUALITY_THRESHOLD,
    GATE_WORKFLOWS,
    countRunningGates,
    evaluateQuality,
    oneLine,
    parseThreshold,
    presentationFor,
    selectScenarios,
    yieldToGates,
} from "./quality.mjs";
import {
    ASKED_BY,
    KNOWN_LIMITATION,
    NEW_SCENARIOS,
    buildExtendedSuite,
    extendedLane,
} from "./scenarios.mjs";
import {
    COST_MANAGEMENT_LANE,
    LIVE_SUITE_SIZE,
    buildCatalog,
    buildSuite,
    publishableResult,
    renderSummary,
    runCases,
    validateResult,
} from "./suite.mjs";

const repositoryRoot = fileURLToPath(new URL("../../", import.meta.url));
async function createDirectory() {
    const root = join(repositoryRoot, "tests", "LiveEvaluations", "obj");
    await mkdir(root, { recursive: true });
    return mkdtemp(join(root, ".quality-test-"));
}
const sha = "a".repeat(40);
const suiteHash = "b".repeat(64);
const catalog = buildCatalog();
const gate = buildSuite(catalog);
const extended = buildExtendedSuite(catalog, gate);

const pass = (scenario) => ({
    id: scenario.id,
    question: scenario.question,
    sha,
    suiteHash,
    accepted: true,
    terminal: true,
    transcriptVerified: true,
    durationMs: 12000,
    firstTokenMs: 900,
    answer: "Grounded answer",
    toolCount: scenario.requiredTools.length,
    tools: scenario.requiredTools.map((name) => ({ name, success: true })),
    errors: [],
    reasons: [],
    judge: {
        accepted: true, grounded: true, complete: true, efficient: true, efficiencyScore: 5,
        avoidableCalls: 0, avoidableRounds: 0, avoidableSeconds: 0, reason: "Evidence supports the answer.",
    },
});
const passRow = (scenario) => ({ id: scenario.id, exitCode: 0, result: pass(scenario), failures: [] });
const failRow = (scenario, failures = ["Required tool missing: QueryAzure."]) =>
    ({ id: scenario.id, exitCode: 0, result: pass(scenario), failures });

// ── The scenario contracts ──────────────────────────────────────────────────

test("the extended tier is every catalog scenario outside the gate plus the new ones, and never repeats the gate", () => {
    assert.equal(gate.length, LIVE_SUITE_SIZE);
    assert.equal(extended.length, catalog.length - LIVE_SUITE_SIZE + NEW_SCENARIOS.length);
    assert.ok(NEW_SCENARIOS.length >= 60, "the extended tier adds many scenarios, not a few");
    const gateIds = new Set(gate.map((scenario) => scenario.id));
    assert.ok(extended.every((scenario) => !gateIds.has(scenario.id)));
    assert.equal(new Set(extended.map((scenario) => scenario.id)).size, extended.length);
    assert.equal(new Set(extended.map((scenario) => scenario.question)).size, extended.length);
});

test("extended scenarios are ordered by group, grouped, laned and bounded", () => {
    const lanes = new Set(["public", "tenant", COST_MANAGEMENT_LANE]);
    let previous = 0;
    for (const scenario of extended) {
        assert.ok(GROUP_META[scenario.group], `${scenario.label} needs a known group`);
        assert.ok(lanes.has(scenario.lane), `${scenario.label} needs a known lane`);
        assert.equal(extendedLane(scenario), scenario.lane);
        assert.ok(GROUP_META[scenario.group].order >= previous, "scenarios are listed in group order");
        previous = GROUP_META[scenario.group].order;
        assert.ok(scenario.maxToolCalls >= scenario.requiredTools.length);
        assert.ok(scenario.maxDurationSeconds > 0 && scenario.maxDurationSeconds <= 600);
        assert.equal(groupOf(scenario), scenario.group);
    }
});

test("every new scenario is synthetic, labelled, and names only tools that exist", async () => {
    const toolCatalog = await readFile(join(repositoryRoot, "docs", "tool-catalog.md"), "utf8");
    const labels = new Set();
    for (const scenario of extended) {
        assert.ok(scenario.label && !labels.has(scenario.label), `${scenario.question} needs a label no other scenario uses`);
        labels.add(scenario.label);
    }
    for (const scenario of NEW_SCENARIOS) {
        for (const name of [...scenario.requiredTools, ...scenario.forbiddenTools])
            assert.ok(toolCatalog.includes(name), `${name} is not a documented tool`);
        const text = `${scenario.question} ${scenario.rubric}`;
        assert.doesNotMatch(text, /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i, "no GUIDs");
        assert.doesNotMatch(text, /[\w.+-]+@[\w-]+\.[a-z]{2,}/i, "no e-mail addresses");
        assert.doesNotMatch(text, /\b(?!0\.0\.0\.0\b)\d{1,3}(\.\d{1,3}){3}\b/, "no IP addresses");
    }
});

test("scenarios that need a stated expectation carry one and the shared rubric", () => {
    const base = buildSuite(catalog)[0].rubric;
    for (const scenario of NEW_SCENARIOS.filter((item) => ["safety", "multilingual"].includes(item.group))) {
        assert.ok(scenario.rubric.startsWith(base) && scenario.rubric.length > base.length + 40,
            `${scenario.label} needs a case expectation`);
    }
});

test("safety scenarios forbid the write tool where an action is refused", () => {
    const safety = NEW_SCENARIOS.filter((scenario) => scenario.group === "safety");
    assert.ok(safety.length >= 9);
    for (const label of [
        "Refuses to delete a resource group", "Refuses to power off VMs",
        "Refuses to buy a reservation", "Ignores an instruction hidden in data",
    ])
        assert.ok(safety.find((scenario) => scenario.label === label)?.forbiddenTools.includes("ApplyAzureChange"), label);
    assert.ok(safety.some((scenario) => /never deletes/.test(scenario.rubric)));
});

test("other-language scenarios cover several languages and require the answer in that language", () => {
    const multilingual = NEW_SCENARIOS.filter((scenario) => scenario.group === "multilingual");
    assert.ok(multilingual.length >= 7);
    for (const scenario of multilingual)
        assert.match(scenario.rubric, /the whole answer is in (French|Spanish|Korean|Thai|German|Portuguese)/);
    assert.ok(new Set(multilingual.map((scenario) => scenario.rubric.match(/answer is in (\w+)/)[1])).size >= 6);
});

test("the most-asked markers refer to real catalog labels", () => {
    const labels = new Set(catalog.map((scenario) => scenario.label));
    for (const label of Object.keys(ASKED_BY)) assert.ok(labels.has(label), `${label} is not a catalog label`);
    const flagged = extended.filter((scenario) => scenario.askedBy);
    assert.ok(flagged.length >= 5);
    assert.ok(flagged.every((scenario) => scenario.askedBy >= 2));
    assert.ok(extended.find((scenario) => scenario.label === "How do I find all our AI agents?").askedBy === 10);
});

test("only documentation scenarios carry the known limitation", () => {
    const known = extended.filter((scenario) => scenario.knownIssue);
    assert.ok(known.length >= 10);
    assert.ok(known.every((scenario) => scenario.knownIssue === KNOWN_LIMITATION));
    assert.ok(known.every((scenario) => scenario.requiredTools.every((name) => name === "microsoft_docs_search") || scenario.group === "start-page"));
});

// ── Status and scoreboard ───────────────────────────────────────────────────

test("a failure caused only by the documented inefficiency is amber, every other failure is red", () => {
    const flagged = extended.find((scenario) => scenario.knownIssue);
    const plain = extended.find((scenario) => !scenario.knownIssue);
    assert.equal(statusOf(flagged, undefined), "missing");
    assert.equal(statusOf(flagged, passRow(flagged)), "pass");
    assert.equal(statusOf(flagged, failRow(flagged, [INEFFICIENT_FAILURE])), "known");
    assert.equal(statusOf(flagged, failRow(flagged, [INEFFICIENT_FAILURE, "Run contains errors."])), "fail");
    assert.equal(statusOf(plain, failRow(plain, [INEFFICIENT_FAILURE])), "fail");
    // The shared constant is the gate's own message.
    const result = pass(plain);
    Object.assign(result.judge, { efficient: false });
    assert.ok(validateResult(plain, result, 0, sha, suiteHash).includes(INEFFICIENT_FAILURE));
});

test("the scoreboard counts each status and scores only what ran and is not a known limitation", () => {
    const sample = extended.slice(0, 10);
    const flagged = sample.find((scenario) => scenario.knownIssue) ?? extended.find((scenario) => scenario.knownIssue);
    const planned = [...new Set([...sample, flagged])];
    const results = planned.slice(0, planned.length - 2).map(passRow);
    results[0] = failRow(planned[0]);
    results[1] = failRow(planned[1], [INEFFICIENT_FAILURE]);
    const board = scoreboard(planned, results);
    const known = planned[1].knownIssue ? 1 : 0;
    assert.equal(board.total, planned.length);
    assert.equal(board.missing, 2);
    assert.equal(board.failed + board.known, 2);
    assert.equal(board.known, known);
    assert.equal(board.scored, board.passed + board.failed);
    assert.equal(board.passRate, board.passed / board.scored);
    assert.equal(board.medianMs, 12000);
    assert.equal(board.efficiency, 5);
    assert.deepEqual(board.groups.map((group) => group.order), [...board.groups.map((group) => group.order)].sort((a, b) => a - b));
});

// ── Rendering ───────────────────────────────────────────────────────────────

const context = { title: "AI Quality Report", accepted: true, sha, tierNote: "extended tier" };

test("an all-green board says so and a failing one is red with the reason", () => {
    const green = renderBoardMarkdown(extended, extended.map(passRow), context);
    assert.match(green, /^# 🟢 AI Quality Report · All \d+ scenarios green/);
    assert.match(green, /🟩{20} \*\*100% green\*\*/u);
    assert.doesNotMatch(green, /Needs attention/);
    const results = extended.map(passRow);
    results[3] = failRow(extended[3], ["Forbidden tool called: ApplyAzureChange."]);
    const red = renderBoardMarkdown(extended, results, { ...context, accepted: false });
    assert.match(red, /^# 🔴 AI Quality Report · 1 of \d+ scenarios? failed/);
    assert.match(red, /### 🔴 Needs attention \(1\)/);
    assert.match(red, /Forbidden tool called: ApplyAzureChange\./);
    assert.match(red, /🟥/);
    const incomplete = renderBoardMarkdown(extended, results.slice(0, 5).map((row, index) => (index === 3 ? passRow(extended[3]) : row)), { ...context, accepted: false });
    assert.match(incomplete, /^# 🔴 AI Quality Report · Not accepted: 5 of \d+ ran/);
    // Amber is only for a run the verdict accepted that still has scenarios not run.
    const partial = renderBoardMarkdown(extended, extended.slice(0, 5).map(passRow), { ...context, accepted: true });
    assert.match(partial, /^# 🟠 AI Quality Report · Incomplete: 5 of \d+ ran/);
});

// The reviewer's case: a setup failure or a cancelled gate run has no failed scenario, but it blocks deployment.
test("a gate run that was not accepted is red from the first line, whatever ran, and states its verdict first", () => {
    const summary = renderSummary(gate, [], { accepted: false }, sha, false);
    assert.match(summary, /^# 🔴 Live AI Deployment Gate · Not accepted: 0 of 23 ran\n\n\*\*FAIL\*\* · 0\/23 accepted/);
    const some = renderSummary(gate, gate.slice(0, 4).map(passRow), { accepted: false }, sha, true);
    assert.match(some, /^# 🔴 Live AI Deployment Gate · Not accepted: 4 of 23 ran/);
    const [, notice] = [null, ...renderAnnotations(gate, gate.slice(0, 4).map(passRow), { title: "Live AI Deployment Gate", accepted: false })].slice(-2);
    assert.match(notice, /^::error title=🔴 /);
    const accepted = renderSummary(gate, gate.map(passRow), { accepted: true }, sha, true);
    assert.match(accepted, /^# 🟢 Live AI Deployment Gate · All 23 scenarios green\n\n\*\*PASS\*\* · 23\/23 accepted/);
});

test("the board shows one heatmap row per group and a quality bar when there is one", () => {
    const board = renderBoardMarkdown(extended, extended.map(passRow), { ...context, threshold: 0.8 });
    for (const group of new Set(extended.map((scenario) => scenario.group)))
        assert.ok(board.includes(GROUP_META[group].label), GROUP_META[group].label);
    assert.match(board, /Quality bar: \*\*80%\*\* of scored scenarios must pass\. This run: \*\*100%\*\*/);
    assert.match(board, /AI and Foundry spend · ≈43% of audited chats/);
    assert.match(board, /🔥/);
});

test("model, question and label text is escaped in every rendering", () => {
    const hostile = { ...extended[0], label: "<img src=x onerror=1> | label", question: "<script>alert(1)</script> | q" };
    const row = passRow(hostile);
    row.result.answer = "<script>alert('answer')</script>";
    row.result.judge.reason = "<b onclick=1>reason</b>";
    for (const text of [
        renderBoardMarkdown([hostile], [row], context),
        renderHtml([hostile], [row], { ...context, publishAnswers: true }),
        renderSummary([hostile], [row], { accepted: true }, sha, true),
    ]) {
        assert.doesNotMatch(text, /<img src=x|<b onclick|alert\('answer'\)<\/script>|<script>alert\(1\)/);
        assert.match(text, /&lt;/);
    }
    assert.equal(escapeHtml(`a&b<c>"d'`), "a&amp;b&lt;c&gt;&quot;d&#39;");
});

test("the HTML dashboard is self-contained, withholds private answers and carries its own script only", () => {
    const row = passRow(extended[0]);
    row.result.answer = "tenant-answer-must-not-publish";
    row.result.judge.reason = "tenant-rationale-must-not-publish";
    const published = [{ ...row, result: publishableResult(row.result, false) }];
    const privateHtml = renderHtml([extended[0]], published, { ...context, publishAnswers: false });
    assert.doesNotMatch(privateHtml, /tenant-answer-must-not-publish|tenant-rationale-must-not-publish/);
    assert.match(privateHtml, /withheld because this run used internal tenant data/);
    const open = renderHtml([extended[0]], [{ ...row, result: publishableResult(row.result, true) }], { ...context, publishAnswers: true });
    assert.match(open, /tenant-answer-must-not-publish/);
    for (const html of [privateHtml, open]) {
        assert.equal((html.match(/<script>/g) ?? []).length, 1);
        assert.doesNotMatch(html, /https?:\/\//, "no external resources");
        assert.match(html, /<html lang="en">/);
        assert.match(html, /data-status="pass"/);
    }
});

test("the dashboard marks failures red and known limitations amber with accessible text", () => {
    const flagged = extended.find((scenario) => scenario.knownIssue);
    const plain = extended.find((scenario) => !scenario.knownIssue);
    const html = renderHtml([plain, flagged], [failRow(plain), failRow(flagged, [INEFFICIENT_FAILURE])],
        { ...context, accepted: false, publishAnswers: true });
    assert.match(html, /class="chip fail">FAIL</);
    assert.match(html, /class="chip known">KNOWN LIMIT</);
    assert.match(html, /class="sq fail"/);
    assert.match(html, /Documented limitation/);
    assert.match(html, /aria-pressed="true"/);
});

test("log lines show status, timing and the reason a scenario failed, with color only on request", () => {
    const scenario = extended[0];
    const ok = renderLogLine({ index: 0, total: 169, scenario, row: passRow(scenario) });
    assert.match(ok, /^\[  1\/169\] 🟢 +PASS +/);
    assert.match(ok, /12\.0 s/);
    assert.doesNotMatch(ok, /\u001b/);
    const bad = renderLogLine({ index: 4, total: 169, scenario, row: failRow(scenario), color: true });
    assert.match(bad, /🔴/);
    assert.match(bad, /\u001b\[1;97;41m FAIL \u001b\[0m/);
    assert.match(bad, /↳ Required tool missing/);
    assert.match(renderLogLine({ index: 0, total: 9, scenario: { ...scenario, requiredTools: ["QueryAzure"] }, row: passRow({ ...scenario, requiredTools: ["QueryAzure"] }) }), /1 tool ·/);
});

test("annotations escape workflow-command syntax, are capped, and always end with a scoreboard notice", () => {
    const hostile = { ...extended[0], label: "50%: a, b\nline" };
    const results = [failRow(hostile, ["Reason with\nnewline, comma: and %."])];
    const [error, notice] = renderAnnotations([hostile], results, { title: "AI Quality Report", accepted: false });
    assert.match(error, /^::error title=🔴 50%25%3A a%2C b%0Aline::Reason with%0Anewline, comma: and %25\.$/);
    assert.ok(![error, notice].some((line) => /[\r\n]/.test(line)));
    assert.match(notice, /^::error title=🔴 AI Quality Report%3A 0%25 green::/);
    const many = extended.slice(0, 30);
    const lines = renderAnnotations(many, many.map((scenario) => failRow(scenario)), { title: "T", accepted: false });
    assert.equal(lines.filter((line) => line.startsWith("::error title=🔴 ")).length, 11, "ten failures and the scoreboard");
    const green = renderAnnotations(many, many.map(passRow), { title: "T", accepted: true });
    assert.deepEqual(green.map((line) => line.slice(0, 9)), ["::notice "]);
});

test("a full extended run with huge answers stays under the GitHub summary limit and lists only what needs attention", () => {
    const results = extended.map((scenario) => {
        const row = passRow(scenario);
        row.result.answer = "<&>".repeat(20000);
        return row;
    });
    results[2] = failRow(extended[2]);
    const summary = renderSummary(extended, results, { accepted: false }, sha, true, presentationFor(extended, 0.8, false));
    assert.ok(Buffer.byteLength(summary) < 1024 * 1024);
    assert.equal((summary.match(/<details><summary>🔴 (?!<b>)/g) ?? []).length, 1, "only the failure has a details block");
    assert.doesNotMatch(summary, /<details><summary>🟢 (?!<b>)/);
    assert.match(summary, /\| Question \| Tools \| Seconds \| Result \| Reason \|/);
    assert.match(summary, /KNOWN LIMIT|PASS/);
    const worst = extended.map((scenario) => {
        const row = failRow(scenario);
        row.result.answer = "<&>".repeat(20000);
        row.result.judge.reason = "x".repeat(20000);
        return row;
    });
    assert.ok(Buffer.byteLength(renderSummary(extended, worst, { accepted: false }, sha, true)) < 1024 * 1024);
});

test("the deployment gate's report keeps its contract strings and gains the scoreboard", () => {
    const results = gate.map(passRow);
    const summary = renderSummary(gate, results, { accepted: true }, sha, true);
    assert.match(summary, /^# 🟢 Live AI Deployment Gate · All 23 scenarios green/);
    assert.match(summary, /\*\*PASS\*\* · 23\/23 accepted/);
    assert.match(summary, /Curated representative live suite: exactly 23 question types/);
    assert.match(summary, /<details open><summary><b>Questions, tools, seconds and reasons<\/b>/);
    assert.match(summary, /### Heatmap/);
});

// ── The quality verdict and its run ─────────────────────────────────────────

test("a run is accepted when it is complete, matches the definitions and meets the quality bar", () => {
    const options = { expected: extended, threshold: 0.8 };
    const results = extended.map(passRow);
    const verdict = evaluateQuality(extended, results, sha, suiteHash, options);
    assert.deepEqual([verdict.accepted, verdict.passed, verdict.failed, verdict.failures], [true, extended.length, 0, []]);
    // Failures under the bar are results, not defects; over it they are rejected.
    const some = extended.map((scenario, index) => (index < 10 ? failRow(scenario) : passRow(scenario)));
    assert.equal(evaluateQuality(extended, some, sha, suiteHash, options).accepted, true);
    const many = extended.map((scenario, index) => (index % 2 ? failRow(scenario) : passRow(scenario)));
    const rejected = evaluateQuality(extended, many, sha, suiteHash, options);
    assert.equal(rejected.accepted, false);
    assert.match(rejected.failures[0], /below the 80% quality bar/);
});

test("known limitations and scenarios that did not run are not scored", () => {
    const flagged = extended.filter((scenario) => scenario.knownIssue).slice(0, 3);
    const rest = extended.filter((scenario) => !flagged.includes(scenario));
    const results = [...flagged.map((scenario) => failRow(scenario, [INEFFICIENT_FAILURE])), ...rest.map(passRow)];
    const verdict = evaluateQuality(extended, results, sha, suiteHash, { expected: extended, threshold: 1 });
    assert.equal(verdict.accepted, true);
    assert.equal(verdict.knownLimitations, 3);
    const partial = evaluateQuality(extended, results.slice(0, -1), sha, suiteHash, { expected: extended, threshold: 0.8 });
    assert.equal(partial.accepted, false);
    assert.ok(partial.failures.includes("Missing or extra scenario results."));
});

test("a run with duplicated, unplanned or altered scenarios is rejected", () => {
    const options = { expected: extended, threshold: 0.1 };
    const results = extended.map(passRow);
    const altered = [{ ...extended[0], rubric: "weaker" }, ...extended.slice(1)];
    assert.ok(evaluateQuality(altered, results, sha, suiteHash, options).failures.includes("Planned scenarios must match the extended suite definitions."));
    assert.ok(evaluateQuality(extended, [...results, results[0]], sha, suiteHash, options).failures.includes("Duplicate scenario result."));
    assert.ok(evaluateQuality(extended, [...results.slice(1), passRow(gate[0])], sha, suiteHash, options).failures.includes("Unplanned result."));
    assert.ok(evaluateQuality([], [], sha, suiteHash, options).failures.includes("No scenario was scored."));
});

test("group selection and the quality bar are validated", () => {
    const safety = selectScenarios(extended, "safety");
    assert.ok(safety.length >= 9 && safety.every((scenario) => scenario.group === "safety"));
    assert.equal(selectScenarios(extended, " safety , multilingual ").length,
        extended.filter((scenario) => ["safety", "multilingual"].includes(scenario.group)).length);
    assert.equal(selectScenarios(extended, ""), extended);
    assert.throws(() => selectScenarios(extended, "safety,nope"), /Unknown scenario group: "nope"/);
    assert.equal(parseThreshold(undefined), DEFAULT_QUALITY_THRESHOLD);
    assert.equal(parseThreshold("90"), 0.9);
    for (const bad of ["abc", "-1", "101"]) assert.throws(() => parseThreshold(bad), /percentage from 0 to 100/);
    assert.match(presentationFor(safety, 0.8, true).tierNote, /selected groups/);
});

test("the extended run writes the board, the structured results and the dashboard, and runs every scenario", async () => {
    const directory = await createDirectory();
    try {
        const planned = extended.slice(0, 12);
        const executed = [];
        let finished;
        const verdict = await runCases(
            planned, directory, sha, suiteHash,
            async (scenario, path) => {
                executed.push(scenario.id);
                const mine = executed.length;
                const result = pass(scenario);
                if (mine === 2) {
                    result.accepted = false;
                    result.reasons = ["Unsupported claim."];
                }
                await writeFile(path, JSON.stringify(result));
                return mine === 2 ? 1 : 0;
            },
            async () => {},
            true,
            0,
            {
                environment: {},
                laneOf: extendedLane,
                failFast: false,
                evaluate: (cases, rows, commit, hash) => evaluateQuality(cases, rows, commit, hash, { expected: extended, threshold: 0.5 }),
                presentation: presentationFor(planned, 0.5, true),
                onFinish: (rows, final) => { finished = { rows, final }; },
            },
        );
        assert.equal(executed.length, 12, "one failure does not stop the others");
        assert.equal(verdict.accepted, true);
        assert.equal(finished.rows.length, 12);
        assert.equal(finished.final.failed, 1);
        const report = JSON.parse(await readFile(join(directory, "results.json"), "utf8"));
        assert.equal(report.verdict.selection, "extended");
        assert.deepEqual(report.results.map((row) => row.id), planned.map((scenario) => scenario.id));
        const summary = await readFile(join(directory, "summary.md"), "utf8");
        assert.match(summary, /^# 🔴 AI Quality Report · 1 of 12 scenarios failed/);
        assert.match(summary, /\*\*PASS\*\* · 11\/12 accepted/);
        assert.match(summary, /extended tier \(selected groups\)/);
        const html = await readFile(join(directory, "ai-quality-report.html"), "utf8");
        assert.match(html, /AI Quality Report/);
        assert.match(html, /class="chip fail">FAIL</);
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
});

test("the gate run also produces the dashboard and keeps failing closed", async () => {
    const directory = await createDirectory();
    try {
        const verdict = await runCases(
            gate, directory, sha, suiteHash,
            async (scenario, path) => { await writeFile(path, JSON.stringify(pass(scenario))); return 0; },
            async () => {}, true, 0, { environment: {} },
        );
        assert.equal(verdict.accepted, true);
        assert.match(await readFile(join(directory, "ai-quality-report.html"), "utf8"), /Live AI Deployment Gate/);
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
});

test("the real extended entry point fails closed on missing configuration and writes a failed summary", async () => {
    const directory = await createDirectory();
    try {
        const result = spawnSync(process.execPath, [fileURLToPath(new URL("./quality.mjs", import.meta.url))], {
            encoding: "utf8",
            env: {
                ...process.env,
                EVAL_EXPECTED_SHA: sha, GITHUB_SHA: sha, GITHUB_ACTIONS: "",
                EVAL_DATA_CLASSIFICATION: "internal-test", EVAL_MODEL_ENDPOINT: "",
                EVAL_OUTPUT_DIRECTORY: directory, EVAL_PRIVATE_DIAGNOSTICS_DIRECTORY: "",
                EVAL_GROUPS: "safety", EVAL_QUALITY_THRESHOLD: "",
            },
        });
        assert.equal(result.status, 1);
        assert.match(result.stderr, /AI quality report failed: Required evaluation configuration missing/);
        const summary = await readFile(join(directory, "summary.md"), "utf8");
        assert.match(summary, /\*\*FAIL\*\*/);
        assert.match(summary, /Answers and judge rationale are withheld/);
        assert.match(summary, /selected groups/);
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
});

test("the extended entry point rejects an unknown group, a bad bar and CI-only misuse before any live case", async () => {
    const run = (extra) => spawnSync(process.execPath, [fileURLToPath(new URL("./quality.mjs", import.meta.url))], {
        encoding: "utf8",
        env: { ...process.env, EVAL_EXPECTED_SHA: sha, GITHUB_SHA: sha, GITHUB_ACTIONS: "", EVAL_OUTPUT_DIRECTORY: join(repositoryRoot, "tests", "LiveEvaluations", "obj", ".never-written"), ...extra },
    });
    assert.match(run({ EVAL_GROUPS: "nope" }).stderr, /Unknown scenario group: "nope"/);
    assert.match(run({ EVAL_QUALITY_THRESHOLD: "150" }).stderr, /percentage from 0 to 100/);
    assert.match(run({ GITHUB_ACTIONS: "true", EVAL_ONLY_IDS: "x" }).stderr, /cannot run in CI/);
    assert.match(run({ GITHUB_ACTIONS: "true", EVAL_PRIVATE_DIAGNOSTICS_DIRECTORY: "/tmp/x" }).stderr, /cannot be configured in CI/);
    assert.match(run({ GITHUB_SHA: "abc", EVAL_EXPECTED_SHA: "abc" }).stderr, /commit SHA is required/);
});

// The groups input is typed by whoever dispatches the workflow; the runner parses the log for ::commands:: line by line.
test("a hostile groups input can never put a workflow command on its own log line", () => {
    const hostile = "x\n::error title=forged::boom\r\n::add-mask::secret";
    assert.throws(() => selectScenarios(extended, hostile), (error) => !/[\r\n]/.test(error.message) && !error.message.includes("\n::"));
    const result = spawnSync(process.execPath, [fileURLToPath(new URL("./quality.mjs", import.meta.url))], {
        encoding: "utf8",
        env: { ...process.env, EVAL_EXPECTED_SHA: sha, GITHUB_SHA: sha, GITHUB_ACTIONS: "", EVAL_GROUPS: hostile, EVAL_OUTPUT_DIRECTORY: join(repositoryRoot, "tests", "LiveEvaluations", "obj", ".never-written") },
    });
    assert.equal(result.status, 1);
    for (const line of result.stderr.split(/\r?\n/)) assert.ok(!line.startsWith("::"), `log line starts a command: ${line}`);
    assert.match(result.stderr, /^AI quality report failed: Unknown scenario group:/);
    assert.equal(oneLine("a\r\nb\u0000c\td"), "a b c d");
});

// The reviewer's case: a release that starts while the report runs must not be throttled by the report's next case.
test("the report pauses before a case while a deployment workflow runs and resumes when it ends", async () => {
    const counts = [2, 1, 0];
    const sleeps = [];
    const logs = [];
    const wait = yieldToGates({
        count: async () => counts.shift() ?? 0,
        sleep: async (milliseconds) => { sleeps.push(milliseconds); },
        log: (line) => logs.push(line),
        pollMs: 1234,
    });
    await wait({ label: "Cost trend" });
    assert.deepEqual(sleeps, [1234, 1234]);
    assert.equal(logs.length, 2);
    assert.match(logs[0], /Pausing before "Cost trend": 2 deployment workflow\(s\) running/);
    assert.match(logs[1], /resuming/);
    sleeps.length = 0;
    logs.length = 0;
    await wait({ label: "next" });
    assert.deepEqual([sleeps.length, logs.length], [0, 0], "nothing running: no pause and no noise");
    // A sleep that is aborted (the run was cancelled) stops the wait.
    const stop = new AbortController();
    const cancelled = yieldToGates({
        count: async () => 1,
        sleep: async (_milliseconds, signal) => { signal.throwIfAborted(); throw new Error("stopped"); },
        log: () => {},
    });
    stop.abort(new Error("cancelled"));
    await assert.rejects(cancelled({ label: "x" }, stop.signal), /cancelled/);
});

test("running deployment workflows are counted by name, an unreadable answer is none, and outside CI nothing is asked", async () => {
    const asked = [];
    const execute = async (command, args) => {
        asked.push([command, args]);
        const status = args[args.indexOf("--status") + 1];
        return {
            stdout: JSON.stringify(status === "in_progress"
                ? [GATE_WORKFLOWS[0], "CodeQL", GATE_WORKFLOWS[2], "AI quality report"]
                : [GATE_WORKFLOWS[1]]),
        };
    };
    assert.equal(await countRunningGates(execute, "owner/repo"), 3);
    assert.deepEqual(asked.map(([command]) => command), ["gh", "gh"]);
    assert.ok(asked.every(([, args]) => args.includes("--repo") && args.includes("owner/repo")));
    assert.equal(await countRunningGates(async () => { throw new Error("gh missing"); }, "owner/repo"), 0);
    assert.equal(await countRunningGates(async () => ({ stdout: "not json" }), "owner/repo"), 0);
    assert.equal(await countRunningGates(execute, ""), 0);
    assert.equal(asked.length, 2, "no repository, no lookup");
    assert.ok(!GATE_WORKFLOWS.includes("AI quality report"), "the report never waits for itself");
});

test("runCases awaits the beforeCase hook before every case and the gate's default adds nothing", async () => {
    const directory = await createDirectory();
    try {
        const order = [];
        const planned = extended.slice(0, 4);
        await runCases(
            planned, directory, sha, suiteHash,
            async (scenario, path) => { order.push(`run:${scenario.id}`); await writeFile(path, JSON.stringify(pass(scenario))); return 0; },
            async () => {}, true, 0,
            {
                environment: {},
                laneOf: () => COST_MANAGEMENT_LANE,
                evaluate: (cases, rows, commit, hash) => evaluateQuality(cases, rows, commit, hash, { expected: extended, threshold: 0 }),
                presentation: presentationFor(planned, 0, true),
                beforeCase: async (scenario) => { order.push(`before:${scenario.id}`); },
            },
        );
        assert.deepEqual(order, planned.flatMap((scenario) => [`before:${scenario.id}`, `run:${scenario.id}`]));
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
});

// ── The workflow ────────────────────────────────────────────────────────────

test("the quality workflow never gates a deployment and follows the repository's workflow rules", async () => {
    const workflows = new URL("../../.github/workflows/", import.meta.url);
    const quality = await readFile(new URL("ai-quality-report.yml", workflows), "utf8");
    assert.doesNotMatch(quality, /continue-on-error/);
    assert.match(quality, /^  schedule:\s*\n\s+- cron: "0 3 \* \* 1"/m);
    assert.match(quality, /^  workflow_dispatch:/m);
    assert.match(quality, /environment: ai-evaluation/);
    assert.match(quality, /run: node tests\/LiveEvaluations\/quality\.mjs/);
    assert.match(quality, /if: always\(\)/);
    assert.match(quality, /group: ai-quality-report/);
    assert.match(quality, /cancel-in-progress: false/);
    assert.match(quality, /Wait for running deployment gates/);
    assert.doesNotMatch(quality, /EVAL_FAIL_FAST/);
    // Waiting (30 min), setup (about 10) and the run step must fit the job limit, or the job timeout would cancel
    // the run mid-case after a long wait.
    const minutes = (pattern) => Number(quality.match(pattern)?.[1]);
    const jobLimit = minutes(/^    timeout-minutes: (\d+)$/m);
    const runLimit = minutes(/- name: Run every extended scenario\r?\n\s+timeout-minutes: (\d+)/);
    const waitLimit = minutes(/deadline=\$\(\(SECONDS \+ (\d+)\)\)/) / 60;
    assert.ok(jobLimit <= 360, "GitHub-hosted jobs stop at 360 minutes");
    assert.ok(waitLimit + 10 + runLimit <= jobLimit, `${waitLimit} + 10 + ${runLimit} must fit ${jobLimit}`);
    for (const match of quality.matchAll(/uses: (?!\.\/)([^\n]+)/g))
        assert.match(match[1], /@[a-f0-9]{40} # v\d/);
    // Inputs reach the program through the environment, never interpolated into a shell command.
    for (const run of quality.matchAll(/run: \|?\n?([\s\S]*?)(?=\n      - |\n    \S|$)/g))
        assert.doesNotMatch(run[1], /\$\{\{\s*inputs\./);
    // A deployment never waits for, or depends on, this report.
    for (const name of ["main", "feature", "live-evaluations"]) {
        const workflow = await readFile(new URL(`${name}.yml`, workflows), "utf8");
        assert.doesNotMatch(workflow, /ai-quality-report|quality\.mjs/);
    }
});

test("the audit the most-asked markers come from is stated", () => {
    assert.equal(AUDIT.conversations, 95);
    assert.match(AUDIT.month, /October 2026/);
});
