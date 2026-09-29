# Agent Tool Catalog

This catalog covers all 17 application tools registered by [AgentSessionFactory.cs](../src/Dashboard/AI/AgentSessionFactory.cs#L1), their model-facing inputs, and the application's prompt sources as of 2026-09-26. The linked declarations contain the complete descriptions and JSON examples; source remains authoritative.

## Metadata Model

- All 17 tools are registered directly on every run through `ChatClientAgentRunOptions`; there is no deferred loading or tool search. The agent itself adds only the model's hosted web search tool (`AzureOpenAI:WebSearch`, default on), limited by the prompt to recent public information that no API returns and capped at three hosted calls (searches, page opens and finds) per model request through the Responses API `max_tool_calls`; the service stops at about one more, and function calls are not counted.
- `AIFunctionFactory.Create` publishes `Name`, `Description`, inferred `JsonSchema`, and parameter-level `[Description]` text. JSON bags such as `paramsJson`, `slidesJson` and report payloads are described in the tool metadata and validated by their implementations.
- Tools are plain `AIFunction`s built per turn from the owner's tokens and invoked by Agent Framework's `FunctionInvokingChatClient`; there is no host wrapper. Its `FunctionInvoker` hook only normalizes arguments ([ModelJson.cs](../src/Dashboard/AI/Tools/ModelJson.cs#L1)), and [TurnExecution.cs](../src/Dashboard/AI/TurnExecution.cs#L1) records evidence from each streamed result. `ApplyAzureChange` is an `ApprovalRequiredAIFunction`: Agent Framework holds the call until the user's next turn carries an explicit approval, so write approval is enforced by the runtime, not by descriptions alone.
- Public means no delegated Azure-service token is required, not an unauthenticated tool endpoint. Tools still run inside an application session. Owner IDs, tokens and cancellation tokens are host-supplied, never model parameters.
- Direct availability never grants arbitrary host execution. `GenerateScript` packages code as a 24-hour owner-bound artifact; it never executes the script. There are no shell, filesystem, MCP or cross-session memory tools.

Inputs below are strings unless `int`, `double`, or `bool` is shown. `=value` is a C# default; `?` denotes nullable, not necessarily optional in the emitted schema. Cancellation tokens are omitted because the host supplies them.

## Evidence, Chart And State Tools (14)

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
| `QueryAzure` | Host-routed delegated or public HTTP | `url=""`, `method=GET`, `body`, `query=""` | The one model-authored HTTP evidence tool, with stateless LINQ cropping. A leading `/` is an ARM path; exact hosts select ARM, Graph, Log Analytics/Application Insights, Blob Storage, Retail Prices or public credential-free GET. Empty `url` with `query` is a calculator over copied figures: it sends no request and is not source evidence. URLs must be HTTPS with no userinfo, fragment, custom port, backslash, control characters or `//` prefix, and errors do not echo the URL. CSV/XML success bodies become JSON, HTML becomes text lines. DELETE is blocked; PUT/PATCH return 405 and go through `ApplyAzureChange`; POST is allowlisted for read-only query/report/calculation/diagnostic endpoints including PolicyInsights policy-state summarize/queryResults and policy-event queryResults at subscription, resource-group or management-group scope (never `triggerEvaluation`) and validated Network Watcher `connectivityCheck` from an existing VM. Paginated ARM/Graph/Retail GETs follow same-origin links up to the fixed 10-page host limit. When ARM rejects a read's api-version and supplies versions, a GET or allowlisted POST is retried with the newest listed stable version (newest preview when none is stable), then with up to two additional listed versions while the provider still answers `UnsupportedApiVersion`; an unlisted `UnsupportedApiVersion` reads the provider manifest and tries at most two older stable versions. A successful repaired body carries a root `_apiVersion` note. `body` is native JSON, but JSON strings with comments/trailing commas are read leniently. A subscription-prefixed Resource Graph path whose subscription is already listed in the body is sent to `/providers/Microsoft.ResourceGraph/resources`. Dropped connections are retried up to twice for GETs only. A Cost Management `/query` body's redundant `Dimension` grouping named `Currency` is removed and reported in root `_request`; a `TagKey` named Currency is kept. |
| `ApplyAzureChange` | Delegated ARM, user approval | `method`, `url`, `body` | The only write path: one ARM PUT or PATCH. An `ApprovalRequiredAIFunction`, so the UI shows the exact method, URL and body and the call runs only after the user approves it; rejection sends nothing. 201/202 is accepted, not complete: the model polls the returned `Azure-AsyncOperation` or `Location` URL with `QueryAzure`. |
| `RecordSavingsAction` | Owner-bound state | `title`, `category`, `estimatedMonthlyUsd`, `scope`, `status` | Record evidenced remediation. Script delivery/pending writes are proposed; executed requires confirmed application. |
| `UpdateSavingsAction` | Owner-bound state | `id`, `status`, `verifiedMonthlyUsd=""` | Advance an entry to proposed/executed/verified/dismissed; omitted or empty `verifiedMonthlyUsd` preserves the prior value. |
| `GetSavingsLedger` | Owner-bound state | `status=null`, `category=null`, `scopeContains=null`, `limit=50`, `offset=0` | Host-side filters and newest-first paging; default 50, max 200 details, or limit 0 for totals only. Totals cover all matches before paging. |
| `QueryUploadedFile` | Conversation-bound upload | `fileId`, `mode`, `paramsJson=null` | Registered upload only; query mode supports filters, groups, aggregates, sorting, output columns (1-50), offset and limit. Parameterless modes such as `workbook` need no argument bag. |

## Presentation And Publication Tools (3)

| Tool | Credential / scope | Inputs | Model-facing prompt contract |
| --- | --- | --- | --- |
| `GenerateHtmlPresentation` | Owner-bound artifact | `slidesJson`, `filename=null`, `customer=null` | Structured slides: title, section, kpi, chart, content, two_column, maturity, alerts, table, roadmap or closing. |
| `GenerateMaturityReport` | Owner-bound artifact | `reportJson`, `filename=null`, `customer=null` | Evidence-based scrolling HTML assessment, up to 19 capabilities across four FinOps domains. |
| `PublishFAQ` | Azure-connected user | `question`, `answer`, `title` | Publish or queue a public FinOps Q&A only; tenant-specific data is prohibited. |

## Evidence, Arithmetic And Publication

- `QueryAzure` is the single HTTP evidence tool. Several GET urls, one per line (at most 50, no body), are one call: the host runs at most 4 at a time, applies the same `query` to each response, returns results in url order, shows a schema shared by several large responses once and marks failed lines as a partial result. It returns raw API JSON as-is, converts successful CSV to row objects and XML to JSON, and routes by exact host: delegated ARM, Graph, Log Analytics/Application Insights and Storage tokens where appropriate, Retail Prices without credentials, and other public HTTPS GETs without credentials.
- Public web reads use [PublicWebReader.cs](../src/Dashboard/AI/Tools/PublicWebReader.cs#L1): literal private/loopback/link-local/metadata hosts are blocked before DNS, connect-time DNS allows only public IPs on every redirect, no proxy/cookies/auth are used, downloads are capped at 8 MB, HTML is stripped to text, JSON is returned as-is, XML/CSV are converted to JSON, and unavailable pages are explicit failures. `learn.microsoft.com/api/search` is canonicalized with `locale=en-us`.
- Retail price requests are `QueryAzure` GETs to `https://prices.azure.com/api/retail/prices` with a model-authored OData `$filter` and optional `currencyCode`. The host drops `$top`/`$skip`, defaults `api-version` and USD, follows same-origin `NextPageLink` under the fixed page cap, retries 429/5xx, and returns raw `Items`. Zero items means no match, not a zero price.
- Microsoft Graph report CSV is converted to columnar JSON. A report function's HTTP 404 `UnknownTenantId` is returned as a determinate `reportServiceProvisioned=false` result, not a transport failure. Retrieval time is not the report refresh date. Every Graph request, including pagination, carries `ConsistencyLevel: eventual`, so directory advanced queries (`$count=true`, `$search`, `assignedLicenses/$count`, `ne`/`not`/`endsWith` filters) work; simple reads are unaffected.
- Forecasts retain their daily date/status/currency coverage. A whole-month forecast can include actual rows with top-level `includeActualCost=true`; that flag and `includeFreshPartialCost` belong beside `dataset`, never in `dataset.configuration`.
- Arithmetic is performed by `query`: `Sum`, `Count`, `Average`, `GroupBy`, `Math.Round` and running totals such as `root.rows.Where(r => r.date <= x.date).Sum(r => r.cost ?? 0)`. JSON numbers are `double?`, so round money explicitly with `Math.Round(x.cost ?? 0, 2)`. Figures from separate responses are combined by a url-less `query` over values copied exactly from earlier results. These calculations establish arithmetic only; they do not prove a price is valid, current, deployable, or paid.
- Maturity scoring has no host-built Crawl evidence bundle. For Crawl, Walk, Run and Playbook, the model gathers scoped evidence with the general tools, keeps source freshness/coverage in the answer, calls `ReportMaturityScore` once with observed/null/not-applicable dimensions, and uses raw Advisor, Resource Graph, Policy, Budget, Monitor or Cost Management evidence where needed.
- `PublishFAQ` runs only for an explicit request to publish or submit public content, never as an automatic background action after an ordinary answer. Existing authentication and moderation remain in force; pending review is not publication.

## Stateless Query Cropping

Nothing is stored between tool calls and the application has no database; the Foundry Responses chain already holds every earlier tool result as model context. [JsonQuery.cs](../src/Dashboard/Infrastructure/JsonQuery.cs#L1) crops each response in memory before it reaches the model:

- A response up to 32 KiB returns whole. A larger one returns its HTTP/timestamp preamble, a `Coverage:` line with the root coverage and provenance fields (`complete`, `pagesRead`, `nextLink`, `_finops`, `sourceEvidence`…) and the schema inferred from its JSON: property names, CLR types (`double?`, `string`, `Dictionary<string,T>` for tag-like maps), item counts, distinct samples and a starter expression. The model then repeats the request with `query`.
- `query` is a Dynamic LINQ ([System.Linq.Dynamic.Core](https://dynamic-linq.net/)) C# expression over the response root `it`. Only its JSON result returns (up to 48 KiB, with a note when a list is truncated; an object result over the cap keeps its fields and trims its largest lists, with a note), after the `Coverage:` line, so a projection cannot change the source's own completeness. Column/row tables (Cost Management, Log Analytics, Resource Graph table format, CSV) become row objects read by column name; other text becomes `it.lines`. JSON names that are Dynamic LINQ keywords become `_name`, and `@odata.nextLink` becomes `_odata_nextLink`; the schema shows both spellings.
- Reads are as forgiving as JSON, because the model often writes `query` on the first call before seeing the shape, and a response omits null members and has no items to type in an empty list. A member this response lacks reads as null and the result says `Not in this response, so read as null: it.value[].properties.policyRule.`; reading through null (a null `displayName.ToLower()`, a missing `tags["env"]` or list index) gives null, `false` or 0, and a null list enumerates as empty. An object read as a collection (`notifications.Select(n => n.Value.threshold)`) becomes a dictionary of its merged values. A member never seen with a value takes `double?` when the query uses it as a number (`r.placeholders ?? 0`). `Math` accepts `double?` arguments and names the `?? 0` fix only when a null reaches it. A member that differs only by case from a real one stays an error, and an unknown name is always a data member, never a type.
- Without `url`, `query` evaluates over an empty object: a calculator for figures copied from earlier results. It sends no request and `TurnExecution` never records it as source evidence.
- A query error returns the error and the schema instead of data; the model corrects the query and repeats the request.

The sandbox parses with case-sensitive members, no dynamic type creation beyond anonymous projections, and an allowlist of LINQ, string, `Math`, `Convert`, `DateTime`, `TimeSpan`, `Guid` and primitive members. `GetType`, reflection, delegates, `PadLeft`/`PadRight`, `string.Format`/`Join`/`Concat` over sequences, non-constant `Replace`, large `ToString` formats and array allocation are rejected. Expressions are at most 4,000 characters and run under a 20-million-step, 10-second, cancellation-aware budget with 64K-character strings. A call inside a lambda that reads only the response root, such as the total in a per-item share `x.cost / root.value.Sum(y => y.cost ?? 0)`, is evaluated once when first reached rather than once per item, so share-of-total over 14,000 cost rows stays linear. No other model code runs in the host process.

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
| [AzureQueryTools.cs](../src/Dashboard/AI/Tools/AzureQueryTools.cs#L1), [PublicWebReader.cs](../src/Dashboard/AI/Tools/PublicWebReader.cs#L1), [ResponseShaper.cs](../src/Dashboard/Infrastructure/ResponseShaper.cs#L1), [JsonQuery.cs](../src/Dashboard/Infrastructure/JsonQuery.cs#L1) | QueryAzure, ApplyAzureChange, public-web / CSV / XML shaping and LINQ query cropping |
| [SavingsLedgerTools.cs](../src/Dashboard/AI/Tools/SavingsLedgerTools.cs#L1) | RecordSavingsAction, UpdateSavingsAction, GetSavingsLedger |
| [UploadedFileTools.cs](../src/Dashboard/AI/Tools/UploadedFileTools.cs#L1) | QueryUploadedFile |
| [HtmlPresentationTools.cs](../src/Dashboard/AI/Tools/HtmlPresentationTools.cs#L1) | GenerateHtmlPresentation |
| [MaturityReportTools.cs](../src/Dashboard/AI/Tools/MaturityReportTools.cs#L1) | GenerateMaturityReport |
| [FaqTools.cs](../src/Dashboard/AI/Tools/FaqTools.cs#L1) | PublishFAQ |

## Prompt Sources

| Layer | Source and responsibility |
| --- | --- |
| System prompt | [AgentSessionFactory.cs](../src/Dashboard/AI/AgentSessionFactory.cs#L25): `SystemPrompt` is the agent's `ChatOptions.Instructions`, sent with every turn. Its sections are outlined below. |
| Tool prompts | The declaration sources above supply each of the 17 functions' description, parameter metadata and nested input examples. |
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
| How you work | Investigate like a senior cloud engineer. The model authors every API call with `QueryAzure`, looks up live provider apiVersions and official specs instead of guessing, shapes work at the source, runs independent calls in parallel, crops large responses with `query`, and reuses evidence already returned this turn. |
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
| Microsoft Graph | Use a `QueryAzure` Graph URL with only supported `$filter`, `$select`, `$top` or report/count operations; follow needed `@odata.nextLink` pages and disclose partial coverage. Report CSV becomes row objects. |
| Log Analytics / App Insights | Use a `QueryAzure` `/v1/.../query` URL. Filter by time/resource first, then `summarize`, narrow `project` and bounded `top`/`take`; retain finer bins when the question requires them. |
| Blob listings and reads | Exact account/container and the narrowest known name/date prefix; listing bodies can be up to 16 MB, single blob reads use the first 6 MiB range. CSV rows can be partial; request an upload for complete large-export analysis. |
| Retail pricing | One `QueryAzure` Retail Prices URL with model-authored OData `$filter` and currency. Do not send `$top` or `$skip`; inspect raw returned fields and use `query` to select rows before comparing or calculating. |
| Public web | Specific authoritative HTTPS URL through `QueryAzure`; pages become text lines. Select the needed part of long text with `query` over `lines`. Public Azure status feed is not tenant-specific. |
| Uploaded files | In `query` mode, apply predicates, grouping/aggregates and sort, then `columns` projection and paging. Select 1-50 distinct output names, including aggregate aliases. Retain counts, totals and coverage. |
| Savings ledger | Filter by status/category/literal scope substring on the host, aggregate all matching entries, then page details. Use limit 0 for totals only, otherwise 1-200. Not a source-side Azure query. |
| Changes | Use `ApplyAzureChange` for one ARM PUT/PATCH the user approves in the UI, then poll its `Azure-AsyncOperation` or `Location` URL with `QueryAzure` GET after `Retry-After`. |

The API guidance is model-facing, not an automatic query-rewriting engine or a universal payload guarantee. Uploaded-file projection and ledger filtering/paging are enforced host behavior. Some sources have no query controls: blob CSV reads cannot filter rows on the service, and the public health feed has no region parameter. Requested full scans must not silently become top-N samples.

## Filtering Coverage

Every registered tool is covered below. A scoped request can still need substantial upstream data; only explicit source API filters reduce service-side rows. Host projection, pagination and renderer input shaping reduce returned context instead.

Microsoft 365 usage and licensing now use `QueryAzure` Graph calls. The model must choose the supported report route and query options from Learn or Graph metadata, follow needed pages, count and page results with `query` when necessary, and keep the report's own refresh date distinct from retrieval time.

Pricing guidance clarifies missing material product configuration, including VM OS/license or service tier, before quotes or rankings unless the user supplied enough context or authorized assumptions. Fixed-region quotes need a region; if the user gives a region and size, quote on-demand list prices with stated defaults rather than blocking on every optional configuration. Complete source coverage within a model-authored filter is not proof that the filter matches the intended scenario.

Retail rows also retain `tierMinimumUnits` and currency. Volume bands are separate variants: a high-volume discount cannot become the default rate for a small dataset. Query arithmetic consumes only selected, verified quantities and rates; it cannot recover missing price or capacity evidence. Report month/year/scenario changes explicitly and recalculate rather than reusing an incompatible total.

`RenderChart` keeps `type` as the canonical schema field. A deliberate compatibility adapter accepts the observed legacy `chart` key, but documentation, prompts and tests should use `type`.

| Surface | Tools | Filtering or payload contract |
| --- | --- | --- |
| ARM, inventory, Advisor, budgets and policy | `QueryAzure` | Supported filters/projections only; scoped Resource Graph alternatives; aggregate before limits. Parallel reads keep their own scopes. |
| Directory and Microsoft 365 reports | `QueryAzure` | Supported Graph OData fields/filters/pages or reports/counts; no invented options on unsupported endpoints. |
| Telemetry | `QueryAzure` | KQL time/resource predicates, aggregate, narrow fields, then detail limits. |
| Retail | `QueryAzure` | One source-shaped OData filter and fixed bounded pages; raw rows require inspection and `query` for totals/rankings. |
| Compute and capacity | `QueryAzure` | Exact requested SKUs/scopes/regions through Compute usages, skus, Spot placement score POST, Resource Graph SpotResources and related APIs. |
| Connectivity | `QueryAzure` | One validated Network Watcher `connectivityCheck` from an existing VM to a destination and TCP port. No agent-host or broad network scan. |
| Maturity scoring | `ReportMaturityScore`, `GetScoreHistory` | Model-gathered evidence for each dimension; persisted scores keep observed/unknown/not-applicable distinctions and concise evidence. |
| Uploads | `QueryUploadedFile` | Row predicates, aggregates, sort, validated column projection and paging while preserving complete totals. |
| Blob exports | `QueryAzure`, `QueryUploadedFile` | Prefix-filtered discovery, bounded blob reads, then uploaded-file analysis for complete large exports. CSV response truncation is not a filtered full-export total. |
| Contract rates | `QueryAzure`, `QueryUploadedFile` | One billing scope POST starts the pricesheet download, `QueryAzure` polls its returned `Location` URL, and selected uploaded rates are analyzed from the file. |
| Public sources | `QueryAzure` | Specific web URLs and `query` text selection. Public Azure status is a feed read, not a tenant-specific resource-health check. |
| Changes | `ApplyAzureChange` | One exact ARM PUT or PATCH, held for explicit user approval; never DELETE. Poll 201/202 results rather than retrying the write. |
| Ledger | `GetSavingsLedger`, `RecordSavingsAction`, `UpdateSavingsAction` | Filter and page reads; write one exact evidenced action or known entry ID. Totals are computed before pagination. |
| Charts | `RenderChart`, `RenderAdvancedChart` | Already filtered aggregates and only needed series/display fields; preserve top-N caveats and units. |
| Reports | `GenerateDataReport`, `GenerateHtmlPresentation`, `GenerateMaturityReport` | Only relevant fields and aggregates, but retain all explicitly requested rows, findings or capabilities. Renderers cannot recover omitted data. |
| Code | `GenerateScript` | Complete code for the requested scope; generated queries use supported source filters. A requested cleanup or remediation script contains the dry-run-guarded change commands for the identified targets, not only an inventory. Packaging never executes code. |
| Follow-ups | `SuggestFollowUp` | Concrete target/action/scope, no embedded raw results or transcripts. |
| Scheduled outcomes | `ReportJobOutcome` | Concise scoped outcome and exact evidence tool names; never drop failed scopes to claim success. |
| Public publication | `PublishFAQ` | Only concise, verified public facts for one question; no tenant data or tool-result dumps. |
## New Input Examples

For `QueryAzure` retail pricing with immediate projection of the response:

```json
{
  "url": "https://prices.azure.com/api/retail/prices?currencyCode=USD&$filter=serviceName eq 'Virtual Machines' and armRegionName eq 'eastus' and armSkuName eq 'Standard_D2s_v5' and priceType eq 'Consumption'",
  "query": "Items.Select(i => new { i.productName, i.meterName, unitPrice = Math.Round(i.unitPrice ?? 0, 4), i.unitOfMeasure, i.currencyCode }).Take(20)"
}
```

For quota reads across candidate regions, send the regions as url lines of one call; the same `query` crops each response:

```json
{
  "url": "/subscriptions/{subscriptionId}/providers/Microsoft.Compute/locations/eastus/usages?api-version=2024-07-01\n/subscriptions/{subscriptionId}/providers/Microsoft.Compute/locations/westus3/usages?api-version=2024-07-01",
  "query": "value.Where(v => v.name.value == \"cores\" || v.name.value == \"lowPriorityCores\").Select(v => new { name = v.name.value, used = v.currentValue, v.limit })"
}
```

For a calculation over figures copied from earlier results, omit `url`; no request is sent:

```json
{ "query": "new { monthly = Math.Round(0.096 * 730 * 4, 2) }" }
```

For an approved change, `ApplyAzureChange` receives the exact write the user reviews:

```json
{
  "method": "PATCH",
  "url": "/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Compute/virtualMachines/{vmName}?api-version=2024-07-01",
  "body": { "tags": { "costCenter": "1234" } }
}
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

Keep these skills aligned with this catalog whenever tool names, registration mode, route validation, result cropping behavior, or UI/SSE contracts change.



