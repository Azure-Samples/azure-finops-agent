const terminalMessages = {
  stopped: "You stopped this response before it finished.",
  empty: "No answer was generated for this message.",
  error: "No answer was returned for this question. This turn has ended and is no longer generating.",
  timeout: "This turn timed out. Review any partial results or pending operations before retrying.",
  interrupted: "This turn was interrupted. Review any partial results or pending operations before retrying.",
  rejected: "This request was rejected before completion. Review the request and any error before retrying.",
};

export function terminalRecoveryState(active, outcomes, userMessageCount) {
  if (active !== false || !Array.isArray(outcomes) || !Number.isInteger(userMessageCount)
      || userMessageCount < 1 || outcomes.length !== userMessageCount) return null;
  const latest = [...outcomes].sort((left, right) =>
    String(left.startedUtc || "").localeCompare(String(right.startedUtc || ""))).at(-1);
  if (!latest?.completedUtc || !Number.isFinite(Date.parse(latest.completedUtc))) return null;
  const text = terminalMessages[latest.status];
  return text ? {
    status: latest.status,
    text,
    failure: latest.status === "stopped" ? null : describeTurnFailure("", latest.status),
  } : null;
}

const modelAuthorizationError = /Authentication failed with provider[\s\S]*HTTP (?:401|403)/i;

// A reloaded page (or a severed stream) has no live events for a turn the
// server is still running; the /active probe is all it has. Returns null unless
// the probe positively reports a running turn.
export function serverTurnFromProbe(sessionId, probe) {
  if (!sessionId || probe?.active !== true) return null;
  const started = Date.parse(probe.startedUtc || "");
  const tools = probe.toolsCompleted;
  return {
    sessionId,
    startedMs: Number.isFinite(started) ? started : null,
    toolsCompleted: Number.isInteger(tools) && tools > 0 ? tools : 0,
    scheduled: probe.scheduled === true,
  };
}

export function formatElapsed(ms) {
  const total = Math.max(0, Math.floor(Number(ms) / 1000) || 0);
  const minutes = Math.floor(total / 60);
  const seconds = total % 60;
  return minutes ? `${minutes}m ${String(seconds).padStart(2, "0")}s` : `${seconds}s`;
}

// Elapsed time and finished tool calls are evidence the turn is alive, not
// completion progress. Chat turns can be stopped; scheduled runs cannot here.
export function describeServerTurn(turn, nowMs) {
  if (!turn) return "";
  const parts = [];
  if (Number.isFinite(turn.startedMs)) parts.push(`Running for ${formatElapsed(nowMs - turn.startedMs)}`);
  if (turn.toolsCompleted > 0)
    parts.push(`${turn.toolsCompleted} tool call${turn.toolsCompleted === 1 ? "" : "s"} finished`);
  const status = parts.length ? `${parts.join(" · ")}.` : "";
  return turn.scheduled ? status : [status, "Press Stop to cancel it."].filter(Boolean).join(" ");
}

export function userFacingStreamError(message) {
  const text = String(message || "The request failed.");
  if (modelAuthorizationError.test(text)) {
    const status = /HTTP (401|403)/i.exec(text)[1];
    return `The app could not access its AI model (HTTP ${status}). Your tenant sign-in is separate from model inference access.`;
  }
  return text;
}

export function describeTurnFailure(message, status = "error") {
  const titles = {
    error: "Couldn't complete this answer",
    empty: "No answer was returned",
    timeout: "The response timed out",
    interrupted: "The response was interrupted",
    rejected: "The request was not accepted",
  };
  const modelAccessBlocked = modelAuthorizationError.test(String(message || ""));
  return {
    title: modelAccessBlocked ? "AI model access is blocked" : titles[status] || titles.error,
    text: message ? userFacingStreamError(message) : terminalMessages[status] || terminalMessages.error,
    hint: modelAccessBlocked
      ? "The deployment owner needs to check the configured OpenAI endpoint, model identity and its inference permissions. Signing in to your tenant again will not fix model access. Never paste keys or tokens into chat."
      : "Review any partial results or pending changes before trying again.",
  };
}
