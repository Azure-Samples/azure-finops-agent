using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;

using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Infrastructure;

namespace AzureFinOps.Dashboard.AI.Tools;

/// <summary>
/// Single tool for querying any Azure ARM API using the user's delegated access token.
/// The LLM constructs the URL and optional body; this tool executes the HTTP request.
/// All calls are traced via OpenTelemetry → Application Insights for analysis.
///
/// Security model: GET, POST, PUT, and PATCH are allowed. DELETE is blocked at the
/// code level. Beyond that, the user's Entra RBAC role is the security boundary —
/// assign Reader / Cost Management Reader for read-only access.
/// </summary>
public class AzureQueryTools
{
    private readonly UserTokens _tokens;

    public AzureQueryTools(UserTokens tokens) => _tokens = tokens;

    public IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(QueryAzure, "QueryAzure", @"Queries Azure ARM REST APIs using the signed-in user's delegated token and returns raw JSON.
Base: https://management.azure.com — provide path starting with /.
Allowed methods: GET, POST, PUT, PATCH. DELETE is blocked at the code level — the agent never deletes resources. Beyond that, the user's Entra RBAC role governs what they can actually do (assign Reader / Cost Management Reader for read-only).
Use POST freely for cost/query/report endpoints (/query, /forecast, /resources, /generateCostDetailsReport, /calculatePrice, /carbonEmissionReports, /getEntities, /pricesheets/download, etc.) — these are the canonical Cost Management and Resource Graph surfaces.
DATA SCOPING: For Cost Management POST .../query, ALWAYS use grouping (ServiceName, ResourceGroupName, MeterCategory — NOT 'ResourceGroup', NOT 'UsageDate') and date granularity (Daily/Monthly). For per-day breakdowns set granularity=""Daily"" — DO NOT add UsageDate to the grouping array (UsageDate is the response column name, not a valid grouping dimension; 400 BadRequest if used). Never request raw ungrouped cost data. For Resource Graph POST .../resources, use KQL 'project' and 'top 20' — never select all columns. For list APIs (VMs, storage, etc.), results are already scoped by subscription.

=== API VERSIONS REFERENCE (use these exact api-version values) ===
Microsoft.CostManagement: 2025-03-01 (query, forecast, exports, views, alerts, scheduledActions, dimensions, benefitUtilizationSummaries, benefitRecommendations, costAllocationRules, generateCostDetailsReport, generateDetailedCostReport, generateReservationDetailsReport, generateBenefitUtilizationSummariesReport, priceSheet, settings)
Microsoft.Consumption: 2025-07-01 (budgets, pricesheets, reservationSummaries, reservationRecommendations, lots, credits, balances, charges, marketplaces)
Microsoft.Billing: 2024-04-01 (billingAccounts, invoices, billingSubscriptions, billingProfiles, invoiceSections, departments, enrollmentAccounts)
Microsoft.Capacity: 2022-11-01 (reservationOrders, reservations, catalog, calculatePrice, calculateExchange)
Microsoft.BillingBenefits: 2022-11-01 (savingsPlanOrders, savingsPlans, calculatePrice, validatePurchase)
Microsoft.Advisor: 2025-01-01 (recommendations)
Microsoft.ResourceGraph: 2024-04-01 (resources POST query)
Microsoft.Insights (metrics): 2024-02-01 (metrics, metricDefinitions, metricBaselines)
Microsoft.Insights (diagnostics): 2021-05-01-preview (diagnosticSettings)
Microsoft.Insights (autoscale): 2022-10-01 (autoscaleSettings)
Microsoft.Insights (activity log): 2015-04-01 (eventtypes/management/values)
Microsoft.Compute (VMs/VMSS): 2025-11-01 (virtualMachines, virtualMachineScaleSets)
Microsoft.Compute (Disks): 2025-01-02 (disks, snapshots)
Microsoft.Compute (SKUs): 2021-07-01 (skus — VM sizes per region, use $filter=location eq '{region}')
Microsoft.Compute (Usage): 2025-11-01 (locations/{region}/usages — quota usage per family)
Microsoft.ContainerService: 2026-02-01 (managedClusters — AKS)
Microsoft.Network (core): 2025-07-01 (virtualNetworks, publicIPAddresses, loadBalancers, natGateways, privateEndpoints)
Microsoft.Network (gateways/firewall): 2025-09-01 (applicationGateways, expressRouteCircuits, vpnGateways, azureFirewalls)
Microsoft.Storage: 2025-08-01 (storageAccounts)
Microsoft.Sql: 2025-01-01 (servers, databases, managedInstances — now GA)
Microsoft.Web: 2025-05-01 (serverfarms, sites)
Microsoft.OperationalInsights: 2025-07-01 (workspaces, tables, usages)
Microsoft.MachineLearningServices: 2026-03-01 (workspaces, computes, onlineEndpoints, batchEndpoints)
Microsoft.CognitiveServices: 2026-03-01 (accounts, deployments, models, locations/{region}/usages, locations/{region}/models)
Microsoft.Databricks: 2026-01-01 (workspaces)
Microsoft.DocumentDB: 2025-10-15 (databaseAccounts, sqlDatabases — Cosmos DB)
Microsoft.Cache: 2024-11-01 (redis)
Microsoft.DataFactory: 2018-06-01 (factories, pipelines)
Microsoft.Synapse: 2023-05-01 (workspaces, sqlPools, bigDataPools)
Microsoft.App: 2026-01-01 (containerApps, managedEnvironments)
Microsoft.ResourceHealth: 2025-05-01 (availabilityStatuses)
Microsoft.Security: 2025-05-04 (assessments, assessmentMetadata); 2020-01-01 (secureScores)
Microsoft.Authorization (RBAC): 2022-04-01 (roleAssignments, roleDefinitions)
Microsoft.Authorization (Policy): 2025-11-01 (policyDefinitions, policyAssignments)
Microsoft.Authorization (Locks): 2020-05-01 (locks)
Microsoft.PolicyInsights: 2024-10-01 (policyStates)
Microsoft.Management: 2023-04-01 (managementGroups, descendants, getEntities)
Microsoft.Resources: 2022-12-01 (subscriptions); 2023-07-01 (tagNames, resourceGroups, resources)
Microsoft.Quota: 2025-09-01 (quotas, usages, quotaRequests — scope: /subscriptions/{subId}/providers/Microsoft.Compute/locations/{region}/providers/Microsoft.Quota/quotas)
Microsoft.Carbon: 2025-04-01 (carbonEmissionReports)
Microsoft.Migrate: 2024-01-15 (assessmentProjects, assessments, assessedMachines — NOTE: resource type is `assessmentProjects`, NOT `migrateProjects`)
Microsoft.Support: 2024-04-01 (supportTickets, services)

=== {scope} GRAMMAR (REQUIRED for ALL Microsoft.CostManagement, Microsoft.Consumption, and Microsoft.CostManagement/budgets paths) ===
{scope} MUST be exactly ONE of these prefixes — never empty, never bare /providers/...:
  /subscriptions/{subId}
  /subscriptions/{subId}/resourceGroups/{rgName}
  /providers/Microsoft.Management/managementGroups/{mgId}
  /providers/Microsoft.Billing/billingAccounts/{billingAccountId}
  /providers/Microsoft.Billing/billingAccounts/{billingAccountId}/billingProfiles/{profileId}
  /providers/Microsoft.Billing/billingAccounts/{billingAccountId}/invoiceSections/{invoiceSectionId}
✓ POST /subscriptions/abc-123/providers/Microsoft.CostManagement/query?api-version=2025-03-01
✗ POST /providers/Microsoft.CostManagement/query  (missing scope → HTTP 400 BadRequest from agent preflight, was previously 404 InvalidResourceType from ARM)
THROTTLING: Cost Management /query and /forecast are aggressively throttled per-tenant — the agent silently retries 429s up to 5× with exponential backoff. Do NOT call CostManagement endpoints in parallel with each other from the same turn (parallel CM calls trigger 429 cascades). Resource Graph and Advisor are higher-throughput; safe to parallelize.

=== COST MANAGEMENT (Microsoft.CostManagement, api-version=2025-03-01) ===
POST /{scope}/providers/Microsoft.CostManagement/query — cost analysis with grouping/filtering. POST /{scope}/providers/Microsoft.CostManagement/forecast — forecast. POST /{scope}/providers/Microsoft.CostManagement/generateCostDetailsReport — line-item cost data (replaces Consumption usageDetails). POST /{scope}/providers/Microsoft.CostManagement/generateDetailedCostReport — detailed cost report (async; separate from generateCostDetailsReport). POST /{scope}/providers/Microsoft.CostManagement/generateReservationDetailsReport — reservation utilization line-item. POST /{scope}/providers/Microsoft.CostManagement/generateBenefitUtilizationSummariesReport — triggers async benefit utilization summaries report. GET /{scope}/providers/Microsoft.CostManagement/alerts — cost anomaly alerts. GET /{scope}/providers/Microsoft.CostManagement/dimensions — available dimensions for queries. GET /{scope}/providers/Microsoft.CostManagement/benefitUtilizationSummaries — reservation/savings plan utilization. GET /{scope}/providers/Microsoft.CostManagement/benefitRecommendations — purchase recommendations. GET /{scope}/providers/Microsoft.CostManagement/costAllocationRules — split shared costs across scopes. GET /{scope}/providers/Microsoft.CostManagement/exports — scheduled cost data exports to storage. GET /{scope}/providers/Microsoft.CostManagement/scheduledActions — scheduled cost alert emails/reports. GET /{scope}/providers/Microsoft.CostManagement/views — pre-saved cost analysis views. GET /{scope}/providers/Microsoft.CostManagement/settings — cost management settings per scope. POST .../pricesheets/download — download price sheets by billing account/profile/invoice.

=== BUDGETS (Microsoft.Consumption, api-version=2025-07-01) ===
GET /{scope}/providers/Microsoft.Consumption/budgets?api-version=2025-07-01 — list all budgets for a scope; returns budget name, amount, time grain (Monthly/Quarterly/Annually), current spend vs limit, and notification thresholds. GET /{scope}/providers/Microsoft.Consumption/budgets/{budgetName}?api-version=2025-07-01 — get specific budget details including spend-to-date and forecast.

=== BILLING (Microsoft.Billing, api-version=2024-04-01) ===
GET /providers/Microsoft.Billing/billingAccounts?api-version=2024-04-01 — list all billing accounts (EA enrollment, MCA, MPA). GET /providers/Microsoft.Billing/billingAccounts/{billingAccountId}/billingProfiles?api-version=2024-04-01 — billing profiles (MCA payment instruments). GET /providers/Microsoft.Billing/billingAccounts/{billingAccountId}/billingProfiles/{profileId}/invoiceSections?api-version=2024-04-01 — invoice sections for cost grouping. GET /providers/Microsoft.Billing/billingAccounts/{billingAccountId}/invoices?api-version=2024-04-01 — list invoices with amounts, due dates, PDF download links. GET /providers/Microsoft.Billing/billingAccounts/{billingAccountId}/transactions?api-version=2024-04-01 — individual billing transactions. GET /providers/Microsoft.Billing/billingAccounts/{billingAccountId}/billingSubscriptions?api-version=2024-04-01 — subscriptions under billing account with status and spend. GET /providers/Microsoft.Billing/billingAccounts/{billingAccountId}/departments?api-version=2024-04-01 — EA departments. GET /providers/Microsoft.Billing/billingAccounts/{billingAccountId}/enrollmentAccounts?api-version=2024-04-01 — EA enrollment accounts. GET /providers/Microsoft.Billing/billingAccounts/{billingAccountId}/customers?api-version=2024-04-01 — CSP partner customers.

=== CONSUMPTION (Microsoft.Consumption, api-version=2025-07-01) ===
GET /{scope}/providers/Microsoft.Consumption/pricesheets/default?api-version=2025-07-01 — negotiated price sheet for EA/MCA (meter rates, unit prices). GET /{scope}/providers/Microsoft.Consumption/reservationSummaries?grain=daily&api-version=2025-07-01 — reservation utilization summary (daily/monthly grain). GET /{scope}/providers/Microsoft.Consumption/reservationRecommendations?api-version=2025-07-01 — purchase recommendations based on usage patterns (single/shared scope, 1yr/3yr). GET /{scope}/providers/Microsoft.Consumption/reservationTransactions?api-version=2025-07-01 — reservation buy/exchange/refund transactions. GET /providers/Microsoft.Billing/billingAccounts/{id}/providers/Microsoft.Consumption/lots?api-version=2025-07-01 — Azure prepayment (monetary commitment) lot balances. GET /providers/Microsoft.Billing/billingAccounts/{id}/providers/Microsoft.Consumption/credits?api-version=2025-07-01 — Azure credit balance and expiry. GET /providers/Microsoft.Billing/billingAccounts/{id}/providers/Microsoft.Consumption/balances?api-version=2025-07-01 — EA monetary commitment balance summary. GET /{scope}/providers/Microsoft.Consumption/charges?api-version=2025-07-01 — charges by department/enrollment account. GET /{scope}/providers/Microsoft.Consumption/marketplaces?api-version=2025-07-01 — third-party Marketplace charges with publisher, plan, cost. NOTE: usageDetails is deprecated — prefer generateCostDetailsReport or Exports. reservationDetails is deprecated — prefer generateReservationDetailsReport.

=== RESERVATIONS (Microsoft.Capacity, api-version=2022-11-01) ===
GET /providers/Microsoft.Capacity/reservationOrders?api-version=2022-11-01 — list all reservation orders (purchase records with term, quantity, billing scope). GET /providers/Microsoft.Capacity/reservationOrders/{orderId}/reservations?api-version=2022-11-01 — individual reservations within an order (utilization %, expiry, applied scope). GET /subscriptions/{id}/providers/Microsoft.Capacity/catalogs?api-version=2022-11-01&reservedResourceType=VirtualMachines&location={region} — available reservation catalog for a resource type and region. POST /providers/Microsoft.Capacity/calculatePrice?api-version=2022-11-01 — simulate reservation purchase price without buying (body: {sku, term, quantity, location}). POST /providers/Microsoft.Capacity/calculateExchange?api-version=2022-11-01 — simulate exchange/return costs (body: {reservationsToExchange, reservationsToReturn}).

=== SAVINGS PLANS (Microsoft.BillingBenefits, api-version=2022-11-01) ===
GET /providers/Microsoft.BillingBenefits/savingsPlanOrders?api-version=2022-11-01 — list all savings plan orders (commitment amount, term, billing scope). GET /providers/Microsoft.BillingBenefits/savingsPlanOrders/{orderId}/savingsPlans?api-version=2022-11-01 — individual savings plans with utilization %, committed hourly amount, applied scope. POST /providers/Microsoft.BillingBenefits/savingsPlanOrders/calculatePrice?api-version=2022-11-01 — simulate savings plan purchase price. POST /providers/Microsoft.BillingBenefits/savingsPlanOrders/validatePurchase?api-version=2022-11-01 — validate purchase eligibility without committing.

=== ADVISOR (Microsoft.Advisor, api-version=2025-01-01) ===
GET /subscriptions/{id}/providers/Microsoft.Advisor/recommendations?api-version=2025-01-01&$filter=Category eq 'Cost' — cost optimization recommendations (right-size VMs, shut down idle, buy reservations, delete orphaned resources). Also supports $filter=Category eq 'HighAvailability' | 'Security' | 'Performance' | 'OperationalExcellence'. Each recommendation returns impactedField (resource type), impactedValue (resource name), shortDescription, remediation actions, and estimatedSavings. Use $top=50 to limit results.

=== RESOURCE GRAPH (Microsoft.ResourceGraph, api-version=2024-04-01) ===
POST /providers/Microsoft.ResourceGraph/resources?api-version=2024-04-01 — run KQL queries across all subscriptions in one call. Body: {""query"": ""Resources | where type =~ 'Microsoft.Compute/virtualMachines' | project name, resourceGroup, location, properties.hardwareProfile.vmSize | top 20"", ""subscriptions"": [""subId1""]}. Supports all resource types, tags, properties. Use for: cross-subscription resource inventory, finding untagged resources, orphaned NICs/IPs/disks, resource counts by type/region. ALWAYS use 'project' to limit columns and 'top N' to limit rows.

=== MONITOR (Microsoft.Insights, api-version=2024-02-01) ===
GET /{resourceId}/providers/Microsoft.Insights/metrics?api-version=2024-02-01&metricnames={name1,name2}&timespan={start}/{end}&interval={PT1H|P1D}&aggregation={Average,Maximum,Minimum,Total,Count} — query metric values for any resource. Use metricnamespace to scope (e.g. Microsoft.Compute/virtualMachines). Common FinOps metrics: VMs → 'Percentage CPU','Available Memory Bytes','Disk Read Bytes','Disk Write Bytes','Network In Total','Network Out Total'; SQL DB → 'dtu_consumption_percent','storage_percent'; Cosmos DB → 'TotalRequestUnits','NormalizedRUConsumption'; Storage → 'UsedCapacity','BlobCapacity','Transactions'; App Service → 'CpuPercentage','MemoryPercentage','Requests'; Redis → 'usedmemorypercentage','serverLoad'. Use $filter for dimensions (e.g. 'Tier eq ''Hot'''). Use resultType=Metadata to discover available dimension values. Use AutoAdjustTimegrain=true for flexibility.
GET /{resourceId}/providers/Microsoft.Insights/metricDefinitions?api-version=2024-02-01 — list all available metrics for a resource with supported aggregations, time grains, dimensions, and unit.
GET /{resourceId}/providers/Microsoft.Insights/metricBaselines?api-version=2024-02-01 — metric baselines for anomaly detection (expected low/medium/high bands).
GET /{resourceId}/providers/Microsoft.Insights/diagnosticSettings?api-version=2021-05-01-preview — check if diagnostics are enabled (logs/metrics flowing to Log Analytics/storage/Event Hub).
GET /subscriptions/{id}/providers/Microsoft.Insights/autoscaleSettings?api-version=2022-10-01 — autoscale rules (min/max/default instance counts, scale rules, schedules).

=== ACTIVITY LOG (Microsoft.Insights, api-version=2015-04-01) ===
GET /subscriptions/{id}/providers/Microsoft.Insights/eventtypes/management/values?$filter=eventTimestamp ge '...' — who created/deleted/modified resources (cost attribution audit trail).

=== COMPUTE (Microsoft.Compute) ===
GET /subscriptions/{id}/providers/Microsoft.Compute/virtualMachines?api-version=2025-11-01 — list all VMs in subscription. GET .../virtualMachineScaleSets?api-version=2025-11-01 — VMSS instances for right-sizing. GET /subscriptions/{id}/providers/Microsoft.Compute/skus?api-version=2021-07-01&$filter=location eq '{region}' — VM sizes per region (use filter to limit; includes 'family' field and capabilities like LowPriorityCapable). GET /subscriptions/{id}/providers/Microsoft.Compute/disks?api-version=2025-01-02 — managed disks (find unattached via diskState=Unattached). GET /subscriptions/{id}/providers/Microsoft.Compute/locations/{region}/usages?api-version=2025-11-01 — quota usage per VM family.

=== QUOTA (Microsoft.Quota, api-version=2025-09-01) ===
GET /subscriptions/{subId}/providers/Microsoft.Compute/locations/{region}/providers/Microsoft.Quota/quotas?api-version=2025-09-01 — list ALL compute quotas for a region (standard family quotas + lowPriorityCores). GET .../quotas/{resourceName}?api-version=2025-09-01 — get single quota by name. GET .../providers/Microsoft.Quota/quotaRequests?api-version=2025-09-01 — list quota change requests.
NOTE: Microsoft.Quota requires RP registration on the subscription — if the call returns 400 MissingRegistrationForResourceProvider, the user (or an Owner) must register it first via PUT /subscriptions/{subId}/providers/Microsoft.Quota/register?api-version=2022-09-01. Until then fall back to GET /subscriptions/{id}/providers/Microsoft.Compute/locations/{region}/usages?api-version=2025-11-01 which always works for compute quotas.
IMPORTANT — SPOT QUOTA NAMING: Spot/low-priority VM quota is a SINGLE regional bucket called 'lowPriorityCores' (NOT per VM family). This covers ALL spot VMs including H100, A100, etc. Standard quotas are per-family (e.g., 'standardNDSH100v5Family', 'StandardNCadsH100v5Family'). To check H100 spot capacity: query lowPriorityCores limit. To check H100 standard capacity: query standardNDSH100v5Family and StandardNCadsH100v5Family.
ALSO: Legacy Compute usage API (GET .../locations/{region}/usages?api-version=2025-11-01) returns BOTH standard and spot usage with family names like 'lowPriorityCores' and per-family entries. Use BOTH APIs for a complete picture.

=== AKS (Microsoft.ContainerService, api-version=2026-02-01) ===
GET /subscriptions/{id}/providers/Microsoft.ContainerService/managedClusters?api-version=2026-02-01 — list all AKS clusters; returns cluster name, location, kubernetesVersion, powerState (Running/Stopped), networkProfile, sku (Free/Standard/Premium). GET /subscriptions/{id}/resourceGroups/{rg}/providers/Microsoft.ContainerService/managedClusters/{name}?api-version=2026-02-01 — single cluster detail with agentPoolProfiles (node pools): vmSize, count, minCount/maxCount (autoscaler), osDiskSizeGB, mode (System/User), enableAutoScaling, spotMaxPrice (spot node pools). Use for: right-sizing node pools, identifying stopped clusters still incurring costs, evaluating spot vs regular node pools.

=== NETWORK (Microsoft.Network, core resources api-version=2025-07-01; gateways/firewall/expressRoute api-version=2025-09-01) ===
GET /subscriptions/{id}/providers/Microsoft.Network/publicIPAddresses?api-version=2025-07-01 — all public IPs; find unassociated IPs (ipConfiguration is null = orphaned, still billed). GET /subscriptions/{id}/providers/Microsoft.Network/loadBalancers?api-version=2025-07-01 — LBs with sku (Basic/Standard/Gateway) and pricing tier. GET /subscriptions/{id}/providers/Microsoft.Network/applicationGateways?api-version=2025-09-01 — App Gateways with sku (Standard_v2/WAF_v2), tier, capacity units for cost analysis. GET /subscriptions/{id}/providers/Microsoft.Network/expressRouteCircuits?api-version=2025-09-01 — ExpressRoute circuits with bandwidth (Mbps), peering, provider for billing analysis. GET /subscriptions/{id}/providers/Microsoft.Network/vpnGateways?api-version=2025-09-01 — VPN gateways with sku (Basic/VpnGw1-5) for right-sizing. GET /subscriptions/{id}/providers/Microsoft.Network/natGateways?api-version=2025-07-01 — NAT gateways (billed per hour + data processed). GET /subscriptions/{id}/providers/Microsoft.Network/azureFirewalls?api-version=2025-09-01 — Azure Firewalls with sku (Standard/Premium) and threat intel mode. GET /subscriptions/{id}/providers/Microsoft.Network/privateEndpoints?api-version=2025-07-01 — private endpoints (billed per hour). GET /subscriptions/{id}/providers/Microsoft.Network/virtualNetworks?api-version=2025-07-01 — VNets with subnets, peerings, address space.

=== STORAGE (Microsoft.Storage, api-version=2025-08-01) ===
GET /subscriptions/{id}/providers/Microsoft.Storage/storageAccounts?api-version=2025-08-01 — all storage accounts; returns kind (StorageV2/BlobStorage/FileStorage), sku.name (Standard_LRS/Standard_GRS/Premium_LRS), accessTier (Hot/Cool/Cold/Archive), primaryLocation, encryption, networkAcls. GET /subscriptions/{id}/resourceGroups/{rg}/providers/Microsoft.Storage/storageAccounts/{name}?api-version=2025-08-01 — single account detail including creation time, lastGeoFailover, minimumTlsVersion. For cost optimization: identify accounts on expensive replication (GRS→LRS), wrong access tier (Hot with infrequent access → Cool), or unused accounts (check metrics via Monitor API).

=== SQL (Microsoft.Sql, api-version=2025-01-01) ===
GET /subscriptions/{id}/providers/Microsoft.Sql/servers?api-version=2025-01-01 — all SQL logical servers with location, version, admin login. GET /subscriptions/{id}/resourceGroups/{rg}/providers/Microsoft.Sql/servers/{serverName}/databases?api-version=2025-01-01 — databases on a server; returns sku (name=GP_S_Gen5_2 / BC_Gen5_4 / S0-S12 / P1-P15), maxSizeBytes, currentServiceObjectiveName (DTU tier or vCore config), zoneRedundant, licenseType (BasePrice=AHUB, LicenseIncluded=full price), requestedBackupStorageRedundancy (Geo/Local/Zone). GET /subscriptions/{id}/providers/Microsoft.Sql/managedInstances?api-version=2025-01-01 — SQL Managed Instances with sku (GP_Gen5/BC_Gen5), vCores, storageSizeInGB, licenseType for right-sizing. For cost optimization: check AHB license savings, over-provisioned DTU/vCores, Basic/S0 tier for dev databases, zone redundancy cost.

=== APP SERVICE (Microsoft.Web, api-version=2025-05-01) ===
GET /subscriptions/{id}/providers/Microsoft.Web/serverfarms?api-version=2025-05-01 — all App Service plans; returns sku (name=F1/B1/B2/S1/P1v3/P0v3, tier=Free/Basic/Standard/Premium/PremiumV3), numberOfWorkers, currentNumberOfWorkers, maximumNumberOfWorkers, numberOfSites (0 = empty plan still billed). GET /subscriptions/{id}/providers/Microsoft.Web/sites?api-version=2025-05-01 — all web/function apps; returns kind (app/functionapp/linux), state (Running/Stopped), serverFarmId (linked plan), httpsOnly, clientAffinityEnabled, siteConfig.alwaysOn. For cost optimization: find empty App Service plans (numberOfSites=0 still billed), over-provisioned plans, stopped apps on paid plans, F1→B1 upgrade opportunities.

=== LOG ANALYTICS (Microsoft.OperationalInsights, api-version=2025-07-01) ===
GET /subscriptions/{id}/providers/Microsoft.OperationalInsights/workspaces?api-version=2025-07-01 — all Log Analytics workspaces; returns sku (PerGB2018/CapacityReservation/Free), retentionInDays, dailyQuotaGb (ingestion cap), workspaceCapping, customerId (workspace GUID). GET /subscriptions/{id}/resourceGroups/{rg}/providers/Microsoft.OperationalInsights/workspaces/{name}/usages?api-version=2025-07-01 — data ingestion volume per data type (SecurityEvent, Perf, Heartbeat, Syslog, etc.) in bytes — identify top ingestion cost drivers. GET /subscriptions/{id}/resourceGroups/{rg}/providers/Microsoft.OperationalInsights/workspaces/{name}/tables?api-version=2025-07-01 — table-level config: plan (Analytics=full query/$$$, Basic=limited query/$$, Archive=cheapest/no query), retentionInDays, totalRetentionInDays. For cost optimization: move verbose tables to Basic/Archive plan, reduce retention, identify tables ingesting >1GB/day, evaluate CapacityReservation tier.

=== AZURE ML (Microsoft.MachineLearningServices, api-version=2026-03-01) ===
GET /subscriptions/{id}/providers/Microsoft.MachineLearningServices/workspaces?api-version=2026-03-01 — all ML workspaces with sku, storageAccount, keyVault associations. GET /subscriptions/{id}/resourceGroups/{rg}/providers/Microsoft.MachineLearningServices/workspaces/{name}/computes?api-version=2026-03-01 — compute resources: type (ComputeInstance/AmlCompute/Kubernetes), vmSize (Standard_NC6s_v3, Standard_ND96asr_v4 etc.), state (Running/Stopped/Creating), scaleSettings (minNodeCount/maxNodeCount), idleTimeBeforeShutdown. Find idle GPU instances, over-provisioned clusters, and instances left running without auto-shutdown. GET .../workspaces/{name}/onlineEndpoints?api-version=2026-03-01 — managed online endpoints with deployment details, instance count, sku for inference cost analysis. GET .../workspaces/{name}/batchEndpoints?api-version=2026-03-01 — batch inference endpoints.

=== COGNITIVE SERVICES / AZURE AI FOUNDRY / AZURE OPENAI (Microsoft.CognitiveServices, api-version=2026-03-01) ===
Use this for all Foundry / Azure OpenAI questions — model deployments, quota, available models, capacity per region. The user's deployed models AND their per-region quota live here, not in prices.azure.com.
GET /subscriptions/{id}/providers/Microsoft.CognitiveServices/accounts?api-version=2026-03-01 — all Cognitive Services / Foundry / Azure OpenAI accounts in the subscription. Returns kind (OpenAI, AIServices, ComputerVision, SpeechServices, etc.), sku.name (S0, F0), location, properties.endpoint, properties.customSubDomainName.
GET /subscriptions/{id}/resourceGroups/{rg}/providers/Microsoft.CognitiveServices/accounts/{accountName}/deployments?api-version=2026-03-01 — model deployments on a specific account. Returns properties.model.name (e.g. gpt-5.5, gpt-4o, o3-mini), properties.model.version, properties.model.format (OpenAI), sku.name (Standard/GlobalStandard/DataZoneStandard/ProvisionedManaged), sku.capacity (TPM in thousands), properties.currentCapacity, properties.rateLimits. Use to map deployments to actual capacity consumption.
GET /subscriptions/{id}/providers/Microsoft.CognitiveServices/locations/{region}/usages?api-version=2026-03-01 — PER-REGION QUOTA USAGE (the canonical Foundry quota check). Returns an array of {name.value (e.g. 'OpenAI.GlobalStandard.gpt-5.5', 'OpenAI.Standard.gpt-4o', 'AccountQuota.AIServices.S0'), name.localizedValue, currentValue, limit, unit, status}. To check 'how much gpt-5 quota do I have left in Sweden Central?', call this endpoint with region=swedencentral and filter the response client-side by name.value containing 'gpt-5'. Equivalent CLI: `az cognitiveservices usage list --location {region}` — ARM URL: `https://management.azure.com/subscriptions/{subId}/providers/Microsoft.CognitiveServices/locations/{region}/usages?api-version=2026-03-01`.
GET /subscriptions/{id}/providers/Microsoft.CognitiveServices/locations/{region}/models?api-version=2026-03-01 — ALL models available in a region with their supported SKUs and capacity caps. Returns model.name, model.version, model.format, model.skus[].name (Standard/GlobalStandard/DataZoneStandard/ProvisionedManaged), model.skus[].capacity (default/maximum/minimum/step in TPM units). Use to answer 'is gpt-5.5 available in westeurope?' or 'what's the max TPM for o3 GlobalStandard in eastus2?'.
GET /subscriptions/{id}/resourceGroups/{rg}/providers/Microsoft.CognitiveServices/accounts/{accountName}/usages?api-version=2026-03-01 — per-account usage counters.
GET /subscriptions/{id}/resourceGroups/{rg}/providers/Microsoft.CognitiveServices/accounts/{accountName}/models?api-version=2026-03-01 — models supported by a specific account (filtered by account kind).
For cost optimization: identify deployments with sku.capacity (TPM) far above currentCapacity (over-provisioned), find ProvisionedManaged deployments with low utilization (PTUs are billed hourly regardless of use), spot deployments of legacy/expensive models that should migrate to gpt-4o-mini / gpt-5-nano, audit accounts with kind=OpenAI in many regions (each adds an endpoint and quota footprint).

=== DATABRICKS (Microsoft.Databricks, api-version=2026-01-01) ===
GET /subscriptions/{id}/providers/Microsoft.Databricks/workspaces?api-version=2026-01-01 — all Databricks workspaces; returns sku (standard/premium/trial — premium costs more but adds RBAC, audit logs, Unity Catalog), managedResourceGroupId (contains VMs, disks, NSGs billed to your subscription), provisioningState, authorizations, storageAccountIdentity. For cost optimization: compare premium vs standard tier justification, check managed RG for orphaned resources, evaluate workspace consolidation.

=== COSMOS DB (Microsoft.DocumentDB, api-version=2025-10-15) ===
GET /subscriptions/{id}/providers/Microsoft.DocumentDB/databaseAccounts?api-version=2025-10-15 — all Cosmos DB accounts; returns databaseAccountOfferType (Standard), consistencyPolicy (Strong/Bounded/Session/Eventual — affects cost), locations (multi-region write = 2x cost), capabilities (e.g. EnableServerless), enableFreeTier, enableAutomaticFailover, backupPolicy (Periodic/Continuous). GET /subscriptions/{id}/resourceGroups/{rg}/providers/Microsoft.DocumentDB/databaseAccounts/{name}/sqlDatabases?api-version=2025-10-15 — SQL databases with resource.id. GET .../sqlDatabases/{dbName}/containers?api-version=2025-10-15 — containers with partition key and indexing policy. GET .../sqlDatabases/{dbName}/throughputSettings/default?api-version=2025-10-15 — database-level throughput (RU/s): manual vs autoscale, current RU/s value, maxThroughput for autoscale. For cost optimization: identify over-provisioned RU/s (manual > actual usage), evaluate serverless vs provisioned, multi-region write cost, consistency level cost impact.

=== REDIS (Microsoft.Cache, api-version=2024-11-01) ===
GET /subscriptions/{id}/providers/Microsoft.Cache/redis?api-version=2024-11-01 — all Redis Cache instances; returns sku (name=Basic/Standard/Premium, family=C/P, capacity=0-6 mapping to cache size), hostName, port, sslPort, provisioningState, redisVersion, linkedServers (geo-replication adds cost), instances (shards for Premium), minimumTlsVersion. For cost optimization: downsize over-provisioned caches (Premium→Standard if clustering not needed), identify Basic tier in production (no SLA), find unused caches via connection metrics.

=== DATA FACTORY (Microsoft.DataFactory, api-version=2018-06-01) ===
GET /subscriptions/{id}/providers/Microsoft.DataFactory/factories?api-version=2018-06-01 — all ADF instances; returns name, location, provisioningState, repoConfiguration (Git integration), globalParameters, encryption. GET /subscriptions/{id}/resourceGroups/{rg}/providers/Microsoft.DataFactory/factories/{name}/pipelines?api-version=2018-06-01 — all pipelines with activities (Copy, DataFlow, HDInsight, Databricks, SQL), parameters, and concurrency settings. GET .../factories/{name}/datasets?api-version=2018-06-01 — datasets (linked data sources). GET .../factories/{name}/linkedservices?api-version=2018-06-01 — linked services (connections to storage, SQL, Databricks, etc.). GET .../factories/{name}/triggers?api-version=2018-06-01 — triggers (schedule, tumbling window, event) with frequency/interval for run cost projection. GET .../factories/{name}/integrationRuntimes?api-version=2018-06-01 — integration runtimes: type (Managed/SelfHosted), computeProperties (location, nodeSize=Standard_D1_v2 etc., numberOfNodes, maxParallelExecutionsPerNode) — self-hosted IR nodes are customer-managed VMs. For cost optimization: identify idle pipelines, over-provisioned SSIS IR clusters, optimize Data Flow compute type/core counts.

=== SYNAPSE (Microsoft.Synapse, api-version=2023-05-01) ===
GET /subscriptions/{id}/providers/Microsoft.Synapse/workspaces?api-version=2023-05-01 — all Synapse workspaces with managedResourceGroupName, defaultDataLakeStorage, sqlAdministratorLogin. GET /subscriptions/{id}/resourceGroups/{rg}/providers/Microsoft.Synapse/workspaces/{name}/sqlPools?api-version=2023-05-01 — dedicated SQL pools; returns sku (name=DW100c through DW30000c — DWU tier), status (Online/Paused — paused pools don't incur compute cost), collation, maxSizeBytes, storageAccountType. GET .../workspaces/{name}/bigDataPools?api-version=2023-05-01 — Spark pools; returns nodeSize (Small/Medium/Large/XLarge/XXLarge), nodeSizeFamily (MemoryOptimized), nodeCount, autoScale (enabled, minNodeCount, maxNodeCount), autoPause (enabled, delayInMinutes), sparkVersion. For cost optimization: pause idle dedicated SQL pools, right-size DWU tier, enable Spark auto-pause and auto-scale, evaluate serverless on-demand vs provisioned.

=== CONTAINER APPS (Microsoft.App, api-version=2026-01-01) ===
GET /subscriptions/{id}/providers/Microsoft.App/containerApps?api-version=2026-01-01 — all Container Apps; returns configuration (activeRevisionsMode, ingress with targetPort/transport/traffic weights), template (containers with image/resources.cpu/resources.memory, scale with minReplicas/maxReplicas/rules), provisioningState, latestRevisionFqdn. GET /subscriptions/{id}/providers/Microsoft.App/managedEnvironments?api-version=2026-01-01 — Container App environments; returns sku (name=Consumption/Premium), vnetConfiguration, zoneRedundant, workloadProfiles (Consumption/Dedicated with workloadProfileType like D4/D8/E4/E8/NC24-A100). For cost optimization: ensure minReplicas=0 for non-critical apps (scale to zero), right-size CPU/memory per container, evaluate Consumption vs Dedicated workload profiles.

=== RESOURCE HEALTH (Microsoft.ResourceHealth, api-version=2025-05-01) ===
GET /{resourceId}/providers/Microsoft.ResourceHealth/availabilityStatuses/current?api-version=2025-05-01 — current availability status (Available/Unavailable/Degraded/Unknown) with reasonType and summary. GET /subscriptions/{id}/providers/Microsoft.ResourceHealth/availabilityStatuses?api-version=2025-05-01 — availability status of all resources in subscription. Useful for identifying resources with frequent outages that may indicate over/under-provisioning.

=== SECURITY (Microsoft.Security) ===
GET /subscriptions/{id}/providers/Microsoft.Security/assessments?api-version=2025-05-04 — all Defender for Cloud security assessments; each returns displayName, status.code (Healthy/Unhealthy/NotApplicable), resourceDetails.id (affected resource), severity, category (Compute/Networking/Data/IdentityAndAccess). Use to find unprotected resources that may also be idle/orphaned. GET /subscriptions/{id}/providers/Microsoft.Security/secureScores?api-version=2020-01-01 — overall security score (current/max) and per-control score breakdown.

=== SUBSCRIPTIONS (api-version=2022-12-01) ===
GET /subscriptions?api-version=2022-12-01.

=== CARBON (Microsoft.Carbon, api-version=2025-04-01) ===
POST /providers/Microsoft.Carbon/carbonEmissionReports?api-version=2025-04-01 — generate carbon emission reports for Azure resources (body: {reportType, subscriptionIds, dateRange}). Returns emissions data in kgCO2e by resource type, region, service — correlate with cost data for sustainability-aware FinOps.

=== POLICY (Microsoft.Authorization, api-version=2025-11-01) ===
GET /{scope}/providers/Microsoft.Authorization/policyDefinitions?api-version=2025-11-01 — all policy definitions (built-in + custom). GET /{scope}/providers/Microsoft.Authorization/policyAssignments?api-version=2025-11-01 — all policy assignments with scope, enforcement mode (Default/DoNotEnforce), parameters. POST /{scope}/providers/Microsoft.PolicyInsights/policyStates/latest/summarize?api-version=2024-10-01 — compliance summary: total resources, compliant/non-compliant counts per policy. For FinOps: check tag enforcement policies, allowed VM size policies, region restrictions.

=== RBAC (Microsoft.Authorization, api-version=2022-04-01) ===
GET /{scope}/providers/Microsoft.Authorization/roleAssignments?api-version=2022-04-01 — all role assignments; returns principalId, roleDefinitionId, scope, principalType (User/Group/ServicePrincipal). For FinOps governance: audit who has Contributor/Owner (can create costly resources), identify over-privileged service principals. GET /{scope}/providers/Microsoft.Authorization/roleDefinitions?api-version=2022-04-01 — role definitions with permissions (actions/notActions).

=== LOCKS (Microsoft.Authorization, api-version=2020-05-01) ===
GET /{scope}/providers/Microsoft.Authorization/locks?api-version=2020-05-01 — all resource locks; returns lockLevel (CanNotDelete/ReadOnly), notes, owners. CanNotDelete locks prevent accidental deletion of critical resources; ReadOnly locks prevent any modification. For FinOps: verify critical resources are locked, identify resources that can't be cleaned up due to locks.

=== MGMT GROUPS (Microsoft.Management, api-version=2023-04-01) ===
GET /providers/Microsoft.Management/managementGroups?api-version=2023-04-01 — top-level management groups (org hierarchy for cost governance). GET /providers/Microsoft.Management/managementGroups/{groupId}/descendants?api-version=2023-04-01 — all child groups and subscriptions under a management group. POST /providers/Microsoft.Management/getEntities?api-version=2023-04-01 — full entity tree (management groups + subscriptions) for org-wide cost visibility.

=== TAGS (Microsoft.Resources, api-version=2023-07-01) ===
GET /subscriptions/{id}/tagNames?api-version=2023-07-01 — all tag names used in subscription with value counts. GET /subscriptions/{id}/resources?api-version=2023-07-01 — list all resources with type, location, tags, sku. Use for: resource inventory, untagged resource discovery (resources missing CostCenter/Environment/Owner tags), tag coverage analysis for chargeback.

=== MIGRATE (Microsoft.Migrate, api-version=2024-01-15) ===
GET /subscriptions/{id}/providers/Microsoft.Migrate/assessmentProjects?api-version=2024-01-15 — list all Azure Migrate assessment projects in the subscription (the canonical resource type — `migrateProjects` is NOT a valid type and returns 404 InvalidResourceType). GET /subscriptions/{id}/resourceGroups/{rg}/providers/Microsoft.Migrate/assessmentProjects/{project}/assessments?api-version=2024-01-15 — migration assessments with target Azure sizing recommendations, estimated monthly cost, readiness status (Ready/ConditionallyReady/NotReady). GET .../assessments/{name}/assessedMachines?api-version=2024-01-15 — individual assessed machines with recommended VM size, disk type, estimated cost — use for pre-migration cost projection. Discovery sources live under a separate provider: GET /subscriptions/{id}/providers/Microsoft.OffAzure/MasterSites?api-version=2023-06-06.

=== SUPPORT (Microsoft.Support, api-version=2024-04-01) ===
GET /subscriptions/{id}/providers/Microsoft.Support/supportTickets?api-version=2024-04-01 — all support tickets with severity (Minimal/Moderate/Critical/Highestcriticalimpact), status (Open/Closed), title, createdDate, serviceId. GET /providers/Microsoft.Support/services?api-version=2024-04-01 — available Azure services for support ticket categorization.

Scope = /subscriptions/{subId} or /subscriptions/{subId}/resourceGroups/{rg}.
For retail pricing use the built-in fetch tool with https://prices.azure.com (no auth required). ALWAYS filter by armRegionName + serviceName + armSkuName and use $top=20 for comparisons.
SECURITY: GET, POST, PUT, PATCH allowed. DELETE blocked. The user's Entra RBAC role is the effective access boundary.");

        yield return AIFunctionFactory.Create(BulkAzureRequest, "BulkAzureRequest", @"Executes MANY Azure ARM requests in ONE tool call, in parallel, server-side. Use this whenever you would otherwise loop QueryAzure for the same kind of operation across multiple resources (bulk tagging, cleanup discovery, autoshutdown rollout, budget rollout across subs, multi-resource right-sizing, RBAC fan-out, etc.).
Input: requestsJson = JSON array of {""method"":""GET|POST|PUT|PATCH"",""path"":""/...?api-version=..."",""body"":""<optional JSON string>""}.
Optional: parallelism (default 20, max 50), stopOnFirstError (default false).
Returns ONE compact JSON summary: {""total"":N,""succeeded"":X,""failed"":Y,""durationMs"":Z,""failures"":[{""index"":i,""status"":code,""path"":""..."",""error"":""...""}],""successSamples"":[{""path"":""..."",""name"":""...""}]}.
DELETE is still blocked at the code level. Same per-request response trimming as QueryAzure (PUT/PATCH echoes are compacted). Throttling-aware: 429 retries are handled per request, batches stay below ARM's 1200 writes/hour/sub.
Use this INSTEAD of looping QueryAzure when you have ≥5 similar requests. Build the request list from your prior Resource Graph discovery query in the same turn.");
    }

    private async Task<string> QueryAzure(
        [Description("HTTP method: GET, POST, PUT, or PATCH (DELETE is blocked)")] string method,
        [Description("API path starting with /, e.g. /subscriptions?api-version=2022-12-01")] string path,
        [Description("Optional JSON request body for POST/PUT/PATCH requests. Omit or leave empty for GET.")] string? body = null)
    {
        using var activity = HttpHelper.Telemetry.StartActivity("QueryAzure");
        activity?.SetTag("azure.method", method);
        activity?.SetTag("azure.path", path);
        activity?.SetTag("azure.has_body", !string.IsNullOrWhiteSpace(body));
        if (!string.IsNullOrWhiteSpace(body))
            activity?.SetTag("azure.body", body.Length > 2000 ? body[..2000] + "..." : body);

        var token = _tokens.AzureToken;
        if (string.IsNullOrEmpty(token))
            return HttpHelper.TokenMissing("AzureToken", activity, "azure");

        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/'))
        {
            activity?.SetTag("azure.result", "invalid_path");
            activity?.SetStatus(ActivityStatusCode.Error, "Invalid path");
            return $"HTTP 400 BadRequest\nInvalid path: '{path}'. Path must start with /.";
        }

        // Scope-prefix preflight: catches the #1 production failure pattern observed in App Insights —
        // the LLM emitting bare /providers/Microsoft.CostManagement|Consumption|... paths without the
        // required {scope} prefix (subscriptions / resourceGroups / managementGroups / billingAccounts).
        // ARM responds 404 InvalidResourceType in that case; we return a precise 400 with the grammar
        // so the LLM corrects on the next turn instead of burning a round-trip.
        var scopeError = ValidateScopePrefix(path);
        if (scopeError is not null)
        {
            activity?.SetTag("azure.result", "missing_scope");
            activity?.SetStatus(ActivityStatusCode.Error, "Missing scope prefix");
            return scopeError;
        }

        var (httpMethod, methodError) = HttpHelper.ResolveMethod(method, activity, "azure");
        if (methodError is not null) return methodError;

        var hasBody = !string.IsNullOrWhiteSpace(body);
        return await HttpHelper.SendWithRetryAsync(
            $"https://management.azure.com{path}",
            token, activity, "azure",
            method: httpMethod,
            jsonBody: hasBody && httpMethod != HttpMethod.Get ? body : null,
            includeTimestamp: true);
    }

    /// <summary>
    /// Returns null if the path is acceptable, otherwise a ready-to-return HTTP 400 message explaining
    /// the missing {scope} prefix. Scope-required providers (Cost Management, Consumption budgets,
    /// PolicyInsights states, etc.) MUST be prefixed with one of the five canonical scope shapes.
    /// Bare /providers/Microsoft.CostManagement/query was 5/27 of all 4xx failures in the last 5 days.
    /// </summary>
    private static string? ValidateScopePrefix(string path)
    {
        // Strip query string for the check
        var qIdx = path.IndexOf('?');
        var clean = qIdx >= 0 ? path[..qIdx] : path;

        // Only enforce on the providers that actually require {scope}. Cost Management is the big one.
        // Consumption/budgets and PolicyInsights/policyStates also require it. ResourceGraph, Capacity,
        // BillingBenefits, Billing, Advisor, etc. live at root and are unaffected.
        string[] scopeRequired =
        [
            "/providers/Microsoft.CostManagement/",
            "/providers/Microsoft.Consumption/budgets",
            "/providers/Microsoft.PolicyInsights/policyStates",
        ];

        foreach (var marker in scopeRequired)
        {
            if (!clean.Contains(marker, StringComparison.OrdinalIgnoreCase)) continue;
            // Acceptable: the marker is preceded by a valid scope segment.
            if (clean.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase)
                || clean.StartsWith("/providers/Microsoft.Management/managementGroups/", StringComparison.OrdinalIgnoreCase)
                || clean.StartsWith("/providers/Microsoft.Billing/billingAccounts/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return "HTTP 400 BadRequest\n" +
                   $"Path is missing the required {{scope}} prefix before '{marker.TrimEnd('/')}'.\n" +
                   "Prepend exactly ONE of:\n" +
                   "  /subscriptions/{subId}\n" +
                   "  /subscriptions/{subId}/resourceGroups/{rgName}\n" +
                   "  /providers/Microsoft.Management/managementGroups/{mgId}\n" +
                   "  /providers/Microsoft.Billing/billingAccounts/{billingAccountId}\n" +
                   "  /providers/Microsoft.Billing/billingAccounts/{billingAccountId}/billingProfiles/{profileId}\n" +
                   "Example: POST /subscriptions/abc-123/providers/Microsoft.CostManagement/query?api-version=2025-03-01";
        }
        return null;
    }

    private async Task<string> BulkAzureRequest(
        [Description("JSON array of {method,path,body?} objects, e.g. [{\"method\":\"PATCH\",\"path\":\"/subscriptions/.../tags/default?api-version=2021-04-01\",\"body\":\"{...}\"}]")] string requestsJson,
        [Description("Max parallel requests in flight. Default 20, max 50.")] int parallelism = 20,
        [Description("Stop the whole bulk run on the first failure. Default false (continue and report all failures).")] bool stopOnFirstError = false)
    {
        using var activity = HttpHelper.Telemetry.StartActivity("BulkAzureRequest");
        var token = _tokens.AzureToken;
        if (string.IsNullOrEmpty(token))
            return HttpHelper.TokenMissing("AzureToken", activity, "bulk");

        if (string.IsNullOrWhiteSpace(requestsJson))
            return "HTTP 400 BadRequest\nrequestsJson is empty.";

        List<BulkRequestItem>? items;
        try
        {
            items = JsonSerializer.Deserialize<List<BulkRequestItem>>(
                requestsJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            return $"HTTP 400 BadRequest\nInvalid requestsJson: {ex.Message}";
        }
        if (items is null || items.Count == 0)
            return "HTTP 400 BadRequest\nrequestsJson must be a non-empty JSON array.";

        var maxPar = Math.Clamp(parallelism, 1, 50);
        activity?.SetTag("bulk.total", items.Count);
        activity?.SetTag("bulk.parallelism", maxPar);

        var sw = Stopwatch.StartNew();
        var results = new BulkResult[items.Count];
        var cts = new CancellationTokenSource();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, items.Count),
            new ParallelOptions { MaxDegreeOfParallelism = maxPar, CancellationToken = cts.Token },
            async (i, ct) =>
            {
                var item = items[i];
                var (httpMethod, methodError) = HttpHelper.ResolveMethod(item.Method, activity, "bulk");
                if (methodError is not null)
                {
                    results[i] = new BulkResult(i, 0, item.Path ?? "", methodError, false);
                    if (stopOnFirstError) cts.Cancel();
                    return;
                }
                if (string.IsNullOrWhiteSpace(item.Path) || !item.Path.StartsWith('/'))
                {
                    results[i] = new BulkResult(i, 400, item.Path ?? "", $"Invalid path: '{item.Path}'", false);
                    if (stopOnFirstError) cts.Cancel();
                    return;
                }

                var hasBody = !string.IsNullOrWhiteSpace(item.Body);
                var resp = await HttpHelper.SendWithRetryAsync(
                    $"https://management.azure.com{item.Path}",
                    token, activity, "bulk",
                    method: httpMethod,
                    jsonBody: hasBody && httpMethod != HttpMethod.Get ? item.Body : null,
                    includeTimestamp: false,
                    maxResponseChars: 1024); // hard cap so a stray verbose 4xx doesn't blow up the summary

                // Parse the "HTTP {code} {reason}\n{body}" envelope SendWithRetryAsync returns.
                var firstLine = resp.IndexOf('\n');
                var statusLine = firstLine > 0 ? resp[..firstLine] : resp;
                var statusParts = statusLine.Split(' ', 3);
                int.TryParse(statusParts.ElementAtOrDefault(1), out var status);
                var ok = status >= 200 && status < 300;
                var bodyPart = firstLine > 0 ? resp[(firstLine + 1)..] : "";

                string? name = null;
                try
                {
                    var idx = bodyPart.IndexOf('{');
                    if (idx >= 0)
                    {
                        using var doc = JsonDocument.Parse(bodyPart[idx..]);
                        if (doc.RootElement.ValueKind == JsonValueKind.Object
                            && doc.RootElement.TryGetProperty("name", out var n))
                            name = n.GetString();
                    }
                }
                catch { /* ignore */ }

                results[i] = new BulkResult(i, status, item.Path, ok ? null : bodyPart, ok, name);
                if (!ok && stopOnFirstError) cts.Cancel();
            });

        sw.Stop();
        var succeeded = results.Count(r => r is not null && r.Ok);
        var failed = results.Count(r => r is not null && !r.Ok);
        activity?.SetTag("bulk.succeeded", succeeded);
        activity?.SetTag("bulk.failed", failed);
        activity?.SetTag("bulk.duration_ms", sw.ElapsedMilliseconds);

        var failuresPayload = results
            .Where(r => r is not null && !r.Ok)
            .Take(20)
            .Select(r => new
            {
                index = r!.Index,
                status = r.Status,
                path = r.Path,
                error = (r.Error ?? "").Length > 200 ? r.Error![..200] : r.Error
            });

        var successSamples = results
            .Where(r => r is not null && r.Ok)
            .Take(5)
            .Select(r => new { path = r!.Path, name = r.Name });

        var summary = new
        {
            total = items.Count,
            succeeded,
            failed,
            durationMs = sw.ElapsedMilliseconds,
            stopped = cts.IsCancellationRequested && stopOnFirstError,
            failures = failuresPayload,
            successSamples
        };
        return JsonSerializer.Serialize(summary);
    }

    private sealed class BulkRequestItem
    {
        public string Method { get; set; } = "GET";
        public string Path { get; set; } = "";
        public string? Body { get; set; }
    }

    private sealed record BulkResult(int Index, int Status, string Path, string? Error, bool Ok, string? Name = null);
}
