// FinOps maturity sidebar categories — extracted from ChatView.vue for maintainability.
// Pure static data; no runtime dependencies.

// The start page's questions: the five most important per section, most
// important first, because phones show only the first three. Each button shows
// a short question a customer would ask support (label); the prompt it sends is
// the precise, slightly technical request the agent answers. Prompts never name
// a model version, so the agent always looks up the newest models. A prompt
// that is a curated live-evaluation case is identified by a hash of its text:
// changing that text changes the gate (CURATED_CASE_IDS).

// The products a start page question covers, shown as their official icons on
// its card (public/product-icons); a section's sources apply to its questions
// that name none.
const AZURE = "azure";
const COPILOT = "copilot";
const FOUNDRY = "foundry";
const AGENT_365 = "agent365";
const GITHUB = "github";

// AI governance and security prompts — public, answered briefly from current
// Microsoft Learn pages. Ranked by what customers raise most: knowing every agent
// and its owner, what the newest Microsoft 365 suite adds, auditing what agents
// do, choosing the control plane, and allowing only approved models and agents.
const aiGovernancePrompts = [
  {
    label: "How do I find all our AI agents?",
    sources: [AGENT_365, FOUNDRY],
    prompt:
      "How do I build one inventory of every AI agent in my organization (Foundry, Copilot Studio and agents on other clouds) with a named owner for each, and find unapproved (shadow) agents? Show the steps in one table: step, Microsoft tool (linked to its Microsoft Learn page), what to do, what it needs (license, preview opt-in or admin role).",
  },
  {
    label: "What does E7 add for AI agents?",
    sources: [COPILOT, AGENT_365],
    prompt:
      "What does Microsoft 365 E7 add over Microsoft 365 E5 for AI agents, and what does it cost per user? Use current Microsoft Learn and microsoft.com licensing pages. Show one table: capability, in E5, in E7, what it does for agents. Say which items you could not confirm.",
  },
  {
    label: "How do we audit what agents do?",
    sources: [AGENT_365, FOUNDRY],
    prompt:
      "How do I trace every action an AI agent takes back to its agent identity and an accountable owner? Cover Microsoft Entra Agent ID, Entra sign-in and audit logs, Microsoft Purview audit and Foundry tracing. Show one table: what to set up, where, what it proves to an auditor.",
  },
  {
    label: "Agent 365, Foundry or API Center?",
    sources: [AGENT_365, FOUNDRY],
    prompt:
      "Compare Agent 365, Foundry Control Plane and Azure API Center for governing AI agents across the organization. Show one table: product, what it controls, who uses it, when to choose it. Then recommend which one to use as the org-wide control plane.",
  },
  {
    label: "Can we allow only approved models?",
    sources: [FOUNDRY, AZURE],
    prompt:
      "How do I allow only approved AI models in Foundry (for example with Azure Policy) and require approval before a new agent goes live? Show the steps in one table: control, how to set it up, what it blocks.",
  },
];

// AI and LLM pricing prompts — public. Ranked by what customers raise most:
// planning and justifying a budget across seats, credits and tokens, choosing a
// model for its quality and price (current benchmarks and Azure retail prices),
// seeing who spends what, capping spend, and reconciling costs with the invoice.
// No prompt names a model service or version, so answers cover every model.
const aiPricingPrompts = [
  {
    label: "How do we budget and justify AI?",
    sources: [COPILOT, FOUNDRY, GITHUB],
    prompt:
      "How do I plan and justify a monthly AI budget when Microsoft 365 Copilot is billed per user, GitHub Copilot per seat plus AI credits, Copilot Studio per credit and Foundry models per token? Show one table: product, how it is billed, what drives the cost, how to forecast it.",
  },
  {
    label: "Which AI model is best value?",
    sources: [FOUNDRY],
    prompt:
      "Which of the newest models in Foundry give the best quality for the price? Compare the newest OpenAI, Anthropic, Meta, Mistral, DeepSeek and xAI models on their latest published quality benchmark and current Azure token prices. Show one table: model, quality score, input price per 1M tokens, output price per 1M tokens. Give each score's source and date in a note, then name the best-value pick and the measure it is ranked by.",
  },
  {
    label: "Who is spending what on AI?",
    sources: [AZURE, FOUNDRY],
    prompt:
      "How do I see AI spend per team, app and cost centre, and charge it back when several teams share one model deployment? Cover Azure Cost Management (tags, cost allocation rules, Foundry projects) and API Management token metrics. Show the steps in one table: step, tool, what it gives you.",
  },
  {
    label: "Can we cap AI spending?",
    sources: [AZURE, FOUNDRY, GITHUB],
    prompt:
      "How do I set AI budgets and spending limits per user, app and company? Cover Azure budgets and alerts, model deployment token quotas, API Management token limits and GitHub Copilot AI credit budgets. Show one table: control, scope, what happens when the limit is reached.",
  },
  {
    label: "Why don't costs match my invoice?",
    sources: [AZURE],
    prompt:
      "Why do my AI costs in Azure Cost Management differ from my Azure invoice, and how do I reconcile them? Cover actual vs amortized cost, billing period vs calendar month, credits, taxes, currency and data delay. Show one table: cause, how to check it.",
  },
];

