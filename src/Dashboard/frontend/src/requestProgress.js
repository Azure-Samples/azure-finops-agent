export function createRequestProgress(event, now = Date.now(), service = "Service") {
  const suppliedDeadline = Date.parse(event.retryAtUtc || "");
  const suppliedWait = Number(event.waitSeconds);
  const wait = Number.isFinite(suppliedWait) ? Math.max(0, suppliedWait) : 0;
  const deadline = Number.isFinite(suppliedDeadline) ? suppliedDeadline : now + wait * 1000;
  return {
    service,
    status: Number(event.status) || 0,
    attempt: Number(event.attempt) || 0,
    willRetry: event.willRetry !== false,
    deadline,
    retryAtUtc: Number.isFinite(suppliedDeadline) ? event.retryAtUtc : new Date(deadline).toISOString(),
    startedAt: now,
    initialWait: wait,
    toolCallId: event.toolCallId || null,
    url: event.url || "",
    tool: event.tool || "",
  };
}

export function describeRequestProgress(progress, now = Date.now()) {
  const remaining = Math.max(0, Math.ceil((progress.deadline - now) / 1000));
  const retryTime = new Date(progress.deadline).toLocaleTimeString(undefined, {
    hour: "2-digit", minute: "2-digit", second: "2-digit", timeZoneName: "short",
  });
  const billing = /microsoft\.costmanagement\b/i.test(progress.url)
    || /cost management|forecast api|cost exports|cost views/i.test(progress.service);
  const explanation = progress.status === 429
    ? billing
      ? "Azure rate-limits its shared billing service. We honor its retry deadline."
      : `${progress.service} is rate limited. We honor the service's retry deadline.`
    : progress.status === 0
      ? "This response is taking a little longer. This is not a new throttling response."
      : `${progress.service} is temporarily unavailable (HTTP ${progress.status}). This is a service response, not a rate-limit response.`;
  const common = {
    explanation,
    badge: progress.status ? `HTTP ${progress.status}` : "Waiting",
    remaining,
    countdown: remaining < 60 ? `${remaining}s` : `${Math.floor(remaining / 60)}m ${String(remaining % 60).padStart(2, "0")}s`,
    retryAtUtc: progress.retryAtUtc,
    retryTime,
    percent: null,
    resourceNote: billing
      ? "This wait does not stop your Azure resources or reduce their charges."
      : "",
  };

  if (!progress.willRetry) {
    const guidance = remaining > 0 && progress.status !== 0
      ? `No further automatic retries for this turn. Retry after ${retryTime}, once this turn finishes.`
      : "No further automatic retries for this turn. You can submit a new request when this turn finishes.";
    return {
      ...common,
      phase: "stopped",
      title: `${progress.service}: automatic retry stopped`,
      heading: "Automatic retry has stopped",
      detail: `${explanation} ${guidance}`,
      guidance,
      badge: progress.status ? common.badge : "Stopped",
      remaining: progress.status === 0 ? null : remaining,
      countdownLabel: "Wait before a new request",
      sidebarLabel: "Stopped",
    };
  }
  if (progress.status === 0) {
    const guidance = "The request is still running. No retry countdown has been supplied; you do not need to resend.";
    return {
      ...common,
      phase: "slow",
      title: `${progress.service}: waiting for a response`,
      heading: "Still waiting on the service",
      detail: `${explanation} ${guidance}`,
      guidance,
      remaining: null,
      sidebarLabel: "Waiting",
    };
  }
  if (remaining === 0) {
    const guidance = "The retry deadline has passed. Waiting for the server's actual result; this is not a success signal. You do not need to resend.";
    return {
      ...common,
      phase: "retry-wait",
      title: `${progress.service}: waiting for the retry response`,
      heading: "Waiting for the retry response",
      detail: `${explanation} ${guidance}`,
      guidance,
      countdownLabel: "Cooldown elapsed",
      sidebarLabel: "Waiting",
    };
  }
  const guidance = `Waiting until ${retryTime}, then retrying automatically. You do not need to resend.`;
  const duration = progress.deadline - progress.startedAt;
  return {
    ...common,
    phase: "cooldown",
    title: `${progress.service}: retry in ${remaining}s`,
    heading: progress.status === 429
      ? billing ? "Azure's billing API is catching its breath" : "Giving the service a little breathing room"
      : "Giving the service a moment to recover",
    detail: `${explanation} ${guidance}`,
    guidance,
    countdownLabel: "Until the retry window",
    sidebarLabel: `${remaining}s`,
    percent: duration > 0 ? Math.min(99, Math.max(0, Math.floor((now - progress.startedAt) / duration * 100))) : 0,
  };
}

export function toolHttpStatus(result) {
  if (typeof result !== "string") return null;
  const status = /^HTTP\s+(\d{3})\b/.exec(result);
  return status ? Number(status[1]) : null;
}

export function toolResultSucceeded(sdkSuccess, result) {
  const status = toolHttpStatus(result);
  if (sdkSuccess === false || status >= 400) return false;

  let batch = result;
  if (typeof batch === "string") {
    try {
      batch = JSON.parse(status === null ? batch : batch.slice(batch.indexOf("\n") + 1));
    } catch {
      return true;
    }
  }
  const counters = ["succeeded", "failed", "pending", "unattempted", "cancelled"];
  const isBulkEnvelope = batch && !Array.isArray(batch)
    && Array.isArray(batch.results)
    && Number.isSafeInteger(batch.total) && batch.total >= 0
    && [...counters, "complete", "stopped"].some((key) => Object.hasOwn(batch, key));
  if (!isBulkEnvelope) return true;

  // SDK completion only means the batch tool returned, not that its work completed.
  return status !== 202
    && batch.complete === true
    && batch.stopped !== true
    && batch.succeeded === batch.total
    && batch.results.length === batch.total
    && counters.slice(1).every((key) => batch[key] === undefined || batch[key] === 0)
    && batch.results.every((item) => item?.outcome === "succeeded"
      && item.partial === false
      && Number.isInteger(item.status) && item.status >= 200 && item.status < 300
      && item.status !== 202);
}
