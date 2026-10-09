# Postprovision hook (azd) — runs after Bicep deployment, before `azd deploy`.
#
# Responsibilities:
# 1. Patch the Entra app registration with the now-known App Service hostname
#    (and optional preview slot hostname) so OAuth callbacks work
#    (`https://<hostname>/auth/microsoft/callback`).
# 2. Federate the web app's (and optional slot's) managed identity to the app
#    registration for secretless OAuth.
# 3. Print a concise summary of what was provisioned.
#
# Safe to re-run: az ad app update is idempotent and we de-dupe before sending.

$ErrorActionPreference = 'Stop'
# az returns non-zero on benign conditions (e.g. an empty list); handle those via
# $LASTEXITCODE checks instead of letting native command errors throw.
$PSNativeCommandUseErrorActionPreference = $false

Write-Host "`n=== azd postprovision ===" -ForegroundColor Cyan

$envValues = azd env get-values -o json 2>$null | ConvertFrom-Json -AsHashtable
$objectId = $envValues['AZURE_ENTRA_OBJECT_ID']
$webHost  = $envValues['WEB_APP_HOSTNAME']
$webUrl   = $envValues['WEB_APP_URL']
$customDomain = $envValues['AZURE_CUSTOM_DOMAIN']
$slotHost = $envValues['WEB_APP_SLOT_HOSTNAME']
if ($slotHost) { $slotHost = $slotHost.Trim('"') }

if (-not $objectId -or -not $webHost) {
    Write-Host "  AZURE_ENTRA_OBJECT_ID or WEB_APP_HOSTNAME missing from azd env — skipping redirect-URI patch." -ForegroundColor Yellow
} else {
    $objectId = $objectId.Trim('"')
    $webHost  = $webHost.Trim('"')
    if ($customDomain) { $customDomain = $customDomain.Trim('"') }

    $desired = @(
        'http://localhost:5000/auth/microsoft/callback',
        'http://localhost:5000/auth/microsoft/adminconsent/callback',
        "https://$webHost/auth/microsoft/callback",
        "https://$webHost/auth/microsoft/adminconsent/callback"
    )
    if ($customDomain) {
        $desired += @(
            "https://$customDomain/auth/microsoft/callback",
            "https://$customDomain/auth/microsoft/adminconsent/callback",
            "https://www.$customDomain/auth/microsoft/callback",
            "https://www.$customDomain/auth/microsoft/adminconsent/callback"
        )
    }
    if ($slotHost) {
        $desired += @(
            "https://$slotHost/auth/microsoft/callback",
            "https://$slotHost/auth/microsoft/adminconsent/callback"
        )
    }

    Write-Host "  Patching Entra app redirect URIs for hostname: $webHost" -ForegroundColor Yellow
    $existingJson = az ad app show --id $objectId --query 'web.redirectUris' -o json 2>$null
    # `az ... -o json` comes back as an ARRAY OF LINES in PowerShell, and piping
    # that straight into ConvertFrom-Json yields a bare string rather than a list
    # — so `$existing + $desired` silently did STRING CONCATENATION and produced
    # one mangled "…callbackhttp://…" URI that Entra rejected outright. Join the
    # lines first, then hard-cast to string[] so `+` is always array append.
    $existing = @()
    if ($existingJson) {
        $parsed = ($existingJson | Out-String) | ConvertFrom-Json
        if ($null -ne $parsed) { $existing = [string[]]@($parsed) }
    }
    $merged = @([string[]]($existing + $desired) | Select-Object -Unique)

    az ad app update --id $objectId --web-redirect-uris @merged --output none
    $updateExit = $LASTEXITCODE

    # Re-read and assert: a silently dropped patch leaves the production callback
    # missing and every post-consent sign-in fails with AADSTS50011.
    $appliedJson = az ad app show --id $objectId --query 'web.redirectUris' -o json 2>$null
    $applied = @()
    if ($appliedJson) {
        $appliedParsed = ($appliedJson | Out-String) | ConvertFrom-Json
        if ($null -ne $appliedParsed) { $applied = [string[]]@($appliedParsed) }
    }
    $missing = @($desired | Where-Object { $applied -notcontains $_ })

    if ($updateExit -eq 0 -and $missing.Count -eq 0) {
        Write-Host "  Redirect URIs updated:" -ForegroundColor Green
        foreach ($u in $merged) { Write-Host "    $u" -ForegroundColor Gray }
    } else {
        if ($updateExit -ne 0) {
            Write-Host "  Failed to update redirect URIs (exit $updateExit)." -ForegroundColor Red
        } else {
            Write-Host "  Redirect-URI patch did not apply — missing:" -ForegroundColor Red
            foreach ($u in $missing) { Write-Host "    $u" -ForegroundColor Red }
        }
        Write-Host "  OAuth sign-in will fail with AADSTS50011 until this is fixed. Run manually:" -ForegroundColor Red
        Write-Host "    az ad app update --id $objectId --web-redirect-uris $($merged -join ' ')" -ForegroundColor Gray
        exit 1
    }
}

