import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdir, mkdtemp, readFile, rm, stat, writeFile } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import {
    captureRollback,
    httpsOrigin,
    redact,
    restoreSettings,
    rollbackImage,
    smokeChat,
} from "../../infra/scripts/deployment-smoke.mjs";

const origin = "https://preview-random.westeurope-01.azurewebsites.net";
const code = "DEPLOY-CHECK-12345";
const helper = fileURLToPath(new URL("../../infra/scripts/deployment-smoke.mjs", import.meta.url));
const permissionFailure = "The model request failed (HTTP 403). HTTP 403 (ServiceError: UserError) Identity(object id: 1a2b3c4d-0000-4000-8000-123456789abc) does not have permissions for Microsoft.CognitiveServices/accounts/AIServices/agents/write actions. Please refer to https://learn.microsoft.com/azure/foundry/concepts/rbac-foundry to fix the permissions issue. { \"code\": \"UserError\",";

function sse(...events) {
    return events.map((event) => `data: ${typeof event === "string" ? event : JSON.stringify(event)}\n\n`).join("");
}

function stream(chunks) {
    const encoder = new TextEncoder();
    return new ReadableStream({
        start(controller) {
            for (const chunk of chunks) controller.enqueue(encoder.encode(chunk));
            controller.close();
        },
    });
}

function deployment({ chat = () => sse({ type: "delta", content: `Code: ${code}` }, "[DONE]"), cookie = "finops.session=abc; path=/; secure; httponly", versionStatus = 200, requests = [] } = {}) {
    return async (url, init = {}) => {
        requests.push({ url, init });
        if (url.endsWith("/api/version")) {
            const headers = new Headers({ "content-type": "application/json" });
            if (cookie) headers.append("set-cookie", cookie);
            return new Response("{}", { status: versionStatus, headers });
        }
        const result = await chat(init);
        if (result instanceof Response) return result;
        return new Response(Array.isArray(result) ? stream(result) : result,
            { status: 200, headers: { "content-type": "text/event-stream" } });
    };
}

const once = (fetchImpl, options = {}) => smokeChat(origin, {
    fetchImpl, attempts: 1, sleep: async () => {}, log: () => {}, nextCode: () => code, ...options,
});

test("a real streamed answer containing the requested code passes as a browser-shaped anonymous turn", async () => {
    const requests = [];
    const result = await once(deployment({ requests }));
    assert.equal(result.attempt, 1);
    assert.equal(requests.length, 2);
    assert.equal(requests[0].url, `${origin}/api/version`);
    const chat = requests[1];
    assert.equal(chat.url, `${origin}/api/chat`);
    assert.equal(chat.init.method, "POST");
    assert.equal(chat.init.redirect, "error");
    assert.equal(chat.init.headers.origin, origin);
    assert.equal(chat.init.headers.cookie, "finops.session=abc");
    assert.equal(chat.init.headers["content-type"], "application/json");
    assert.match(JSON.parse(chat.init.body).prompt, new RegExp(code));
});

test("events split across network chunks and final message events are reassembled", async () => {
    const body = sse({ type: "session", id: "s" }, { type: "delta", content: "DEPLOY-" }, { type: "delta", content: "CHECK-12345" }, "[DONE]");
    await once(deployment({ chat: () => [body.slice(0, 17), body.slice(17, 60), body.slice(60)] }));
    await once(deployment({ chat: () => sse({ type: "message", content: code.toLowerCase() }, "[DONE]") }));
});

