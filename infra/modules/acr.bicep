param location string
param resourceToken string
param tags object

// Basic SKU, admin disabled. The Web App pulls images via its system-assigned
// managed identity (AcrPull role assignment in roles.bicep).
resource registry 'Microsoft.ContainerRegistry/registries@2024-11-01-preview' = {
  name: 'crfinops${resourceToken}'
  location: location
  tags: tags
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
    anonymousPullEnabled: false
    publicNetworkAccess: 'Enabled'
  }
}

output name string = registry.name
output loginServer string = registry.properties.loginServer
output id string = registry.id
