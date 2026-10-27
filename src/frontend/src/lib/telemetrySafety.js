export function redactDiagnosticText(value) {
  return String(value ?? "")
    .replace(/\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b/g, "[REDACTED]")
    .replace(/\bBearer\s+[A-Za-z0-9._~+/=-]+/gi, "Bearer [REDACTED]")
    .replace(/\b(?:password|client_secret|accountkey|sharedaccesskey|api_key)\s*[:=]\s*[^\s,;]+/gi, "[REDACTED]")
    .replace(/https?:\/\/[^\s)"']+/g, (value) => {
      try {
        const url = new URL(value);
        url.username = "";
        url.password = "";
        url.search = "";
        url.hash = "";
        return url.href;
      } catch { return "[URL]"; }
    });
}

export function safeTelemetryProperties(properties = {}) {
  return Object.fromEntries(Object.entries(properties).slice(0, 30)
    .filter(([key]) => !/prompt$|content|message|authorization|password|secret|token|args|body|header/i.test(key))
    .map(([key, value]) => [key, redactDiagnosticText(typeof value === "object" ? "[structured value omitted]" : value).slice(0, 500)]));
}

export function createExceptionReporter(send, now = Date.now) {
  const recent = new Map();
  let reporting = false;
  return (error, properties = {}) => {
    if (reporting) return false;
    const original = error instanceof Error ? error : new Error(typeof error === "string" ? error : "Frontend exception");
    const exception = new Error(redactDiagnosticText(original.message).slice(0, 1000));
    exception.name = original.name;
    exception.stack = redactDiagnosticText(original.stack || exception.stack).slice(0, 4000);
    const fingerprint = `${exception.name}:${exception.message}:${exception.stack}`;
    const time = now();
    const previous = recent.get(fingerprint);
    if (previous !== undefined && time - previous < 2000) return false;
    recent.set(fingerprint, time);
    if (recent.size > 100) recent.delete(recent.keys().next().value);
    reporting = true;
    try { send(exception, safeTelemetryProperties(properties)); return true; }
    catch { return false; }
    finally { reporting = false; }
  };
}