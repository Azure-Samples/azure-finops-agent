// Outside-in availability monitoring for the public endpoint.
//
// Closes a real blind spot: every other alert in this stack is a scheduled KQL
// query over App Insights `requests`, which only fires when the app is already
// receiving and logging traffic. If DNS breaks, the TLS cert lapses, or the
// site is hard-down, ZERO requests arrive and those rules can never fire — the
// dashboard just goes quiet and looks healthy. A synthetic test runs from
// outside Azure and fails loudly in exactly those cases.

param location string
param tags object

@description('Resource ID of the Application Insights component that owns this test.')
param appInsightsId string

@description('Fully-qualified URL to probe, e.g. https://azure-finops-agent.com/api/version')
param testUrl string

@description('Optional resource IDs of action groups to notify. The alert is always created; an empty list means it has no notification destination yet.')
param actionGroupIds array = []

@description('Mailbox notified when the public endpoint is down or its certificate is close to expiry. Empty creates no action group, so the alert fires with nobody told. Set it for any long-lived deployment.')
param alertEmail string = ''

var notifyGroupIds = empty(alertEmail) ? actionGroupIds : concat(actionGroupIds, [availabilityActionGroup.id])

// Keyed on the component only — NOT on testUrl. A web test's name is its identity,
// so hashing a mutable property meant changing the probe URL (e.g. once the custom
// domain was set) provisioned a *second* test and orphaned the first: still running,
// still billed, and no longer attached to the alert, which only ever tracks the
// current one. Keeping the name stable makes a URL change update this test in place.
var testName = 'availability-${uniqueString(appInsightsId)}'

// Five geographically spread locations. Azure only counts a test as failed when
// multiple locations agree, so fewer than five makes false positives likely.
var testLocations = [
  { Id: 'emea-nl-ams-azr' }
  { Id: 'us-va-ash-azr' }
  { Id: 'us-ca-sjc-azr' }
  { Id: 'emea-ru-msa-edge' }
  { Id: 'apac-sg-sin-azr' }
]

resource webTest 'Microsoft.Insights/webtests@2022-06-15' = {
  name: testName
  location: location
  // App Insights only surfaces a web test in its Availability blade when this
  // hidden-link tag points back at the component. Without it the test runs but
  // is effectively invisible in the portal.
  tags: union(tags, {
    'hidden-link:${appInsightsId}': 'Resource'
  })
  kind: 'standard'
  properties: {
    SyntheticMonitorId: testName
    Name: 'FinOps Agent — public endpoint'
    Description: 'Outside-in probe of the public URL. Also fails when the TLS certificate is within 7 days of expiry.'
    Enabled: true
    Frequency: 300
    Timeout: 30
    Kind: 'standard'
    RetryEnabled: true
    Locations: testLocations
    Request: {
      RequestUrl: testUrl
      HttpVerb: 'GET'
      ParseDependentRequests: false
    }
    ValidationRules: {
      ExpectedHttpStatusCode: 200
      SSLCheck: true
      // Turns the synthetic test into the certificate-expiry alarm too. App
      // Service managed certs auto-renew ~45 days out, but renewal fails
      // SILENTLY if DNS ever stops resolving to the app — this catches that
      // while there is still a week to react.
      SSLCertRemainingLifetimeCheck: 7
    }
  }
}

resource availabilityActionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = if (!empty(alertEmail)) {
  name: 'FinOps-PublicEndpoint-Notify'
  location: 'global'
  tags: tags
  properties: {
    groupShortName: 'FinOpsDown'
    enabled: true
    emailReceivers: [
      {
        name: 'owner'
        emailAddress: alertEmail
        useCommonAlertSchema: true
      }
    ]
  }
}

resource availabilityAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: 'FinOps-PublicEndpoint-Down'
  location: 'global'
  tags: tags
  properties: {
    description: 'The public endpoint failed from multiple locations, or its TLS certificate is close to expiry.'
    severity: 1
    enabled: true
    scopes: [
      webTest.id
      appInsightsId
    ]
    evaluationFrequency: 'PT1M'
    windowSize: 'PT5M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.WebtestLocationAvailabilityCriteria'
      webTestId: webTest.id
      componentId: appInsightsId
      failedLocationCount: 2
    }
    actions: [
      for agId in notifyGroupIds: {
        actionGroupId: agId
      }
    ]
  }
}

output webTestId string = webTest.id
output webTestName string = testName
