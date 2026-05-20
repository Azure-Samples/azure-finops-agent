// FinOps maturity sidebar categories — extracted from ChatView.vue for maintainability.
// Pure static data; no runtime dependencies.

// Public retail-pricing prompts — work without an Azure login.
const publicPricingPrompts = [
  {
    label: "Compare VM pricing by region",
    prompt:
      "Compare the monthly cost of a D4s_v5 VM across the 10 cheapest Azure regions. Show a bar chart.",
  },
  {
    label: "Spot vs on-demand savings",
    prompt:
      "Compare spot vs on-demand pricing for D4s_v5, D8s_v5, and NC24ads_A100_v4 in East US. Show the discount % for each.",
  },
  {
    label: "Reserved vs pay-as-you-go",
    prompt:
      "Compare pay-as-you-go vs 1-year vs 3-year reserved pricing for a D4s_v5 VM in East US.",
  },
  {
    label: "Storage tier comparison",
    prompt:
      "Compare Azure Blob Storage costs for 10 TB across Hot, Cool, Cold, and Archive tiers in East US.",
  },
  {
    label: "Database pricing comparison",
    prompt:
      "Compare monthly cost of Azure SQL 8-vCore vs Cosmos DB 10K RU/s vs PostgreSQL Flexible 8-vCore with 500 GB storage.",
  },
  {
    label: "3-tier app cost estimate",
    prompt:
      "Estimate monthly cost for a 3-tier app in East US: 2x D4s_v5 VMs, Azure SQL 4-vCore 500 GB, 1 TB Premium SSD, Standard LB.",
  },
  {
    label: "AKS vs Container Apps vs Functions",
    prompt:
      "Compare cost of running 20 microservices on AKS vs Azure Container Apps vs Azure Functions consumption plan.",
  },
  {
    label: "GPU training cluster cost",
    prompt:
      "Compare monthly cost of 4x A100 (ND96asr_v4) vs 4x H100 (NC80adis_H100_v5) on-demand in East US.",
  },
  {
    label: "Global VM pricing map",
    prompt:
      "Show a world map of Azure regions color-coded by D4s_v5 VM pricing.",
  },
  {
    label: "Azure service health",
    prompt: "Are there any active Azure service health incidents right now?",
  },
  {
    label: "Kubernetes node pool sizing",
    prompt:
      "Compare monthly cost of an AKS cluster with 3x D4s_v5 vs 3x D8s_v5 vs 3x D16s_v5 nodes in East US.",
  },
  {
    label: "Estimate new deployment",
    prompt:
      "I want to estimate the monthly cost of a new deployment. Help me price out the infrastructure — I'll describe the resources I need (VMs, storage, databases, networking) and you calculate the estimated monthly cost using Azure retail pricing.",
  },
  {
    label: "Azure OpenAI token pricing",
    prompt:
      "Compare Azure OpenAI pricing for GPT-4o vs GPT-4o-mini vs GPT-4.1 per 1M input and output tokens.",
  },
  {
    label: "Azure Firewall cost tiers",
    prompt:
      "Compare Azure Firewall Basic vs Standard vs Premium monthly cost including 5 TB data processed.",
  },
];

// Personalised pricing prompts — only useful once the user has connected Azure,
// because they cross-reference the user's actual resources with retail pricing.
const connectedPricingPrompts = [
  {
    label: "Re-price my top 5 VMs in cheaper regions",
    prompt:
      "List my top 5 most expensive VMs by monthly cost. For each, look up the retail price in 3 cheaper Azure regions (e.g. North Europe, Sweden Central, West US 3) and show a comparison table with the potential monthly savings if I migrated.",
  },
  {
    label: "Reservation savings on my current VMs",
    prompt:
      "For each VM SKU I'm currently running on pay-as-you-go, compute the savings from moving to a 1-year and 3-year reservation using Azure retail pricing. Sort by total annual savings.",
  },
  {
    label: "Spot eligibility for dev/test",
    prompt:
      "Find my VMs that look like dev/test (tagged Environment=Dev or in resource groups containing 'dev', 'test', 'sandbox') and compute the savings if I converted them to spot pricing using Azure retail rates.",
  },
  {
    label: "Storage tier downgrade savings",
    prompt:
      "List my storage accounts and their used capacity. For Hot-tier blobs that haven't been accessed in 30+ days, compute monthly savings from moving them to Cool or Cold using Azure retail pricing.",
  },
  {
    label: "Cost projection if I add this workload",
    prompt:
      "I want to add a new workload to my subscription. Describe the resources you need (VMs, storage, databases) and I'll project the new monthly bill using Azure retail prices, broken down by service and added on top of my current spend.",
  },
];