// Infrastructure retail-pricing prompts — work without an Azure login. Pricing
// the user's own configuration was the most common typed request in the
// November 2026 audit (6 of 37 people over 14 days, often "like the pricing
// calculator") and no card offered it, so it leads here: the card asks for
// every input in one message and prices them all in the next reply. The next
// three are curated live-evaluation cases identified by their text
// (CURATED_CASE_IDS); "Which storage tier is cheapest?" left the page (one
// click in that audit) and stays an evaluation-only case.
const infrastructurePricingPrompts = [
  {
    label: "Price my own setup",
    prompt:
      "Price my own Azure setup like the Azure pricing calculator. Before looking up any price, ask me in one short message for everything you need: each service with its size or tier and quantity, the region, the operating system or license, storage, the currency, and the pricing term (pay-as-you-go, a 1-year or 3-year reservation, or a savings plan), and say which default you will use for anything I leave out. Then price every line from the Azure Retail Prices API and show one table: service, configuration, monthly price.",
  },
  {
    label: "What will my 3-tier app cost?",
    prompt:
      "Estimate monthly cost for a 3-tier app in East US: 2x D4s_v5 VMs, Azure SQL 4-vCore 500 GB, 1 TB Premium SSD, Standard LB.",
  },
  {
    label: "Which region is cheapest for a VM?",
    prompt:
      "Compare the monthly cost of a D4s_v5 VM across the 10 cheapest Azure regions. Show a bar chart.",
  },
  {
    label: "Which database is cheapest?",
    prompt:
      "Compare monthly cost of Azure SQL 8-vCore vs Cosmos DB 10K RU/s vs PostgreSQL Flexible 8-vCore with 500 GB storage in East US.",
  },
  {
    label: "Reservation or savings plan?",
    prompt:
      "Compare the monthly price of a D4s_v5 Linux VM in East US on pay-as-you-go, a 1-year and 3-year reservation, and a 1-year and 3-year savings plan, using Azure retail prices. Show one table: option, monthly cost, saving versus pay-as-you-go.",
  },
];

// Signed-in prompts, shown first in the AI section. Microsoft 365 Copilot cost
// management (announced 17 November 2026, Microsoft Graph beta) reports Copilot
// Credits per user and service. The cards rank the top Copilot users with what
// they use Copilot for, show the admin center's Services & agents view, price
// the user's own Copilot work, show Copilot users next to the Azure VMs that
// cost the most, and put Copilot, Foundry and GitHub spend in one view (no
// admin center shows these together). Every name follows the Microsoft 365
// admin center (views, columns, services) or Azure Cost Management (services),
// never a grouping of our own. All of them only read; proposing a spending
// limit is in the Prompts library. A user without the Copilot cost consent
// gets its button in the answer.
const connectedAiPrompts = [
  {
    label: "Who uses Copilot most, and for what?",
    sources: [COPILOT, AGENT_365],
    badge: "Preview",
    prompt:
      "Who are our top 5 Copilot users, what did they spend Copilot Credits on, and under which spending policy? Find the top users in the daily per-user reports for the last 7 completed days and include me. For each user, read their credits this month per service from their user balance, with each service's spending policy (its name, its monthly limit for all users and per user, what remains, and whether the user may still use the service) and their display name; for me, also read today's sessions from my raw usage. Name every service as the Microsoft 365 admin center does (for example Code, Cowork). Show one table with one row per user and service: user, service, what it is used for (in plain words, as Microsoft Learn describes it), credits this month, spending policy and what remains. Then list my sessions today with their times and credits, and say how current each figure is.",
  },
  {
    label: "Copilot services & agents",
    sources: [COPILOT, AGENT_365],
    badge: "Preview",
    prompt:
      "Show the Services & agents view of Copilot cost management's Consumption for Month to date, as the Microsoft 365 admin center shows it. Take each service's credits from the daily per-service reports for the completed days of this month, and today's use from the raw usage of me and the top users in the daily per-user reports, including services that no policy's service settings list. Show one table with the admin center's columns and names: Service or agent (each service by its admin center name, such as Code, Cowork, Opal or Copilot Managed Runtime), Active users, Session count, Total credits used, Last activity date, plus the list price in USD at the pay-as-you-go rate per Copilot Credit from the Azure Retail Prices API. Fill Active users and Last activity date from the usage read, saying which users it covers, and write Unavailable only where no read gives a value, as the admin center does. Under the table, compare the credits listed with the month's total by funding class and say how many credits Microsoft Graph has not reported by service yet, and how current each figure is.",
  },
  {
    label: "What did my Copilot work cost?",
    sources: [COPILOT, AZURE],
    badge: "Preview",
    prompt:
      "What did my Copilot work cost today? Read my Copilot use today from my raw usage: each session with its start and end time and credits, and the credits per service, each named as the Microsoft 365 admin center names it (for example Code, Cowork). Price it at the pay-as-you-go list price per Copilot Credit from the Azure Retail Prices API, and look for what Azure Cost Management has billed this month under Microsoft Copilot Studio. Show one table: time (UTC), service, credits, list-price cost in USD rounded to the cent. Then give the total, my credits this month under my spending policy and its limit, the billed amount, and how current each figure is.",
  },
  {
    label: "All our AI spend in one view",
    sources: [COPILOT, FOUNDRY, GITHUB],
    badge: "Preview",
    prompt:
      "Show all our AI spend this month in one view, each figure also as cost in our billing currency. Microsoft 365 Copilot: the tenant's Copilot Credits this month by service, from the daily per-service reports for the completed days of this month and my raw usage today, each service named as the Microsoft 365 admin center names it (for example Code, Cowork), priced at the pay-as-you-go list price per Copilot Credit from the Azure Retail Prices API in that currency. Azure: actual cost this month by service name as Azure Cost Management names it, for Microsoft Copilot Studio (where Copilot Credits are billed), Foundry Models, Foundry Tools and GitHub, with Foundry Models cost per deployment tag and agent.name tag, sent together in one round. Show one table with one row per service: source (Microsoft 365 admin center or Azure Cost Management), service, Copilot Credits, cost, largest deployment or agent where known. Under the table, give the month's total Copilot Credits by funding class and how many of them Microsoft Graph has not reported by service yet, then the total cost, counting Copilot once (its credits at list price, or what Azure billed for them), which service to review first, and how current each figure is.",
  },
  {
    label: "Copilot user and VM spend",
    sources: [COPILOT, AZURE],
    badge: "Preview",
    prompt:
      "Who are our top Copilot users this month, and which Azure VMs cost the most? Find the 5 users who used the most Copilot Credits in the daily per-user reports for the last 7 completed days, include me, and take each one's credits this month from their Copilot user balance, with their display name. Add the 5 VMs with the highest actual cost this month from Azure Cost Management. Show one table: source (Copilot or Azure VM), user or VM, this month (with its unit, credits or the billing currency). Then say how much of our Copilot credit balance is left and how current each figure is.",
  },
];

