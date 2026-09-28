# syntax=docker/dockerfile:1.7
# ── Stage 1: Build Vue frontend ──
FROM node:26-slim AS frontend
WORKDIR /app/frontend
COPY frontend/package.json frontend/package-lock.json* ./
# Plain RUN (no BuildKit cache mount) so azd's `az acr build` classic builder works.
RUN npm ci --no-audit --no-fund --prefer-offline
COPY frontend/ ./
RUN npx vite build --outDir ../wwwroot

# ── Stage 2: Build .NET backend ──
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Dashboard.csproj ./
RUN dotnet restore -r linux-x64
COPY . .
COPY --from=frontend /app/wwwroot ./wwwroot/
RUN dotnet publish -c Release -r linux-x64 --self-contained false -o /app/publish

# ── Stage 3: Final runtime image ──
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

# Python 3 runs the file-analysis tool (plain RUN — classic-builder compatible, no BuildKit).
# Microsoft.Data.Sqlite bundles its own native SQLite, so no sqlite3/jq CLIs are installed.
RUN apt-get update -qq && \
  apt-get install -y --no-install-recommends \
  python3 python3-pip && \
  ln -sf /usr/bin/python3 /usr/bin/python && \
  rm -rf /var/lib/apt/lists/*

# Pip packages for the file-analysis tool (plain RUN — classic-builder compatible).
RUN pip3 install --break-system-packages \
  requests pandas numpy openpyxl tabulate python-dateutil \
  "pdfminer.six>=20240706" "pyarrow>=17.0"

WORKDIR /app
COPY --from=build /app/publish .
COPY entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh && \
  mkdir -p /home/copilot/users /home/copilot/anon /home/dataprotection-keys && \
  chown -R app:app /home/copilot /home/dataprotection-keys

# Build metadata baked into the image
ARG BUILD_SHA=dev
ARG BUILD_NUMBER=0
ARG BUILD_BRANCH=main
ENV BUILD_SHA=${BUILD_SHA}
ENV BUILD_NUMBER=${BUILD_NUMBER}
ENV BUILD_BRANCH=${BUILD_BRANCH}

# App Service expects port 8080 by default for containers
ENV ASPNETCORE_URLS=http://+:8080
# Persist conversations on the App Service /home Azure Files mount so chat
# history survives container restarts, scale operations and slot swaps. Entra
# conversations are isolated under a versioned hash of the validated
# tenant/object pair; anonymous conversations use a generated user id.
ENV COPILOT_HOME=/home/copilot
EXPOSE 8080

USER $APP_UID
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
