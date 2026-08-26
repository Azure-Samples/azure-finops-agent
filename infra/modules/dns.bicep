// Public DNS zone for a custom domain, plus the records App Service needs.
//
// Deliberately does NOT create the hostname bindings or managed certificates.
// Those are strictly ordered — the binding must exist unsecured, then the
// managed cert can only be issued once public DNS already resolves to the app,
// then the binding is updated with the cert thumbprint. That ordering spans
// DNS propagation (minutes to hours), which a single ARM deployment cannot wait
// on. See .github/prompts/migrate-tenant.prompt.md for the two CLI commands.
//
// Creating the zone here still gets the nameservers assigned and every record
// in place declaratively, which is the part that matters for reproducibility.

param domainName string
param tags object

@description('Comma-separated App Service inbound VIPs for the apex A record. Not readable from the site resource at deploy time (the property is absent from SiteProperties), so they are passed in explicitly. Leave empty to create the zone without an apex A record and add it afterwards with `az network dns record-set a add-record`.')
param appServiceInboundIp string = ''

@description('Default *.azurewebsites.net hostname, used as the www CNAME target.')
param appServiceDefaultHostname string

@description('The web app customDomainVerificationId, published as the asuid TXT record so App Service will accept the hostname binding.')
param customDomainVerificationId string

@description('Mailbox for DMARC aggregate reports. Empty publishes the policy without a rua tag.')
param dmarcReportEmail string = ''

@description('Protect the zone with a CanNotDelete lock. Leave false for evaluation deployments so `azd down` can tear the environment down cleanly; set true for a long-lived production domain.')
param enableDeleteLock bool = false

resource zone 'Microsoft.Network/dnsZones@2023-07-01-preview' = {
  name: domainName
  location: 'global'
  tags: tags
  properties: {
    zoneType: 'Public'
  }
}

resource apex 'Microsoft.Network/dnsZones/A@2023-07-01-preview' = if (!empty(appServiceInboundIp)) {
  parent: zone
  name: '@'
  properties: {
    TTL: 3600
    ARecords: map(split(appServiceInboundIp, ','), ip => {
      ipv4Address: trim(ip)
    })
  }
}

resource www 'Microsoft.Network/dnsZones/CNAME@2023-07-01-preview' = {
  parent: zone
  name: 'www'
  properties: {
    TTL: 3600
    CNAMERecord: {
      cname: appServiceDefaultHostname
    }
  }
}

// Proves domain ownership to App Service for the apex binding. The www binding
// verifies via its CNAME instead, so it needs no asuid record — unless www is
// ever repointed at Front Door/CDN, at which case add asuid.www too.
resource asuid 'Microsoft.Network/dnsZones/TXT@2023-07-01-preview' = {
  parent: zone
  name: 'asuid'
  properties: {
    TTL: 3600
    TXTRecords: [
      { value: [customDomainVerificationId] }
    ]
  }
}

// Without a CAA record ANY public CA may issue for this domain. App Service
// managed certificates chain to DigiCert, which owns the geotrust.com,
// digicert.com and digicert.ne.jp issuer domains — all three must be allowed
// or renewal fails. `iodef` routes violation reports back to the owner.
resource caa 'Microsoft.Network/dnsZones/CAA@2023-07-01-preview' = {
  parent: zone
  name: '@'
  properties: {
    TTL: 3600
    caaRecords: concat(
      [
        { flags: 0, tag: 'issue', value: 'digicert.com' }
        { flags: 0, tag: 'issue', value: 'geotrust.com' }
        { flags: 0, tag: 'issue', value: 'digicert.ne.jp' }
      ],
      empty(dmarcReportEmail) ? [] : [{ flags: 0, tag: 'iodef', value: 'mailto:${dmarcReportEmail}' }]
    )
  }
}

// This domain sends no mail, so lock it down against spoofing: a null MX
// (RFC 7505) declares it accepts none, SPF hard-fails every sender, and DMARC
// tells receivers to reject anything that slips past both.
resource nullMx 'Microsoft.Network/dnsZones/MX@2023-07-01-preview' = {
  parent: zone
  name: '@'
  properties: {
    TTL: 3600
    MXRecords: [
      { preference: 0, exchange: '.' }
    ]
  }
}

resource spf 'Microsoft.Network/dnsZones/TXT@2023-07-01-preview' = {
  parent: zone
  name: '@'
  properties: {
    TTL: 3600
    TXTRecords: [
      { value: ['v=spf1 -all'] }
    ]
  }
}

resource dmarc 'Microsoft.Network/dnsZones/TXT@2023-07-01-preview' = {
  parent: zone
  name: '_dmarc'
  properties: {
    TTL: 3600
    TXTRecords: [
      {
        value: [
          empty(dmarcReportEmail)
            ? 'v=DMARC1; p=reject; sp=reject; adkim=s; aspf=s'
            : 'v=DMARC1; p=reject; sp=reject; adkim=s; aspf=s; rua=mailto:${dmarcReportEmail}'
        ]
      }
    ]
  }
}

// The zone is the single point of failure for the whole domain: delete it and
// the nameservers are reassigned on recreation, forcing a registrar change and
// a fresh propagation window. Nothing about normal operation requires deleting
// it, so make that impossible by accident. Opt-in, because a lock also blocks
// `azd down`.
resource zoneLock 'Microsoft.Authorization/locks@2020-05-01' = if (enableDeleteLock) {
  scope: zone
  name: 'do-not-delete-dns-zone'
  properties: {
    level: 'CanNotDelete'
    notes: 'Deleting this zone reassigns the nameservers and takes the custom domain offline until the registrar is updated and DNS re-propagates.'
  }
}

@description('Assign these at the registrar to delegate the domain to Azure DNS.')
output nameServers array = zone.properties.nameServers
output zoneName string = zone.name