// Signed-in prompts, shown first in the third section under its connected
// label. In the November 2026 audit, signed-in users typed a FinOps assessment
// report (3 of 8 people, across six chats) and ran the savings-and-Crawl
// prompt, while three earlier cards here (Spot eligibility, storage tier
// downgrade, workload projection) got no click in 14 days. The savings card
// sends the gate-tested Crawl prompt word for word, so it stays one case.
const connectedCostPrompts = [
  {
    label: "Where can I save money?",
    prompt: "Where are my biggest Azure savings opportunities? Score my Crawl maturity.",
  },
  {
    label: "FinOps assessment report",
    prompt:
      "Create a FinOps maturity assessment report of my connected Azure subscriptions, aligned with the FinOps Foundation framework, as one HTML document: an executive summary with this month's cost, each capability's score with its evidence, priority and next step, and a phased roadmap. Score only from what the Azure APIs return, and mark any capability you could not check as not assessed.",
  },
  {
    label: "Cheaper regions for my top 5 VMs",
    prompt:
      "List my top 5 most expensive VMs by monthly cost. For each, find the 3 Azure regions with the lowest retail price for the same VM size and OS that are cheaper than its current region, and show a comparison table with the potential monthly savings if I migrated.",
  },
  {
    label: "Reservation savings on my VMs",
    prompt:
      "For each VM SKU I'm currently running on pay-as-you-go, compute the savings from moving to a 1-year and 3-year reservation using Azure retail pricing. Sort by total annual savings.",
  },
];

// Every public prompt, for the live-evaluation catalog and callers of .publicPrompts.
const publicPricingPrompts = [
  ...aiGovernancePrompts,
  ...aiPricingPrompts,
  ...infrastructurePricingPrompts,
];

// The start page shows the public questions as three sections, most important
// first; once Azure is connected, a section lists its signed-in prompts first,
// and the price section is titled for the user's own estate. Titles say what the
// questions help with.
export const pricingSections = [
  {
    key: "ai-governance",
    label: "Govern AI agents",
    icon: "smartToy",
    prompts: aiGovernancePrompts,
    connectedPrompts: [],
  },
  {
    key: "ai-pricing",
    label: "Control AI spending",
    icon: "attachMoney",
    prompts: aiPricingPrompts,
    connectedPrompts: connectedAiPrompts,
  },
  {
    key: "infrastructure-pricing",
    label: "Compare Azure prices",
    connectedLabel: "Optimize my Azure costs",
    icon: "tag",
    sources: [AZURE],
    prompts: infrastructurePricingPrompts,
    connectedPrompts: connectedCostPrompts,
  },
];

