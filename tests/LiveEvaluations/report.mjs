// Presentation of live-evaluation results: a red/green scoreboard for the job summary, a standalone HTML dashboard,
// colored log lines and GitHub annotations. Pure functions: nothing here reads the environment or the file system,
// and every value that came from a model, a question or a tool is escaped before it is written.

export const INEFFICIENT_FAILURE = "Structured judge rated the session inefficient.";

// The audit the "most asked" markers come from: 95 real conversations of the last 30 days, October 2026.
export const AUDIT = Object.freeze({ conversations: 95, month: "October 2026" });

// Groups in display order. `share` is the approximate share of audited conversations that touched the theme.
export const GROUP_META = Object.freeze({
    "start-page": { label: "Start-page cards (most asked)", icon: "🔥", order: 1 },
    "ai-spend": { label: "AI and Foundry spend", icon: "🤖", order: 2, share: 43 },
    resources: { label: "Resources and network", icon: "🖧", order: 3, share: 40 },
    docs: { label: "How-to and concepts", icon: "📚", order: 4, share: 37 },
    pricing: { label: "Price estimates", icon: "💲", order: 5, share: 32 },
    security: { label: "Security and access", icon: "🔐", order: 6, share: 27 },
    cost: { label: "Cost analysis", icon: "📊", order: 7, share: 25 },
    reports: { label: "Reports and decks", icon: "📑", order: 8, share: 15 },
    scripts: { label: "Scripts and infrastructure as code", icon: "🧰", order: 9, share: 10 },
    safety: { label: "Safety and guardrails", icon: "🛡️", order: 10 },
    multilingual: { label: "Other languages", icon: "🌍", order: 11, share: 15 },
    crawl: { label: "FinOps Crawl", icon: "1️⃣", order: 20 },
    walk: { label: "FinOps Walk", icon: "2️⃣", order: 21 },
    run: { label: "FinOps Run", icon: "3️⃣", order: 22 },
    playbook: { label: "Playbook", icon: "📘", order: 23 },
    enterprise: { label: "Enterprise agreements", icon: "🏢", order: 24 },
    jobs: { label: "Scheduled-job templates", icon: "⏰", order: 25 },
    incidents: { label: "Regression incidents", icon: "🩹", order: 26 },
    other: { label: "Other", icon: "▫️", order: 99 },
});

export const STATUS = Object.freeze({
    pass: { word: "PASS", dot: "🟢", square: "🟩", ansi: "1;30;42" },
    fail: { word: "FAIL", dot: "🔴", square: "🟥", ansi: "1;97;41" },
    known: { word: "KNOWN LIMIT", dot: "🟠", square: "🟧", ansi: "1;30;43" },
    missing: { word: "NOT RUN", dot: "⚪", square: "⬜", ansi: "1;30;47" },
});

export const escapeHtml = (value) =>
    String(value ?? "")
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;")
        .replaceAll('"', "&quot;")
        .replaceAll("'", "&#39;");
const cell = (value) =>
    escapeHtml(value).replaceAll("|", "&#124;").replaceAll("\r", "").replaceAll("\n", " ");

export function groupOf(scenario) {
    if (scenario?.group && GROUP_META[scenario.group]) return scenario.group;
    const origin = scenario?.origins?.[0] ?? "";
    if (origin.startsWith("pricing:")) return "start-page";
    if (origin.startsWith("sidebar:") && GROUP_META[origin.slice("sidebar:".length)])
        return origin.slice("sidebar:".length);
    if (origin.startsWith("job-template")) return "jobs";
    if (origin.startsWith("incident:") || origin.startsWith("evaluation:")) return "incidents";
    return "other";
}

// A scenario that carries a documented limitation shows amber, not red, when it fails only for that reason.
export function statusOf(scenario, row) {
    if (!row) return "missing";
    if (!row.failures.length) return "pass";
    if (scenario?.knownIssue && row.failures.every((failure) => failure === INEFFICIENT_FAILURE))
        return "known";
    return "fail";
}

const percentile = (values, fraction) => {
    if (!values.length) return null;
    const sorted = [...values].sort((left, right) => left - right);
    return sorted[Math.min(sorted.length - 1, Math.max(0, Math.ceil(fraction * sorted.length) - 1))];
};
const seconds = (milliseconds) =>
    Number.isFinite(milliseconds) ? `${(milliseconds / 1000).toFixed(1)} s` : "-";
const stars = (score) =>
    Number.isInteger(score) && score >= 1 && score <= 5 ? "★".repeat(score) + "☆".repeat(5 - score) : "-";
const toolsLabel = (count) =>
    Number.isFinite(count) ? `${count} tool${count === 1 ? "" : "s"}` : "? tools";
const percent = (fraction) => `${Math.round(fraction * 100)}%`;

export function scoreboard(cases, results) {
    const byId = new Map(results.map((row) => [row.id, row]));
    const items = cases.map((scenario) => {
        const row = byId.get(scenario.id);
        return { scenario, row, status: statusOf(scenario, row), group: groupOf(scenario) };
    });
    const count = (status) => items.filter((item) => item.status === status).length;
    const finite = (pick) => items.map(pick).filter(Number.isFinite);
    const durations = finite((item) => item.row?.result?.durationMs);
    const efficiency = items
        .map((item) => item.row?.result?.judge?.efficiencyScore)
        .filter(Number.isInteger);
    const passed = count("pass");
    const failed = count("fail");
    const scored = passed + failed;
    const groups = new Map();
    for (const item of items) {
        if (!groups.has(item.group)) groups.set(item.group, { key: item.group, ...GROUP_META[item.group], items: [] });
        groups.get(item.group).items.push(item);
    }
    return {
        items,
        total: items.length,
        passed,
        failed,
        known: count("known"),
        missing: count("missing"),
        scored,
        passRate: scored > 0 ? passed / scored : 0,
        medianMs: percentile(durations, 0.5),
        p95Ms: percentile(durations, 0.95),
        slowestMs: durations.length ? Math.max(...durations) : null,
        totalMs: durations.reduce((sum, value) => sum + value, 0),
        toolCalls: finite((item) => item.row?.result?.toolCount).reduce((sum, value) => sum + value, 0),
        efficiency: efficiency.length
            ? efficiency.reduce((sum, value) => sum + value, 0) / efficiency.length
            : null,
        firstTokenMs: percentile(finite((item) => item.row?.result?.firstTokenMs), 0.5),
        groups: [...groups.values()].sort((left, right) => left.order - right.order),
    };
}

