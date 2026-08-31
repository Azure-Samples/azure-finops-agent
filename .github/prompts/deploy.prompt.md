---
agent: agent
description: "Validate and deploy Azure FinOps Agent using the configured Azure environment"
---

## Deploy Azure FinOps Agent

Deploy only after the user explicitly confirms the target environment and asks to deploy. Never infer or hardcode tenant IDs, subscription IDs, resource groups, app names, registry names, endpoints, IP addresses, or credentials.

### 1. Establish the target

1. Inspect `git status --short`; do not deploy uncommitted or unreviewed changes.
2. Read `azure.yaml`, the current `azd` environment, and GitHub Actions configuration.
3. Show the active Azure account with `az account show` and ask for confirmation if the intended tenant/subscription is not already unambiguous.
4. Resolve deployment coordinates from one of these external sources:
   - Customer deployment: `azd env get-values`
   - Maintainer CI: GitHub Actions repository variables and secrets
   - Manual recovery: user-supplied values for this run only
5. Never write resolved values into tracked files.

### 2. Validate

```powershell
cd src/Dashboard/frontend
npm ci
npm run build

cd ..
dotnet build Dashboard.csproj --no-restore
```

Run relevant tests and inspect the complete diff. Scan tracked changes for secrets, GUIDs, resource IDs, host-specific names, email addresses, and IP addresses before proceeding.

### 3. Deploy

Prefer the customer-supported Azure Developer CLI path:

```powershell
azd up
```

For maintainer CI, push an already-reviewed commit to the branch mapped by the workflow. The workflow must read all target coordinates from GitHub Actions configuration and authenticate with OIDC.

Use a manual ACR/App Service deployment only for an explicitly requested recovery. Resolve the registry, image, web app, resource group, and verification URL from external configuration first; never substitute repository-specific literals into this prompt.

### 4. Verify

1. Wait for deployment completion and require a successful deployment status.
2. Read the verification URL from deployment output or external configuration.
3. Check `/api/version` until it reports the expected commit/build.
4. Run a production smoke test without exposing credentials or customer data.
5. Report the deployed commit, target type, verification result, and any rollback action taken.

### Rules

- Never deploy automatically or without explicit user approval.
- Never commit deployment coordinates or credentials.
- Prefer managed identity and OIDC over client secrets.
- Do not weaken the application's no-delete security boundary.
- Do not report success from a build alone; verify the running endpoint.
