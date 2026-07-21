import { ApplicationInsights } from "@microsoft/applicationinsights-web";
import { createApp } from "vue";
import App from "./App.vue";

let appInsights = null;
const pendingTelemetry = [];

function withTelemetry(callback) {
  if (appInsights) {
    callback(appInsights);
    return;
  }

  pendingTelemetry.push(callback);
}

function flushPendingTelemetry() {
  if (!appInsights) return;

  while (pendingTelemetry.length > 0) {
    const callback = pendingTelemetry.shift();
    try {
      callback?.(appInsights);
    } catch {}
  }
}

function normalizeError(error, fallbackMessage) {
  if (error instanceof Error) return error;
  if (typeof error === "string" && error.trim()) return new Error(error);

  try {
    const serialized = JSON.stringify(error);
    if (serialized && serialized !== "{}") return new Error(serialized);
  } catch {}

  return new Error(fallbackMessage);
}

function trackFrontendException(error, properties = {}) {
  const exception = normalizeError(error, "Unknown frontend exception");
  withTelemetry((ai) => {
    ai.trackException({ exception, properties });
  });
}

function trackFrontendTrace(message, properties = {}) {
  withTelemetry((ai) => {
    ai.trackTrace({ message, properties });
  });
}

function trackFrontendEvent(name, properties = {}) {
  // Custom event — surfaces in the App Insights `customEvents` table so we can
  // chart the chat stream lifecycle (tab hidden/visible, stream done/severed,
  // background reconnects) and finally diagnose the "came back to an empty
  // answer" bug from real telemetry instead of guesswork.
  withTelemetry((ai) => {
    ai.trackEvent({ name }, properties);
  });
}

window.__trackAppInsightsException = trackFrontendException;
window.__trackAppInsightsTrace = trackFrontendTrace;
window.__trackAppInsightsEvent = trackFrontendEvent;

window.addEventListener("error", (event) => {
  trackFrontendException(event.error || event.message, {
    source: "window.error",
    filename: event.filename || "",
    lineno: String(event.lineno || 0),
    colno: String(event.colno || 0),
  });
});

window.addEventListener("unhandledrejection", (event) => {
  trackFrontendException(event.reason, {
    source: "window.unhandledrejection",
  });
});

window.addEventListener("securitypolicyviolation", (event) => {
  trackFrontendTrace(`CSP violation: ${event.violatedDirective}`, {
    source: "securitypolicyviolation",
    blockedURI: event.blockedURI || "",
    effectiveDirective: event.effectiveDirective || "",
    originalPolicy: event.originalPolicy || "",
    disposition: event.disposition || "",
  });
});

const app = createApp(App);

app.config.errorHandler = (error, instance, info) => {
  trackFrontendException(error, {
    source: "vue.errorHandler",
    info,
    component:
      instance?.type?.name || instance?.type?.__name || "anonymous-component",
  });
};

// Initialize Application Insights for frontend telemetry (page views, dependencies, browser exceptions)
fetch("/api/config")
  .then((r) => r.json())
  .then((config) => {
    if (!config.appInsightsConnectionString) return;

    appInsights = new ApplicationInsights({
      config: {
        connectionString: config.appInsightsConnectionString,
        enableAutoRouteTracking: true,
        enableCorsCorrelation: true,
        enableRequestHeaderTracking: true,
        enableResponseHeaderTracking: true,
        enableUnhandledPromiseRejectionTracking: true,
        correlationHeaderExcludedDomains: [
          "cdn.jsdelivr.net",
          "js.monitor.azure.com",
        ],
      },
    });

    appInsights.loadAppInsights();
    appInsights.trackPageView();
    window.__appInsights = appInsights;
    flushPendingTelemetry();
  })
  .catch(() => {});

app.mount("#app");
