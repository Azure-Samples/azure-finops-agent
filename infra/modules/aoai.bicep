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
@description('Deployment service tier. "Default" is standard processing. "Priority" enables priority processing at a premium, but only for models that support it; gpt-6.1-terra 2026-10-26 is not listed as supporting it, gpt-6-sol 2026-09-22 is on GlobalStandard.')
@allowed(['Default', 'Priority'])
param serviceTier string = 'Default'
param existingAoaiResourceId string
@description('When reusing an existing account, also create or update the model deployment on it. Requires deployment permission and available model-specific quota on that account.')
param deployModelOnExistingAccount bool = false

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
    raiPolicyName: 'Microsoft.DefaultV2'
    // Priority processing (the Foundry portal's "Priority processing" toggle):
    // requests are served with faster time-to-first-token at a price premium.
    serviceTier: serviceTier
  }
}

resource existingAccount 'Microsoft.CognitiveServices/accounts@2026-03-01' existing = if (useExisting) {
  name: existingName
  scope: resourceGroup(existingSubId, existingRg)
}

module existingModelDeployment 'aoai-deployment.bicep' = if (useExisting && deployModelOnExistingAccount) {
  name: 'aoai-model-${uniqueString(existingAoaiResourceId, deploymentName)}'
  scope: resourceGroup(existingSubId, existingRg)
  params: {
    accountName: existingName
    modelName: modelName
    modelVersion: modelVersion
    deploymentName: deploymentName
    modelCapacity: modelCapacity
    serviceTier: serviceTier
  }
}

var accountProperties = useExisting ? existingAccount!.properties : newAccount!.properties
var serviceEndpoints object = accountProperties.?endpoints ?? {}
var accountKind = useExisting ? existingAccount!.kind : 'AIServices'

output accountName string = useExisting ? existingName : newAccount!.name
output deploymentName string = useExisting && deployModelOnExistingAccount
  ? existingModelDeployment!.outputs.deploymentName
  : deploymentName
output resourceGroup string = useExisting ? existingRg : resourceGroup().name
output subscriptionId string = useExisting ? existingSubId : subscription().subscriptionId
// The app calls the account's own Responses API (https://{host}/openai/v1), so no Foundry project is needed: a Foundry
// (AIServices) account serves it on its services.ai.azure.com host, a legacy Azure OpenAI account on its OpenAI route.
output foundryEndpoint string = accountKind == 'AIServices'
  ? 'https://${accountProperties.customSubDomainName}.services.ai.azure.com'
  : (serviceEndpoints[?'OpenAI Language Model Instance API'] ?? accountProperties.endpoint)
