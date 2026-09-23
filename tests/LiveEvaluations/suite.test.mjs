import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { EventEmitter } from "node:events";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import {
    buildSuite,
    evaluateSuite,
    executeCase,
    refreshEvaluationIdentity,
    renderSummary,
    runCases,
    validateResult,
} from "./suite.mjs";

const sha = "a".repeat(40),
    suiteHash = "b".repeat(64);
const cases = buildSuite();
const pass = (scenario) => ({
    id: scenario.id,
    question: scenario.question,
    sha,
    suiteHash,
    accepted: true,
    terminal: true,
    transcriptVerified: true,
    durationMs: 1000,
    firstTokenMs: 100,
    answer: "Grounded answer",
    toolCount: scenario.requiredTools.length,
    tools: scenario.requiredTools.map((name) => ({ name, success: true })),
    errors: [],
    reasons: [],
    judge: {
        accepted: true,
        grounded: true,
        complete: true,
        reason: "Evidence supports the answer.",
    },
});
const rows = () =>
    cases.map((scenario) => ({
        id: scenario.id,
        exitCode: 0,
        result: pass(scenario),
        failures: [],
    }));

test("both deployment workflows require every live evaluation", async () => {
    for (const name of ["main", "feature"]) {
        const workflow = await readFile(
            new URL(`../../.github/workflows/${name}.yml`, import.meta.url),
            "utf8",
        );
        assert.match(workflow, /needs: \[regressions, live-evaluations\]/);
        assert.match(
            workflow,
            /uses: \.\/\.github\/workflows\/live-evaluations\.yml/,
        );
        assert.doesNotMatch(workflow, /continue-on-error:\s*true/);
    }
    const live = await readFile(
        new URL(
            "../../.github/workflows/live-evaluations.yml",
            import.meta.url,
        ),
        "utf8",
    );
    assert.match(live, /run: node tests\/LiveEvaluations\/suite.mjs/);
    assert.match(live, /if: always\(\)/);
    assert.doesNotMatch(live, /continue-on-error:\s*true/);
    for (const match of live.matchAll(/uses: (?!\.\/)([^\n]+)/g))
        assert.match(match[1], /@[a-f0-9]{40} # v\d/);
});

test("suite imports every frontend template and includes at least 100 distinct questions", () => {
    assert.ok(cases.length >= 100);
    assert.equal(
        new Set(cases.map((item) => item.question)).size,
        cases.length,
    );
    assert.ok(cases.some((item) => item.origins.includes("incident:compute")));
    assert.ok(cases.some((item) => item.origins.includes("job-template:chat")));
});
test("deployment accepts only all planned passing results from this revision", () => {
    assert.equal(evaluateSuite(cases, rows(), sha, suiteHash).accepted, true);
    const failed = rows();
    failed[5].result.accepted = false;
    assert.equal(evaluateSuite(cases, failed, sha, suiteHash).accepted, false);
    assert.equal(
        evaluateSuite(cases, rows().slice(1), sha, suiteHash).accepted,
        false,
    );
    assert.equal(
        evaluateSuite(cases, rows(), "c".repeat(40), suiteHash).accepted,
        false,
    );
    assert.equal(
        evaluateSuite(cases, [...rows(), rows()[0]], sha, suiteHash).accepted,
        false,
    );
});
test("malformed verdicts, missing results, timeouts and failed tools fail closed", () => {
    const scenario = cases[0];
    for (const result of [
        null,
        {},
        { ...pass(scenario), judge: null },
        {
            ...pass(scenario),
            tools: [{ name: "QueryAzure", success: false }],
            toolCount: 1,
        },
        {
            ...pass(scenario),
            durationMs: scenario.maxDurationSeconds * 1000 + 1,
        },
    ])
        assert.ok(
            validateResult(scenario, result, 0, sha, suiteHash).length > 0,
        );
    assert.ok(
        validateResult(scenario, pass(scenario), 1, sha, suiteHash).length > 0,
    );
});
test("summary escapes model HTML and shows question, count and verdict", () => {
    const results = rows();
    results[0].result.answer = "<script>alert(1)</script>";
    const markdown = renderSummary(
        cases,
        results,
        evaluateSuite(cases, results, sha, suiteHash),
        sha,
    );
    assert.ok(
        markdown.includes("| Question | Tools | Seconds | Result | Reason |"),
    );
    assert.ok(markdown.includes("&lt;script&gt;"));
    assert.ok(!markdown.includes("<script>"));
});

test("all-large answers stay under the GitHub summary size limit", () => {
    const results = rows();
    for (const row of results) row.result.answer = "<&>".repeat(20000);
    const summary = renderSummary(
        cases,
        results,
        evaluateSuite(cases, results, sha, suiteHash),
        sha,
    );
    assert.ok(Buffer.byteLength(summary) < 1024 * 1024);
    assert.match(summary, /full answer in results artifact/);
});

test("each execution removes any previous result instead of accepting stale output", async () => {
    const directory = await mkdtemp(join(tmpdir(), "finops-suite-"));
    try {
        const path = join(directory, "result.json");
        await writeFile(path, JSON.stringify(pass(cases[0])));
        const code = await executeCase(cases[0], path, sha, suiteHash, () => {
            const child = new EventEmitter();
            queueMicrotask(() => child.emit("close", 0));
            return child;
        });
        assert.equal(code, 0);
        await assert.rejects(readFile(path));
        assert.ok(
            validateResult(cases[0], null, code, sha, suiteHash).length > 0,
        );
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
});

test("CI obtains a new scoped OIDC login for each case without exposing tokens", async () => {
    const environment = {
        GITHUB_ACTIONS: "true",
        ACTIONS_ID_TOKEN_REQUEST_URL: "https://example.test/token",
        ACTIONS_ID_TOKEN_REQUEST_TOKEN: "synthetic-request",
        EVAL_CLIENT_ID: "synthetic-client",
        EVAL_TENANT_ID: "synthetic-tenant",
        EVAL_LOGIN_SUBSCRIPTION: "synthetic-scope",
    };
    const commands = [];
    const request = async (url) => {
        assert.equal(
            url.searchParams.get("audience"),
            "api://AzureADTokenExchange",
        );
        return {
            ok: true,
            json: async () => ({ value: "synthetic-assertion" }),
        };
    };
    await refreshEvaluationIdentity(
        environment,
        request,
        async (command, args) => commands.push([command, args]),
    );
    assert.equal(commands.length, 2);
    assert.equal(commands[0][1][0], "login");
    assert.ok(commands[0][1].includes("--federated-token"));
    await assert.rejects(
        refreshEvaluationIdentity(environment, async () => {
            throw new Error("secret-must-not-escape");
        }),
        (error) =>
            !error.message.includes("secret-must-not-escape") &&
            error.message.includes("OIDC renewal failed"),
    );
});

test("suite execution writes all results but rejects one failed answer", async () => {
    const directory = await mkdtemp(join(tmpdir(), "finops-suite-run-"));
    try {
        const planned = cases.slice(0, 100);
        let executed = 0;
        const verdict = await runCases(
            planned,
            directory,
            sha,
            suiteHash,
            async (scenario, path) => {
                executed++;
                const result = pass(scenario);
                if (executed === 51) {
                    result.accepted = false;
                    result.judge.accepted = false;
                    result.reasons = ["Unsupported claim."];
                }
                await writeFile(path, JSON.stringify(result));
                return executed === 51 ? 1 : 0;
            },
            async () => {},
        );
        assert.equal(executed, 100);
        assert.equal(verdict.accepted, false);
        const report = JSON.parse(
            await readFile(join(directory, "results.json"), "utf8"),
        );
        assert.equal(report.results.length, 100);
        assert.equal(report.verdict.accepted, false);
        assert.match(
            await readFile(join(directory, "summary.md"), "utf8"),
            /99\/100 accepted/,
        );
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
});

test("internal-test runs publish verdicts but withhold answers and judge rationale", async () => {
    const directory = await mkdtemp(join(tmpdir(), "finops-suite-private-"));
    try {
        const planned = cases.slice(0, 100);
        const verdict = await runCases(
            planned,
            directory,
            sha,
            suiteHash,
            async (scenario, path) => {
                const result = pass(scenario);
                result.answer = "tenant-answer-must-not-publish";
                result.judge.reason = "tenant-rationale-must-not-publish";
                await writeFile(path, JSON.stringify(result));
                return 0;
            },
            async () => {},
            false,
        );
        assert.equal(verdict.accepted, true);
        const published = [
            await readFile(join(directory, "results.json"), "utf8"),
            await readFile(join(directory, "summary.md"), "utf8"),
            await readFile(join(directory, `${planned[0].id}.json`), "utf8"),
        ].join("\n");
        assert.ok(!published.includes("tenant-answer-must-not-publish"));
        assert.ok(!published.includes("tenant-rationale-must-not-publish"));
        assert.match(published, /100\/100 accepted/);
        assert.match(published, /Answers and judge rationale are withheld/);
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
});

test("the real suite entry point exits nonzero on missing configuration and writes a failed summary", async () => {
    const directory = await mkdtemp(join(tmpdir(), "finops-suite-missing-"));
    try {
        const result = spawnSync(
            process.execPath,
            [new URL("./suite.mjs", import.meta.url).pathname],
            {
                encoding: "utf8",
                env: {
                    ...process.env,
                    EVAL_EXPECTED_SHA: sha,
                    GITHUB_SHA: sha,
                    EVAL_MODEL_ENDPOINT: "",
                    EVAL_OUTPUT_DIRECTORY: directory,
                },
            },
        );
        assert.equal(result.status, 1);
        assert.match(
            result.stderr,
            /Required evaluation configuration missing/,
        );
        assert.match(
            await readFile(join(directory, "summary.md"), "utf8"),
            /\*\*FAIL\*\*/,
        );
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
});