export const pricingCategory = {
  key: "pricing",
  label: "Pricing & Estimates",
  icon: "$",
  colorClass: "cat-pricing",
  requiresAzure: false,
  publicPrompts: publicPricingPrompts,
  // Every signed-in start-page prompt, for the live-evaluation catalog.
  connectedPrompts: [...connectedAiPrompts, ...connectedCostPrompts],
  sections: pricingSections,
  // Backwards-compat: callers that read .prompts get the public list.
  prompts: publicPricingPrompts,
};
const maturityCategories = [
  // ── CRAWL — "Where am I?" Visibility & baseline ──
  {
    key: "crawl",
    label: "Crawl",
    subtitle: "Visibility & Baseline",
    icon: "1",
    colorClass: "cat-crawl",
    requiresAzure: true,
    prompts: [
      {
        label: "Score Crawl maturity",
        prompt:
          "Where are my biggest Azure savings opportunities? Score my Crawl maturity.",
      },
      // Cost visibility
      {
        label: "Cost this month",
        prompt:
          "Show my Azure cost for the current month grouped by service. Chart it.",
      },
      {
        label: "Cost trend (30 days)",
        prompt:
          "Show my daily Azure spend for the last 30 days as a line chart.",
      },
      {
        label: "Cost by subscription",
        prompt:
          "Compare Azure costs across all my subscriptions for the current month. Show a table ranking subscriptions by spend, with subscription name, cost, and month-over-month change.",
      },
      {
        label: "Cost by resource group",
        prompt:
          "Break down my current month's Azure cost by resource group and show a bar chart.",
      },
      {
        label: "Cost by tag",
        prompt:
          "Break down my Azure costs by the cost-center tag for the current month. Show a table ranked by spend, including untagged cost. Which cost centers have the highest spend?",
      },
      {
        label: "Cost by region",
        prompt:
          "Break down my Azure spend by region/location for the current month. Show a bar chart and identify which regions are the most expensive. Are there opportunities to move workloads to cheaper regions?",
      },
      {
        label: "Top 10 costly resources",
        prompt:
          "What are my top 10 most expensive Azure resources this month? Show a chart.",
      },
      {
        label: "Month-over-month change",
        prompt:
          "Compare this month's Azure spend to last month by service. Highlight the biggest increases and show a chart.",
      },
      // Tagging
      {
        label: "Tag compliance audit",
        prompt:
          "Audit tag compliance across all my subscriptions. What percentage of resources have cost-center, environment, and owner tags? List the resource groups with the worst tag coverage so I can follow up with the responsible teams.",
      },
      // Orphaned resources
      {
        label: "Orphaned resources",
        prompt:
          "Find all orphaned resources across my subscriptions — unattached disks, unused public IPs, empty resource groups, NICs not attached to VMs, and NSGs not attached to any subnet or NIC. List them with their monthly cost and tags so I can clean them up.",
      },
      {
        label: "Unattached disks",
        prompt:
          "How many unattached disks do I have and what would be my savings if I remove them? List them in a table ranked by highest savings potential at the top. Include the disk name, size, SKU, monthly cost, resource group, and all tags so I can plan outreach to the responsible teams.",
      },
      // Advisor
      {
        label: "Advisor recommendations",
        prompt:
          "What cost optimization recommendations does Azure Advisor have for me? Group them by impact (high, medium, low) and show the estimated annual savings for each.",
      },
      // Budgets
      {
        label: "Budget vs actual",
        prompt:
          "Show my Azure budgets vs actual spend for the current billing period. Which budgets are at risk of being exceeded? Show a gauge chart per budget.",
      },
      // Inventory
      {
        label: "Resource inventory",
        prompt:
          "Query Resource Graph for a count of all resources by type across my subscriptions. Show a table with the full breakdown by type, sorted by count.",
      },
      {
        label: "My subscriptions",
        prompt:
          "List all my Azure subscriptions with their states and subscription IDs.",
      },
      {
        label: "Billing accounts",
        prompt:
          "Show my billing account structure — accounts, profiles, invoice sections.",
      },
    ],
  },
  // ── WALK — "What should I optimize?" Savings & enforcement ──
  {
    key: "walk",
    label: "Walk",
    subtitle: "Optimization & Governance",
    icon: "2",
    colorClass: "cat-walk",
    requiresAzure: true,
    prompts: [
      {
        label: "Score Walk maturity",
        prompt:
          "Score my Walk-level FinOps maturity (0-5 per dimension). Check these using Azure APIs: (1) Reservations & Savings Plans — do I have active reservations or savings plans? How many? (2) Right-sizing — how many Advisor right-sizing/resize recommendations are open? (3) Non-Prod Snoozing — what % of VMs have auto-shutdown schedules? (4) Tag Policy Enforcement — are there Azure Policy assignments for tagging? For each, give a score 0-5 and a one-line reason, then call ReportMaturityScore with level 'walk' and the scores array.",
      },
      // Reservations & savings plans
      {
        label: "Reservation utilization",
        prompt:
          "Are we using our reservations? List the usage/utilization of all my reservations and identify under-utilized ones. Show a table with the reservation name, resource type, utilization %, and the monetary waste from unused capacity. Sort by lowest utilization first.",
      },
      {
        label: "Savings plan coverage",
        prompt:
          "Show my savings plan and reservation coverage for compute. What percentage of my eligible spend is covered by commitments vs pay-as-you-go? Show a pie chart.",
      },
      {
        label: "Reservation recommendations",
        prompt:
          "Based on my usage patterns, what new reservation purchases does Azure recommend? Show the recommended reservations with resource type, term, estimated monthly savings, and upfront cost in a table.",
      },
      {
        label: "Savings plan vs reservation",
        prompt:
          "Based on my compute spend patterns, should I buy a savings plan or a reservation? Compare the savings from each option for my top 5 compute workloads. Show a table with resource, current monthly cost, RI savings, SP savings, and recommendation.",
      },
      {
        label: "Expiring reservations",
        prompt:
          "List all my reservations that expire in the next 90 days. For each, show the reservation name, resource type, expiry date, current utilization, and the monthly cost impact if not renewed.",
      },
      {
        label: "RI exchange opportunities",
        prompt:
          "Analyze my current reservations for exchange opportunities. Which reservations are underutilized and could be exchanged for a better-fitting SKU or region? Show the current reservation, utilization %, and recommended exchange target.",
      },
      // Right-sizing
      {
        label: "VM right-sizing",
        prompt:
          "Analyze my running VMs and identify which ones are oversized based on Advisor recommendations. For each, show current SKU, recommended SKU, current monthly cost, projected monthly cost, and monthly savings. Sort by highest savings first.",
      },
      {
        label: "Idle resources cleanup",
        prompt:
          "Find all idle or underutilized VMs, disks, public IPs, App Service plans, and load balancers across my subscriptions. For each, show the resource name, type, resource group, monthly cost, and tags in a table sorted by cost. What's my total potential savings?",
      },
      {
        label: "Dev/test savings",
        prompt:
          "Identify resources in dev/test environments (by tag or naming convention) that could be shut down, scaled down, or deallocated to save costs. Show a table with the resource, current SKU, recommended action, and estimated monthly savings.",
      },
      // Policy & tagging enforcement
      {
        label: "Policy compliance",
        prompt:
          "Show my Azure Policy compliance state. Which policies have the most non-compliant resources? List the top 10 non-compliant policies with the count of affected resources and their resource types.",
      },
      // Hybrid benefit
      {
        label: "Azure Hybrid Benefit check",
        prompt:
          "Which of my Windows VMs and SQL databases are NOT using Azure Hybrid Benefit? List them with their current monthly cost and the savings I'd get by enabling AHUB. Show total potential savings.",
      },
      // Storage optimization
      {
        label: "Storage optimization",
        prompt:
          "Find storage accounts with no recent access and recommend tiering or cleanup for cost savings. Show the storage account, current tier, last access date, size, monthly cost, and recommended tier in a table.",
      },
      {
        label: "Blob lifecycle analysis",
        prompt:
          "Analyze my blob storage accounts. Which ones are missing lifecycle management policies? For large storage accounts, estimate the savings if I moved data older than 30 days to Cool, 90 days to Cold, and 180 days to Archive tier.",
      },
      {
        label: "Log Analytics ingestion cost",
        prompt:
          "Analyze my Log Analytics workspace ingestion costs. Show data volume by table (e.g. AzureDiagnostics, Perf, ContainerLog) over the past 30 days. Which tables are the biggest cost drivers? Recommend tables to move to Basic Logs or archive tier.",
      },
      // Workload-specific
      {
        label: "App Service consolidation",
        prompt:
          "List all my App Service plans with their pricing tier, instance count, and the number of apps hosted on each. Identify plans with low utilization or only one app that could be consolidated. Show potential savings from merging underutilized plans.",
      },
      {
        label: "AKS cost optimization",
        prompt:
          "Analyze my AKS clusters. Show each cluster's node pool configuration, VM sizes, node count, and monthly cost. Identify over-provisioned node pools where CPU/memory requests are significantly below capacity. Recommend right-sizing actions with estimated savings.",
      },
      {
        label: "AKS namespace costs",
        prompt:
          "Break down my AKS cluster costs by namespace using the cost analysis add-on data. Show which namespaces drive spend, and flag workloads missing CPU/memory requests that make allocation inaccurate.",
      },
      {
        label: "Network cost review",
        prompt:
          "Review my network-related costs: ExpressRoute circuits, VPN gateways, NAT gateways, public IPs, and Application Gateways. List each resource with its SKU, monthly cost, and utilization. Flag any that appear oversized or idle.",
      },
      {
        label: "Disk SKU optimization",
        prompt:
          "List all my managed disks with their SKU (Premium, Standard SSD, Standard HDD). Identify Premium SSD disks attached to VMs with low IOPS usage that could be downgraded to Standard SSD. Show the current vs recommended cost per disk.",
      },
      {
        label: "Optimize top resource group",
        prompt:
          "I need to cost optimize my most expensive resource group. First show me my top 5 resource groups by cost this month, then give me the top Advisor recommendations and idle resources for the most expensive one.",
      },
    ],
  },
  // ── RUN — "How do I scale this?" Culture & accountability ──
  {
    key: "run",
    label: "Run",
    subtitle: "Scale & Accountability",
    icon: "3",
    colorClass: "cat-run",
    requiresAzure: true,
    prompts: [
      {
        label: "Score Run maturity",
        prompt:
          "Score my Run-level FinOps maturity (0-5 per dimension). Check these using Azure APIs: (1) Cost Exports — do any subscriptions have Cost Management exports configured? (2) Management Group Structure — how many management groups exist beyond the root? (3) Chargeback Readiness — what % of spend can be attributed via cost-center or owner tags? For each, give a score 0-5 and a one-line reason, then call ReportMaturityScore with level 'run' and the scores array.",
      },
      // Executive reporting
      {
        label: "Executive cost summary",
        prompt:
          "Create an executive summary of my Azure spend: total cost this month, month-over-month trend, top 5 services by cost, biggest cost increases, active Advisor savings opportunities, and reservation utilization. Include charts.",
      },
      {
        label: "Cost forecast",
        prompt:
          "Based on my current spending trend, forecast my Azure bill for the rest of this month. Show the projected cost vs budget as a line chart.",
      },
      {
        label: "Forecast variance",
        prompt:
          "Show budget vs forecast vs actual variance for this month per subscription. Flag any subscription where the forecast exceeds its budget, with the % overrun.",
      },
      {
        label: "Savings ledger",
        prompt:
          "Show my savings ledger — everything we've saved so far, estimated vs verified. Verify any executed actions against actual cost data and update their status.",
      },
      {
        label: "Weekly cost digest (email)",
        prompt:
          "Set up a weekly scheduled email digest of my Azure costs using Cost Management scheduled actions — cost by service plus notable changes. Ask me for the recipient email and cadence in one question.",
      },
      {
        label: "Amortized cost view",
        prompt:
          "Show my amortized Azure costs for the current month — spreading reservation and savings plan purchases across their term. Compare amortized vs actual cost by service in a table.",
      },
      // Chargeback / showback
      {
        label: "Chargeback report",
        prompt:
          "Generate a chargeback report for the current month. Break down costs by the owner tag or cost-center tag. Show a table with each team's total spend, top services, and month-over-month change.",
      },
      {
        label: "Showback report",
        prompt:
          "Generate a showback report for the current month — show each department/team their Azure costs by tag (cost-center or owner) without billing attribution. Include a summary table with each team's cost and share of total.",
      },
      {
        label: "Cost allocation model",
        prompt:
          "Analyze my cost allocation strategy. Show costs broken down by subscription, resource group, and tags (cost-center, environment, owner). What percentage of my spend can be attributed vs unattributed? Recommend improvements to my allocation model.",
      },
      // Unit economics & benchmarking
      {
        label: "Unit economics",
        prompt:
          "Help me calculate unit economics for my top workloads. For each of my top 5 resource groups by cost, calculate the cost-per-day and trend over the past 30 days. If I share transaction or user counts, we can derive cost-per-unit KPIs.",
      },
      {
        label: "Cross-sub benchmarking",
        prompt:
          "Benchmark cost efficiency across all my subscriptions. For each subscription, calculate: total cost, resource count, cost-per-resource, cost-per-vCPU, and month-over-month change. Rank by cost efficiency and identify outliers.",
      },
      {
        label: "FOCUS cost mapping",
        prompt:
          "Show my current month's costs mapped to FOCUS (FinOps Open Cost & Usage Specification) concepts: BilledCost (actual invoice amount), EffectiveCost (amortized with commitments spread), and ChargeCategory (usage, purchase, tax, credit). Show a comparison table.",
      },
      {
        label: "Cost anomaly detection",
        prompt:
          "Check my Azure Cost Management anomaly alerts. Show all active cost alerts and anomalies detected in the past 30 days. For each, show the affected scope, expected vs actual cost, deviation percentage, and the root cause if identified.",
      },
      // License optimization (Graph)
      {
        label: "License optimization",
        prompt:
          "Analyze my Microsoft 365 and Azure license usage. List all license types with total purchased, assigned, and unused counts. Show unused licenses with their monthly cost — what's my total waste from unassigned licenses?",
      },
      {
        label: "M365 Copilot ROI",
        prompt:
          "Show my Microsoft 365 Copilot license usage. How many seats are assigned vs actively used? Which users haven't used Copilot in the last 30 days? Calculate the monthly waste from inactive Copilot licenses.",
      },
      {
        label: "Copilot credits per user",
        prompt:
          "Who used the most Copilot Credits this month? Show the top 10 users by name with the credits each used this month, which Copilot services the credits went to, and how much of our tenant credit balance is left. Then show which Foundry model deployments and agents cost the most this month.",
      },
      // The admin center's Consumption views, built from Microsoft Graph with the same names and columns.
      {
        label: "Copilot policies",
        prompt:
          "Show the Policies view of Copilot cost management's Consumption for Month to date, as the Microsoft 365 admin center shows it: one row per spending policy with the columns Spending policy, Policy status, Applies to, Active users, Credits used, Policy monthly limit, Credit usage rate and Billing method. Take the credits as the admin center counts them: for a policy that governs all use, the month's total by funding class; otherwise the daily per-user reports for the completed days of this month and the raw usage of me and those users today. Write Unavailable only where no read gives a value. Then say which users and days the figures cover and how current each figure is.",
      },
      {
        label: "Copilot users & groups",
        prompt:
          "Show the Users & groups view of Copilot cost management's Consumption for Month to date, as the Microsoft 365 admin center shows it: one row per user who used Copilot Credits this month (from the daily per-user reports for the completed days of this month, plus me today) with the columns User, Monthly limit, Total credits used, Microsoft Copilot License and Last activity date, then the groups with credits from the daily per-group reports. Write Unavailable only where no read gives a value, and say how current each figure is.",
      },
      // Proposes a change: nothing is sent unless the user approves the exact request.
      {
        label: "Cap Copilot spend per user",
        prompt:
          "Should we cap Copilot Credits per user? Show our Copilot spending policies with each one's limit for all its users and per user, and the 5 users who used the most Copilot Credits in the daily per-user reports for the last 7 completed days plus me, by name with their credits this month from their user balance. Then propose a monthly per-user limit on the policy that covers all users, with headroom above the highest user's credits this month, as one change I can approve. Show one table: user, credits this month.",
      },
      {
        label: "Windows Server licensing",
        prompt:
          "Audit my Windows Server VMs. How many are using Azure Hybrid Benefit vs pay-as-you-go licensing? Show the per-VM cost difference and total savings opportunity from enabling AHUB on all eligible VMs.",
      },
      // AI & data platform
      {
        label: "GPU compute inventory",
        prompt:
          "List all my GPU VMs (NC, ND, NV series) across all subscriptions. Show each VM's name, size, GPU count, resource group, monthly cost, and tags. Which GPU VMs have low utilization and could be deallocated or right-sized?",
      },
      {
        label: "Azure ML cost analysis",
        prompt:
          "List all my Azure ML workspaces and their compute resources (instances, clusters, endpoints). Show each compute's type, VM size, state (running/stopped), monthly cost, and idle time. Identify ML computes burning money while idle.",
      },
      {
        label: "Cosmos DB RU optimization",
        prompt:
          "List all my Cosmos DB accounts with their provisioned throughput (RU/s), autoscale settings, consistency level, and monthly cost. Identify databases that are over-provisioned (low RU utilization) or could benefit from switching to autoscale or serverless.",
      },
      {
        label: "Synapse & Data Factory costs",
        prompt:
          "List all my Synapse workspaces (dedicated SQL pools, Spark pools) and Data Factory instances. Show each resource's configuration and monthly cost. Identify idle dedicated SQL pools that should be paused and over-provisioned Spark pools.",
      },
      // Governance
      {
        label: "Management groups",
        prompt:
          "Show my management group hierarchy with subscriptions under each.",
      },
      {
        label: "Security posture",
        prompt:
          "Show my Microsoft Defender for Cloud secure score across all subscriptions. List the top 10 unhealthy security recommendations with their severity and affected resource count.",
      },
      {
        label: "Carbon emissions",
        prompt:
          "Show my Azure carbon emissions data. Break down emissions by service and region. Which workloads have the highest carbon footprint, and what would be the impact of moving them to a greener region?",
      },
      {
        label: "FinOps maturity assessment",
        prompt:
          "Conduct a structured FinOps maturity assessment. Check each dimension: (1) Do I have budgets set per subscription? (2) What % of resources are tagged? (3) Am I using reservations/savings plans? (4) Advisor recommendation adoption rate? (5) Do I have cost exports configured? (6) Are my management groups structured for cost governance? Score each as Crawl/Walk/Run and recommend next steps.",
      },
    ],
  },
  // ── PLAYBOOK — Tailored FinOps priorities ──
  {
    key: "playbook",
    label: "Playbook",
    subtitle: "Tailored Analysis",
    icon: "P",
    colorClass: "cat-playbook",
    requiresAzure: true,
    prompts: [
      {
        label: "Score Playbook maturity",
        prompt:
          "Score my Playbook-level FinOps maturity (0-5 per dimension). Check these using Azure APIs: (1) Budget Coverage — what % of subscriptions have budgets configured with alert thresholds? (2) Budget Alerting — do existing budgets have alerts for actual (80%, 100%) and forecasted (100%, 120%) spend? (3) Anomaly Detection — are cost anomaly alerts enabled? (4) Egress Optimization — what is my bandwidth/egress cost as a % of total spend? (5) Reservation Health — what is the average utilization of my active reservations? (6) Tag-based Grouping — can costs be grouped by application/team via tags? For each, give a score 0-5 and a one-line reason, then call ReportMaturityScore with level 'playbook' and the scores array.",
      },
      {
        label: "Score trend over time",
        prompt:
          "Show my FinOps maturity score trend over time. Retrieve my score history using the GetScoreHistory tool and compare previous assessments to the latest. Show a line chart of overall scores per level over time and highlight whether I'm trending positively or negatively. If no history exists, tell me to run a scoring first.",
      },
      // ── Cost Overview by Analysis Bucket ──
      {
        label: "Spend by analysis bucket",
        prompt:
          "Analyze my Azure spend across all subscriptions and group it into these analysis buckets: Compute (VMs, VMSS, dedicated hosts), Storage (blob, files, disks, managed disks, NetApp), Databases (SQL, Cosmos DB, MySQL, PostgreSQL, MariaDB, Redis Cache), DataServices (Data Factory, Synapse, Databricks, HDInsight, Data Lake, Stream Analytics, Purview), Networking (VNets, peering, ExpressRoute, VPN, Load Balancer, App Gateway, Firewall, Front Door, CDN, bandwidth/egress, NAT Gateway, Traffic Manager, Private Link), Serverless (Functions, Logic Apps, Event Grid, Service Bus, Event Hubs, API Management), Monitoring (Log Analytics, App Insights, Monitor, Sentinel), Backup_DR (Recovery Services, Site Recovery, Backup), Integration (API Management, Service Bus, Logic Apps, Event Grid), Security (Defender, Key Vault, DDoS Protection, WAF), Containers (AKS, Container Instances, Container Registry, Container Apps), Identity (Entra ID, MFA), AI_ML (Cognitive Services, OpenAI, ML workspaces). Show a table with bucket name, total cost, and % of total spend sorted by highest spend first.",
      },
      {
        label: "Top 20 subscriptions by spend",
        prompt:
          "Show the top 20 Azure subscriptions by total spend for the current month. Show a table with rank, subscription name, cost, and % of total spend.",
      },
      {
        label: "Top subs per service bucket",
        prompt:
          "For each of my top 5 analysis buckets by spend (Compute, Storage, Databases, DataServices, Networking), show the top 10 subscriptions contributing the most cost within that bucket. Use Cost Management queries grouped by SubscriptionName and filtered by the relevant ServiceName/MeterCategory for each bucket. Show a summary table per bucket with subscription name, cost within that bucket, and % of bucket spend.",
      },
      {
        label: "Drill down subscription costs",
        prompt:
          "For my most expensive subscription, drill down into the cost details. Break down by analysis bucket (Compute, Storage, Databases, DataServices, Networking, Serverless, Monitoring, Backup_DR, Integration, Security, Containers). Show which resource types and specific resources are increasing or decreasing month over month. Show a waterfall chart of cost changes by service.",
      },
      // ── Trends ──
      {
        label: "Month-over-month trends",
        prompt:
          "Show historic cost trends for the last 6 months grouped by analysis bucket (Compute, Storage, Databases, DataServices, Networking, Serverless, Monitoring, Backup_DR). Show a line chart with monthly totals per bucket and highlight significant month-over-month increases or decreases. Then show the same trend for my top 10 subscriptions by spend.",
      },
      {
        label: "Subscription spend change",
        prompt:
          "Compare this month vs last month spend by subscription. Which subscriptions had the biggest cost increase and decrease? Show a table with subscription name, last month cost, this month cost, change amount, and change %.",
      },
      // ── Budgets ──
      {
        label: "Budget coverage gaps",
        prompt:
          "Which of my Azure subscriptions are NOT covered by a budget? List all subscriptions and indicate whether each has a budget configured. Show the coverage percentage and flag uncovered subscriptions. This is a large estate — query across all subscriptions efficiently.",
      },
      {
        label: "Budget exceeded or at risk",
        prompt:
          "For all my Azure budgets, show which have been exceeded and which are forecasted to be exceeded this month. Show a table with budget name, scope, budget amount, actual spend, forecasted spend, and status (OK / At Risk / Exceeded).",
      },
      {
        label: "Suggest budget amounts",
        prompt:
          "For subscriptions that don't have budgets, analyze the last 3 months of spend and suggest an appropriate monthly budget amount for each. Also recommend alert thresholds following Microsoft best practices: 50%, 80%, 100% for actual spend and 100%, 120% for forecasted spend.",
      },
      {
        label: "Budget alert audit",
        prompt:
          "Audit my existing Azure budget alert configurations. For each budget, show the configured notification thresholds for actual and forecasted spend. Compare against Microsoft best practices (actual: 50%, 80%, 100%; forecasted: 100%, 120%). Flag budgets with missing or misconfigured alerts.",
      },
      {
        label: "Cost anomaly detection check",
        prompt:
          "Check if cost anomaly detection and alerting is enabled for my Azure subscriptions. List any active cost anomaly alerts and scheduled actions. Are these following Microsoft best practices? What's missing?",
      },
      // ── Networking ──
      {
        label: "Egress cost analysis",
        prompt:
          "Analyze my Azure egress and bandwidth charges across all subscriptions. Networking is ~9% of total spend. Break down by subscription, service, and region. Which services generate the most outbound data transfer costs? Show a bar chart of egress costs by service and recommend ways to reduce excessive charges.",
      },
      {
        label: "VNet peering cost analysis",
        prompt:
          "Analyze my virtual network peering costs and topology. List all VNet peerings with their data transfer volumes and costs. Which peering connections are the most expensive? Are there opportunities to reduce costs by consolidating VNets or using service endpoints instead?",
      },
      // ── Reservations ──
      {
        label: "Reservation breakeven analysis",
        prompt:
          "For each of my active reservations, calculate the breakeven utilization point. Show the purchase price, on-demand equivalent cost over the term, breakeven utilization %, and actual average utilization %. Am I saving money or losing money on each reservation? Show a table sorted by savings/loss.",
      },
      // ── Regions ──
      {
        label: "Region cost comparison",
        prompt:
          "If I moved my workloads to a different Azure region, what would be the cost difference? List my current resources by region, look up retail pricing for my top resources in 3 alternative regions, and show a comparison table with current vs alternative monthly costs. Highlight the cheapest option.",
      },
      // ── Subscription Mapping ──
      {
        label: "Group costs by application tag",
        prompt:
          "Group my Azure costs by the 'Application' or 'app' tag across all subscriptions. Which applications span multiple subscriptions? Show a table with application name, subscriptions involved, and total cost, sorted by cost.",
      },
      {
        label: "Scope costs by tag",
        prompt:
          "I want to see costs for a specific scope defined by tags. Show me how costs break down when I filter by environment (Production vs Dev vs Test) or by team/department tags. Show a pie chart of cost distribution by the selected tag.",
      },
      // ── Cost Exports ──
      {
        label: "Analyze cost export data",
        prompt:
          "I have cost export data in an Azure Storage account (scheduled export, FOCUS format). Help me analyze it. First, ask me for the storage account name and container. Then list the available export blobs, read the latest one, and analyze it: show spend by service bucket, top subscriptions, and month-over-month trends.",
      },
      // ── Scheduling ──
      {
        label: "Schedule a report",
        prompt:
          "I want to schedule a recurring FinOps report. Help me set it up — ask me what analysis I want (cost overview, FinOps score, recommendations, etc.), the frequency (daily, weekly, monthly), the scope (subscription, resource group, or all), and the output format (chat summary or HTML presentation). Then save the schedule.",
      },
      {
        label: "View saved reports",
        prompt:
          "Show all my saved report schedules. For each, show the name, frequency, scope, last run time, and next scheduled run. Are any overdue?",
      },
    ],
  },
  // ── ENTERPRISE — EA / MCA negotiated rates + commitment-aware analysis ──
  {
    key: "enterprise",
    label: "Enterprise",
    subtitle: "EA / MCA & Commitments",
    icon: "E",
    colorClass: "cat-enterprise",
    requiresAzure: true,
    prompts: [
      {
        label: "List my billing accounts",
        prompt:
          "List my billing accounts and (for MCA) billing profiles. Show the agreement type (EA / MCA / MOSP), account name, and ID. I'll use these to download the negotiated pricesheet.",
      },
      {
        label: "Download EA pricesheet",
        prompt:
          "Start a negotiated pricesheet download for my EA billing account (POST {billingScope}/providers/Microsoft.CostManagement/pricesheets/default/download), then poll the returned operation until ready (respect nextPollUtc). When done, tell me how to download the file and how it differs from public retail rates for my top 5 SKUs.",
      },
      {
        label: "Download MCA pricesheet",
        prompt:
          "List my MCA billing profiles, ask which one, then start a negotiated pricesheet download (POST {billingProfile}/providers/Microsoft.CostManagement/pricesheets/default/download) and poll the returned operation until ready. Tell me how to download the file.",
      },
      {
        label: "Re-price top VMs at negotiated rates",
        prompt:
          "List my top 10 most expensive VMs by monthly cost. Then download my negotiated pricesheet (Cost Management pricesheets/default/download, then poll the returned operation). Compare each VM's billed rate to its negotiated rate AND to retail. Show a table — flag any case where the negotiated rate inverts the public-retail recommendation (e.g. retail says move regions, but negotiated says stay).",
      },
      {
        label: "Right-size VMs (commitment-aware)",
        prompt:
          "Get Azure Advisor right-sizing recommendations for my VMs. In parallel pull active reservations, savings plans, and their utilization. For each recommendation, add a Commitment column: ✅ Safe / 🟡 Conditional / 🔴 Strands RI / 🟠 Exchange — and show NET monthly savings (gross savings minus stranded commitment cost). Sort by net savings descending.",
      },
      {
        label: "RI exchange opportunities (negotiated)",
        prompt:
          "Find reservations that are underutilized (<60%) or that no longer match my workload mix. For each, recommend a specific exchange target SKU+region using my negotiated pricesheet rates (not retail). Show a table with current RI, utilization %, recommended exchange target, and projected monthly savings at negotiated rates.",
      },
      {
        label: "Investigate spike with change correlation",
        prompt:
          "Find cost anomalies (daily spikes) for my most expensive subscription over the last 35 days. For every spike date, query Resource Graph resourcechanges for that day to identify which resources changed (Create / Update / Delete) and which property flipped (e.g. sku.name, capacity, tier). Name the most likely culprit per spike.",
      },
      {
        label: "Policy-blocked SKU audit",
        prompt:
          "Query Azure Policy via Resource Graph (policyresources, listOfAllowedSKUs / allowedLocations parameters) to list every SKU and region restriction in effect across my subscriptions. Then check my current resources — which would be DENIED if redeployed today? Show a table grouped by policy with affected resource count and monthly cost.",
      },
      {
        label: "Negotiated vs retail savings overview",
        prompt:
          "Download my negotiated pricesheet. Pick my top 20 SKUs by spend this month and compute (a) % discount vs retail per SKU, (b) total monthly $ saved vs retail at current usage, (c) any SKUs where negotiated > retail (rare — flag as rate-card anomaly). Show a horizontal bar chart of $ saved per SKU.",
      },
    ],
  },
  // ── Public pricing — also rendered as its own dedicated sidebar card,
  // so it is intentionally NOT included in maturityCategories anymore.
];

export { maturityCategories };
