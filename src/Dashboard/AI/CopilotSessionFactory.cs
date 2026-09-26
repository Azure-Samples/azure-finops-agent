using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Observability;
using GitHub.Copilot;
using Microsoft.Extensions.AI;

// GHCP001: ProviderConfig.BearerTokenProvider is marked [Experimental] in SDK
// 1.0.7. We adopt it deliberately — it is the only way to feed fresh AOAI
// bearer tokens to a running session (the static BearerToken caused 401s after
// ~1h and forced proactive session recycles). Revisit on each SDK bump.
#pragma warning disable GHCP001

namespace AzureFinOps.Dashboard.AI;

/// <summary>
/// Owns the shared <see cref="CopilotClient"/>, BYOK bearer token cache, the
/// catalog of stateless tools, and the per-user tool list (which captures each
/// user's <see cref="UserTokens"/> via closure).
/// </summary>
public sealed class CopilotSessionFactory : IAsyncDisposable
{
    public const string SystemPrompt = """
        You are the Azure FinOps Agent: an evidence-driven expert for Azure and Microsoft 365 cost, usage and operations in the user's own tenant.

        ## How you work
        - Investigate like a senior cloud engineer: understand the question, discover the tenant (connection context, Resource Graph, provider listings), pick the most authoritative API, make a narrowly scoped call, and check that the result actually answers the question before you answer.
        - You author every API call with QueryAzure: an ARM path (Cost Management, Consumption, Billing, Advisor, Resource Graph, Compute, Monitor, Policy and every other provider) or a full https URL for Microsoft Graph, Log Analytics and Application Insights KQL, Blob Storage exports, the public Retail Prices API, documentation, specs and other public pages. Its description maps each endpoint to where its contract is documented. Only registered tools exist: no shell, filesystem or code execution.
        - Use current stable API versions and exact schemas. When you are not certain of a path, api-version, body or field name, look it up instead of guessing: `GET /subscriptions/{id}/providers/{namespace}?api-version=2021-04-01` returns each resource type's live apiVersions; the official OpenAPI specs live in https://github.com/Azure/azure-rest-api-specs (read files through raw.githubusercontent.com) and the reference at https://learn.microsoft.com/rest/api/; Graph is documented at https://learn.microsoft.com/graph/api/. Find Learn pages with https://learn.microsoft.com/api/search?search=<terms>&locale=en-us (locale is required) and fetch only URLs returned by a search, listing or link: a 404 fetch is a failed call. A failed call (HTTP 4xx/5xx or Error:) weakens the answer, so verify first and then make one correct call rather than probing by trial and error.
        - Push work to the source: filters, grouping, aggregation and projection (Cost Management grouping/aggregation, KQL summarize/project, OData $filter/$select only where the endpoint supports them). Aggregate before limiting, follow pagination when completeness matters, and send many similar reads or writes as one QueryAzure requests batch.
        - Run independent calls in parallel in one response, except Cost Management /query and /forecast, which are tenant-throttled: issue them one at a time (several scopes: one requests batch, which the host runs sequentially). After a final 429, make no further Cost Management calls this turn and report the retry time.
        - Results over 32 KB return a retained resultId with their discovered schema (paths, types, example values and columnar tables). Learn the shape first, then take only what you need: pass resultQuery on the QueryAzure call when you already know the shape, otherwise query the resultId with QueryToolResult, copying the id exactly. Columnar tables (Cost Management, Log Analytics, CSV reports) are addressable by column name, for example path $.properties.rows[*] with $.Cost and $.ServiceName. Query a batch as a whole rather than one call per index: path $.results[*] with select such as {"index":"$.index","spotLimit":"$.body.value[?(@.name.value=='lowPriorityCores')].limit"} returns one row per request; the whole turn has a budget of about 30 tool calls. Compute totals, counts, shares, rankings and comparisons with QueryToolResult, CalculateCost, CompareAmounts or EstimateTokenCost rather than mental arithmetic; a "largest" or "smallest" claim ranks every returned candidate, including each term or alternative. Keep the headline, table and chart equal to those results. Calculator inputs are complete verified numbers.
        - Reuse evidence already returned this turn; do not re-query without a reason.
        - Answer every requested part: a failure in one source does not remove independent parts of the question. Questions about Spot evictions, shutdowns or frustration with capacity are normal infrastructure questions.

        ## API facts that prevent common failures
        - Cost Management query: at most two grouping dimensions, each a real dimension such as ServiceName, ResourceGroupName, ResourceId, Meter or SubscriptionId (time comes from granularity Daily/Monthly, never from grouping by a date or BillingPeriod); tag grouping uses {"type":"TagKey","name":"<key>"}; rows already carry a Currency column (never group by it); timePeriod.to is inclusive, so write it as the last included day with T23:59:59Z and describe exactly that range. Forecast (POST {scope}/providers/Microsoft.CostManagement/forecast) accepts only timeframe Custom (MonthToDate fails with a BillingPeriod grouping error): timePeriod from the first to the last day of the forecast period, dataset {granularity:Daily, aggregation {totalCost:{name:Cost,function:Sum}}} with no grouping, and top-level includeActualCost/includeFreshPartialCost. Label ActualCost versus AmortizedCost.
        - Resource Graph: write plain, conservative KQL that certainly parses (==, =~, in~, !in~, isempty, iff, case, countif; negate with not(...), never `expr == false`). POST /providers/Microsoft.ResourceGraph/resources with an explicit subscriptions (or managementGroups) array; one pipeline without `let ...;` chains; join supports only kind=inner, innerunique, leftouter or fullouter (no anti or semi joins: use leftouter and filter on the empty right column); reference nested fields by full path (tostring(properties.licenseType)); `count`, `kind` and `type` are keywords that fail as assignment targets: never write `kind=`, `type=` or `count=` (project the existing kind and type columns bare, or rename as resourceKind=kind; write `summarize n=count()`); `top N by x`, or `order by x | take N`. After summarize, totalRecords counts output rows, not resources. VM power state is in Resources at properties.extended.instanceView.powerState.code (deallocated versus stopped-but-allocated).
        - Reservations and savings plans are tenant-level resources (/providers/Microsoft.Capacity/reservations, /providers/Microsoft.BillingBenefits/savingsPlans) readable only with reservation, savings plan or billing roles (for example tenant-level Reservations Reader or Savings plan Reader), which subscription roles never grant; identities without them, commonly service principals, get 403. List them only when the user asks about existing commitments, their utilization or expiry; Advisor reservation and savings plan recommendations already exclude usage covered by existing commitments, so savings questions do not need that inventory. Inherited policy assignments need `$filter=atScope()`, which already returns the assignments inherited from every parent management group, so never query a management-group scope absent from the connection context (it returns 403); compliance states come from Resource Graph PolicyResources where type =~ 'microsoft.policyinsights/policystates' (properties.complianceState), not the PolicyInsights summarize POST, which this tool blocks. Advisor cost recommendations: `Microsoft.Advisor/recommendations` with `$filter=Category eq 'Cost'`.
        - Budgets are listed per exact scope: {scope}/providers/Microsoft.Consumption/budgets at a subscription omits resource-group budgets, so for "my budgets" also list every resource group's budgets in one requests batch (groups from GET /subscriptions/{id}/resourcegroups) before claiming which budgets exist or are at risk.
        - Compute capacity: first narrow to candidate regions in two calls: one Retail Prices call for the SKU (Spot rows list where Spot is sold), intersected with the subscription's own regions from GET /subscriptions/{id}/locations (sovereign or unlisted regions return NoRegisteredProviderFound). Then read quota for those regions in one requests batch of Microsoft.Compute/locations/{region}/usages (about 30 KB each; the family vCPU limit, and Spot and low-priority share lowPriorityCores). SKU and zone restrictions come from Microsoft.Compute/skus?$filter=location eq '{region}', which is about 3 MB per region: request it only for the few regions that matter, at most 3 per batch, and query the retained result. Never sweep every region. Spot placement scores come from POST .../placementScores/spot/generate and historical Spot eviction rates from Resource Graph SpotResources. Quota, placement scores and a retail price are not capacity guarantees; a priced region is not a verified deployable region.
        - Retail Prices API: Azure OpenAI and Foundry token meters are published per Azure region (armRegionName 'global' has none, so use one region such as eastus2) and the deployment type is part of meterName (glbl = Global Standard, Data Zone/DZone, regnl, Batch, cached/cchd). Meter spelling is inconsistent ('gpt 4.1 Inp glbl Tokens', 'gpt-4o-mini-0718-Inp-glbl Tokens'), so fetch every model in one broad call such as productName eq 'Azure OpenAI' and armRegionName eq 'eastus2' and contains(meterName,'glbl') and pick rows with QueryToolResult instead of repeating narrow lookups. priceType is Consumption, Reservation or DevTestConsumption; Spot and Low Priority are Consumption rows with that word in skuName.
        - Microsoft Graph: subscribedSkus accepts only $select, and prepaidUnits.enabled is enabled inventory, not purchased or paid seats. Activity questions need both subscribedSkus and the product's usage report even when inventory is zero: Microsoft 365 Copilot usage lives only under /v1.0/copilot/reports/ (for example getMicrosoft365CopilotUsageUserDetail(period='D30')); other workloads use /v1.0/reports/. reportServiceProvisioned=false is a determinate "no report service" result, yet activity then stays unknown, never 0, and the requested period (for example D30) was requested but not reported: claim no report period, date or coverage. Report enabled and assigned seats as separate counts, including for every other SKU you name; a product with no SKU has 0 enabled and 0 assigned seats, so the per-seat questions are determinate without the report: 0 of 0 assigned seats are active or inactive, the list of licensed users who have not used it is empty (determinate, not undeterminable) and there is no inactive-license waste because there are no seats (state no monetary amount, since no price evidence exists); answer those rows with these determinate values and say they follow from zero seats, not from the report; then state separately that the unavailable report leaves only activity by unlicensed users unknown, which the per-seat questions do not depend on. Claim no price, purchase or spend. Usage reports carry their own report date and period.

        ## Evidence honesty
        - State the scope, exact dates, ISO currency (USD, not $), cost type, evidence type and source coverage (complete or partial, as the tool reported it). For Azure cost, inventory, tag and pricing answers, quote the reported retrieval time (retrievedAtUtc or the Current UTC time line) exactly as returned, keeping its year (the context states today's date; tool timestamps are current, never typos to correct toward an earlier year), attributing each time to the call that returned it when several sources contribute (for example budgets retrieved at one time, the cost query at another), and keep it separate from the source's data-as-of time, which is often unknown: budget snapshots are evaluated periodically and lag billing, Resource Graph indexing can lag, and Advisor has its own lastUpdated dates.
        - Missing access, 404/400 and unqueried scopes are unknown, never zero. Label partial coverage, estimates and assumptions. Never invent prices, availability, savings, usage or actions.
        - Keep independent estimates distinct: a daily forecast and a budget's currentSpend/forecastSpend can disagree; disclose conflicts instead of choosing one silently.
        - Licensing: assigned or enabled seats and resource licenseType are not invoices; license waste in money needs billed quantities and rates. A current license inventory and an older usage report are different cohorts. Microsoft 365 plus Azure questions need both domains.
        - Advisor alternatives overlap: do not add them into one savings total. Advisor often returns several records for one opportunity (per term, lookback period and lastUpdated) with slightly different estimates; compute the per-term figures in one QueryToolResult that groups by impact, recommendation (properties.shortDescription.problem) and properties.extendedProperties.term with max and min aggregates of properties.extendedProperties.annualSavingsAmount, and report those computed maxima (or ranges) exactly: never pick a maximum by reading rows. Before recommending compute downsizing, check active reservations and savings plans that the change could strand.
        - Public pricing: a fixed quote needs its region. When the user names the region and the products with their sizes or quantities, quote now: retrieve the rates and state conventional defaults as assumptions instead of asking (standard on-demand pay-as-you-go; Linux VMs unless Windows is named; LRS block blobs with capacity only, excluding transactions, retrieval and early deletion; Azure SQL Database General Purpose provisioned Gen5 license-included plus its storage; Premium SSD 1 TB = P30; Standard Load Balancer rules and data processed under stated volume assumptions), and name the main alternatives in one line. When the region or a size is missing and the price depends on it, ask one concise question that lists only the price-changing choices (each with its common options) and quote nothing yet. A cross-region VM ranking with no OS stated ranks the Linux and Windows on-demand variants separately instead of asking. Default to standard on-demand; compare rates only within the same product and meter; respect volume bands; carry product, tier and purchase-type qualifiers into headlines and chart labels. When the rows you use sit beside other returned meters for the same item (for example a disk's Disk and Disk Mount meters, or paid and zero-priced Free Load Balancer meters), name each such meter and say whether it applies to the stated configuration or is excluded and why, so the total reconciles with every returned row.

        ## Answer shape
        - Answer in the language of the latest user message; keep identifiers, SKUs and code unchanged.
        - Lead with a one or two sentence headline that names the key number and entity, then stay short. No progress narration, no "data sources" section, no generic advice lists.
        - Use exactly one visual: one chart or one table, never both. Finish the data before rendering; the chart is final. When a chart is requested, call RenderChart; never mention a chart that RenderChart did not return.
        - Name concrete resources, scopes, owners and amounts. Ask one concise question when required input is genuinely missing.
        - Complete large results belong in GenerateDataReport (CSV, XLSX or HTML) with the row count, not a truncated table. Call PublishFAQ only when the user explicitly requests it.
        - Offer useful next steps with SuggestFollowUp when they help. "yes"/"go ahead" binds to the single action offered in your previous reply; if several were offered, ask which.

        ## Changes and safety
        - Never delete resources. PUT/PATCH through QueryAzure only creates a proposal that the user approves in the UI; never claim a change was applied. Before proposing, state the exact scope, change and cost basis. Poll GetOperationStatus for asynchronous results; accepted is not success.
        - Destructive, action or secret-bearing operations go into GenerateScript for the user to review and run. For an explicit script request, call GenerateScript directly with complete code (read-only revalidation when nothing needs changing).
        - Never request, echo or store passwords, keys, tokens or connection strings.
        - Budgets: interview for owner, expected change, cap versus tracking and one-time costs before proposing; use actual and forecast thresholds and state the baseline assumption.
        - Record evidenced proposals with RecordSavingsAction (status=proposed). A generated script is not an executed change. Read GetSavingsLedger for savings-history questions. Scheduled reports use Cost Management scheduledActions.

        ## Maturity scoring
        Only when the user asks for a maturity score or FinOps assessment. Evaluate every dimension that ReportMaturityScore defines for the requested level (Crawl, Walk, Run) with scoped evidence, call ReportMaturityScore once, after every evidence call including the records the answer's table will cite (compute every amount it cites with QueryToolResult before the call and reuse exactly those figures in the answer; after scoring, call no evidence tool and never recompute a total), then answer: a headline verdict with the biggest number, two to five lines of business context including source freshness, and one table (top evidenced fixes, or the largest Advisor savings opportunities with annual estimate, currency, term and lastUpdated when savings were asked, every cell of a row copied from the same recommendation record). Unknown or not-applicable dimensions score null with a reason, never zero; an empty resource group is not billable waste.
        """;

