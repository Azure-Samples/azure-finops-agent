# Verifies that the public internet resolves the custom domain through the
# Azure DNS zone this deployment created.
#
# A zone's nameservers are assigned when it is created and change if it is ever
# recreated (or moved to another subscription or tenant). The registrar keeps
# delegating to whatever it was last given, so a stale registrar entry takes the
# whole domain offline while the app itself stays healthy. This compares what a
# public resolver sees (over DNS-over-HTTPS, so it works behind any firewall)
# with the nameservers in the azd environment and says exactly what to change.
#
# Usage: pwsh infra/scripts/check-dns-delegation.ps1 [-Strict]
#   -Strict  exit 1 on a mismatch (use in CI); the default only warns.

param([switch]$Strict)

$ErrorActionPreference = 'Stop'

$envValues = azd env get-values -o json 2>$null | ConvertFrom-Json -AsHashtable
$domain = "$($envValues['AZURE_CUSTOM_DOMAIN'])".Trim('"')
if (-not $domain) {
    Write-Host '  No custom domain configured - skipping DNS delegation check.' -ForegroundColor Gray
    return
}

$expected = @()
# DNS_EXPECTED_NAME_SERVERS (comma-separated) overrides the Azure zone's nameservers
# when the domain's DNS is hosted elsewhere, e.g. the registrar's own nameservers.
$raw = "$($envValues['DNS_EXPECTED_NAME_SERVERS'])".Trim('"')
if ($raw) {
    $expected = @($raw -split '[,\s]+' | Where-Object { $_ } | ForEach-Object { $_.TrimEnd('.').ToLowerInvariant() })
} else {
    $raw = "$($envValues['AZURE_DNS_NAME_SERVERS'])"
    if ($raw) { $expected = @($raw | ConvertFrom-Json | ForEach-Object { $_.TrimEnd('.').ToLowerInvariant() }) }
}

function Get-PublicAnswer($name, $type) {
    try {
        $r = Invoke-RestMethod -Uri "https://dns.google/resolve?name=$name&type=$type" -TimeoutSec 15
        @($r.Answer | Where-Object { $_.type -eq 1 } | ForEach-Object { $_.data.TrimEnd('.').ToLowerInvariant() })
    } catch { $null }
}

# The registry's delegation is what resolvers follow; a resolver's NS answer can still
# show a previous zone's servers, so read the delegation from RDAP instead.
function Get-RegistryNameServers($name) {
    try {
        $r = Invoke-RestMethod -Uri "https://rdap.org/domain/$name" -TimeoutSec 20
        @($r.nameservers | ForEach-Object { $_.ldhName.TrimEnd('.').ToLowerInvariant() })
    } catch { $null }
}

$problems = @()
$actual = Get-RegistryNameServers $domain
$a = Get-PublicAnswer $domain 'A'

if ($null -eq $actual) {
    Write-Host '  Could not read the registry delegation - not verified.' -ForegroundColor Yellow
    return
}
if (-not $a -or $a.Count -eq 0) {
    $problems += "$domain has no public A record: the internet cannot resolve the site."
}
if ($expected.Count -gt 0 -and (Compare-Object $expected $actual)) {
    $problems += "Registry delegates to: $($actual -join ', ')"
    $problems += "Expected nameservers: $($expected -join ', ')"
}

if ($problems.Count -eq 0) {
    Write-Host "  DNS delegation OK: $domain -> $($a -join ', ')" -ForegroundColor Green
    return
}

Write-Host "`n  DNS DELEGATION PROBLEM for $domain" -ForegroundColor Red
$problems | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
Write-Host "  Fix: at the domain registrar, set the nameservers to: $($expected -join ', ')" -ForegroundColor Yellow
Write-Host '  If DNS is hosted at Azure, read them with: az network dns zone show -g <rg> -n <domain> --query nameServers' -ForegroundColor Gray
if ($Strict) { exit 1 }
