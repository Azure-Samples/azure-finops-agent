// The extended evaluation tier: node tests/LiveEvaluations/quality.mjs
//
// Runs every scenario the deployment gate does not (the unused catalog plus scenarios grounded in real customer
// questions, safety invariants and other languages) against the same candidate handler, tools and judge, and reports
// each one red or green. It never blocks a deployment: the gate in suite.mjs stays at its 23 curated scenarios. This run
// fails (red) only when the share of scored scenarios that pass drops below the quality bar, or when it cannot complete.

import { createHash } from "node:crypto";
import { execFile } from "node:child_process";
import { mkdir, readdir, readFile, writeFile } from "node:fs/promises";
import { join, resolve } from "node:path";
import { setTimeout as delay } from "node:timers/promises";
import { pathToFileURL } from "node:url";
import { promisify } from "node:util";
import { GROUP_META, renderAnnotations, renderHtml, scoreboard } from "./report.mjs";
import { buildExtendedSuite, extendedLane } from "./scenarios.mjs";
import {
    COST_MANAGEMENT_LANE,
    COST_QUOTA_WINDOW_MS,
    assertCandidateRevision,
    diagnosticsRecipient,
    executeCase,
    refreshEvaluationIdentity,
    renderSummary,
    runCases,
} from "./suite.mjs";

export const DEFAULT_QUALITY_THRESHOLD = 0.8;

// Workflows that run the deployment gate (the reusable gate reports under its caller's name).
export const GATE_WORKFLOWS = Object.freeze([
    "Deploy main to production",
    "Deploy feature branch to test slot",
    "Live AI evaluation gate",
]);
const GATE_POLL_MS = 60 * 1000;

// Counts deployment workflows that are running or queued, through the GitHub CLI the workflow already authenticates.
// An unreadable answer is "none": this tier never blocks anything, so it does not stall on a failed lookup.
export async function countRunningGates(execute = promisify(execFile), repository = process.env.GITHUB_REPOSITORY) {
    if (!repository) return 0;
    let busy = 0;
    for (const status of ["in_progress", "queued"]) {
        try {
            const { stdout } = await execute("gh", [
                "run", "list", "--repo", repository, "--status", status, "--limit", "50",
                "--json", "workflowName", "--jq", "[.[].workflowName]",
            ], { timeout: 30000 });
            busy += JSON.parse(stdout).filter((name) => GATE_WORKFLOWS.includes(name)).length;
        } catch {
            // Unknown is not busy: see above.
        }
    }
    return busy;
}

// Both tiers share the tenant's Cost Management quota, which the gate cannot tolerate being throttled. Before each case
// this report waits while a deployment workflow is running, so a release that starts mid-report is never throttled by
// the report's next case (a case already running when the gate starts is the only overlap).
export function yieldToGates({
    count = countRunningGates,
    sleep = (milliseconds, signal) => delay(milliseconds, undefined, { signal }),
    log = console.log,
    pollMs = GATE_POLL_MS,
} = {}) {
    return async (scenario, signal) => {
        let announced = false;
        for (let busy = await count(); busy > 0; busy = await count()) {
            if (!announced) {
                log(`Pausing before "${scenario.label ?? scenario.id}": ${busy} deployment workflow(s) running; the report yields to them.`);
                announced = true;
            }
            await sleep(pollMs, signal);
        }
        if (announced) log("Deployment workflows finished; resuming.");
    };
}

// The log is parsed for workflow commands line by line, so an input must never reach it with a line break.
export const oneLine = (value) => String(value).replace(/[\u0000-\u001f\u007f]+/g, " ").trim();

