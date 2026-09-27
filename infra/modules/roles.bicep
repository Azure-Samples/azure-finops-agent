// Role assignments for the Web App's system-assigned managed identity:
// - AcrPull on the ACR (so the Web App can pull container images)
// - Foundry User (formerly Azure AI User) on the Foundry (AIServices) account — the
//   project data-plane access the app needs to call the project Responses endpoint
//   via managed-identity token (no API keys)
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
// Foundry User — project data actions, including Responses through the project endpoint.
// Cognitive Services OpenAI User covers only the account's OpenAI endpoints.
var foundryUserRoleId = '53ca6127-db72-4b80-b1b0-d745d6d5456d'

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

// Foundry User role on the Foundry account — applied via a nested
// module because the account may live in a different RG/subscription when reused.
module aoaiRole 'roles-aoai.bicep' = {
  name: 'aoai-role'
  scope: resourceGroup(aoaiSubscriptionId, aoaiResourceGroup)
  params: {
    aoaiName: aoaiName
    webAppPrincipalId: webAppPrincipalId
    foundryUserRoleId: foundryUserRoleId
  }
}
