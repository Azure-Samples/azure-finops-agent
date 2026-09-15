import assert from "node:assert/strict";
import test from "node:test";
import { createRequire } from "node:module";
import { createExceptionReporter, redactDiagnosticText, safeTelemetryProperties } from "../../src/Dashboard/frontend/src/telemetrySafety.js";

test("installed notification manager preserves its asynchronous state argument", async () => {
  const require = createRequire(new URL("../../src/Dashboard/frontend/package.json", import.meta.url));
  const { NotificationManager } = require("@microsoft/applicationinsights-core-js");
  const manager = new NotificationManager({});
  let timeout;
  try {
    await new Promise((resolve, reject) => {
      timeout = setTimeout(() => reject(new Error("Notification callback did not run")), 2000);
      manager.addNotificationListener({ eventsSent(events) { assert.deepEqual(events, [{ name: "synthetic" }]); resolve(); } });
      manager.eventsSent([{ name: "synthetic" }]);
    });
  } finally { clearTimeout(timeout); manager.unload(false); }
});

test("one fault captured through two handlers produces one incident", () => {
  const events = [];
  const report = createExceptionReporter((exception, properties) => events.push({ exception, properties }));
  const error = new Error("Synthetic notification error");
  report(error, { source: "window.error" });
  report(error, { source: "vue.errorHandler" });
  assert.equal(events.length, 1);
});

test("diagnostic URLs and credential fields are redacted", () => {
  const properties = safeTelemetryProperties({ sessionId: "synthetic", prompt: "private input", authorization: "Bearer synthetic", url: "https://user:password@example.test/path?sig=secret", tool: "Read" });
  assert.deepEqual(properties, { sessionId: "synthetic", url: "https://example.test/path", tool: "Read" });
  assert.equal(redactDiagnosticText("Bearer synthetic-token"), "Bearer [REDACTED]");
});

test("telemetry failures do not escape into application execution", () => {
  const report = createExceptionReporter(() => { throw new Error("SDK failure"); });
  assert.equal(report(new Error("Synthetic error")), false);
});