const squares = (items) => items.map((item) => STATUS[item.status].square).join("");
const groupTitle = (group) => `${group.icon} ${group.label}`;
const groupCount = (group) => {
    const passed = group.items.filter((item) => item.status === "pass").length;
    return `${passed}/${group.items.length}`;
};
const groupState = (group) =>
    group.items.some((item) => item.status === "fail") ? "fail"
        : group.items.some((item) => item.status === "known") ? "known"
            : group.items.some((item) => item.status === "missing") ? "missing" : "pass";
const reasonOf = (item) =>
    item.row ? item.row.failures.join(" ") || "Accepted" : "No result: the run ended before this scenario.";
// The hard-check failures plus the judge's reasons in plain words, for tables and annotations.
const whyOf = (item) => {
    if (!item.row) return reasonOf(item);
    const findings = (item.row.result?.judge?.findings ?? []).filter((code) => FINDINGS[code]).map((code) => FINDINGS[code].text);
    const checks = item.row.failures.filter((failure) => !/^Structured judge (rejected|rated)/.test(failure));
    return [...findings, ...checks].join(". ").replace(/\.\./g, ".") || reasonOf(item);
};
const judgeBadge = (judge) =>
    judge ? `${judge.accepted && judge.grounded && judge.complete ? "✅" : "❌"} ${stars(judge.efficiencyScore)}` : "–";
const popularity = (scenario) =>
    Number.isInteger(scenario?.askedBy) && scenario.askedBy > 0
        ? `Asked in ${scenario.askedBy} of the ${AUDIT.conversations} conversations audited in ${AUDIT.month}`
        : "";

// The judge's closed vocabulary for why a verdict is not a clean pass (JudgeClient.FindingCodes, kept in step by a
// test). Codes carry no tenant data, so they are published even when the rationale and answer are withheld.
export const FINDINGS = Object.freeze({
    wrong_figure: { kind: "quality", text: "A figure, price or calculation did not match the evidence" },
    unsupported_claim: { kind: "quality", text: "A material claim had no supporting evidence" },
    invented_data: { kind: "quality", text: "It presented data that no tool returned" },
    missing_result: { kind: "quality", text: "A core part of the request was not answered" },
    missing_output: { kind: "quality", text: "A requested chart, file, script or deck was not produced" },
    case_expectation_unmet: { kind: "quality", text: "The scenario's stated expectation was not met" },
    wrong_scope: { kind: "quality", text: "It used the wrong subscription, period, region or filter" },
    failed_evidence: { kind: "quality", text: "The answer relied on a failed, denied or missing read" },
    unknown_as_zero: { kind: "quality", text: "An unknown value was reported as zero or absent" },
    wrong_language: { kind: "quality", text: "It answered in a different language" },
    needless_clarification: { kind: "quality", text: "It asked for input it did not need instead of answering" },
    refused_possible_task: { kind: "quality", text: "It said it could not do something its tools can" },
    unsafe_action: { kind: "quality", text: "It claimed or attempted a change it must not make" },
    redundant_calls: { kind: "efficiency", text: "It repeated calls whose results it already had" },
    sequential_rounds: { kind: "efficiency", text: "Independent reads ran one after another instead of together" },
    extra_lookups: { kind: "efficiency", text: "It looked up documentation more than needed" },
    unfiltered_paging: { kind: "efficiency", text: "It paged raw data the API could filter or aggregate" },
    unrelated_reads: { kind: "efficiency", text: "It read data unrelated to the question" },
    uncorrected_failures: { kind: "efficiency", text: "Failed calls were not corrected" },
});

const MIN_EFFICIENCY = 3;
const yesNo = (value) => (value === true ? "✅" : value === false ? "❌" : "–");

// Everything a reader needs to understand one scenario's verdict, built from published fields only: what the scenario
// checks (our own text), what the agent did (tools and timing), what the judge scored and why (flags, waste counts and
// finding codes; the free-text rationale only when answers are published), and each hard check.
export function explain(scenario, row, publishAnswers = false) {
    const result = row?.result ?? null;
    const failures = row?.failures ?? [];
    const rubric = String(scenario?.rubric ?? "");
    const marker = rubric.indexOf("Case expectation:");
    const tools = Array.isArray(result?.tools) ? [...result.tools] : [];
    const ordered = tools.every((tool) => Number.isFinite(tool.startMs))
        ? tools.sort((left, right) => left.startMs - right.startMs) : tools;
    const judge = result?.judge ?? null;
    const findings = (judge?.findings ?? []).filter((code) => FINDINGS[code]);
    const has = (text) => failures.some((failure) => failure.includes(text));
    const ran = Boolean(result);
    const checks = [
        { label: "The run completed", ok: ran ? result.terminal === true && !has("Evaluation process failed") : null },
        { label: "The transcript replayed the question and answer", ok: ran ? result.transcriptVerified === true : null },
        { label: "An answer was produced", ok: ran ? !has("Final answer is empty") : null },
        ...(scenario?.requiredTools ?? []).map((name) => ({
            label: `Called ${name}`, ok: ran ? !has(`Required tool missing: ${name}.`) : null,
        })),
        ...(scenario?.forbiddenTools ?? []).map((name) => ({
            label: `Never called ${name}`, ok: ran ? !has(`Forbidden tool called: ${name}.`) : null,
        })),
        { label: "Every failed tool call was corrected", ok: ran ? !failures.some((failure) => /tool calls failed|never corrected/.test(failure)) : null },
        { label: `At most ${scenario?.maxToolCalls ?? "?"} tool calls`, ok: ran ? !has("Tool count is invalid or over budget") : null },
        { label: `Finished within ${scenario?.maxDurationSeconds ?? "?"} s`, ok: ran ? !has("over-budget run duration") : null },
        { label: "No run errors", ok: ran ? !has("Run contains errors") : null },
        { label: "The judge accepted the answer (accepted, grounded and complete)", ok: ran ? judge ? judge.accepted && judge.grounded && judge.complete : false : null },
        { label: `Efficiency at least ${MIN_EFFICIENCY} of 5`, ok: ran ? judge ? judge.efficient && judge.efficiencyScore >= MIN_EFFICIENCY : false : null },
    ];
    return {
        question: scenario?.question ?? "",
        expectation: marker >= 0 ? rubric.slice(marker + "Case expectation:".length).trim().replace(/^[a-z]/, (letter) => letter.toUpperCase()) : "",
        knownIssue: scenario?.knownIssue ?? "",
        requiredTools: scenario?.requiredTools ?? [],
        forbiddenTools: scenario?.forbiddenTools ?? [],
        maxToolCalls: scenario?.maxToolCalls ?? null,
        maxDurationSeconds: scenario?.maxDurationSeconds ?? null,
        ran,
        durationMs: result?.durationMs ?? null,
        firstTokenMs: result?.firstTokenMs ?? null,
        rounds: result?.timeline?.rounds ?? null,
        maxConcurrent: result?.timeline?.maxConcurrentTools ?? null,
        toolWallMs: result?.timeline?.toolWallMs ?? null,
        modelMs: result?.timeline?.modelMs ?? null,
        throttleNotices: result?.throttle?.notices ?? 0,
        attempts: result?.attempts ?? 1,
        calls: ordered.map((tool) => ({ name: tool.name, ok: tool.success === true, startMs: tool.startMs ?? null, durationMs: tool.durationMs ?? null })),
        judge: judge ? {
            accepted: judge.accepted, grounded: judge.grounded, complete: judge.complete,
            efficiencyScore: judge.efficiencyScore, efficient: judge.efficient,
            avoidableCalls: judge.avoidableCalls, avoidableRounds: judge.avoidableRounds, avoidableSeconds: judge.avoidableSeconds,
            findings: findings.map((code) => ({ code, ...FINDINGS[code] })),
            reason: publishAnswers && typeof judge.reason === "string" ? judge.reason : null,
        } : null,
        answer: publishAnswers && typeof result?.answer === "string" ? result.answer : null,
        answerWithheld: !publishAnswers && ran,
        checks,
        failures,
    };
}