# ── Federated identity credential (secretless auth) ──
# Let each App Service system-assigned managed identity (the web app and any
# preview slot — a slot has its own identity) authenticate the app registration
# via Workload Identity Federation, so NO client secret is needed.
# EntraClientCredentials.cs mints a client_assertion from the MI when no secret
# is configured.
function Set-SiteFederatedCredential {
    param(
        [string]$AppObjectId,
        [string]$TenantId,
        [string]$PrincipalId,
        [string]$Name,
        [string]$Label
    )
    Write-Host "  Federating the $Label managed identity to the app (secretless OAuth)..." -ForegroundColor Yellow

    $issuer = "https://login.microsoftonline.com/$TenantId/v2.0"
    $existing = az ad app federated-credential list --id $AppObjectId --query "[?name=='$Name'] | [0]" -o json 2>$null | ConvertFrom-Json
    # Recreating an identical credential would briefly break sign-in on a live site.
    if ($existing -and $existing.issuer -eq $issuer -and $existing.subject -eq $PrincipalId -and
        @($existing.audiences) -contains 'api://AzureADTokenExchange') {
        Write-Host "  Federated credential already current (subject = $Label MI $PrincipalId)." -ForegroundColor Green
        return
    }
    if ($existing) {
        az ad app federated-credential delete --id $AppObjectId --federated-credential-id $existing.id --output none 2>$null
    }

    $ficFile = Join-Path ([System.IO.Path]::GetTempPath()) "$Name.json"
    @{
        name        = $Name
        issuer      = $issuer
        subject     = $PrincipalId
        audiences   = @('api://AzureADTokenExchange')
        description = "Azure FinOps Agent $Label managed identity (secretless OAuth confidential client)"
    } | ConvertTo-Json | Set-Content -Path $ficFile -Encoding utf8

    az ad app federated-credential create --id $AppObjectId --parameters "@$ficFile" --output none 2>$null
    $ficExit = $LASTEXITCODE
    Remove-Item $ficFile -Force -ErrorAction SilentlyContinue

    if ($ficExit -eq 0) {
        Write-Host "  Federated credential created (subject = $Label MI $PrincipalId)." -ForegroundColor Green
    } else {
        Write-Host "  WARNING: federated credential creation failed (exit $ficExit)." -ForegroundColor Red
        Write-Host "  'Connect Azure' OAuth on the $Label will not work until a credential (audience api://AzureADTokenExchange, subject $PrincipalId) is added to app $AppObjectId." -ForegroundColor Gray
    }
}

$appObjectId   = $envValues['AZURE_ENTRA_OBJECT_ID']
$tenantId      = $envValues['AZURE_TENANT_ID']
$miPrincipalId = $envValues['WEB_APP_PRINCIPAL_ID']
$slotPrincipalId = $envValues['WEB_APP_SLOT_PRINCIPAL_ID']
if ($appObjectId) { $appObjectId = $appObjectId.Trim('"') }
if (-not $appObjectId -or -not $tenantId -or -not $miPrincipalId) {
    Write-Host "  Skipping federated credential — AZURE_ENTRA_OBJECT_ID / AZURE_TENANT_ID / WEB_APP_PRINCIPAL_ID missing." -ForegroundColor Yellow
} else {
    $tenantId = $tenantId.Trim('"')
    Set-SiteFederatedCredential -AppObjectId $appObjectId -TenantId $tenantId `
        -PrincipalId $miPrincipalId.Trim('"') -Name 'finops-appservice-mi' -Label 'App Service'
    if ($slotPrincipalId) {
        Set-SiteFederatedCredential -AppObjectId $appObjectId -TenantId $tenantId `
            -PrincipalId $slotPrincipalId.Trim('"') -Name 'finops-appservice-slot-mi' -Label 'preview slot'
    }
}

if ($customDomain) {
    # Never fails the deployment: a stale registrar entry is fixed at the registrar, not by azd.
    try { & "$PSScriptRoot/check-dns-delegation.ps1" } catch { Write-Host "  DNS delegation check skipped: $($_.Exception.Message)" -ForegroundColor Yellow }
}

Write-Host "`n  Web App:    $webUrl" -ForegroundColor Cyan
if ($slotHost) { Write-Host "  Preview:    https://$slotHost" -ForegroundColor Cyan }
$deployClientId = $envValues['PREVIEW_DEPLOY_CLIENT_ID']
if ($deployClientId -and $slotHost) {
    Write-Host "  Feature workflow: set secrets AZURE_CLIENT_ID=$($deployClientId.Trim('"')), AZURE_TENANT_ID, AZURE_SUBSCRIPTION_ID" -ForegroundColor Gray
    Write-Host "  and variables TEST_WEBAPP_NAME, TEST_RESOURCE_GROUP, TEST_SLOT_NAME, TEST_ACR_*, TEST_CONTAINER_IMAGE, TEST_VERIFY_URL=https://$slotHost" -ForegroundColor Gray
}
Write-Host "  Next: image will be built and pushed by the postdeploy hook." -ForegroundColor Gray
Write-Host "=== postprovision complete ===`n" -ForegroundColor Cyan
