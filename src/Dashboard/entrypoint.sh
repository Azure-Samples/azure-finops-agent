#!/bin/bash
set -eu

COLLECTOR_PID=""
APP_PID=""

stop_children() {
  trap '' TERM INT
  if [ -n "$APP_PID" ]; then
    kill -TERM "$APP_PID" 2>/dev/null || true
    wait "$APP_PID" || true
  fi
  if [ -n "$COLLECTOR_PID" ]; then
    kill -TERM "$COLLECTOR_PID" 2>/dev/null || true
    wait "$COLLECTOR_PID" || true
  fi
}

trap stop_children TERM INT EXIT

if [ -n "${APPLICATIONINSIGHTS_CONNECTION_STRING:-}" ] || [ -n "${ApplicationInsights__ConnectionString:-}" ]; then
  # Normalise both env-var spellings so the collector config picks one up.
  export APPLICATIONINSIGHTS_CONNECTION_STRING="${APPLICATIONINSIGHTS_CONNECTION_STRING:-$ApplicationInsights__ConnectionString}"
  echo "[entrypoint] starting OTel collector → Azure Monitor"
  /usr/local/bin/otelcol --config /etc/otelcol/config.yaml &
  COLLECTOR_PID=$!
else
  echo "[entrypoint] APPLICATIONINSIGHTS_CONNECTION_STRING not set — skipping collector"
fi

# Ensure the Copilot SDK session-state root exists on the persistent /home mount.
# COPILOT_HOME is passed to the SDK via CopilotClientOptions.CopilotHome, which
# makes the CLI write under {COPILOT_HOME}/.copilot/session-state/. Failure here
# (e.g. /home is read-only) should fail loudly — silently swallowing the error
# masks a misconfigured mount and surfaces as a confusing runtime error later.
mkdir -p \
  "${COPILOT_HOME:-/home/copilot}/users" \
  "${COPILOT_HOME:-/home/copilot}/anon" \
  "${COPILOT_HOME:-/home/copilot}/.copilot/session-state"

dotnet Dashboard.dll &
APP_PID=$!
APP_EXIT=0
wait "$APP_PID" || APP_EXIT=$?
APP_PID=""
exit "$APP_EXIT"
