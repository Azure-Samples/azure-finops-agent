import { execFile, spawn } from "node:child_process";
import { createHash } from "node:crypto";
import {
    mkdir, mkdtemp, readFile, realpath, rm, stat, writeFile,
} from "node:fs/promises";
import { isAbsolute, join, relative, resolve, sep } from "node:path";
import { setTimeout as delay } from "node:timers/promises";
import { fileURLToPath, pathToFileURL } from "node:url";
import { promisify } from "node:util";
import { JOB_TEMPLATES } from "../../src/Dashboard/frontend/src/data/jobTemplates.js";
import {
    maturityCategories,
    pricingCategory,
} from "../../src/Dashboard/frontend/src/data/sidebarCategories.js";

const rubric =
    "Fulfil the actual question in its language using the returned source evidence. Keep scopes, dates, currencies, units, source freshness and partial coverage explicit. Never invent availability, costs, savings, usage or actions. If the question genuinely lacks required user inputs, ask a concise clarification instead of inventing values. Missing access, a failed tool, or a broken query is not successful fulfilment of a fully specified evidence request. Do not execute writes or purchases; requested changes require the existing application approval flow. Do not claim a chart or generated file unless the corresponding tool actually returned it.";
const repositoryRoot = fileURLToPath(new URL("../../", import.meta.url));
const captureRoot = resolve(repositoryRoot, "tests", "LiveEvaluations", "obj");
const evaluationSourcePaths = [
    join("src", "Dashboard"), "tests", ".github",
    ...["", "src"].flatMap((directory) =>
        [
            "global.json", ".editorconfig", "NuGet.Config",
            "Directory.Build.*", "Directory.Packages.props",
        ].map((name) => `:(icase)${join(directory, name)}`),
    ),
];
const LIVE_SUITE_SIZE = 20;
const THROTTLE_MARGIN_MS = 5000;
// The app waits at most five minutes for a cost retry; allow a bounded margin beyond it.
const MAX_THROTTLE_WAIT_MS = 6 * 60 * 1000;
const CURATED_CASE_IDS = Object.freeze([
    "fa300ef5396c951b", // Crawl maturity
    "967d9300e17ef22a", // Current-month cost by service
    "c0172debe25f1c20", // All-subscription cost comparison
    "89c66e0ad009d9e5", // Costly resource detail
    "75282dc082630fdd", // Cost forecast
    "d7c2e62701ecfd11", // Budget versus actual
    "522cb8a56daeae81", // Advisor savings evidence
    "f756478143fce63c", // Resource Graph inventory
    "81374a24e31aa410", // Tag compliance
    "eac3ecb5ceb83c4b", // License assignments and waste
    "84206ed740e28de4", // Copilot usage and inactive licenses
    "979163d9aeedc2b8", // Chargeback data report
    "e4e6c2296f2dfc97", // Regional VM price comparison
    "095d1c30190c1015", // Storage tier comparison
    "f9b2c97a215d8923", // Cross-service pricing comparison
    "160eca54c580c4f8", // Non-token workload estimate
    "126bedbbf645cbd1", // Batched token pricing per million tokens
    "f75b5b6527c41d5c", // Waste evidence and a reviewable script
    "062a296be5be951f", // H200 Spot incident
    "552f572706ca51cd", // English deterministic CalculateCost incident
]);

export async function assertCandidateRevision(
    sha,
    execute = promisify(execFile),
) {
    if (typeof sha !== "string" || !/^[a-f0-9]{40}$/i.test(sha))
        throw new Error("The full candidate commit SHA is required.");
    let head, status, untrackedSource;
    try {
        const options = {
            cwd: repositoryRoot, encoding: "utf8", timeout: 30000,
        };
        ({ stdout: head } = await execute(
            "git",
            ["rev-parse", "--verify", "HEAD"],
            options,
        ));
        ({ stdout: status } = await execute(
            "git",
            [
                "status", "--porcelain=v1",
                "--untracked-files=no", "--ignore-submodules=none",
                "--", ...evaluationSourcePaths,
            ],
            options,
        ));
        ({ stdout: untrackedSource } = await execute(
            "git",
            [
                "ls-files", "--others", "--exclude-standard", "--",
                ...evaluationSourcePaths,
            ],
            options,
        ));
    } catch {
        throw new Error(
            "Unable to verify the candidate git revision and source cleanliness.",
        );
    }
    if (
        typeof head !== "string" ||
        head.trim().toLowerCase() !== sha.toLowerCase()
    )
        throw new Error(
            "The candidate commit SHA does not match the checked-out git HEAD.",
        );
    if (typeof status !== "string" || status.trim())
        throw new Error(
            "Tracked source has uncommitted changes; evaluate a clean candidate revision.",
        );
    if (typeof untrackedSource !== "string" || untrackedSource.trim())
        throw new Error(
            "Untracked application, evaluation, or workflow source is present; evaluate a clean candidate revision.",
        );
}

