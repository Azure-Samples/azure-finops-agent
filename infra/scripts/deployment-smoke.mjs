// Post-deployment gate: one real anonymous chat turn through the deployed app, so
// the model call runs under the app's own managed identity and settings. The live
// evaluations run in-process with the evaluation identity and cannot detect a
// deployed identity that lacks model access. Also captures and restores the prior
// image and model settings so a failed deployment does not stay live.
import { randomInt } from "node:crypto";
import { appendFile, readFile, rm, writeFile } from "node:fs/promises";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

export class SmokeError extends Error {}

export const MODEL_SETTING_NAMES = ["AzureOpenAI__Endpoint", "AzureOpenAI__DeploymentName", "AzureOpenAI__ReasoningEffort"];

const PERMISSION_HINT = " The deployed app's managed identity (not the evaluation identity) needs Foundry User on the Foundry account.";

export function httpsOrigin(value) {
    if (typeof value !== "string" || value !== value.trim() || !/^https:\/\/[a-z0-9.-]+\/?$/i.test(value))
        throw new SmokeError("The smoke-test URL must be an HTTPS origin without credentials, ports, paths, queries or fragments.");
    const host = new URL(value).hostname.toLowerCase();
    if (host.length > 253 || !/^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$/.test(host))
        throw new SmokeError("Invalid smoke-test hostname.");
    return `https://${host}`;
}

