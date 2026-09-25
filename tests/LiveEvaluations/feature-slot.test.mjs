import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { mkdir, mkdtemp, readFile, rm, stat, writeFile } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import {
    endpointFingerprint,
    modelSettings,
    readFeatureConfiguration,
    readProductionConfiguration,
    validateFeatureTarget,
    validateModelContract,
    verifyModelSettings,
    verifyVersion,
} from "../../infra/scripts/feature-slot.mjs";

const evaluatedEndpointSha256 = createHash("sha256")
    .update("https://synthetic-model.openai.azure.com", "utf8").digest("hex");
const environment = {
    TARGET_SUBSCRIPTION: "11111111-1111-1111-1111-111111111111",
    RESOURCE_GROUP: "rg-synthetic",
    WEBAPP_NAME: "synthetic-app",
    SLOT_NAME: "test",
    VERIFY_URL: "https://preview-random.westeurope-01.azurewebsites.net/",
    PRODUCTION_VERIFY_URL: "https://production.example.test",
    AOAI_ENDPOINT: "https://synthetic-model.openai.azure.com/",
    EVAL_MODEL_ENDPOINT: "https://synthetic-model.openai.azure.com/",
    EVALUATED_ENDPOINT_SHA256: evaluatedEndpointSha256,
    EVALUATED_MODEL: "evaluated-deployment-not-the-default",
    EVALUATED_REASONING_EFFORT: "medium",
    EVALUATED_SHA: "a".repeat(40),
    GITHUB_SHA: "a".repeat(40),
};
const config = readFeatureConfiguration(environment);
const parent = {
    id: `/subscriptions/${environment.TARGET_SUBSCRIPTION}/resourceGroups/rg-synthetic/providers/Microsoft.Web/sites/synthetic-app`,
    name: "synthetic-app",
    type: "Microsoft.Web/sites",
    defaultHostName: "synthetic-app.azurewebsites.net",
};
const slot = {
    id: `${parent.id}/slots/test`,
    name: "synthetic-app/test",
    type: "Microsoft.Web/sites/slots",
    defaultHostName: "preview-random.westeurope-01.azurewebsites.net",
};
const helper = fileURLToPath(new URL("../../infra/scripts/feature-slot.mjs", import.meta.url));

