import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { EventEmitter } from "node:events";
import {
    mkdir, mkdtemp, readFile, readdir, realpath, rm, stat, symlink, writeFile,
} from "node:fs/promises";
import { dirname, join, relative, sep } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { JOB_TEMPLATES } from "../../src/Dashboard/frontend/src/data/jobTemplates.js";
import {
    maturityCategories,
    pricingCategory,
} from "../../src/Dashboard/frontend/src/data/sidebarCategories.js";
import {
    assertCandidateRevision,
    buildCatalog,
    buildSuite,
    evaluateSuite,
    executeCase,
    preparePrivateDiagnostics,
    publishableResult,
    refreshEvaluationIdentity,
    renderSummary,
    runCases,
    terminateProcessTree,
    validateResult,
} from "./suite.mjs";

const repositoryRoot = fileURLToPath(new URL("../../", import.meta.url));
async function createDirectory() {
    const root = join(repositoryRoot, "tests", "LiveEvaluations", "obj");
    await mkdir(root, { recursive: true });
    return mkdtemp(join(root, ".live-evaluation-test-"));
}
async function createDiagnosticsFixture() {
    const directory = await createDirectory();
    const sourceRoot = join(directory, "source");
    const output = join(directory, "published");
    const diagnostics = join(directory, "private");
    await Promise.all([sourceRoot, output, diagnostics].map((path) =>
        mkdir(path, { recursive: true })));
    return {
        directory, sourceRoot, output, diagnostics,
        options: {
            privateDiagnosticsDirectory: diagnostics,
            diagnosticsSourceRoot: sourceRoot,
            environment: {},
        },
    };
}
const sha = "a".repeat(40),
    suiteHash = "b".repeat(64);
const catalog = buildCatalog();
const cases = buildSuite(catalog);
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
const privateResult = (scenario) => {
    const result = pass(scenario);
    result.answer = "tenant-answer-must-not-publish";
    result.judge.reason = "tenant-rationale-must-not-publish";
    result.tools.push({
        name: "CalculateCost",
        success: true,
        arguments: "tenant-arguments-must-not-publish",
        result: "tenant-tool-output-must-not-publish",
    });
    result.toolCount = result.tools.length;
    result.failedToolDetails = [{
        arguments: "tenant-arguments-must-not-publish",
        detail: "tenant-details-must-not-publish",
    }];
    result.toolDetails = [{
        name: "QueryGraph",
        success: true,
        arguments: "tenant-arguments-must-not-publish",
        result: "tenant-tool-output-must-not-publish",
        resultTruncated: false,
        error: "tenant-private-error-must-not-publish",
    }];
    result.failure = {
        phase: "tenant-private-phase-must-not-publish",
        type: "tenant-private-type-must-not-publish",
        detail: "tenant-private-error-must-not-publish",
    };
    result.extra = { context: "tenant-extra-must-not-publish" };
    return result;
};
async function readPublished(directory) {
    const entries = await readdir(directory, { withFileTypes: true });
    return (await Promise.all(entries.map((entry) =>
        entry.isDirectory()
            ? readPublished(join(directory, entry.name))
            : readFile(join(directory, entry.name), "utf8"),
    ))).join("\n");
}

test("candidate preflight scopes tracked and untracked checks to evaluation source and inherited build inputs", async () => {
    const commands = [];
    await assertCandidateRevision(sha, async (command, args, options) => {
        commands.push([command, args]);
        assert.equal(options.cwd, repositoryRoot);
        assert.equal(options.timeout, 30000);
        return { stdout: commands.length === 1 ? `${sha.toUpperCase()}\n` : "" };
    });
    assert.equal(commands.length, 3);
    assert.ok(commands.every(([command]) => command === "git"));
    assert.deepEqual(commands[0][1], ["rev-parse", "--verify", "HEAD"]);
    assert.deepEqual(commands[1][1].slice(0, 5), [
        "status", "--porcelain=v1",
        "--untracked-files=no", "--ignore-submodules=none", "--",
    ]);
    const paths = commands[1][1].slice(5);
    assert.deepEqual(paths.slice(0, 3), [
        join("src", "Dashboard"), "tests", ".github",
    ]);
    for (const directory of ["", "src"])
        for (const name of [
            "global.json", ".editorconfig", "NuGet.Config",
            "Directory.Build.*", "Directory.Packages.props",
        ])
            assert.ok(paths.includes(`:(icase)${join(directory, name)}`));
    assert.deepEqual(commands[2][1], [
        "ls-files", "--others", "--exclude-standard", "--", ...paths,
    ]);
});

test("candidate preflight rejects missing or abbreviated SHAs without running git", async () => {
    for (const invalid of [undefined, "", "a".repeat(39), "a".repeat(41), "not-a-revision"])
        await assert.rejects(
            assertCandidateRevision(invalid, () => assert.fail("git must not run")),
            /full candidate commit SHA/,
        );
});

test("candidate preflight rejects mismatched HEAD and staged or unstaged tracked changes", async () => {
    for (const [head, status, message] of [
        ["c".repeat(40), "", /does not match/],
        [sha, " M tests/LiveEvaluations/suite.mjs\n", /uncommitted changes/],
        [sha, "M  src/Dashboard/Program.cs\n", /uncommitted changes/],
        [sha, "MM .github/workflows/live-evaluations.yml\n", /uncommitted changes/],
    ])
        await assert.rejects(
            assertCandidateRevision(sha, async (_, args) => ({
                stdout: args[0] === "rev-parse"
                    ? head : args[0] === "status" ? status : "",
            })),
            message,
        );
});

test("candidate preflight rejects untracked application, evaluation and workflow source", async () => {
    for (const source of [
        "src/Dashboard/Untracked.cs",
        "tests/LiveEvaluations/Untracked.cs",
        ".github/workflows/untracked.yml",
    ])
        await assert.rejects(
            assertCandidateRevision(sha, async (_, args) => ({
                stdout: args[0] === "rev-parse"
                    ? sha : args[0] === "ls-files" ? `${source}\n` : "",
            })),
            (error) =>
                /Untracked application, evaluation, or workflow source/.test(error.message) &&
                !error.message.includes(source),
        );
});

