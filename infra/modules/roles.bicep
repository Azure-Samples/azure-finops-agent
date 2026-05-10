// Role assignments for the Web App's system-assigned managed identity:
// - AcrPull on the ACR (so the Web App can pull container images)
// - Cognitive Services User on the Azure OpenAI account (BYOK token via DefaultAzureCredential)
//
// The AOAI assignment is scoped to either a freshly-created account in this RG
// or an existing account in another RG/subscription.

param webAppPrincipalId string
param acrName string
param aoaiName string
param aoaiResourceGroup string
param aoaiSubscriptionId string

// Built-in role definition IDs (constant across all Azure subscriptions).
var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'
var cognitiveServicesUserRoleId = 'a97b65f3-24c7-4388-baec-2e87135dc908'

resource acr 'Microsoft.ContainerRegistry/registries@2024-11-01-preview' existing = {
  name: acrName
}

resource acrPullAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: acr
  name: guid(acr.id, webAppPrincipalId, acrPullRoleId)
  properties: {
    principalId: webAppPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
  }
}

// Cognitive Services User role on AOAI — applied via a nested module because
// the AOAI account may live in a different RG/subscription when reused.
module aoaiRole 'roles-aoai.bicep' = {
  name: 'aoai-role'
  scope: resourceGroup(aoaiSubscriptionId, aoaiResourceGroup)
  params: {
    aoaiName: aoaiName
    webAppPrincipalId: webAppPrincipalId
    cognitiveServicesUserRoleId: cognitiveServicesUserRoleId
  }
}