export function buildCatalog() {
    const cases = new Map();
    const add = (
        question,
        label,
        origin,
        requiredTools = [],
        forbiddenTools = [],
    ) => {
        const key = question.trim();
        const existing = cases.get(key);
        if (existing) {
            existing.origins.push(origin);
            return;
        }
        cases.set(key, {
            id: createHash("sha256").update(key).digest("hex").slice(0, 16),
            question: key,
            label,
            origins: [origin],
            rubric,
            requiredTools,
            forbiddenTools,
            maxToolCalls: 30,
            maxDurationSeconds: 600,
        });
    };
    for (const category of maturityCategories)
        for (const prompt of category.prompts ?? [])
            add(
                prompt.prompt,
                prompt.label,
                `sidebar:${category.key}`,
                prompt.label === "Score Crawl maturity"
                    ? ["GetCrawlMaturityEvidence"]
                    : [],
                prompt.label === "Score Crawl maturity"
                    ? ["QueryAzure", "ReportMaturityScore", "SuggestFollowUp"]
                    : [],
            );
    for (const [type, prompts] of [
        ["public", pricingCategory.publicPrompts],
        ["connected", pricingCategory.connectedPrompts],
    ])
        for (const prompt of prompts ?? [])
            add(prompt.prompt, prompt.label, `pricing:${type}`);
    for (const template of JOB_TEMPLATES)
        add(template.prompt, template.label, "job-template:chat");
    add(
        "in which regions can I get h200 on spot quota?",
        "H200 Spot quota",
        "incident:compute",
        ["CheckComputeFeasibility"],
    );
    add(
        "Use CalculateCost for 2 units at USD 3 per unit, period month, zero discount and tax. Report the total in English. Do not look up prices.",
        "English calculator result",
        "incident:language",
        ["CalculateCost"],
    );
    return [...cases.values()];
}

export function buildSuite(catalog = buildCatalog()) {
    if (
        CURATED_CASE_IDS.length !== LIVE_SUITE_SIZE ||
        new Set(CURATED_CASE_IDS).size !== LIVE_SUITE_SIZE
    )
        throw new Error("The curated live suite must select exactly 20 distinct case IDs.");
    const selected = CURATED_CASE_IDS.map((id) => {
        const matches = catalog.filter((item) => item.id === id);
        if (matches.length !== 1)
            throw new Error(
                `Curated representative case ${id} is missing or duplicated in the catalog.`,
            );
        return matches[0];
    });
    if (
        selected.some((item) =>
            typeof item.question !== "string" || !item.question.trim()) ||
        new Set(selected.map((item) => item.question)).size !== LIVE_SUITE_SIZE
    )
        throw new Error("The curated live suite must contain exactly 20 distinct, nonempty questions.");
    return selected;
}

export function validateResult(scenario, result, exitCode, sha, suiteHash) {
    const failures = [];
    if (exitCode !== 0)
        failures.push("Evaluation process failed or timed out.");
    if (!result || typeof result !== "object" || Array.isArray(result))
        return ["No structured evaluation result."];
    if (result.id !== scenario.id || result.question !== scenario.question)
        failures.push("Result does not match the planned case.");
    if (result.sha !== sha || result.suiteHash !== suiteHash)
        failures.push("Result belongs to another revision or suite.");
    if (
        result.accepted !== true ||
        result.terminal !== true ||
        result.transcriptVerified !== true
    )
        failures.push("Execution, replay, or judge did not accept the result.");
    if (
        !Number.isFinite(result.durationMs) ||
        result.durationMs < 0 ||
        result.durationMs > scenario.maxDurationSeconds * 1000
    )
        failures.push("Invalid or over-budget run duration.");
    if (typeof result.answer !== "string" || !result.answer.trim())
        failures.push("Final answer is empty.");
    if (
        !Array.isArray(result.tools) ||
        result.toolCount !== result.tools.length ||
        result.tools.length > scenario.maxToolCalls
    )
        failures.push("Tool count is invalid or over budget.");
    else {
        if (
            result.tools.some(
                (tool) =>
                    !tool ||
                    typeof tool.name !== "string" ||
                    tool.success !== true,
            )
        )
            failures.push("A tool failed.");
        for (const name of scenario.requiredTools)
            if (!result.tools.some((tool) => tool?.name === name))
                failures.push(`Required tool missing: ${name}.`);
        for (const name of scenario.forbiddenTools)
            if (result.tools.some((tool) => tool?.name === name))
                failures.push(`Forbidden tool called: ${name}.`);
    }
    if (!Array.isArray(result.errors) || result.errors.length)
        failures.push("Run contains errors.");
    if (!Array.isArray(result.reasons) || result.reasons.length)
        failures.push("Judge or deterministic checks rejected the answer.");
    if (
        typeof result.judge?.accepted !== "boolean" ||
        typeof result.judge?.grounded !== "boolean" ||
        typeof result.judge?.complete !== "boolean" ||
        typeof result.judge?.reason !== "string" ||
        !result.judge.reason.trim()
    )
        failures.push("Missing or invalid structured judge verdict.");
    else if (
        !result.judge.accepted ||
        !result.judge.grounded ||
        !result.judge.complete
    )
        failures.push("Structured judge rejected the answer.");
    return failures;
}

