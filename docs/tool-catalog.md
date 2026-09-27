# Agent Tool Catalog

This catalog covers all 16 application tools registered by [AgentSessionFactory.cs](../src/Dashboard/AI/AgentSessionFactory.cs#L1), their model-facing inputs, and the application's prompt sources as of 2026-09-26. The linked declarations contain the complete descriptions and JSON examples; source remains authoritative.

## Metadata Model

- All 16 tools are registered directly on every run through `ChatClientAgentRunOptions`; there is no deferred loading or tool search. The agent itself adds only the optional hosted web search tool (`AzureOpenAI:WebSearch`).
- `AIFunctionFactory.Create` publishes `Name`, `Description`, inferred `JsonSchema`, and parameter-level `[Description]` text. JSON bags such as `paramsJson`, `slidesJson` and report payloads are described in the tool metadata and validated by their implementations.
- [ProtectedTool.cs](../src/Dashboard/AI/Tools/ProtectedTool.cs#L1) binds each callback to the owner, conversation and active turn (call ID from `FunctionInvokingChatClient.CurrentContext`), screens arguments, holds cancellation leases, records evidence, redacts returns, stores successful evidence through [ResultDatabase.cs](../src/Dashboard/Infrastructure/ResultDatabase.cs#L1), and handles local `QueryAzure` SQL when no URL is supplied. ARM write proposals and approval dispatch are enforced by the HTTP/operation layer, not by descriptions alone.
- Public means no delegated Azure-service token is required, not an unauthenticated tool endpoint. Tools still run inside an application session. Owner IDs, tokens and cancellation tokens are host-supplied, never model parameters.
- Direct availability never grants arbitrary host execution. `GenerateScript` packages code with credential redaction as a 24-hour owner-bound artifact; it never executes the script. There are no shell, filesystem, MCP or cross-session memory tools.

Inputs below are strings unless `int`, `double`, or `bool` is shown. `=value` is a C# default; `?` denotes nullable, not necessarily optional in the emitted schema. Cancellation tokens are omitted because the host supplies them.

## Direct Registrations (13)

| Tool | Credential / scope | Inputs | Model-facing prompt contract |
| --- | --- | --- | --- |
| `RenderChart` | Public | `type`, `title`, `seriesName`, `data`, `xAxisName=null`, `yAxisName=null` | Render one bounded ECharts bar, horizontal bar, line, pie, scatter, funnel, or race chart. |
| `RenderAdvancedChart` | Public | `options` | Static ECharts JSON for maps and advanced charts, at most 100,000 characters; DOM, HTML, links, and remote-image sinks are rejected. |
| `SuggestFollowUp` | Session | `label`, `prompt`, `label2=null`, `prompt2=null`, `label3=null`, `prompt3=null` | Emit one to three concrete clickable next actions; labels at most 60 characters, each describing its own paired prompt (a label without its prompt is dropped). Public fast-path turns use a prompt link instead. |
| `GenerateScript` | Owner-bound artifact | `scriptContent`, `filename=null`, `language=null`, `description=null` | For an explicit Azure CLI or PowerShell code request, submit the complete executable code directly in this call; package it without executing it. |
| `ReportJobOutcome` | Scheduled turn only | `status`, `summary`, `evidenceToolsJson`, `dataAsOfUtc=null`, `nextEligibleRunUtc=null` | One terminal outcome: completed, unchanged, goal_achieved, blocked, partial or failed. Successful outcomes require host-validated evidence. |
| `GenerateDataReport` | Owner-bound artifact | `format`, `dataJson`, `filename=null` | Create CSV, XLSX, or filterable HTML from `{title,source,sheets:[{name,columns,rows,sourceRowCount}]}`; maximum 5,000 rows, 50 columns and 10 sheets. |
| `ReportMaturityScore` | Owner-bound state | `level`, `scores` (native JSON array) | Persist evidence-backed Crawl/Walk/Run/Playbook dimensions; unknown and not-applicable scores stay null. Submitted once, by itself, after every evidence read it scores has returned. |
| `GetScoreHistory` | Owner-bound state | `level=null` | Return up to 100 persisted maturity assessments, optionally filtered by level. |
| `QueryAzure` | Host-routed delegated or public HTTP | `url=""`, `method=GET`, `body`, `sql=""` | The one model-authored HTTP evidence and stored-response SQL tool. A leading `/` is an ARM path; exact hosts select ARM, Graph, Log Analytics/Application Insights, Blob Storage, Retail Prices or public credential-free GET. Empty `url` with `sql` reads this conversation's stored responses without an API call. `operation:<id>` polls a host-registered operation; `operation:` lists up to 100 owner-bound operations in the conversation. URLs must be HTTPS with no userinfo, fragment, custom port, backslash, control characters or `//` prefix, and errors do not echo the URL. CSV/XML success bodies become JSON, HTML becomes text. ARM DELETE is blocked; PUT/PATCH create approval proposals; POST is allowlisted for read-only query/report/calculation/diagnostic endpoints including PolicyInsights policy-state summarize/queryResults and policy-event queryResults at subscription, resource-group or management-group scope (never `triggerEvaluation`) and validated Network Watcher `connectivityCheck` from an existing VM. Paginated ARM/Graph/Retail GETs follow same-origin links up to the fixed 10-page host limit. When ARM rejects a read's api-version and supplies versions, a GET or allowlisted POST is retried with the newest listed stable version (newest preview when none is stable), then with up to two additional listed versions while the provider still answers `UnsupportedApiVersion`; an unlisted `UnsupportedApiVersion` reads the provider manifest and tries at most two older stable versions. A successful repaired body carries a root `_apiVersion` note. `body` is native JSON, but JSON strings with comments/trailing commas are read leniently. A subscription-prefixed Resource Graph path whose subscription is already listed in the body is sent to `/providers/Microsoft.ResourceGraph/resources`. Dropped connections are retried up to twice for GETs only. A Cost Management `/query` body's redundant `Dimension` grouping named `Currency` is removed and reported in root `_request`; a `TagKey` named Currency is kept. |
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

- `QueryAzure` is the single HTTP evidence tool. Several GET urls, one per line (at most 50, no body or `operation:`), are one call: the host runs at most 4 at a time, stores each response separately, applies `sql` to each (`$id` is that response), returns results in url order, shows a shape shared by several large responses once and marks failed lines as a partial result. One `sql` argument may hold several `;`-separated SELECT statements; each returns its own table. It returns raw API JSON as-is, converts successful CSV to columnar JSON and XML to JSON, and routes by exact host: delegated ARM, Graph, Log Analytics/Application Insights and Storage tokens where appropriate, Retail Prices without credentials, and other public HTTPS GETs without credentials.
- Public web reads use [PublicWebReader.cs](../src/Dashboard/AI/Tools/PublicWebReader.cs#L1): literal private/loopback/link-local/metadata hosts are blocked before DNS, connect-time DNS allows only public IPs on every redirect, no proxy/cookies/auth are used, downloads are capped at 8 MB, HTML is stripped to text, JSON is stored as-is, XML/CSV are converted to JSON, and unavailable pages are explicit failures. `learn.microsoft.com/api/search` is canonicalized with `locale=en-us`.
- Retail price requests are `QueryAzure` GETs to `https://prices.azure.com/api/retail/prices` with a model-authored OData `$filter` and optional `currencyCode`. The host drops `$top`/`$skip`, defaults `api-version` and USD, follows same-origin `NextPageLink` under the fixed page cap, retries 429/5xx, and returns raw `Items`. Zero items means no match, not a zero price.
- Microsoft Graph report CSV is converted to columnar JSON. A report function's HTTP 404 `UnknownTenantId` is returned as a determinate `reportServiceProvisioned=false` result, not a transport failure. Retrieval time is not the report refresh date. Every Graph request, including pagination, carries `ConsistencyLevel: eventual`, so directory advanced queries (`$count=true`, `$search`, `assignedLicenses/$count`, `ne`/`not`/`endsWith` filters) work; simple reads are unaffected.
- Forecasts retain their daily date/status/currency coverage. A whole-month forecast can include actual rows with top-level `includeActualCost=true`; that flag and `includeFreshPartialCost` belong beside `dataset`, never in `dataset.configuration`.
- Arithmetic is performed with SQLite over stored responses: use `SUM`, `COUNT`, `ROUND`, joins, grouping, and window functions such as `SUM() OVER`. SQLite numbers are binary floats, so round money explicitly with `round(x, 2)`. These calculations establish arithmetic only; they do not prove a price is valid, current, deployable, or paid.
- Maturity scoring has no host-built Crawl evidence bundle. For Crawl, Walk, Run and Playbook, the model gathers scoped evidence with the general tools, keeps source freshness/coverage in the answer, calls `ReportMaturityScore` once with observed/null/not-applicable dimensions, and uses raw Advisor, Resource Graph, Policy, Budget, Monitor or Cost Management evidence where needed.
- `PublishFAQ` runs only for an explicit request to publish or submit public content, never as an automatic background action after an ordinary answer. Existing authentication and moderation remain in force; pending review is not publication.
## SQLite-Backed Stored Responses

The SDK's automatic temporary-file substitution is disabled on create and resume: built-in filesystem readers remain disabled. Every successful `QueryAzure` or `QueryUploadedFile` response body is stored in one host-owned in-memory SQLite database keyed by the exact owner and conversation. The table is `responses(id, url, method, status, retrieved_utc, body)`. Approval proposals and operation envelopes that contain an `operationId` bypass storage so UI control messages remain inline.

Small responses, at or below 32 KiB, are returned whole with a `[Stored as responses.id = N.]` note. Larger bodies return their HTTP/timestamp preamble, the stored-id note, and a dynamically discovered shape from `json_tree`: paths, types, array lengths, examples, and columnar `columns[].name` entries. Text bodies report their character count and can be searched with `instr` and `substr`.

The model can pass read-only SQLite in `sql`. With a URL, the query runs against the newly stored response and `$id` is bound to that row; only the query rows come back. With no URL, SQL reads earlier stored responses and calls no API, so it is never fresh evidence. A SQL error returns the error plus the shape and does not repeat the HTTP request.

The sandbox enforces one in-memory database per conversation, attached database limit 0, SQL length 64 KiB, value length 64 MiB, a 15-second deadline and cancellation-aware progress handler. The SQLite authorizer allows only read operations (`SELECT`, `READ`, functions and recursive queries); write, schema, attach, pragma, vacuum and extension-loading operations are denied. Output is TSV capped at about 48 KiB with a row-count note. Conversation storage is capped at 64 million characters with oldest-row eviction; all conversations share a 512 million character cap and 30-minute idle expiry. Databases are lost on host restart. No model code runs in the host process other than this sandboxed read-only SQL.

## Cost Detail And Retry Behavior

Network and cost-service throttling remains source evidence. Cost Management `/query` and `/forecast` calls are serialized per tenant by `CostQueryCoordinator`, retried once after a bounded service delay, and stop after a final 429 for the rest of the turn. Cooldown SSE includes `retryAtUtc` and `willRetry`: true means this request is waiting to continue, false means no automatic retry remains for it. Both direct timestamped responses and consolidated source evidence retain the deadline.

Service totals and resource/meter details must be reconciled using the same scope, dates, currency, cost type, filters and compatible source coverage. Matching dates alone do not prove matching billing snapshots. If raw source results still differ, disclose the unresolved amount rather than inventing its cause or silently dropping it.

Network Watcher `connectivityCheck` is an allowlisted `QueryAzure` ARM POST only when the body validates a probe from an existing VM resource ID to one destination address or resourceId and a TCP port from 1-65535. It is a diagnostic read of connectivity state, not an agent-host probe or broad scan.

One simple next question can be an existing `prompt:` link in the final answer; a separate `SuggestFollowUp` round-trip is not required. Structured multiple actions still use that tool. Deliver the primary answer and visual before optional follow-up work.

See the [Cost Query API](https://learn.microsoft.com/rest/api/cost-management/query/usage) for its grouping and retry contracts. Successful synthetic tests do not guarantee future billing-service capacity or exact model cost attribution.

## Complete Metadata Sources

These links expose the full tool descriptions, parameter descriptions, nested JSON examples, defaults, and implementation validations, rather than a second hand-maintained copy of each long prompt.

| Source | Tools |
| --- | --- |
| [ChartTools.cs](../src/Dashboard/AI/Tools/ChartTools.cs#L1) | RenderChart, RenderAdvancedChart |
| [FollowUpTools.cs](../src/Dashboard/AI/Tools/FollowUpTools.cs#L1) | SuggestFollowUp |
| [ScriptTools.cs](../src/Dashboard/AI/Tools/ScriptTools.cs#L1) | GenerateScript |
| [JobRunOutcome.cs](../src/Dashboard/Jobs/JobRunOutcome.cs#L1) | ReportJobOutcome |
| [ReportTools.cs](../src/Dashboard/AI/Tools/ReportTools.cs#L1) | GenerateDataReport |
| [ScoreTools.cs](../src/Dashboard/AI/Tools/ScoreTools.cs#L1) | ReportMaturityScore, GetScoreHistory |
| [AzureQueryTools.cs](../src/Dashboard/AI/Tools/AzureQueryTools.cs#L1), [PublicWebReader.cs](../src/Dashboard/AI/Tools/PublicWebReader.cs#L1), [ResponseShaper.cs](../src/Dashboard/Infrastructure/ResponseShaper.cs#L1), [ResultDatabase.cs](../src/Dashboard/Infrastructure/ResultDatabase.cs#L1), [OperationStore.cs](../src/Dashboard/Infrastructure/OperationStore.cs#L1) | QueryAzure, public-web / CSV / XML shaping, stored-response SQL and operation polling |
| [SavingsLedgerTools.cs](../src/Dashboard/AI/Tools/SavingsLedgerTools.cs#L1) | RecordSavingsAction, UpdateSavingsAction, GetSavingsLedger |
| [UploadedFileTools.cs](../src/Dashboard/AI/Tools/UploadedFileTools.cs#L1) | QueryUploadedFile |
| [HtmlPresentationTools.cs](../src/Dashboard/AI/Tools/HtmlPresentationTools.cs#L1) | GenerateHtmlPresentation |
| [MaturityReportTools.cs](../src/Dashboard/AI/Tools/MaturityReportTools.cs#L1) | GenerateMaturityReport |
| [FaqTools.cs](../src/Dashboard/AI/Tools/FaqTools.cs#L1) | PublishFAQ |

## Prompt Sources

| Layer | Source and responsibility |
| --- | --- |
| System prompt | [AgentSessionFactory.cs](../src/Dashboard/AI/AgentSessionFactory.cs#L25): `SystemPrompt` is the agent's `ChatOptions.Instructions`, sent with every turn. Its sections are outlined below. |
| Tool prompts | The 16 declaration sources above supply each function's description, parameter metadata and nested input examples. |
| Connection and file context | [ChatEndpoints.cs](../src/Dashboard/AI/ChatEndpoints.cs#L248): connected APIs are labelled as `QueryAzure` routes for ARM, Graph, Log Analytics/Application Insights and Blob Storage, plus discovered subscription/management-group scopes, upload IDs and schemas, and file-evidence guidance. Re-sent when the context changes; no bearer tokens. |
| Greeting directive | [ChatEndpoints.cs](../src/Dashboard/AI/ChatEndpoints.cs#L78): one short sentence and no tools for recognized standalone greetings. Substantive follow-ups retain tools. |
| Scheduled-run prefix | [JobScheduler.cs](../src/Dashboard/Jobs/JobScheduler.cs#L320): objective, run number, cadence, latest bounded result, fresh evidence for the entire scope, and mandatory ReportJobOutcome. |
| Scheduled compaction | [JobScheduler.cs](../src/Dashboard/Jobs/JobScheduler.cs#L263): every 20 runs the job starts a fresh model context; the run prefix carries the latest bounded result, and the visible transcript is kept. |
| Sidebar starters | [sidebarCategories.js](../src/Dashboard/frontend/src/data/sidebarCategories.js#L1): all public/connected pricing prompts, maturity questions and grouped starter prompts. These are user-turn templates, not system instructions. |
| Job templates and quick actions | [ChatView.vue](../src/Dashboard/frontend/src/components/ChatView.vue#L1): scheduled-job templates, run-now/edit/delete prompts, deck/report quick actions and prompt chips. |
| Dynamic next-action prompts | SuggestFollowUp supplies `label`/`prompt` pairs; the system prompt also defines public `prompt:` links. They become user turns only when selected. |

The nine scheduled templates are Check capacity of X (15 minutes), Reserve X when available (15 minutes), 1-min test (1 minute), Daily cost digest (daily), Anomaly watch (hourly), Budget guard (daily), Idle resource sweep (weekly), Advisor watch (daily), and Retry last question (hourly). Saved jobs may use a custom cadence; the user's saved prompt is appended to the scheduled-run prefix.

## System Prompt Sections

| Section | Main contract |
| --- | --- |
| How you work | Investigate like a senior cloud engineer. The model authors every API call with `QueryAzure`, looks up live provider apiVersions and official specs instead of guessing, shapes work at the source, runs independent calls in parallel, uses stored-response SQL for returned evidence, and reuses evidence already returned this turn. |
| API facts | Preserve service-specific rules for Cost Management, Resource Graph, reservations/savings plans, Compute capacity/Spot, Retail Prices, and Microsoft Graph reports and licensing. |
| Evidence honesty | State scope, dates, ISO currency, cost type and retrieval time separately from source data-as-of time. Missing, denied, failed and unqueried evidence remains unknown, never zero. |
| Answer shape | Use the latest user's language, a short evidence-backed headline, one visual at most, concrete entities and amounts, downloadable reports for large results, and explicit clarification when required inputs are missing. |
| Maturity scoring | Only score explicit maturity or FinOps assessment requests. Gather scoped evidence with general tools, call `ReportMaturityScore` once, and keep unknown or not-applicable dimensions null with reasons. |

## Query Payload Rules

| Surface | Required source shaping |
| --- | --- |
| ARM list APIs | Use `QueryAzure` with an ARM path, live `api-version`, and `$filter`, `$select`, or a small `$top` only where the endpoint supports them. |
| Azure Resource Graph | POST through `QueryAzure` with one explicit nonempty `subscriptions` (GUID strings) or `managementGroups` (ID strings) array. Use `where`, `summarize` and narrow `project`, then `top N by field` or `order by field` then `take N`; never bare `top N` or implicit tenant-wide scope. |
| Cost Management | Dataset filters, aggregation and at most two grouping dimensions over bounded dates; totals need not have grouping. Start detail requests with the required grouping and never compute a full total from top-N detail. |
| Microsoft Graph | Use a `QueryAzure` Graph URL with only supported `$filter`, `$select`, `$top` or report/count operations; follow needed `@odata.nextLink` pages and disclose partial coverage. Report CSV becomes columnar JSON. |
| Log Analytics / App Insights | Use a `QueryAzure` `/v1/.../query` URL. Filter by time/resource first, then `summarize`, narrow `project` and bounded `top`/`take`; retain finer bins when the question requires them. |
| Blob listings and reads | Exact account/container and the narrowest known name/date prefix; listing bodies can be up to 16 MB, single blob reads use the first 6 MiB range. CSV rows can be partial; request an upload for complete large-export analysis. |
| Retail pricing | One `QueryAzure` Retail Prices URL with model-authored OData `$filter` and currency. Do not send `$top` or `$skip`; inspect raw returned fields and use SQL over stored rows before comparing or calculating. |
| Public web | Specific authoritative HTTPS URL through `QueryAzure`; pages are stored whole as text. Search long text with SQL `instr`/`substr` over `responses.body`. Public Azure status feed is not tenant-specific. |
| Uploaded files | In `query` mode, apply predicates, grouping/aggregates and sort, then `columns` projection and paging. Select 1-50 distinct output names, including aggregate aliases. Retain counts, totals and coverage. |
| Savings ledger | Filter by status/category/literal scope substring on the host, aggregate all matching entries, then page details. Use limit 0 for totals only, otherwise 1-200. Not a source-side Azure query. |
| Operations | Use `QueryAzure` with `url` `operation:<id>` to poll one exact host-registered operation, or `operation:` to list recent owner/conversation-scoped operations. |

The API guidance is model-facing, not an automatic query-rewriting engine or a universal payload guarantee. Uploaded-file projection and ledger filtering/paging are enforced host behavior. Some sources have no query controls: blob CSV reads cannot filter rows on the service, and the public health feed has no region parameter. Requested full scans must not silently become top-N samples.

## Filtering Coverage

Every registered tool is covered below. A scoped request can still need substantial upstream data; only explicit source API filters reduce service-side rows. Host projection, pagination and renderer input shaping reduce returned context instead.

Microsoft 365 usage and licensing now use `QueryAzure` Graph calls. The model must choose the supported report route and query options from Learn or Graph metadata, follow needed pages, count and page stored results with SQL when necessary, and keep the report's own refresh date distinct from retrieval time.

Pricing guidance clarifies missing material product configuration, including VM OS/license or service tier, before quotes or rankings unless the user supplied enough context or authorized assumptions. Fixed-region quotes need a region; if the user gives a region and size, quote on-demand list prices with stated defaults rather than blocking on every optional configuration. Complete source coverage within a model-authored filter is not proof that the filter matches the intended scenario.

Retail rows also retain `tierMinimumUnits` and currency. Volume bands are separate variants: a high-volume discount cannot become the default rate for a small dataset. SQL consumes only selected, verified quantities and rates; it cannot recover missing price or capacity evidence. Report month/year/scenario changes explicitly and recalculate rather than reusing an incompatible total.

`RenderChart` keeps `type` as the canonical schema field. A deliberate compatibility adapter accepts the observed legacy `chart` key, but documentation, prompts and tests should use `type`.

| Surface | Tools | Filtering or payload contract |
| --- | --- | --- |
| ARM, inventory, Advisor, budgets and policy | `QueryAzure` | Supported filters/projections only; scoped Resource Graph alternatives; aggregate before limits. Parallel reads keep their own scopes and stored ids. |
| Directory and Microsoft 365 reports | `QueryAzure` | Supported Graph OData fields/filters/pages or reports/counts; no invented options on unsupported endpoints. |
| Telemetry | `QueryAzure` | KQL time/resource predicates, aggregate, narrow fields, then detail limits. |
| Retail | `QueryAzure` | One source-shaped OData filter and fixed bounded pages; raw rows require inspection and SQL for totals/rankings. |
| Compute and capacity | `QueryAzure` | Exact requested SKUs/scopes/regions through Compute usages, skus, Spot placement score POST, Resource Graph SpotResources and related APIs. |
| Connectivity | `QueryAzure` | One validated Network Watcher `connectivityCheck` from an existing VM to a destination and TCP port. No agent-host or broad network scan. |
| Maturity scoring | `ReportMaturityScore`, `GetScoreHistory` | Model-gathered evidence for each dimension; persisted scores keep observed/unknown/not-applicable distinctions and concise evidence. |
| Uploads | `QueryUploadedFile` | Row predicates, aggregates, sort, validated column projection and paging while preserving complete totals. |
| Blob exports | `QueryAzure`, `QueryUploadedFile` | Prefix-filtered discovery, bounded blob reads, then uploaded-file analysis for complete large exports. CSV response truncation is not a filtered full-export total. |
| Contract rates | `QueryAzure`, `QueryUploadedFile` | One billing scope starts the pricesheet operation, `QueryAzure` polls the owner-bound operation, and selected uploaded rates are analyzed from the file. |
| Public sources | `QueryAzure` | Specific web URLs and SQL text selection. Public Azure status is a feed read, not a tenant-specific resource-health check. |
| Operations | `QueryAzure` | Exact operation lookup or one owner/conversation-scoped list of up to 100 operations. Reuse terminal states. |
| Ledger | `GetSavingsLedger`, `RecordSavingsAction`, `UpdateSavingsAction` | Filter and page reads; write one exact evidenced action or known entry ID. Totals are computed before pagination. |
| Charts | `RenderChart`, `RenderAdvancedChart` | Already filtered aggregates and only needed series/display fields; preserve top-N caveats and units. |
| Reports | `GenerateDataReport`, `GenerateHtmlPresentation`, `GenerateMaturityReport` | Only relevant fields and aggregates, but retain all explicitly requested rows, findings or capabilities. Renderers cannot recover omitted data. |
| Code | `GenerateScript` | Complete code for the requested scope; generated queries use supported source filters. A requested cleanup or remediation script contains the dry-run-guarded change commands for the identified targets, not only an inventory. Packaging never executes code. |
| Follow-ups | `SuggestFollowUp` | Concrete target/action/scope, no embedded raw results or transcripts. |
| Scheduled outcomes | `ReportJobOutcome` | Concise scoped outcome and exact evidence tool names; never drop failed scopes to claim success. |
| Public publication | `PublishFAQ` | Only concise, verified public facts for one question; no tenant data or tool-result dumps. |
## New Input Examples

For `QueryAzure` retail pricing with immediate projection from the stored response:

```json
{
  "url": "https://prices.azure.com/api/retail/prices?currencyCode=USD&$filter=serviceName eq 'Virtual Machines' and armRegionName eq 'eastus' and armSkuName eq 'Standard_D2s_v5' and priceType eq 'Consumption'",
  "sql": "SELECT json_extract(i.value,'$.productName') AS product, json_extract(i.value,'$.meterName') AS meter, round(json_extract(i.value,'$.unitPrice'), 4) AS unitPrice, json_extract(i.value,'$.unitOfMeasure') AS unit, json_extract(i.value,'$.currencyCode') AS currency FROM responses r, json_each(r.body,'$.Items') i WHERE r.id = $id LIMIT 20"
}
```

For quota reads, first select candidate regions from stored evidence, then send one parallel `QueryAzure` call per returned row:

```json
{
  "url": "/subscriptions/{subscriptionId}/providers/Microsoft.Compute/locations/{region}/usages?api-version=2024-07-01",
  "sql": "SELECT json_extract(v.value,'$.name.value') AS name, json_extract(v.value,'$.currentValue') AS used, json_extract(v.value,'$.limit') AS limit FROM responses r, json_each(r.body,'$.value') v WHERE r.id = $id AND name IN ('cores','lowPriorityCores')"
}
```

For listing recent owner-bound operations in the current conversation:

```json
{ "url": "operation:" }
```

For polling a specific host-registered operation:

```json
{ "url": "operation:{operationId}" }
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

## Maintainer Skills

These 13 [Agent Skills](https://code.visualstudio.com/docs/agent-customization/agent-skills) (`.github/skills/<name>/SKILL.md`) are separate from the deployed agent's system prompt and tool declarations. They replaced the older `.github/prompts/*.prompt.md` runbooks, which VS Code's Agent Host no longer loads:

- Local work: [debug-local](../.github/skills/debug-local/SKILL.md), [debug-local-docker](../.github/skills/debug-local-docker/SKILL.md).
- Validation: [safety-check](../.github/skills/safety-check/SKILL.md), [security-audit](../.github/skills/security-audit/SKILL.md), [ui-test](../.github/skills/ui-test/SKILL.md).
- Release/deployment: [deploy](../.github/skills/deploy/SKILL.md), [migrate-tenant](../.github/skills/migrate-tenant/SKILL.md), [release](../.github/skills/release/SKILL.md).
- Maintenance: [repo-metadata-refresh](../.github/skills/repo-metadata-refresh/SKILL.md), [refresh-agent-knowledge](../.github/skills/refresh-agent-knowledge/SKILL.md), [update-apis](../.github/skills/update-apis/SKILL.md), [investigate-logs](../.github/skills/investigate-logs/SKILL.md), [investigate-ai-sessions](../.github/skills/investigate-ai-sessions/SKILL.md).

There is also one [custom agent](../.github/agents/api-integration.agent.md) (`api-integration`), a reference-only knowledge agent for Azure Cost/Billing/FinOps API surfaces.

Keep these skills aligned with this catalog whenever tool names, registration mode, route validation, result storage behavior, or UI/SSE contracts change.



