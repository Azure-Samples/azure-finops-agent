---
name: debug-local-docker
description: "Build and run the Azure FinOps Agent locally with Docker for debugging. Use when asked to debug locally with Docker, run the container on localhost, or test appsettings.Local.json inside a container."
---

## Local Debug (Docker)

> **CRITICAL**: You **must** set `ASPNETCORE_ENVIRONMENT=Development` when running the container.
> Without it, ASP.NET Core defaults to Production, which loads `appsettings.Production.json`
> (production OAuth credentials) and skips `appsettings.Local.json`. This causes an
> OAuth `redirect_uri` mismatch error.

> **CRITICAL**: `appsettings.Local.json` is excluded from the Docker image by `.dockerignore`.
> You **must** mount it into the container (step 2) so the app has your local Microsoft Entra ID
> and Azure OpenAI credentials.

1. Build the Docker image from `src/Dashboard/`:

```bash
cd src/Dashboard
docker build -t azure-finops-agent-local .
```

2. Run the container on local port 5000 (container listens on 8080), mounting the local secrets file:

```bash
docker run --rm -p 5000:8080 \
  -e ASPNETCORE_ENVIRONMENT=Development \
  -v "$(pwd)/appsettings.Local.json:/app/appsettings.Local.json:ro" \
  --name azure-finops-agent-local \
  azure-finops-agent-local
```

3. Open http://localhost:5000 in the browser and verify the chat page loads.

4. Confirm the `/api/version` endpoint responds with 200:

```bash
curl -i http://localhost:5000/api/version
```

5. Stop the app when done:

```bash
docker stop azure-finops-agent-local
```