export function evaluateSuite(cases, results, sha, suiteHash) {
    const failures = [];
    const expected = new Map(buildSuite().map((item) => [item.id, item]));
    if (cases.length !== LIVE_SUITE_SIZE)
        failures.push("Exactly 20 curated representative scenarios are required.");
    if (cases.some((item) => {
        const original = expected.get(item.id);
        return !original || [
            "question", "rubric", "requiredTools", "forbiddenTools",
            "maxToolCalls", "maxDurationSeconds",
        ].some((key) => JSON.stringify(item[key]) !== JSON.stringify(original[key]));
    }))
        failures.push("Planned scenarios must match the curated selection and its original case contracts.");
    if (
        new Set(cases.map((item) => item.id)).size !== cases.length ||
        new Set(cases.map((item) => item.question)).size !== cases.length
    )
        failures.push("Duplicate planned scenarios.");
    if (results.length !== cases.length)
        failures.push("Missing or extra scenario results.");
    const seen = new Set();
    for (const row of results) {
        if (seen.has(row.id)) failures.push("Duplicate scenario result.");
        seen.add(row.id);
        const scenario = expected.get(row.id);
        if (!scenario) {
            failures.push("Unplanned result.");
            continue;
        }
        for (const failure of validateResult(
            scenario,
            row.result,
            row.exitCode,
            sha,
            suiteHash,
        ))
            failures.push(`${scenario.id}: ${failure}`);
    }
    for (const scenario of expected.values())
        if (!seen.has(scenario.id))
            failures.push(`${scenario.id}: Missing result.`);
    return {
        accepted: failures.length === 0,
        selection: "curated-representative",
        required: LIVE_SUITE_SIZE,
        planned: cases.length,
        completed: results.length,
        failures,
    };
}

const html = (value) =>
    String(value ?? "")
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;")
        .replaceAll('"', "&quot;")
        .replaceAll("'", "&#39;");
const cell = (value) =>
    html(value)
        .replaceAll("|", "&#124;")
        .replaceAll("\r", "")
        .replaceAll("\n", " ");