// The verdict of an extended run. Per-scenario failures are results, not defects of the run: the run is accepted when
// it is complete, matches the extended definitions, and enough of the scored scenarios pass.
export function evaluateQuality(cases, results, sha, suiteHash, { expected, threshold = DEFAULT_QUALITY_THRESHOLD }) {
    const failures = [];
    const definitions = new Map(expected.map((scenario) => [scenario.id, scenario]));
    if (cases.some((scenario) => JSON.stringify(scenario) !== JSON.stringify(definitions.get(scenario.id))))
        failures.push("Planned scenarios must match the extended suite definitions.");
    if (
        new Set(cases.map((scenario) => scenario.id)).size !== cases.length ||
        new Set(cases.map((scenario) => scenario.question)).size !== cases.length
    )
        failures.push("Duplicate planned scenarios.");
    const planned = new Set(cases.map((scenario) => scenario.id));
    const seen = new Set();
    for (const row of results) {
        if (seen.has(row.id)) failures.push("Duplicate scenario result.");
        seen.add(row.id);
        if (!planned.has(row.id)) failures.push("Unplanned result.");
    }
    if (results.length !== cases.length) failures.push("Missing or extra scenario results.");
    const board = scoreboard(cases, results);
    // Amber scenarios are not scored, so a group made only of known limitations has nothing to score; that is a
    // result, not a defect (a missing result is caught above).
    if (board.scored === 0) {
        if (!board.known) failures.push("No scenario was scored.");
    } else if (board.passRate < threshold)
        failures.push(`${Math.round(board.passRate * 100)}% of scored scenarios passed, below the ${Math.round(threshold * 100)}% quality bar.`);
    return {
        accepted: failures.length === 0,
        selection: "extended",
        required: cases.length,
        planned: cases.length,
        completed: results.length,
        passed: board.passed,
        failed: board.failed,
        knownLimitations: board.known,
        passRate: board.passRate,
        threshold,
        failures,
    };
}

// Groups and scenario ids both come from workflow inputs; either narrows the run, and both together intersect.
export function selectScenarios(all, groupsText = "", idsText = "") {
    const list = (text) => String(text ?? "").split(",").map((item) => item.trim()).filter(Boolean);
    const groups = list(groupsText);
    const ids = list(idsText);
    const quoted = (items) => items.map((item) => JSON.stringify(oneLine(item).slice(0, 40))).join(", ");
    const unknown = groups.filter((group) => !GROUP_META[group]);
    // Names come from a workflow input: quoted and stripped of control characters, never echoed raw.
    if (unknown.length) throw new Error(`Unknown scenario group: ${quoted(unknown)}.`);
    const known = new Set(all.map((scenario) => scenario.id));
    const strangers = ids.filter((id) => !known.has(id));
    if (strangers.length) throw new Error(`Unknown scenario id: ${quoted(strangers)}.`);
    return all.filter((scenario) =>
        (!groups.length || groups.includes(scenario.group)) && (!ids.length || ids.includes(scenario.id)));
}

export function parseThreshold(text) {
    if (text === undefined || text === "") return DEFAULT_QUALITY_THRESHOLD;
    const value = Number(text);
    if (!Number.isFinite(value) || value < 0 || value > 100)
        throw new Error("EVAL_QUALITY_THRESHOLD must be a percentage from 0 to 100.");
    return value / 100;
}

export function presentationFor(cases, threshold, partial) {
    return {
        title: "AI Quality Report",
        tierNote: `extended tier${partial ? " (selected groups)" : ""} · ${cases.length} scenarios beyond the deployment gate`,
        threshold,
        required: cases.length,
        intro: [
            "The deployment gate stays at its 23 curated scenarios and decides whether a build ships. This extended tier is broader: every catalog scenario the gate does not run, the questions customers asked most in an audit of real conversations (rewritten as synthetic wording), safety guardrails and other languages. It never blocks a deployment; a scenario that keeps passing here is a candidate for the gate.",
            "",
            "Real model calls through the candidate chat handler and tools, with a separate structured judge. An amber scenario failed only because of a documented limitation. Test-host authentication is not a browser sign-in test.",
        ],
    };
}

// One matrix job per scenario: each eval is its own green or red entry on the run page, in the run graph and in the
// commit's checks, with its own summary card (what it checks, what happened, what the judge scored and why), and
// "Re-run failed jobs" re-runs exactly the failed evals. A known limitation keeps its job green and shows amber in the
// card and the scoreboard; the scoreboard job applies the overall quality bar.
export const SCENARIO_LAYOUT = "scenario";