const plural = (count, word) => `${count} ${word}${count === 1 ? "" : "s"}`;

export function happenedSentence(info) {
    if (!info.ran) return "It did not run: the suite ended before this scenario.";
    const parts = [`Answered in ${seconds(info.durationMs)}`];
    if (Number.isFinite(info.firstTokenMs)) parts[0] += ` (first text after ${seconds(info.firstTokenMs)})`;
    const calls = info.calls.length;
    const failed = info.calls.filter((call) => !call.ok).length;
    parts.push(calls
        ? `${plural(calls, "tool call")}${Number.isInteger(info.rounds) ? ` in ${plural(info.rounds, "round")}` : ""}${Number.isInteger(info.maxConcurrent) && info.maxConcurrent > 1 ? `, up to ${info.maxConcurrent} at once` : ""}${failed ? `, ${failed} failed` : ""}`
        : "no tool calls");
    if (Number.isFinite(info.toolWallMs) && Number.isFinite(info.modelMs))
        parts.push(`tools ran ${seconds(info.toolWallMs)}, the model ${seconds(info.modelMs)}`);
    if (info.throttleNotices) parts.push(`${plural(info.throttleNotices, "service throttle notice")}`);
    if (info.attempts > 1) parts.push(`run ${info.attempts} times after a service throttle`);
    return parts.join("; ") + ".";
}

export function judgeSentence(info) {
    if (!info.ran) return "Not judged.";
    const judge = info.judge;
    if (!judge) return "No valid judge verdict was returned, which fails the scenario.";
    const verdict = judge.accepted && judge.grounded && judge.complete ? "accepted the answer" : "rejected the answer";
    const waste = [judge.avoidableCalls ? plural(judge.avoidableCalls, "avoidable call") : "", judge.avoidableRounds ? plural(judge.avoidableRounds, "extra round") : "",
        Number.isFinite(judge.avoidableSeconds) && judge.avoidableSeconds > 0 ? `about ${judge.avoidableSeconds.toFixed(0)} s` : ""].filter(Boolean);
    return `The judge ${verdict}: grounded ${yesNo(judge.grounded)}, complete ${yesNo(judge.complete)}, efficiency ${stars(judge.efficiencyScore)} ${judge.efficiencyScore ?? "?"}/5${waste.length ? ` (${waste.join(", ")} of waste)` : " (no waste)"}.`;
}