export function renderSummary(
    cases,
    results,
    verdict,
    sha,
    publishAnswers = true,
) {
    results = results.map((row) => ({
        ...row,
        result: publishableResult(row.result, publishAnswers),
    }));
    const preview = (value, maximum) => {
        const escaped = html(value);
        return escaped.length <= maximum
            ? escaped
            : escaped.slice(0, maximum) +
                  " [Preview shortened; full answer in results artifact.]";
    };
    const lines = [
        "## Live AI Deployment Gate",
        "",
        `**${verdict.accepted ? "PASS" : "FAIL"}** · ${results.filter((row) => row.failures.length === 0).length}/${LIVE_SUITE_SIZE} accepted · Revision \`${sha}\``,
        "",
        `Curated representative live suite: exactly ${LIVE_SUITE_SIZE} question types, not a verified usage-frequency ranking. Every selected case must pass its original checks.`,
        "",
        "Real model calls through the candidate chat handler and tools, with a separate structured judge. Test-host authentication is not a browser sign-in test. Job templates here test answer/tool routing, not scheduler execution.",
        ...(publishAnswers
            ? []
            : [
                  "",
                  "Answers and judge rationale are withheld because this run used internal tenant data; only verdicts and deterministic checks are published.",
              ]),
        "",
        "| Question | Tools | Seconds | Result | Reason |",
        "|---|---:|---:|---|---|",
    ];
    for (const scenario of cases) {
        const row = results.find((result) => result.id === scenario.id);
        lines.push(
            `| ${cell(scenario.question)} | ${row?.result?.toolCount ?? "-"} | ${Number.isFinite(row?.result?.durationMs) ? (row.result.durationMs / 1000).toFixed(1) : "-"} | ${row && !row.failures.length ? "PASS" : "FAIL"} | ${cell(row?.failures.join(" ") || (row ? "Accepted" : "Missing result"))} |`,
        );
    }
    for (const row of results) {
        const scenario = cases.find((item) => item.id === row.id);
        lines.push(
            "",
            `<details><summary>${cell(scenario?.label ?? row.id)}: ${row.failures.length ? "FAIL" : "PASS"}</summary>`,
            "",
            `<p><strong>Question:</strong> ${html(scenario?.question)}</p>`,
            `<p><strong>Tools:</strong> ${preview(Array.isArray(row.result?.tools) ? row.result.tools.map((tool) => `${tool?.name ?? "unknown"}: ${tool?.success ? "ok" : "failed"}`).join(", ") : "Not recorded", 2000)}</p>`,
            `<p><strong>First token:</strong> ${html(row.result?.firstTokenMs ?? "not observed")} ms</p>`,
            ...(publishAnswers
                ? [
                      `<p><strong>Judge:</strong> ${preview(row.result?.judge?.reason ?? "No verdict", 1200)}</p>`,
                      `<pre>${preview(row.result?.answer ?? "No final answer.", 2400)}</pre>`,
                  ]
                : [
                      `<p><strong>Checks:</strong> ${html(row.failures.join(" ") || "All deterministic checks and the judge accepted the answer.")}</p>`,
                  ]),
            "</details>",
        );
    }
    return lines.join("\n") + "\n";
}

export function publishableResult(result, publishAnswers) {
    if (!result || typeof result !== "object" || Array.isArray(result))
        return null;
    const { answer, errors, reasons, judge } = result;
    // Opt-in diagnostics and future private extensions are never public, even for synthetic data.
    const published = {
        id: typeof result.id === "string" ? result.id : null,
        question: typeof result.question === "string" ? result.question : null,
        sha: typeof result.sha === "string" ? result.sha : null,
        suiteHash: typeof result.suiteHash === "string" ? result.suiteHash : null,
        accepted: result.accepted === true,
        terminal: result.terminal === true,
        transcriptVerified: result.transcriptVerified === true,
        durationMs: Number.isFinite(result.durationMs) ? result.durationMs : null,
        firstTokenMs: Number.isFinite(result.firstTokenMs)
            ? result.firstTokenMs : null,
        toolCount: Number.isFinite(result.toolCount) ? result.toolCount : null,
        failedTools: Number.isFinite(result.failedTools)
            ? result.failedTools : null,
        tools: Array.isArray(result.tools)
            ? result.tools.map((tool) => ({
                  name: typeof tool?.name === "string" ? tool.name : "unknown",
                  success: tool?.success === true,
              }))
            : [],
        judge: judge
            ? {
                  accepted: judge.accepted === true,
                  grounded: judge.grounded === true,
                  complete: judge.complete === true,
              }
            : null,
        attempts: Number.isInteger(result.attempts) ? result.attempts : 1,
        throttle: {
            notices: Number.isInteger(result.throttle?.notices)
                ? result.throttle.notices : 0,
            final: result.throttle?.final === true,
            retryAtUtc: Number.isFinite(Date.parse(result.throttle?.retryAtUtc ?? ""))
                ? new Date(result.throttle.retryAtUtc).toISOString() : null,
        },
    };
    if (!publishAnswers)
        return {
            ...published,
            answerWithheld: typeof answer === "string" && answer.length > 0,
            errorCount: Array.isArray(errors) ? errors.length : 0,
            reasonCount: Array.isArray(reasons) ? reasons.length : 0,
        };
    return {
        ...published,
        answer: typeof answer === "string" ? answer : null,
        errors: Array.isArray(errors)
            ? errors.map((error) => typeof error === "string" ? error : "Non-text error withheld.")
            : null,
        reasons: Array.isArray(reasons)
            ? reasons.map((reason) => typeof reason === "string" ? reason : "Non-text reason withheld.")
            : null,
        judge: published.judge
            ? {
                  ...published.judge,
                  reason: typeof judge.reason === "string" ? judge.reason : null,
              }
            : null,
    };
}

const interruption = (signal) =>
    Object.assign(
        new Error(
            `Live evaluation suite interrupted by ${signal}; no remaining cases will run.`,
        ),
        { code: "EVAL_INTERRUPTED" },
    );

