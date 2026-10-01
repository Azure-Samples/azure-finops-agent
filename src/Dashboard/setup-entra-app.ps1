<#
.SYNOPSIS
    Creates the Microsoft Entra ID app registration for Azure FinOps Agent.

.DESCRIPTION
    Automates the Entra ID app registration setup:
    - Creates a multi-tenant app registration
    - Configures redirect URIs (localhost + optional production URL)
    - Adds required API permissions (Azure ARM, Microsoft Graph, Log Analytics)
    - Creates a client secret
    - Outputs the values needed for appsettings.json

    Requires: Azure CLI (az) logged in with permissions to create app registrations.

.PARAMETER AppName
    Display name for the app registration (default: "Azure FinOps Agent").

.PARAMETER ProductionUrl
    Optional production URL (e.g. https://azure-finops-agent.com). If provided,
    adds it as a redirect URI alongside localhost.

.PARAMETER SecretExpiryMonths
    Client secret validity in months (default: 12).

.EXAMPLE
    # Basic setup (localhost only)
    .\setup-entra-app.ps1

    # With production URL
    .\setup-entra-app.ps1 -ProductionUrl "https://myfinops.azurewebsites.net"

    # Custom name and expiry
    .\setup-entra-app.ps1 -AppName "My FinOps Agent" -ProductionUrl "https://myfinops.com" -SecretExpiryMonths 24
#>

param(
    [string]$AppName = "Azure FinOps Agent",
    [string]$ProductionUrl = "",
    [int]$SecretExpiryMonths = 12,
    # Extra redirect URIs to register beyond the localhost + ProductionUrl defaults.
    # Used by the azd preprovision hook to register additional callbacks (e.g. App Service hostname).
    [string[]]$ExtraRedirectUris = @(),
    # When set, no client secret is created — the app authenticates via a federated
    # identity credential (App Service managed identity), configured by the azd
    # postprovision hook. Fully secretless (Workload Identity Federation).
    [switch]$NoSecret,
    # When set, suppresses all human-readable Write-Host output and prints a single
    # JSON object {appId, clientSecret, tenantId, redirectUris} to stdout — intended
    # for consumption by the azd preprovision hook.
    [switch]$OutputJson
)

$ErrorActionPreference = "Stop"
# az returns non-zero on conditions we handle explicitly below (missing service
# principal, failed update). Check $LASTEXITCODE instead of letting native command
# errors throw.
$PSNativeCommandUseErrorActionPreference = $false

# When -OutputJson is set, route all chatty status output to stderr so stdout is
# pure JSON the caller can pipe into ConvertFrom-Json.
function Write-Status {
    param([string]$Message, [string]$ForegroundColor = 'Gray')
    if ($OutputJson) {
        [Console]::Error.WriteLine($Message)
    }
    else {
        Write-Host $Message -ForegroundColor $ForegroundColor
    }
}

Write-Status "`n=== Azure FinOps Agent — Entra ID App Registration Setup ===" 'Cyan'
Write-Status "This script creates a multi-tenant app registration with read-only permissions.`n" 'Gray'

# ── 1. Verify az CLI is logged in ──
Write-Status "[1/6] Checking Azure CLI login..." 'Yellow'
$account = az account show 2>$null | ConvertFrom-Json
if (-not $account) {
    Write-Status "  Not logged in. Run 'az login' first." 'Red'
    exit 1
}
Write-Status "  Tenant: $($account.tenantId)" 'Gray'
Write-Status "  User:   $($account.user.name)" 'Gray'

# ── 2. Build redirect URIs ──
Write-Status "`n[2/6] Configuring redirect URIs..." 'Yellow'
$redirectUris = @(
    "http://localhost:5000/auth/microsoft/callback"
)
if ($ProductionUrl) {
    $baseUrl = $ProductionUrl.TrimEnd('/')
    $redirectUris += "$baseUrl/auth/microsoft/callback"

    # If bare domain, also add www variant
    $uri = [System.Uri]::new($baseUrl)
    if (-not $uri.Host.StartsWith("www.")) {
        $redirectUris += "$($uri.Scheme)://www.$($uri.Host)/auth/microsoft/callback"
    }
}
foreach ($u in $ExtraRedirectUris) {
    if ($u -and ($redirectUris -notcontains $u)) { $redirectUris += $u }
}
foreach ($u in $redirectUris) {
    Write-Status "  $u" 'Gray'
}

# ── 3. Create the app registration ──
Write-Status "`n[3/6] Creating app registration '$AppName'..." 'Yellow'

$appJson = az ad app create `
    --display-name $AppName `
    --sign-in-audience "AzureADMultipleOrgs" `
    --web-redirect-uris @redirectUris `
    --enable-id-token-issuance false `
    --enable-access-token-issuance false `
    2>$null

if (-not $appJson) {
    Write-Status "  Failed to create app registration. Check permissions." 'Red'
    exit 1
}

$app = $appJson | ConvertFrom-Json
$clientId = $app.appId
$objectId = $app.id

Write-Status "  App ID (ClientId): $clientId" 'Green'
Write-Status "  Object ID:         $objectId" 'Gray'

# ── 4. Add API permissions (all read-only) ──
Write-Status "`n[4/6] Adding API permissions (read-only)..." 'Yellow'

# Every permission below MUST be a DELEGATED permission (an entry in the resource's
# oauth2PermissionScopes), because they are requested with type = "Scope". Using an
# appRole GUID here makes admin consent fail atomically for the whole app.
# The GUIDs are Microsoft-published and stable across tenants, but they are also
# re-resolved by value at runtime (below) so a wrong constant cannot break consent.
function Resolve-DelegatedScopeId {
    param(
        [Parameter(Mandatory)][string]$ResourceAppId,
        [Parameter(Mandatory)][string]$ScopeValue,
        [Parameter(Mandatory)][string]$FallbackId
    )

    $resolved = az ad sp show --id $ResourceAppId `
        --query "oauth2PermissionScopes[?value=='$ScopeValue'].id | [0]" -o tsv 2>$null

    $queryExit = $LASTEXITCODE
    if ($queryExit -eq 0 -and $resolved) {
        $resolved = ($resolved | Out-String).Trim()
        if ($resolved -and $resolved -ne 'None') {
            if ($resolved -ne $FallbackId) {
                Write-Status "  Resolved $ScopeValue delegated scope id: $resolved" 'Gray'
            }
            return $resolved
        }
    }

    # Resource service principal not present in this tenant (or no directory read
    # permission) — fall back to the published GUID.
    return $FallbackId
}

# Azure Service Management
$armAppId = "797f4846-ba00-4fd7-ba43-dac1f8f63013"
$armUserImpersonation = Resolve-DelegatedScopeId $armAppId "user_impersonation" "41094075-9dad-400e-a0bd-54e686782033"

# Microsoft Graph
$graphAppId = "00000003-0000-0000-c000-000000000000"
$graphUserRead = Resolve-DelegatedScopeId $graphAppId "User.Read" "e1fe6dd8-ba31-4d61-89e7-88639da4683d"
$graphOrgReadAll = Resolve-DelegatedScopeId $graphAppId "Organization.Read.All" "4908d5b9-3fb2-4b1e-9336-1888b7937185"
$graphReportsReadAll = Resolve-DelegatedScopeId $graphAppId "Reports.Read.All" "02e97553-ed7b-43d0-ab3c-f8bace0d040c"
$graphUserReadAll = Resolve-DelegatedScopeId $graphAppId "User.Read.All" "a154be20-db9c-4678-8ab7-66f6cc099a59"
$graphGroupReadAll = Resolve-DelegatedScopeId $graphAppId "Group.Read.All" "5f8c59db-677d-491f-a6b8-5f174b11ec1d"

# Log Analytics
$laAppId = "ca7f3f0b-7d91-482c-8e09-c5d840d0eac5"
$laDataRead = Resolve-DelegatedScopeId $laAppId "Data.Read" "e8dac03d-d467-4a7e-9293-9cca7df08b31"

# Azure Storage
$storageAppId = "e406a681-f3d4-42a8-90b6-c2b029497af1"
$storageUserImpersonation = Resolve-DelegatedScopeId $storageAppId "user_impersonation" "03e0da56-190b-40ad-a80c-ea378c433f7f"

# Build the required resource access JSON
$requiredAccess = @(
    @{
        resourceAppId  = $armAppId
        resourceAccess = @(
            @{ id = $armUserImpersonation; type = "Scope" }
        )
    },
    @{
        resourceAppId  = $graphAppId
        resourceAccess = @(
            @{ id = $graphUserRead; type = "Scope" },
            @{ id = $graphOrgReadAll; type = "Scope" },
            @{ id = $graphReportsReadAll; type = "Scope" },
            @{ id = $graphUserReadAll; type = "Scope" },
            @{ id = $graphGroupReadAll; type = "Scope" }
        )
    },
    @{
        resourceAppId  = $laAppId
        resourceAccess = @(
            @{ id = $laDataRead; type = "Scope" }
        )
    },
    @{
        resourceAppId  = $storageAppId
        resourceAccess = @(
            @{ id = $storageUserImpersonation; type = "Scope" }
        )
    }
) | ConvertTo-Json -Depth 4 -Compress

# Write to temp file (az CLI doesn't accept inline JSON well on Windows)
$tempFile = [System.IO.Path]::GetTempFileName()
$requiredAccess | Out-File -FilePath $tempFile -Encoding utf8 -NoNewline

az ad app update --id $objectId --required-resource-accesses "@$tempFile" --output none
$permExit = $LASTEXITCODE
Remove-Item $tempFile -Force

if ($permExit -ne 0) {
    Write-Status "  Failed to set API permissions (exit $permExit). Consent will not work." 'Red'
    exit 1
}

Write-Status "  Azure ARM:       user_impersonation (delegated)" 'Gray'
Write-Status "  Microsoft Graph: User.Read, Organization.Read.All, Reports.Read.All," 'Gray'
Write-Status "                   User.Read.All, Group.Read.All (all delegated, read-only)" 'Gray'
Write-Status "  Log Analytics:   Data.Read (delegated, read-only)" 'Gray'
Write-Status "  Azure Storage:   user_impersonation (delegated, for cost exports)" 'Gray'
Write-Status ""
Write-Status "  NOTE: All Graph and Log Analytics scopes use incremental consent —" 'DarkYellow'
Write-Status "  users only see consent prompts when they opt into each tier." 'DarkYellow'

# ── 5. Create client secret (skipped for federated managed-identity mode) ──
if ($NoSecret) {
    Write-Status "`n[5/6] Skipping client secret — federated managed identity (no secret)." 'Yellow'
    $secret = ''
    $endDate = ''
}
else {
    Write-Status "`n[5/6] Creating client secret (valid $SecretExpiryMonths months)..." 'Yellow'

    $endDate = (Get-Date).AddMonths($SecretExpiryMonths).ToString("yyyy-MM-ddTHH:mm:ssZ")
    $secretJson = az ad app credential reset `
        --id $objectId `
        --display-name "FinOps Agent Secret" `
        --end-date $endDate `
        --query "{password: password}" `
        2>$null

    if (-not $secretJson) {
        Write-Status "  Failed to create client secret." 'Red'
        exit 1
    }

    $secret = ($secretJson | ConvertFrom-Json).password
    Write-Status "  Secret created (expires: $endDate)" 'Gray'
}

# ── 6. Output configuration ──
if ($OutputJson) {
    [pscustomobject]@{
        appId        = $clientId
        objectId     = $objectId
        clientSecret = $secret
        tenantId     = $account.tenantId
        redirectUris = $redirectUris
        secretExpiry = $endDate
    } | ConvertTo-Json -Compress
    return
}

Write-Host "`n[6/6] Setup complete!" -ForegroundColor Green
Write-Host "`n$('=' * 60)" -ForegroundColor Cyan
Write-Host "  ADD THESE VALUES TO YOUR CONFIGURATION" -ForegroundColor Cyan
Write-Host "$('=' * 60)" -ForegroundColor Cyan

Write-Host "`n  For local development (.NET User Secrets):" -ForegroundColor Yellow
Write-Host @"

    dotnet user-secrets set "Microsoft:ClientId" "$clientId"
    dotnet user-secrets set "Microsoft:ClientSecret" "$secret"
    dotnet user-secrets set "Microsoft:TenantId" "common"

"@ -ForegroundColor White

Write-Host "  For Azure App Service (environment variables):" -ForegroundColor Yellow
Write-Host @"

  Microsoft__ClientId=$clientId
  Microsoft__ClientSecret=$secret
  Microsoft__TenantId=common

"@ -ForegroundColor White

if ($ProductionUrl) {
    Write-Host "  Redirect URIs configured for:" -ForegroundColor Yellow
    foreach ($u in $redirectUris) {
        Write-Host "    $u" -ForegroundColor White
    }
    Write-Host ""
}

Write-Host "  Security notes:" -ForegroundColor Yellow
Write-Host "  - The agent can apply approved non-delete PUT/PATCH changes under the user's RBAC" -ForegroundColor Gray
Write-Host "  - All Graph/Log Analytics permissions are read-only by scope definition" -ForegroundColor Gray
Write-Host "  - ARM uses user_impersonation (only delegated scope available)" -ForegroundColor Gray
Write-Host "  - Azure DELETE and mutating action POST operations are blocked in code" -ForegroundColor Gray
Write-Host "  - For defense-in-depth, assign users Reader or Cost Management Reader RBAC" -ForegroundColor Gray
Write-Host ""
Write-Host "  IMPORTANT: Store the ClientSecret securely — it will NOT be shown again." -ForegroundColor Red
Write-Host ""
