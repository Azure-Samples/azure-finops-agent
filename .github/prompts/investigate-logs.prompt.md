---
mode: agent
description: "Investigate Application Insights exceptions for the Azure FinOps Agent and fix the root cause in code. Encodes the workspace-based-AI gotcha so the dig is fast."
---

# Investigate Logs → Fix Exceptions

Use my Azure CLI context to pull the app's exceptions from the **last 2 days** (override if I give a different window), find the root cause in the code, fix it, and verify. Read `/memories/repo/finops-agent-debugging.md` first — it holds the running catalog of known exceptions and past fixes.

## 0. Confirm az context (once)

Production telemetry lives in the subscription that owns `rg-finops-agent`. The persistent pwsh terminal lags on cross-tenant `az`, so **route every query to a temp file and read it back** (`> $env:TEMP\x.json 2>&1; Get-Content …`).

```pwsh
az account show --query "{sub:name, tenant:tenantId}" -o tsv
az group show -n rg-finops-agent --query id -o tsv   # fails → wrong tenant: az login --tenant <t>; az account set --subscription <s>
```

## 1. Pull the exceptions — the CRITICAL gotcha

`finops-agent` is a **workspace-based** Application Insights (`ingestionMode: LogAnalytics`). The classic query API **lies**: `az monitor app-insights query --app <appId>` returns rows for `traces`/`requests` but **`exceptions` comes back EMPTY** — the OTel-collector-ingested exceptions only land in the workspace `AppExceptions` table. Querying the classic `exceptions` table makes you wrongly conclude "no exceptions" while an alert is firing.

**Always resolve the backing workspace and query `AppExceptions` directly** (discover the GUID — never hardcode it; real sub/tenant/workspace IDs are not committed to this repo):

```pwsh
# Resolve the workspace this component ingests into.
$wsRes  = az monitor app-insights component show --app finops-agent -g rg-finops-agent --query workspaceResourceId -o tsv  # NOTE: camelCase query key
$wsName = ($wsRes -split '/')[-1]; $wsRg = ($wsRes -split '/')[4]
$ws     = az monitor log-analytics workspace show -g $wsRg -n $wsName --query customerId -o tsv

# Rank exception types over the window.
az monitor log-analytics query -w $ws --analytics-query "AppExceptions | where TimeGenerated > ago(2d) | summarize cnt=count(), latest=max(TimeGenerated) by ExceptionType, Method | order by cnt desc | take 25" > $env:TEMP\exc.json 2>&1
Get-Content $env:TEMP\exc.json -Raw
```

Then drill into the top offenders for messages + timing. **A burst of N rows at the same millisecond = one request fanning out** (e.g. a connect looping over N DNS addresses):

```pwsh
az monitor log-analytics query -w $ws --analytics-query "AppExceptions | where TimeGenerated > ago(2d) and Method contains '<Method>' | project TimeGenerated, ExceptionType, OuterMessage, OperationName, Method | order by TimeGenerated asc | take 40" > $env:TEMP\exc2.json 2>&1
Get-Content $env:TEMP\exc2.json -Raw
```

If an alert fired, read its real query/threshold (it evaluates the workspace, so it DOES see `AppExceptions`):

```pwsh
az monitor scheduled-query show -g rg-finops-agent -n FinOps-Exceptions --query "{q:criteria.allOf[0].query, threshold:criteria.allOf[0].threshold, window:windowSize}" -o json
```

KQL-via-CLI quirks: `summarize by problemId` and `has_any(...)` sometimes throw `BadArgumentError` — use `project` / `contains` and a simple `summarize by`. The App Insights App ID (for the classic `traces`/`requests` API, which DOES work) is in `copilot-instructions.md`.

## 2. Triage — actionable vs benign

Classify every exception type before touching code. **Known-benign — do NOT "fix" and do NOT log louder:**

- **Transient egress** — `SocketException` / `HttpRequestException` at `ExchangeRefreshTokenForResource` or `NetworkStream.ReadAsync`: already retried + degraded; auto-instrumentation records them.
- **SDK-internal** — `RemoteRpcException` / `InvalidOperationException` under `GitHub.Copilot.*`: self-healing (session resume spins up fresh sessions).
- **Client aborts** — `TaskCanceledException` / `OperationCanceledException` from a browser closing mid-request.
- **Bot scans** — 404s for `/wp-*.php`, `/admin.php`, etc.
- **Startup races** — `Failed to bind …127.0.0.1:5000` (stale local binary).

Actionable = our code throwing unexpectedly, or **one request emitting many exception rows**.

## 3. Trace to code + fix — the no-noise principle

**The #1 root-cause pattern here is exception-logging noise.** Under `UseAzureMonitor()`, `ILogger.Log(…, exception, …)` with a **non-null exception object → a new `AppExceptions` row**. So logging expected/transient failures with the exception object — especially inside a loop — turns one flaky request into 10+ exceptions and trips the `FinOps-Exceptions` alert (>10 in 15 min).

When fixing:

- **Expected/transient** failures (connect timeouts, cancellations, retryable network errors) → log a **structured message WITHOUT the exception object** (`"… {Reason}", ex.Message`) so it lands in `AppTraces`, not `AppExceptions`. Bail out of loops on caller cancellation (`ct.ThrowIfCancellationRequested()`); propagate `OperationCanceledException` instead of converting it to a fault.
- **Genuine one-off faults** → log ONCE with the exception object (rich `AppExceptions` row with `{Method} {Path} traceId`). That's what the table is for.
- Agent tools never use try/catch — the Copilot CLI + `UseAzureMonitor()` handle those centrally.
- Templates already in the tree: `Infrastructure/Ipv4HttpHandler.cs` (per-attempt CTS + structured logs) and the global `UseExceptionHandler` boundary in `Program.cs`.

## 4. Build, verify, record — do NOT deploy

```pwsh
dotnet build src/Dashboard/Dashboard.csproj -c Debug --nologo -v q
```

- Frontend touched? `cd src/Dashboard/frontend; npm run build`.
- Append the finding + fix to `/memories/repo/finops-agent-debugging.md`.
- **Never deploy** — that's the owner's call (see `.github/prompts/deploy.prompt.md`).