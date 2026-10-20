// The extended evaluation tier: node tests/LiveEvaluations/quality.mjs
//
// Runs every scenario the deployment gate does not (the unused catalog plus scenarios grounded in real customer
// questions, safety invariants and other languages) against the same candidate handler, tools and judge, and reports
// each one red or green. It never blocks a deployment: the gate in suite.mjs stays at its 23 curated scenarios. This run
// fails (red) only when the share of scored scenarios that pass drops below the quality bar, or when it cannot complete.

import { createHash } from "node:crypto";
import { execFile } from "node:child_process";
import { mkdir, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { setTimeout as delay } from "node:timers/promises";
import { pathToFileURL } from "node:url";
import { promisify } from "node:util";
import { GROUP_META, renderAnnotations, scoreboard } from "./report.mjs";
import { buildExtendedSuite, extendedLane } from "./scenarios.mjs";
import {
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
    if (board.scored === 0) failures.push("No scenario was scored.");
    else if (board.passRate < threshold)
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

export function selectScenarios(all, groupsText = "") {
    const groups = groupsText.split(",").map((group) => group.trim()).filter(Boolean);
    const unknown = groups.filter((group) => !GROUP_META[group]);
    // Names come from a workflow input: quoted and stripped of control characters, never echoed raw.
    if (unknown.length) throw new Error(`Unknown scenario group: ${unknown.map((group) => JSON.stringify(oneLine(group).slice(0, 40))).join(", ")}.`);
    return groups.length ? all.filter((scenario) => groups.includes(scenario.group)) : all;
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

async function main() {
    const sha = process.env.GITHUB_SHA ?? process.env.EVAL_EXPECTED_SHA;
    if (!sha || !/^[a-f0-9]{40}$/i.test(sha)) throw new Error("The candidate commit SHA is required.");
    const expected = buildExtendedSuite();
    const only = process.env.EVAL_ONLY_IDS?.split(",").filter(Boolean) ?? [];
    if (only.length && process.env.GITHUB_ACTIONS === "true")
        throw new Error("EVAL_ONLY_IDS is a local diagnostic filter and cannot run in CI.");
    if (process.env.GITHUB_ACTIONS === "true" && process.env.EVAL_PRIVATE_DIAGNOSTICS_DIRECTORY)
        throw new Error("Private evaluation diagnostics are local-only and cannot be configured in CI.");
    const selected = selectScenarios(expected, process.env.EVAL_GROUPS);
    const cases = only.length ? selected.filter((scenario) => only.includes(scenario.id)) : selected;
    if (!cases.length) throw new Error("No scenario matches the selection.");
    const threshold = parseThreshold(process.env.EVAL_QUALITY_THRESHOLD);
    const suiteHash = createHash("sha256").update(JSON.stringify(cases)).digest("hex");
    const output = resolve(process.env.EVAL_OUTPUT_DIRECTORY ?? "TestResults/live-evaluations");
    const classification = process.env.EVAL_DATA_CLASSIFICATION ?? "";
    const publishAnswers =
        classification === "synthetic" ||
        (classification !== "internal-test" && process.env.GITHUB_ACTIONS !== "true");
    const presentation = presentationFor(cases, threshold, cases.length !== expected.length);
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
                }))
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
