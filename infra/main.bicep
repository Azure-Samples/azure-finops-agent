// Subscription-scope entry point for `azd up`.
// Creates the resource group and delegates everything else to main-resources.bicep.
targetScope = 'subscription'

@minLength(1)
@maxLength(64)
@description('Name of the azd environment. Used to derive resource names and tags.')
param environmentName string

@allowed([
  'australiaeast'
  'brazilsouth'
  'canadacentral'
  'canadaeast'
  'centralus'
  'eastus'
  'eastus2'
  'francecentral'
  'germanywestcentral'
  'italynorth'
  'japaneast'
  'koreacentral'
  'northcentralus'
  'northeurope'
  'norwayeast'
  'polandcentral'
  'southafricanorth'
  'southcentralus'
  'southeastasia'
  'southindia'
  'spaincentral'
  'swedencentral'
  'switzerlandnorth'
  'switzerlandwest'
  'uaenorth'
  'uksouth'
  'westeurope'
  'westus'
  'westus3'
])
@description('Primary Azure region for the resource group and non-AOAI resources. Model availability and quota are verified separately in aoaiLocation.')
param location string

@allowed([
  'australiaeast'
  'brazilsouth'
  'canadacentral'
  'canadaeast'
  'centralus'
  'eastus'
  'eastus2'
  'francecentral'
  'germanywestcentral'
  'italynorth'
  'japaneast'
  'koreacentral'
  'northcentralus'
  'northeurope'
  'norwayeast'
  'polandcentral'
  'southafricanorth'
  'southcentralus'
  'southeastasia'
  'southindia'
  'spaincentral'
  'swedencentral'
  'switzerlandnorth'
  'switzerlandwest'
  'uaenorth'
  'uksouth'
  'westeurope'
  'westus'
  'westus3'
])
@description('Azure region for the Azure OpenAI account. Verify the selected model/version and available GlobalStandard quota in this region before deployment. May differ from location. Default: swedencentral.')
param aoaiLocation string = 'swedencentral'

@allowed(['B1', 'B2', 'B3', 'S1', 'S2', 'S3', 'P0V3', 'P1V3', 'P2V3', 'P3V3'])
@description('App Service Plan SKU. B1 (~$13/mo) is the recommended evaluation default; P0V3 matches production.')
param appServicePlanSku string = 'B1'

@description('Azure OpenAI model name to deploy. The default gpt-5.6-luna supports the Responses API used by this agent; verify availability in aoaiLocation.')
param aoaiModelName string = 'gpt-5.6-luna'

@description('Azure OpenAI model version. Must match the model name: gpt-5.6-luna = 2026-07-09.')
param aoaiModelVersion string = '2026-07-09'

@description('Azure OpenAI deployment name surfaced as `AzureOpenAI__DeploymentName` to the app.')
param aoaiDeploymentName string = 'gpt-5.6-luna'

@description('Azure OpenAI GlobalStandard deployment capacity in model-specific quota units. For gpt-5.6-luna, 1000 units corresponds to 1M tokens/minute. Verify unallocated quota for the model, SKU and region; quota already assigned to other deployments is not available. Lower this value when needed. The service is billed by usage, not reserved throughput.')
param aoaiModelCapacity int = 1000

@description('Optional resource ID of an existing Azure OpenAI account to reuse instead of creating a new one. When set, `aoaiLocation`/`aoaiModelName`/`aoaiModelVersion` are ignored — the deployment must already exist on the existing account.')
param existingAoaiResourceId string = ''

@description('Entra ID multi-tenant app registration client ID. Created automatically by the preprovision hook if empty.')
param entraAppId string = ''

@secure()
@description('Entra ID app registration client secret. Created automatically by the preprovision hook if empty.')
param entraClientSecret string = ''

@description('Entra tenant ID for OAuth — `common` for multi-tenant. Leave default unless restricting to a single tenant.')
param entraTenantId string = 'common'

