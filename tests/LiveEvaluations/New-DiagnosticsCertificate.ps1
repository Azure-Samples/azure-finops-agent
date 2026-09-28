#Requires -Version 7.2
<#
.SYNOPSIS
Creates the maintainer's live-evaluation diagnostics certificate and prints its public PEM.

.DESCRIPTION
The RSA 4096 private key is created non-exportable in Cert:\CurrentUser\My and never leaves this
Windows user profile. CI encrypts failed-case judge rationale, answers and tool details to the
public certificate; only this profile can decrypt them with Unprotect-Diagnostics.ps1.

Store the printed PEM in the repository variable (not a secret; forks do not inherit it):
    ./tests/LiveEvaluations/New-DiagnosticsCertificate.ps1 | gh variable set EVAL_DIAGNOSTICS_CERT
#>
[CmdletBinding()]
param([ValidateRange(1, 5)][int]$ValidYears = 2)
$ErrorActionPreference = 'Stop'

$certificate = New-SelfSignedCertificate `
    -Subject 'CN=Azure FinOps Agent live-evaluation diagnostics' `
    -Type DocumentEncryptionCert `
    -KeyAlgorithm RSA -KeyLength 4096 `
    -Provider 'Microsoft Software Key Storage Provider' `
    -KeyExportPolicy NonExportable `
    -CertStoreLocation Cert:\CurrentUser\My `
    -NotAfter (Get-Date).AddYears($ValidYears)
Write-Host "Created Cert:\CurrentUser\My\$($certificate.Thumbprint) (expires $($certificate.NotAfter.ToString('yyyy-MM-dd')))."
$certificate.ExportCertificatePem()
