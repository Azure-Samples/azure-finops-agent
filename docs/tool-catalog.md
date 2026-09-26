# Agent Tool Catalog

This catalog covers all 21 application tools registered by [CopilotSessionFactory.cs](../src/Dashboard/AI/CopilotSessionFactory.cs#L1), their model-facing inputs, and the application's prompt sources as of 2026-09-25. The linked declarations below contain the complete descriptions and JSON examples; source remains authoritative.

## Metadata Model

- **18 direct registrations** have no `defer` property. The always-registered evidence surface now includes `QueryAzure`, which covers ARM, Microsoft Graph, Log Analytics/Application Insights, Blob Storage exports, the Azure Retail Prices API, official documentation/specs and other public HTTPS pages.
- **3 Auto-defer registrations** carry `AdditionalProperties["defer"] = CopilotToolDefer.Auto`: `GenerateHtmlPresentation`, `GenerateMaturityReport` and `PublishFAQ`. This is declaration metadata, not proof of on-demand discovery or a latency saving: [RuntimePolicy.cs](../src/Dashboard/AI/RuntimePolicy.cs#L1) explicitly disables tool search.
- `AIFunctionFactory.Create` publishes `Name`, `Description`, inferred `JsonSchema`, and parameter-level `[Description]` text. JSON bags such as `queryJson`, `paramsJson` and `slidesJson` are strings in that schema; their inner contracts are described in the tool metadata and validated by their implementations.
- [ProtectedTool.cs](../src/Dashboard/AI/Tools/ProtectedTool.cs#L1) binds each callback to the owner, session and SDK invocation, screens arguments, holds cancellation leases, records evidence, redacts returns, retains successful evidence JSON over 32 KiB, and applies `QueryAzure.resultQuery` to the complete retained result when supplied. ARM write proposals and approval dispatch are enforced by the HTTP/operation layer, not by descriptions alone.
- Public means no delegated Azure-service token is required, not an unauthenticated tool endpoint. Tools still run inside an application session. Owner IDs, tokens and cancellation tokens are host-supplied, never model parameters.
- Direct availability never grants arbitrary host execution. `GenerateScript` packages code with credential redaction as a 24-hour owner-bound artifact; it never executes the script. Built-in shell, filesystem, MCP, cross-session memory, and logged-in CLI tools remain disabled.

Inputs below are strings unless `int`, `double`, or `bool` is shown. `=value` is a C# default; `?` denotes nullable, not necessarily optional in the emitted schema. Cancellation tokens are omitted because the host supplies them.

## Direct Registrations (18)

| Tool | Credential / scope | Inputs | Model-facing prompt contract |
| --- | --- | --- | --- |
| `RenderChart` | Public | `type`, `title`, `seriesName`, `data`, `xAxisName=null`, `yAxisName=null` | Render one bounded ECharts bar, horizontal bar, line, pie, scatter, funnel, or race chart. |
| `RenderAdvancedChart` | Public | `options` | Static ECharts JSON for maps and advanced charts, at most 100,000 characters; DOM, HTML, links, and remote-image sinks are rejected. |
| `SuggestFollowUp` | Session | `label`, `prompt`, `label2=null`, `prompt2=null`, `label3=null`, `prompt3=null` | Emit one to three concrete clickable next actions; labels at most 60 characters. Public fast-path turns use a prompt link instead. |
| `EstimateTokenCost` | Public | `modelsJson`, `inputTokensPerConversation`, `outputTokensPerConversation`, `conversationsPerMonth`, `cachedInputTokensPerConversation=null`, `currency=null` | Calculate reconciled costs for 1-20 models from per-million-token rates; effective cached tokens 0 and currency USD. Unknown rates are not zero. |
| `CalculateCost` | Public | `lineItemsJson`, `currency`, `period`, `discountPercent=0`, `taxPercent=0` | Decimal arithmetic for 1-50 explicit line items. Lines inherit the required top-level currency when omitted; an explicit blank, invalid or different currency is rejected. |
| `CompareAmounts` | Public | `itemsJson`, `unit`, `context=null` | Compare 1-50 verified decimal amounts in the same unit. Labels, full digits and assumptions must already be supplied. |
| `GenerateScript` | Owner-bound artifact | `scriptContent`, `filename=null`, `language=null`, `description=null` | For an explicit Azure CLI or PowerShell code request, submit the complete executable code directly in this call; package it without executing it. |
| `ReportJobOutcome` | Scheduled turn only | `status`, `summary`, `evidenceToolsJson`, `dataAsOfUtc=null`, `nextEligibleRunUtc=null` | One terminal outcome: completed, unchanged, goal_achieved, blocked, partial or failed. Successful outcomes require host-validated evidence. |
| `GenerateDataReport` | Owner-bound artifact | `format`, `dataJson`, `filename=null` | Create CSV, XLSX, or filterable HTML from `{title,source,sheets:[{name,columns,rows,sourceRowCount}]}`; maximum 5,000 rows, 50 columns and 10 sheets. |
| `QueryToolResult` | Owner + conversation bound | `resultId`, `queryJson={}` | Query retained large JSON by discovered schema. Modes are `query`, `schema` and `keys`; aliases include `filter`→`where`, `fields`/`project`→`select`, `orderBy`/`order`→`sort`, `top`/`take`→`limit`, and `skip`→`offset`. Columnar table rows are addressable by column name and position. |
| `ReportMaturityScore` | Owner-bound state | `level`, `scores` | Persist evidence-backed Crawl/Walk/Run/Playbook dimensions; unknown and not-applicable scores stay null. |
| `GetScoreHistory` | Owner-bound state | `level=null` | Return up to 100 persisted maturity assessments, optionally filtered by level. |
| `QueryAzure` | Host-routed delegated or public HTTP | `url=""`, `method=GET`, `body=""`, `requests=""`, `resultQuery=""`, `maxPages=5`, `grepFor=""`, `parallelism=20` | The one model-authored HTTP evidence tool. Exactly one of `url` or `requests` is required. A leading `/` is an ARM path; exact hosts select ARM, Graph, Log Analytics/Application Insights, Blob Storage, Retail Prices or public credential-free GET. URLs must be HTTPS with no userinfo, fragment, custom port, backslash, control characters or `//` prefix, and errors do not echo the URL. CSV/XML success bodies become JSON. Batch `requests` accepts 1-200 `{method,url|path,body}` objects and runs cost query/forecast sequentially. `resultQuery` applies a `QueryToolResult` query to the complete retained result in the same call; invalid queries return the schema/annotation without repeating the request. ARM DELETE is blocked, PUT/PATCH create approval proposals, and POST is allowlisted for read-only query/report/calculation/diagnostic endpoints including PolicyInsights policy-state summarize/queryResults and policy-event queryResults at subscription, resource-group or management-group scope (never `triggerEvaluation`) and validated Network Watcher `connectivityCheck` from an existing VM. Paginated ARM/Graph/Retail GETs follow same-origin links up to `maxPages`. When ARM rejects a read's api-version (`InvalidResourceType`, `NoRegisteredProviderFound` or an `*ApiVersion*` code) and lists supported versions, a GET or allowlisted POST is retried once with the newest listed stable version (newest preview when none is stable). When a provider returns `UnsupportedApiVersion` without a list, the host reads that provider's manifest (`/providers/{namespace}?api-version=2021-04-01` at the path's subscription or tenant scope) and tries at most two older stable versions of the addressed resource type, newest first. A successful body carries a root `_apiVersion` note; otherwise the original error is returned. Batch `requests`, read-only POST bodies and `resultQuery` pass through a structural JSON repair that closes unbalanced containers, drops stray closers, splits a sibling object misplaced inside the previous one, and drops only bracket/identifier noise after the complete root; an unterminated string or a non-container root is still rejected. A subscription-prefixed Resource Graph path whose subscription is already listed in the body's `subscriptions` array is sent to `/providers/Microsoft.ResourceGraph/resources`. Dropped connections are retried up to twice for GETs only; POST, PUT and PATCH are never re-sent. A Cost Management `/query` body's `Dimension` grouping named `Currency`, which the service rejects although every row already carries a Currency column, is removed before dispatch and reported in a root `_request` note; a `TagKey` grouping named Currency is kept. Public pages use the SSRF-guarded reader and `grepFor` for long pages. |
| `GetOperationStatus` | Owner-bound ARM operation | `operationId=""` | Poll only the host-stored URL for one approved mutation, accepted diagnostic or report download; accepted and in-progress are not success. With an empty `operationId`, list up to 100 recent owner-bound operations in the current conversation, including partial prerequisites for reviewed cleanup scripts. |
| `RecordSavingsAction` | Owner-bound state | `title`, `category`, `estimatedMonthlyUsd`, `scope`, `status` | Record evidenced remediation. Script delivery/pending writes are proposed; executed requires confirmed application. |
| `UpdateSavingsAction` | Owner-bound state | `id`, `status`, `verifiedMonthlyUsd=""` | Advance an entry to proposed/executed/verified/dismissed; omitted or empty `verifiedMonthlyUsd` preserves the prior value. |
| `GetSavingsLedger` | Owner-bound state | `status=null`, `category=null`, `scopeContains=null`, `limit=50`, `offset=0` | Host-side filters and newest-first paging; default 50, max 200 details, or limit 0 for totals only. Totals cover all matches before paging. |
| `QueryUploadedFile` | Conversation-bound upload | `fileId`, `mode`, `paramsJson=null` | Registered upload only; query mode supports filters, groups, aggregates, sorting, output columns (1-50), offset and limit. Parameterless modes such as `workbook` need no argument bag. |

## Auto-Defer Registrations (3)

| Tool | Credential / scope | Inputs | Model-facing prompt contract |
| --- | --- | --- | --- |
| `GenerateHtmlPresentation` | Owner-bound artifact | `slidesJson`, `filename=null`, `customer=null` | Structured slides: title, section, kpi, chart, content, two_column, maturity, alerts, table, roadmap or closing. |
| `GenerateMaturityReport` | Owner-bound artifact | `reportJson`, `filename=null`, `customer=null` | Evidence-based scrolling HTML assessment, up to 19 capabilities across four FinOps domains. |
| `PublishFAQ` | Azure-connected user | `question`, `answer`, `title` | Publish or queue a public FinOps Q&A only; tenant-specific data is prohibited. |

## Evidence, Arithmetic And Publication

- `QueryAzure` is the single HTTP evidence tool. It returns raw API JSON as-is, converts successful CSV to columnar JSON and XML to JSON, and routes by exact host: delegated ARM, Graph, Log Analytics/Application Insights and Storage tokens where appropriate, Retail Prices without credentials, and other public HTTPS GETs without credentials.
- Public web reads use [PublicWebReader.cs](../src/Dashboard/AI/Tools/PublicWebReader.cs#L1): literal private/loopback/link-local/metadata hosts are blocked before DNS, connect-time DNS allows only public IPs on every redirect, no proxy/cookies/auth are used, HTML is stripped to text, JSON is retained, XML/CSV are converted to JSON, and unavailable pages are explicit failures. `learn.microsoft.com/api/search` is canonicalized with `locale=en-us`.
- Retail price requests are `QueryAzure` GETs to `https://prices.azure.com/api/retail/prices` with a model-authored OData `$filter`, optional `currencyCode` and bounded `maxPages`. The host drops `$top`/`$skip`, defaults `api-version` and USD, follows same-origin `NextPageLink`, retries 429/5xx, and returns raw `Items`. Zero items means no match, not a zero price.
- Microsoft Graph report CSV is converted to columnar JSON. A report function's HTTP 404 `UnknownTenantId` is returned as a determinate `reportServiceProvisioned=false` result, not a transport failure. Retrieval time is not the report refresh date. Every Graph request, including pagination, carries `ConsistencyLevel: eventual`, so directory advanced queries (`$count=true`, `$search`, `assignedLicenses/$count`, `ne`/`not`/`endsWith` filters) work; simple reads are unaffected.
- Forecasts retain their daily date/status/currency coverage. A whole-month forecast can include actual rows with top-level `includeActualCost=true`; that flag and `includeFreshPartialCost` belong beside `dataset`, never in `dataset.configuration`.
- `CalculateCost` and `CompareAmounts` establish arithmetic only. They do not prove a price is valid, current, deployable, or paid. Copy complete verified numbers, dates, units and currencies from evidence.
- Maturity scoring has no host-built Crawl evidence bundle. For Crawl, Walk, Run and Playbook, the model gathers scoped evidence with the general tools, keeps source freshness/coverage in the answer, calls `ReportMaturityScore` once with observed/null/not-applicable dimensions, and uses raw Advisor, Resource Graph, Policy, Budget, Monitor or Cost Management evidence where needed.
- `PublishFAQ` runs only for an explicit request to publish or submit public content, never as an automatic background action after an ordinary answer. Existing authentication and moderation remain in force; pending review is not publication.

## Schema-First Large Results

The SDK's automatic temporary-file substitution is disabled on create and resume: built-in filesystem readers remain disabled. Protected read tools can retain JSON larger than 32 KiB and return `kind=queryable_tool_result`, an opaque `resultId`, source metadata, expiry, SHA-256 and a discovered schema. The complete **redacted** response is retained unchanged in process memory, never exposed by a model-supplied path. Identity and conversation must both match on every query. Results expire after 30 minutes and do not survive a host restart.

Retained-result IDs use compact 22-character Base64url encoding of 128 cryptographically random bits. They are references, not content hashes or authorization credentials. Source annotations and query responses expose the exact case-sensitive handle for reuse; lookup never abbreviates, normalizes or searches for a similar ID.

Retention is limited to 16 MiB per result, 32 MiB per owner, 128 MiB of payload globally and 4096 entries. If retention is unavailable or the result is not a JSON object/array, the original redacted result stays inline; no fake handle or silent truncation is substituted. Chart, score, generated artifact and approval/operation messages remain inline; results containing an `operationId` are never reshaped away from the UI. Original HTTP/timestamp preambles, success/freshness/partial flags and retrieval time stay attached; local queries are not new source reads.

Smaller successful evidence objects are also retained and stay inline with one inserted root property, `_resultQuery` (`resultId`, `queryTool`, `expiresUtc`). It is an annotation, not source data, and keeps every existing JSON consumer valid. Large failed batches return a retained description with failed indexed items while preserving successful items for diagnosis.

`QueryAzure.resultQuery` accepts the same query JSON as `QueryToolResult` and is applied after the complete result is retained. Use it when the shape is already known to return only needed rows, fields, groups or totals in the same tool call. If it is invalid, the host returns the complete schema/annotation with `resultQueryProblem` or `_resultQuery.problem`; it does not repeat the HTTP request.

`queryJson` is a JSON object with `mode` (`query`, `schema`, or `keys`) and a JSONPath `path` (default `$`). `keys` lists property names of selected objects, useful for large OpenAPI specs (for example `path: $.paths`). Common aliases are accepted and canonicalized: `filter`→`where`, `orderBy`/`order`→`sort`, `top`/`take`→`limit`, `skip`→`offset`, and `fields`/`project`→`select`. Unknown or duplicate keys are rejected.

Schema discovery reports fields with paths, types, observation counts, min/max array lengths and an `example` for scalar values. It also reports `tables` for columnar data (`rowsPath`, `rowCount`, `columns`, `usage`). Cost Management, Log Analytics and converted CSV rows can be addressed by column name (`$.Cost`) or original position (`$[0]`). Relative row paths such as `name`, `.name`, `@.name` and `[0]` normalize to the current row.

`select` may be a mapping or an array of paths named by their last segment. `where` is ANDed across up to 12 conditions; positive operators pass if any matched value satisfies the predicate, while `ne` and `notIn` require every matched value to be unequal or outside the set. `groupBy` maps up to 12 scalar keys. `queryJson` (and `QueryAzure`'s `resultQuery`) may instead be an array of up to 8 query objects; the answers return in order as `queries[i]` with the handle copied once, and any invalid query fails the whole call with its index rather than hiding the error. A query path that matches nothing returns a `note` naming the retained root's top-level keys, and a schema view of a sub-selection adds `queryPaths` explaining that queries still start at the retained root. When every `groupBy` path and every non-count aggregate path runs through the same array wildcard (for example `$.body.value[*].sku` after `path` `$.results[*]` and a `where` on `$.index`), rows become that array's elements, paths are evaluated relative to each element, and a `note` says counts are element counts; mixed paths still fail with a hint to move the wildcard into `path`. If `groupBy` is present and `aggregates` is omitted, each group is counted by default; an explicit empty `aggregates` array is still an error. Ungrouped aggregates are always available in `totals`, including when `limit=0`; grouped values, especially different currencies, are never combined into an unrequested grand total.

Every query returns `totalMatches`, `totalResults`, `returned`, `complete`, and `nextOffset`. These describe the **local view**, not Azure coverage. Default limit is 50, maximum 200; negative offset clamps to 0, negative limit is served as a 200-row page, and offset is capped at 100000. A 16 KiB row-output budget can shorten a page; a single oversized row yields `requiresProjection=true` rather than silently dropping fields. Source-side filtering and aggregation remain the preferred way to reduce downloads.

## Cost Detail And Retry Behavior

The primary agent model defaults to `gpt-6-luna` version `2026-09-22`. The tool contracts and permission boundaries do not change with the model. An existing-account configuration requires a deployed model and account-scoped inference access; it does not create quota or move a deployment.

Cost Management supports at most two grouping dimensions. Resource detail at a management group uses SubscriptionId plus ResourceId; subscription-level resource/model detail can use ResourceId plus Meter. Derive resource-group labels from the resource ID instead of adding another grouping. Detailed billing requests do not require a preliminary totals-only call. When billing detail is unavailable, say so without repeatedly substituting subscription totals, current inventory or token activity.

The host retains per-tenant cost-query serialization and one final block per turn. It waits for the longest service retry deadline and automatically retries a cost query once when the wait is at most five minutes. Without a retry header, the delay is 60 seconds. A new request may wait for an existing short cooldown before sending; a same-turn final block cannot be bypassed. Longer deadlines are not shortened. Stop cancels both network and waiting work.

For grouped details across two or more known scopes, use one `QueryAzure` `requests` batch with `parallelism="1"` instead of a model round-trip for every scope. The host enforces sequential execution for cost query/forecast even if a higher value is supplied and returns indexed unattempted entries after a final cost 429. Preserve the requested scopes, exact dates, cost type, filters and grouping; do not combine a speculative management-group partial result with subscription-level detail unless the answer says exactly how coverage differs.

Negotiated pricesheets now start with `QueryAzure` POST to `{billingScope}/providers/Microsoft.CostManagement/pricesheets/default/download`. The response returns an owner-bound `operationId`; poll it with `GetOperationStatus`. Credential-bearing download URLs must never be passed as `url`. Analyze a downloaded pricesheet only after it is uploaded and registered, using `QueryUploadedFile`.

Network Watcher `connectivityCheck` is an allowlisted `QueryAzure` ARM POST only when the body validates a probe from an existing VM resource ID to one destination address or resourceId and a TCP port from 1-65535. It is a diagnostic read of connectivity state, not an agent-host probe or broad scan.

One simple next question can be an existing `prompt:` link in the final answer; a separate `SuggestFollowUp` round-trip is not required. Structured multiple actions still use that tool. Deliver the primary answer and visual before optional follow-up work.

Service totals and resource/meter details must be reconciled using the same scope, dates, currency, cost type, filters and compatible source coverage. Matching dates alone do not prove matching billing snapshots. If raw source results still differ, disclose the unresolved amount rather than inventing its cause or silently dropping it.

Cooldown SSE includes `retryAtUtc` and `willRetry`: true means this request is waiting to continue, false means no automatic retry remains for it. The UI shows that status in the chat on mobile and desktop. Both direct timestamped responses and consolidated `sourceEvidence` retain the deadline.

See the [Cost Query API](https://learn.microsoft.com/rest/api/cost-management/query/usage) for its grouping and retry contracts. Successful synthetic tests do not guarantee future billing-service capacity or exact model cost attribution.

## Complete Metadata Sources

These links expose the full tool descriptions, parameter descriptions, nested JSON examples, defaults, and implementation validations, rather than a second hand-maintained copy of each long prompt.

| Source | Tools |
| --- | --- |
| [ChartTools.cs](../src/Dashboard/AI/Tools/ChartTools.cs#L1) | RenderChart, RenderAdvancedChart |
| [FollowUpTools.cs](../src/Dashboard/AI/Tools/FollowUpTools.cs#L1) | SuggestFollowUp |
| [CostEstimateTools.cs](../src/Dashboard/AI/Tools/CostEstimateTools.cs#L1) | EstimateTokenCost |
| [CostCalculationTools.cs](../src/Dashboard/AI/Tools/CostCalculationTools.cs#L1) | CalculateCost, CompareAmounts |
| [ScriptTools.cs](../src/Dashboard/AI/Tools/ScriptTools.cs#L1) | GenerateScript |
| [JobRunOutcome.cs](../src/Dashboard/Jobs/JobRunOutcome.cs#L1) | ReportJobOutcome |
| [ReportTools.cs](../src/Dashboard/AI/Tools/ReportTools.cs#L1) | GenerateDataReport |
| [ToolResultQueryTools.cs](../src/Dashboard/AI/Tools/ToolResultQueryTools.cs#L1) | QueryToolResult |
| [ScoreTools.cs](../src/Dashboard/AI/Tools/ScoreTools.cs#L1) | ReportMaturityScore, GetScoreHistory |
| [AzureQueryTools.cs](../src/Dashboard/AI/Tools/AzureQueryTools.cs#L1), [PublicWebReader.cs](../src/Dashboard/AI/Tools/PublicWebReader.cs#L1), [ResponseShaper.cs](../src/Dashboard/Infrastructure/ResponseShaper.cs#L1) | QueryAzure and its public-web / CSV / XML shaping helpers |
| [OperationTools.cs](../src/Dashboard/AI/Tools/OperationTools.cs#L1) | GetOperationStatus |
| [SavingsLedgerTools.cs](../src/Dashboard/AI/Tools/SavingsLedgerTools.cs#L1) | RecordSavingsAction, UpdateSavingsAction, GetSavingsLedger |
| [UploadedFileTools.cs](../src/Dashboard/AI/Tools/UploadedFileTools.cs#L1) | QueryUploadedFile |
| [HtmlPresentationTools.cs](../src/Dashboard/AI/Tools/HtmlPresentationTools.cs#L1) | GenerateHtmlPresentation |
| [MaturityReportTools.cs](../src/Dashboard/AI/Tools/MaturityReportTools.cs#L1) | GenerateMaturityReport |
| [FaqTools.cs](../src/Dashboard/AI/Tools/FaqTools.cs#L1) | PublishFAQ |
## Prompt Sources

| Layer | Source and responsibility |
| --- | --- |
| System prompt | [CopilotSessionFactory.cs](../src/Dashboard/AI/CopilotSessionFactory.cs#L28): `SystemPrompt` replaces the default CLI system message on both create and resume. Its sections are outlined below. |
| Tool prompts | The 21 declaration sources above supply each function's description, parameter metadata and nested input examples. |
| Connection and file context | [ChatEndpoints.cs](../src/Dashboard/AI/ChatEndpoints.cs#L248): connected APIs are labelled as `QueryAzure` routes for ARM, Graph, Log Analytics/Application Insights and Blob Storage, plus discovered subscription/management-group scopes, upload IDs and schemas, and file-evidence guidance. Re-sent when the context changes; no bearer tokens. |
| Greeting directive | [ChatEndpoints.cs](../src/Dashboard/AI/ChatEndpoints.cs#L78): one short sentence and no tools for recognized standalone greetings. Substantive follow-ups retain tools. |
| Tool-use hook | [RuntimePolicy.cs](../src/Dashboard/AI/RuntimePolicy.cs#L1): reinforces credential exclusion and host-registered operations; code also enforces the custom-tool allowlist. |
| Scheduled-run prefix | [JobScheduler.cs](../src/Dashboard/Jobs/JobScheduler.cs#L320): objective, run number, cadence, latest bounded result, fresh evidence for the entire scope, and mandatory ReportJobOutcome. |
| Scheduled compaction | [JobScheduler.cs](../src/Dashboard/Jobs/JobScheduler.cs#L263): retain objective, declared scope, blockers and latest evidence; past answers are not fresh evidence and credentials must not persist. |
| Sidebar starters | [sidebarCategories.js](../src/Dashboard/frontend/src/data/sidebarCategories.js#L1): all public/connected pricing prompts, maturity questions and grouped starter prompts. These are user-turn templates, not system instructions. |
| Job templates and quick actions | [ChatView.vue](../src/Dashboard/frontend/src/components/ChatView.vue#L1): scheduled-job templates, run-now/edit/delete prompts, deck/report quick actions and prompt chips. |
| Dynamic next-action prompts | SuggestFollowUp supplies `label`/`prompt` pairs; the system prompt also defines public `prompt:` links. They become user turns only when selected. |

The nine scheduled templates are Check capacity of X (15 minutes), Reserve X when available (15 minutes), 1-min test (1 minute), Daily cost digest (daily), Anomaly watch (hourly), Budget guard (daily), Idle resource sweep (weekly), Advisor watch (daily), and Retry last question (hourly). Saved jobs may use a custom cadence; the user's saved prompt is appended to the scheduled-run prefix.

## System Prompt Sections

| Section | Main contract |
| --- | --- |
| How you work | Investigate like a senior cloud engineer. The model authors every API call with `QueryAzure`, looks up live provider apiVersions and official specs instead of guessing, shapes work at the source, batches similar requests, uses `resultQuery`/`QueryToolResult` for retained results, and reuses evidence already returned this turn. |
| API facts | Preserve service-specific rules for Cost Management, Resource Graph, reservations/savings plans, Compute capacity/Spot, Retail Prices, and Microsoft Graph reports and licensing. |
| Evidence honesty | State scope, dates, ISO currency, cost type and retrieval time separately from source data-as-of time. Missing, denied, failed and unqueried evidence remains unknown, never zero. |
| Answer shape | Use the latest user's language, a short evidence-backed headline, one visual at most, concrete entities and amounts, downloadable reports for large results, and explicit clarification when required inputs are missing. |
| Maturity scoring | Only score explicit maturity or FinOps assessment requests. Gather scoped evidence with general tools, call `ReportMaturityScore` once, and keep unknown or not-applicable dimensions null with reasons. |

## Query Payload Rules

| Surface | Required source shaping |
| --- | --- |
| ARM list APIs | Use `QueryAzure` with an ARM path, live `api-version`, and `$filter`, `$select`, or a small `$top` only where the endpoint supports them. |
| Azure Resource Graph | POST through `QueryAzure` with one explicit nonempty `subscriptions` (GUID strings) or `managementGroups` (ID strings) array. Use `where`, `summarize` and narrow `project`, then `top N by field` or `order by field | take N`; never bare `top N` or implicit tenant-wide scope. |
| Cost Management | Dataset filters, aggregation and at most two grouping dimensions over bounded dates; totals need not have grouping. Start detail requests with the required grouping and never compute a full total from top-N detail. |
| Microsoft Graph | Use a `QueryAzure` Graph URL with only supported `$filter`, `$select`, `$top` or report/count operations; follow needed `@odata.nextLink` pages and disclose partial coverage. Report CSV becomes columnar JSON. |
| Log Analytics / App Insights | Use a `QueryAzure` `/v1/.../query` URL. Filter by time/resource first, then `summarize`, narrow `project` and bounded `top`/`take`; retain finer bins when the question requires them. |
| Blob listings and reads | Exact account/container and the narrowest known name/date prefix; listing bodies can be up to 16 MB, single blob reads use the first 6 MiB range. CSV rows can be partial; request an upload for complete large-export analysis. |
| Retail pricing | One `QueryAzure` Retail Prices URL with model-authored OData `$filter`, `currencyCode`, and bounded `maxPages`; inspect raw returned fields before comparing or calculating. |
| Public web | Specific authoritative HTTPS URL through `QueryAzure`; use `grepFor` for long docs/specs. Public Azure status feed is not tenant-specific. |
| Uploaded files | In `query` mode, apply predicates, grouping/aggregates and sort, then `columns` projection and paging. Select 1-50 distinct output names, including aggregate aliases. Retain counts, totals and coverage. |
| Savings ledger | Filter by status/category/literal scope substring on the host, aggregate all matching entries, then page details. Use limit 0 for totals only, otherwise 1-200. Not a source-side Azure query. |
| Operations | `GetOperationStatus` polls one exact opaque id or lists recent owner/conversation-scoped operations when called with an empty id. |

The API guidance is model-facing, not an automatic query-rewriting engine or a universal payload guarantee. Uploaded-file projection and ledger filtering/paging are enforced host behavior. Some sources have no query controls: blob CSV reads cannot filter rows on the service, and the public health feed has no region parameter. Requested full scans must not silently become top-N samples.

## Filtering Coverage

Every registered tool is covered below. A scoped request can still need substantial upstream data; only explicit source API filters reduce service-side rows. Host projection, pagination and renderer input shaping reduce returned context instead.

Microsoft 365 usage and licensing now use `QueryAzure` Graph calls. The model must choose the supported report route and query options from Learn or Graph metadata, follow needed pages, count and page retained results with `QueryToolResult` when necessary, and keep the report's own refresh date distinct from retrieval time.

Pricing guidance clarifies missing material product configuration, including VM OS/license or service tier, before quotes or rankings unless the user supplied enough context or authorized assumptions. Fixed-region quotes need a region; if the user gives a region and size, quote on-demand list prices with stated defaults rather than blocking on every optional configuration. Complete source coverage within a model-authored filter is not proof that the filter matches the intended scenario.

Retail rows also retain `tierMinimumUnits` and currency. Volume bands are separate variants: a high-volume discount cannot become the default rate for a small dataset. `CalculateCost` consumes only selected, verified quantities and rates; it cannot recover missing price or capacity evidence. Report month/year/scenario changes explicitly and recalculate rather than reusing an incompatible total.

`RenderChart` keeps `type` as the canonical schema field. A deliberate compatibility adapter accepts the observed legacy `chart` key, but documentation, prompts and tests should use `type`.

| Surface | Tools | Filtering or payload contract |
| --- | --- | --- |
| ARM, inventory, Advisor, budgets and policy | `QueryAzure` | Supported filters/projections only; scoped Resource Graph alternatives; aggregate before limits. Every batch read has its own scope. |
| Directory and Microsoft 365 reports | `QueryAzure` | Supported Graph OData fields/filters/pages or reports/counts; no invented options on unsupported endpoints. |
| Telemetry | `QueryAzure` | KQL time/resource predicates, aggregate, narrow fields, then detail limits. |
| Retail | `QueryAzure` | One source-shaped OData filter and bounded pages; raw rows require model-side inspection and retained-result queries for totals/rankings. |
| Compute and capacity | `QueryAzure` | Exact requested SKUs/scopes/regions through Compute usages, skus, Spot placement score POST, Resource Graph SpotResources and related APIs. |
| Connectivity | `QueryAzure` | One validated Network Watcher `connectivityCheck` from an existing VM to a destination and TCP port. No agent-host or broad network scan. |
| Maturity scoring | `ReportMaturityScore`, `GetScoreHistory` | Model-gathered evidence for each dimension; persisted scores keep observed/unknown/not-applicable distinctions and concise evidence. |
| Uploads | `QueryUploadedFile` | Row predicates, aggregates, sort, validated column projection and paging while preserving complete totals. |
| Blob exports | `QueryAzure`, `QueryUploadedFile` | Prefix-filtered discovery, bounded blob reads, then uploaded-file analysis for complete large exports. CSV response truncation is not a filtered full-export total. |
| Contract rates | `QueryAzure`, `GetOperationStatus`, `QueryUploadedFile` | One billing scope starts the pricesheet operation, one owner-bound poll checks it, and selected uploaded rates are analyzed from the file. |
| Public sources | `QueryAzure` | Specific web URLs and post-download text selection. Public Azure status is a feed read, not a tenant-specific resource-health check. |
| Operations | `GetOperationStatus` | Exact opaque-ID lookup or one owner/conversation-scoped list of up to 100 operations. Reuse terminal states. |
| Ledger | `GetSavingsLedger`, `RecordSavingsAction`, `UpdateSavingsAction` | Filter and page reads; write one exact evidenced action or known entry ID. Totals are computed before pagination. |
| Calculator | `EstimateTokenCost`, `CalculateCost`, `CompareAmounts` | Only requested models/variants and compatible selected rates, not a whole price catalogue; deterministic arithmetic over verified inputs. |
| Charts | `RenderChart`, `RenderAdvancedChart` | Already filtered aggregates and only needed series/display fields; preserve top-N caveats and units. |
| Reports | `GenerateDataReport`, `GenerateHtmlPresentation`, `GenerateMaturityReport` | Only relevant fields and aggregates, but retain all explicitly requested rows, findings or capabilities. Renderers cannot recover omitted data. |
| Code | `GenerateScript` | Complete code for the requested scope; generated queries use supported source filters. Packaging never executes code. |
| Follow-ups | `SuggestFollowUp` | Concrete target/action/scope, no embedded raw results or transcripts. |
| Scheduled outcomes | `ReportJobOutcome` | Concise scoped outcome and exact evidence tool names; never drop failed scopes to claim success. |
| Public publication | `PublishFAQ` | Only concise, verified public facts for one question; no tenant data or tool-result dumps. |
### New Input Examples

For `QueryAzure` retail pricing with immediate projection from the complete retained result:

```json
{
  "url": "https://prices.azure.com/api/retail/prices?currencyCode=USD&$filter=serviceName eq 'Virtual Machines' and armRegionName eq 'eastus' and armSkuName eq 'Standard_D2s_v5' and priceType eq 'Consumption'",
  "maxPages": "3",
  "resultQuery": "{\"path\":\"$.Items[*]\",\"select\":[\"$.productName\",\"$.meterName\",\"$.unitPrice\",\"$.unitOfMeasure\",\"$.currencyCode\"],\"limit\":20}"
}
```

For a batched quota read with one row per request in the local query:

```json
{
  "requests": "[{\"method\":\"GET\",\"url\":\"/subscriptions/{subscriptionId}/providers/Microsoft.Compute/locations/{region}/usages?api-version=2024-07-01\"}]",
  "parallelism": "20",
  "resultQuery": "{\"path\":\"$.results[*]\",\"select\":{\"index\":\"$.index\",\"status\":\"$.status\",\"limits\":\"$.body.value[*].limit\"}}"
}
```

For listing recent owner-bound operations in the current conversation:

```json
{ "operationId": "" }
```

For `QueryUploadedFile` with `mode='query'`, pass this object serialized as `paramsJson` (using columns that exist in the selected file):

```json
{
  "filters": [{ "column": "category", "op": "eq", "value": "AI" }],
  "group_by": ["month"],
  "aggregates": [{ "column": "cost", "op": "sum", "as": "total" }],
  "columns": ["month", "total"],
  "limit": 20
}
```

`columns` is applied after filtering/aggregation/sorting. Unknown, duplicate, empty or excessive projection lists are rejected. Returned totals cover the complete filtered set even when the output is paged.

For a ledger summary without entry bodies:

```json
{ "category": "cleanup", "status": "proposed", "limit": "0" }
```

For bounded ledger detail, supply `limit` and then the returned `nextOffset` as `offset`. `scopeContains` is a case-insensitive literal substring of the stored action scope, never an authorization filter. `totalsComplete=true` describes all matching totals; `complete` describes whether the current response contains every matching detail entry. Empty filters return all categories/statuses within the owner boundary. Each call reads the current ledger; pages are not frozen snapshots across concurrent updates.

## Maintainer Prompts

These 15 VS Code runbooks are separate from the deployed agent's system prompt and tool declarations:

- Local work: [debug-local.prompt.md](../.github/prompts/debug-local.prompt.md#L1), [debug-local-docker.prompt.md](../.github/prompts/debug-local-docker.prompt.md#L1), [check-code-changes.prompt.md](../.github/prompts/check-code-changes.prompt.md#L1).
- Validation: [safety-check.prompt.md](../.github/prompts/safety-check.prompt.md#L1), [security-audit.prompt.md](../.github/prompts/security-audit.prompt.md#L1), [ui-test.prompt.md](../.github/prompts/ui-test.prompt.md#L1), [time-test.prompt.md](../.github/prompts/time-test.prompt.md#L1).
- Release/deployment: [deploy.prompt.md](../.github/prompts/deploy.prompt.md#L1), [migrate-tenant.prompt.md](../.github/prompts/migrate-tenant.prompt.md#L1), [release.prompt.md](../.github/prompts/release.prompt.md#L1).
- Maintenance: [metadata.prompt.md](../.github/prompts/metadata.prompt.md#L1), [refresh.prompt.md](../.github/prompts/refresh.prompt.md#L1), [update-apis.prompt.md](../.github/prompts/update-apis.prompt.md#L1), [investigate-logs.prompt.md](../.github/prompts/investigate-logs.prompt.md#L1), [investigate-ai-sessions.prompt.md](../.github/prompts/investigate-ai-sessions.prompt.md#L1).

Keep these prompts aligned with this catalog whenever tool names, registration mode, route validation, result-retention behavior, or UI/SSE contracts change.
