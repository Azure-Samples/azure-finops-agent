# Contributing to Azure FinOps Agent

This project welcomes contributions and suggestions. Most contributions require you to agree to a Contributor License Agreement (CLA) declaring that you have the right to, and actually do, grant us the rights to use your contribution. For details, visit https://cla.opensource.microsoft.com.

When you submit a pull request, a CLA bot will automatically determine whether you need to provide a CLA and decorate the PR appropriately (e.g., status check, comment). Simply follow the instructions provided by the bot. You will only need to do this once across all repos using our CLA.

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/). For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.

## How to Contribute

1. Fork this repository.
2. Create a feature branch (`git checkout -b feature/my-feature`).
3. Commit your changes (`git commit -am 'Add my feature'`).
4. Push to the branch (`git push origin feature/my-feature`).
5. Open a Pull Request.

## Branch Naming Convention

New branches must use one of two prefixes (kept simple on purpose):

| Prefix                 | Use                                      |
| ---------------------- | ---------------------------------------- |
| `feature/<short-desc>` | new functionality, refactor, docs, chore |
| `bug/<short-desc>`     | bug fix or hotfix                        |

When maintainer CI is configured, pushing a non-`main` branch deploys it to the
test slot selected by the `TEST_*` GitHub Actions variables in `feature.yml`.
The branch name is shown in the top-right badge of the running app.

## Development Setup

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js LTS](https://nodejs.org/)
- Microsoft Entra ID app registration (for Azure tenant data access)

### Building & Running

- **Backend**: .NET 10 minimal API in `src/Dashboard/`
- **Frontend**: Vue 3 + Vite SPA in `src/Dashboard/frontend/`

```powershell
# One-time: Create Entra ID app registration
cd src/Dashboard
.\setup-entra-app.ps1
# Store the output ClientId/ClientSecret via dotnet user-secrets (see README → Running Locally)

# Build the Vue frontend to wwwroot/
cd frontend
npm ci
npm run build

# Start the .NET backend (must set Development environment)
cd ..
$env:ASPNETCORE_ENVIRONMENT="Development"
dotnet run --urls "http://localhost:5000"

# Open http://localhost:5000
```

> **Important**: You must set `ASPNETCORE_ENVIRONMENT=Development` before running. Without it, the app defaults to Production and the OAuth `redirect_uri` will mismatch.

### Local model authorization

The backend uses the local Azure CLI identity for model inference, independently of the user's browser consent for Cost Management and other tools. Verify the tenant, the configured account endpoint and the deployment before troubleshooting an inference `401` or `403`.

Use the account's published **OpenAI Language Model Instance API** endpoint. For a multi-service `AIServices` account, the general `properties.endpoint` can be a different Cognitive Services endpoint; copying it into `AzureOpenAI:Endpoint` can return `PermissionDenied` even with valid OpenAI inference permissions. Read the published endpoint from the account's `properties.endpoints` instead of guessing or rewriting its hostname.

Have an authorized account owner grant **Cognitive Services OpenAI User** to the CLI identity in the model account's tenant, at that account's resource scope only. Management-plane Owner access alone does not grant inference. Do not substitute API keys, broaden the grant to a subscription, or commit account/principal identifiers. [Role assignments can take up to five minutes to become effective](https://learn.microsoft.com/azure/ai-foundry/openai/how-to/managed-identity#assign-role); verify a small synthetic inference request before retrying tenant cost queries.

For privately managed developer access, the equivalent Bicep fragment below assumes your template already declares the existing model account as `modelAccount`. Supply the principal ID locally; do not add a maintainer identity to the sample's shared deployment.

```bicep
param developerPrincipalId string

var inferenceRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')

resource developerInference 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(modelAccount.id, developerPrincipalId, inferenceRoleId)
  scope: modelAccount
  properties: {
    roleDefinitionId: inferenceRoleId
    principalId: developerPrincipalId
    principalType: 'User'
  }
}
```

### Secrets

Local dev secrets are managed via [`dotnet user-secrets`](https://learn.microsoft.com/aspnet/core/security/app-secrets) — they live outside the repo and cannot be committed by accident. See [README → Run locally](README.md#run-locally) for the full list of keys.

- `appsettings.json` — base config with empty placeholders (committed)
- App Service settings and managed identity — production configuration outside Git

### Managed-device package feeds

Use your organization's approved package source configuration. Check `dotnet nuget list source`, `python -m pip config list`, and `npm config get registry` before troubleshooting restore failures. Preserve existing approved team feeds; do not disable TLS checks or work around registry enforcement.

Keep personal credentials and company-specific feed URLs out of this public sample. Configure approved sources in user-local settings or the build environment. Containers and WSL do not automatically inherit host configuration; supply their approved feed settings explicitly. A newly published package may be quarantined by the feed even when its public release exists.

If dependencies are already installed and manifests have not changed, normal local builds can use `--no-restore`; this is not a substitute for validating an actual restore when dependency versions change.

### Regression Tests

Install Python 3 with `pandas` and `openpyxl` for file-query/report tests. Set `FINOPS_PYTHON` to the interpreter path when it is not the default `python` (Windows) or `python3` (Linux).

From the repository root:

```powershell
dotnet test tests/Dashboard.Tests/Dashboard.Tests.csproj
python -m unittest discover -s tests -p "test_*.py" -v
npm --prefix src/Dashboard/frontend ci
npm --prefix src/Dashboard/frontend run test
```

From `src/Dashboard/frontend`, run `npx playwright install chromium`, then `npm run test:browser` and `npm run build`. The browser tests cover desktop and mobile with synthetic API fixtures; they do not grant consent or mutate Azure resources.

The backend suite executes the package-supplied Copilot runtime against a local synthetic model provider, including images, cancellation, compaction and resume. Do not skip runtime acquisition to make the tests pass. The shared `validate.yml` workflow also builds and starts the Linux image as non-root and tests shutdown with an unavailable telemetry destination. Both deployment workflows depend on this validation job. Pushing `main` can deploy production automatically; local tests must pass first.

### Project Structure

```
src/Dashboard/
├── Program.cs              # App composition, middleware, endpoint mapping
├── AI/                     # Copilot SDK session factory, chat SSE endpoint, tools
├── Auth/                   # Microsoft Entra ID OAuth, session token store, persistent identity
├── Endpoints/              # Sessions, downloads, uploads, SEO/meta endpoints
├── Infrastructure/         # HTTP helper, temp file helper
├── Observability/          # OpenTelemetry sources/meters
├── frontend/src/components/  # Vue 3 components (ChatView, Dashboard)
├── Dockerfile              # Multi-stage build (frontend + .NET + Python + OTel)
└── setup-entra-app.ps1     # Entra ID app registration setup (one-time)
```

### Code Conventions

- **Backend**: Clean C# following Microsoft coding conventions, .NET 10 APIs, Vue 3 Composition API with `<script setup>`
- **Tools**: Return compact API JSON, use `string` parameters, and keep tools simple. Approved non-delete `PUT`/`PATCH` changes are bounded by the signed-in user's RBAC; Azure `DELETE` and mutating action `POST` operations are blocked in code.
- **Frontend**: Modern JavaScript, ECharts for visualization, SSE for streaming

See the [README](README.md) for full architecture details.

See [Minimal, Contract-Driven Architecture](docs/architecture.md) for state, transport, evidence and security boundaries.

## Reporting Issues

Please use [GitHub Issues](https://github.com/Azure-Samples/azure-finops-agent/issues) to report bugs or request features.
