// Azure AI Foundry (Cognitive Services, kind=AIServices) account + model deployment.
// Conditionally created — when `existingAoaiResourceId` is provided, the module
// skips creation and reads the endpoint/name from the existing account so the
// Web App MI gets the role grant on whichever account is targeted.
param aoaiLocation string
param resourceToken string
param tags object
param modelName string
param modelVersion string
param deploymentName string
param modelCapacity int
param existingAoaiResourceId string

var useExisting = !empty(existingAoaiResourceId)

// Parse `/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.CognitiveServices/accounts/{name}`
// into segments so we can `existing` reference the account in its real RG/sub.
var existingSegments = split(existingAoaiResourceId, '/')
var existingSubId = useExisting ? existingSegments[2] : subscription().subscriptionId
var existingRg = useExisting ? existingSegments[4] : resourceGroup().name
var existingName = useExisting ? existingSegments[8] : ''

resource newAccount 'Microsoft.CognitiveServices/accounts@2026-03-01' = if (!useExisting) {
  name: 'aoai-finops-${resourceToken}'
  location: aoaiLocation
  tags: tags
  kind: 'AIServices'
  sku: {
    name: 'S0'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    // Foundry account (enables projects/agents/evals later). Key auth OFF —
    // access is managed-identity / Entra token only (no API keys).
    allowProjectManagement: true
    customSubDomainName: 'aoai-finops-${resourceToken}'
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: true
  }
}

resource modelDeployment 'Microsoft.CognitiveServices/accounts/deployments@2026-03-01' = if (!useExisting) {
  parent: newAccount
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
  }
}

resource existingAccount 'Microsoft.CognitiveServices/accounts@2026-03-01' existing = if (useExisting) {
  name: existingName
  scope: resourceGroup(existingSubId, existingRg)
}

output endpoint string = useExisting ? existingAccount!.properties.endpoint : newAccount!.properties.endpoint
output accountName string = useExisting ? existingName : newAccount!.name
output deploymentName string = deploymentName
output resourceGroup string = useExisting ? existingRg : resourceGroup().name
output subscriptionId string = useExisting ? existingSubId : subscription().subscriptionId
