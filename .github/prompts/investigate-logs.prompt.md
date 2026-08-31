---
mode: agent
description: "Discover the configured Application Insights workspace, investigate recent exceptions, fix the root cause, and verify locally."
---

# Investigate logs and fix exceptions

Read `/memories/repo/finops-agent-debugging.md` first. Use the requested time window, or the last two days by default.

## 1. Confirm the target

Never hardcode or infer a tenant, subscription, resource group, Application Insights application ID, workspace ID, alert name, or application name.

1. Show `az account show`.
2. Resolve the intended deployment from `azd env get-values`, GitHub Actions configuration, or explicit user input.
3. List Application Insights components in that confirmed scope.
4. If more than one target is plausible, ask the user to select it.
5. Resolve the selected component's `workspaceResourceId` and the workspace `customerId` dynamically.

Do not copy discovered identifiers into source, prompts, memories, or the final response.

## 2. Query workspace tables

For workspace-based Application Insights, query Log Analytics directly. The classic Application Insights query surface may not include OpenTelemetry exceptions.

At minimum inspect:

- `AppExceptions`: count and latest timestamp by exception type and method
- `AppTraces`: severity 3+ and messages containing failure/throttle keywords
- `AppRequests`: unsuccessful requests by operation and result code
- `AppDependencies`: unsuccessful Azure, Graph, storage, and model calls by operation and result code

Drill into top offenders with bounded projections. Treat many rows at one timestamp as possible fan-out from one request. Discover scheduled-query alerts by listing them in the confirmed resource group rather than assuming a name.

## 3. Triage

Classify each finding before editing:

- Expected transient egress failures already retried/degraded
- SDK-internal self-healing session failures
- Browser/client cancellation
- Internet bot scans
- Local startup races
- Actionable application defects or exception fan-out

Under `UseAzureMonitor()`, logging a non-null exception object creates an `AppExceptions` row. Expected failures should generally be structured trace messages without the exception object; genuine faults should be logged once with correlation context.

## 4. Fix and validate

- Trace actionable findings to code and apply the smallest root-cause fix.
- Preserve the rule that agent tools do not catch API exceptions internally.
- Build the backend; build the frontend if touched.
- Run focused tests and re-query telemetry only if the user explicitly requests deployment or a live post-deploy check.
- Record reusable debugging lessons in repository memory without deployment identifiers.

Do not deploy or push unless explicitly requested.
