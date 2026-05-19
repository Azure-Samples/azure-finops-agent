# syntax=docker/dockerfile:1.7
# ── Stage 1: Build Vue frontend ──
FROM node:26-slim AS frontend
WORKDIR /app/frontend
COPY frontend/package.json frontend/package-lock.json* ./
# Use `npm install` instead of `npm ci` so that Linux-only platform-optional
# deps (e.g. @emnapi/*) that aren't present when the lockfile was last
# regenerated on a Windows dev machine still resolve cleanly in CI.
# BuildKit cache mount keeps ~/.npm warm across CI builds.
RUN --mount=type=cache,target=/root/.npm \
    npm install --no-audit --no-fund --prefer-offline
COPY frontend/ ./
RUN npx vite build --outDir ../wwwroot

# ── Stage 2: Build .NET backend ──
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Dashboard.csproj ./
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore -r linux-x64
COPY . .
COPY --from=frontend /app/wwwroot ./wwwroot/
# Note: we deliberately do NOT pass --no-restore here. BuildKit cache mounts
# only exist for the duration of a single RUN, so the restore step above
# can't share its /root/.nuget/packages with this step. Re-mounting the
# cache makes the second restore essentially free (everything cache-hits).
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish -c Release -r linux-x64 --self-contained false -o /app/publish

# ── Stage 3: Final runtime image ──
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

# Install Python 3 + CLI tools. apt cache mounted so re-installs are cheap; we
# disable the apt auto-clean docker hook so the cache survives the RUN.
RUN rm -f /etc/apt/apt.conf.d/docker-clean
RUN --mount=type=cache,target=/var/cache/apt,sharing=locked \
    --mount=type=cache,target=/var/lib/apt,sharing=locked \
    apt-get update -qq && \
    apt-get install -y --no-install-recommends \
    python3 python3-pip \
    jq sqlite3 && \
    ln -sf /usr/bin/python3 /usr/bin/python

# Pip packages — cached separately so changing apt vs pip doesn't bust both.
RUN --mount=type=cache,target=/root/.cache/pip \
    pip3 install --break-system-packages \
    requests pandas numpy openpyxl tabulate python-dateutil \
    "pdfminer.six>=20240706" "pyarrow>=17.0"

# OpenTelemetry Collector (contrib build — needed for the azuremonitor exporter).
# Bridges OTLP from the Copilot CLI subprocess into Azure Application Insights.
COPY --from=otel/opentelemetry-collector-contrib:0.108.0 /otelcol-contrib /usr/local/bin/otelcol
COPY otel-collector-config.yaml /etc/otelcol/config.yaml

WORKDIR /app
COPY --from=build /app/publish .
COPY entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh

# Build metadata baked into the image
ARG BUILD_SHA=dev
ARG BUILD_NUMBER=0
ARG BUILD_BRANCH=main
ENV BUILD_SHA=${BUILD_SHA}
ENV BUILD_NUMBER=${BUILD_NUMBER}
ENV BUILD_BRANCH=${BUILD_BRANCH}

# App Service expects port 8080 by default for containers
ENV ASPNETCORE_URLS=http://+:8080
# Tell the Copilot CLI (and any other OTLP-capable child process) to push
# telemetry to the local collector.
ENV OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318
ENV OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
# Chat sessions are stateless: conversation history lives only in the user's
# browser (IndexedDB). The Copilot CLI uses its default ephemeral state dir
# under /tmp; nothing is persisted between turns.
EXPOSE 8080

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