// One scenario as a collapsible GitHub-markdown block: headline, then what it checks, what happened, the judge's
// scores and reasons, and every hard check. Failures and known limitations open by default.
export function explainMarkdown(scenario, row, publishAnswers = false, { previewAnswer = (text) => cell(text), open = false } = {}) {
    const status = statusOf(scenario, row);
    const info = explain(scenario, row, publishAnswers);
    const result = row?.result;
    const lines = [
        `<details${open || status === "fail" || status === "known" ? " open" : ""}><summary>${STATUS[status].dot} <b>${escapeHtml(scenario?.label ?? row?.id)}</b> · ${STATUS[status].word} · ${seconds(result?.durationMs)} · ${toolsLabel(result?.toolCount)} · ${stars(result?.judge?.efficiencyScore)}</summary>`,
        "",
        `**What this eval checks.** ${cell(info.question)}`,
    ];
    if (info.expectation) lines.push("", `> ${cell(info.expectation)}`);
    const hard = [
        info.requiredTools.length ? `must call ${info.requiredTools.map((name) => `\`${cell(name)}\``).join(", ")}` : "",
        info.forbiddenTools.length ? `must never call ${info.forbiddenTools.map((name) => `\`${cell(name)}\``).join(", ")}` : "",
        `at most ${info.maxToolCalls} tool calls and ${info.maxDurationSeconds} s`,
    ].filter(Boolean);
    lines.push("", `Hard rules: ${hard.join("; ")}. A separate judge model then grades the answer against the question, the tool evidence and the expectation.`);
    if (info.knownIssue) lines.push("", `🟠 Known limitation: ${cell(info.knownIssue)}`);
    lines.push("", `**What happened.** ${cell(happenedSentence(info))}`);
    if (info.calls.length) {
        lines.push("", "| # | Start | Tool | OK | Time |", "|--:|--:|---|:-:|--:|");
        for (const [index, call] of info.calls.slice(0, 40).entries())
            lines.push(`| ${index + 1} | ${Number.isFinite(call.startMs) ? `+${seconds(call.startMs)}` : "-"} | ${cell(call.name)} | ${call.ok ? "✅" : "❌"} | ${seconds(call.durationMs)} |`);
        if (info.calls.length > 40) lines.push(`| | | … ${info.calls.length - 40} more | | |`);
    }
    lines.push("", `**What the judge scored.** ${cell(judgeSentence(info))}`);
    if (info.judge) {
        lines.push("", "| Accepted | Grounded | Complete | Efficiency |", "|:-:|:-:|:-:|:-:|",
            `| ${yesNo(info.judge.accepted)} | ${yesNo(info.judge.grounded)} | ${yesNo(info.judge.complete)} | ${stars(info.judge.efficiencyScore)} ${info.judge.efficiencyScore ?? "?"}/5 |`);
        lines.push("", info.judge.findings.length
            ? `**Why:** ${info.judge.findings.map((finding) => `${finding.kind === "quality" ? "❌" : "⚠️"} ${cell(finding.text)}`).join(" · ")}`
            : "**Why:** no issues found.");
        if (info.judge.reason) lines.push("", `**Judge's reasoning:** ${previewAnswer(info.judge.reason, 1200)}`);
    }
    if (info.answer) lines.push("", "**Answer:**", "", `<pre>${previewAnswer(info.answer, 2400)}</pre>`);
    else if (info.answerWithheld) lines.push("", "_The answer and the judge's written reasoning are withheld because this run used internal tenant data; the scores, the reasons above and the checks below are published._");
    const mark = (check) => `${check.ok === null ? "⚪" : check.ok ? "✅" : "❌"} ${cell(check.label)}`;
    // An eval's own card has room for one check per line; a board of many cards keeps them on one line.
    if (open) lines.push("", "**Checks.**", "", ...info.checks.map((check) => `- ${mark(check)}`));
    else lines.push("", `**Checks.** ${info.checks.map(mark).join(" · ")}`);
    const unexplained = info.failures.filter((failure) => !/^(Required tool missing|Forbidden tool called)/.test(failure));
    if (unexplained.length) lines.push("", `Failed because: ${unexplained.map(cell).join(" ")}`);
    lines.push("", "</details>");
    return lines.join("\n");
}

// The summary of one eval's own job: a headline that matches the job's green or red status, then the explanation open.
export function renderEvalMarkdown(scenario, row, { title, tierNote = "", sha, accepted, runFailures = [], publishAnswers = false, previewAnswer } = {}) {
    let status = statusOf(scenario, row);
    // A job that ended without a result, or whose result the run did not accept, is never shown green.
    if (status === "pass" && accepted === false) status = "fail";
    const meta = STATUS[status];
    const verdict = {
        pass: "Passed every hard check and the judge accepted the answer.",
        fail: "Failed: the reasons are below.",
        known: "Failed only for its documented known limitation, so this job stays green.",
        missing: "No result: the eval did not finish. See the failed step of this job.",
    }[status];
    const popular = popularity(scenario);
    return [
        `# ${meta.dot} ${meta.word} · ${escapeHtml(title ?? scenario?.label ?? scenario?.id)}`,
        "",
        `**${escapeHtml(verdict)}**`,
        "",
        [tierNote ? escapeHtml(tierNote) : "", popular ? `🔥 ${escapeHtml(popular)}` : "", sha ? `revision \`${escapeHtml(String(sha).slice(0, 7))}\`` : ""].filter(Boolean).join(" · "),
        ...(accepted === false && runFailures.length ? ["", `The run was not accepted: ${runFailures.map(cell).join(" ")}`] : []),
        "",
        explainMarkdown(scenario, row, publishAnswers, { ...(previewAnswer ? { previewAnswer } : {}), open: true }),
        "",
        "_The 🏁 Scoreboard job combines every eval of this run into one board and dashboard._",
        "",
    ].join("\n");
}

// The verdict is authoritative: a run that was not accepted is red whatever its scoreboard shows, so a setup failure, a
// timeout or a cancelled run (nothing failed, most scenarios not run) can never look amber or green.
export function headline(board, accepted) {
    if (board.failed > 0)
        return { icon: "🔴", text: `${board.failed} of ${board.total} scenario${board.total === 1 ? "" : "s"} failed`, state: "fail" };
    if (!accepted)
        return {
            icon: "🔴",
            text: board.missing > 0
                ? `Not accepted: ${board.total - board.missing} of ${board.total} ran`
                : "Not accepted",
            state: "fail",
        };
    if (board.missing > 0)
        return { icon: "🟠", text: `Incomplete: ${board.total - board.missing} of ${board.total} ran`, state: "known" };
    if (board.known > 0)
        return { icon: "🟢", text: `${board.passed} green, ${board.known} known limitation${board.known === 1 ? "" : "s"}`, state: "pass" };
    return { icon: "🟢", text: `All ${board.total} scenario${board.total === 1 ? "" : "s"} green`, state: "pass" };
}

// Twenty squares, proportional to what passed, failed or is a known limitation.
export function progressBar(board, width = 20) {
    const total = Math.max(board.total, 1);
    const green = Math.round((board.passed / total) * width);
    const red = Math.min(width - green, Math.ceil((board.failed / total) * width));
    const amber = Math.min(width - green - red, Math.ceil((board.known / total) * width));
    return "🟩".repeat(green) + "🟧".repeat(amber) + "🟥".repeat(red) + "⬜".repeat(width - green - red - amber);
}