export const pricingCategory = {
  key: "pricing",
  label: "Pricing & Estimates",
  icon: "$",
  colorClass: "cat-pricing",
  requiresAzure: false,
  publicPrompts: publicPricingPrompts,
  connectedPrompts: connectedPricingPrompts,
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
          "Compare Azure costs across all my subscriptions for the current month. Show a bar chart ranking subscriptions by spend, and a table with subscription name, cost, and month-over-month change.",
      },
      {
        label: "Cost by resource group",
        prompt:
          "Break down my current month's Azure cost by resource group and show a bar chart.",
      },
      {
        label: "Cost by tag",
        prompt:
          "Break down my Azure costs by the cost-center tag for the current month. Show a pie chart and table. Which cost centers have the highest spend?",
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
          "Query Resource Graph for a count of all resources by type across my subscriptions. Show a pie chart of the top 15 resource types and a table with the full breakdown.",
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
        label: "Amortized cost view",
        prompt:
          "Show my amortized Azure costs for the current month — spreading reservation and savings plan purchases across their term. Compare amortized vs actual cost by service in a table.",
      },
      // Chargeback / showback
      {
        label: "Chargeback report",
        prompt:
          "Generate a chargeback report for the current month. Break down costs by the owner tag or cost-center tag. Show a bar chart and table with each team's total spend, top services, and month-over-month change.",
      },
      {
        label: "Showback report",
        prompt:
          "Generate a showback report for the current month — show each department/team their Azure costs by tag (cost-center or owner) without billing attribution. Include a summary table and a pie chart of cost distribution across teams.",
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
          "Analyze my Azure spend across all subscriptions and group it into these analysis buckets: Compute (VMs, VMSS, dedicated hosts), Storage (blob, files, disks, managed disks, NetApp), Databases (SQL, Cosmos DB, MySQL, PostgreSQL, MariaDB, Redis Cache), DataServices (Data Factory, Synapse, Databricks, HDInsight, Data Lake, Stream Analytics, Purview), Networking (VNets, peering, ExpressRoute, VPN, Load Balancer, App Gateway, Firewall, Front Door, CDN, bandwidth/egress, NAT Gateway, Traffic Manager, Private Link), Serverless (Functions, Logic Apps, Event Grid, Service Bus, Event Hubs, API Management), Monitoring (Log Analytics, App Insights, Monitor, Sentinel), Backup_DR (Recovery Services, Site Recovery, Backup), Integration (API Management, Service Bus, Logic Apps, Event Grid), Security (Defender, Key Vault, DDoS Protection, WAF), Containers (AKS, Container Instances, Container Registry, Container Apps), Identity (Entra ID, MFA), AI_ML (Cognitive Services, OpenAI, ML workspaces). Show a pie chart of spend by bucket with % of total. Then show a table with bucket name, total cost, and % of total spend sorted by highest spend first.",
      },
      {
        label: "Top 20 subscriptions by spend",
        prompt:
          "Show the top 20 Azure subscriptions by total spend for the current month. Show a bar chart and a table with rank, subscription name, cost, and % of total spend.",
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
          "Compare this month vs last month spend by subscription. Which subscriptions had the biggest cost increase and decrease? Show a waterfall chart of changes and a table with subscription name, last month cost, this month cost, change amount, and change %.",
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
          "For all my Azure budgets, show which have been exceeded and which are forecasted to be exceeded this month. Show a table with budget name, scope, budget amount, actual spend, forecasted spend, and status (OK / At Risk / Exceeded). Show a gauge chart for each at-risk budget.",
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
          "Group my Azure costs by the 'Application' or 'app' tag across all subscriptions. Show total cost per application as a bar chart. Which applications span multiple subscriptions? Show a table with application name, subscriptions involved, and total cost.",
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
  // ── Public pricing — also rendered as its own dedicated sidebar card,
  // so it is intentionally NOT included in maturityCategories anymore.
];

export { maturityCategories };

