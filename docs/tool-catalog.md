# Agent Tool Catalog

This catalog covers all 36 application tools registered by [CopilotSessionFactory.cs](../src/Dashboard/AI/CopilotSessionFactory.cs#L1), their model-facing inputs, and the application's prompt sources as of 2026-09-17. The linked declarations below contain the complete descriptions and JSON examples; source remains authoritative.

## Metadata Model

- **24 direct registrations** have no `defer` property. `GenerateScript` is in this set, with instructions to supply complete code directly.
- **12 Auto-defer registrations** carry `AdditionalProperties["defer"] = CopilotToolDefer.Auto`. This is declaration metadata, not proof of on-demand discovery or a latency saving: [RuntimePolicy.cs](../src/Dashboard/AI/RuntimePolicy.cs#L1) explicitly disables tool search. The SDK/CLI determines effective schema delivery under that policy. No additional discovery tool has been enabled.
- `AIFunctionFactory.Create` publishes `Name`, `Description`, inferred `JsonSchema`, and parameter-level `[Description]` text. JSON bags such as `paramsJson` and `slidesJson` are strings in that schema; their inner contracts are described in the tool metadata and validated by their implementations.
- [ProtectedTool.cs](../src/Dashboard/AI/Tools/ProtectedTool.cs#L1) binds each callback to the owner, session and SDK invocation, screens arguments, holds cancellation leases, records evidence, and redacts returns. ARM write proposals and approval dispatch are enforced by the HTTP/operation layer, not by descriptions alone.
- Public means no delegated Azure-service token is required, not an unauthenticated tool endpoint. Tools still run inside an application session. Owner IDs, tokens and cancellation tokens are host-supplied, never model parameters.
- Direct availability never grants arbitrary host execution. `GenerateScript` packages code with credential redaction as a 24-hour owner-bound artifact; it never executes the script. Delivery is a proposed remediation, not an executed or verified savings action.
- Built-in shell, filesystem, MCP, cross-session memory, and logged-in CLI tools remain disabled. Host-owned file/report helpers are not arbitrary model-code execution.

Inputs below are strings unless `int`, `double`, or `bool` is shown. `=value` is a C# default; `?` denotes nullable, not necessarily optional in the emitted schema. For a nullable input without a default, supply null when unused. Defaults of null may have an effective fallback described in the contract. Cancellation tokens are omitted because the host supplies them.

## Direct Registrations (24)

| Tool | Credential / scope | Inputs | Model-facing prompt contract |
| --- | --- | --- | --- |
| `RenderChart` | Public | `type`, `title`, `seriesName`, `data`, `xAxisName?`, `yAxisName?` | Render one bounded ECharts bar, horizontal bar, line, pie, scatter, funnel, or race chart. |
| `RenderAdvancedChart` | Public | `options` | Static ECharts JSON for maps and advanced charts, at most 100,000 characters; DOM, HTML, links, and remote-image sinks are rejected. |
| `SuggestFollowUp` | Session | `label`, `prompt`, `label2=null`, `prompt2=null`, `label3=null`, `prompt3=null` | Emit one to three concrete clickable next actions; labels at most 60 characters. Public fast-path turns use a prompt link instead. |
| `GetAzureRetailPricing` | Public | `serviceName`, `armRegionName=null`, `armSkuName=null`, `priceType=null`, `meterNameContains=null`, `productNameContains=null`, `skuNameContains=null`, `currencyCode=null`, `rank=null`, `top:int=50` | One filtered retail-price lookup; effective currency USD. Read FACETS and RESOLUTION, preserve meter/region coverage, and use `rank=cheapest` for full ranking. `top` is clamped to 1-100. |
| `GetAzureRetailPricingBatch` | Public | `queriesJson` | Run 2-8 independently filtered retail-price lookups in one parallel host call. |
| `EstimateTokenCost` | Public | `modelsJson`, `inputTokensPerConversation`, `outputTokensPerConversation`, `conversationsPerMonth`, `cachedInputTokensPerConversation=null`, `currency=null` | Calculate reconciled costs for 1-20 models from per-million-token rates; effective cached tokens 0 and currency USD. Unknown rates are not zero. |
| `GenerateScript` | Owner-bound artifact | `scriptContent`, `filename?`, `language?`, `description?` | For an explicit Azure CLI or PowerShell code request, submit the complete executable code directly in this call; package it without executing it. |
| `ReportJobOutcome` | Scheduled turn only | `status`, `summary`, `evidenceToolsJson`, `dataAsOfUtc=null`, `nextEligibleRunUtc=null` | One terminal outcome: completed, unchanged, goal_achieved, blocked, partial or failed. Summary at most 1,000 characters and up to 50 cited tool names; successful outcomes require host-validated evidence. |
| `GenerateDataReport` | Owner-bound artifact | `format`, `dataJson`, `filename=null` | Create CSV, XLSX, or filterable HTML from `{title,source,sheets:[{name,columns,rows,sourceRowCount}]}`; maximum 5,000 rows, 50 columns and 10 sheets. |
| `ReportMaturityScore` | Owner-bound state | `level`, `scores` | Persist evidence-backed Crawl/Walk/Run/Playbook dimensions; unknown and not-applicable scores stay null. |
| `GetScoreHistory` | Owner-bound state | `level=null` | Return up to 100 persisted maturity assessments, optionally filtered by level. |
| `QueryAzure` | Delegated ARM token | `method`, `path`, `body=null` | Shape reads using supported API options only. Read-only POST is allowlisted; PUT/PATCH require host approval; DELETE is blocked. |
| `QueryCostsAcrossSubscriptions` | Delegated ARM token | `subscriptionsJson`, `from`, `to`, `managementGroupId=null` | One all-subscription total/breakdown call; up to 500 scopes and a 366-day date range (`to` exclusive). Budget MTD may lag billing; other periods use a bounded sequential fallback. |
| `BulkAzureRequest` | Delegated ARM token | `requestsJson`, `parallelism:int=20`, `stopOnFirstError:bool=false` | 1-200 indexed requests with filtered reads or reviewable writes; concurrency 1-50. Returns pending/failed/unattempted/partial coverage. Cost queries must not be placed in a parallel batch. |
| `CheckComputeFeasibility` | Delegated ARM token | `subscriptionsJson`, `skuNames`, `regions=all`, `count=1`, `priority=standard`, `zone=null` | 1-10 subscriptions, 1-5 exact SKUs, count 1-1000, Spot or standard, optional zone 1/2/3. Preserve explicit all-region coverage, catalogue coverage, quota, placement and eviction evidence separately. |
| `CheckVmConnectivity` | Delegated ARM token | `networkWatcherResourceId`, `sourceVmResourceId`, `destinationAddress`, `destinationPort` | Start a bounded TCP diagnostic from an existing VM through an existing Network Watcher. |
| `GetOperationStatus` | Owner-bound ARM operation | `operationId` | Poll only the host-stored URL for one approved mutation or diagnostic; accepted and in-progress are not success. |
| `ListOperationResults` | Owner-bound state | None | List up to 100 recent operations for the current conversation, including partial prerequisites. |
| `QueryGraph` | Delegated Graph token | `path`, `method=GET`, `body=null` | Use supported `$filter`, `$select`, `$top`, reports/counts and necessary pagination. Standard delegated tiers are read-only; writes require separate write scopes. |
| `GetCrawlMaturityEvidence` | Delegated ARM token | `subscriptionsJson`, `managementGroupId=null` | Collect, score, persist, and emit all seven Crawl dimensions exactly once without supplemental evidence tools. |
| `RecordSavingsAction` | Owner-bound state | `title`, `category`, `estimatedMonthlyUsd`, `scope`, `status` | Record evidenced remediation. Script delivery/pending writes are proposed; executed requires confirmed application. No entry for generic examples. |
| `UpdateSavingsAction` | Owner-bound state | `id`, `status`, `verifiedMonthlyUsd` | Advance an entry to proposed/executed/verified/dismissed; an empty `verifiedMonthlyUsd` preserves the prior value. |
| `GetSavingsLedger` | Owner-bound state | `status=null`, `category=null`, `scopeContains=null`, `limit=50`, `offset=0` | Host-side filters and newest-first paging; default 50, max 200 details, or limit 0 for totals only. Totals cover all matches before paging; preserve matchedEntries, nextOffset, complete and totalsComplete. Paging values are strings. |
| `QueryUploadedFile` | Conversation-bound upload | `fileId`, `mode`, `paramsJson?` | Registered upload only; query mode supports filters, groups, aggregates, sorting, output columns (1-50), offset and limit. Counts/totals survive projection and paging; at most 200 rows or 8,000 characters per response. |

## Auto-Defer Registrations (12)

| Tool | Credential / scope | Inputs | Model-facing prompt contract |
| --- | --- | --- | --- |
| `GetAzureServiceHealth` | Public | None | Reuse one public RSS feed read; no service/region filter. Not proof of tenant-specific resource health. |
| `FetchPublicWebPage` | Public HTTPS | `url`, `grepFor=null`, `maxChars:int=60000` | One bounded fallback to an authoritative page; 600,000-byte transfer cap, 1,000-200,000 output characters. `grepFor` reduces model context after download. |
| `GenerateHtmlPresentation` | Owner-bound artifact | `slidesJson`, `filename?`, `customer=null` | Structured slides: title, section, kpi, chart, content, two_column, maturity, alerts, table, roadmap or closing. Full slide schema is in the declaration. |
| `GenerateMaturityReport` | Owner-bound artifact | `reportJson`, `filename=null`, `customer=null` | Evidence-based scrolling HTML assessment, up to 19 capabilities across four FinOps domains. The declaration documents all report sections. |
| `QueryLogAnalytics` | Delegated Log Analytics token | `id`, `query`, `timespan=P1D`, `target=loganalytics` | Run KQL with selective `where`, `summarize`, narrow `project`, and `top`/`take`; aggregate before drilling down. |
| `ListCostExportBlobs` | Delegated Storage token | `storageAccount`, `container`, `prefix=null` | Prefix-filtered XML listing of at most 50 blobs. Nonempty NextMarker means partial; refine the prefix. |
| `ReadCostExportBlob` | Delegated Storage token | `storageAccount`, `container`, `blobPath` | At most 512,000 output characters from one exact blob, not a byte-range download. Request an upload for full analysis or generate a requested user-run download workflow without credentials. |
| `DetectCostAnomalies` | Delegated ARM token | `subscriptionId`, `days:int=35`, `zThreshold:double=2`, `groupBy=ServiceName` | Daily cost outliers; days clamped to 14-90, threshold 1-5, grouped breakdowns. Correlate the spike window with resource changes. |
| `StartPricesheetDownload` | Delegated ARM token | `billingScope` | Start an EA/MCA negotiated-pricesheet long-running operation. |
| `GetPricesheetStatus` | Delegated ARM token | `operationStatusUrl` | Poll only the existing returned operation URL, respecting backoff. For rate analysis use an uploaded pricesheet; do not forward a credential-bearing download URL to public web fetch. |
| `FindIdleResources` | Delegated ARM token | `subscriptionIds=null`, `topPerPattern:int=50` | Eight filtered Resource Graph waste patterns, 1-200 matches per pattern. Omitted subscriptions means all accessible. Empty resource groups are not themselves billable waste. |
| `PublishFAQ` | Azure-connected user | `question`, `answer`, `title` | Publish or queue a public FinOps Q&A only; tenant-specific data is prohibited. |

## Complete Metadata Sources

These links expose the full tool descriptions, parameter descriptions, nested JSON examples, defaults, and implementation validations, rather than a second hand-maintained copy of each long prompt.

| Source | Tools |
| --- | --- |
| [ChartTools.cs](../src/Dashboard/AI/Tools/ChartTools.cs#L1) | RenderChart, RenderAdvancedChart |
| [FollowUpTools.cs](../src/Dashboard/AI/Tools/FollowUpTools.cs#L1) | SuggestFollowUp |
| [RetailPricingTools.cs](../src/Dashboard/AI/Tools/RetailPricingTools.cs#L1) | GetAzureRetailPricing, GetAzureRetailPricingBatch |
| [CostEstimateTools.cs](../src/Dashboard/AI/Tools/CostEstimateTools.cs#L1) | EstimateTokenCost |
| [ScriptTools.cs](../src/Dashboard/AI/Tools/ScriptTools.cs#L1) | GenerateScript |
| [JobRunOutcome.cs](../src/Dashboard/Jobs/JobRunOutcome.cs#L1) | ReportJobOutcome |
| [ReportTools.cs](../src/Dashboard/AI/Tools/ReportTools.cs#L1) | GenerateDataReport |
| [ScoreTools.cs](../src/Dashboard/AI/Tools/ScoreTools.cs#L1) | ReportMaturityScore, GetScoreHistory |
| [AzureQueryTools.cs](../src/Dashboard/AI/Tools/AzureQueryTools.cs#L1) | QueryAzure, QueryCostsAcrossSubscriptions, BulkAzureRequest |
| [ComputeDiagnosticTools.cs](../src/Dashboard/AI/Tools/ComputeDiagnosticTools.cs#L1) | CheckComputeFeasibility, CheckVmConnectivity |
| [OperationTools.cs](../src/Dashboard/AI/Tools/OperationTools.cs#L1) | GetOperationStatus, ListOperationResults |
| [GraphQueryTools.cs](../src/Dashboard/AI/Tools/GraphQueryTools.cs#L1) | QueryGraph |
| [CrawlMaturityTools.cs](../src/Dashboard/AI/Tools/CrawlMaturityTools.cs#L1) | GetCrawlMaturityEvidence |
| [SavingsLedgerTools.cs](../src/Dashboard/AI/Tools/SavingsLedgerTools.cs#L1) | RecordSavingsAction, UpdateSavingsAction, GetSavingsLedger |
| [UploadedFileTools.cs](../src/Dashboard/AI/Tools/UploadedFileTools.cs#L1) | QueryUploadedFile |
| [HealthTools.cs](../src/Dashboard/AI/Tools/HealthTools.cs#L1) | GetAzureServiceHealth |
| [WebFetchTools.cs](../src/Dashboard/AI/Tools/WebFetchTools.cs#L1) | FetchPublicWebPage |
| [HtmlPresentationTools.cs](../src/Dashboard/AI/Tools/HtmlPresentationTools.cs#L1) | GenerateHtmlPresentation |
| [MaturityReportTools.cs](../src/Dashboard/AI/Tools/MaturityReportTools.cs#L1) | GenerateMaturityReport |
| [LogAnalyticsQueryTools.cs](../src/Dashboard/AI/Tools/LogAnalyticsQueryTools.cs#L1) | QueryLogAnalytics |
| [StorageQueryTools.cs](../src/Dashboard/AI/Tools/StorageQueryTools.cs#L1) | ListCostExportBlobs, ReadCostExportBlob |
| [AnomalyTools.cs](../src/Dashboard/AI/Tools/AnomalyTools.cs#L1) | DetectCostAnomalies |
| [PricesheetTools.cs](../src/Dashboard/AI/Tools/PricesheetTools.cs#L1) | StartPricesheetDownload, GetPricesheetStatus |
| [IdleResourceTools.cs](../src/Dashboard/AI/Tools/IdleResourceTools.cs#L1) | FindIdleResources |
| [FaqTools.cs](../src/Dashboard/AI/Tools/FaqTools.cs#L1) | PublishFAQ |

## Prompt Sources

| Layer | Source and responsibility |
| --- | --- |
| System prompt | [CopilotSessionFactory.cs](../src/Dashboard/AI/CopilotSessionFactory.cs#L28): `SystemPrompt` replaces the default CLI system message on both create and resume. Its sections are outlined below. |
| Tool prompts | The 25 declaration sources above supply each function's description, parameter metadata and nested input examples. |
| Connection and file context | [ChatEndpoints.cs](../src/Dashboard/AI/ChatEndpoints.cs#L248): connected APIs, discovered subscription/management-group scopes, upload IDs and schemas, and file-evidence guidance. Re-sent when the context changes; no bearer tokens. |
| Greeting directive | [ChatEndpoints.cs](../src/Dashboard/AI/ChatEndpoints.cs#L78): one short sentence and no tools for recognized standalone greetings. Substantive follow-ups retain tools. |
| Tool-use hook | [RuntimePolicy.cs](../src/Dashboard/AI/RuntimePolicy.cs#L1): reinforces credential exclusion and host-registered operations; code also enforces the custom-tool allowlist. |
| Scheduled-run prefix | [JobScheduler.cs](../src/Dashboard/Jobs/JobScheduler.cs#L320): objective, run number, cadence, latest bounded result, fresh evidence for the entire scope, and mandatory ReportJobOutcome. |
| Scheduled compaction | [JobScheduler.cs](../src/Dashboard/Jobs/JobScheduler.cs#L263): retain objective, declared scope, blockers and latest evidence; past answers are not fresh evidence and credentials must not persist. |
| Sidebar starters | [sidebarCategories.js](../src/Dashboard/frontend/src/data/sidebarCategories.js#L1): all public/connected pricing prompts, maturity questions and grouped starter prompts. These are user-turn templates, not system instructions. |
| Job templates and quick actions | [ChatView.vue](../src/Dashboard/frontend/src/components/ChatView.vue#L3741): nine editable scheduled templates plus generated user-turn actions. Templates cannot approve writes or grant permissions. |
| Dynamic next-action prompts | SuggestFollowUp supplies `label`/`prompt` pairs; the system prompt also defines public `prompt:` links. They become user turns only when selected. |

The nine scheduled templates are Check capacity of X (15 minutes), Reserve X when available (15 minutes), 1-min test (1 minute), Daily cost digest (daily), Anomaly watch (hourly), Budget guard (daily), Idle resource sweep (weekly), Advisor watch (daily), and Retry last question (hourly). Saved jobs may use a custom cadence; the user's saved prompt is appended to the scheduled-run prefix.

## System Prompt Sections

| Section | Main contract |
| --- | --- |
| Top-priority routing | Score only explicit maturity requests; named files and specific resource questions take precedence. |
| Core rules | Direct complete-code GenerateScript calls, no progress narration, one visual, connection-context reuse, secret exclusion, and targeted upload queries without redundant preview. |
| Public pricing fast path | One lookup or one batch; reuse results and permit only bounded refinement/fallback for unresolved components. |
| Response shape | Short evidence-based headline and one visual; detailed full data belongs in a downloadable report. |
| Ambiguous affirmatives | Bind yes/proceed to a single most recent offer; clarify multiple options. |
| Evidence and bounded recovery | Tenant data first, then appropriate metrics/public sources; distinguish missing, stale, denied and partial evidence. |
| Speed | Parallelize independent calls except Cost Management query/forecast; filter and aggregate before limiting, only with supported API options. |
| Large data strategy | Source-shaped summaries, bounded file queries, then targeted drill-down. |
| Commitment-reconciled right-sizing | Reconcile Advisor recommendations with reservations and savings plans; distinguish gross from net savings. |
| Anomaly/change correlation | Pair anomalous cost windows with Resource Graph resource changes. |
| Policy evidence | Assignment-name matches do not prove effective policy or allocation eligibility. |
| Budget setup | Interview for owner, expected changes, budget type and one-time costs before proposing a budget. |
| Savings ledger | Script delivery remains proposed; executed requires confirmed application, verified requires measured savings. |
| Scheduled reports | Native Cost Management scheduled-action proposals, with recipient/cadence and UI approval. |
| Mutations | Exact approved PUT/PATCH proposals, read-only POST allowlist, blocked DELETE/action POSTs, reviewed scripts for destructive work. |
| Compute and Spot evidence | Keep catalogue, quota, placement, eviction history, price, prior deployment and policy distinct; preserve all-region requests and unknowns. |
| Bounded FinOps operations | Scope once, batch same-shape requests, track pending operations and never repeat uncertain writes blindly. |
| Maturity scoring | Crawl is one consolidated tool call; other levels use evidence and explicit observed/unknown/notApplicable scoring. |

## Query Payload Rules

| Surface | Required source shaping |
| --- | --- |
| ARM list APIs | `$filter`, `$select`, and a small `$top` whenever supported. |
| Azure Resource Graph | `where`, `summarize` or narrow `project`, then `top`/`take`; one query pipeline. |
| Cost Management | Dataset filters, aggregation and requested grouping over bounded dates; totals need not have grouping. Never compute a full total from top-N detail. |
| Microsoft Graph | Only supported `$filter`, `$select`, `$top` or report/count operations; follow needed `@odata.nextLink` pages and disclose partial coverage. |
| Log Analytics / App Insights | Time/resource `where`, then `summarize`, narrow `project` and bounded `top`/`take`; retain finer bins when the question requires them. |
| Blob listings | Exact account/container and the narrowest known name/date prefix; maximum 50 names. |
| Uploaded files | In `query` mode, apply predicates, grouping/aggregates and sort, then `columns` projection and paging. Select 1-50 distinct output names, including aggregate aliases. Retain counts, totals and coverage. |
| Savings ledger | Filter by status/category/literal scope substring on the host, aggregate all matching entries, then page details. Use limit 0 for totals only, otherwise 1-200. Not a source-side Azure query. |
| Public web | Specific authoritative URL plus `grepFor` and bounded `maxChars`; these filter the returned context after download. |
| Retail pricing | Typed service/region/SKU/meter filters and bounded `top`; use one batch for independent combinations. |

The API guidance is model-facing, not an automatic query-rewriting engine or a universal payload guarantee. Uploaded-file projection and ledger filtering/paging are enforced host behavior. Some sources have no query controls: blob CSV reads cannot filter rows on the service, and the public health feed has no region parameter. Requested full scans must not silently become top-N samples.

## Filtering Coverage

Every registered tool is covered below. A scoped request can still need substantial upstream data; only explicit source API filters reduce service-side rows. Host projection, pagination and renderer input shaping reduce returned context instead.

| Surface | Tools | Filtering or payload contract |
| --- | --- | --- |
| ARM | `QueryAzure`, `BulkAzureRequest` | Supported filters/projections only; scoped Resource Graph alternatives; aggregate before limits. Every batch read has its own scope. |
| Cross-subscription totals | `QueryCostsAcrossSubscriptions` | Requested dates and full requested scope. No sampled subscriptions or filtered-budget substitutes for whole-estate totals. |
| Directory | `QueryGraph` | Supported OData fields/filters/pages or reports/counts; no invented options on unsupported endpoints. |
| Telemetry | `QueryLogAnalytics` | Time/resource predicates, aggregate, narrow fields, then detail limits. |
| Retail | `GetAzureRetailPricing`, `GetAzureRetailPricingBatch` | Service/SKU/region/type and evidenced vocabulary filters; reuse combinations and preserve resolution/coverage. |
| Compute | `CheckComputeFeasibility` | Exact requested SKUs/scopes/regions; preserve explicit all-region requests and incomplete catalogue evidence. |
| Connectivity | `CheckVmConnectivity` | One existing source VM, Network Watcher, destination and TCP port. No agent-host or broad network scan. |
| Waste | `FindIdleResources` | Requested subscriptions and source-limited patterns. For one pattern/RG, use a targeted Resource Graph query instead of eight scans. |
| Anomalies | `DetectCostAnomalies` | Requested subscription and bounded baseline window. Reuse returned contributors; one named service/RG requires a scoped cost query. |
| Crawl | `GetCrawlMaturityEvidence` | Host-aggregated evidence across all seven dimensions and every requested subscription; no raw-inventory follow-up. |
| Uploads | `QueryUploadedFile` | Row predicates, aggregates, sort, validated column projection and paging while preserving complete totals. |
| Blob exports | `ListCostExportBlobs`, `ReadCostExportBlob` | Prefix-filtered discovery then one relevant blob. CSV response truncation is not a filtered full-export total. |
| Contract rates | `StartPricesheetDownload`, `GetPricesheetStatus` | One billing scope and one returned operation; no per-SKU export filter. Analyze selected rates through an uploaded file. |
| Public sources | `GetAzureServiceHealth`, `FetchPublicWebPage` | Reuse one fixed feed; use specific web URLs and post-download text selection. No unsupported region filters or download-limit claims. |
| Operations | `GetOperationStatus`, `ListOperationResults` | Exact opaque-ID lookup or one owner/conversation-scoped list of up to 100 operations. Reuse terminal states. |
| Ledger | `GetSavingsLedger`, `RecordSavingsAction`, `UpdateSavingsAction` | Filter and page reads; write one exact evidenced action or known entry ID. Totals are computed before pagination. |
| Scoring/history | `ReportMaturityScore`, `GetScoreHistory` | All requested level dimensions with concise evidence; level-filtered retained history, not repeated reads by date. |
| Calculator | `EstimateTokenCost` | Only requested models/variants and compatible selected rates, not a whole price catalogue. |
| Charts | `RenderChart`, `RenderAdvancedChart` | Already filtered aggregates and only needed series/display fields; preserve top-N caveats and units. |
| Reports | `GenerateDataReport`, `GenerateHtmlPresentation`, `GenerateMaturityReport` | Only relevant fields and aggregates, but retain all explicitly requested rows, findings or capabilities. Renderers cannot recover omitted data. |
| Code | `GenerateScript` | Complete code for the requested scope; generated queries use supported source filters. Packaging never executes code. |
| Follow-ups | `SuggestFollowUp` | Concrete target/action/scope, no embedded raw results or transcripts. |
| Scheduled outcomes | `ReportJobOutcome` | Concise scoped outcome and exact evidence tool names; never drop failed scopes to claim success. |
| Public publication | `PublishFAQ` | Only concise, verified public facts for one question; no tenant data or tool-result dumps. |

### New Input Examples

For `QueryUploadedFile` with `mode='query'`, pass this object serialized as `paramsJson` (using columns that exist in the selected file):

```json
{
	"filters": [{"column": "category", "op": "eq", "value": "AI"}],
	"group_by": ["month"],
	"aggregates": [{"column": "cost", "op": "sum", "as": "total"}],
	"columns": ["month", "total"],
	"limit": 20
}
```

`columns` is applied after filtering/aggregation/sorting. Unknown, duplicate, empty or excessive projection lists are rejected. Returned totals cover the complete filtered set even when the output is paged.

For a ledger summary without entry bodies:

```json
{"category":"cleanup","status":"proposed","limit":"0"}
```

For bounded ledger detail, supply `limit` and then the returned `nextOffset` as `offset`. `scopeContains` is a case-insensitive literal substring of the stored action scope, never an authorization filter. `totalsComplete=true` describes all matching totals; `complete` describes whether the current response contains every matching detail entry. Empty filters return all categories/statuses within the owner boundary. Each call reads the current ledger; pages are not frozen snapshots across concurrent updates.

## Maintainer Prompts

These 14 VS Code runbooks are separate from the deployed agent's system prompt and tool declarations:

- Local work: [debug-local.prompt.md](../.github/prompts/debug-local.prompt.md#L1), [debug-local-docker.prompt.md](../.github/prompts/debug-local-docker.prompt.md#L1), [check-code-changes.prompt.md](../.github/prompts/check-code-changes.prompt.md#L1).
- Validation: [safety-check.prompt.md](../.github/prompts/safety-check.prompt.md#L1), [security-audit.prompt.md](../.github/prompts/security-audit.prompt.md#L1), [ui-test.prompt.md](../.github/prompts/ui-test.prompt.md#L1), [time-test.prompt.md](../.github/prompts/time-test.prompt.md#L1).
- Release/deployment: [deploy.prompt.md](../.github/prompts/deploy.prompt.md#L1), [migrate-tenant.prompt.md](../.github/prompts/migrate-tenant.prompt.md#L1), [release.prompt.md](../.github/prompts/release.prompt.md#L1).
- Maintenance: [metadata.prompt.md](../.github/prompts/metadata.prompt.md#L1), [refresh.prompt.md](../.github/prompts/refresh.prompt.md#L1), [update-apis.prompt.md](../.github/prompts/update-apis.prompt.md#L1), [investigate-logs.prompt.md](../.github/prompts/investigate-logs.prompt.md#L1).

Repository development rules live in [copilot-instructions.md](../.github/copilot-instructions.md#L1). Metadata and artifact tests live in [RuntimePolicyTests.cs](../tests/Dashboard.Tests/RuntimePolicyTests.cs#L1) and [ArtifactTests.cs](../tests/Dashboard.Tests/ArtifactTests.cs#L1). Host filtering/projection tests live in [SavingsLedgerTests.cs](../tests/Dashboard.Tests/SavingsLedgerTests.cs#L1), [FileQueryContractTests.cs](../tests/Dashboard.Tests/FileQueryContractTests.cs#L1), and [test_file_inspect.py](../tests/test_file_inspect.py#L1). These credential-free tests validate contracts and retained totals, not live model selection, latency or production payload reduction.
