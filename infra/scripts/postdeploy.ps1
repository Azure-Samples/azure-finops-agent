# Postdeploy hook (azd) — runs after `azd deploy`.
#
# Responsibilities:
# 1. Build and push the container image to ACR using `az acr build` (no local
#    Docker daemon required — ACR runs the build server-side).
# 2. Restart the App Service so it pulls the freshly-tagged image.
# 3. Print the final URL.

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot | Split-Path -Parent
$dashboardDir = Join-Path $repoRoot 'src/Dashboard'

Write-Host "`n=== azd postdeploy ===" -ForegroundColor Cyan

$envValues = azd env get-values | ConvertFrom-StringData
$acrName    = $envValues['AZURE_CONTAINER_REGISTRY_NAME']
$image      = $envValues['AZURE_CONTAINER_REGISTRY_IMAGE']
$webApp     = $envValues['WEB_APP_NAME']
$webUrl     = $envValues['WEB_APP_URL']
$rg         = $envValues['AZURE_RESOURCE_GROUP']

foreach ($v in 'acrName','image','webApp','rg') {
    if (-not (Get-Variable -Name $v -ValueOnly).Trim('"')) {
        Write-Host "  Missing azd env var: $v. Aborting." -ForegroundColor Red
        exit 1
    }
}
$acrName = $acrName.Trim('"')
$image   = $image.Trim('"')
$webApp  = $webApp.Trim('"')
$rg      = $rg.Trim('"')

Write-Host "  Building image $image in ACR $acrName (this can take 3-6 min on the first run)..." -ForegroundColor Yellow
Push-Location $dashboardDir
try {
    az acr build `
        --registry $acrName `
        --image $image `
        --file Dockerfile `
        --output none `
        .
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  az acr build failed." -ForegroundColor Red
        exit 1
    }
} finally {
    Pop-Location
}
Write-Host "  Image built and pushed." -ForegroundColor Green

Write-Host "  Restarting App Service so the new image is pulled..." -ForegroundColor Yellow
az webapp restart --name $webApp --resource-group $rg --output none
if ($LASTEXITCODE -ne 0) {
    Write-Host "  Restart failed (exit $LASTEXITCODE). Restart manually if the site is stale." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "  ✅ Deployment complete." -ForegroundColor Green
Write-Host "  URL: $($webUrl.Trim('""'))" -ForegroundColor Cyan
Write-Host "  Health: $($webUrl.Trim('""'))/api/version" -ForegroundColor DarkGray
Write-Host "=== postdeploy complete ===`n" -ForegroundColor Cyan