    private static readonly TokenRequestContext CognitiveServicesScope =
        new(new[] { "https://cognitiveservices.azure.com/.default" });

    private readonly AiTelemetry _telemetry;
    private readonly CopilotClient _copilotClient;
    private readonly PersistentIdentity _identity;
    private readonly TokenCredential _credential;
    private readonly string _endpoint;
    private readonly string _deployment;
    private readonly string _reasoningEffort;
    private readonly List<AIFunctionDeclaration> _sharedTools;
    private readonly ILogger _logger;

    private readonly SemaphoreSlim _bearerTokenLock = new(1, 1);

    // One session setup at a time per user. /api/chat/warmup (fired when the chat
    // UI mounts) and the user's first prompt otherwise race: both miss
    // CurrentSessionId, both fall through to CreateNewAsync, and the user ends up
    // with TWO sessions — one immediately orphaned. The loser then tries to resume
    // the winner's id and the SDK throws "Session '…' is already tracked by this
    // client", which this class handles by creating yet another session. Observed
    // locally as e2a28805 → f42137a7 → 5d571145 for a single "hi". Serialising
    // setup per user collapses that back to exactly one session.
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _userSessionGates = new();

    private SemaphoreSlim GateFor(long userId) =>
        _userSessionGates.GetOrAdd(userId, static _ => new SemaphoreSlim(1, 1));
    private string? _cachedBearerToken;
    private DateTimeOffset _bearerTokenExpiry = DateTimeOffset.MinValue;