function assertLocalPrivateDiagnostics(directory, environment) {
    if (directory && ["GITHUB_ACTIONS", "CI", "TF_BUILD"].some((key) => {
        const value = String(environment[key] ?? "").trim().toLowerCase();
        return value && value !== "false" && value !== "0";
    }))
        throw new Error("Private evaluation diagnostics are local-only and cannot be enabled in CI.");
}

const containsPath = (root, candidate) => {
    const path = relative(root, candidate);
    return !isAbsolute(path) && path !== ".." && !path.startsWith(`..${sep}`);
};

async function privateDiagnosticsLocation(directory, output, sourceRoot) {
    if (typeof directory !== "string" || !isAbsolute(directory))
        throw new Error("Private diagnostics require an existing absolute directory.");
    let location, source, published;
    try {
        [location, source, published] = await Promise.all([
            realpath(directory), realpath(sourceRoot), realpath(output),
        ]);
        if (!(await stat(location)).isDirectory())
            throw new Error("Not a directory.");
    } catch {
        throw new Error("Private diagnostics require an existing, accessible directory.");
    }
    if ([source, published].some((root) =>
        containsPath(root, location) || containsPath(location, root)))
        throw new Error(
            "Private diagnostics must not overlap the repository or published output, including symlink targets.",
        );
    return location;
}

export async function preparePrivateDiagnostics(
    directory,
    output,
    environment = process.env,
    sourceRoot = repositoryRoot,
) {
    if (directory === undefined || directory === "") return null;
    assertLocalPrivateDiagnostics(directory, environment);
    const location = await privateDiagnosticsLocation(directory, output, sourceRoot);
    let captureDirectory;
    try {
        captureDirectory = await mkdtemp(join(location, "live-evaluations-"));
    } catch {
        throw new Error("Private diagnostics retention setup failed; no cases were started.");
    }
    return privateDiagnosticsLocation(captureDirectory, output, sourceRoot);
}

