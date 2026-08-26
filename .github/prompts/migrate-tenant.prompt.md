---
agent: agent
description: "Migrate the Azure FinOps Agent stack and its custom domain to a different Azure tenant"
---

# Migrate to another tenant

Rebuild the whole stack in a new tenant/subscription and cut the custom domain over to it,
with no window where the public URL is broken.

> **Read this first.** Azure **cannot move resources across tenants.** `az resource move` is
> same-tenant only, and the DNS zone, ACR, App Insights history and the Entra app registration
> are all tenant-bound. Subscription transfer is unavailable for MCAP sandbox subscriptions.
> So this is a **rebuild-and-cutover**, not a move. Everything below assumes that.

Fill these in before starting — never commit real values (see `.github/copilot-instructions.md`):

| | Source (old) | Target (new) |
|---|---|---|
| Tenant | `<SRC_TENANT_ID>` | `<DST_TENANT_ID>` |
| Subscription | `<SRC_SUB_ID>` | `<DST_SUB_ID>` |
| Resource group | `rg-finops-agent` | `rg-<AZURE_ENV_NAME>` (created by azd) |
| Web app | `finops-agent-container` | created by azd |
| ACR | `crfinopsagent` | created by azd |

## What does NOT come across — decide before you start

| Thing | Reality |
|---|---|
| **Entra app registration** | Cannot move tenants. A **new ClientId** is created, so **every existing user must re-consent**, and all persisted refresh tokens become invalid. Unavoidable. |
| **Chat history / jobs / identities** | Live on the old app's `/home` Azure Files share. Encrypted with Data Protection keys that also live there, and bound to the old ClientId — so copying them across still logs everyone out. **Recommended: start fresh.** |
| **App Insights telemetry** | History stays in the old workspace. Keep the old RG read-only for a while if you need to refer back. |
| **Managed TLS certificates** | Cannot move. New ones are issued free on the new app, but only *after* DNS resolves to it. |
| **The domain registration itself** | Stays at Namecheap and is unaffected — only the DNS *hosting* and records move. |

---

## 1. Prerequisites on the target

```pwsh
az login --tenant <DST_TENANT_ID>
az account set --subscription <DST_SUB_ID>
az account show --query "{sub:name, tenant:tenantId, user:user.name}" -o json
```

The target subscription needs two resource providers that are **not** registered by default there:

```pwsh
az provider register --namespace Microsoft.Web --wait
az provider register --namespace Microsoft.Network --wait
az provider list --query "[?namespace=='Microsoft.Web' || namespace=='Microsoft.Network'].{ns:namespace,state:registrationState}" -o table
```

## 2. Provision the new stack

`azd up` creates everything: RG, Log Analytics + App Insights, ACR, AI Foundry account + model
deployment, App Service plan + web app with managed identity, role assignments, the Entra app
registration, **and now** the DNS zone, an outside-in availability test, and the CAA/SPF/DMARC
records.

Set the custom domain so the zone is created and the app knows its canonical hostname:

```pwsh
azd env new <AZURE_ENV_NAME>
azd env set AZURE_CUSTOM_DOMAIN     "azure-finops-agent.com"
azd env set DMARC_REPORT_EMAIL      "<ops-alias@yourdomain>"
azd env set ENABLE_DELETE_LOCKS     "true"     # long-lived domain — blocks accidental deletes
azd env set APP_SERVICE_PLAN_SKU    "P0V3"     # match production
azd up
```

`azd up` prints `AZURE_DNS_NAME_SERVERS`. **Do not touch the registrar yet** — the whole point of
the sequencing below is that the old site keeps serving until the new one is proven.

## 3. Verify the new stack on its own hostname

```pwsh
$new = azd env get-value WEB_APP_HOSTNAME
Invoke-RestMethod "https://$new/api/version"
```

Exercise it properly before going near DNS: sign in with Entra, run a real chat turn, confirm a
tool call executes and a chart renders. A broken new stack discovered *after* cutover is the one
outcome this runbook exists to prevent.

## 4. Point the apex A record at the new app

The inbound VIP is not readable from the Bicep template, so add it once the app exists:

```pwsh
$newIp = az webapp show -g rg-<AZURE_ENV_NAME> -n (azd env get-value WEB_APP_NAME) `
  --query "possibleInboundIpAddresses" -o tsv
$newIp = ($newIp -split ',')[0]
az network dns record-set a add-record -g rg-<AZURE_ENV_NAME> -z azure-finops-agent.com `
  -n "@" -a $newIp
```

## 5. Bind the hostnames and issue certificates

Strictly ordered, and the reason this is not in Bicep: the binding must exist **unsecured** first,
the managed certificate can only be issued once **public DNS already resolves to the app**, and
only then can the binding be updated with the thumbprint. That ordering spans DNS propagation,
which a single ARM deployment cannot wait on.

