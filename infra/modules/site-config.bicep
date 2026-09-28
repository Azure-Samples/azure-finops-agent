// Site configuration shared by the web app and its optional preview slot, so a
// slot starts from the production configuration. Feature deployments later
// replace only the slot's image and evaluated model settings.

@export()
func buildSiteConfig(
  acrLoginServer string,
  containerImageName string,
  appInsightsConnectionString string,
  aoaiEndpoint string,
  aoaiDeploymentName string,
  aoaiReasoningEffort string,
  entraAppId string,
  entraClientSecret string,
  entraTenantId string,
  publicSiteHost string
) object => {
  linuxFxVersion: 'DOCKER|${acrLoginServer}/${containerImageName}'
  acrUseManagedIdentityCreds: true
  // Keep the container resident so the first request after idle doesn't pay a
  // container cold start. Always On is supported on Basic and above (all SKUs
  // allowed by main.bicep).
  alwaysOn: true
  http20Enabled: true
  ftpsState: 'Disabled'
  minTlsVersion: '1.2'
  healthCheckPath: '/api/version'
  appSettings: [
    // Tells App Service which port the container listens on (matches Dockerfile EXPOSE 8080).
    { name: 'WEBSITES_PORT', value: '8080' }
    // CRITICAL: mount the persistent /home Azure Files share into the container.
    // Chat history, Data Protection keys, and persisted identities (refresh
    // tokens) all live under /home — with this 'false' every restart/deploy
    // wiped them: users were silently logged out (cookie decrypt failed with
    // "key not found in the key ring") and all conversations disappeared.
    { name: 'WEBSITES_ENABLE_APP_SERVICE_STORAGE', value: 'true' }
    // Root for sessions, identities, jobs, uploads and artifacts on the
    // persistent mount. The name predates the Agent Framework runtime.
    { name: 'COPILOT_HOME', value: '/home/copilot' }
    // The image carries .NET plus a Python data stack; the first cold start
    // after a pull can exceed the 230s default. Allow up to 30 min to warm up.
    { name: 'WEBSITES_CONTAINER_START_TIME_LIMIT', value: '1800' }
    { name: 'DOCKER_REGISTRY_SERVER_URL', value: 'https://${acrLoginServer}' }
    // Foundry project endpoint and model deployment (Program.cs fail-fast key).
    // Inference authenticates with the running site's managed identity.
    { name: 'AzureOpenAI__Endpoint', value: aoaiEndpoint }
    { name: 'AzureOpenAI__DeploymentName', value: aoaiDeploymentName }
    // Reasoning effort for reasoning-capable models. CI deployments replace
    // it with the effort the live evaluation gate tested.
    { name: 'AzureOpenAI__ReasoningEffort', value: aoaiReasoningEffort }
    // Entra ID OAuth (multi-tenant). Empty values disable OAuth gracefully.
    { name: 'Microsoft__ClientId', value: entraAppId }
    { name: 'Microsoft__ClientSecret', value: entraClientSecret }
    { name: 'Microsoft__TenantId', value: entraTenantId }
    // Application Insights — Program.cs reads ApplicationInsights__ConnectionString;
    // APPLICATIONINSIGHTS_CONNECTION_STRING is the standard name that Azure
    // Monitor tooling and App Service integration recognize.
    { name: 'ApplicationInsights__ConnectionString', value: appInsightsConnectionString }
    { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
    { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
    { name: 'PUBLIC_SITE_HOST', value: publicSiteHost }
  ]
}
