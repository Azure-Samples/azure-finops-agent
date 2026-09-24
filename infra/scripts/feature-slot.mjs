import { createHash } from "node:crypto";
import { appendFile, readFile, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

class ValidationError extends Error {}

function text(value, label) {
    if (typeof value !== "string" || !value || value !== value.trim() ||
        /[\u0000-\u001f\u007f]/.test(value))
        throw new ValidationError(`Missing or invalid ${label}.`);
    return value;
}

function hostname(value) {
    text(value, "Azure hostname");
    if (value.length > 253 || !/^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$/i.test(value))
        throw new ValidationError("Invalid Azure hostname.");
    return value.toLowerCase();
}

function previewOrigin(value) {
    text(value, "verification URL");
    if (!/^https:\/\/[a-z0-9.-]+\/?$/i.test(value))
        throw new ValidationError("Verification URLs must be HTTPS origins without credentials, ports, paths, queries or fragments.");
    return `https://${hostname(new URL(value).hostname)}`;
}

export function endpointFingerprint(endpoint) {
    text(endpoint, "Azure OpenAI endpoint");
    let endpointUrl;
    try {
        endpointUrl = new URL(endpoint);
    } catch {
        throw new ValidationError("Invalid Azure OpenAI endpoint.");
    }
    if (endpointUrl.protocol !== "https:" || endpointUrl.username || endpointUrl.password ||
        /[?#]/.test(endpoint) || !/^https:\/\/[^\\\s]+$/i.test(endpoint))
        throw new ValidationError("Azure OpenAI requires an HTTPS endpoint without credentials, queries or fragments.");
    const normalized = endpointUrl.href.replace(/\/+$/, "");
    return createHash("sha256").update(normalized, "utf8").digest("hex");
}

export function validateModelContract({ model, reasoningEffort, sha }) {
    if (!/^[a-z0-9][a-z0-9._-]{0,63}$/i.test(text(model, "evaluated deployment")))
        throw new ValidationError("Invalid evaluated deployment name.");
    if (!["low", "medium", "high", "xhigh"].includes(reasoningEffort))
        throw new ValidationError("Missing or unsupported evaluated reasoning effort.");
    if (!/^[a-f0-9]{40}$/.test(text(sha, "evaluated commit SHA")))
        throw new ValidationError("The evaluated commit must be a full SHA.");
    return { model, reasoningEffort, sha };
}

export function readFeatureConfiguration(environment) {
    const subscription = text(environment.TARGET_SUBSCRIPTION, "test subscription");
    const resourceGroup = text(environment.RESOURCE_GROUP, "test resource group");
    const webAppName = text(environment.WEBAPP_NAME, "test web app");
    const slotName = text(environment.SLOT_NAME, "test slot");
    if (!/^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i.test(subscription))
        throw new ValidationError("The test subscription must be an explicit subscription ID.");
    if (!/^[\p{L}\p{N}_().-]{1,90}$/u.test(resourceGroup) || resourceGroup.endsWith("."))
        throw new ValidationError("Invalid test resource group name.");
    for (const name of [webAppName, slotName])
        if (!/^[a-z0-9][a-z0-9-]{0,59}$/i.test(name))
            throw new ValidationError("Invalid test web app or slot name.");
    if (slotName.toLowerCase() === "production")
        throw new ValidationError("The production slot cannot be a feature target.");

    const contract = validateModelContract({
        model: environment.EVALUATED_MODEL,
        reasoningEffort: environment.EVALUATED_REASONING_EFFORT,
        sha: environment.EVALUATED_SHA,
    });
    if (contract.sha !== environment.GITHUB_SHA)
        throw new ValidationError("The successfully evaluated commit does not match this deployment.");

    const endpoint = environment.AOAI_ENDPOINT;
    const evaluatedFingerprint = text(environment.EVALUATED_ENDPOINT_SHA256, "evaluated endpoint fingerprint");
    if (!/^[a-f0-9]{64}$/.test(evaluatedFingerprint))
        throw new ValidationError("The evaluated endpoint fingerprint must be a lowercase SHA-256 digest.");
    if (endpointFingerprint(endpoint) !== evaluatedFingerprint)
        throw new ValidationError("Deployment endpoint does not match the successfully evaluated inference endpoint. Deployment is blocked.");

    const verifyUrl = previewOrigin(environment.VERIFY_URL);
    const productionUrl = environment.PRODUCTION_VERIFY_URL
        ? previewOrigin(environment.PRODUCTION_VERIFY_URL) : undefined;
    if (verifyUrl === productionUrl)
        throw new ValidationError("The production URL cannot be a feature verification target.");
    return { subscription, resourceGroup, webAppName, slotName, endpoint, verifyUrl, productionUrl, ...contract };
}

export function readProductionConfiguration(environment) {
    const contract = validateModelContract({
        model: environment.EVALUATED_MODEL,
        reasoningEffort: environment.EVALUATED_REASONING_EFFORT,
        sha: environment.EVALUATED_SHA,
    });
    if (contract.sha !== environment.GITHUB_SHA)
        throw new ValidationError("The successfully evaluated commit does not match this deployment.");
    const endpoint = environment.AOAI_ENDPOINT;
    const evaluatedFingerprint = text(environment.EVALUATED_ENDPOINT_SHA256, "evaluated endpoint fingerprint");
    if (!/^[a-f0-9]{64}$/.test(evaluatedFingerprint))
        throw new ValidationError("The evaluated endpoint fingerprint must be a lowercase SHA-256 digest.");
    if (endpointFingerprint(endpoint) !== evaluatedFingerprint)
        throw new ValidationError("Deployment endpoint does not match the successfully evaluated inference endpoint. Deployment is blocked.");
    return { endpoint, ...contract };
}

function resourceHostname(resource, id, type, names) {
    if (!resource || typeof resource.id !== "string" ||
        resource.id.toLowerCase() !== id.toLowerCase() ||
        typeof resource.type !== "string" || resource.type.toLowerCase() !== type.toLowerCase() ||
        typeof resource.name !== "string" ||
        !names.some((name) => resource.name.toLowerCase() === name.toLowerCase()))
        throw new ValidationError("Azure resource metadata does not match the explicitly configured feature target.");
    return hostname(resource.defaultHostName);
}

export function validateFeatureTarget(config, parent, slot) {
    const parentId = `/subscriptions/${config.subscription}/resourceGroups/${config.resourceGroup}/providers/Microsoft.Web/sites/${config.webAppName}`;
    const parentHost = resourceHostname(parent, parentId, "Microsoft.Web/sites", [config.webAppName]);
    const slotHost = resourceHostname(slot, `${parentId}/slots/${config.slotName}`,
        "Microsoft.Web/sites/slots", [config.slotName, `${config.webAppName}/${config.slotName}`]);
    const slotUrl = `https://${slotHost}`;
    if (slotHost === parentHost || slotUrl === config.productionUrl || slotUrl !== config.verifyUrl)
        throw new ValidationError("The verification URL must match Azure's nonproduction slot hostname, not production.");
    return slotUrl;
}

// `az webapp config appsettings set` merges these keys into the existing dictionary,
// so only App Service configuration rights (Website Contributor) are required.
export function modelSettings(config) {
    return [
        { name: "AzureOpenAI__Endpoint", value: config.endpoint, slotSetting: false },
        { name: "AzureOpenAI__DeploymentName", value: config.model, slotSetting: false },
        { name: "AzureOpenAI__ReasoningEffort", value: config.reasoningEffort, slotSetting: false },
    ];
}

export function verifyModelSettings(settings, config) {
    const expected = {
        AzureOpenAI__Endpoint: config.endpoint,
        AzureOpenAI__DeploymentName: config.model,
        AzureOpenAI__ReasoningEffort: config.reasoningEffort,
    };
    if (!Array.isArray(settings) || settings.length !== Object.keys(expected).length ||
        Object.entries(expected).some(([name, value]) =>
            settings.filter((setting) => setting?.name === name && setting.value === value).length !== 1))
        throw new ValidationError("The three effective Azure OpenAI settings do not match the evaluated deployment contract.");
}

export function verifyVersion(version, { sha, build, branch }) {
    if (!/^[a-f0-9]{40}$/.test(text(sha, "expected commit SHA")) ||
        !/^[1-9][0-9]*$/.test(text(build, "expected build number")))
        throw new ValidationError("Invalid expected build metadata.");
    text(branch, "expected branch");
    if (!version || version.sha !== sha || version.build !== build || version.branch !== branch)
        throw new ValidationError("The expected full SHA/build/branch is not active on the feature slot.");
}

async function readJson(path) {
    try {
        return JSON.parse(await readFile(path, "utf8"));
    } catch {
        throw new ValidationError("Unable to read a valid workflow metadata document.");
    }
}

async function main() {
    const [command, ...paths] = process.argv.slice(2);
    const environment = process.env;
    if (command === "evaluation-output" && paths.length === 0) {
        const { model, reasoningEffort, sha } = validateModelContract({
            model: environment.EVAL_MODEL,
            reasoningEffort: environment.EVAL_REASONING_EFFORT,
            sha: environment.GITHUB_SHA,
        });
        const fingerprint = endpointFingerprint(environment.EVAL_MODEL_ENDPOINT);
        await appendFile(environment.GITHUB_OUTPUT,
            `model=${model}\nreasoning_effort=${reasoningEffort}\nsha=${sha}\nendpoint_sha256=${fingerprint}\n`);
    } else if (command === "check-version" && paths.length === 1) {
        verifyVersion(await readJson(paths[0]), {
            sha: environment.EXPECTED_SHA,
            build: environment.EXPECTED_BUILD,
            branch: environment.EXPECTED_BRANCH,
        });
    } else if (command === "check-production-config" && paths.length === 0) {
        readProductionConfiguration(environment);
    } else if (command === "production-settings" && paths.length === 1) {
        const config = readProductionConfiguration(environment);
        await writeFile(paths[0], JSON.stringify(modelSettings(config)), { mode: 0o600, flag: "wx" });
    } else if (command === "check-production-settings" && paths.length === 1) {
        verifyModelSettings(await readJson(paths[0]), readProductionConfiguration(environment));
    } else {
        const config = readFeatureConfiguration(environment);
        if (command === "check-config" && paths.length === 0) return;
        if (command === "check-target" && paths.length === 2) {
            const slotUrl = validateFeatureTarget(config, await readJson(paths[0]), await readJson(paths[1]));
            await appendFile(environment.GITHUB_OUTPUT, `slot_url=${slotUrl}\n`);
        } else if (command === "settings" && paths.length === 1) {
            await writeFile(paths[0], JSON.stringify(modelSettings(config)), { mode: 0o600, flag: "wx" });
        } else if (command === "check-settings" && paths.length === 1) {
            verifyModelSettings(await readJson(paths[0]), config);
        } else {
            throw new ValidationError("Unsupported feature-slot validation command.");
        }
    }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url))
    main().catch((error) => {
        // CLI and JSON errors can contain private metadata; do not print their payloads.
        const message = error instanceof ValidationError ? error.message
            : "Unable to process feature-slot workflow metadata. Check file access and configuration.";
        console.error(`::error::${message}`);
        process.exitCode = 1;
    });
