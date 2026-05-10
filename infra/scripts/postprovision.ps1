# Postprovision hook (azd) — runs after Bicep deployment, before `azd deploy`.
#
# Responsibilities:
# 1. Patch the Entra app registration with the now-known App Service hostname
#    so OAuth callbacks work (`https://<hostname>/auth/microsoft/callback`).
# 2. Print a concise summary of what was provisioned.
#
# Safe to re-run: az ad app update is idempotent and we de-dupe before sending.

$ErrorActionPreference = 'Stop'

Write-Host "`n=== azd postprovision ===" -ForegroundColor Cyan

$envValues = azd env get-values | ConvertFrom-StringData
$objectId = $envValues['AZURE_ENTRA_OBJECT_ID']
$webHost  = $envValues['WEB_APP_HOSTNAME']
$webUrl   = $envValues['WEB_APP_URL']

if (-not $objectId -or -not $webHost) {
    Write-Host "  AZURE_ENTRA_OBJECT_ID or WEB_APP_HOSTNAME missing from azd env — skipping redirect-URI patch." -ForegroundColor Yellow
} else {
    $objectId = $objectId.Trim('"')
    $webHost  = $webHost.Trim('"')

    $desired = @(
        'http://localhost:5000/auth/microsoft/callback',
        "https://$webHost/auth/microsoft/callback"
    )

    Write-Host "  Patching Entra app redirect URIs for hostname: $webHost" -ForegroundColor Yellow
    $existingJson = az ad app show --id $objectId --query 'web.redirectUris' -o json 2>$null
    $existing = if ($existingJson) { $existingJson | ConvertFrom-Json } else { @() }
    $merged = @($existing + $desired | Select-Object -Unique)

    az ad app update --id $objectId --web-redirect-uris @merged --output none
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  Redirect URIs updated:" -ForegroundColor Green
        foreach ($u in $merged) { Write-Host "    $u" -ForegroundColor Gray }
    } else {
        Write-Host "  Failed to update redirect URIs (exit $LASTEXITCODE). Run manually:" -ForegroundColor Red
        Write-Host "    az ad app update --id $objectId --web-redirect-uris $($merged -join ' ')" -ForegroundColor Gray
    }
}

Write-Host "`n  Web App:    $webUrl" -ForegroundColor Cyan
Write-Host "  Next: image will be built and pushed by the postdeploy hook." -ForegroundColor Gray
Write-Host "=== postprovision complete ===`n" -ForegroundColor Cyan
