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
param aoaiServiceTier string
param aoaiReasoningEffort string
param existingAoaiResourceId string
param deployModelOnExistingAccount bool
param existingAoaiProjectName string = ''
param entraAppId string
@secure()
param entraClientSecret string
param entraTenantId string
param customDomainName string
param dmarcReportEmail string
param enableDeleteLocks bool
param appServiceInboundIp string
param previewSlotName string = ''
param previewDeploySubjects string[] = []

var containerImageName = 'finops-agent:latest'
// Prefer the custom domain for the synthetic probe when there is one — that is
// the hostname real users hit, and probing it also exercises DNS and the
// custom-domain certificate, which the *.azurewebsites.net host would not.
var publicUrl = empty(customDomainName) ? 'https://${appservice.outputs.hostname}' : 'https://${customDomainName}'

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
    serviceTier: aoaiServiceTier
    existingAoaiResourceId: existingAoaiResourceId
    deployModelOnExistingAccount: deployModelOnExistingAccount
    existingProjectName: existingAoaiProjectName
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
    aoaiEndpoint: aoai.outputs.projectEndpoint
    aoaiDeploymentName: aoai.outputs.deploymentName
    aoaiReasoningEffort: aoaiReasoningEffort
    entraAppId: entraAppId
    entraClientSecret: entraClientSecret
    entraTenantId: entraTenantId
    publicSiteHost: customDomainName
  }
}

// Optional preview slot on the same web app and plan, plus a GitHub deploy
// identity that can change only that slot.
module preview 'modules/preview.bicep' = if (!empty(previewSlotName)) {
  name: 'preview'
  params: {
    location: location
    tags: tags
    webAppName: appservice.outputs.name
    slotName: previewSlotName
    acrLoginServer: acr.outputs.loginServer
    containerImageName: containerImageName
    appInsightsConnectionString: monitoring.outputs.appInsightsConnectionString
    aoaiEndpoint: aoai.outputs.projectEndpoint
    aoaiDeploymentName: aoai.outputs.deploymentName
    aoaiReasoningEffort: aoaiReasoningEffort
    entraAppId: entraAppId
    entraClientSecret: entraClientSecret
    entraTenantId: entraTenantId
    publicSiteHost: customDomainName
    acrName: acr.outputs.name
    aoaiName: aoai.outputs.accountName
    aoaiResourceGroup: aoai.outputs.resourceGroup
    aoaiSubscriptionId: aoai.outputs.subscriptionId
    deploySubjects: previewDeploySubjects
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
    // modules/preview.bicep grants the preview slot's own identity.
    sitePrincipalIds: [appservice.outputs.principalId]
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
output webAppSlotName string = previewSlotName
output webAppSlotHostname string = empty(previewSlotName) ? '' : preview!.outputs.slotHostname
output webAppSlotPrincipalId string = empty(previewSlotName) ? '' : preview!.outputs.slotPrincipalId
output previewDeployClientId string = empty(previewSlotName) ? '' : preview!.outputs.deployClientId
output aoaiEndpoint string = aoai.outputs.projectEndpoint
output aoaiDeploymentName string = aoai.outputs.deploymentName
output aiProjectName string = aoai.outputs.projectName
output appInsightsConnectionString string = monitoring.outputs.appInsightsConnectionString
output logAnalyticsWorkspaceId string = monitoring.outputs.logAnalyticsWorkspaceId
output customDomainName string = customDomainName
// Empty unless a custom domain was requested. Assign these four at the
// registrar to delegate the domain to Azure DNS.
output dnsNameServers array = empty(customDomainName) ? [] : dns!.outputs.nameServers
