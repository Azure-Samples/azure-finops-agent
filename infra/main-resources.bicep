// Resource-group-scope orchestrator. All app-level resources live here.
targetScope = 'resourceGroup'

param location string
param aoaiLocation string
param resourceToken string
param tags object
param appServicePlanSku string
param aoaiModelName string
param aoaiModelVersion string
param aoaiDeploymentName string
param aoaiModelCapacity int
param existingAoaiResourceId string
param entraAppId string
@secure()
param entraClientSecret string
param entraTenantId string
param customDomainName string
param dmarcReportEmail string
param enableDeleteLocks bool
param appServiceInboundIp string

var containerImageName = 'finops-agent:latest'
// Prefer the custom domain for the synthetic probe when there is one — that is
// the hostname real users hit, and probing it also exercises DNS and the
// custom-domain certificate, which the *.azurewebsites.net host would not.
var publicUrl = empty(customDomainName)
  ? 'https://${appservice.outputs.hostname}'
  : 'https://${customDomainName}'

module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring'
  params: {
    location: location
    resourceToken: resourceToken
    tags: tags
  }
}

module acr 'modules/acr.bicep' = {
  name: 'acr'
  params: {
    location: location
    resourceToken: resourceToken
    tags: tags
  }
}

module aoai 'modules/aoai.bicep' = {
  name: 'aoai'
  params: {
    aoaiLocation: aoaiLocation
    resourceToken: resourceToken
    tags: tags
    modelName: aoaiModelName
    modelVersion: aoaiModelVersion
    deploymentName: aoaiDeploymentName
    modelCapacity: aoaiModelCapacity
    existingAoaiResourceId: existingAoaiResourceId
  }
}

module appservice 'modules/appservice.bicep' = {
  name: 'appservice'
  params: {
    location: location
    resourceToken: resourceToken
    tags: tags
    appServicePlanSku: appServicePlanSku
    acrLoginServer: acr.outputs.loginServer
    containerImageName: containerImageName
    appInsightsConnectionString: monitoring.outputs.appInsightsConnectionString
    aoaiEndpoint: aoai.outputs.endpoint
    aoaiDeploymentName: aoai.outputs.deploymentName
    entraAppId: entraAppId
    entraClientSecret: entraClientSecret
    entraTenantId: entraTenantId
    publicSiteHost: customDomainName
  }
}

module dns 'modules/dns.bicep' = if (!empty(customDomainName)) {
  name: 'dns'
  params: {
    domainName: customDomainName
    tags: tags
    appServiceInboundIp: appServiceInboundIp
    appServiceDefaultHostname: appservice.outputs.hostname
    customDomainVerificationId: appservice.outputs.customDomainVerificationId
    dmarcReportEmail: dmarcReportEmail
    enableDeleteLock: enableDeleteLocks
  }
}

module availability 'modules/availability.bicep' = {
  name: 'availability'
  params: {
    location: location
    tags: tags
    appInsightsId: monitoring.outputs.appInsightsId
    testUrl: '${publicUrl}/api/version'
  }
}

module roles 'modules/roles.bicep' = {
  name: 'roles'
  params: {
    webAppPrincipalId: appservice.outputs.principalId
    acrName: acr.outputs.name
    aoaiName: aoai.outputs.accountName
    aoaiResourceGroup: aoai.outputs.resourceGroup
    aoaiSubscriptionId: aoai.outputs.subscriptionId
  }
}

output acrName string = acr.outputs.name
output acrLoginServer string = acr.outputs.loginServer
output containerImageName string = containerImageName
output webAppName string = appservice.outputs.name
output webAppHostname string = appservice.outputs.hostname
output webAppUrl string = 'https://${appservice.outputs.hostname}'
output webAppPrincipalId string = appservice.outputs.principalId
output aoaiEndpoint string = aoai.outputs.endpoint
output aoaiDeploymentName string = aoai.outputs.deploymentName
output aiProjectName string = aoai.outputs.projectName
output appInsightsConnectionString string = monitoring.outputs.appInsightsConnectionString
output logAnalyticsWorkspaceId string = monitoring.outputs.logAnalyticsWorkspaceId
output customDomainName string = customDomainName
// Empty unless a custom domain was requested. Assign these four at the
// registrar to delegate the domain to Azure DNS.
output dnsNameServers array = empty(customDomainName) ? [] : dns!.outputs.nameServers
