// Nested module so the role assignments are created in the Foundry account's own
// resource group / subscription (which may differ when reusing an existing
// account via `existingAoaiResourceId`).
param aoaiName string
@minLength(1)
param sitePrincipalIds string[]
param foundryUserRoleId string

resource aoai 'Microsoft.CognitiveServices/accounts@2026-03-01' existing = {
  name: aoaiName
}

resource aoaiFoundryUserAssignments 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in sitePrincipalIds: {
  scope: aoai
  name: guid(aoai.id, principalId, foundryUserRoleId)
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', foundryUserRoleId)
  }
}]