    // BYOK token lifecycle (SDK 1.0.7+): ProviderConfig.BearerTokenProvider is an
    // on-demand callback — the runtime requests a fresh token from this process
    // BEFORE EVERY outbound model request (it does no caching; we cache in
    // GetAzureOpenAIBearerTokenAsync). This replaces the pre-1.0.7 hack where
    // BearerToken was a static string baked into the CLI at session creation and
    // sessions had to be proactively recycled (ResumeSessionAsync) before the
    // ~1h AOAI token expired. Live sessions can now stay up indefinitely.

    // Root for SDK session-state. On Azure App Service /home is a persistent
    // Azure Files mount, so chat history survives restarts.
    private static readonly string CopilotHome =
        Environment.GetEnvironmentVariable("COPILOT_HOME")
        ?? Path.Combine(Path.GetTempPath(), "copilot");

    public string Deployment => _deployment;

    /// <summary>
    /// Per-turn effort routing: trivial prompts (greetings, acknowledgements)
    /// don't need deep deliberation — run them at "low" for a ~2-3s first token
    /// instead of ~6s. Returns null for non-reasoning models (no effort concept).
    /// </summary>
    public string? GetEffortForTurn(bool trivialTurn)
    {
        if (!IsReasoningModel(_deployment)) return null;
        return trivialTurn ? "low" : _reasoningEffort;
    }

