// Nested module so the role assignment is created in the AOAI account's own
// resource group / subscription (which may differ when reusing an existing
// AOAI account via `existingAoaiResourceId`).
param aoaiName string
param webAppPrincipalId string
param cognitiveServicesUserRoleId string

resource aoai 'Microsoft.CognitiveServices/accounts@2025-04-01-preview' existing = {
  name: aoaiName
}

resource aoaiUserAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: aoai
  name: guid(aoai.id, webAppPrincipalId, cognitiveServicesUserRoleId)
  properties: {
    principalId: webAppPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesUserRoleId)
  }
}
