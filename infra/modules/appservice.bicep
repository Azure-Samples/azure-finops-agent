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
param entraAppId string
@secure()
param entraClientSecret string
param entraTenantId string

var planTier = startsWith(appServicePlanSku, 'B') ? 'Basic' : (startsWith(appServicePlanSku, 'S') ? 'Standard' : 'PremiumV3')

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
    siteConfig: {
      linuxFxVersion: 'DOCKER|${acrLoginServer}/${containerImageName}'
      acrUseManagedIdentityCreds: true
      // Keep the container (and the warm Copilot CLI subprocess) resident so the
      // first request after idle doesn't pay an ~80s cold start. Always On is
      // supported on Basic and above (all SKUs allowed by main.bicep).
      alwaysOn: true
      http20Enabled: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      healthCheckPath: '/api/version'
      appSettings: [
        // Tells App Service which port the container listens on (matches Dockerfile EXPOSE 8080).
        { name: 'WEBSITES_PORT', value: '8080' }
        { name: 'WEBSITES_ENABLE_APP_SERVICE_STORAGE', value: 'false' }
        // The image is heavy (node + .NET + Python + OTel collector); the first
        // cold start can exceed the 230s default. Allow up to 30 min to warm up.
        { name: 'WEBSITES_CONTAINER_START_TIME_LIMIT', value: '1800' }
        { name: 'DOCKER_REGISTRY_SERVER_URL', value: 'https://${acrLoginServer}' }
        // BYOK Azure OpenAI (Program.cs fail-fast key).
        { name: 'AzureOpenAI__Endpoint', value: aoaiEndpoint }
        { name: 'AzureOpenAI__DeploymentName', value: aoaiDeploymentName }
        // Entra ID OAuth (multi-tenant). Empty values disable OAuth gracefully.
        { name: 'Microsoft__ClientId', value: entraAppId }
        { name: 'Microsoft__ClientSecret', value: entraClientSecret }
        { name: 'Microsoft__TenantId', value: entraTenantId }
        // Application Insights — Program.cs reads ApplicationInsights__ConnectionString,
        // entrypoint.sh's OTel collector reads APPLICATIONINSIGHTS_CONNECTION_STRING.
        { name: 'ApplicationInsights__ConnectionString', value: appInsightsConnectionString }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
        { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
      ]
    }
  }
}

output name string = webApp.name
output hostname string = webApp.properties.defaultHostName
output principalId string = webApp.identity.principalId