export async function runCases(
    cases,
    output,
    sha,
    suiteHash,
    execute = executeCase,
    renew = refreshEvaluationIdentity,
    publishAnswers = true,
    pauseMs = 0,
    {
        signals = process,
        privateDiagnosticsDirectory,
        environment = process.env,
        diagnosticsSourceRoot = repositoryRoot,
    } = {},
) {
    const results = [];
    await mkdir(output, { recursive: true });
    await mkdir(captureRoot, { recursive: true });
    const captureRootFromOutput = relative(
        await realpath(output),
        await realpath(captureRoot),
    );
    if (
        !isAbsolute(captureRootFromOutput) &&
        captureRootFromOutput !== ".." &&
        !captureRootFromOutput.startsWith(`..${sep}`)
    )
        throw new Error(
            "The published output directory cannot contain private captures.",
        );
    const retainedCapture = await preparePrivateDiagnostics(
        privateDiagnosticsDirectory, output, environment, diagnosticsSourceRoot,
    );
    // Default captures remain disposable; retained captures require explicit local opt-in.
    const captureDirectory = retainedCapture ?? await mkdtemp(
        resolve(captureRoot, ".live-evaluation-capture-"),
    );
    const controller = new AbortController();
    let runFailure, finalVerdict;
    const stop = (signal) => {
        if (controller.signal.aborted) return;
        runFailure = interruption(signal);
        controller.abort(runFailure);
    };
    const interrupt = () => stop("SIGINT");
    const terminate = () => stop("SIGTERM");
    signals.on("SIGINT", interrupt);
    signals.on("SIGTERM", terminate);
    const report = async () => {
        const failure = runFailure;
        const verdict = evaluateSuite(cases, results, sha, suiteHash);
        if (failure) {
            verdict.accepted = false;
            verdict.failures.push(failure.message);
        }
        const publishedResults = results.map((row) => ({
            ...row,
            result: publishableResult(row.result, publishAnswers),
        }));
        await writeFile(
            resolve(output, "results.json"),
            JSON.stringify(
                {
                    sha,
                    suiteHash,
                    cases,
                    results: publishedResults,
                    verdict,
                },
                null,
                2,
            ),
        );
        await writeFile(
            resolve(output, "summary.md"),
            renderSummary(
                cases, results, verdict, sha, publishAnswers,
            ) + (failure ? `\n**Suite failure:** ${html(failure.message)}\n` : ""),
        );
        if (runFailure !== failure) return report();
        return verdict;
    };
    try {
        await report();
        let cooldownUntil = 0;
        const waitForCooldown = async (minimumMs) => {
            // Honour the service's retry deadline so the next attempt does not start inside a Cost Management cooldown.
            const remaining = Math.min(
                Math.max(cooldownUntil - Date.now() + THROTTLE_MARGIN_MS, 0),
                MAX_THROTTLE_WAIT_MS,
            );
            const waitMs = Math.max(minimumMs, remaining);
            if (waitMs > 0) {
                if (remaining > minimumMs)
                    console.log(`Waiting ${Math.ceil(waitMs / 1000)}s for a reported service cooldown.`);
                await delay(waitMs, undefined, { signal: controller.signal });
            }
        };
        const noteThrottle = (result) => {
            const retryAt = Date.parse(result?.throttle?.retryAtUtc ?? "");
            if (Number.isFinite(retryAt)) cooldownUntil = Math.max(cooldownUntil, retryAt);
            return result?.throttle?.final === true;
        };
        for (const [index, scenario] of cases.entries()) {
            controller.signal.throwIfAborted();
            // Spaces cases so the suite does not throttle tenant-wide Cost Management quota itself.
            await waitForCooldown(index > 0 ? pauseMs : 0);
            controller.signal.throwIfAborted();
            await renew();
            controller.signal.throwIfAborted();
            const resultPath = resolve(captureDirectory, `${scenario.id}.json`);
            const runAttempt = async () => {
                const exitCode = await execute(
                    scenario,
                    resultPath,
                    sha,
                    suiteHash,
                    undefined,
                    { signal: controller.signal },
                );
                controller.signal.throwIfAborted();
                let result;
                try {
                    result = JSON.parse(await readFile(resultPath, "utf8"));
                } catch {
                    result = null;
                }
                const failures = validateResult(
                    scenario,
                    result,
                    exitCode,
                    sha,
                    suiteHash,
                );
                return { exitCode, result, failures };
            };
            let { exitCode, result, failures } = await runAttempt();
            let attempts = 1;
            // A final service throttle is an environmental refusal, not an agent verdict: rerun once, unchanged, after the deadline.
            if (noteThrottle(result) && failures.length > 0) {
                console.log(`[${index + 1}/${cases.length}] ${scenario.id} throttled by the service; retrying once after cooldown.`);
                await waitForCooldown(pauseMs);
                controller.signal.throwIfAborted();
                await renew();
                controller.signal.throwIfAborted();
                ({ exitCode, result, failures } = await runAttempt());
                noteThrottle(result);
                attempts = 2;
            }
            if (result && typeof result === "object" && !Array.isArray(result))
                result.attempts = attempts;
            if (retainedCapture && failures.length === 0) {
                try {
                    const location = await privateDiagnosticsLocation(
                        captureDirectory, output, diagnosticsSourceRoot,
                    );
                    await Promise.all([
                        rm(join(location, `${scenario.id}.json`), { force: true }),
                        rm(join(location, `${scenario.id}.json.tmp`), { force: true }),
                    ]);
                } catch {
                    runFailure = new Error(
                        "Private evaluation diagnostics cleanup failed; passing captures could not be removed.",
                    );
                    throw runFailure;
                }
            }
            await writeFile(
                resolve(output, `${scenario.id}.json`),
                JSON.stringify(publishableResult(result, publishAnswers)),
            );
            results.push({ id: scenario.id, exitCode, result, failures });
            await report();
            controller.signal.throwIfAborted();
            const published = publishableResult(result, publishAnswers);
            console.log(
                `[${index + 1}/${cases.length}] ${scenario.id} ${failures.length ? "FAIL" : "PASS"} tools=${published?.toolCount ?? "?"} durationMs=${published?.durationMs ?? "?"} ${scenario.label}`,
            );
        }
    } catch {
        runFailure ??= new Error(
            "Evaluation execution failed; no remaining cases will run.",
        );
    } finally {
        if (retainedCapture) {
            try {
                const location = await privateDiagnosticsLocation(
                    captureDirectory, output, diagnosticsSourceRoot,
                );
                await writeFile(
                    join(location, "diagnostics.json"),
                    JSON.stringify({
                        sha, suiteHash, cases,
                        completed: results.length,
                        results: results.filter((row) => row.failures.length > 0),
                        failure: runFailure?.message ?? null,
                    }, null, 2),
                    { mode: 0o600, flag: "wx" },
                );
            } catch {
                const failure = "Private evaluation diagnostics retention failed; local captures may be incomplete.";
                runFailure = new Error(
                    runFailure ? `${runFailure.message} ${failure}` : failure,
                );
            }
        } else {
            try {
                await rm(captureDirectory, { recursive: true, force: true });
            } catch {
                runFailure ??= new Error("Private evaluation capture cleanup failed.");
            }
        }
        try {
            finalVerdict = await report();
        } finally {
            signals.removeListener("SIGINT", interrupt);
            signals.removeListener("SIGTERM", terminate);
        }
    }
    if (runFailure) throw runFailure;
    return finalVerdict;
}