// Service errors can carry principal ids, resource ids, hosts and URLs; publish only their shape.
export function redact(value) {
    return String(value ?? "")
        .replace(/[\u0000-\u001f\u007f]+/g, " ")
        .replace(/\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+/g, "<token>")
        .replace(/\b[a-z][a-z0-9+.-]*:\/\/[^\s"'<>)]+/gi, "<url>")
        .replace(/\/subscriptions\/[^\s"'<>)]+/gi, "<resource-id>")
        .replace(/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/gi, "<id>")
        .replace(/[a-z0-9._%+-]+@[a-z0-9.-]+\.[a-z]{2,}/gi, "<email>")
        .replace(/\b\d{1,3}(?:\.\d{1,3}){3}\b/g, "<ip>")
        .replace(/\b(?:[a-z0-9-]+\.)+(?:com|net|io|ms|org)\b/gi, "<host>")
        .replace(/\s+/g, " ")
        .trim()
        .slice(0, 300);
}

export function smokePrompt(code) {
    return `Deployment health check. Do not call any tools. Reply with only this verification code: ${code}`;
}

export function applyEvent(state, data) {
    if (data === "[DONE]") {
        state.done = true;
        return;
    }
    let event;
    try {
        event = JSON.parse(data);
    } catch {
        return;
    }
    if (event?.type === "delta" && typeof event.content === "string") state.deltas += event.content;
    else if (event?.type === "message" && typeof event.content === "string") state.messages.push(event.content);
    else if (event?.type === "error") state.errors.push({ code: event.code, message: event.message });
    else if (event?.type === "busy") state.errors.push({ code: "busy", message: event.message });
}

export async function readEvents(body) {
    const state = { done: false, deltas: "", messages: [], errors: [] };
    const decoder = new TextDecoder();
    let buffer = "";
    for await (const chunk of body) {
        buffer += decoder.decode(chunk, { stream: true });
        let boundary;
        while ((boundary = buffer.indexOf("\n\n")) >= 0) {
            const block = buffer.slice(0, boundary);
            buffer = buffer.slice(boundary + 2);
            const data = block.split("\n").map((line) => line.replace(/\r$/, ""))
                .filter((line) => line.startsWith("data:")).map((line) => line.slice(5).replace(/^ /, "")).join("\n");
            if (data) applyEvent(state, data);
            if (state.done) return state;
        }
    }
    return state;
}

export function assessTurn(state, code) {
    if (state.errors.length > 0) {
        const { code: errorCode, message } = state.errors[0];
        const detail = redact(message).replace(/\s*\{.*$/, "").replace(/[.\s]+$/, "") || "no detail";
        const label = typeof errorCode === "string" && /^[a-z0-9_.-]{1,64}$/i.test(errorCode) ? ` '${errorCode}'` : "";
        const hint = /\b40[13]\b|permission|forbidden|unauthori[sz]ed|AuthorizationFailed/i.test(String(message)) ? PERMISSION_HINT : "";
        throw new SmokeError(`The deployed chat returned error${label}: ${detail}.${hint}`);
    }
    if (!state.done) throw new SmokeError("The chat stream ended before the turn completed.");
    const answer = [state.deltas, ...state.messages].join("\n");
    if (!answer.trim()) throw new SmokeError("The deployed chat completed without an answer.");
    if (!answer.toUpperCase().includes(code.toUpperCase()))
        throw new SmokeError("The deployed chat answered without the requested verification code.");
}

function sessionCookies(response) {
    const headers = typeof response.headers.getSetCookie === "function" ? response.headers.getSetCookie() : [];
    return headers.map((header) => header.split(";", 1)[0].trim()).filter((pair) => /^[^=\s;]+=[^;]*$/.test(pair));
}

export async function chatOnce(origin, { fetchImpl = globalThis.fetch, timeoutMs = 180_000, code }) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);
    try {
        // Any request assigns an anonymous chat identity and issues the session cookie.
        const bootstrap = await fetchImpl(`${origin}/api/version`,
            { redirect: "error", signal: controller.signal, headers: { accept: "application/json" } });
        await bootstrap.arrayBuffer();
        if (!bootstrap.ok) throw new SmokeError(`The deployment returned HTTP ${bootstrap.status} for the version probe.`);
        const cookies = sessionCookies(bootstrap);
        if (cookies.length === 0) throw new SmokeError("The deployment did not issue an anonymous chat session cookie.");

        const response = await fetchImpl(`${origin}/api/chat`, {
            method: "POST",
            redirect: "error",
            signal: controller.signal,
            headers: {
                accept: "text/event-stream",
                "content-type": "application/json",
                origin,
                cookie: cookies.join("; "),
            },
            body: JSON.stringify({ prompt: smokePrompt(code) }),
        });
        if (response.status !== 200) {
            await response.arrayBuffer().catch(() => undefined);
            throw new SmokeError(`The chat endpoint returned HTTP ${response.status}.`);
        }
        if (!/^text\/event-stream\b/i.test(response.headers.get("content-type") ?? "") || !response.body)
            throw new SmokeError("The chat endpoint did not return an event stream.");
        assessTurn(await readEvents(response.body), code);
    } catch (error) {
        if (error instanceof SmokeError) throw error;
        if (controller.signal.aborted) throw new SmokeError(`No complete chat answer within ${Math.round(timeoutMs / 1000)} s.`);
        const reason = redact(error?.cause?.code ?? error?.name ?? "") || "network error";
        throw new SmokeError(`The deployment could not be reached (${reason}).`);
    } finally {
        clearTimeout(timer);
    }
}

export async function smokeChat(url, {
    fetchImpl = globalThis.fetch,
    attempts = 3,
    timeoutMs = 180_000,
    delayMs = 15_000,
    sleep = (ms) => new Promise((done) => setTimeout(done, ms)),
    log = (line) => console.log(line),
    nextCode = () => `DEPLOY-CHECK-${randomInt(10_000, 100_000)}`,
} = {}) {
    const origin = httpsOrigin(url);
    let failure;
    for (let attempt = 1; attempt <= attempts; attempt++) {
        const started = Date.now();
        try {
            await chatOnce(origin, { fetchImpl, timeoutMs, code: nextCode() });
            return { attempt, seconds: (Date.now() - started) / 1000 };
        } catch (error) {
            failure = error instanceof SmokeError ? error : new SmokeError("Unexpected smoke-test failure.");
            if (attempt < attempts) {
                log(`::warning::Deployed chat attempt ${attempt}/${attempts} failed: ${failure.message}`);
                await sleep(delayMs * attempt);
            }
        }
    }
    throw failure;
}

// `az webapp config show --query linuxFxVersion` for a single-container app: DOCKER|registry/repository[:tag|@digest].
export function rollbackImage(linuxFxVersion) {
    if (typeof linuxFxVersion !== "string") return null;
    const match = /^DOCKER\|((?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}(?::[0-9]{1,5})?)\/([a-z0-9]+(?:(?:[._-]|__|\/)[a-z0-9]+)*)(:[A-Za-z0-9_][A-Za-z0-9._-]{0,127}|@sha256:[a-f0-9]{64})?$/.exec(linuxFxVersion);
    if (!match) return null;
    return { image: `${match[1]}/${match[2]}${match[3] ?? ""}`, registry: match[1] };
}

// Restores only the three model keys the deployment overwrites; others are never touched.
export function restoreSettings(settings) {
    if (!Array.isArray(settings)) throw new SmokeError("Unable to read the current model settings.");
    const restored = [];
    for (const name of MODEL_SETTING_NAMES) {
        const matches = settings.filter((setting) => setting?.name === name);
        if (matches.length > 1) throw new SmokeError("The current model settings are ambiguous.");
        if (matches.length === 1) {
            if (typeof matches[0].value !== "string") throw new SmokeError("Unable to read the current model settings.");
            restored.push({ name, value: matches[0].value, slotSetting: matches[0].slotSetting === true });
        }
    }
    return restored;
}

async function readJson(path) {
    try {
        return JSON.parse(await readFile(path, "utf8"));
    } catch {
        throw new SmokeError("Unable to read a valid workflow metadata document.");
    }
}

export async function captureRollback(directory, output, { log = console.log } = {}) {
    const target = rollbackImage(await readJson(join(directory, "runtime.json")));
    const settings = restoreSettings(await readJson(join(directory, "current-settings.json")));
    await rm(join(directory, "current-settings.json"), { force: true });
    await rm(join(directory, "runtime.json"), { force: true });
    if (!target) {
        log("::warning::Automatic rollback is unavailable: the target is not running a single registry container image.");
        await appendFile(output, "captured=false\n");
        return false;
    }
    const options = { mode: 0o600, flag: "wx" };
    await writeFile(join(directory, "image"), target.image, options);
    await writeFile(join(directory, "registry"), target.registry, options);
    if (settings.length > 0) await writeFile(join(directory, "restore-settings.json"), JSON.stringify(settings), options);
    if (settings.length < MODEL_SETTING_NAMES.length)
        log("::warning::Rollback restores only model settings that existed before this deployment.");
    await appendFile(output, "captured=true\n");
    return true;
}

async function main() {
    const [command, ...paths] = process.argv.slice(2);
    if (command === "chat" && paths.length === 0) {
        try {
            const { attempt, seconds } = await smokeChat(process.env.SMOKE_URL);
            console.log(`Deployed chat answered through the app identity in ${seconds.toFixed(1)} s (attempt ${attempt}).`);
        } catch (error) {
            if (error instanceof SmokeError) throw new SmokeError(`Deployed chat verification failed: ${error.message}`);
            throw error;
        }
    } else if (command === "check-url" && paths.length === 0) {
        httpsOrigin(process.env.SMOKE_URL);
    } else if (command === "capture" && paths.length === 1) {
        await captureRollback(paths[0], process.env.GITHUB_OUTPUT);
    } else {
        throw new SmokeError("Unsupported deployment smoke command.");
    }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url))
    main().catch((error) => {
        const message = error instanceof SmokeError ? error.message
            : "Unable to complete the deployment smoke check. Check file access and configuration.";
        console.error(`::error::${message}`);
        process.exitCode = 1;
    });