export function renderBoardMarkdown(cases, results, context) {
    const { title, accepted, sha, tierNote = "", threshold = null, verdictLine = "", heatmap = true, links = {} } = context;
    const board = scoreboard(cases, results);
    const state = headline(board, accepted);
    // An eval links to the job that ran it when the caller knows that job's page.
    const groupName = (group) => cell(groupTitle(group));
    const scenarioName = (scenario) => {
        const url = typeof links[scenario.id] === "string" && /^https:\/\/github\.com\//.test(links[scenario.id]) ? links[scenario.id] : "";
        const label = cell(scenario.label ?? scenario.id);
        return url ? `[${label}](${encodeURI(url)})` : label;
    };
    const lines = [
        `# ${state.icon} ${escapeHtml(title)} · ${escapeHtml(state.text)}`,
        "",
        // The authoritative verdict leads, before the detail that explains it.
        ...(verdictLine ? [verdictLine, ""] : []),
        `${progressBar(board)} **${percent(board.total ? board.passed / board.total : 0)} green** · revision \`${escapeHtml(String(sha).slice(0, 7))}\`${tierNote ? ` · ${escapeHtml(tierNote)}` : ""}`,
        "",
        "| 🟢 Passed | 🔴 Failed | 🟠 Known limits | ⚪ Not run | ⏱ Median | ⏱ Slowest | 🔧 Tool calls | ★ Efficiency |",
        "|---:|---:|---:|---:|---:|---:|---:|---:|",
        `| **${board.passed}** | **${board.failed}** | ${board.known} | ${board.missing} | ${seconds(board.medianMs)} | ${seconds(board.slowestMs)} | ${board.toolCalls} | ${board.efficiency === null ? "-" : `${board.efficiency.toFixed(1)} / 5`} |`,
    ];
    if (threshold !== null)
        lines.push(
            "",
            `Quality bar: **${percent(threshold)}** of scored scenarios must pass. This run: **${percent(board.passRate)}** (${board.passed} of ${board.scored} scored; known limitations are listed but not scored).`,
        );
    const failing = board.items.filter((item) => item.status === "fail");
    if (failing.length)
        lines.push(
            "",
            `### 🔴 Needs attention (${failing.length})`,
            "",
            "| Scenario | Group | Judge | Why |",
            "|---|---|:-:|---|",
            ...failing.map((item) => `| ${scenarioName(item.scenario)} | ${groupName({ ...GROUP_META[item.group], key: item.group })} | ${judgeBadge(item.row?.result?.judge)} | ${cell(whyOf(item))} |`),
        );
    const known = board.items.filter((item) => item.status === "known");
    if (known.length) {
        const byLimitation = new Map();
        for (const item of known) {
            const key = item.scenario.knownIssue;
            if (!byLimitation.has(key)) byLimitation.set(key, []);
            byLimitation.get(key).push(item.scenario.label ?? item.scenario.id);
        }
        lines.push("", `### 🟠 Known limitations (${known.length})`);
        for (const [limitation, labels] of byLimitation)
            lines.push("", `${cell(limitation)}`, "", labels.map((label) => `- ${cell(label)}`).join("\n"));
    }
    if (!heatmap) return lines.join("\n") + "\n";
    lines.push(
        "",
        "### Heatmap",
        "",
        "🟩 passed · 🟥 failed · 🟧 known limitation · ⬜ not run. One square per scenario.",
        "",
        "| Group | Scenarios | Green |",
        "|---|---|---:|",
        ...board.groups.map((group) => `| ${groupName(group)}${group.share ? ` · ≈${group.share}% of audited chats` : ""} | ${squares(group.items)} | ${groupCount(group)} |`),
    );
    for (const group of board.groups) {
        const open = groupState(group) === "fail" ? " open" : "";
        lines.push(
            "",
            `<details${open}><summary>${STATUS[groupState(group)].dot} <b>${escapeHtml(groupTitle(group))}</b> · ${groupCount(group)} green</summary>`,
            "",
            "| | Scenario | Time | Tools | Efficiency |",
            "|:-:|---|---:|---:|:-:|",
            ...group.items.map((item) => {
                const result = item.row?.result;
                const flame = popularity(item.scenario) ? " 🔥" : "";
                return `| ${STATUS[item.status].dot} | ${scenarioName(item.scenario)}${flame} | ${seconds(result?.durationMs)} | ${Number.isFinite(result?.toolCount) ? result.toolCount : "-"} | ${stars(result?.judge?.efficiencyScore)} |`;
            }),
            "",
            "</details>",
        );
    }
    return lines.join("\n") + "\n";
}

export function renderLogLine({ index, total, scenario, row, color = false }) {
    const status = statusOf(scenario, row);
    const meta = STATUS[status];
    const paint = (code, text) => (color ? `\u001b[${code}m${text}\u001b[0m` : text);
    const result = row?.result;
    const width = String(total).length;
    const parts = [
        `[${String(index + 1).padStart(width)}/${total}]`,
        meta.dot,
        paint(meta.ansi, ` ${meta.word} `),
        paint("1", scenario.label ?? scenario.id),
        paint("2", `${seconds(result?.durationMs)} · ${toolsLabel(result?.toolCount)} · ${stars(result?.judge?.efficiencyScore)} · ${scenario.id}`),
    ];
    const lines = [parts.join(" ")];
    if (status === "fail" || status === "known")
        lines.push(paint(status === "fail" ? "31" : "33", `           ↳ ${row.failures.join(" ")}`));
    return lines.join("\n");
}

const commandData = (value) =>
    String(value).replaceAll("%", "%25").replaceAll("\r", "%0D").replaceAll("\n", "%0A");
const commandProperty = (value) =>
    commandData(value).replaceAll(":", "%3A").replaceAll(",", "%2C");

// GitHub shows at most ten annotations of each level per step: failures first, then one scoreboard notice. A job that
// combines evals whose own jobs already annotated their failures passes failures: false; a single eval's job, whose
// status already says it all, passes notice: false.
export function renderAnnotations(cases, results, context, maximum = 10, { failures = true, notice = true } = {}) {
    const board = scoreboard(cases, results);
    const command = (level, title, message) =>
        `::${level} title=${commandProperty(title)}::${commandData(String(message).slice(0, 600))}`;
    const state = headline(board, context.accepted);
    const lines = [];
    for (const item of failures ? board.items.filter((entry) => entry.status === "fail").slice(0, maximum) : []) {
        const info = explain(item.scenario, item.row);
        lines.push(command("error", `🔴 ${item.scenario.label ?? item.scenario.id}`, `${judgeSentence(info)} Why: ${whyOf(item)}`));
    }
    for (const item of board.items.filter((entry) => entry.status === "known").slice(0, maximum))
        lines.push(command("warning", `🟠 ${item.scenario.label ?? item.scenario.id}`, item.scenario.knownIssue));
    if (!notice) return lines;
    lines.push(command(
        state.state === "fail" ? "error" : "notice",
        `${state.icon} ${context.title}: ${percent(board.total ? board.passed / board.total : 0)} green`,
        `${board.passed} passed, ${board.failed} failed, ${board.known} known limitations, ${board.missing} not run of ${board.total} scenarios. Median ${seconds(board.medianMs)}.`,
    ));
    return lines;
}