export async function terminateProcessTree(
    child,
    platform = process.platform,
    execute = promisify(execFile),
    kill = process.kill.bind(process),
) {
    if (!Number.isSafeInteger(child.pid) || child.pid <= 0)
        throw new Error("Evaluation child process has no valid PID.");
    if (platform === "win32") {
        await execute(
            "taskkill",
            ["/PID", String(child.pid), "/T", "/F"],
            { windowsHide: true, timeout: 10000 },
        );
    } else {
        try {
            kill(-child.pid, "SIGKILL");
        } catch (error) {
            if (error.code !== "ESRCH") throw error;
        }
    }
}

export async function executeCase(
    scenario,
    resultPath,
    sha,
    suiteHash,
    spawnProcess = spawn,
    {
        signal,
        signals = process,
        terminateTree = terminateProcessTree,
        startTimer = setTimeout,
        cancelTimer = clearTimeout,
    } = {},
) {
    signal?.throwIfAborted();
    await Promise.all([
        rm(resultPath, { force: true }),
        rm(`${resultPath}.tmp`, { force: true }),
    ]);
    signal?.throwIfAborted();
    return new Promise((resolveResult, rejectResult) => {
        const child = spawnProcess(
            "dotnet",
            [
                process.env.EVAL_RUNNER_DLL ??
                    "tests/LiveEvaluations/bin/Release/net10.0/LiveEvaluations.dll",
            ],
            {
                cwd: repositoryRoot,
                env: {
                    ...process.env,
                    EVAL_CASE_ID: scenario.id,
                    EVAL_QUESTION: scenario.question,
                    EVAL_RUBRIC: scenario.rubric,
                    EVAL_REQUIRED_TOOLS: scenario.requiredTools.join(","),
                    EVAL_FORBIDDEN_TOOLS: scenario.forbiddenTools.join(","),
                    EVAL_MAX_TOOL_CALLS: String(scenario.maxToolCalls),
                    EVAL_MAX_DURATION_SECONDS: String(
                        scenario.maxDurationSeconds,
                    ),
                    EVAL_RESULT_PATH: resultPath,
                    EVAL_EXPECTED_SHA: sha,
                    EVAL_SUITE_HASH: suiteHash,
                },
                stdio: ["ignore", "ignore", "ignore"],
                detached: process.platform !== "win32",
            },
        );
        let timedOut = false;
        let completed = false;
        let interrupted, termination;
        const terminate = (error) => {
            if (completed) return;
            interrupted ??= error;
            if (termination) return;
            timedOut = true;
            termination = Promise.resolve().then(() => terminateTree(child));
            termination.catch(() => complete(-1));
        };
        const timer = startTimer(
            () => terminate(),
            (scenario.maxDurationSeconds + 330) * 1000,
        );
        const interrupt = () => terminate(interruption("SIGINT"));
        const stop = () => terminate(interruption("SIGTERM"));
        const abort = () => terminate(signal.reason);
        const failed = () => complete(-1);
        const complete = async (code) => {
            if (completed) return;
            completed = true;
            cancelTimer(timer);
            signals.removeListener("SIGTERM", stop);
            signals.removeListener("SIGINT", interrupt);
            signal?.removeEventListener("abort", abort);
            child.removeListener("error", failed);
            child.removeListener("close", complete);
            try {
                await termination;
                if (interrupted) rejectResult(interrupted);
                else resolveResult(timedOut ? -1 : (code ?? -1));
            } catch {
                rejectResult(new Error(
                    "The owned evaluation process tree could not be terminated.",
                ));
            }
        };
        child.once("error", failed);
        child.once("close", complete);
        if (signal) {
            signal.addEventListener("abort", abort, { once: true });
            if (signal.aborted) abort();
        } else {
            signals.on("SIGTERM", stop);
            signals.on("SIGINT", interrupt);
        }
    });
}

