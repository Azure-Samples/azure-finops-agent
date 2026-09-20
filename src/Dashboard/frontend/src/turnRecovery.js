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
