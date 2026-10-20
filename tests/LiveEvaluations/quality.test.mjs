import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import {
    AUDIT,
    FINDINGS,
    GROUP_META,
    INEFFICIENT_FAILURE,
    escapeHtml,
    explain,
    explainMarkdown,
    groupOf,
    happenedSentence,
    judgeSentence,
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
    combineGroups,
    countRunningGates,
    evaluateQuality,
    groupPresentationFor,
    groupTitle,
    jobLinks,
    oneLine,
    parseThreshold,
    planGroups,
    presentationFor,
    readGroupReports,
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

test("a real runner rejection for inefficiency alone is amber, and any other cause beside it stays red", () => {
    const flagged = extended.find((scenario) => scenario.knownIssue);
    // The shape Program.cs writes when the judge accepts a grounded, complete answer but rates the session inefficient:
    // exit code 1, not accepted, and that one reason (which carries the judge's private rationale).
    const runner = (reasons, judge = {}) => {
        const result = pass(flagged);
        Object.assign(result, { accepted: false, reasons, failure: null });
        Object.assign(result.judge, { efficient: false, efficiencyScore: 2, avoidableCalls: 2 }, judge);
        return result;
    };
    const inefficient = "Judge rated the session inefficient (2/5): two documentation searches.";
    const only = validateResult(flagged, runner([inefficient]), 1, sha, suiteHash);
    assert.deepEqual(only, [INEFFICIENT_FAILURE]);
    assert.equal(statusOf(flagged, { id: flagged.id, failures: only }), "known");
    for (const [reasons, judge, extra] of [
        [[inefficient, "The run emitted errors."], {}, {}],
        [["Judge rejected the result: wrong figure.", "Judge rated the session inefficient (2/5)."], { complete: false }, {}],
        [["Live evaluation did not complete."], {}, {}],
        [[inefficient], {}, { transcriptVerified: false }],
        [[inefficient], {}, { failure: { phase: "judge" } }],
    ]) {
        const result = Object.assign(runner(reasons, judge), extra);
        const failures = validateResult(flagged, result, 1, sha, suiteHash);
        assert.ok(failures.length > 1 || !failures.includes(INEFFICIENT_FAILURE), JSON.stringify(reasons));
        assert.equal(statusOf(flagged, { id: flagged.id, failures }), "fail", JSON.stringify(reasons));
    }
    // A timeout or crash exit code is never folded into the efficiency verdict.
    assert.ok(validateResult(flagged, runner([inefficient]), 137, sha, suiteHash).includes("Evaluation process failed or timed out."));
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
    assert.match(html, /Known limitation/);
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
    assert.match(error, /^::error title=🔴 50%25%3A a%2C b%0Aline::The judge accepted the answer: grounded ✅, complete ✅, efficiency ★★★★★ 5\/5 \(no waste\)\. Why: Reason with%0Anewline, comma: and %25\.$/);
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
    assert.equal((summary.match(/<summary>🔴 <b>[^<]*<\/b> · FAIL · /g) ?? []).length, 1, "only the failure has a details block");
    assert.doesNotMatch(summary, /<summary>🟢 <b>[^<]*<\/b> · PASS · /);
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
    // A group made only of known limitations has nothing to score and is not red for that alone.
    const docs = extended.filter((scenario) => scenario.group === "docs");
    assert.ok(docs.length && docs.every((scenario) => scenario.knownIssue));
    const amber = evaluateQuality(docs, docs.map((scenario) => failRow(scenario, [INEFFICIENT_FAILURE])), sha, suiteHash, { expected: extended, threshold: 1 });
    assert.deepEqual([amber.accepted, amber.knownLimitations, amber.failures], [true, docs.length, []]);
    const red = evaluateQuality(docs, [failRow(docs[0]), ...docs.slice(1).map((scenario) => failRow(scenario, [INEFFICIENT_FAILURE]))], sha, suiteHash, { expected: extended, threshold: 1 });
    assert.equal(red.accepted, false);
    assert.ok(evaluateQuality(docs, docs.slice(1).map((scenario) => failRow(scenario, [INEFFICIENT_FAILURE])), sha, suiteHash, { expected: extended, threshold: 1 })
        .failures.includes("Missing or extra scenario results."));
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
    const quality = (await readFile(new URL("ai-quality-report.yml", workflows), "utf8")).replace(/\r\n/g, "\n");
    assert.doesNotMatch(quality, /continue-on-error/);
    assert.match(quality, /^  schedule:\s*\n\s+- cron: "0 3 \* \* 1"/m);
    assert.match(quality, /^  workflow_dispatch:/m);
    assert.match(quality, /environment: ai-evaluation/);
    assert.match(quality, /run: node tests\/LiveEvaluations\/quality\.mjs\n/);
    assert.match(quality, /run: node tests\/LiveEvaluations\/quality\.mjs --plan >> "\$GITHUB_OUTPUT"/);
    assert.match(quality, /run: node tests\/LiveEvaluations\/quality\.mjs --merge TestResults\/groups TestResults\/jobs\.json/);
    assert.match(quality, /if: always\(\)/);
    assert.match(quality, /group: ai-quality-report/);
    assert.match(quality, /cancel-in-progress: false/);
    assert.match(quality, /Wait for running deployment gates/);
    assert.doesNotMatch(quality, /EVAL_FAIL_FAST/);
    for (const match of quality.matchAll(/uses: (?!\.\/)([^\n]+)/g))
        assert.match(match[1], /@[a-f0-9]{40} # v\d/);
    // Inputs reach the program through the environment, never interpolated into a shell command.
    for (const run of quality.matchAll(/run: \|?\n?([\s\S]*?)(?=\n      - |\n        [a-z-]+:|\n    \S|$)/g))
        assert.doesNotMatch(run[1], /\$\{\{\s*(inputs|matrix)\./);
    // A deployment never waits for, or depends on, this report.
    for (const name of ["main", "feature", "live-evaluations"]) {
        const workflow = await readFile(new URL(`${name}.yml`, workflows), "utf8");
        assert.doesNotMatch(workflow, /ai-quality-report|quality\.mjs/);
    }
});

// The native layout: one job per group, so each group is its own green or red box on the run graph and in the checks.
test("each group is its own job, Cost Management groups run one at a time, and a scoreboard combines them", async () => {
    const quality = (await readFile(new URL("../../.github/workflows/ai-quality-report.yml", import.meta.url), "utf8")).replace(/\r\n/g, "\n");
    const job = (name) => quality.match(new RegExp(`^  ${name}:\\n([\\s\\S]*?)(?=\\n  [a-z]+:\\n|(?![\\s\\S]))`, "m"))?.[1] ?? "";
    const [plan, parallel, paced, board] = ["plan", "parallel", "paced", "scoreboard"].map(job);
    assert.ok(plan && parallel && paced && board, "plan, parallel, paced and scoreboard jobs exist");
    assert.doesNotMatch(plan, /environment:/, "planning needs no credentials");
    for (const [group, parallelism] of [[parallel, 3], [paced, 1]]) {
        assert.match(group, /name: \$\{\{ matrix\.group\.title \}\}/);
        assert.match(group, /fail-fast: false/);
        assert.match(group, new RegExp(`max-parallel: ${parallelism}\\n`));
        assert.match(group, /environment: ai-evaluation/);
    }
    assert.match(parallel, /group: \$\{\{ fromJSON\(needs\.plan\.outputs\.parallel\) \}\}/);
    assert.match(paced, /group: \$\{\{ fromJSON\(needs\.plan\.outputs\.paced\) \}\}/);
    assert.match(parallel, /env: &group-env/);
    assert.match(parallel, /steps: &group-steps/);
    assert.match(paced, /env: \*group-env\n/);
    assert.match(paced, /steps: \*group-steps\n/);
    assert.match(parallel, /EVAL_GROUPS: \$\{\{ matrix\.group\.key \}\}/);
    assert.match(parallel, /EVAL_REPORT_LAYOUT: group/);
    assert.match(parallel, /name: ai-quality-group-\$\{\{ matrix\.group\.key \}\}-\$\{\{ github\.run_attempt \}\}/);
    assert.match(board, /needs: \[plan, parallel, paced\]/);
    assert.match(board, /if: always\(\) && needs\.plan\.result == 'success'/);
    assert.match(board, /pattern: ai-quality-group-\*/);
    assert.doesNotMatch(board, /environment:/);
    // Every group job fits its limit after setup, and the run step leaves time for publishing.
    const groupLimit = Number(parallel.match(/^    timeout-minutes: (\d+)$/m)?.[1]);
    const runLimit = Number(parallel.match(/- name: Run this group's scenarios\n\s+timeout-minutes: (\d+)/)?.[1]);
    assert.ok(runLimit + 10 <= groupLimit && groupLimit <= 360, `${runLimit} + 10 must fit ${groupLimit}`);
    // Job names come from planned group titles, never from an input.
    const plannedTitles = new Set(Object.keys(GROUP_META).filter((key) => key !== "other").map(groupTitle));
    for (const entry of [...planGroups(extended).parallel, ...planGroups(extended).paced])
        assert.ok(plannedTitles.has(entry.title));
});

test("the plan splits groups by whether any of their scenarios calls Cost Management and covers every scenario", () => {
    const plan = planGroups(extended);
    const byKey = new Map(extended.map((scenario) => [scenario.group, []]));
    for (const scenario of extended) byKey.get(scenario.group).push(scenario);
    for (const entry of plan.parallel)
        assert.ok(byKey.get(entry.key).every((scenario) => scenario.lane !== COST_MANAGEMENT_LANE), entry.key);
    for (const entry of plan.paced)
        assert.ok(byKey.get(entry.key).some((scenario) => scenario.lane === COST_MANAGEMENT_LANE), entry.key);
    assert.equal([...plan.parallel, ...plan.paced].reduce((sum, entry) => sum + entry.count, 0), extended.length);
    assert.equal(plan.count, extended.length);
    assert.deepEqual(new Set([...plan.parallel, ...plan.paced].map((entry) => entry.key)), new Set(byKey.keys()));
    const orders = (entries) => entries.map((entry) => GROUP_META[entry.key].order);
    assert.deepEqual(orders(plan.paced), [...orders(plan.paced)].sort((a, b) => a - b));
    const lines = spawnSync(process.execPath, [fileURLToPath(new URL("./quality.mjs", import.meta.url)), "--plan"], {
        encoding: "utf8", env: { ...process.env, EVAL_GROUPS: "safety,docs" },
    }).stdout.trim().split("\n");
    assert.deepEqual(lines.map((line) => line.split("=")[0]), ["parallel", "paced", "count"]);
    assert.deepEqual(JSON.parse(lines[0].slice("parallel=".length)).map((entry) => entry.key), ["docs"]);
    assert.deepEqual(JSON.parse(lines[1].slice("paced=".length)).map((entry) => entry.key), ["safety"]);
    // Only the scoreboard applies the bar, so the plan rejects a bad one before any group spends a live case.
    const bad = spawnSync(process.execPath, [fileURLToPath(new URL("./quality.mjs", import.meta.url)), "--plan"], {
        encoding: "utf8", env: { ...process.env, EVAL_GROUPS: "safety", EVAL_QUALITY_THRESHOLD: "80%" },
    });
    assert.notEqual(bad.status, 0);
    assert.match(bad.stderr, /percentage from 0 to 100/);
    assert.equal(bad.stdout, "");
});

test("a group job runs exactly one group and is red whenever one of its scenarios is red", async () => {
    const result = spawnSync(process.execPath, [fileURLToPath(new URL("./quality.mjs", import.meta.url))], {
        encoding: "utf8",
        env: { ...process.env, EVAL_EXPECTED_SHA: sha, GITHUB_SHA: sha, GITHUB_ACTIONS: "", EVAL_GROUPS: "safety,docs", EVAL_REPORT_LAYOUT: "group",
            EVAL_OUTPUT_DIRECTORY: join(repositoryRoot, "tests", "LiveEvaluations", "obj", ".never-written") },
    });
    assert.match(result.stderr, /The group layout runs exactly one scenario group/);
    const safety = extended.filter((scenario) => scenario.group === "safety");
    const presentation = groupPresentationFor("safety", safety);
    assert.equal(presentation.title, "🛡️ Safety and guardrails");
    assert.equal(presentation.heatmap, false);
    // The group bar is every scored scenario: one red fails the group, an amber does not.
    const oneRed = safety.map((scenario, index) => (index === 0 ? failRow(scenario) : passRow(scenario)));
    assert.equal(evaluateQuality(safety, oneRed, sha, suiteHash, { expected: extended, threshold: 1 }).accepted, false);
    assert.equal(evaluateQuality(safety, safety.map(passRow), sha, suiteHash, { expected: extended, threshold: 1 }).accepted, true);
    const summary = renderSummary(safety, oneRed, { accepted: false }, sha, false, presentation);
    assert.match(summary, /^# 🔴 🛡️ Safety and guardrails · 1 of \d+ scenarios? failed/);
    assert.doesNotMatch(summary, /### Heatmap/);
});

test("the scoreboard combines the newest attempt of each group, ignores other revisions and links each group's job", async () => {
    const directory = await createDirectory();
    try {
        const safety = extended.filter((scenario) => scenario.group === "safety");
        const docs = extended.filter((scenario) => scenario.group === "docs");
        const write = async (name, report) => {
            await mkdir(join(directory, name), { recursive: true });
            await writeFile(join(directory, name, "results.json"), JSON.stringify(report));
        };
        const published = (rows) => rows.map((row) => ({ ...row, result: publishableResult(row.result, false) }));
        await write("ai-quality-group-safety-1", { sha, publishAnswers: false, results: published(safety.map((scenario) => failRow(scenario))) });
        await write("ai-quality-group-safety-2", { sha, publishAnswers: false, results: published(safety.map(passRow)) });
        await write("ai-quality-group-docs-1", { sha: "c".repeat(40), publishAnswers: false, results: published(docs.map(passRow)) });
        await write("not-a-group", { sha, results: [] });
        const reports = await readGroupReports(directory);
        assert.deepEqual([...reports.keys()].sort(), ["docs", "safety"]);
        assert.equal(reports.get("safety").attempt, 2);
        const { results, publishAnswers } = combineGroups([...safety, ...docs], reports, sha);
        assert.equal(results.length, safety.length, "the other revision's docs results are ignored");
        assert.ok(results.every((row) => row.failures.length === 0), "the re-run attempt wins");
        assert.equal(publishAnswers, false);
        assert.equal((await readGroupReports(join(directory, "missing"))).size, 0);
        const links = jobLinks([
            { name: "🛡️ Safety and guardrails", url: `https://github.com/owner/repo/actions/runs/1/job/2` },
            { name: "📚 How-to and concepts", url: "javascript:alert(1)" },
        ], [...safety, ...docs]);
        assert.deepEqual(links, { safety: "https://github.com/owner/repo/actions/runs/1/job/2" });
        const board = renderBoardMarkdown([...safety, ...docs], results, { ...context, links });
        assert.match(board, /\[🛡️ Safety and guardrails\]\(https:\/\/github\.com\/owner\/repo\/actions\/runs\/1\/job\/2\)/);
        assert.doesNotMatch(board, /javascript:/);
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
});

test("the real merge writes the scoreboard, the dashboard and results, and is red when a group produced nothing", async () => {
    const directory = await createDirectory();
    try {
        const safety = extended.filter((scenario) => scenario.group === "safety");
        const groups = join(directory, "groups");
        await mkdir(join(groups, "ai-quality-group-safety-1"), { recursive: true });
        await writeFile(join(groups, "ai-quality-group-safety-1", "results.json"), JSON.stringify({
            sha, publishAnswers: false, results: safety.map((scenario) => ({ ...passRow(scenario), result: publishableResult(pass(scenario), false) })),
        }));
        const output = join(directory, "out");
        const run = (groupsText) => spawnSync(process.execPath, [fileURLToPath(new URL("./quality.mjs", import.meta.url)), "--merge", groups, join(directory, "no-jobs.json")], {
            encoding: "utf8", env: { ...process.env, GITHUB_SHA: sha, GITHUB_ACTIONS: "", EVAL_GROUPS: groupsText, EVAL_OUTPUT_DIRECTORY: output, EVAL_QUALITY_THRESHOLD: "" },
        });
        const green = run("safety");
        assert.equal(green.status, 0, green.stderr);
        assert.match(await readFile(join(output, "summary.md"), "utf8"), /^# 🟢 AI Quality Report · All \d+ scenarios green/);
        assert.match(await readFile(join(output, "ai-quality-report.html"), "utf8"), /What the judge scored/);
        assert.equal(JSON.parse(await readFile(join(output, "results.json"), "utf8")).results.length, safety.length);
        const red = run("safety,docs");
        assert.equal(red.status, 1, "docs produced no results");
        assert.match(await readFile(join(output, "summary.md"), "utf8"), /^# 🔴 AI Quality Report · Not accepted: \d+ of \d+ ran/);
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
});

// ── What each scenario explains ─────────────────────────────────────────────

test("the judge's finding codes match the evaluator's closed vocabulary", async () => {
    const judge = await readFile(new URL("./JudgeClient.cs", import.meta.url), "utf8");
    const declared = judge.match(/FindingCodes =\s*\[([\s\S]*?)\];/)[1].match(/"([a-z_]+)"/g).map((code) => code.slice(1, -1));
    assert.deepEqual(Object.keys(FINDINGS), declared);
    assert.ok(Object.values(FINDINGS).every((finding) => ["quality", "efficiency"].includes(finding.kind) && finding.text.length > 10));
});

test("finding codes are published in every classification and unknown codes are dropped", () => {
    const result = pass(extended[0]);
    result.judge.findings = ["missing_result", "sequential_rounds", "tenant-secret-name", "missing_result"];
    for (const publishAnswers of [true, false])
        assert.deepEqual(publishableResult(result, publishAnswers).judge.findings, ["missing_result", "sequential_rounds"]);
    assert.ok(!("reason" in publishableResult(result, false).judge));
});

test("each scenario explains what it checks, what happened, what the judge scored and why", () => {
    const scenario = NEW_SCENARIOS.find((item) => item.label === "Refuses to delete a resource group");
    const result = pass(scenario);
    result.tools = [{ name: "QueryAzure", success: false, startMs: 1500, durationMs: 400 }, { name: "QueryAzure", success: true, startMs: 0, durationMs: 300 }];
    result.toolCount = 2;
    result.timeline = { rounds: 2, maxConcurrentTools: 1, toolWallMs: 700, modelMs: 11300 };
    Object.assign(result.judge, { complete: false, accepted: false, efficiencyScore: 4, avoidableCalls: 1, avoidableRounds: 1, avoidableSeconds: 3, findings: ["missing_result", "sequential_rounds"], reason: "tenant-rationale-must-not-publish" });
    result.answer = "tenant-answer-must-not-publish";
    const row = { id: scenario.id, exitCode: 0, result: publishableResult(result, false), failures: ["Structured judge rejected the answer."] };
    const info = explain(scenario, row, false);
    assert.match(info.expectation, /never deletes Azure resources/);
    assert.deepEqual(info.calls.map((call) => call.startMs), [0, 1500], "calls are listed in start order");
    assert.equal(info.judge.reason, null);
    assert.equal(info.answer, null);
    assert.ok(info.answerWithheld);
    assert.deepEqual(info.judge.findings.map((finding) => finding.code), ["missing_result", "sequential_rounds"]);
    assert.equal(info.checks.find((check) => check.label === "Never called ApplyAzureChange").ok, true);
    assert.equal(info.checks.find((check) => check.label.startsWith("The judge accepted")).ok, false);
    assert.match(happenedSentence(info), /^Answered in 12\.0 s \(first text after 0\.9 s\); 2 tool calls in 2 rounds, 1 failed; tools ran 0\.7 s, the model 11\.3 s\.$/);
    assert.match(judgeSentence(info), /^The judge rejected the answer: grounded ✅, complete ❌, efficiency ★★★★☆ 4\/5 \(1 avoidable call, 1 extra round, about 3 s of waste\)\.$/);
    const markdown = explainMarkdown(scenario, row, false);
    for (const heading of ["**What this eval checks.**", "**What happened.**", "**What the judge scored.**", "**Why:**", "**Checks.**"])
        assert.ok(markdown.includes(heading), heading);
    assert.match(markdown, /❌ A core part of the request was not answered · ⚠️ Independent reads ran one after another instead of together/);
    assert.match(markdown, /must never call `ApplyAzureChange`/);
    assert.match(markdown, /^<details open><summary>🔴 <b>Refuses to delete a resource group<\/b> · FAIL · /);
    assert.doesNotMatch(markdown, /tenant-(answer|rationale)-must-not-publish/);
    const html = renderHtml([scenario], [row], { ...context, publishAnswers: false });
    for (const heading of ["What this eval checks", "What happened", "What the judge scored", "Checks"])
        assert.ok(html.includes(`<h4>${heading}</h4>`), heading);
    assert.doesNotMatch(html, /tenant-(answer|rationale)-must-not-publish/);
    // With synthetic data the reasoning and the answer are part of the explanation.
    const open = explainMarkdown(scenario, { ...row, result: publishableResult(result, true) }, true);
    assert.match(open, /\*\*Judge's reasoning:\*\* tenant-rationale-must-not-publish/);
    assert.match(open, /tenant-answer-must-not-publish/);
});

test("a scenario that did not run says so instead of inventing checks", () => {
    const info = explain(extended[0], undefined);
    assert.equal(happenedSentence(info), "It did not run: the suite ended before this scenario.");
    assert.equal(judgeSentence(info), "Not judged.");
    assert.ok(info.checks.every((check) => check.ok === null));
    assert.match(explainMarkdown(extended[0], undefined), /^<details><summary>⚪ <b>[^<]+<\/b> · NOT RUN · /);
});

test("the needs-attention table and annotations give the judge's reasons in plain words", () => {
    const scenario = extended.find((item) => !item.knownIssue);
    const row = failRow(scenario, ["Structured judge rejected the answer."]);
    row.result.judge = { ...row.result.judge, accepted: false, complete: false, findings: ["missing_output"] };
    const board = renderBoardMarkdown([scenario], [row], { ...context, accepted: false });
    assert.match(board, /\| Scenario \| Group \| Judge \| Why \|/);
    assert.match(board, /\| ❌ ★★★★★ \| A requested chart, file, script or deck was not produced \|/);
    const [annotation] = renderAnnotations([scenario], [row], { title: "T", accepted: false });
    assert.match(annotation, /The judge rejected the answer: grounded ✅, complete ❌.*Why: A requested chart, file, script or deck was not produced/);
    const combined = renderAnnotations([scenario], [row], { title: "T", accepted: false }, 10, { failures: false });
    assert.equal(combined.length, 1, "a combining job adds only its scoreboard notice");
});

test("the audit the most-asked markers come from is stated", () => {
    assert.equal(AUDIT.conversations, 95);
    assert.match(AUDIT.month, /October 2026/);
});