// The job name: the group's icon and the scenario's label. Labels are our own text and unique (a test checks both).
export function scenarioTitle(scenario) {
    const meta = GROUP_META[scenario?.group];
    if (!meta) throw new Error(`Unknown scenario group: ${JSON.stringify(oneLine(scenario?.group ?? "").slice(0, 40))}.`);
    return oneLine(`${meta.icon} ${scenario.label ?? scenario.id}`);
}

export function scenarioPresentationFor(scenario) {
    return {
        title: scenarioTitle(scenario),
        single: true,
        tierNote: `${GROUP_META[scenario.group].label} · one eval of the AI quality report`,
        threshold: null,
        required: 1,
        heatmap: false,
        intro: [],
    };
}

// Splits the planned evals into those that never call Cost Management, which run side by side, and those that do,
// which run one at a time because the tenant-wide Cost Management quota is shared (the same rule as the lanes). Both
// keep the extended suite's group order, so the run page lists evals group by group.
export function planScenarios(cases) {
    const entries = cases.map((scenario) => ({
        id: scenario.id,
        title: scenarioTitle(scenario),
        paced: (scenario.lane ?? extendedLane(scenario)) === COST_MANAGEMENT_LANE,
    }));
    const strip = ({ paced, ...entry }) => entry;
    return {
        parallel: entries.filter((entry) => !entry.paced).map(strip),
        paced: entries.filter((entry) => entry.paced).map(strip),
        count: cases.length,
    };
}

// Eval artifacts are named ai-quality-eval-<scenario id>-<attempt>; a re-run of a failed eval uploads a new attempt and
// the newest attempt of each eval wins.
export const EVAL_ARTIFACT = /^ai-quality-eval-([0-9a-f]{16})-(\d+)$/;

export async function readEvalReports(directory) {
    const latest = new Map();
    let names = [];
    try {
        names = await readdir(directory, { withFileTypes: true });
    } catch {
        return latest;
    }
    for (const entry of names) {
        const match = entry.isDirectory() ? EVAL_ARTIFACT.exec(entry.name) : null;
        if (!match) continue;
        const attempt = Number(match[2]);
        if (latest.has(match[1]) && latest.get(match[1]).attempt >= attempt) continue;
        try {
            const report = JSON.parse(await readFile(join(directory, entry.name, "results.json"), "utf8"));
            latest.set(match[1], { attempt, report });
        } catch {
            // An eval whose job ended before writing results stays "not run".
        }
    }
    return latest;
}

// The overall picture from every eval's results: only rows for planned scenarios of this revision count, an artifact
// counts only for the eval it is named after, and answers are published only when every eval published them.
export function combineReports(cases, reports, sha) {
    const planned = new Set(cases.map((scenario) => scenario.id));
    const results = [];
    let publishAnswers = reports.size > 0;
    for (const [id, { report }] of reports) {
        if (report?.sha !== sha || !Array.isArray(report.results)) continue;
        publishAnswers &&= report.publishAnswers === true;
        for (const row of report.results)
            if (row?.id === id && planned.has(row.id) && Array.isArray(row.failures)) results.push(row);
    }
    const order = new Map(cases.map((scenario, index) => [scenario.id, index]));
    results.sort((left, right) => order.get(left.id) - order.get(right.id));
    const seen = new Set();
    return { results: results.filter((row) => !seen.has(row.id) && seen.add(row.id)), publishAnswers };
}

// Links each eval in the scoreboard to the job page that ran it, from `gh api .../attempts/N/jobs`.
export function jobLinks(jobs, cases) {
    const links = {};
    if (!Array.isArray(jobs)) return links;
    for (const scenario of cases) {
        const job = jobs.filter((item) => item?.name === scenarioTitle(scenario) && typeof item.url === "string").at(-1);
        if (job && /^https:\/\/github\.com\/[\w.-]+\/[\w.-]+\/actions\/runs\/\d+\/job\/\d+$/.test(job.url)) links[scenario.id] = job.url;
    }
    return links;
}