test("git source discovery ignores build outputs and unrelated tracked or untracked local files", async () => {
    const directory = await createDirectory();
    try {
        const initialized = spawnSync("git", ["init", "--quiet", directory]);
        assert.equal(initialized.status, 0);
        await writeFile(
            join(directory, ".gitignore"),
            "src/Dashboard/bin/\ntests/obj/\n",
        );
        await Promise.all([
            mkdir(join(directory, "src", "Dashboard", "bin"), { recursive: true }),
            mkdir(join(directory, "tests", "obj"), { recursive: true }),
            mkdir(join(directory, ".github"), { recursive: true }),
        ]);
        const preserved = join(directory, "local-notes.txt");
        const solution = join(directory, "azure-finops-agent.sln");
        await Promise.all([
            writeFile(preserved, "unrelated local notes"),
            writeFile(solution, "unrelated solution"),
            writeFile(join(directory, "README.md"), "unrelated documentation"),
            writeFile(join(directory, "src", "Dashboard", "bin", "output.dll"), "ignored output"),
            writeFile(join(directory, "tests", "obj", "Generated.cs"), "ignored generated source"),
        ]);
        const staged = spawnSync("git", [
            "add", "--", "azure-finops-agent.sln", "README.md",
        ], { cwd: directory });
        assert.equal(staged.status, 0);
        await writeFile(solution, "unrelated solution modification");
        const execute = async (command, args, options) => {
            if (args[0] === "rev-parse") return { stdout: sha };
            const result = spawnSync(command, args, { ...options, cwd: directory });
            assert.equal(result.status, 0);
            return result;
        };
        await assertCandidateRevision(sha, execute);
        for (const path of [
            join(directory, "src", "Dashboard", "Untracked.cs"),
            join(directory, "tests", "Untracked.cs"),
            join(directory, ".github", "untracked.yml"),
            join(directory, "Directory.Build.props"),
            join(directory, "Directory.Packages.props"),
            join(directory, "global.json"),
            join(directory, "src", "Directory.Build.targets"),
            join(directory, "src", "nuget.config"),
        ]) {
            await writeFile(path, "untracked source");
            await assert.rejects(
                assertCandidateRevision(sha, execute),
                /Untracked application, evaluation, or workflow source/,
            );
            assert.equal(await readFile(path, "utf8"), "untracked source");
            await rm(path);
        }
        await assertCandidateRevision(sha, execute);
        const relevant = join(directory, "src", "Dashboard", "Tracked.cs");
        await writeFile(relevant, "tracked source");
        assert.equal(spawnSync("git", [
            "add", "--", relative(directory, relevant),
        ], { cwd: directory }).status, 0);
        await assert.rejects(
            assertCandidateRevision(sha, execute),
            /Tracked source has uncommitted changes/,
        );
        assert.equal(await readFile(preserved, "utf8"), "unrelated local notes");
        assert.equal(await readFile(solution, "utf8"), "unrelated solution modification");
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
});

test("candidate preflight fails closed without exposing git diagnostics", async () => {
    for (const command of ["rev-parse", "status", "ls-files"])
        await assert.rejects(
            assertCandidateRevision(sha, async (_, args) => {
                if (args[0] === command)
                    throw new Error("private-git-diagnostic-must-not-escape");
                return { stdout: args[0] === "rev-parse" ? sha : "" };
            }),
            (error) =>
                /Unable to verify/.test(error.message) &&
                !error.message.includes("private-git-diagnostic"),
        );
});

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
    for (const secret of [
        "AZURE_EVAL_CLIENT_ID",
        "AZURE_EVAL_TENANT_ID",
        "AZURE_EVAL_SUBSCRIPTION_ID",
        "EVAL_MODEL_ENDPOINT",
    ])
        assert.match(
            live,
            new RegExp(`^\\s*${secret}:\\s*\\r?\\n\\s+required:\\s*false\\s*$`, "m"),
            `${secret} must be optional at the reusable workflow boundary.`,
        );
    assert.match(live, /environment: ai-evaluation/);
    assert.match(live, /EVAL_CLIENT_ID EVAL_TENANT_ID EVAL_LOGIN_SUBSCRIPTION/);
    assert.match(live, /exit 1/);
    for (const match of live.matchAll(/uses: (?!\.\/)([^\n]+)/g))
        assert.match(match[1], /@[a-f0-9]{40} # v\d/);
});

test("manual feature validation does not deploy unless explicitly requested", async () => {
    const workflow = await readFile(
        new URL("../../.github/workflows/feature.yml", import.meta.url),
        "utf8",
    );
    assert.match(workflow, /workflow_dispatch:\s+inputs:\s+deploy:/);
    assert.match(workflow, /type: boolean\s+default: false/);
    assert.match(
        workflow,
        /if:.*github\.event_name != 'workflow_dispatch' \|\| inputs\.deploy/,
    );
    assert.match(workflow, /'test-slot-evaluation' \|\| 'test-slot-deploy'/);
    assert.match(workflow, /cancel-in-progress: false/);
    assert.match(workflow, /github\.ref_type == 'branch' && github\.ref_name != 'main'/);
    assert.match(workflow, /- "infra\/\*\*"/);
});

test("feature deployment consumes only the successful environment-resolved evaluation contract", async () => {
    const [feature, live] = await Promise.all(["feature", "live-evaluations"].map((name) =>
        readFile(new URL(`../../.github/workflows/${name}.yml`, import.meta.url), "utf8")));
    for (const [name, environment] of [
        ["model", "EVALUATED_MODEL"],
        ["reasoning_effort", "EVALUATED_REASONING_EFFORT"],
        ["sha", "EVALUATED_SHA"],
        ["endpoint_sha256", "EVALUATED_ENDPOINT_SHA256"],
    ]) {
        assert.ok(live.includes(`value: \${{ jobs.evaluate.outputs.${name} }}`));
        assert.ok(live.includes(`${name}: \${{ steps.accepted.outputs.${name} }}`));
        assert.ok(feature.includes(`${environment}: \${{ needs.live-evaluations.outputs.${name} }}`));
    }
    assert.match(live, /EVAL_MODEL: \$\{\{ vars\.EVAL_MODEL \}\}/);
    assert.match(live, /EVAL_MODEL_ENDPOINT: \$\{\{ secrets\.EVAL_MODEL_ENDPOINT \}\}/);
    assert.doesNotMatch(live, /vars\.EVAL_MODEL_ENDPOINT/);
    assert.match(live, /EVAL_REASONING_EFFORT: \$\{\{ vars\.EVAL_REASONING_EFFORT \|\| 'xhigh' \}\}/);
    assert.match(live, /id: accepted\s+if: success\(\)\s+run: node infra\/scripts\/feature-slot\.mjs evaluation-output/);
    assert.ok(live.indexOf("run: node tests/LiveEvaluations/suite.mjs") <
        live.indexOf("id: accepted"));
    assert.doesNotMatch(feature, /vars\.EVAL_MODEL|gpt-6-luna|EVALUATED_MODEL:.*\|\|/);
    assert.doesNotMatch(feature, /EVAL_MODEL_ENDPOINT/);
});

