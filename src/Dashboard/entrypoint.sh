#!/bin/bash
# Container entrypoint — starts the OTel collector in the background, then
# launches the .NET app in the foreground so its stdout/stderr (and exit
# status) drive container lifecycle.
set -e

if [ -n "$APPLICATIONINSIGHTS_CONNECTION_STRING" ] || [ -n "$ApplicationInsights__ConnectionString" ]; then
  # Normalise both env-var spellings so the collector config picks one up.
  export APPLICATIONINSIGHTS_CONNECTION_STRING="${APPLICATIONINSIGHTS_CONNECTION_STRING:-$ApplicationInsights__ConnectionString}"
  echo "[entrypoint] starting OTel collector → Azure Monitor"
  /usr/local/bin/otelcol --config /etc/otelcol/config.yaml &
  COLLECTOR_PID=$!
  # Forward signals so SIGTERM from App Service shuts both down cleanly.
  trap "kill -TERM $COLLECTOR_PID 2>/dev/null || true" TERM INT
else
  echo "[entrypoint] APPLICATIONINSIGHTS_CONNECTION_STRING not set — skipping collector"
fi

exec dotnet Dashboard.dll
