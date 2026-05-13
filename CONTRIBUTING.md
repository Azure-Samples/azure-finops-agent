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

New branches must use one of the following prefixes (enforced by a GitHub
ruleset on branch creation — existing branches are unaffected):

| Prefix | Use |
|---|---|
| `feature/<short-desc>` | new functionality |
| `fix/<short-desc>` | bug fix |
| `bugfix/<short-desc>` | bug fix (alias) |
| `hotfix/<short-desc>` | urgent production fix |
| `chore/<short-desc>` | tooling, dependencies, refactor |
| `docs/<short-desc>` | docs-only change |
| `users/<alias>/<short-desc>` | personal experiments |
| `dependabot/**` | reserved for Dependabot |

Pushing to any non-`main` branch deploys it to the test slot at
<https://finops-agent-container-test.azurewebsites.net> via `feature.yml`.
The branch name is shown in the top-right badge of the running app.

## Development Setup

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js LTS](https://nodejs.org/)
- Microsoft Entra ID app registration (for Azure tenant data access)

### Building & Running

- **Backend**: .NET 10 minimal API in `src/Dashboard/`
- **Frontend**: Vue 3 + Vite SPA in `src/Dashboard/frontend/`

```bash
# One-time: Create Entra ID app registration
cd src/Dashboard
.\setup-entra-app.ps1
# Copy the output ClientId/ClientSecret into appsettings.Local.json

# Build the Vue frontend to wwwroot/
cd client
npm install
npm run build

# Start the .NET backend (must set Development environment)
cd ..
$env:ASPNETCORE_ENVIRONMENT="Development"
dotnet run --urls "http://localhost:5000"

# Open http://localhost:5000
```

> **Important**: You must set `ASPNETCORE_ENVIRONMENT=Development` before running. Without it, the app defaults to Production, loads `appsettings.Production.json`, and causes OAuth redirect_uri mismatch errors.

### Secrets

- `appsettings.Local.json` — local dev Microsoft Entra ID and Azure OpenAI secrets (gitignored, only loaded in Development)
- `appsettings.Production.json` — production OAuth secrets (gitignored)
- `appsettings.json` — base config with empty placeholders (committed)

### Project Structure

```
src/Dashboard/
├── Program.cs              # App composition, middleware, endpoint mapping
├── AI/                     # Copilot SDK session factory, chat SSE endpoint, tools
├── Auth/                   # Microsoft Entra ID OAuth, session token store
├── Web/                    # Upload/download/SEO/meta endpoints
├── Infrastructure/         # HTTP helper, temp file helper
├── Observability/          # OpenTelemetry sources/meters
├── frontend/src/components/  # Vue 3 components (ChatView, Dashboard)
├── Dockerfile              # Multi-stage build (frontend + .NET + Python + OTel)
└── setup-entra-app.ps1     # Entra ID app registration setup (one-time)
```

### Code Conventions

- **Backend**: Clean C# following Microsoft coding conventions, .NET 10 APIs, Vue 3 Composition API with `<script setup>`
- **Tools**: Return raw API JSON (let the LLM interpret it), use `string` parameters, keep tools simple. **All tools are read-only** — write operations are blocked at the HTTP client level.
- **Frontend**: Modern JavaScript, ECharts for visualization, SSE for streaming

See the [README](README.md) for full architecture details.

## Reporting Issues

Please use [GitHub Issues](https://github.com/Azure-Samples/azure-finops-agent/issues) to report bugs or request features.
