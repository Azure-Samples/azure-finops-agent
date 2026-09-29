import { buildSiteConfig } from 'site-config.bicep'

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
param aoaiWebSearch bool = true
param entraAppId string
@secure()
param entraClientSecret string
param entraTenantId string

@description('Canonical public hostname (bare, no scheme), e.g. contoso.com. Marks every other host — including the default *.azurewebsites.net — as noindex so search engines do not split ranking across duplicates. Empty disables the check.')
param publicSiteHost string = ''

var planTier = startsWith(appServicePlanSku, 'B') ? 'Basic' : (startsWith(appServicePlanSku, 'S') ? 'Standard' : 'PremiumV3')

var siteConfig = buildSiteConfig(acrLoginServer, containerImageName, appInsightsConnectionString, aoaiEndpoint,
  aoaiDeploymentName, aoaiReasoningEffort, aoaiWebSearch, entraAppId, entraClientSecret, entraTenantId, publicSiteHost)

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

output name string = webApp.name
output hostname string = webApp.properties.defaultHostName
output principalId string = webApp.identity.principalId
// Consumed by modules/dns.bicep to build the asuid ownership TXT record for a
// custom domain. The inbound VIP for the apex A record is NOT available here —
// it is absent from SiteProperties — so it is passed into the DNS module as a
// parameter instead.
output customDomainVerificationId string = webApp.properties.customDomainVerificationId