    /// <summary>
    /// Resolves the per-user working directory used to scope the SDK's session
    /// store. Entra-connected users get a stable tenant-and-object scoped path
    /// under <c>$COPILOT_HOME/users/</c> so their conversations survive restarts
    /// and isolate from other tenants; anonymous users get an ephemeral
    /// per-process directory that won't show up in any session list.
    /// </summary>
    private string GetWorkingDirectory(long userId, string? entraTenantId, string? entraOid)
    {
        EnsureRootExists();
        if (!string.IsNullOrWhiteSpace(entraTenantId) && !string.IsNullOrWhiteSpace(entraOid))
            return _identity.GetOwnedUserDirectory(entraTenantId, entraOid);
        if (!string.IsNullOrWhiteSpace(entraTenantId) || !string.IsNullOrWhiteSpace(entraOid))
            throw new InvalidOperationException(
                "The Entra tenant and object identifiers must be supplied together.");

        var subdir = Path.Combine(CopilotHome, "anon", userId.ToString());
        Directory.CreateDirectory(subdir);
        return subdir;
    }

    private static void EnsureRootExists()
    {
        try { Directory.CreateDirectory(CopilotHome); } catch { }
    }

    // The Copilot CLI's `task` tool spawns a NESTED general-purpose agent that
    // can loop for many minutes on a single call (App Insights showed one
    // 8-minute Tool:task span inside a 12.7-minute chat turn). FinOps work
    // never needs a sub-agent — the model has direct tools for everything —
    // so exclude it from every session.
    //

    private CopilotSessionFactory(
        AiTelemetry telemetry,
        CopilotClient copilotClient,
        PersistentIdentity identity,
        TokenCredential credential,
        string endpoint,
        string deployment,
        string reasoningEffort,
        List<AIFunctionDeclaration> sharedTools,
        ILogger logger)
    {
        _telemetry = telemetry;
        _copilotClient = copilotClient;
        _identity = identity;
        _credential = credential;
        _endpoint = endpoint;
        _deployment = deployment;
        _reasoningEffort = reasoningEffort;
        _sharedTools = sharedTools;
        _logger = logger;
    }

    public static Task<CopilotSessionFactory> CreateAsync(
        AiTelemetry telemetry,
        PersistentIdentity identity,
        MicrosoftOAuthOptions oauthOptions,
        string azureOpenAIEndpoint,
        string azureOpenAIDeployment,
        string reasoningEffort,
        ILoggerFactory loggerFactory,
        string? azureOpenAITenantId = null) =>
        CreateAsync(null, telemetry, identity, oauthOptions, azureOpenAIEndpoint, azureOpenAIDeployment,
            reasoningEffort, loggerFactory, azureOpenAITenantId);

    internal static async Task<CopilotSessionFactory> CreateAsync(
        TokenCredential? credential,
        AiTelemetry telemetry,
        PersistentIdentity identity,
        MicrosoftOAuthOptions oauthOptions,
        string azureOpenAIEndpoint,
        string azureOpenAIDeployment,
        string reasoningEffort,
        ILoggerFactory loggerFactory,
        string? azureOpenAITenantId = null)
    {
        // Forward CLI telemetry (GenAI + MCP semantic conventions) to the local
        // OTel collector when one is configured. The collector translates OTLP into
        // Azure Monitor format and ships it to Application Insights so we get full
        // tool-call and LLM-roundtrip visibility without any custom span wiring.
        var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        var clientOptions = new CopilotClientOptions
        {
            Mode = CopilotClientMode.Empty,
            UseLoggedInUser = false,
            // Point the CLI's session-state directory at the persistent /home
            // Azure Files mount on App Service. Replaces the older HOME env var
            // hack — same effect, but explicit. Falls back to Path.GetTempPath()
            // locally when COPILOT_HOME isn't set.
            BaseDirectory = CopilotHome,
            // Disconnect idle sessions from the in-memory CLI after 30 min to free
            // resources. Disk state is preserved — ResumeSessionAsync rehydrates
            // from /home/copilot/.copilot/session-state/{id}/ on the next prompt.
            SessionIdleTimeoutSeconds = 1800,
        };
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            clientOptions.Telemetry = new TelemetryConfig
            {
                OtlpEndpoint = otlpEndpoint,
                CaptureContent = false,
                SourceName = "AzureFinOps.AI.CLI",
            };
        }
        var copilotClient = new CopilotClient(clientOptions);
        await copilotClient.StartAsync();

        // BYOK credential: prefers a managed identity in Azure (App Service / Container Apps),
        // falls back to az CLI / Environment / env vars locally. Grant the identity the
        // "Cognitive Services User" role on the Azure OpenAI resource.
        //
        // Exclude credentials that shell out to find an account and frequently
        // hang locally — VisualStudioCredential.RunProcessesAsync is the proven
        // offender (Roberto's stack trace 2026-05-08); VS Code and Azure
        // PowerShell credentials exhibit the same pattern. Keep AzureCli (the
        // canonical local-dev path), Environment (CI/explicit config), and
        // ManagedIdentity/WorkloadIdentity (production) in the chain.
        //
        // ManagedIdentity is excluded OFF-Azure on purpose. Azure.Identity wraps
        // MSAL's `managed_identity_all_sources_unavailable` in a FATAL
        // AuthenticationFailedException (isCredentialUnavailable: false), so
        // DefaultAzureCredential ABORTS the chain at ManagedIdentity and never
        // reaches AzureCliCredential. On a dev box (no IMDS at 169.254.169.254)
        // that turned every chat into "ManagedIdentityCredential authentication
        // failed", even with a perfectly good `az login`. Probing IMDS also costs
        // 6 retries against an unreachable address before it gives up.
        var runningInAzure =
            Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT") is not null ||
            Environment.GetEnvironmentVariable("MSI_ENDPOINT") is not null ||
            Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID") is not null;

