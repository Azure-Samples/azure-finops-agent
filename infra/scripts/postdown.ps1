# Postdown hook (azd) — runs after `azd down` tears down the infrastructure.
#
# `azd down` only deletes ARM resources (the resource group + its contents, and
# with `--purge` the soft-deleted Cognitive Services / Foundry account). The
# Entra app registration is created by the preprovision hook as a DIRECTORY
# object — not an ARM resource — so azd cannot remove it. Without this hook,
# every `azd up` / `azd down` cycle would leave an orphaned multi-tenant app
# (and its client secret) behind in the tenant.
#
# This deletes that app, but ONLY when azd created it
# (AZURE_ENTRA_APP_CREATED_BY_AZD = 'true'). A user-supplied app passed via
# AZURE_ENTRA_APP_ID is never touched.

# Best-effort cleanup: never let a non-zero az exit code throw or block `azd down`.
$ErrorActionPreference = 'Continue'
$PSNativeCommandUseErrorActionPreference = $false

Write-Host "`n=== azd postdown ===" -ForegroundColor Cyan

function Get-AzdEnvValue {
    param([string]$Key)
    $val = azd env get-value $Key 2>$null
    if ($LASTEXITCODE -ne 0 -or $null -eq $val) { return '' }
    return ([string]$val).Trim().Trim('"')
}

$createdByAzd = Get-AzdEnvValue 'AZURE_ENTRA_APP_CREATED_BY_AZD'
$objectId     = Get-AzdEnvValue 'AZURE_ENTRA_OBJECT_ID'
$appId        = Get-AzdEnvValue 'AZURE_ENTRA_APP_ID'

if ($createdByAzd -ne 'true') {
    Write-Host "  Entra app was user-supplied (or none was created) — leaving it in place." -ForegroundColor Yellow
    Write-Host "=== postdown complete ===`n" -ForegroundColor Cyan
    exit 0
}

if ([string]::IsNullOrWhiteSpace($objectId)) {
    Write-Host "  AZURE_ENTRA_OBJECT_ID missing from azd env — can't auto-delete the app." -ForegroundColor Yellow
    if ($appId) { Write-Host "  Delete it manually: az ad app delete --id $appId" -ForegroundColor Gray }
    Write-Host "=== postdown complete ===`n" -ForegroundColor Cyan
    exit 0
}

Write-Host "  Deleting the Entra app registration azd created: $appId ($objectId)" -ForegroundColor Yellow
az ad app delete --id $objectId 2>$null
if ($LASTEXITCODE -eq 0) {
    Write-Host "  Entra app deleted." -ForegroundColor Green
    # Clear the cached identifiers so a later `azd up` in this env creates a fresh app.
    foreach ($k in 'AZURE_ENTRA_APP_ID', 'AZURE_ENTRA_CLIENT_SECRET', 'AZURE_ENTRA_OBJECT_ID', 'AZURE_ENTRA_APP_CREATED_BY_AZD') {
        azd env set $k '' | Out-Null
    }
} else {
    Write-Host "  Could not delete the app (exit $LASTEXITCODE). Delete it manually:" -ForegroundColor Red
    Write-Host "    az ad app delete --id $objectId" -ForegroundColor Gray
}

Write-Host "=== postdown complete ===`n" -ForegroundColor Cyan
exit 0