async function merge(directory, jobsFile) {
    const sha = process.env.GITHUB_SHA ?? process.env.EVAL_EXPECTED_SHA;
    if (!sha || !/^[a-f0-9]{40}$/i.test(sha)) throw new Error("The candidate commit SHA is required.");
    const expected = buildExtendedSuite();
    const cases = selectScenarios(expected, process.env.EVAL_GROUPS, process.env.EVAL_SCENARIOS);
    const threshold = parseThreshold(process.env.EVAL_QUALITY_THRESHOLD);
    const output = resolve(process.env.EVAL_OUTPUT_DIRECTORY ?? "TestResults/live-evaluations");
    const reports = await readEvalReports(resolve(directory));
    const { results, publishAnswers } = combineReports(cases, reports, sha);
    let jobs = [];
    try {
        jobs = JSON.parse(await readFile(resolve(jobsFile), "utf8"));
    } catch {
        // Links are a convenience; the scoreboard stands without them.
    }
    const suiteHash = createHash("sha256").update(JSON.stringify(cases)).digest("hex");
    const verdict = evaluateQuality(cases, results, sha, suiteHash, { expected, threshold });
    const presentation = { ...presentationFor(cases, threshold, cases.length !== expected.length), links: jobLinks(jobs, cases) };
    await mkdir(output, { recursive: true });
    await writeFile(resolve(output, "results.json"), JSON.stringify({ sha, suiteHash, cases, results, verdict, publishAnswers }, null, 2));
    await writeFile(resolve(output, "summary.md"), renderSummary(cases, results, verdict, sha, publishAnswers, presentation));
    await writeFile(resolve(output, "ai-quality-report.html"), renderHtml(cases, results, {
        title: presentation.title, accepted: verdict.accepted, sha, publishAnswers,
        tierNote: presentation.tierNote, threshold, generatedAt: new Date().toISOString(),
    }));
    if (process.env.GITHUB_ACTIONS === "true")
        for (const line of renderAnnotations(cases, results, { title: presentation.title, accepted: verdict.accepted }, 10, { failures: false }))
            console.log(line);
    if (!verdict.accepted) process.exitCode = 1;
}