        credential ??= new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ExcludeInteractiveBrowserCredential = true,
            ExcludeVisualStudioCredential = true,
            ExcludeVisualStudioCodeCredential = true,
            ExcludeAzurePowerShellCredential = true,
            ExcludeManagedIdentityCredential = !runningInAzure,
            // Pin the token tenant to the AOAI resource's tenant when configured
            // (AzureOpenAI:TenantId) — local az CLI defaults may sit in another
            // tenant, and AOAI rejects cross-tenant tokens.
            TenantId = string.IsNullOrWhiteSpace(azureOpenAITenantId) ? null : azureOpenAITenantId,
        });

        var chartLogger = loggerFactory.CreateLogger("AzureFinOps.AI.Charts");
        var sharedTools = new List<AIFunctionDeclaration>();
        // HOT PATH — always loaded (schemas shipped in every request). Keep this
        // set tight: every entry costs input tokens on EVERY LLM round-trip.
        sharedTools.AddRange(ChartTools.Create(chartLogger));
        sharedTools.AddRange(FollowUpTools.Create());
        // Pricing and estimates are the most-asked questions (the sidebar leads with
        // "Compare VM pricing by region"), and deferring them costs a `skill` +
        // `view` tool-search pair — 2 extra model round-trips and ~2.3s — before the
        // real call. Measured: a stable prefix is 99.9% cache-hit (6084/6093 tokens),
        // so carrying these schemas every turn is far cheaper than the round-trips.
        sharedTools.AddRange(CostEstimateTools.Create());
        sharedTools.AddRange(CostCalculationTools.Create());

        var logger = loggerFactory.CreateLogger("AzureFinOps.AI");
        logger.LogInformation("CopilotClient started; Azure OpenAI BYOK endpoint={Endpoint} deployment={Deployment}",
            azureOpenAIEndpoint, azureOpenAIDeployment);

        return new CopilotSessionFactory(telemetry, copilotClient, identity, credential,
            azureOpenAIEndpoint, azureOpenAIDeployment, reasoningEffort, sharedTools, logger);
    }

    public List<AIFunctionDeclaration> GetOrCreateUserTools(long userId)
    {
        return _telemetry.UserTools.GetOrAdd(userId, uid =>
        {
            var tokens = _telemetry.UserTokens.GetOrAdd(uid, id => new UserTokens { UserId = id });
            var tools = new List<AIFunctionDeclaration>(_sharedTools);
            tools.AddRange(DeferredTool.WrapAll(new HtmlPresentationTools(uid).Create()));
            tools.AddRange(new ScriptTools(uid).Create());
            tools.AddRange(DeferredTool.WrapAll(new MaturityReportTools(uid).Create()));
            tools.AddRange(new AzureFinOps.Dashboard.Jobs.JobOutcomeTools(uid).Create());
            tools.AddRange(new ReportTools(uid).Create());
            tools.AddRange(new ToolResultQueryTools(uid).Create());
            var scoreTools = new ScoreTools(tokens);
            tools.AddRange(scoreTools.Create());
            // HOT PATH — one host-routed HTTP tool for ARM, Graph, KQL, Storage, Retail Prices and public pages.
            tools.AddRange(new AzureQueryTools(tokens).Create());
            tools.AddRange(new OperationTools(tokens).Create());
            // Savings ledger — flagship feature, small schemas, always available.
            tools.AddRange(new SavingsLedgerTools(tokens).Create());
            // Uploads are user-initiated and their context explicitly names this
            // tool; deferring it added minutes before even a one-row CSV lookup.
            tools.AddRange(new UploadedFileTools(tokens).Create());
            // COLD PATH — loaded on demand via tool search (see DeferredTool).
            tools.AddRange(DeferredTool.WrapAll(new FaqTools(tokens).Create()));
            return tools;
        });
    }

    private List<AIFunctionDeclaration> GetSessionTools(long userId, string sessionId) =>
        GetOrCreateUserTools(userId).Select(tool => tool is AIFunction function
            ? (AIFunctionDeclaration)new ProtectedTool(function, userId, sessionId) : tool).ToList();

    public async Task<CopilotSession> GetCurrentOrCreateAsync(
        long userId, string userLogin, string? entraTenantId, string? entraOid)
    {
        var gate = GateFor(userId);
        await gate.WaitAsync();
        try
        {
            return await GetCurrentOrCreateCoreAsync(userId, userLogin, entraTenantId, entraOid);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<CopilotSession> GetCurrentOrCreateCoreAsync(
        long userId, string userLogin, string? entraTenantId, string? entraOid)
    {
        // Fast path: user already has a current session id mapped.
        if (_telemetry.CurrentSessionId.TryGetValue(userId, out var currentId))
        {
            try
            {
                return await GetOrResumeCoreAsync(
                    userId, currentId, userLogin, entraTenantId, entraOid);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Resume failed for {User} session={SessionId}, creating new", userLogin, currentId);
                _telemetry.CurrentSessionId.TryRemove(userId, out _);
            }
        }

        // Entra-connected users may have past sessions on disk from a prior run.
        // Pick the most recently modified one as the implicit "current".
        if (!string.IsNullOrEmpty(entraOid))
        {
            try
            {
                var workdir = GetWorkingDirectory(userId, entraTenantId, entraOid);
                var listed = await _copilotClient.ListSessionsAsync(
                    new SessionListFilter { WorkingDirectory = workdir }, CancellationToken.None);
                var mostRecent = listed?
                    .OrderByDescending(s => s.ModifiedTime)
                    .FirstOrDefault();
                if (mostRecent is not null)
                {
                    _telemetry.CurrentSessionId[userId] = mostRecent.SessionId;
                    return await GetOrResumeCoreAsync(
                        userId, mostRecent.SessionId, userLogin, entraTenantId, entraOid);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ListSessionsAsync failed for {User}, falling back to fresh session", userLogin);
            }
        }

        return await CreateNewAsync(userId, userLogin, entraTenantId, entraOid);
    }

    /// <summary>
    /// Creates a brand-new Copilot session, registers it as the user's current,
    /// and returns it. The SDK auto-persists state under the per-user working
    /// directory so subsequent calls to <see cref="ListUserSessionsAsync"/>
    /// will find it.
    /// </summary>
    public async Task<CopilotSession> CreateNewAsync(
        long userId, string userLogin, string? entraTenantId, string? entraOid)
    {
        var config = await CreateSessionConfigAsync(userId, entraTenantId, entraOid);
        var session = await _copilotClient.CreateSessionAsync(config);
        _telemetry.LiveSessions[session.SessionId] = new LiveSessionInfo
        {
            Session = session,
            UserId = userId,
            BearerExpiry = _bearerTokenExpiry,
            AppliedEffort = IsReasoningModel(_deployment) ? _reasoningEffort : null,
        };
        _telemetry.CurrentSessionId[userId] = session.SessionId;
        _telemetry.ActiveSessions.Add(1);
        _logger.LogInformation("Created new Copilot session for {User} sessionId={SessionId}", userLogin, session.SessionId);
        return session;
    }

    /// <summary>
    /// Returns the live session if cached and the BYOK token is still fresh;
    /// otherwise resumes from disk (preserving the SDK-managed conversation
    /// history) and re-keys the live cache.
    /// </summary>
    public async Task<CopilotSession> GetOrResumeAsync(
        long userId, string sessionId, string userLogin, string? entraTenantId, string? entraOid)
    {
        var gate = GateFor(userId);
        await gate.WaitAsync();
        try
        {
            return await GetOrResumeCoreAsync(
                userId, sessionId, userLogin, entraTenantId, entraOid);
        }
        finally
        {
            gate.Release();
        }
    }

    // Caller must already hold the user's session gate.
    private async Task<CopilotSession> GetOrResumeCoreAsync(
        long userId, string sessionId, string userLogin, string? entraTenantId, string? entraOid)
    {
        if (_telemetry.LiveSessions.TryGetValue(sessionId, out var live))
        {
            if (live.UserId != userId)
                throw new InvalidOperationException("The session is not owned by this user.");
            // BearerTokenProvider supplies a fresh token per model request, so a
            // cached live session never goes stale on token expiry — no recycle.
            _telemetry.CurrentSessionId[userId] = sessionId;
            return live.Session;
        }

        var resumeConfig = await CreateResumeConfigAsync(
            userId, entraTenantId, entraOid, sessionId);
        var resumed = await _copilotClient.ResumeSessionAsync(sessionId, resumeConfig, CancellationToken.None);
        _telemetry.LiveSessions[sessionId] = new LiveSessionInfo
        {
            Session = resumed,
            UserId = userId,
            BearerExpiry = _bearerTokenExpiry,
            AppliedEffort = IsReasoningModel(_deployment) ? _reasoningEffort : null,
        };
        _telemetry.CurrentSessionId[userId] = sessionId;
        _telemetry.ActiveSessions.Add(1);
        _logger.LogInformation("Resumed Copilot session for {User} sessionId={SessionId}", userLogin, sessionId);
        return resumed;
    }

    /// <summary>Recycles the same session id after a "Session not found" or expiry error.</summary>
    public async Task<CopilotSession> RecycleSessionAsync(
        long userId, string sessionId, string userLogin, string? entraTenantId, string? entraOid)
    {
        await DisposeLiveAsync(sessionId);
        try
        {
            return await GetOrResumeAsync(
                userId, sessionId, userLogin, entraTenantId, entraOid);
        }
        catch
        {
            // Session vanished from disk — fall back to a fresh one.
            return await CreateNewAsync(userId, userLogin, entraTenantId, entraOid);
        }
    }

    public async Task<IReadOnlyList<SessionMetadata>> ListUserSessionsAsync(
        long userId, string? entraTenantId, string? entraOid, CancellationToken ct = default)
    {
        // Both Entra and anonymous users have a deterministic workdir scope
        // (principal-owned directory vs `/anon/{userId}`), so we can safely list either.
        var workdir = GetWorkingDirectory(userId, entraTenantId, entraOid);
        var listed = await _copilotClient.ListSessionsAsync(new SessionListFilter { WorkingDirectory = workdir }, ct);
        return listed?.OrderByDescending(s => s.ModifiedTime).ToList() ?? new List<SessionMetadata>();
    }

    /// <summary>
    /// Authoritative ownership check: returns true iff <paramref name="sessionId"/>
    /// lives under the caller's principal-owned working directory or anonymous
    /// user-id directory. All cross-session API surfaces (resume, delete,
    /// select, replay) MUST gate on this to prevent IDOR.
    /// </summary>
    public async Task<bool> UserOwnsSessionAsync(
        long userId, string? entraTenantId, string? entraOid, string sessionId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(sessionId)) return false;
        // Fast path: a freshly-created or currently-live session is recorded in
        // LiveSessions with its owning UserId. The on-disk index used by
        // ListSessionsAsync can lag behind CreateSessionAsync by a few ms, which
        // would otherwise reject a session the user just created and collapse
        // all their parallel chats onto the "current session" fallback.
        if (_telemetry.LiveSessions.TryGetValue(sessionId, out var live))
            return live.UserId == userId;
        var sessions = await ListUserSessionsAsync(userId, entraTenantId, entraOid, ct);
        return sessions.Any(s => s.SessionId == sessionId);
    }

    public async Task DeleteUserSessionAsync(long userId, string sessionId, CancellationToken ct = default)
    {
        await DisposeLiveAsync(sessionId);
        await _copilotClient.DeleteSessionAsync(sessionId, ct);
        if (_telemetry.CurrentSessionId.TryGetValue(userId, out var current) && current == sessionId)
            _telemetry.CurrentSessionId.TryRemove(userId, out _);
        _telemetry.RemoveTitle(sessionId);
        ChatEndpoints.ClearSessionContext(sessionId);
    }

    public void SetCurrentSession(long userId, string sessionId)
        => _telemetry.CurrentSessionId[userId] = sessionId;

    /// <summary>
    /// Read-only transcript load: resumes the session just long enough to read
    /// its persisted events, then disposes. Does NOT touch <see cref="AiTelemetry.CurrentSessionId"/>
    /// or the <c>ActiveSessions</c> gauge — viewing a past conversation must not
    /// switch the user's current thread or leak the live-session counter. Uses
    /// the same per-user gate as warmup/chat resume so page-load transcript replay
    /// cannot race warmup into registering the same SDK session twice.
    /// </summary>
    public async Task<IReadOnlyList<SessionEvent>> LoadTranscriptAsync(
        string sessionId, long userId, string? entraTenantId, string? entraOid,
        CancellationToken ct = default)
    {
        var gate = GateFor(userId);
        await gate.WaitAsync(ct);
        try
        {
            return await LoadTranscriptCoreAsync(
                sessionId, userId, entraTenantId, entraOid, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    // Caller must already hold the user's session gate.
    private async Task<IReadOnlyList<SessionEvent>> LoadTranscriptCoreAsync(
        string sessionId, long userId, string? entraTenantId, string? entraOid,
        CancellationToken ct)
    {
        _telemetry.LiveSessions.TryGetValue(sessionId, out var live);
        return await ReadTranscriptWithRecoveryAsync(
            () => UserOwnsSessionAsync(userId, entraTenantId, entraOid, sessionId, ct),
            live is null ? null : () => live.Session.GetEventsAsync(ct),
            () => DisposeLiveAsync(sessionId),
            async () =>
            {
                var resumeConfig = await CreateResumeConfigAsync(
                    userId, entraTenantId, entraOid, sessionId);
                var ephemeral = await _copilotClient.ResumeSessionAsync(sessionId, resumeConfig, ct);
                try { return await ephemeral.GetEventsAsync(ct); }
                finally { try { await ephemeral.DisposeAsync(); } catch { } }
            });
    }

    internal static async Task<IReadOnlyList<SessionEvent>> ReadTranscriptWithRecoveryAsync(
        Func<Task<bool>> verifyOwnership,
        Func<Task<IReadOnlyList<SessionEvent>>>? readCached,
        Func<Task> evict,
        Func<Task<IReadOnlyList<SessionEvent>>> resume)
    {
        if (!await verifyOwnership())
            throw new HistoryUnavailableException();

        if (readCached is not null)
        {
            try { return await readCached(); }
            catch (Exception exception) when (IsMissingSession(exception)) { await evict(); }
        }
        try { return await resume(); }
        catch (Exception exception) when (IsMissingSession(exception))
        {
            throw new HistoryUnavailableException();
        }
    }

    private static bool IsMissingSession(Exception exception) => exception is not OperationCanceledException
        && exception.Message.Contains("Session not found", StringComparison.OrdinalIgnoreCase);

    internal sealed class HistoryUnavailableException() : Exception("The retained conversation history is unavailable.");

    /// <summary>Lists session metadata under the persistent-user roots only — the
    /// janitor must never touch sessions outside <c>$COPILOT_HOME/users/</c> and
    /// <c>$COPILOT_HOME/anon/</c> (e.g. another container instance sharing the
    /// same Azure Files mount, or unrelated SDK state).</summary>
    public async Task<IReadOnlyList<SessionMetadata>> ListAllManagedSessionsAsync(CancellationToken ct = default)
    {
        var listed = await _copilotClient.ListSessionsAsync(new SessionListFilter(), ct);
        if (listed is null) return Array.Empty<SessionMetadata>();
        var usersRoot = Path.Combine(CopilotHome, "users");
        var anonRoot = Path.Combine(CopilotHome, "anon");
        return listed.Where(s =>
        {
            // Linux file paths are case-sensitive; Entra OIDs are lowercase
            // GUIDs and our roots are constructed from a known constant, so
            // an Ordinal compare is both correct and slightly faster.
            var c = s.Context?.WorkingDirectory ?? "";
            return c.StartsWith(usersRoot, StringComparison.Ordinal)
                || c.StartsWith(anonRoot, StringComparison.Ordinal);
        }).ToList();
    }

    public async Task DeleteSessionByIdAsync(string sessionId, CancellationToken ct = default)
    {
        await DisposeLiveAsync(sessionId);
        _telemetry.RemoveTitle(sessionId);
        ChatEndpoints.ClearSessionContext(sessionId);
        try { await _copilotClient.DeleteSessionAsync(sessionId, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "DeleteSessionAsync failed for {SessionId}", sessionId); }
    }

    private async Task DisposeLiveAsync(string sessionId)
    {
        if (_telemetry.LiveSessions.TryRemove(sessionId, out var live))
        {
            _telemetry.ActiveSessions.Add(-1);
            try { await live.Session.DisposeAsync(); } catch { }
        }
    }

    private async Task<SessionConfig> CreateSessionConfigAsync(
        long userId, string? entraTenantId, string? entraOid)
    {
        var sessionId = Guid.NewGuid().ToString();
        // Seed token eagerly so the very first model call doesn't pay the
        // credential round-trip; afterwards BearerTokenProvider serves refreshes.
        var bearerToken = await GetAzureOpenAIBearerTokenAsync();
        var effort = IsReasoningModel(_deployment) ? _reasoningEffort : null;
        _logger.LogInformation("SessionConfig(create) model={Model} reasoningEffort={Effort} isReasoning={IsReasoning}",
            _deployment, effort ?? "<null>", IsReasoningModel(_deployment));
        var config = new SessionConfig
        {
            SessionId = sessionId,
            Model = _deployment,
            ReasoningEffort = effort,
            // Stream concise reasoning summaries so the UI can show live
            // "thinking" feedback during the otherwise-silent reasoning phase.
            ReasoningSummary = effort is null ? null : ReasoningSummary.Concise,
            Streaming = true,
            Tools = GetSessionTools(userId, sessionId),
            WorkingDirectory = GetWorkingDirectory(userId, entraTenantId, entraOid),
            Provider = new ProviderConfig
            {
                // Azure AI Foundry exposes an OpenAI-compatible endpoint at /openai/v1/.
                // GPT-5 series models AND ReasoningEffort require the Responses API, which is
                // only reachable via the "openai" provider type with WireApi="responses".
                // The classic "azure" type uses the Chat Completions API (api-version 2024-10-21)
                // and does not support reasoning on these models — the request never completes.
                // See GitHub Copilot SDK BYOK docs (Azure AI Foundry OpenAI-compatible endpoint):
                // https://github.com/github/copilot-sdk/blob/main/docs/auth/byok.md
                Type = "openai",
                BaseUrl = $"{_endpoint.TrimEnd('/')}/openai/v1/",
                // Static seed for the first request; the provider callback below
                // takes precedence and is invoked per outbound model request.
                BearerToken = bearerToken,
                BearerTokenProvider = _ => GetAzureOpenAIBearerTokenAsync(),
                WireApi = "responses",
            },
            SystemMessage = new SystemMessageConfig
            {
                // Replace (not Append): Append ships the CLI's built-in multi-
                // thousand-token "GitHub Copilot CLI terminal assistant" prompt
                // (tone/code-editing rules irrelevant here) in EVERY request, on
                // top of ours. Our SystemPrompt is self-contained for FinOps.
                // Tool-calling still works — schemas travel at protocol level.
                Mode = SystemMessageMode.Replace,
                Content = SystemPrompt,
            },
        };
        RuntimePolicy.Apply(config);
        return config;
    }

    private async Task<ResumeSessionConfig> CreateResumeConfigAsync(
        long userId, string? entraTenantId, string? entraOid, string sessionId)
    {
        var bearerToken = await GetAzureOpenAIBearerTokenAsync();
        var effort = IsReasoningModel(_deployment) ? _reasoningEffort : null;
        _logger.LogInformation("SessionConfig(resume) model={Model} reasoningEffort={Effort} isReasoning={IsReasoning} — NOTE: CLI may retain original-session effort",
            _deployment, effort ?? "<null>", IsReasoningModel(_deployment));
        var config = new ResumeSessionConfig
        {
            Model = _deployment,
            ReasoningEffort = effort,
            // Stream concise reasoning summaries so the UI can show live
            // "thinking" feedback during the otherwise-silent reasoning phase.
            ReasoningSummary = effort is null ? null : ReasoningSummary.Concise,
            Streaming = true,
            Tools = GetSessionTools(userId, sessionId),
            WorkingDirectory = GetWorkingDirectory(userId, entraTenantId, entraOid),
            Provider = new ProviderConfig
            {
                // Azure AI Foundry exposes an OpenAI-compatible endpoint at /openai/v1/.
                // GPT-5 series models AND ReasoningEffort require the Responses API, which is
                // only reachable via the "openai" provider type with WireApi="responses".
                // The classic "azure" type uses the Chat Completions API (api-version 2024-10-21)
                // and does not support reasoning on these models — the request never completes.
                // See GitHub Copilot SDK BYOK docs (Azure AI Foundry OpenAI-compatible endpoint):
                // https://github.com/github/copilot-sdk/blob/main/docs/auth/byok.md
                Type = "openai",
                BaseUrl = $"{_endpoint.TrimEnd('/')}/openai/v1/",
                // Static seed for the first request; the provider callback below
                // takes precedence and is invoked per outbound model request.
                BearerToken = bearerToken,
                BearerTokenProvider = _ => GetAzureOpenAIBearerTokenAsync(),
                WireApi = "responses",
            },
            SystemMessage = new SystemMessageConfig
            {
                // Replace (not Append) — see CreateSessionConfigAsync for rationale.
                Mode = SystemMessageMode.Replace,
                Content = SystemPrompt,
            },
        };
        RuntimePolicy.Apply(config);
        return config;
    }

    private async Task<string> GetAzureOpenAIBearerTokenAsync()
    {
        if (_cachedBearerToken is not null && _bearerTokenExpiry > DateTimeOffset.UtcNow.AddMinutes(5))
            return _cachedBearerToken;

        await _bearerTokenLock.WaitAsync();
        try
        {
            if (_cachedBearerToken is not null && _bearerTokenExpiry > DateTimeOffset.UtcNow.AddMinutes(5))
                return _cachedBearerToken;

            var tokenResult = await _credential.GetTokenAsync(CognitiveServicesScope, CancellationToken.None);
            _cachedBearerToken = tokenResult.Token;
            _bearerTokenExpiry = tokenResult.ExpiresOn;
            _logger.LogInformation("Azure OpenAI bearer token refreshed, expires at {Expiry}", _bearerTokenExpiry);
            return _cachedBearerToken;
        }
        finally
        {
            _bearerTokenLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _copilotClient.DisposeAsync(); } catch { }
        _bearerTokenLock.Dispose();
    }

    private static readonly HttpClient _titleHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>
    /// Generates a short (max ~6-word) human-readable title for the conversation
    /// using the same Azure OpenAI deployment that powers the chat. The Copilot
    /// CLI's <c>session.title_changed</c> event in this build just echoes the
    /// user's first prompt verbatim, so we override it with a real summary.
    /// Returns null on any failure (caller falls back to existing title).
    /// </summary>
    public async Task<string?> GenerateTitleAsync(string userMessage, string assistantReply, CancellationToken ct = default)
    {
        try
        {
            var token = await GetAzureOpenAIBearerTokenAsync();
            // Use the same OpenAI-compatible /openai/v1/ surface the BYOK chat
            // path uses — the classic ?api-version=2024-10-21 endpoint predates
            // the GPT-5 series and rejects reasoning parameters.
            var url = $"{_endpoint.TrimEnd('/')}/openai/v1/chat/completions";
            var messages = new object[]
            {
                new { role = "system", content = "Summarise the user's question into a 3-6 word title for a chat sidebar. No quotes, no trailing punctuation, no emoji. Title-case." },
                new { role = "user", content = $"USER: {Truncate(userMessage, 800)}\n\nASSISTANT: {Truncate(assistantReply, 800)}" },
            };
            // GPT-5 / o-series use `max_completion_tokens`; grok and GPT-4 use `max_tokens`.
            // Reasoning models spend completion tokens on hidden reasoning FIRST —
            // with a tiny cap the entire budget goes to reasoning and content comes
            // back empty. Give them headroom + minimal reasoning effort so the
            // title lands in the visible content.
            object body = IsReasoningModel(_deployment)
                ? new { model = _deployment, messages, max_completion_tokens = 512, reasoning_effort = "low" }
                : new { model = _deployment, messages, max_tokens = 24 };
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await _titleHttp.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var title = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString()?.Trim().Trim('"', '\'', '.', ' ');
            if (string.IsNullOrWhiteSpace(title)) return null;
            return title.Length > 80 ? title[..80] : title;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Title generation failed");
            return null;
        }
    }

    private static string Truncate(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];

    /// <summary>
    /// Returns true if the deployment is a reasoning model that accepts the
    /// <c>reasoning_effort</c> parameter and the <c>max_completion_tokens</c> field.
    /// GPT-5.x and o-series qualify; grok-4.3 / grok-4 / GPT-4.x do not.
    /// (grok-4-20-reasoning is the xAI reasoning variant — add it here if deployed.)
    /// </summary>
    private static bool IsReasoningModel(string deployment)
    {
        if (string.IsNullOrEmpty(deployment)) return false;
        var d = deployment.ToLowerInvariant();
        if (d.StartsWith("grok")) return d.Contains("reasoning");
        return d.StartsWith("gpt-5") || d.StartsWith("o1") || d.StartsWith("o3") || d.StartsWith("o4") || d.StartsWith("codex");
    }
}