@description('Optional custom domain to serve the app on, e.g. contoso.com. When set, an Azure DNS zone is created with the apex/www/asuid/CAA/SPF/DMARC records already in place, and the app marks every other hostname noindex. You must then delegate the domain to the returned nameservers at your registrar and bind the hostnames — see .github/prompts/migrate-tenant.prompt.md. Empty skips all DNS.')
param customDomainName string = ''

@description('Mailbox for DMARC aggregate reports and CAA violation reports on the custom domain. Ignored when customDomainName is empty.')
param dmarcReportEmail string = ''

@description('Apply CanNotDelete locks to the DNS zone. Leave false for evaluation deployments so `azd down` tears the environment down cleanly; set true for a long-lived production domain where an accidental delete would mean a registrar change and DNS re-propagation.')
param enableDeleteLocks bool = false

@description('Comma-separated App Service inbound VIPs for the custom domain apex A record. Only knowable after the web app exists, so leave empty on the first `azd up` — the zone is created without an apex record and the runbook adds them. Ignored when customDomainName is empty.')
param appServiceInboundIp string = ''

var tags = {
  'azd-env-name': environmentName
  application: 'azure-finops-agent'
}

// Globally-unique short token derived from sub + env so multiple users in the
// same subscription/region don't collide on resource names (ACR, Web App).
var resourceToken = toLower(uniqueString(subscription().id, environmentName, location))

resource rg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: 'rg-${environmentName}'
  location: location
  tags: tags
}

module resources 'main-resources.bicep' = {
  name: 'finops-resources'
  scope: rg
  params: {
    location: location
    aoaiLocation: aoaiLocation
    resourceToken: resourceToken
    tags: tags
    appServicePlanSku: appServicePlanSku
    aoaiModelName: aoaiModelName
    aoaiModelVersion: aoaiModelVersion
    aoaiDeploymentName: aoaiDeploymentName
    aoaiModelCapacity: aoaiModelCapacity
    existingAoaiResourceId: existingAoaiResourceId
    entraAppId: entraAppId
    entraClientSecret: entraClientSecret
    entraTenantId: entraTenantId
    customDomainName: customDomainName
    dmarcReportEmail: dmarcReportEmail
    enableDeleteLocks: enableDeleteLocks
    appServiceInboundIp: appServiceInboundIp
  }
}

// ── Outputs ─────────────────────────────────────────────────────────────────
// Surfaced to `azd env` so hooks (and the user) can consume them.

output AZURE_LOCATION string = location
output AZURE_RESOURCE_GROUP string = rg.name
output AZURE_TENANT_ID string = subscription().tenantId
output AZURE_SUBSCRIPTION_ID string = subscription().subscriptionId

output AZURE_CONTAINER_REGISTRY_NAME string = resources.outputs.acrName
output AZURE_CONTAINER_REGISTRY_LOGIN_SERVER string = resources.outputs.acrLoginServer
output AZURE_CONTAINER_REGISTRY_IMAGE string = resources.outputs.containerImageName

output WEB_APP_NAME string = resources.outputs.webAppName
output WEB_APP_HOSTNAME string = resources.outputs.webAppHostname
output WEB_APP_URL string = resources.outputs.webAppUrl
output WEB_APP_PRINCIPAL_ID string = resources.outputs.webAppPrincipalId

output AZURE_OPENAI_ENDPOINT string = resources.outputs.aoaiEndpoint
output AZURE_OPENAI_DEPLOYMENT_NAME string = resources.outputs.aoaiDeploymentName
output AZURE_AI_PROJECT_NAME string = resources.outputs.aiProjectName

output APPLICATIONINSIGHTS_CONNECTION_STRING string = resources.outputs.appInsightsConnectionString
output AZURE_LOG_ANALYTICS_WORKSPACE_ID string = resources.outputs.logAnalyticsWorkspaceId

output AZURE_CUSTOM_DOMAIN string = resources.outputs.customDomainName
output AZURE_DNS_NAME_SERVERS array = resources.outputs.dnsNameServers