test("the build 168 permission failure blocks with a redacted, actionable reason", async () => {
    const failure = once(deployment({ chat: () => sse({ type: "error", code: "model_error", message: permissionFailure }, "[DONE]") }));
    await assert.rejects(failure, (error) => {
        assert.match(error.message, /error 'model_error'.*HTTP 403.*agents\/write/);
        assert.match(error.message, /managed identity \(not the evaluation identity\) needs Foundry User/);
        assert.doesNotMatch(error.message, /1a2b3c4d|learn\.microsoft\.com|https:|UserError",/);
        return true;
    });
});

test("incomplete, empty, off-contract, busy and HTTP-failed turns never pass", async () => {
    const cases = [
        [() => sse({ type: "delta", content: code }), /ended before the turn completed/],
        [() => sse({ type: "timing", phase: "chat.total" }, "[DONE]"), /without an answer/],
        [() => sse({ type: "delta", content: "Hello! How can I help with FinOps?" }, "[DONE]"), /without the requested verification code/],
        [() => sse({ type: "busy", message: "still working" }, "[DONE]"), /error 'busy'/],
        [() => sse({ type: "delta", content: code }, { type: "error", code: "empty_result", message: "x" }, "[DONE]"), /error 'empty_result'/],
        [() => new Response("", { status: 401 }), /HTTP 401/],
        [() => new Response("<html>", { status: 200, headers: { "content-type": "text/html" } }), /did not return an event stream/],
    ];
    for (const [chat, expected] of cases)
        await assert.rejects(once(deployment({ chat })), expected);
    await assert.rejects(once(deployment({ cookie: "" })), /did not issue an anonymous chat session cookie/);
    await assert.rejects(once(deployment({ versionStatus: 503 })), /HTTP 503 for the version probe/);
});

test("hung and unreachable deployments fail within the budget without echoing private hosts", async () => {
    const hung = deployment({ chat: ({ signal }) => new Promise((_, reject) => signal.addEventListener("abort", () => reject(new DOMException("aborted", "AbortError")))) });
    await assert.rejects(once(hung, { timeoutMs: 20 }), /No complete chat answer within 0 s/);
    const unreachable = async () => { throw new TypeError("fetch failed", { cause: Object.assign(new Error("getaddrinfo ENOTFOUND private-host.example.com"), { code: "ENOTFOUND" }) }); };
    await assert.rejects(once(unreachable), (error) => {
        assert.match(error.message, /could not be reached \(ENOTFOUND\)/);
        assert.doesNotMatch(error.message, /private-host/);
        return true;
    });
});

test("transient failures retry with fresh sessions and a persistent failure reports the last reason", async () => {
    let calls = 0;
    const warnings = [];
    const sleeps = [];
    const flaky = deployment({ chat: () => ++calls === 1 ? new Response("", { status: 502 }) : sse({ type: "delta", content: code }, "[DONE]") });
    const result = await smokeChat(origin, { fetchImpl: flaky, attempts: 3, delayMs: 10, sleep: async (ms) => { sleeps.push(ms); }, log: (line) => warnings.push(line), nextCode: () => code });
    assert.equal(result.attempt, 2);
    assert.deepEqual(sleeps, [10]);
    assert.match(warnings[0], /^::warning::Deployed chat attempt 1\/3 failed: .*HTTP 502/);
    await assert.rejects(smokeChat(origin, { fetchImpl: deployment({ chat: () => new Response("", { status: 500 }) }), attempts: 3, delayMs: 0, sleep: async () => {}, log: () => {} }), /HTTP 500/);
});

test("only HTTPS origins are accepted as smoke targets", () => {
    assert.equal(httpsOrigin(`${origin}/`), origin);
    assert.equal(httpsOrigin("HTTPS://Preview.Example.TEST"), "https://preview.example.test");
    for (const value of [undefined, "", ` ${origin}`, "http://preview.example.test", `${origin}/api`, `${origin}:8443`,
        "https://user:pass@preview.example.test", `${origin}?x=1`, "https://localhost"])
        assert.throws(() => httpsOrigin(value), /HTTPS origin|Invalid smoke-test hostname/);
});

test("redaction removes identifiers and locations but keeps the failing action", () => {
    const text = redact(`${permissionFailure} /subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-private/providers/x user@contoso.example.com 10.1.2.3 acct.services.ai.azure.com eyJhbGciOi.eyJzdWIiOi.c2lnbmF0dXJl\u0007`);
    assert.match(text, /Microsoft\.CognitiveServices\/accounts\/AIServices\/agents\/write/);
    for (const hidden of [/1a2b3c4d/, /learn\.microsoft/, /rg-private/, /contoso/, /10\.1\.2\.3/, /services\.ai\.azure\.com/, /eyJ/, /\u0007/])
        assert.doesNotMatch(text, hidden);
    assert.ok(redact("x".repeat(1000)).length <= 300);
});

test("rollback targets only a single registry container image", () => {
    assert.deepEqual(rollbackImage("DOCKER|registry.azurecr.io/finops-agent:test-0123abc"),
        { image: "registry.azurecr.io/finops-agent:test-0123abc", registry: "registry.azurecr.io" });
    assert.deepEqual(rollbackImage(`DOCKER|mcr.microsoft.com/appsvc/staticsite@sha256:${"a".repeat(64)}`),
        { image: `mcr.microsoft.com/appsvc/staticsite@sha256:${"a".repeat(64)}`, registry: "mcr.microsoft.com" });
    for (const value of [null, "", "SITECONTAINERS", "COMPOSE|abc", "DOTNETCORE|10.0", "DOCKER|finops-agent:latest",
        "DOCKER|registry.azurecr.io/a:b; rm -rf /", "DOCKER|registry.azurecr.io/a:b\n", "DOCKER|registry.azurecr.io/$(id):x"])
        assert.equal(rollbackImage(value), null, String(value));
});

test("rollback restores only the three prior model keys and rejects ambiguous state", () => {
    const current = [
        { name: "AzureOpenAI__Endpoint", value: "https://old.example.test/", slotSetting: false },
        { name: "AzureOpenAI__DeploymentName", value: "old-model", slotSetting: true },
        { name: "Unrelated", value: "keep", slotSetting: false },
    ];
    assert.deepEqual(restoreSettings(current), [
        { name: "AzureOpenAI__Endpoint", value: "https://old.example.test/", slotSetting: false },
        { name: "AzureOpenAI__DeploymentName", value: "old-model", slotSetting: true },
    ]);
    assert.deepEqual(restoreSettings([]), []);
    assert.throws(() => restoreSettings(null));
    assert.throws(() => restoreSettings([current[0], current[0]]), /ambiguous/);
    assert.throws(() => restoreSettings([{ name: "AzureOpenAI__Endpoint", value: null }]));
});

async function fixture(run) {
    const root = fileURLToPath(new URL("./obj/", import.meta.url));
    await mkdir(root, { recursive: true });
    const directory = await mkdtemp(join(root, "deployment-smoke-"));
    try {
        await run(directory);
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
}

test("capture writes private rollback files, removes raw CLI output and never prints values", async () => {
    await fixture(async (directory) => {
        const state = join(directory, "state");
        await mkdir(state);
        const output = join(directory, "output");
        await writeFile(join(state, "runtime.json"), JSON.stringify("DOCKER|registry.azurecr.io/finops-agent:test-0123abc"));
        await writeFile(join(state, "current-settings.json"), JSON.stringify([
            { name: "AzureOpenAI__Endpoint", value: "https://private-endpoint.example.test/", slotSetting: false },
        ]));
        const result = spawnSync(process.execPath, [helper, "capture", state], { env: { ...process.env, GITHUB_OUTPUT: output }, encoding: "utf8" });
        assert.equal(result.status, 0, result.stderr);
        assert.doesNotMatch(result.stdout + result.stderr, /private-endpoint|registry\.azurecr\.io/);
        assert.match(result.stdout, /restores only model settings that existed/);
        assert.equal(await readFile(output, "utf8"), "captured=true\n");
        assert.equal(await readFile(join(state, "image"), "utf8"), "registry.azurecr.io/finops-agent:test-0123abc");
        assert.equal(await readFile(join(state, "registry"), "utf8"), "registry.azurecr.io");
        assert.equal(JSON.parse(await readFile(join(state, "restore-settings.json"), "utf8"))[0].value, "https://private-endpoint.example.test/");
        if (process.platform !== "win32")
            assert.equal((await stat(join(state, "restore-settings.json"))).mode & 0o777, 0o600);
        await assert.rejects(stat(join(state, "current-settings.json")), { code: "ENOENT" });
        await assert.rejects(stat(join(state, "runtime.json")), { code: "ENOENT" });
    });
    await fixture(async (directory) => {
        const output = join(directory, "output");
        await writeFile(join(directory, "runtime.json"), JSON.stringify("SITECONTAINERS"));
        await writeFile(join(directory, "current-settings.json"), "[]");
        assert.equal(await captureRollback(directory, output), false);
        assert.equal(await readFile(output, "utf8"), "captured=false\n");
        await assert.rejects(stat(join(directory, "image")), { code: "ENOENT" });
    });
});

test("CLI rejects an invalid smoke target before any request and without echoing it", () => {
    for (const command of ["chat", "check-url"]) {
        const result = spawnSync(process.execPath, [helper, command], { env: { ...process.env, SMOKE_URL: "http://private-host.example.test/path" }, encoding: "utf8" });
        assert.equal(result.status, 1);
        assert.equal(result.stdout, "");
        assert.match(result.stderr, /^::error::.*HTTPS origin/);
        assert.doesNotMatch(result.stderr, /private-host/);
    }
    assert.equal(spawnSync(process.execPath, [helper, "check-url"], { env: { ...process.env, SMOKE_URL: origin }, encoding: "utf8" }).status, 0);
});

for (const [file, target] of [["feature.yml", "steps.target.outputs.slot_url"], ["main.yml", "env.VERIFY_URL"]]) {
    test(`${file} blocks on a real deployed chat answer and restores the previous state on failure`, async () => {
        const workflow = await readFile(new URL(`../../.github/workflows/${file}`, import.meta.url), "utf8");
        const at = (text) => {
            const index = workflow.indexOf(text);
            assert.ok(index >= 0, `${file} is missing ${text}`);
            return index;
        };
        const capture = at("deployment-smoke.mjs capture");
        assert.ok(capture < at("az webapp config appsettings set"));
        assert.ok(capture < at("az webapp config container set"));
        assert.ok(at("/api/version") < at("deployment-smoke.mjs chat"));
        assert.ok(at("deployment-smoke.mjs chat") < at("Restore the previous"));
        assert.match(workflow, new RegExp(`SMOKE_URL: \\$\\{\\{ ${target.replace(/\./g, "\\.")} \\}\\}\\r?\\n\\s+run: node infra/scripts/deployment-smoke\\.mjs chat`));
        assert.match(workflow, /if: \(failure\(\) \|\| cancelled\(\)\) && steps\.rollback-state\.outputs\.captured == 'true' && steps\.apply-settings\.outcome != 'skipped'/);
        assert.match(workflow, /--settings "@\$state\/restore-settings\.json"/);
        assert.match(workflow, /--container-image-name "\$\(<"\$state\/image"\)"/);
        assert.match(workflow, /if: always\(\)\r?\n\s+shell: bash\r?\n\s+run: rm -rf "\$RUNNER_TEMP\/deploy-rollback"/);
        assert.doesNotMatch(workflow, /continue-on-error/);
    });
}

test("production validates the smoke target before Azure login", async () => {
    const workflow = await readFile(new URL("../../.github/workflows/main.yml", import.meta.url), "utf8");
    assert.ok(workflow.indexOf("deployment-smoke.mjs check-url") < workflow.indexOf("uses: azure/login"));
});
