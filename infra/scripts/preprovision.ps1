# Preprovision hook (azd) — runs before `azd provision`.
#
# Responsibilities:
# 1. Verify az CLI is logged into a subscription.
# 2. If AZURE_ENTRA_APP_ID is not already in azd env, create the multi-tenant
#    Entra ID app registration via setup-entra-app.ps1 -OutputJson and stash
#    appId / clientSecret / tenantId in azd env so Bicep picks them up in this
#    same provision run.
# 3. Capture AZURE_PRINCIPAL_ID for downstream role assignments if needed.
#
# Idempotent: if AZURE_ENTRA_APP_ID is already set, skip Entra creation.

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot | Split-Path -Parent

Write-Host "`n=== azd preprovision ===" -ForegroundColor Cyan

# ── 1. az CLI login check ──
$account = az account show 2>$null | ConvertFrom-Json
if (-not $account) {
    Write-Host "  az CLI not logged in. Running 'az login'..." -ForegroundColor Yellow
    az login --output none
    $account = az account show 2>$null | ConvertFrom-Json
}
Write-Host "  Subscription: $($account.name) ($($account.id))" -ForegroundColor Gray
Write-Host "  Tenant:       $($account.tenantId)" -ForegroundColor Gray

# Capture deployer principal ID for optional downstream role grants.
$signedInUser = az ad signed-in-user show --query id -o tsv 2>$null
if ($signedInUser) {
    azd env set AZURE_PRINCIPAL_ID $signedInUser | Out-Null
}

# ── 2. Entra app registration ──
function Get-AzdEnvValue {
    param([string]$Key)
    $val = azd env get-value $Key 2>$null
    if ($LASTEXITCODE -ne 0) { return '' }
    if ($null -eq $val) { return '' }
    return ([string]$val).Trim().Trim('"')
}

$existingAppId  = Get-AzdEnvValue 'AZURE_ENTRA_APP_ID'
$existingSecret = Get-AzdEnvValue 'AZURE_ENTRA_CLIENT_SECRET'
$envName        = Get-AzdEnvValue 'AZURE_ENV_NAME'
if ([string]::IsNullOrWhiteSpace($envName)) { $envName = $env:AZURE_ENV_NAME }
if ([string]::IsNullOrWhiteSpace($envName)) { $envName = 'finops-agent' }

if (-not [string]::IsNullOrWhiteSpace($existingAppId)) {
    $appId = $existingAppId
    $secret = $existingSecret

    # Validate the secret is also provided — Bicep would otherwise pass an empty
    # string into Microsoft__ClientSecret and OAuth would silently fall back to
    # anonymous mode at runtime.
    if ([string]::IsNullOrWhiteSpace($secret)) {
        Write-Host "  AZURE_ENTRA_APP_ID is set but AZURE_ENTRA_CLIENT_SECRET is missing." -ForegroundColor Red
        Write-Host "  Run: azd env set AZURE_ENTRA_CLIENT_SECRET '<your-app-secret>'" -ForegroundColor Yellow
        exit 1
    }

    # Verify the app actually exists in this tenant and capture its objectId so
    # postprovision.ps1 can patch the App Service hostname into the redirect URIs.
    Write-Host "  Reusing existing Entra app: $appId" -ForegroundColor Green
    $existingObjectId = az ad app show --id $appId --query id -o tsv 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $existingObjectId) {
        Write-Host "  App registration $appId not found in tenant $($account.tenantId)." -ForegroundColor Red
        Write-Host "  Either fix AZURE_ENTRA_APP_ID or unset it (azd env set AZURE_ENTRA_APP_ID '') to create a new one." -ForegroundColor Yellow
        exit 1
    }
    azd env set AZURE_ENTRA_OBJECT_ID $existingObjectId | Out-Null
    Write-Host "  Object ID:  $existingObjectId (cached for postprovision redirect-URI patch)" -ForegroundColor Gray
} else {
    Write-Host "  Creating Entra ID app registration (multi-tenant, 5 consent tiers)..." -ForegroundColor Yellow

    $entraScript = Join-Path $repoRoot 'src/Dashboard/setup-entra-app.ps1'
    $appName = "Azure FinOps Agent ($envName)"

    # Production redirect URI is unknown until Bicep runs. Register localhost now;
    # postprovision.ps1 patches the App Service hostname in afterwards.
    $jsonOutput = & $entraScript -AppName $appName -OutputJson
    if ($LASTEXITCODE -ne 0 -or -not $jsonOutput) {
        Write-Host "  setup-entra-app.ps1 failed." -ForegroundColor Red
        exit 1
    }

    $appInfo = $jsonOutput | ConvertFrom-Json
    azd env set AZURE_ENTRA_APP_ID $appInfo.appId | Out-Null
    azd env set AZURE_ENTRA_CLIENT_SECRET $appInfo.clientSecret | Out-Null
    azd env set AZURE_ENTRA_OBJECT_ID $appInfo.objectId | Out-Null

    Write-Host "  Created app: $($appInfo.appId)" -ForegroundColor Green
    Write-Host "  Secret stored in azd env (AZURE_ENTRA_CLIENT_SECRET)." -ForegroundColor Gray
    Write-Host "  Service-principal propagation can take 30-60s; Bicep will retry on first failure." -ForegroundColor DarkGray
}

Write-Host "=== preprovision complete ===`n" -ForegroundColor Cyan
