import { execFile, spawn } from "node:child_process";
import { createHash } from "node:crypto";
import { mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { promisify } from "node:util";
import { JOB_TEMPLATES } from "../../src/Dashboard/frontend/src/data/jobTemplates.js";
import {
    maturityCategories,
    pricingCategory,
} from "../../src/Dashboard/frontend/src/data/sidebarCategories.js";

const rubric =
    "Fulfil the actual question in its language using the returned source evidence. Keep scopes, dates, currencies, units, source freshness and partial coverage explicit. Never invent availability, costs, savings, usage or actions. If the question genuinely lacks required user inputs, ask a concise clarification instead of inventing values. Missing access, a failed tool, or a broken query is not successful fulfilment of a fully specified evidence request. Do not execute writes or purchases; requested changes require the existing application approval flow. Do not claim a chart or generated file unless the corresponding tool actually returned it.";

export function buildSuite() {
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

export function validateResult(scenario, result, exitCode, sha, suiteHash) {
    const failures = [];
    if (exitCode !== 0)
        failures.push("Evaluation process failed or timed out.");
    if (!result || typeof result !== "object")
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
        result.judge?.accepted !== true ||
        result.judge?.grounded !== true ||
        result.judge?.complete !== true ||
        typeof result.judge?.reason !== "string" ||
        !result.judge.reason.trim()
    )
        failures.push("Missing or invalid structured judge verdict.");
    return failures;
}

export function evaluateSuite(cases, results, sha, suiteHash) {
    const failures = [];
    if (cases.length < 100)
        failures.push("At least 100 distinct scenarios are required.");
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
        const scenario = cases.find((item) => item.id === row.id);
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
    for (const scenario of cases)
        if (!seen.has(scenario.id))
            failures.push(`${scenario.id}: Missing result.`);
    return {
        accepted: failures.length === 0,
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
        `**${verdict.accepted ? "PASS" : "FAIL"}** · ${results.filter((row) => row.failures.length === 0).length}/${cases.length} accepted · Revision \`${sha}\``,
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
    if (publishAnswers || !result || typeof result !== "object") return result;
    const { answer, errors, reasons, judge, failedToolDetails, ...rest } =
        result;
    return {
        ...rest,
        answerWithheld: typeof answer === "string" && answer.length > 0,
        errorCount: Array.isArray(errors) ? errors.length : 0,
        reasonCount: Array.isArray(reasons) ? reasons.length : 0,
        judge: judge
            ? {
                  accepted: judge.accepted,
                  grounded: judge.grounded,
                  complete: judge.complete,
              }
            : null,
    };
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
) {
    const results = [];
    await mkdir(output, { recursive: true });
    const report = async () => {
        const verdict = evaluateSuite(cases, results, sha, suiteHash);
        await writeFile(
            resolve(output, "results.json"),
            JSON.stringify(
                {
                    sha,
                    suiteHash,
                    cases,
                    results: results.map((row) => ({
                        ...row,
                        result: publishableResult(row.result, publishAnswers),
                    })),
                    verdict,
                },
                null,
                2,
            ),
        );
        await writeFile(
            resolve(output, "summary.md"),
            renderSummary(cases, results, verdict, sha, publishAnswers),
        );
        return verdict;
    };
    await report();
    try {
        for (const [index, scenario] of cases.entries()) {
            // Spaces cases so the suite does not throttle tenant-wide Cost Management quota itself.
            if (index > 0 && pauseMs > 0)
                await new Promise((done) => setTimeout(done, pauseMs));
            await renew();
            const resultPath = resolve(output, `${scenario.id}.json`);
            const exitCode = await execute(
                scenario,
                resultPath,
                sha,
                suiteHash,
            );
            let result;
            try {
                result = JSON.parse(await readFile(resultPath, "utf8"));
            } catch {
                result = null;
            }
            if (!publishAnswers)
                await writeFile(
                    resultPath,
                    JSON.stringify(publishableResult(result, false)),
                );
            const failures = validateResult(
                scenario,
                result,
                exitCode,
                sha,
                suiteHash,
            );
            results.push({ id: scenario.id, exitCode, result, failures });
            await report();
            console.log(
                `[${index + 1}/${cases.length}] ${scenario.id} ${failures.length ? "FAIL" : "PASS"} tools=${result?.toolCount ?? "?"} durationMs=${result?.durationMs ?? "?"} ${scenario.label}`,
            );
        }
    } finally {
        await report();
    }
    return evaluateSuite(cases, results, sha, suiteHash);
}

export async function executeCase(
    scenario,
    resultPath,
    sha,
    suiteHash,
    spawnProcess = spawn,
) {
    await rm(resultPath, { force: true });
    return new Promise((resolveResult) => {
        const child = spawnProcess(
            "dotnet",
            [
                process.env.EVAL_RUNNER_DLL ??
                    "tests/LiveEvaluations/bin/Release/net10.0/LiveEvaluations.dll",
            ],
            {
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
        const terminate = () => {
            timedOut = true;
            try {
                if (process.platform === "win32") child.kill("SIGKILL");
                else process.kill(-child.pid, "SIGKILL");
            } catch {
                child.kill("SIGKILL");
            }
        };
        const timer = setTimeout(
            terminate,
            (scenario.maxDurationSeconds + 330) * 1000,
        );
        const stop = () => terminate();
        process.once("SIGTERM", stop);
        process.once("SIGINT", stop);
        const complete = (code) => {
            clearTimeout(timer);
            process.removeListener("SIGTERM", stop);
            process.removeListener("SIGINT", stop);
            resolveResult(timedOut ? -1 : (code ?? -1));
        };
        child.on("error", () => complete(-1));
        child.on("close", complete);
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
        classification === "synthetic" || process.env.GITHUB_ACTIONS !== "true";
    await mkdir(output, { recursive: true });
    const initialVerdict = evaluateSuite(cases, [], sha, suiteHash);
    await writeFile(
        resolve(output, "summary.md"),
        renderSummary(cases, [], initialVerdict, sha, publishAnswers),
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
    if (cases.length < 100)
        throw new Error("Suite contains fewer than 100 distinct questions.");
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