async function main() {
    if (process.argv[2] === "--plan") {
        // Only the scoreboard applies the bar, after every eval ran; a bad value must stop the run before any does.
        parseThreshold(process.env.EVAL_QUALITY_THRESHOLD);
        const cases = selectScenarios(buildExtendedSuite(), process.env.EVAL_GROUPS, process.env.EVAL_SCENARIOS);
        if (!cases.length) throw new Error("No scenario matches the selection.");
        const plan = planScenarios(cases);
        console.log(`parallel=${JSON.stringify(plan.parallel)}`);
        console.log(`paced=${JSON.stringify(plan.paced)}`);
        console.log(`count=${plan.count}`);
        return;
    }
    if (process.argv[2] === "--merge") {
        await merge(process.argv[3] ?? "TestResults/evals", process.argv[4] ?? "TestResults/jobs.json");
        return;
    }
    const sha = process.env.GITHUB_SHA ?? process.env.EVAL_EXPECTED_SHA;
    if (!sha || !/^[a-f0-9]{40}$/i.test(sha)) throw new Error("The candidate commit SHA is required.");
    const expected = buildExtendedSuite();
    const only = process.env.EVAL_ONLY_IDS?.split(",").filter(Boolean) ?? [];
    if (only.length && process.env.GITHUB_ACTIONS === "true")
        throw new Error("EVAL_ONLY_IDS is a local diagnostic filter and cannot run in CI.");
    if (process.env.GITHUB_ACTIONS === "true" && process.env.EVAL_PRIVATE_DIAGNOSTICS_DIRECTORY)
        throw new Error("Private evaluation diagnostics are local-only and cannot be configured in CI.");
    const scenarioLayout = process.env.EVAL_REPORT_LAYOUT === SCENARIO_LAYOUT;
    const selected = selectScenarios(expected, process.env.EVAL_GROUPS,
        scenarioLayout ? process.env.EVAL_SCENARIO_ID : process.env.EVAL_SCENARIOS);
    const cases = only.length ? selected.filter((scenario) => only.includes(scenario.id)) : selected;
    if (!cases.length) throw new Error("No scenario matches the selection.");
    if (scenarioLayout && (cases.length !== 1 || cases[0].id !== process.env.EVAL_SCENARIO_ID?.trim()))
        throw new Error("The scenario layout runs exactly one scenario, named by EVAL_SCENARIO_ID.");
    // An eval job is red when its scenario is red; the overall bar belongs to the scoreboard.
    const threshold = scenarioLayout ? 1 : parseThreshold(process.env.EVAL_QUALITY_THRESHOLD);
    const suiteHash = createHash("sha256").update(JSON.stringify(cases)).digest("hex");
    const output = resolve(process.env.EVAL_OUTPUT_DIRECTORY ?? "TestResults/live-evaluations");
    const classification = process.env.EVAL_DATA_CLASSIFICATION ?? "";
    const publishAnswers =
        classification === "synthetic" ||
        (classification !== "internal-test" && process.env.GITHUB_ACTIONS !== "true");
    const presentation = scenarioLayout
        ? scenarioPresentationFor(cases[0])
        : presentationFor(cases, threshold, cases.length !== expected.length);
    const evaluate = (planned, results, commit, hash) =>
        evaluateQuality(planned, results, commit, hash, { expected, threshold });
    await mkdir(output, { recursive: true });
    await writeFile(
        resolve(output, "summary.md"),
        renderSummary(cases, [], evaluate(cases, [], sha, suiteHash), sha, publishAnswers, presentation),
    );
    for (const name of ["EVAL_MODEL_ENDPOINT", "EVAL_MODEL", "EVAL_JUDGE_MODEL", "EVAL_TENANT_ID", "EVAL_SUBSCRIPTION_IDS"])
        if (!process.env[name]?.trim()) throw new Error(`Required evaluation configuration missing: ${name}.`);
    if (process.env.GITHUB_ACTIONS === "true" && !["synthetic", "internal-test"].includes(classification))
        throw new Error("Set EVAL_DATA_CLASSIFICATION to synthetic (answers published) or internal-test (verdicts only).");
    const encryptDiagnosticsTo = diagnosticsRecipient(process.env.EVAL_DIAGNOSTICS_CERT);
    await assertCandidateRevision(sha);
    const verdict = await runCases(
        cases,
        output,
        sha,
        suiteHash,
        executeCase,
        refreshEvaluationIdentity,
        publishAnswers,
        Math.min(Math.max(Number(process.env.EVAL_CASE_PAUSE_SECONDS ?? 20) || 0, 0), 120) * 1000,
        {
            privateDiagnosticsDirectory: process.env.EVAL_PRIVATE_DIAGNOSTICS_DIRECTORY,
            // Every scenario runs: the point of this tier is the whole picture, not the first failure.
            failFast: false,
            laneOf: extendedLane,
            throttleRetryQuietMs: COST_QUOTA_WINDOW_MS,
            encryptDiagnosticsTo,
            evaluate,
            presentation,
            // In CI the report yields to any running deployment workflow; locally there is nothing to yield to.
            beforeCase: process.env.GITHUB_ACTIONS === "true" ? yieldToGates() : undefined,
            onFinish: (finished, finalVerdict) => {
                if (process.env.GITHUB_ACTIONS !== "true") return;
                for (const line of renderAnnotations(cases, finished, {
                    title: presentation.title,
                    accepted: finalVerdict.accepted,
                }, 10, { notice: !scenarioLayout }))
                    console.log(line);
            },
        },
    );
    if (!verdict.accepted) process.exitCode = 1;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href)
    main().catch((error) => {
        console.error(`AI quality report failed: ${oneLine(error.message)}`);
        process.exitCode = 1;
    });