```pwsh
$rg  = "rg-<AZURE_ENV_NAME>"
$app = azd env get-value WEB_APP_NAME

foreach ($h in @("azure-finops-agent.com", "www.azure-finops-agent.com")) {
  az webapp config hostname add -g $rg --webapp-name $app --hostname $h
  az webapp config ssl create   -g $rg --name $app --hostname $h          # free managed cert
  $tp = az webapp config ssl list -g $rg --query "[?name=='$h'].thumbprint" -o tsv
  az webapp config ssl bind     -g $rg --name $app --certificate-thumbprint $tp --ssl-type SNI
}
```

If `ssl create` fails with a validation error, DNS has not propagated to the new app yet. Wait and
retry — do **not** proceed to step 7.

## 6. Enforce HTTPS at the platform

The old app relied only on `UseHttpsRedirection()` in code. Belt and braces:

```pwsh
az webapp update -g $rg -n $app --https-only true
```

## 7. Cut over — two-phase, reversible

**Phase A — repoint records inside the OLD zone (fast, reversible, no registrar change).**
The old zone is still authoritative, so this takes effect at TTL (3600s) rather than waiting on
nameserver propagation. Roll back by putting the old values back.

```pwsh
az account set --subscription <SRC_SUB_ID>          # old tenant
az network dns record-set a    remove-record -g rg-finops-agent -z azure-finops-agent.com -n "@"   -a 52.228.84.33
az network dns record-set a    add-record    -g rg-finops-agent -z azure-finops-agent.com -n "@"   -a $newIp
az network dns record-set cname set-record   -g rg-finops-agent -z azure-finops-agent.com -n "www" -c "$new"
```

Also publish the **new** app's `asuid` value, or the new bindings will fail validation:

```pwsh
$asuid = az webapp show -g $rg -n $app --subscription <DST_SUB_ID> --query customDomainVerificationId -o tsv
az network dns record-set txt remove-record -g rg-finops-agent -z azure-finops-agent.com -n asuid -v "<old value>"
az network dns record-set txt add-record    -g rg-finops-agent -z azure-finops-agent.com -n asuid -v $asuid
```

Verify from outside, then let it soak for a day or two:

```pwsh
Resolve-DnsName azure-finops-agent.com -Server 8.8.8.8 -Type A
Invoke-RestMethod "https://azure-finops-agent.com/api/version"     # should report the new build
```

**Phase B — move DNS hosting itself (only once Phase A is stable).**
The zone in the new tenant already exists from step 2 with every record. Update the four
nameservers at Namecheap to the `AZURE_DNS_NAME_SERVERS` values, then keep the **old zone alive and
identical** for at least 72h — resolvers cache NS records aggressively and will keep hitting the old
zone during that window. Deleting it early is the classic way to cause a partial outage.

## 8. Update the Entra app registration

`azd up` created a new multi-tenant app in the target tenant. Add the custom-domain callbacks:

```pwsh
$appId = azd env get-value AZURE_ENTRA_APP_ID
$uris  = @(
  "https://azure-finops-agent.com/auth/microsoft/callback"
  "https://azure-finops-agent.com/auth/microsoft/adminconsent/callback"
  "https://www.azure-finops-agent.com/auth/microsoft/callback"
  "https://www.azure-finops-agent.com/auth/microsoft/adminconsent/callback"
  "https://$new/auth/microsoft/callback"
  "https://$new/auth/microsoft/adminconsent/callback"
  "http://localhost:5000/auth/microsoft/callback"
  "http://localhost:5000/auth/microsoft/adminconsent/callback"
)
az ad app update --id $appId --web-redirect-uris @uris
```

If you want the domain verified in the new tenant (the old `MS=ms…` TXT record is bound to the old
tenant and is now meaningless), add the domain under Entra ID → Custom domain names and publish the
new verification TXT.

## 9. Post-cutover verification

```pwsh
pwsh -File docs/local/verify-domain.ps1        # if you kept the probe script
```

Otherwise check by hand — all of these must hold:

- `http://azure-finops-agent.com` → 307 → HTTPS
- `https://www.azure-finops-agent.com` → 301 → bare domain
- TLS cert subject matches the host, issued by DigiCert/GeoTrust, >80 days remaining
- `/api/version` reports the expected `sha`/`build`
- `X-Robots-Tag: noindex` is present on the `*.azurewebsites.net` host and **absent** on the custom domain
- The availability test in App Insights → Availability is green from all five locations

## 10. Rollback

Before Phase B, rollback is just restoring the A/CNAME/asuid records in the old zone — one TTL, no
registrar involvement. After Phase B it means changing nameservers back at Namecheap and waiting on
propagation, which is why Phase A must soak first.

## 11. Decommission the old stack — owner's call, not the agent's

Only after the new stack has run clean for a sensible period, and never as part of the cutover.
Take a final export of anything worth keeping first.

```pwsh
# Review before running. This is destructive and irreversible.
az group delete -n rg-finops-agent --subscription <SRC_SUB_ID>
```

Remember to remove the old Entra app registration too, and to revoke its client secret.