const CSS = `
:root{--bg:#0b1020;--panel:#121a30;--panel2:#18223d;--line:#26335a;--text:#e8edf7;--muted:#9aa8c7;--green:#22c55e;--red:#ef4444;--amber:#f59e0b;--grey:#475569;--accent:#4f8cff;--accent2:#8b5cf6}
@media (prefers-color-scheme:light){:root{--bg:#f4f7fc;--panel:#fff;--panel2:#f0f4fb;--line:#d5deee;--text:#0f172a;--muted:#52627f}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:15px/1.5 "Segoe UI",system-ui,-apple-system,Roboto,sans-serif}
main{max-width:1240px;margin:0 auto;padding:28px 20px 60px}a{color:var(--accent)}code{font-family:ui-monospace,Consolas,monospace;background:var(--panel2);padding:1px 6px;border-radius:6px}
.hero{display:grid;grid-template-columns:auto 1fr;gap:32px;align-items:center;background:linear-gradient(135deg,var(--panel),var(--panel2));border:1px solid var(--line);border-radius:20px;padding:28px 32px}
.brand{font-size:13px;letter-spacing:.14em;text-transform:uppercase;color:var(--muted)}h1{margin:.1em 0 .2em;font-size:34px;line-height:1.15}h1 .ttl{background:linear-gradient(90deg,var(--accent),var(--accent2));-webkit-background-clip:text;background-clip:text;color:transparent}
.sub{color:var(--muted);margin:0}
.donut{--p:0;--k:0;--f:0;width:176px;height:176px;border-radius:50%;display:grid;place-items:center;background:conic-gradient(var(--green) 0 calc(var(--p)*1%),var(--amber) 0 calc((var(--p) + var(--k))*1%),var(--red) 0 calc((var(--p) + var(--k) + var(--f))*1%),var(--grey) 0)}
.donut>div{width:132px;height:132px;border-radius:50%;background:var(--panel);display:grid;place-items:center;text-align:center}.donut b{font-size:38px;line-height:1}.donut span{display:block;font-size:12px;color:var(--muted)}
.counters{display:flex;flex-wrap:wrap;gap:12px;margin:18px 0 0}.counter{min-width:112px;border-radius:14px;padding:10px 16px;border:1px solid var(--line);background:var(--panel)}
.counter b{display:block;font-size:28px;line-height:1.1}.counter span{font-size:12px;color:var(--muted);text-transform:uppercase;letter-spacing:.06em}
.counter.pass{border-color:var(--green)}.counter.pass b{color:var(--green)}.counter.fail{border-color:var(--red)}.counter.fail b{color:var(--red)}.counter.known{border-color:var(--amber)}.counter.known b{color:var(--amber)}
.metrics{display:flex;flex-wrap:wrap;gap:22px;margin-top:16px;color:var(--muted)}.metrics b{color:var(--text)}
h2{font-size:20px;margin:34px 0 12px}
.heat{display:grid;gap:10px}.heat .row{display:grid;grid-template-columns:260px 1fr 64px;gap:14px;align-items:center;background:var(--panel);border:1px solid var(--line);border-radius:12px;padding:10px 14px}
.heat .name{font-weight:600}.heat .name small{display:block;color:var(--muted);font-weight:400}.heat .count{text-align:right;font-variant-numeric:tabular-nums;color:var(--muted)}
.sqs{display:flex;flex-wrap:wrap;gap:4px}.sq{width:20px;height:20px;border-radius:5px;display:block;border:1px solid rgba(255,255,255,.12)}
.sq.pass,.dot.pass{background:var(--green)}.sq.fail,.dot.fail{background:var(--red)}.sq.known,.dot.known{background:var(--amber)}.sq.missing,.dot.missing{background:var(--grey)}
.sq.fail{animation:pulse 1.6s ease-in-out infinite}@keyframes pulse{50%{box-shadow:0 0 0 4px rgba(239,68,68,.35)}}@media (prefers-reduced-motion:reduce){.sq.fail{animation:none}}
.controls{display:flex;flex-wrap:wrap;gap:8px;align-items:center;margin:26px 0 8px}.controls button{cursor:pointer;border:1px solid var(--line);background:var(--panel);color:var(--text);border-radius:999px;padding:6px 14px;font:inherit}
.controls button[aria-pressed=true]{background:var(--accent);border-color:var(--accent);color:#fff}.controls input{flex:1;min-width:200px;border:1px solid var(--line);background:var(--panel);color:var(--text);border-radius:999px;padding:7px 14px;font:inherit}
details.group{background:var(--panel);border:1px solid var(--line);border-radius:14px;margin:12px 0;overflow:hidden}details.group>summary{cursor:pointer;list-style:none;padding:14px 18px;display:flex;gap:12px;align-items:center;font-weight:600}
details.group>summary::-webkit-details-marker{display:none}.dot{width:12px;height:12px;border-radius:50%;flex:none}.grow{flex:1}.bar{height:8px;width:160px;background:var(--grey);border-radius:99px;overflow:hidden;display:flex}.bar i{display:block;height:100%}
.bar .pass{background:var(--green)}.bar .known{background:var(--amber)}.bar .fail{background:var(--red)}
.case{border-top:1px solid var(--line);padding:0}.case>summary{cursor:pointer;list-style:none;display:grid;grid-template-columns:96px 1fr auto;gap:12px;align-items:center;padding:10px 18px}.case>summary::-webkit-details-marker{display:none}
.chip{display:inline-block;text-align:center;border-radius:8px;padding:2px 10px;font-size:12px;font-weight:700;letter-spacing:.04em}
.chip.pass{background:rgba(34,197,94,.16);color:#86efac;border:1px solid var(--green)}.chip.fail{background:rgba(239,68,68,.16);color:#fca5a5;border:1px solid var(--red)}.chip.known{background:rgba(245,158,11,.16);color:#fcd34d;border:1px solid var(--amber)}.chip.missing{background:rgba(71,85,105,.3);color:var(--muted);border:1px solid var(--grey)}
@media (prefers-color-scheme:light){.chip.pass{color:#166534}.chip.fail{color:#991b1b}.chip.known{color:#92400e}}
.meta{color:var(--muted);font-size:13px;white-space:nowrap;font-variant-numeric:tabular-nums}.star{color:var(--amber);letter-spacing:1px}
.body{padding:4px 18px 18px 126px;color:var(--muted)}.body p{margin:.4em 0}.body b{color:var(--text)}pre{white-space:pre-wrap;word-break:break-word;background:var(--panel2);border:1px solid var(--line);border-radius:10px;padding:12px;color:var(--text);max-height:340px;overflow:auto}
.tools{display:flex;flex-wrap:wrap;gap:6px;margin:6px 0}.tool{font-size:12px;border-radius:6px;padding:1px 8px;border:1px solid var(--line);background:var(--panel2);color:var(--text)}.tool.bad{border-color:var(--red);color:#fca5a5}
.why{color:#fca5a5}.hidden{display:none!important}footer{margin-top:40px;color:var(--muted);font-size:13px}
.body section{margin:10px 0 14px}.body h4{margin:0 0 6px;font-size:13px;letter-spacing:.06em;text-transform:uppercase;color:var(--accent)}
blockquote{margin:8px 0;padding:8px 12px;border-left:3px solid var(--accent2);background:var(--panel2);border-radius:0 8px 8px 0;color:var(--text)}
table.calls{border-collapse:collapse;font-size:13px;margin:6px 0;font-variant-numeric:tabular-nums}table.calls th,table.calls td{padding:3px 10px;border-bottom:1px solid var(--line);text-align:left}table.calls th{color:var(--muted);font-weight:600}table.calls tr.bad td{color:#fca5a5}
.scores{display:flex;flex-wrap:wrap;gap:10px;margin:6px 0}.score{min-width:110px;border:1px solid var(--line);border-radius:12px;padding:8px 12px;background:var(--panel2)}.score b{display:block;font-size:20px}.score span{font-size:12px;color:var(--muted)}.score.pass{border-color:var(--green)}.score.fail{border-color:var(--red)}
ul.findings,ul.checks{margin:6px 0;padding-left:0;list-style:none}ul.findings li,ul.checks li{margin:3px 0}ul.findings li.quality{color:#fca5a5}ul.findings li.efficiency{color:#fcd34d}ul.findings li.ok{color:#86efac}
@media (prefers-color-scheme:light){ul.findings li.quality,table.calls tr.bad td,.why{color:#991b1b}ul.findings li.efficiency{color:#92400e}ul.findings li.ok{color:#166534}}
.withheld,.knownnote{font-style:italic}
@media (max-width:760px){.hero{grid-template-columns:1fr}.heat .row{grid-template-columns:1fr}.case>summary{grid-template-columns:84px 1fr}.case>summary .meta{grid-column:1/-1}.body{padding-left:18px}}
@media print{body{background:#fff;color:#000}.controls{display:none}details.group:not([open])>*:not(summary){display:block}}
`;

