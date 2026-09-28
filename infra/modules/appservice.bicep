param location string
param resourceToken string
param tags object
param appServicePlanSku string
param acrLoginServer string
param containerImageName string
@secure()
param appInsightsConnectionString string
param aoaiEndpoint string
param aoaiDeploymentName string
@allowed(['low', 'medium', 'high', 'xhigh'])
param aoaiReasoningEffort string = 'high'
param entraAppId string
@secure()
param entraClientSecret string
param entraTenantId string

@description('Canonical public hostname (bare, no scheme), e.g. contoso.com. Marks every other host — including the default *.azurewebsites.net — as noindex so search engines do not split ranking across duplicates. Empty disables the check.')
param publicSiteHost string = ''

@description('Optional deployment slot for previews (for example `test`). Empty creates no slot. Requires a Standard or Premium plan; Basic has no slots.')
param previewSlotName string = ''

var planTier = startsWith(appServicePlanSku, 'B') ? 'Basic' : (startsWith(appServicePlanSku, 'S') ? 'Standard' : 'PremiumV3')

// Shared by the web app and the optional preview slot so a slot starts from the
// exact production configuration. Feature deployments later replace only the
// slot's image and evaluated model settings.
var siteConfig = {
  linuxFxVersion: 'DOCKER|${acrLoginServer}/${containerImageName}'
  acrUseManagedIdentityCreds: true
  // Keep the container resident so the first request after idle doesn't pay a
  // container cold start. Always On is supported on Basic and above (all SKUs
  // allowed by main.bicep).
  alwaysOn: true
  http20Enabled: true
  ftpsState: 'Disabled'
  minTlsVersion: '1.2'
  healthCheckPath: '/api/version'
  appSettings: [
    // Tells App Service which port the container listens on (matches Dockerfile EXPOSE 8080).
    { name: 'WEBSITES_PORT', value: '8080' }
    // CRITICAL: mount the persistent /home Azure Files share into the container.
    // Chat history, Data Protection keys, and persisted identities (refresh
    // tokens) all live under /home — with this 'false' every restart/deploy
    // wiped them: users were silently logged out (cookie decrypt failed with
    // "key not found in the key ring") and all conversations disappeared.
    { name: 'WEBSITES_ENABLE_APP_SERVICE_STORAGE', value: 'true' }
    // Root for sessions, identities, jobs, uploads and artifacts on the
    // persistent mount. The name predates the Agent Framework runtime.
    { name: 'COPILOT_HOME', value: '/home/copilot' }
    // The image carries .NET plus a Python data stack; the first cold start
    // after a pull can exceed the 230s default. Allow up to 30 min to warm up.
    { name: 'WEBSITES_CONTAINER_START_TIME_LIMIT', value: '1800' }
    { name: 'DOCKER_REGISTRY_SERVER_URL', value: 'https://${acrLoginServer}' }
    // Foundry project endpoint and model deployment (Program.cs fail-fast key).
    // Inference authenticates with the running site's managed identity.
    { name: 'AzureOpenAI__Endpoint', value: aoaiEndpoint }
    { name: 'AzureOpenAI__DeploymentName', value: aoaiDeploymentName }
    // Reasoning effort for reasoning-capable models. CI deployments replace
    // it with the effort the live evaluation gate tested.
    { name: 'AzureOpenAI__ReasoningEffort', value: aoaiReasoningEffort }
    // Entra ID OAuth (multi-tenant). Empty values disable OAuth gracefully.
    { name: 'Microsoft__ClientId', value: entraAppId }
    { name: 'Microsoft__ClientSecret', value: entraClientSecret }
    { name: 'Microsoft__TenantId', value: entraTenantId }
    // Application Insights — Program.cs reads ApplicationInsights__ConnectionString;
    // APPLICATIONINSIGHTS_CONNECTION_STRING is the standard name that Azure
    // Monitor tooling and App Service integration recognize.
    { name: 'ApplicationInsights__ConnectionString', value: appInsightsConnectionString }
    { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
    { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
    { name: 'PUBLIC_SITE_HOST', value: publicSiteHost }
  ]
}

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: 'plan-finops-${resourceToken}'
  location: location
  tags: tags
  kind: 'linux'
  sku: {
    name: appServicePlanSku
    tier: planTier
  }
  properties: {
    reserved: true // Linux
  }
}

resource webApp 'Microsoft.Web/sites@2024-04-01' = {
  name: 'app-finops-${resourceToken}'
  location: location
  tags: union(tags, { 'azd-service-name': 'web' })
  kind: 'app,linux,container'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    publicNetworkAccess: 'Enabled'
    siteConfig: siteConfig
  }
}

// A slot has its own system-assigned identity; roles.bicep grants it the same
// registry and Foundry access as the web app. Not tagged `azd-service-name`, so
// `azd deploy` still targets only the production site.
resource previewSlot 'Microsoft.Web/sites/slots@2024-04-01' = if (!empty(previewSlotName)) {
  parent: webApp
  name: empty(previewSlotName) ? 'unused' : previewSlotName
  location: location
  tags: tags
  kind: 'app,linux,container'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    publicNetworkAccess: 'Enabled'
    siteConfig: siteConfig
  }
}

output name string = webApp.name
output hostname string = webApp.properties.defaultHostName
output principalId string = webApp.identity.principalId
output slotName string = previewSlotName
output slotHostname string = empty(previewSlotName) ? '' : previewSlot!.properties.defaultHostName
output slotPrincipalId string = empty(previewSlotName) ? '' : previewSlot!.identity.principalId
// Consumed by modules/dns.bicep to build the asuid ownership TXT record for a
// custom domain. The inbound VIP for the apex A record is NOT available here —
// it is absent from SiteProperties — so it is passed into the DNS module as a
// parameter instead.
output customDomainVerificationId string = webApp.properties.customDomainVerificationId
