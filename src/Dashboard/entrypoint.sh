#!/bin/bash
set -eu

# Conversation state lives on the persistent /home mount. Fail loudly when it
# is not writable instead of surfacing a confusing runtime error later.
mkdir -p "${COPILOT_HOME:-/home/copilot}/users" "${COPILOT_HOME:-/home/copilot}/anon"

exec dotnet Dashboard.dll
