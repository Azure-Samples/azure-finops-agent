// Optional GitHub Actions identity for feature-branch previews. It can push
// images and update only the preview slot; the production site keeps its own
// main-branch identity. No secret exists: GitHub presents an OIDC token that
// must match one of the federated subjects below.

param location string
param tags object
param name string

@minLength(1)
@description('GitHub OIDC subjects allowed to sign in, for example repo:OWNER/REPO:environment:test.')
param subjects string[]

param webAppName string
param slotName string
param acrName string

// Built-in role definition IDs (constant across all Azure subscriptions).
var websiteContributorRoleId = 'de139f84-1756-47ae-9be6-808fbbe84772'
var readerRoleId = 'acdd72a7-3385-48ef-bd42-f606fba81ae7'
var acrPushRoleId = '8311e382-0749-4cb8-b61a-304f252e45ec'

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: name
  location: location
  tags: tags
}

// Azure rejects concurrent federated-credential writes on one identity.
@batchSize(1)
resource credentials 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = [for subject in subjects: {
  parent: identity
  name: 'github-${uniqueString(subject)}'
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: subject
    audiences: ['api://AzureADTokenExchange']
  }
}]

resource webApp 'Microsoft.Web/sites@2024-04-01' existing = {
  name: webAppName
}

resource slot 'Microsoft.Web/sites/slots@2024-04-01' existing = {
  parent: webApp
  name: slotName
}

resource acr 'Microsoft.ContainerRegistry/registries@2024-11-01-preview' existing = {
  name: acrName
}

// Writes are confined to the slot. Reading the parent lets the workflow prove
// the slot hostname differs from production before it changes anything.
resource slotContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: slot
  name: guid(slot.id, identity.id, websiteContributorRoleId)
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', websiteContributorRoleId)
  }
}

resource siteReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: webApp
  name: guid(webApp.id, identity.id, readerRoleId)
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', readerRoleId)
  }
}

// Feature builds push only test-* and buildcache tags; production deploys by commit SHA.
resource registryPush 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: acr
  name: guid(acr.id, identity.id, acrPushRoleId)
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPushRoleId)
  }
}

output clientId string = identity.properties.clientId