async function main() {
    const cases = buildSuite();
    const sha = process.env.GITHUB_SHA ?? process.env.EVAL_EXPECTED_SHA;
    if (!sha || !/^[a-f0-9]{40}$/i.test(sha))
        throw new Error("The candidate commit SHA is required.");
    const suiteHash = createHash("sha256")
        .update(JSON.stringify(cases))
        .digest("hex");
    const output = resolve(
        process.env.EVAL_OUTPUT_DIRECTORY ?? "TestResults/live-evaluations",
    );
    const classification = process.env.EVAL_DATA_CLASSIFICATION ?? "";
    const publishAnswers =
        classification === "synthetic" ||
        (classification !== "internal-test" &&
            process.env.GITHUB_ACTIONS !== "true");
    await mkdir(output, { recursive: true });
    const initialVerdict = evaluateSuite(cases, [], sha, suiteHash);
    await writeFile(
        resolve(output, "summary.md"),
        renderSummary(cases, [], initialVerdict, sha, publishAnswers),
    );
    assertLocalPrivateDiagnostics(
        process.env.EVAL_PRIVATE_DIAGNOSTICS_DIRECTORY, process.env,
    );
    for (const name of [
        "EVAL_MODEL_ENDPOINT",
        "EVAL_MODEL",
        "EVAL_JUDGE_MODEL",
        "EVAL_TENANT_ID",
        "EVAL_SUBSCRIPTION_IDS",
    ])
        if (!process.env[name]?.trim())
            throw new Error(
                `Required evaluation configuration missing: ${name}.`,
            );
    if (
        process.env.GITHUB_ACTIONS === "true" &&
        !["synthetic", "internal-test"].includes(classification)
    )
        throw new Error(
            "Set EVAL_DATA_CLASSIFICATION to synthetic (answers published) or internal-test (verdicts only).",
        );
    if (cases.length !== LIVE_SUITE_SIZE)
        throw new Error("The curated live suite must contain exactly 20 distinct questions.");
    await assertCandidateRevision(sha);
    const only = process.env.EVAL_ONLY_IDS?.split(",").filter(Boolean) ?? [];
    if (only.length && process.env.GITHUB_ACTIONS === "true")
        throw new Error(
            "EVAL_ONLY_IDS is a local diagnostic filter and cannot run in CI.",
        );
    const planned = only.length
        ? cases.filter((item) => only.includes(item.id))
        : cases;
    const verdict = await runCases(
        planned,
        output,
        sha,
        suiteHash,
        executeCase,
        refreshEvaluationIdentity,
        publishAnswers,
        Math.min(
            Math.max(Number(process.env.EVAL_CASE_PAUSE_SECONDS ?? 20) || 0, 0),
            120,
        ) * 1000,
        {
            privateDiagnosticsDirectory:
                process.env.EVAL_PRIVATE_DIAGNOSTICS_DIRECTORY,
        },
    );
    if (!verdict.accepted) process.exitCode = 1;
}

export async function refreshEvaluationIdentity(
    environment = process.env,
    request = fetch,
    execute = promisify(execFile),
) {
    if (environment.GITHUB_ACTIONS !== "true") return;
    try {
        for (const key of [
            "ACTIONS_ID_TOKEN_REQUEST_URL",
            "ACTIONS_ID_TOKEN_REQUEST_TOKEN",
            "EVAL_CLIENT_ID",
            "EVAL_TENANT_ID",
            "EVAL_LOGIN_SUBSCRIPTION",
        ])
            if (!environment[key])
                throw new Error("Missing identity configuration.");
        const url = new URL(environment.ACTIONS_ID_TOKEN_REQUEST_URL);
        if (url.protocol !== "https:")
            throw new Error("Invalid identity endpoint.");
        url.searchParams.set("audience", "api://AzureADTokenExchange");
        const response = await request(url, {
            headers: {
                Authorization: `Bearer ${environment.ACTIONS_ID_TOKEN_REQUEST_TOKEN}`,
            },
            signal: AbortSignal.timeout(30000),
            redirect: "error",
        });
        if (!response.ok) throw new Error("Identity request failed.");
        const token = (await response.json()).value;
        if (typeof token !== "string" || !token)
            throw new Error("Identity token missing.");
        await execute(
            "az",
            [
                "login",
                "--service-principal",
                "--username",
                environment.EVAL_CLIENT_ID,
                "--tenant",
                environment.EVAL_TENANT_ID,
                "--federated-token",
                token,
                "--output",
                "none",
            ],
            { timeout: 60000 },
        );
        await execute(
            "az",
            [
                "account",
                "set",
                "--subscription",
                environment.EVAL_LOGIN_SUBSCRIPTION,
            ],
            { timeout: 30000 },
        );
    } catch {
        throw new Error(
            "Evaluation OIDC renewal failed; no remaining cases or deployment can be accepted.",
        );
    }
}

if (
    process.argv[1] &&
    import.meta.url === pathToFileURL(resolve(process.argv[1])).href
)
    main().catch((error) => {
        console.error(`Live evaluation gate failed: ${error.message}`);
        process.exitCode = 1;
    });
