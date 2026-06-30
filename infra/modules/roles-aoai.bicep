// Nested module so the role assignment is created in the Foundry account's own
// resource group / subscription (which may differ when reusing an existing
// account via `existingAoaiResourceId`).
param aoaiName string
param webAppPrincipalId string
param cognitiveServicesOpenAIUserRoleId string

resource aoai 'Microsoft.CognitiveServices/accounts@2026-03-01' existing = {
  name: aoaiName
}

resource aoaiOpenAIUserAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: aoai
  name: guid(aoai.id, webAppPrincipalId, cognitiveServicesOpenAIUserRoleId)
  properties: {
    principalId: webAppPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesOpenAIUserRoleId)
  }
}