async function fixture(run) {
    const root = fileURLToPath(new URL("./obj/", import.meta.url));
    await mkdir(root, { recursive: true });
    const directory = await mkdtemp(join(root, "feature-slot-"));
    try {
        await run(directory);
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
}

function invoke(command, paths = [], overrides = {}) {
    return spawnSync(process.execPath, [helper, command, ...paths], {
        env: { ...process.env, ...environment, ...overrides },
        encoding: "utf8",
    });
}

test("feature target uses exact Azure resource metadata and its actual hostname", () => {
    assert.equal(validateFeatureTarget(config, parent, slot), `https://${slot.defaultHostName}`);
    assert.equal(validateFeatureTarget(config, {
        ...parent, id: parent.id.toUpperCase(), type: parent.type.toUpperCase(),
    }, {
        ...slot, id: slot.id.toUpperCase(), name: "TEST", defaultHostName: slot.defaultHostName.toUpperCase(),
    }), `https://${slot.defaultHostName}`);
    assert.notEqual(slot.defaultHostName, `${config.webAppName}-${config.slotName}.azurewebsites.net`);
});

test("invalid, blank and case-insensitive production slots fail closed", () => {
    for (const value of [undefined, "", " ", "\t", "\n", "production", "Production", "PRODUCTION",
        " production ", "test ", "test\nmodel=other", "../test", "test/child", 123])
        assert.throws(() => readFeatureConfiguration({ ...environment, SLOT_NAME: value }));
    for (const [key, value] of [
        ["TARGET_SUBSCRIPTION", ""], ["TARGET_SUBSCRIPTION", "a display name"],
        ["RESOURCE_GROUP", " "], ["RESOURCE_GROUP", "../another"],
        ["WEBAPP_NAME", ""], ["WEBAPP_NAME", "other/slots/production"],
    ])
        assert.throws(() => readFeatureConfiguration({ ...environment, [key]: value }));
});

test("wrong subscription, group, app, slot, name or resource type is rejected", () => {
    for (const [key, value] of [
        ["TARGET_SUBSCRIPTION", "22222222-2222-2222-2222-222222222222"],
        ["RESOURCE_GROUP", "rg-other"], ["WEBAPP_NAME", "other-app"], ["SLOT_NAME", "other"],
    ])
        assert.throws(() => validateFeatureTarget(
            readFeatureConfiguration({ ...environment, [key]: value }), parent, slot));
    for (const changed of [
        { id: parent.id }, { id: `${slot.id}/config/appsettings` },
        { id: `${slot.id}/` }, { type: "Microsoft.Web/sites" },
        { name: "production" }, { name: "other-app/test" }, { id: undefined },
    ])
        assert.throws(() => validateFeatureTarget(config, parent, { ...slot, ...changed }));
    for (const changed of [
        { id: slot.id }, { type: slot.type }, { name: "other-app" },
    ])
        assert.throws(() => validateFeatureTarget(config, { ...parent, ...changed }, slot));
    assert.throws(() => validateFeatureTarget(config, null, slot));
    assert.throws(() => validateFeatureTarget(config, parent, null));
});

test("only an HTTPS origin matching the slot is accepted, never production", () => {
    for (const value of [
        "", " ", "http://preview-random.westeurope-01.azurewebsites.net",
        `https://user:private@${slot.defaultHostName}`, `https://${slot.defaultHostName}:443`,
        `https://${slot.defaultHostName}/api`, `https://${slot.defaultHostName}/../`,
        `https://${slot.defaultHostName}/?secret=private`, `https://${slot.defaultHostName}/#fragment`,
        `https://${slot.defaultHostName}\\`, `https://${slot.defaultHostName}\n`,
        environment.PRODUCTION_VERIFY_URL,
    ])
        assert.throws(() => readFeatureConfiguration({ ...environment, VERIFY_URL: value }));
    for (const value of [`https://${parent.defaultHostName}`, "https://wrong.example.test"])
        assert.throws(() => validateFeatureTarget(
            readFeatureConfiguration({ ...environment, VERIFY_URL: value }), parent, slot));
    assert.throws(() => validateFeatureTarget(config,
        { ...parent, defaultHostName: slot.defaultHostName }, slot));
    assert.throws(() => readFeatureConfiguration({
        ...environment, PRODUCTION_VERIFY_URL: environment.VERIFY_URL,
    }));
    for (const value of [undefined, "", " ", "https://other.example.test", "other.example.test/path",
        "other.example.test:443", "other..example.test", "user@other.example.test"])
        assert.throws(() => validateFeatureTarget(config, parent, { ...slot, defaultHostName: value }));
});

test("endpoint fingerprints normalize URL spelling without dropping account, port or path differences", () => {
    for (const endpoint of [
        "https://synthetic-model.openai.azure.com",
        "https://synthetic-model.openai.azure.com/",
        "HTTPS://SYNTHETIC-MODEL.OPENAI.AZURE.COM:443///",
    ])
        assert.equal(endpointFingerprint(endpoint), evaluatedEndpointSha256);
    for (const endpoint of [
        "https://another-model.openai.azure.com/",
        "https://synthetic-model.openai.azure.com:8443/",
        "https://synthetic-model.openai.azure.com/Gateway/V1/",
    ])
        assert.notEqual(endpointFingerprint(endpoint), evaluatedEndpointSha256);
    const gateway = "https://synthetic-model.openai.azure.com/Gateway/V1";
    assert.equal(endpointFingerprint(`${gateway}///`),
        createHash("sha256").update(gateway, "utf8").digest("hex"));
    assert.notEqual(endpointFingerprint(gateway), endpointFingerprint(gateway.toLowerCase()));
});

test("fingerprints reject malformed or credential-bearing endpoints without echoing them", () => {
    for (const endpoint of [
        undefined, "", " ", "not-a-url", "http://private-endpoint.example.test",
        "https://private-endpoint.example.test/?credential=do-not-publish",
        "https://private-endpoint.example.test/#do-not-publish",
        "https://private-endpoint.example.test/?", "https://private-endpoint.example.test/#",
        "https://do-not-publish@private-endpoint.example.test",
        " https://private-endpoint.example.test/", "https://private-endpoint.example.test/\n",
        "https://private-endpoint.example.test\\path",
    ])
        assert.throws(() => endpointFingerprint(endpoint), (error) =>
            /endpoint/i.test(error.message) &&
            !/private-endpoint|do-not-publish/.test(error.message));
});

test("deployment requires the successful endpoint fingerprint and retains the original matching secret", () => {
    for (const fingerprint of [
        undefined, "", " ", "a".repeat(63), "g".repeat(64), evaluatedEndpointSha256.toUpperCase(),
    ])
        assert.throws(() => readFeatureConfiguration({
            ...environment, EVALUATED_ENDPOINT_SHA256: fingerprint,
        }), /evaluated endpoint fingerprint/);
    assert.throws(() => readFeatureConfiguration({
        ...environment, AOAI_ENDPOINT: "https://another-model.openai.azure.com/",
    }), /Deployment endpoint does not match.*Deployment is blocked/);
    const original = "HTTPS://SYNTHETIC-MODEL.OPENAI.AZURE.COM:443///";
    const matching = readFeatureConfiguration({ ...environment, AOAI_ENDPOINT: original });
    assert.equal(matching.endpoint, original);
    assert.equal(modelSettings(matching)[0].value, original);
});

test("the exact successful evaluation contract is mandatory and has no model fallback", () => {
    assert.deepEqual(validateModelContract({
        model: config.model, reasoningEffort: config.reasoningEffort, sha: config.sha,
    }), {
        model: environment.EVALUATED_MODEL,
        reasoningEffort: environment.EVALUATED_REASONING_EFFORT,
        sha: environment.EVALUATED_SHA,
    });
    for (const [key, value] of [
        ["EVALUATED_MODEL", ""], ["EVALUATED_MODEL", " "], ["EVALUATED_MODEL", "model\nsha=other"],
        ["EVALUATED_REASONING_EFFORT", ""], ["EVALUATED_REASONING_EFFORT", "unsupported"],
        ["EVALUATED_SHA", ""], ["EVALUATED_SHA", "a".repeat(7)],
        ["EVALUATED_SHA", "b".repeat(40)], ["GITHUB_SHA", "b".repeat(40)],
        ["AOAI_ENDPOINT", ""], ["AOAI_ENDPOINT", "http://private.example.test"],
        ["AOAI_ENDPOINT", "https://private@example.test"],
        ["AOAI_ENDPOINT", "https://example.test/?token=private"],
    ])
        assert.throws(() => readFeatureConfiguration({ ...environment, [key]: value }));
});

test("settings override only the three model keys and never mark them slot-sticky", () => {
    assert.deepEqual(modelSettings(config), [
        { name: "AzureOpenAI__Endpoint", value: environment.AOAI_ENDPOINT, slotSetting: false },
        { name: "AzureOpenAI__DeploymentName", value: environment.EVALUATED_MODEL, slotSetting: false },
        { name: "AzureOpenAI__ReasoningEffort", value: environment.EVALUATED_REASONING_EFFORT, slotSetting: false },
    ]);
});

test("effective settings must contain exactly one matching value for all three keys", () => {
    const settings = [
        { name: "AzureOpenAI__Endpoint", value: config.endpoint },
        { name: "AzureOpenAI__DeploymentName", value: config.model },
        { name: "AzureOpenAI__ReasoningEffort", value: config.reasoningEffort },
    ];
    verifyModelSettings(settings, config);
    for (let index = 0; index < settings.length; index++) {
        assert.throws(() => verifyModelSettings(settings.filter((_, i) => i !== index), config));
        assert.throws(() => verifyModelSettings(
            settings.map((setting, i) => i === index ? { ...setting, value: "wrong" } : setting), config));
    }
    for (const value of [null, {}, [...settings, settings[0]], [settings[0], settings[0], settings[2]]])
        assert.throws(() => verifyModelSettings(value, config));
});

test("revision verification requires the full exact SHA, build and unmodified branch", () => {
    const expected = { sha: config.sha, build: "42", branch: "feature/$(printf-do-not-execute)" };
    verifyVersion({ ...expected }, expected);
    for (const changed of [
        { sha: expected.sha.slice(0, 7) }, { sha: "b".repeat(40) },
        { build: "41" }, { build: 42 }, { branch: "main" }, { branch: undefined },
    ])
        assert.throws(() => verifyVersion({ ...expected, ...changed }, expected));
    assert.throws(() => verifyVersion(expected, { ...expected, sha: expected.sha.slice(0, 7) }));
    assert.throws(() => verifyVersion(null, expected));
});

test("CLI exports only the successful model contract and endpoint fingerprint without coordinates", async () => {
    await fixture(async (directory) => {
        const output = join(directory, "output");
        const result = invoke("evaluation-output", [], {
            GITHUB_OUTPUT: output,
            EVAL_MODEL: "environment-specific-model",
            EVAL_REASONING_EFFORT: "high",
        });
        assert.equal(result.status, 0, result.stderr);
        assert.equal(result.stdout, "");
        assert.equal(result.stderr, "");
        const contract = await readFile(output, "utf8");
        assert.equal(contract,
            `model=environment-specific-model\nreasoning_effort=high\nsha=${config.sha}\nendpoint_sha256=${evaluatedEndpointSha256}\n`);
        assert.doesNotMatch(contract, /synthetic-model|openai\.azure\.com|https:/);
        const rejected = invoke("evaluation-output", [], {
            GITHUB_OUTPUT: output, EVAL_MODEL: "", EVAL_REASONING_EFFORT: "high",
        });
        assert.equal(rejected.status, 1);
        assert.doesNotMatch(rejected.stderr, /synthetic-model|openai\.azure\.com/);
        const invalidEndpoint = invoke("evaluation-output", [], {
            GITHUB_OUTPUT: output, EVAL_MODEL: "environment-specific-model", EVAL_REASONING_EFFORT: "high",
            EVAL_MODEL_ENDPOINT: "https://private-endpoint.example.test/?credential=do-not-publish",
        });
        assert.equal(invalidEndpoint.status, 1);
        assert.equal(invalidEndpoint.stdout, "");
        assert.doesNotMatch(invalidEndpoint.stderr, /private-endpoint|do-not-publish/);
        assert.equal(await readFile(output, "utf8"), contract);
    });
});

test("CLI blocks a same-model different-endpoint deployment before creating settings", async () => {
    await fixture(async (directory) => {
        const parameters = join(directory, "settings.json");
        const mismatched = { AOAI_ENDPOINT: "https://private-mismatched-account.example.test/" };
        for (const [command, paths] of [["check-config", []], ["settings", [parameters]]]) {
            const result = invoke(command, paths, mismatched);
            assert.equal(result.status, 1);
            assert.equal(result.stdout, "");
            assert.match(result.stderr, /Deployment endpoint does not match.*Deployment is blocked/);
            assert.doesNotMatch(result.stderr, /private-mismatched-account|synthetic-model|https:/);
        }
        await assert.rejects(stat(parameters), { code: "ENOENT" });
        const matching = invoke("check-config", [], {
            AOAI_ENDPOINT: "HTTPS://SYNTHETIC-MODEL.OPENAI.AZURE.COM:443/",
        });
        assert.equal(matching.status, 0, matching.stderr);
        assert.equal(matching.stdout, "");
        assert.equal(matching.stderr, "");
    });
});

test("CLI writes private settings without overwriting an existing file or printing them", async () => {
    await fixture(async (directory) => {
        const path = join(directory, "settings.json");
        const result = invoke("settings", [path]);
        assert.equal(result.status, 0, result.stderr);
        assert.equal(result.stdout, "");
        const content = await readFile(path, "utf8");
        assert.deepEqual(JSON.parse(content), modelSettings(config));
        if (process.platform !== "win32")
            assert.equal((await stat(path)).mode & 0o777, 0o600);
        const second = invoke("settings", [path]);
        assert.equal(second.status, 1);
        assert.equal(await readFile(path, "utf8"), content);
        assert.doesNotMatch(second.stderr, /synthetic-model|evaluated-deployment/);
    });
});

test("CLI emits only the Azure preview URL and fails safely on malformed private metadata", async () => {
    await fixture(async (directory) => {
        const parentPath = join(directory, "parent.json");
        const slotPath = join(directory, "slot.json");
        const output = join(directory, "output");
        await writeFile(parentPath, JSON.stringify(parent));
        await writeFile(slotPath, JSON.stringify(slot));
        const result = invoke("check-target", [parentPath, slotPath], { GITHUB_OUTPUT: output });
        assert.equal(result.status, 0, result.stderr);
        assert.equal(result.stdout, "");
        assert.equal(await readFile(output, "utf8"), `slot_url=https://${slot.defaultHostName}\n`);
        await writeFile(slotPath, '{"private":"credential-must-not-appear", broken}');
        const rejected = invoke("check-target", [parentPath, slotPath], { GITHUB_OUTPUT: output });
        assert.equal(rejected.status, 1);
        assert.match(rejected.stderr, /Unable to read a valid workflow metadata document/);
        assert.doesNotMatch(rejected.stderr, /credential-must-not-appear/);
    });
});

test("workflow validates the target before writes and merges only serialized model settings", async () => {
    const workflow = await readFile(new URL("../../.github/workflows/feature.yml", import.meta.url), "utf8");
    assert.ok(workflow.indexOf("feature-slot.mjs check-config") < workflow.indexOf("uses: azure/login"));
    assert.ok(workflow.indexOf("feature-slot.mjs check-target") < workflow.indexOf("push: true"));
    assert.ok(workflow.indexOf("feature-slot.mjs check-target") < workflow.indexOf("feature-slot.mjs settings"));
    assert.ok(workflow.indexOf("feature-slot.mjs settings") < workflow.indexOf("az webapp config appsettings set"));
    assert.ok(workflow.indexOf("az webapp config appsettings set") < workflow.indexOf("az webapp config container set"));
    assert.ok(workflow.indexOf("az webapp config container set") < workflow.indexOf("feature-slot.mjs check-settings"));
    assert.match(workflow, /cancel-in-progress: false/);
    assert.equal((workflow.match(/az webapp config appsettings set/g) ?? []).length, 1);
    assert.equal((workflow.match(/--settings "@\$work\/settings\.json"/g) ?? []).length, 1);
    assert.match(workflow, /umask 077/);
    assert.match(workflow, /trap 'rm -f "\$work\/settings\.json"; rmdir "\$work"' EXIT/);
    assert.match(workflow, /--slot "\$SLOT_NAME"/);
    // Website Contributor must be sufficient: no ARM deployments, role or account writes.
    assert.doesNotMatch(workflow,
        /az deployment|\.bicep|--mode Complete|appsettings delete|--enable-immutable|az role|az cognitiveservices|slot (?:create|swap)|az webapp create|az account set/);
    for (const command of workflow.replace(/\\\r?\n\s*/g, " ").split(/\r?\n/)
        .filter((line) => !line.trimStart().startsWith("#") && /az (?:webapp|acr) /.test(line)))
        assert.match(command, /--subscription "\$TARGET_SUBSCRIPTION"/);
    for (const match of workflow.matchAll(/uses: (?!\.\/)([^\n]+)/g))
        assert.match(match[1], /@[a-f0-9]{40} # v\d/);
});

test("workflow uses Azure preview metadata and passes branch text as data, never executable shell", async () => {
    const workflow = await readFile(new URL("../../.github/workflows/feature.yml", import.meta.url), "utf8");
    const runLines = [];
    let runIndent = -1;
    for (const line of workflow.split(/\r?\n/)) {
        const start = /^(\s*)run: (.*)$/.exec(line);
        if (start) {
            runIndent = start[2] === "|" ? start[1].length : -1;
            runLines.push(start[2]);
        } else if (runIndent >= 0 && line.trim()) {
            if (/^\s*/.exec(line)[0].length > runIndent) runLines.push(line);
            else runIndent = -1;
        }
    }
    assert.doesNotMatch(runLines.join("\n"), /\$\{\{/);
    assert.match(workflow, /PREVIEW_URL: \$\{\{ steps\.target\.outputs\.slot_url \}\}/);
    assert.match(workflow, /EXPECTED_BRANCH: \$\{\{ steps\.meta\.outputs\.branch \}\}/);
    assert.match(workflow, /"\$PREVIEW_URL\/api\/version"/);
    assert.match(workflow, /'- \*\*Preview URL:\*\* %s\\n' "\$PREVIEW_URL"/);
    assert.doesNotMatch(runLines.join("\n"), /\$\{?VERIFY_URL|curl .*(?:--location| -L\b)/);
});

test("production deployment applies and verifies only the evaluated model contract", async () => {
    const production = readProductionConfiguration(environment);
    assert.deepEqual(production, {
        endpoint: environment.AOAI_ENDPOINT,
        model: environment.EVALUATED_MODEL,
        reasoningEffort: environment.EVALUATED_REASONING_EFFORT,
        sha: environment.EVALUATED_SHA,
    });
    assert.throws(() => readProductionConfiguration({ ...environment, AOAI_ENDPOINT: "https://other-model.openai.azure.com/" }),
        /does not match the successfully evaluated inference endpoint/);
    assert.throws(() => readProductionConfiguration({ ...environment, GITHUB_SHA: "b".repeat(40) }));
    assert.throws(() => readProductionConfiguration({ ...environment, EVALUATED_REASONING_EFFORT: "" }));
    await fixture(async (directory) => {
        const path = join(directory, "settings.json");
        const result = invoke("production-settings", [path], { TARGET_SUBSCRIPTION: "", SLOT_NAME: "" });
        assert.equal(result.status, 0, result.stderr);
        const settings = JSON.parse(await readFile(path, "utf8"));
        assert.deepEqual(settings.map(({ name, value }) => [name, value]), [
            ["AzureOpenAI__Endpoint", environment.AOAI_ENDPOINT],
            ["AzureOpenAI__DeploymentName", environment.EVALUATED_MODEL],
            ["AzureOpenAI__ReasoningEffort", environment.EVALUATED_REASONING_EFFORT],
        ]);
        const effective = join(directory, "effective.json");
        await writeFile(effective, JSON.stringify(settings.map(({ name, value }) => ({ name, value }))));
        assert.equal(invoke("check-production-settings", [effective]).status, 0);
        await writeFile(effective, JSON.stringify(settings.map(({ name, value }) =>
            ({ name, value: name === "AzureOpenAI__DeploymentName" ? "stale-model" : value }))));
        const stale = invoke("check-production-settings", [effective]);
        assert.equal(stale.status, 1);
        assert.doesNotMatch(stale.stderr, /synthetic-model|stale-model/);
    });
    const workflow = await readFile(new URL("../../.github/workflows/main.yml", import.meta.url), "utf8");
    assert.ok(workflow.indexOf("feature-slot.mjs check-production-config") < workflow.indexOf("uses: azure/login"));
    assert.ok(workflow.indexOf("feature-slot.mjs production-settings") < workflow.indexOf("feature-slot.mjs check-production-settings"));
    assert.ok(workflow.indexOf("feature-slot.mjs check-production-settings") < workflow.indexOf("az webapp config container set"));
});