test("full catalog retains every frontend template and both concrete incident cases", () => {
    const questions = new Set([
        ...maturityCategories.flatMap((category) =>
            (category.prompts ?? []).map((prompt) => prompt.prompt)),
        ...(pricingCategory.publicPrompts ?? []).map((prompt) => prompt.prompt),
        ...(pricingCategory.connectedPrompts ?? []).map((prompt) => prompt.prompt),
        ...JOB_TEMPLATES.map((template) => template.prompt),
        "in which regions can I get h200 on spot quota?",
        "Use CalculateCost for 2 units at USD 3 per unit, period month, zero discount and tax. Report the total in English. Do not look up prices.",
    ].map((question) => question.trim()));
    assert.deepEqual(new Set(catalog.map((item) => item.question)), questions);
    assert.equal(catalog.length, questions.size);
    assert.equal(new Set(catalog.map((item) => item.id)).size, catalog.length);
    assert.ok(catalog.length > cases.length);
    assert.ok(catalog.some((item) => item.origins.includes("job-template:chat")));
});

test("live gate selects exactly 20 stable representative questions without changing case contracts", () => {
    assert.equal(cases.length, 20);
    assert.deepEqual(cases.map((item) => item.id), [
        "fa300ef5396c951b", "967d9300e17ef22a", "c0172debe25f1c20",
        "89c66e0ad009d9e5", "75282dc082630fdd", "d7c2e62701ecfd11",
        "522cb8a56daeae81", "f756478143fce63c", "81374a24e31aa410",
        "eac3ecb5ceb83c4b", "84206ed740e28de4", "979163d9aeedc2b8",
        "e4e6c2296f2dfc97", "095d1c30190c1015", "f9b2c97a215d8923",
        "160eca54c580c4f8", "126bedbbf645cbd1", "f75b5b6527c41d5c",
        "062a296be5be951f", "552f572706ca51cd",
    ]);
    assert.notDeepEqual(cases, catalog.slice(0, 20));
    assert.equal(
        new Set(cases.map((item) => item.question)).size,
        20,
    );
    for (const scenario of cases)
        assert.equal(scenario, catalog.find((item) => item.id === scenario.id));
    assert.ok(cases.some((item) => item.origins.includes("incident:compute")));
    assert.ok(cases.some((item) => item.origins.includes("incident:language")));
    assert.deepEqual(cases[0].requiredTools, ["ReportMaturityScore"]);
    assert.deepEqual(cases[0].forbiddenTools, []);
    assert.deepEqual(cases.at(-2).requiredTools, ["QueryAzure"]);
    assert.deepEqual(cases.at(-1).requiredTools, ["CalculateCost"]);
});

test("curated selection fails if a pinned catalog case is missing, duplicated or has a duplicate question", () => {
    assert.throws(
        () => buildSuite(catalog.filter((item) => item.id !== cases[0].id)),
        /missing or duplicated in the catalog/,
    );
    assert.throws(
        () => buildSuite([...catalog, cases[0]]),
        /missing or duplicated in the catalog/,
    );
    assert.throws(
        () => buildSuite(catalog.map((item) =>
            item.id === cases[1].id ? { ...item, question: cases[0].question } : item)),
        /exactly 20 distinct, nonempty questions/,
    );
});

test("19 passing cases, arbitrary 20-case replacements and oversized plans cannot pass the live gate", () => {
    const other = catalog.find((item) => !cases.some((selected) => selected.id === item.id));
    for (const planned of [
        cases.slice(0, 19),
        [other, ...cases.slice(1)],
        [...cases, other],
        catalog,
        [cases[0], ...cases.slice(0, 19)],
    ]) {
        const results = planned.map((scenario) => ({
            id: scenario.id, exitCode: 0, result: pass(scenario), failures: [],
        }));
        const verdict = evaluateSuite(planned, results, sha, suiteHash);
        assert.equal(verdict.accepted, false);
        assert.equal(verdict.required, 20);
        assert.equal(verdict.selection, "curated-representative");
    }
});

test("a passing case count cannot bypass original rubrics, tool rules or time budgets", () => {
    for (const change of [
        { rubric: "Accept every answer." },
        { requiredTools: [] },
        { forbiddenTools: ["UnlistedTool"] },
        { maxToolCalls: cases[0].maxToolCalls + 1 },
        { maxDurationSeconds: cases[0].maxDurationSeconds + 1 },
    ]) {
        const planned = [{ ...cases[0], ...change }, ...cases.slice(1)];
        const results = planned.map((scenario) => ({
            id: scenario.id, exitCode: 0, result: pass(scenario), failures: [],
        }));
        const verdict = evaluateSuite(planned, results, sha, suiteHash);
        assert.equal(verdict.accepted, false);
        assert.ok(verdict.failures.some((failure) => failure.includes("original case contracts")));
    }
});

