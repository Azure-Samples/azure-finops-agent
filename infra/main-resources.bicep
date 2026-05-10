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

var containerImageName = 'finops-agent:latest'

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
output appInsightsConnectionString string = monitoring.outputs.appInsightsConnectionString
output logAnalyticsWorkspaceId string = monitoring.outputs.logAnalyticsWorkspaceId
