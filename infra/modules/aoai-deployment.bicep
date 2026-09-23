// Model deployment on an existing, reused AI Services account. Deployed into the
// account's own resource group so a shared account can receive the app's model
// without being re-created or reconfigured.
param accountName string
param modelName string
param modelVersion string
param deploymentName string
param modelCapacity int
@allowed(['Default', 'Priority'])
param serviceTier string

resource account 'Microsoft.CognitiveServices/accounts@2026-03-01' existing = {
  name: accountName
}

resource modelDeployment 'Microsoft.CognitiveServices/accounts/deployments@2026-03-01' = {
  parent: account
  name: deploymentName
  sku: {
    name: 'GlobalStandard'
    capacity: modelCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: modelName
      version: modelVersion
    }
    raiPolicyName: 'Microsoft.DefaultV2'
    serviceTier: serviceTier
  }
}

output deploymentName string = modelDeployment.name