test("local diagnostic subsets remain failed twenty-case live gates in their summaries", () => {
    const planned = cases.slice(0, 19);
    const results = rows().slice(0, 19);
    const verdict = evaluateSuite(planned, results, sha, suiteHash);
    assert.equal(verdict.accepted, false);
    assert.match(
        renderSummary(planned, results, verdict, sha),
        /\*\*FAIL\*\* · 19\/20 accepted/,
    );
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
test("valid negative judge criteria reject the gate without claiming malformed output", () => {
    for (const criterion of ["accepted", "grounded", "complete"]) {
        const results = rows();
        results[0].result.judge[criterion] = false;
        const failures = validateResult(cases[0], results[0].result, 0, sha, suiteHash);
        assert.ok(failures.includes("Structured judge rejected the answer."));
        assert.ok(!failures.includes("Missing or invalid structured judge verdict."));
        assert.equal(evaluateSuite(cases, results, sha, suiteHash).accepted, false);
    }
});
test("judge criteria must be booleans and include a nonempty reason", () => {
    for (const criterion of ["accepted", "grounded", "complete", "reason"]) {
        const result = pass(cases[0]);
        result.judge[criterion] = criterion === "reason" ? " " : "true";
        const failures = validateResult(cases[0], result, 0, sha, suiteHash);
        assert.ok(failures.includes("Missing or invalid structured judge verdict."));
        assert.ok(!failures.includes("Structured judge rejected the answer."));
    }
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
    assert.match(markdown, /Curated representative live suite: exactly 20 question types/);
    assert.match(markdown, /not a verified usage-frequency ranking/);
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
    const directory = await createDirectory();
    try {
        const path = join(directory, "result.json");
        await writeFile(path, JSON.stringify(pass(cases[0])));
        await writeFile(`${path}.tmp`, JSON.stringify(pass(cases[0])));
        const code = await executeCase(cases[0], path, sha, suiteHash, () => {
            const child = new EventEmitter();
            queueMicrotask(() => child.emit("close", 0));
            return child;
        });
        assert.equal(code, 0);
        await assert.rejects(readFile(path));
        await assert.rejects(readFile(`${path}.tmp`));
        assert.ok(
            validateResult(cases[0], null, code, sha, suiteHash).length > 0,
        );
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
});

test("owned process trees are terminated by PID on Windows and by process group on POSIX", async () => {
    const child = { pid: 12345 };
    const commands = [];
    await terminateProcessTree(child, "win32", async (...args) => commands.push(args));
    assert.deepEqual(commands, [[
        "taskkill",
        ["/PID", "12345", "/T", "/F"],
        { windowsHide: true, timeout: 10000 },
    ]]);
    const kills = [];
    await terminateProcessTree(child, "linux", undefined, (...args) => kills.push(args));
    assert.deepEqual(kills, [[-12345, "SIGKILL"]]);
    for (const pid of [undefined, 0, -1, 1.5])
        await assert.rejects(
            terminateProcessTree({ pid }, "win32", () => assert.fail("no PID, no kill")),
            /no valid PID/,
        );
});

test("tree termination ignores only an already-exited POSIX process group", async () => {
    await terminateProcessTree({ pid: 12345 }, "linux", undefined, () => {
        throw Object.assign(new Error("already exited"), { code: "ESRCH" });
    });
    await assert.rejects(
        terminateProcessTree({ pid: 12345 }, "linux", undefined, () => {
            throw Object.assign(new Error("not permitted"), { code: "EPERM" });
        }),
        /not permitted/,
    );
    await assert.rejects(
        terminateProcessTree({ pid: 12345 }, "win32", async () => {
            throw new Error("tree termination failed");
        }),
        /tree termination failed/,
    );
});

test("child errors and subsequent close events clear execution state exactly once", async () => {
    const directory = await createDirectory();
    const signals = new EventEmitter();
    const child = new EventEmitter();
    let cleared = 0;
    const timer = {};
    try {
        const code = await executeCase(
            cases[0], join(directory, "result.json"), sha, suiteHash,
            () => {
                queueMicrotask(() => {
                    child.emit("error", new Error("synthetic spawn failure"));
                    child.emit("close", 0);
                });
                return child;
            },
            {
                signals,
                startTimer: () => timer,
                cancelTimer: (value) => {
                    assert.equal(value, timer);
                    cleared++;
                },
            },
        );
        assert.equal(code, -1);
        assert.equal(cleared, 1);
        assert.equal(signals.listenerCount("SIGINT"), 0);
        assert.equal(signals.listenerCount("SIGTERM"), 0);
        assert.equal(child.listenerCount("close"), 0);
        assert.equal(child.listenerCount("error"), 0);
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
});

test("timeout kills the owned child tree and cannot accept a zero exit code", async () => {
    const directory = await createDirectory();
    const signals = new EventEmitter();
    const child = new EventEmitter();
    let killed = 0, cleared = 0, terminationCompleted = false;
    try {
        const code = await executeCase(
            cases[0], join(directory, "result.json"), sha, suiteHash,
            () => child,
            {
                signals,
                startTimer: (callback, milliseconds) => {
                    assert.equal(milliseconds, (cases[0].maxDurationSeconds + 330) * 1000);
                    queueMicrotask(callback);
                    return 1;
                },
                cancelTimer: () => cleared++,
                terminateTree: async (owned) => {
                    assert.equal(owned, child);
                    killed++;
                    child.emit("close", 0);
                    await Promise.resolve();
                    terminationCompleted = true;
                },
            },
        );
        assert.equal(code, -1);
        assert.equal(killed, 1);
        assert.equal(cleared, 1);
        assert.equal(terminationCompleted, true);
        assert.equal(signals.listenerCount("SIGINT"), 0);
        assert.equal(signals.listenerCount("SIGTERM"), 0);
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
});

test("failed process-tree termination rejects execution and clears listeners and timers once", async () => {
    const directory = await createDirectory();
    const signals = new EventEmitter();
    const child = new EventEmitter();
    let cleared = 0;
    try {
        await assert.rejects(
            executeCase(
                cases[0], join(directory, "result.json"), sha, suiteHash,
                () => child,
                {
                    signals,
                    startTimer: (callback) => {
                        queueMicrotask(callback);
                        return 1;
                    },
                    cancelTimer: () => cleared++,
                    terminateTree: async () => {
                        throw new Error("private-process-error-must-not-escape");
                    },
                },
            ),
            (error) =>
                /owned evaluation process tree could not be terminated/.test(error.message) &&
                !error.message.includes("private-process-error"),
        );
        assert.equal(cleared, 1);
        assert.equal(signals.listenerCount("SIGINT"), 0);
        assert.equal(signals.listenerCount("SIGTERM"), 0);
        assert.equal(child.listenerCount("close"), 0);
        assert.equal(child.listenerCount("error"), 0);
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
});

for (const signal of ["SIGINT", "SIGTERM"])
    test(`${signal} rejects standalone execution instead of returning a recoverable case failure`, async () => {
        const directory = await createDirectory();
        const signals = new EventEmitter();
        const child = new EventEmitter();
        let killed = 0, cleared = 0;
        try {
            await assert.rejects(
                executeCase(
                    cases[0], join(directory, "result.json"), sha, suiteHash,
                    () => {
                        queueMicrotask(() => {
                            signals.emit(signal);
                            signals.emit(signal);
                        });
                        return child;
                    },
                    {
                        signals,
                        startTimer: () => 1,
                        cancelTimer: () => cleared++,
                        terminateTree: async () => {
                            killed++;
                            child.emit("close", 0);
                        },
                    },
                ),
                (error) => error.code === "EVAL_INTERRUPTED" && error.message.includes(signal),
            );
            assert.equal(killed, 1);
            assert.equal(cleared, 1);
            assert.equal(signals.listenerCount("SIGINT"), 0);
            assert.equal(signals.listenerCount("SIGTERM"), 0);
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

test("fail-fast stops after the first failed case and never accepts the partial suite", async () => {
    const directory = await createDirectory();
    try {
        let executed = 0;
        await assert.rejects(
            runCases(
                cases, directory, sha, suiteHash,
                async (scenario, path) => {
                    executed++;
                    const result = pass(scenario);
                    if (executed === 3) {
                        result.accepted = false;
                        result.judge.accepted = false;
                        result.reasons = ["Unsupported claim."];
                    }
                    await writeFile(path, JSON.stringify(result));
                    return executed === 3 ? 1 : 0;
                },
                async () => {},
                true,
                0,
                { failFast: true },
            ),
            (error) => error.message.includes("Fail-fast") && error.message.includes("17 cases were not run"),
        );
        assert.equal(executed, 3);
        const report = JSON.parse(await readFile(join(directory, "results.json"), "utf8"));
        assert.equal(report.results.length, 3);
        assert.equal(report.verdict.accepted, false);
        assert.ok(report.verdict.failures.some((failure) => failure.includes("Fail-fast")));
        assert.match(await readFile(join(directory, "summary.md"), "utf8"), /Suite failure:.*Fail-fast/);
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
});

test("suite execution writes all results but rejects one failed answer", async () => {
    const directory = await createDirectory();
    try {
        const planned = cases;
        let executed = 0;
        const verdict = await runCases(
            planned,
            directory,
            sha,
            suiteHash,
            async (scenario, path) => {
                executed++;
                const result = pass(scenario);
                if (executed === 11) {
                    result.accepted = false;
                    result.judge.accepted = false;
                    result.reasons = ["Unsupported claim."];
                }
                await writeFile(path, JSON.stringify(result));
                return executed === 11 ? 1 : 0;
            },
            async () => {},
        );
        assert.equal(executed, 20);
        assert.equal(verdict.accepted, false);
        const report = JSON.parse(
            await readFile(join(directory, "results.json"), "utf8"),
        );
        assert.equal(report.results.length, 20);
        assert.equal(report.verdict.required, 20);
        assert.equal(report.verdict.selection, "curated-representative");
        assert.equal(report.verdict.accepted, false);
        assert.match(
            await readFile(join(directory, "summary.md"), "utf8"),
            /19\/20 accepted/,
        );
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
});

test("a final service throttle is retried once after its deadline with the rubric unchanged", async () => {
    const directory = await createDirectory();
    try {
        const planned = cases.slice(0, 3);
        const calls = [];
        const verdict = await runCases(
            planned,
            directory,
            sha,
            suiteHash,
            async (scenario, path) => {
                calls.push(scenario.id);
                const result = pass(scenario);
                const throttled = scenario === planned[1] && calls.filter((id) => id === scenario.id).length === 1;
                const refused = scenario === planned[2];
                if (throttled || refused) {
                    result.accepted = false;
                    result.tools = [{ name: "QueryAzure", success: false }];
                    result.toolCount = 1;
                    result.reasons = ["At least one tool failed."];
                }
                if (throttled)
                    result.throttle = { notices: 2, final: true, retryAtUtc: new Date(Date.now() + 50).toISOString() };
                await writeFile(path, JSON.stringify(result));
                return throttled || refused ? 1 : 0;
            },
            async () => {},
            false,
        );
        assert.deepEqual(calls, [planned[0].id, planned[1].id, planned[1].id, planned[2].id]);
        assert.equal(verdict.accepted, false);
        const report = JSON.parse(await readFile(join(directory, "results.json"), "utf8"));
        const retried = report.results.find((row) => row.id === planned[1].id);
        assert.equal(retried.failures.length, 0);
        assert.equal(retried.result.attempts, 2);
        const failed = report.results.find((row) => row.id === planned[2].id);
        assert.ok(failed.failures.length > 0);
        assert.equal(failed.result.attempts, 1);
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
});

test("a throttled case that still fails after its single retry remains failed", async () => {
    const directory = await createDirectory();
    try {
        const planned = cases.slice(0, 1);
        let executed = 0;
        const verdict = await runCases(
            planned,
            directory,
            sha,
            suiteHash,
            async (scenario, path) => {
                executed++;
                const result = pass(scenario);
                result.accepted = false;
                result.reasons = ["At least one tool failed."];
                result.throttle = { notices: 1, final: true, retryAtUtc: new Date(Date.now() + 20).toISOString() };
                await writeFile(path, JSON.stringify(result));
                return 1;
            },
            async () => {},
            false,
        );
        assert.equal(executed, 2);
        assert.equal(verdict.accepted, false);
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
});

test("internal-test runs publish verdicts but withhold answers and judge rationale", async () => {
    const directory = await createDirectory();
    let captureDirectory;
    try {
        const planned = cases;
        const verdict = await runCases(
            planned,
            directory,
            sha,
            suiteHash,
            async (scenario, path) => {
                captureDirectory ??= dirname(path);
                assert.equal(dirname(path), captureDirectory);
                assert.ok(relative(directory, path).startsWith(`..${sep}`));
                const result = privateResult(scenario);
                await writeFile(path, JSON.stringify(result));
                await writeFile(`${path}.tmp`, JSON.stringify(result));
                if (scenario === planned[0])
                    assert.doesNotMatch(await readPublished(directory), /tenant-.*-must-not-publish/);
                return 0;
            },
            async () => {},
            false,
        );
        assert.equal(verdict.accepted, true);
        const published = await readPublished(directory);
        assert.doesNotMatch(published, /tenant-.*-must-not-publish/);
        assert.match(published, /20\/20 accepted/);
        assert.match(published, /Answers and judge rationale are withheld/);
        assert.ok(!(await readdir(directory)).some((name) => name.endsWith(".tmp")));
        await assert.rejects(stat(captureDirectory), { code: "ENOENT" });
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
});

test("local diagnostics retain redacted failed-case rationale without changing public classification or acceptance", async () => {
    const fixture = await createDiagnosticsFixture();
    let captureDirectory;
    try {
        const preserved = join(fixture.diagnostics, "operator-owned.txt");
        await writeFile(preserved, "preserve existing diagnostics");
        const verdict = await runCases(
            cases, fixture.output, sha, suiteHash,
            async (scenario, path) => {
                captureDirectory ??= dirname(path);
                assert.equal(dirname(path), captureDirectory);
                assert.ok(relative(fixture.sourceRoot, path).startsWith(`..${sep}`));
                assert.ok(relative(fixture.output, path).startsWith(`..${sep}`));
                const result = privateResult(scenario);
                if (scenario === cases[0]) {
                    result.accepted = false;
                    result.judge.accepted = false;
                    result.reasons = ["tenant-judge-rejection-must-not-publish"];
                }
                await writeFile(path, JSON.stringify(result));
                return result.accepted ? 0 : 1;
            },
            async () => {},
            false,
            0,
            fixture.options,
        );
        assert.equal(verdict.accepted, false);
        assert.equal(verdict.completed, 20);
        const retained = JSON.parse(await readFile(
            join(captureDirectory, `${cases[0].id}.json`), "utf8",
        ));
        assert.equal(retained.judge.reason, "tenant-rationale-must-not-publish");
        assert.equal(retained.reasons[0], "tenant-judge-rejection-must-not-publish");
        const snapshot = JSON.parse(await readFile(
            join(captureDirectory, "diagnostics.json"), "utf8",
        ));
        assert.equal(snapshot.completed, 20);
        assert.equal(snapshot.results.length, 1);
        assert.equal(snapshot.results[0].result.answer, "tenant-answer-must-not-publish");
        assert.equal(snapshot.results[0].result.failedToolDetails[0].arguments,
            "tenant-arguments-must-not-publish");
        assert.deepEqual((await readdir(captureDirectory)).sort(),
            [`${cases[0].id}.json`, "diagnostics.json"].sort());
        const published = await readPublished(fixture.output);
        assert.doesNotMatch(published, /tenant-.*-must-not-publish/);
        assert.ok(!published.includes(captureDirectory));
        assert.match(published, /19\/20 accepted/);
        assert.equal(await readFile(preserved, "utf8"), "preserve existing diagnostics");
        assert.equal((await stat(captureDirectory)).isDirectory(), true);
        if (process.platform !== "win32") {
            assert.equal((await stat(captureDirectory)).mode & 0o777, 0o700);
            assert.equal((await stat(join(captureDirectory, "diagnostics.json"))).mode & 0o777, 0o600);
        }
    } finally {
        await rm(fixture.directory, { recursive: true, force: true });
    }
});

test("interrupted local diagnostics retain unfinished captures but not passing answers without starting more cases", async () => {
    const fixture = await createDiagnosticsFixture();
    const signals = new EventEmitter();
    let executed = 0, captureDirectory;
    try {
        await assert.rejects(
            runCases(
                cases, fixture.output, sha, suiteHash,
                async (scenario, path) => {
                    executed++;
                    captureDirectory = dirname(path);
                    if (executed === 1) {
                        await writeFile(path, JSON.stringify(privateResult(scenario)));
                        return 0;
                    }
                    await writeFile(`${path}.tmp`, JSON.stringify(privateResult(scenario)));
                    signals.emit("SIGINT");
                    return -1;
                },
                async () => {},
                false,
                0,
                { ...fixture.options, signals },
            ),
            /interrupted by SIGINT/,
        );
        assert.equal(executed, 2);
        assert.equal(signals.listenerCount("SIGINT"), 0);
        assert.equal(signals.listenerCount("SIGTERM"), 0);
        const snapshot = JSON.parse(await readFile(
            join(captureDirectory, "diagnostics.json"), "utf8",
        ));
        assert.equal(snapshot.completed, 1);
        assert.equal(snapshot.results.length, 0);
        await assert.rejects(stat(join(captureDirectory, `${cases[0].id}.json`)), { code: "ENOENT" });
        assert.match(snapshot.failure, /interrupted by SIGINT/);
        assert.match(await readFile(
            join(captureDirectory, `${cases[1].id}.json.tmp`), "utf8",
        ), /tenant-rationale-must-not-publish/);
        const published = await readPublished(fixture.output);
        assert.doesNotMatch(published, /tenant-.*-must-not-publish/);
        assert.match(published, /"accepted": false/);
        assert.ok(!(await readdir(fixture.output)).some((name) => name.endsWith(".tmp")));
    } finally {
        await rm(fixture.directory, { recursive: true, force: true });
    }
});

test("executor exceptions retain their already-written private result without exposing exception text", async () => {
    const fixture = await createDiagnosticsFixture();
    let executed = 0, captureDirectory;
    try {
        await assert.rejects(
            runCases(
                cases, fixture.output, sha, suiteHash,
                async (scenario, path) => {
                    executed++;
                    captureDirectory = dirname(path);
                    await writeFile(path, JSON.stringify(privateResult(scenario)));
                    throw new Error("tenant-executor-error-must-not-publish");
                },
                async () => {},
                false,
                0,
                fixture.options,
            ),
            /Evaluation execution failed/,
        );
        assert.equal(executed, 1);
        assert.match(await readFile(
            join(captureDirectory, `${cases[0].id}.json`), "utf8",
        ), /tenant-rationale-must-not-publish/);
        assert.equal((await stat(join(captureDirectory, "diagnostics.json"))).isFile(), true);
        assert.doesNotMatch(await readPublished(fixture.output), /tenant-.*-must-not-publish/);
    } finally {
        await rm(fixture.directory, { recursive: true, force: true });
    }
});

test("retention write failures visibly fail the gate even when all twenty case verdicts passed", async () => {
    const fixture = await createDiagnosticsFixture();
    let executed = 0, captureDirectory;
    try {
        await assert.rejects(
            runCases(
                cases, fixture.output, sha, suiteHash,
                async (scenario, path) => {
                    executed++;
                    captureDirectory = dirname(path);
                    if (executed === 1)
                        await mkdir(join(captureDirectory, "diagnostics.json"));
                    await writeFile(path, JSON.stringify(privateResult(scenario)));
                    return 0;
                },
                async () => {},
                false,
                0,
                fixture.options,
            ),
            /Private evaluation diagnostics retention failed/,
        );
        assert.equal(executed, 20);
        const report = JSON.parse(await readFile(join(fixture.output, "results.json"), "utf8"));
        assert.equal(report.verdict.accepted, false);
        assert.ok(report.verdict.failures.some((failure) => failure.includes("retention failed")));
        const published = await readPublished(fixture.output);
        assert.match(published, /Suite failure:.*retention failed/);
        assert.doesNotMatch(published, /tenant-.*-must-not-publish/);
        assert.ok(!published.includes(captureDirectory));
    } finally {
        await rm(fixture.directory, { recursive: true, force: true });
    }
});

test("private diagnostics reject CI while leaving the disabled default unchanged", async () => {
    const fixture = await createDiagnosticsFixture();
    try {
        for (const environment of [
            { GITHUB_ACTIONS: "true" }, { CI: "true" }, { CI: "1" }, { TF_BUILD: "True" },
        ])
            await assert.rejects(
                runCases(
                    cases, fixture.output, sha, suiteHash,
                    () => assert.fail("No CI case may start with private retention enabled"),
                    async () => {},
                    false,
                    0,
                    { ...fixture.options, environment },
                ),
                /local-only and cannot be enabled in CI/,
            );
        assert.deepEqual(await readdir(fixture.diagnostics), []);
        assert.equal(await preparePrivateDiagnostics(
            undefined, fixture.output, { CI: "true" }, fixture.sourceRoot,
        ), null);
        assert.equal(await preparePrivateDiagnostics(
            "", fixture.output, { GITHUB_ACTIONS: "true" }, fixture.sourceRoot,
        ), null);
    } finally {
        await rm(fixture.directory, { recursive: true, force: true });
    }
});

test("private diagnostics require existing absolute locations disjoint from repository and public output", async () => {
    const fixture = await createDiagnosticsFixture();
    try {
        for (const directory of [
            fixture.sourceRoot, fixture.output, fixture.directory,
            join(fixture.sourceRoot, "nested"), join(fixture.output, "nested"),
        ]) {
            await mkdir(directory, { recursive: true });
            await assert.rejects(
                preparePrivateDiagnostics(directory, fixture.output, {}, fixture.sourceRoot),
                /must not overlap the repository or published output/,
            );
        }
        await assert.rejects(
            preparePrivateDiagnostics(fixture.diagnostics, fixture.output, {}),
            /must not overlap the repository or published output/,
        );
        await assert.rejects(
            preparePrivateDiagnostics("relative-diagnostics", fixture.output, {}, fixture.sourceRoot),
            /existing absolute directory/,
        );
        await assert.rejects(
            preparePrivateDiagnostics(join(fixture.directory, "absent"), fixture.output, {}, fixture.sourceRoot),
            /existing, accessible directory/,
        );
        const file = join(fixture.directory, "not-a-directory");
        await writeFile(file, "not a directory");
        await assert.rejects(
            preparePrivateDiagnostics(file, fixture.output, {}, fixture.sourceRoot),
            /existing, accessible directory/,
        );
        assert.deepEqual(await readdir(fixture.diagnostics), []);
    } finally {
        await rm(fixture.directory, { recursive: true, force: true });
    }
});

test("private diagnostics resolve symlinks for destination, repository and public-output boundaries", async () => {
    const fixture = await createDiagnosticsFixture();
    try {
        const sourceAlias = join(fixture.directory, "source-alias");
        const outputAlias = join(fixture.directory, "output-alias");
        const privateAlias = join(fixture.directory, "private-alias");
        const type = process.platform === "win32" ? "junction" : "dir";
        await Promise.all([
            symlink(fixture.sourceRoot, sourceAlias, type),
            symlink(fixture.output, outputAlias, type),
            symlink(fixture.diagnostics, privateAlias, type),
        ]);
        for (const [directory, output, source] of [
            [sourceAlias, fixture.output, fixture.sourceRoot],
            [outputAlias, fixture.output, fixture.sourceRoot],
            [fixture.sourceRoot, fixture.output, sourceAlias],
            [fixture.output, outputAlias, fixture.sourceRoot],
        ])
            await assert.rejects(
                preparePrivateDiagnostics(directory, output, {}, source),
                /must not overlap the repository or published output/,
            );
        const first = await preparePrivateDiagnostics(
            privateAlias, outputAlias, {}, sourceAlias,
        );
        const second = await preparePrivateDiagnostics(
            privateAlias, outputAlias, {}, sourceAlias,
        );
        assert.notEqual(first, second);
        assert.equal(dirname(first), await realpath(fixture.diagnostics));
        assert.equal(dirname(second), await realpath(fixture.diagnostics));
    } finally {
        await rm(fixture.directory, { recursive: true, force: true });
    }
});

test("executor crashes cannot publish private captures or leave their temporary files behind", async () => {
    const directory = await createDirectory();
    const output = join(directory, "published");
    const preserved = join(directory, "unrelated.json.tmp");
    let executed = 0, captureDirectory;
    try {
        await writeFile(preserved, "do not remove caller-owned files");
        await assert.rejects(
            runCases(
                cases, output, sha, suiteHash,
                async (scenario, path) => {
                    executed++;
                    captureDirectory = dirname(path);
                    const result = privateResult(scenario);
                    await writeFile(path, JSON.stringify(result));
                    await writeFile(`${path}.tmp`, JSON.stringify(result));
                    assert.doesNotMatch(await readPublished(output), /tenant-.*-must-not-publish/);
                    if (executed === 2)
                        throw new Error("tenant-executor-error-must-not-publish");
                    return 0;
                },
                async () => {},
                false,
            ),
            /^Error: Evaluation execution failed/,
        );
        assert.equal(executed, 2);
        const report = JSON.parse(await readFile(join(output, "results.json"), "utf8"));
        assert.equal(report.results.length, 1);
        assert.equal(report.verdict.accepted, false);
        assert.ok(report.verdict.failures.some((failure) => failure.includes("execution failed")));
        assert.doesNotMatch(await readPublished(output), /tenant-.*-must-not-publish/);
        assert.ok(!(await readdir(output)).some((name) => name.endsWith(".tmp")));
        await assert.rejects(stat(captureDirectory), { code: "ENOENT" });
        assert.equal(await readFile(preserved, "utf8"), "do not remove caller-owned files");
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
});

test("leftover private .tmp output is not accepted and does not skip remaining cases", async () => {
    const directory = await createDirectory();
    let executed = 0, captureDirectory;
    try {
        const verdict = await runCases(
            cases.slice(0, 2), directory, sha, suiteHash,
            async (scenario, path) => {
                executed++;
                captureDirectory = dirname(path);
                await writeFile(`${path}.tmp`, JSON.stringify(privateResult(scenario)));
                return 0;
            },
            async () => {},
            false,
        );
        assert.equal(executed, 2);
        assert.equal(verdict.accepted, false);
        const report = JSON.parse(await readFile(join(directory, "results.json"), "utf8"));
        assert.ok(report.results.every((row) =>
            row.result === null && row.failures.includes("No structured evaluation result."),
        ));
        assert.doesNotMatch(await readPublished(directory), /tenant-.*-must-not-publish/);
        assert.ok(!(await readdir(directory)).some((name) => name.endsWith(".tmp")));
        await assert.rejects(stat(captureDirectory), { code: "ENOENT" });
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
});

test("internal-test publication rejects malformed raw values and retains only safe result fields", () => {
    for (const value of [null, "tenant-answer-must-not-publish", ["tenant-answer-must-not-publish"]])
        assert.equal(publishableResult(value, false), null);
    const result = privateResult(cases[0]);
    result.errors = ["tenant-error-must-not-publish"];
    result.reasons = ["tenant-reason-must-not-publish"];
    result.tools.push(null, { name: {}, success: "tenant-success-must-not-publish" });
    const published = publishableResult(result, false);
    assert.doesNotMatch(JSON.stringify(published), /tenant-.*-must-not-publish/);
    assert.equal(published.errorCount, 1);
    assert.equal(published.reasonCount, 1);
    assert.equal(published.answerWithheld, true);
    const synthetic = publishableResult(result, true);
    assert.equal(synthetic.answer, result.answer);
    assert.deepEqual(synthetic.errors, result.errors);
    assert.deepEqual(synthetic.reasons, result.reasons);
    assert.deepEqual(synthetic.judge, result.judge);
    assert.ok(!("failedToolDetails" in synthetic));
    assert.ok(!("toolDetails" in synthetic));
    assert.ok(!("failure" in synthetic));
    assert.ok(!("extra" in synthetic));
    assert.doesNotMatch(JSON.stringify(synthetic),
        /tenant-(?:arguments|details|extra|tool-output|success|private-error|private-phase|private-type)-must-not-publish/);
    assert.equal(result.tools[1].arguments, "tenant-arguments-must-not-publish");
    assert.equal(result.failedToolDetails[0].detail, "tenant-details-must-not-publish");
    assert.equal(result.toolDetails[0].result, "tenant-tool-output-must-not-publish");
    assert.equal(result.failure.detail, "tenant-private-error-must-not-publish");
});

test("a published directory cannot contain the private capture root", async () => {
    await assert.rejects(
        runCases(
            cases, repositoryRoot, sha, suiteHash,
            () => assert.fail("No case should start with an unsafe output directory"),
            async () => {},
            false,
        ),
        /published output directory cannot contain private captures/,
    );
});

for (const signal of ["SIGINT", "SIGTERM"])
    test(`${signal} stops the suite after its owned child and publishes no private data`, async () => {
        const directory = await createDirectory();
        const signals = new EventEmitter();
        const child = new EventEmitter();
        let executed = 0, killed = 0, cleared = 0, captureDirectory;
        try {
            await assert.rejects(
                runCases(
                    cases, directory, sha, suiteHash,
                    (scenario, path, candidate, hash, _, options) => {
                        executed++;
                        captureDirectory = dirname(path);
                        return executeCase(scenario, path, candidate, hash, () => child, {
                            ...options,
                            signals,
                            startTimer: () => {
                                queueMicrotask(() => {
                                    signals.emit(signal);
                                    signals.emit(signal);
                                });
                                return 1;
                            },
                            cancelTimer: () => cleared++,
                            terminateTree: async (owned) => {
                                assert.equal(owned, child);
                                killed++;
                                await writeFile(path, JSON.stringify(privateResult(scenario)));
                                await writeFile(`${path}.tmp`, JSON.stringify(privateResult(scenario)));
                                child.emit("close", 0);
                            },
                        });
                    },
                    async () => {},
                    false,
                    0,
                    { signals },
                ),
                (error) => error.code === "EVAL_INTERRUPTED" && error.message.includes(signal),
            );
            assert.equal(executed, 1);
            assert.equal(killed, 1);
            assert.equal(cleared, 1);
            assert.equal(signals.listenerCount("SIGINT"), 0);
            assert.equal(signals.listenerCount("SIGTERM"), 0);
            assert.equal(child.listenerCount("close"), 0);
            assert.equal(child.listenerCount("error"), 0);
            const report = JSON.parse(await readFile(join(directory, "results.json"), "utf8"));
            assert.equal(report.verdict.accepted, false);
            assert.equal(report.results.length, 0);
            assert.ok(report.verdict.failures.some((failure) => failure.includes(signal)));
            assert.match(
                await readFile(join(directory, "summary.md"), "utf8"),
                /Suite failure:.*interrupted/,
            );
            assert.doesNotMatch(await readPublished(directory), /tenant-.*-must-not-publish/);
            await assert.rejects(stat(captureDirectory), { code: "ENOENT" });
        } finally {
            await rm(directory, { recursive: true, force: true });
        }
    });

test("interruptions during identity renewal cannot launch a subsequent case", async () => {
    const directory = await createDirectory();
    const signals = new EventEmitter();
    try {
        await assert.rejects(
            runCases(
                cases.slice(0, 2), directory, sha, suiteHash,
                () => assert.fail("No case should start after an interruption"),
                async () => signals.emit("SIGTERM"),
                false,
                0,
                { signals },
            ),
            /interrupted by SIGTERM/,
        );
        assert.equal(signals.listenerCount("SIGINT"), 0);
        assert.equal(signals.listenerCount("SIGTERM"), 0);
        const report = JSON.parse(await readFile(join(directory, "results.json"), "utf8"));
        assert.equal(report.verdict.accepted, false);
        assert.equal(report.results.length, 0);
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
});

test("the real suite entry point exits nonzero on missing configuration and writes a failed summary", async () => {
    const directory = await createDirectory();
    try {
        const result = spawnSync(
            process.execPath,
            [fileURLToPath(new URL("./suite.mjs", import.meta.url))],
            {
                encoding: "utf8",
                env: {
                    ...process.env,
                    EVAL_EXPECTED_SHA: sha,
                    GITHUB_SHA: sha,
                    GITHUB_ACTIONS: "",
                    EVAL_DATA_CLASSIFICATION: "internal-test",
                    EVAL_MODEL_ENDPOINT: "",
                    EVAL_OUTPUT_DIRECTORY: directory,
                    EVAL_PRIVATE_DIAGNOSTICS_DIRECTORY: "",
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
        assert.match(
            await readFile(join(directory, "summary.md"), "utf8"),
            /Answers and judge rationale are withheld/,
        );
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
});

test("the real suite entry point rejects the wrong candidate revision before any live case", async () => {
    const directory = await createDirectory();
    try {
        const head = spawnSync("git", ["rev-parse", "HEAD"], {
            cwd: repositoryRoot,
            encoding: "utf8",
        });
        assert.equal(head.status, 0, head.stderr || head.error?.message);
        const wrongSha = head.stdout.trim() === sha ? "c".repeat(40) : sha;
        const result = spawnSync(
            process.execPath,
            [fileURLToPath(new URL("./suite.mjs", import.meta.url))],
            {
                encoding: "utf8",
                timeout: 10000,
                env: {
                    ...process.env,
                    GITHUB_ACTIONS: "",
                    GITHUB_SHA: wrongSha,
                    EVAL_EXPECTED_SHA: wrongSha,
                    EVAL_MODEL_ENDPOINT: "https://example.invalid",
                    EVAL_MODEL: "synthetic-model",
                    EVAL_JUDGE_MODEL: "synthetic-judge",
                    EVAL_TENANT_ID: "synthetic-tenant",
                    EVAL_SUBSCRIPTION_IDS: "synthetic-scope",
                    EVAL_DATA_CLASSIFICATION: "internal-test",
                    EVAL_ONLY_IDS: "no-matching-case-never-dispatch",
                    EVAL_OUTPUT_DIRECTORY: directory,
                    EVAL_PRIVATE_DIAGNOSTICS_DIRECTORY: "",
                },
            },
        );
        assert.equal(result.status, 1);
        assert.match(result.stderr, /does not match the checked-out git HEAD/);
        assert.match(
            await readFile(join(directory, "summary.md"), "utf8"),
            /\*\*FAIL\*\*/,
        );
        assert.deepEqual(await readdir(directory), ["summary.md"]);
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
});

test("the real suite entry point rejects private diagnostics in CI before credentials or live execution", async () => {
    const directory = await createDirectory();
    try {
        const result = spawnSync(
            process.execPath,
            [fileURLToPath(new URL("./suite.mjs", import.meta.url))],
            {
                encoding: "utf8",
                timeout: 10000,
                env: {
                    ...process.env,
                    GITHUB_ACTIONS: "true",
                    CI: "true",
                    GITHUB_SHA: sha,
                    EVAL_EXPECTED_SHA: sha,
                    EVAL_MODEL_ENDPOINT: "",
                    EVAL_DATA_CLASSIFICATION: "internal-test",
                    EVAL_OUTPUT_DIRECTORY: directory,
                    EVAL_PRIVATE_DIAGNOSTICS_DIRECTORY: directory,
                },
            },
        );
        assert.equal(result.status, 1);
        assert.match(result.stderr, /local-only and cannot be enabled in CI/);
        assert.match(
            await readFile(join(directory, "summary.md"), "utf8"),
            /\*\*FAIL\*\*/,
        );
        assert.deepEqual(await readdir(directory), ["summary.md"]);
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
});
