// Optional preview slot on an existing web app, on the same App Service plan:
// the slot, its own identity's registry and Foundry grants, and an optional
// GitHub deploy identity that can change only this slot. Kept apart from
// appservice.bicep so an existing production site can gain a slot without the
// site itself being redeployed.
import { buildSiteConfig } from 'site-config.bicep'

param location string
param tags object
param webAppName string
@description('Slot name, for example `test`. Must not be `production`.')
param slotName string
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
param entraClientSecret string = ''
param entraTenantId string
param publicSiteHost string = ''
param acrName string
param aoaiName string
param aoaiResourceGroup string = resourceGroup().name
param aoaiSubscriptionId string = subscription().subscriptionId

@description('GitHub OIDC subjects for a deploy identity limited to this slot, for example repo:OWNER/REPO:environment:test. Empty creates none.')
param deploySubjects string[] = []
param deployIdentityName string = take('id-${webAppName}-${slotName}-deploy', 128)

resource webApp 'Microsoft.Web/sites@2024-04-01' existing = {
  name: webAppName
}

// Not tagged `azd-service-name`, so `azd deploy` still targets only the production site.
resource slot 'Microsoft.Web/sites/slots@2024-04-01' = {
  parent: webApp
  name: slotName
  location: location
  tags: tags
  kind: 'app,linux,container'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: webApp.properties.serverFarmId
    httpsOnly: true
    publicNetworkAccess: 'Enabled'
    siteConfig: buildSiteConfig(acrLoginServer, containerImageName, appInsightsConnectionString, aoaiEndpoint,
      aoaiDeploymentName, aoaiReasoningEffort, entraAppId, entraClientSecret, entraTenantId, publicSiteHost)
  }
}

// A slot's identity is distinct from the web app's; without its own grants the
// slot cannot pull images and every chat fails with a Foundry agents/write 403.
module slotRoles 'roles.bicep' = {
  name: 'preview-roles'
  params: {
    sitePrincipalIds: [slot.identity.principalId]
    acrName: acrName
    aoaiName: aoaiName
    aoaiResourceGroup: aoaiResourceGroup
    aoaiSubscriptionId: aoaiSubscriptionId
    aoaiRoleDeploymentName: 'preview-aoai-role'
  }
}

module deployer 'preview-deployer.bicep' = if (!empty(deploySubjects)) {
  name: 'preview-deployer'
  params: {
    location: location
    tags: tags
    name: deployIdentityName
    subjects: deploySubjects
    webAppName: webApp.name
    slotName: slot.name
    acrName: acrName
  }
}

output slotHostname string = slot.properties.defaultHostName
output slotPrincipalId string = slot.identity.principalId
output deployClientId string = empty(deploySubjects) ? '' : deployer!.outputs.clientId