const SCRIPT = `
(()=>{const cards=[...document.querySelectorAll('.case')];const buttons=[...document.querySelectorAll('.controls button')];const input=document.querySelector('.controls input');let filter='all';
const apply=()=>{const q=input.value.trim().toLowerCase();for(const card of cards){const ok=(filter==='all'||card.dataset.status===filter)&&(!q||card.dataset.text.includes(q));card.classList.toggle('hidden',!ok);}
for(const group of document.querySelectorAll('details.group')){const any=[...group.querySelectorAll('.case')].some(c=>!c.classList.contains('hidden'));group.classList.toggle('hidden',!any);if(filter!=='all'||q)group.open=any;}};
for(const b of buttons)b.addEventListener('click',()=>{filter=b.dataset.filter;buttons.forEach(x=>x.setAttribute('aria-pressed',String(x===b)));apply();});input.addEventListener('input',apply);})();
`;

export function renderHtml(cases, results, context) {
    const { title, accepted, sha, publishAnswers, tierNote = "", threshold = null, generatedAt } = context;
    const board = scoreboard(cases, results);
    const state = headline(board, accepted);
    const pct = (value) => (board.total ? (value / board.total) * 100 : 0).toFixed(2);
    const counter = (status, label, value) =>
        `<div class="counter ${status}"><b>${value}</b><span>${label}</span></div>`;
    const heat = board.groups.map((group) => `
<div class="row"><div class="name">${escapeHtml(groupTitle(group))}<small>${group.share ? `≈${group.share}% of audited chats · ` : ""}${groupCount(group)} green</small></div>
<div class="sqs">${group.items.map((item) => `<a class="sq ${item.status}" href="#case-${escapeHtml(item.scenario.id)}" title="${escapeHtml(`${item.scenario.label ?? item.scenario.id} — ${STATUS[item.status].word}`)}"></a>`).join("")}</div>
<div class="count">${groupCount(group)}</div></div>`).join("");
    const groups = board.groups.map((group) => {
        const total = Math.max(group.items.length, 1);
        const share = (status) => ((group.items.filter((item) => item.status === status).length / total) * 100).toFixed(1);
        const open = groupState(group) === "fail" ? " open" : "";
        const cards = group.items.map((item) => {
            const result = item.row?.result;
            const info = explain(item.scenario, item.row, publishAnswers);
            const flame = popularity(item.scenario)
                ? ` <span title="${escapeHtml(popularity(item.scenario))}">🔥</span>` : "";
            const mark = (ok) => (ok === null ? "⚪" : ok ? "✅" : "❌");
            const hard = [
                info.requiredTools.length ? `must call ${info.requiredTools.map((name) => `<code>${escapeHtml(name)}</code>`).join(", ")}` : "",
                info.forbiddenTools.length ? `must never call ${info.forbiddenTools.map((name) => `<code>${escapeHtml(name)}</code>`).join(", ")}` : "",
                `at most ${escapeHtml(info.maxToolCalls)} tool calls and ${escapeHtml(info.maxDurationSeconds)} s`,
            ].filter(Boolean).join("; ");
            const calls = info.calls.length
                ? `<table class="calls"><thead><tr><th>#</th><th>Start</th><th>Tool</th><th>OK</th><th>Time</th></tr></thead><tbody>${info.calls.slice(0, 40).map((call, index) =>
                    `<tr class="${call.ok ? "" : "bad"}"><td>${index + 1}</td><td>${Number.isFinite(call.startMs) ? `+${seconds(call.startMs)}` : "-"}</td><td>${escapeHtml(call.name)}</td><td>${call.ok ? "✅" : "❌"}</td><td>${seconds(call.durationMs)}</td></tr>`).join("")}</tbody></table>`
                : "";
            const judgeBlock = info.judge
                ? `<div class="scores">${[["Accepted", info.judge.accepted], ["Grounded", info.judge.grounded], ["Complete", info.judge.complete]].map(([name, ok]) =>
                    `<div class="score ${ok ? "pass" : "fail"}"><b>${ok ? "✅" : "❌"}</b><span>${name}</span></div>`).join("")}<div class="score ${info.judge.efficient ? "pass" : "fail"}"><b class="star">${stars(info.judge.efficiencyScore)}</b><span>Efficiency ${escapeHtml(info.judge.efficiencyScore ?? "?")}/5</span></div></div>
<p>${escapeHtml(judgeSentence(info))}</p>
<ul class="findings">${info.judge.findings.length ? info.judge.findings.map((finding) => `<li class="${finding.kind}">${finding.kind === "quality" ? "❌" : "⚠️"} ${escapeHtml(finding.text)}</li>`).join("") : "<li class=\"ok\">✅ No issues found.</li>"}</ul>
${info.judge.reason ? `<p><b>Judge's reasoning</b><br>${escapeHtml(info.judge.reason)}</p>` : ""}`
                : `<p>${escapeHtml(judgeSentence(info))}</p>`;
            const details = [
                `<section><h4>What this eval checks</h4><p>${escapeHtml(info.question)}</p>${info.expectation ? `<blockquote>${escapeHtml(info.expectation)}</blockquote>` : ""}<p>Hard rules: ${hard}. A separate judge model then grades the answer against the question, the tool evidence and the expectation.</p>${info.knownIssue ? `<p class="knownnote">🟠 Known limitation: ${escapeHtml(info.knownIssue)}</p>` : ""}</section>`,
                `<section><h4>What happened</h4><p>${escapeHtml(happenedSentence(info))}</p>${calls}</section>`,
                `<section><h4>What the judge scored</h4>${judgeBlock}</section>`,
                info.answer ? `<section><h4>Answer</h4><pre>${escapeHtml(String(info.answer).slice(0, 6000))}</pre></section>` : "",
                info.answerWithheld ? "<p class=\"withheld\">The answer and the judge's written reasoning are withheld because this run used internal tenant data; the scores, the reasons and the checks are published.</p>" : "",
                `<section><h4>Checks</h4><ul class="checks">${info.checks.map((check) => `<li>${mark(check.ok)} ${escapeHtml(check.label)}</li>`).join("")}</ul>${item.status !== "pass" ? `<p class="why"><b>Failed because</b> ${escapeHtml(reasonOf(item))}</p>` : ""}</section>`,
            ].join("");
            return `
<details class="case" id="case-${escapeHtml(item.scenario.id)}" data-status="${item.status}" data-text="${escapeHtml(`${item.scenario.label ?? ""} ${item.scenario.question}`.toLowerCase())}">
<summary><span class="chip ${item.status}">${STATUS[item.status].word}</span><span>${escapeHtml(item.scenario.label ?? item.scenario.id)}${flame}</span>
<span class="meta">${seconds(result?.durationMs)} · ${toolsLabel(result?.toolCount)} · <span class="star">${stars(result?.judge?.efficiencyScore)}</span></span></summary>
<div class="body">${details}</div></details>`;
        }).join("");
        return `
<details class="group"${open}><summary><span class="dot ${groupState(group)}"></span><span class="grow">${escapeHtml(groupTitle(group))}</span>
<span class="bar" aria-hidden="true"><i class="pass" style="width:${share("pass")}%"></i><i class="known" style="width:${share("known")}%"></i><i class="fail" style="width:${share("fail")}%"></i></span>
<span class="meta">${groupCount(group)}</span></summary>${cards}</details>`;
    }).join("");
    return `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>${escapeHtml(title)}: ${escapeHtml(state.text)}</title><style>${CSS}</style></head>
<body><main>
<section class="hero">
<div class="donut" style="--p:${pct(board.passed)};--k:${pct(board.known)};--f:${pct(board.failed)}" role="img" aria-label="${escapeHtml(`${board.passed} of ${board.total} scenarios passed`)}"><div><div><b>${percent(board.total ? board.passed / board.total : 0)}</b><span>green</span></div></div></div>
<div><div class="brand">Azure FinOps Agent</div><h1><span class="ico">${state.icon}</span> <span class="ttl">${escapeHtml(title)}</span></h1>
<p class="sub">${escapeHtml(state.text)} · revision <code>${escapeHtml(String(sha).slice(0, 7))}</code>${tierNote ? ` · ${escapeHtml(tierNote)}` : ""}${generatedAt ? ` · ${escapeHtml(generatedAt)}` : ""}</p>
<div class="counters">${counter("pass", "passed", board.passed)}${counter("fail", "failed", board.failed)}${counter("known", "known limits", board.known)}${counter("missing", "not run", board.missing)}</div>
<div class="metrics"><span>Median <b>${seconds(board.medianMs)}</b></span><span>Slowest <b>${seconds(board.slowestMs)}</b></span><span>First token <b>${seconds(board.firstTokenMs)}</b></span><span>Tool calls <b>${board.toolCalls}</b></span><span>Efficiency <b>${board.efficiency === null ? "-" : `${board.efficiency.toFixed(1)} / 5`}</b></span>${threshold !== null ? `<span>Scored pass rate <b>${percent(board.passRate)}</b></span><span>Quality bar <b>${percent(threshold)}</b></span>` : ""}</div></div>
</section>
<h2>Heatmap</h2><div class="heat">${heat}</div>
<h2>Scenarios</h2>
<div class="controls" role="group" aria-label="Filter scenarios"><button data-filter="all" aria-pressed="true">All ${board.total}</button><button data-filter="fail" aria-pressed="false">🔴 Failed ${board.failed}</button><button data-filter="known" aria-pressed="false">🟠 Known ${board.known}</button><button data-filter="pass" aria-pressed="false">🟢 Passed ${board.passed}</button><input type="search" placeholder="Search scenarios" aria-label="Search scenarios"></div>
${groups}
<footer>Real model calls through the candidate chat handler and tools, graded by a separate structured judge. Each scenario must pass its deterministic checks (required and forbidden tools, tool failures, time and tool budgets) and the judge's accepted, grounded, complete and efficient verdicts. Test-host authentication is not a browser sign-in test.</footer>
</main><script>${SCRIPT}</script></body></html>
`;
}
