// Deploy incrementally and serialize slot-settings writers: the read/merge/write is not atomic.
targetScope = 'resourceGroup'

@minLength(1)
@description('Name of the existing App Service in the deployment resource group.')
param webAppName string

@minLength(1)
@description('Name of the existing nonproduction slot. The caller must reject production (case-insensitive), whitespace-only names, and unapproved targets before deployment.')
param slotName string

@secure()
@minLength(1)
@description('Azure OpenAI inference endpoint for the existing feature slot.')
param endpoint string

@minLength(1)
@description('Name of an already-provisioned model deployment accessible to the slot identity.')
param deploymentName string = 'gpt-6-luna'

@minLength(1)
@description('Reasoning effort supported by the selected model deployment.')
param reasoningEffort string = 'xhigh'

resource webApp 'Microsoft.Web/sites@2024-04-01' existing = {
  name: webAppName
}

resource slot 'Microsoft.Web/sites/slots@2024-04-01' existing = {
  parent: webApp
  name: slotName
}

resource settings 'Microsoft.Web/sites/slots/config@2024-04-01' = {
  parent: slot
  name: 'appsettings'
  properties: union(list('${slot.id}/config/appsettings', '2024-04-01').properties, {
    AzureOpenAI__Endpoint: endpoint
    AzureOpenAI__DeploymentName: deploymentName
    AzureOpenAI__ReasoningEffort: reasoningEffort
  })
}

@description('HTTPS URL from the existing slot hostname returned by Azure.')
output slotUrl string = 'https://${slot.properties.defaultHostName}'
