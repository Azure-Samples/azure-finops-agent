#Requires -Version 7.2
<#
.SYNOPSIS
Decrypts a live-evaluation run's failed-case diagnostics with the maintainer certificate.

.DESCRIPTION
Pass -RunId to download that GitHub Actions run's artifacts for the current repository into a
temporary folder, or -Path for an already downloaded private-diagnostics.enc.json. Prints each
failed case's judge verdict, rationale, gate failures and answer. -OutFile also saves the full
decrypted JSON (including tool arguments and results); keep it in an access-restricted folder.

.EXAMPLE
./tests/LiveEvaluations/Unprotect-Diagnostics.ps1 -RunId 36473392453
#>
[CmdletBinding(DefaultParameterSetName = 'Run')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Run')][long]$RunId,
    [Parameter(Mandatory, ParameterSetName = 'File')][string]$Path,
    [string]$OutFile
)
$ErrorActionPreference = 'Stop'
$format = 'azure-finops-agent/live-evaluation-diagnostics/v1'

$download = $null
try {
    if ($PSCmdlet.ParameterSetName -eq 'Run') {
        $download = Join-Path ([IO.Path]::GetTempPath()) "finops-eval-diagnostics-$RunId-$([guid]::NewGuid().ToString('n'))"
        gh run download $RunId --dir $download --pattern 'live-ai-evaluations-*' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not download the artifacts of run $RunId." }
        $Path = Get-ChildItem $download -Recurse -Filter 'private-diagnostics.enc.json' |
            Select-Object -First 1 -ExpandProperty FullName
        if (-not $Path) { throw "Run $RunId has no encrypted diagnostics: every case passed, or EVAL_DIAGNOSTICS_CERT was not set." }
    }

    $envelope = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($envelope.format -ne $format -or $envelope.keyEncryption -ne 'RSA-OAEP-256' -or $envelope.contentEncryption -ne 'A256GCM') {
        throw 'Unsupported diagnostics envelope.'
    }
    $certificate = Get-ChildItem Cert:\CurrentUser\My | Where-Object {
        $_.HasPrivateKey -and
        [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($_.PublicKey.ExportSubjectPublicKeyInfo())) -eq $envelope.recipientSpkiSha256
    } | Select-Object -First 1
    if (-not $certificate) { throw "No certificate with a private key in Cert:\CurrentUser\My matches recipient $($envelope.recipientSpkiSha256)." }

    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
    $contentKey = $rsa.Decrypt([Convert]::FromBase64String($envelope.encryptedKey), [Security.Cryptography.RSAEncryptionPadding]::OaepSHA256)
    $ciphertext = [Convert]::FromBase64String($envelope.ciphertext)
    $plaintext = [byte[]]::new($ciphertext.Length)
    $aes = [Security.Cryptography.AesGcm]::new($contentKey, 16)
    try {
        $aes.Decrypt([Convert]::FromBase64String($envelope.iv), $ciphertext, [Convert]::FromBase64String($envelope.tag), $plaintext, [Text.Encoding]::UTF8.GetBytes($format))
    } finally {
        $aes.Dispose()
        [Array]::Clear($contentKey)
    }
    $json = [Text.Encoding]::UTF8.GetString($plaintext)
    if ($OutFile) { Set-Content -LiteralPath $OutFile -Value $json -NoNewline }

    $diagnostics = $json | ConvertFrom-Json
    if ($diagnostics.failure) { Write-Host "Suite failure: $($diagnostics.failure)" }
    foreach ($row in $diagnostics.results) {
        [pscustomobject]@{
            Id              = $row.id
            Question        = $row.result.question
            Accepted        = $row.result.judge.accepted
            Grounded        = $row.result.judge.grounded
            Complete        = $row.result.judge.complete
            EfficiencyScore = $row.result.judge.efficiencyScore
            JudgeReason     = $row.result.judge.reason
            Failures        = $row.failures -join ' | '
            Answer          = $row.result.answer
            Unfinished      = [bool]$row.unfinishedCapture
        }
    }
} finally {
    if ($download) { Remove-Item -LiteralPath $download -Recurse -Force -ErrorAction SilentlyContinue }
}
