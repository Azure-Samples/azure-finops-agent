#Requires -Version 7.2
<#
.SYNOPSIS
Decrypts a live-evaluation run's failed-case diagnostics with the maintainer certificate.

.DESCRIPTION
Pass -RunId to download that GitHub Actions run's artifacts for the current repository into a
temporary folder, or -Path for an already downloaded private-diagnostics.enc.json. Works for the
deployment gate (live-ai-evaluations-*) and the AI quality report, whose stage jobs each keep their
own diagnostics (ai-quality-stage-*), including a stage that finished while the run still goes on;
every encrypted file in the run is decrypted. Prints each
failed case's judge verdict, findings, rationale, gate failures and answer. -OutFile also saves the
full decrypted JSON (including tool arguments and results; a JSON array when the run had several
files); keep it in an access-restricted folder.

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

function Unprotect-File([string]$File) {
    $envelope = Get-Content -LiteralPath $File -Raw | ConvertFrom-Json
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
    [Text.Encoding]::UTF8.GetString($plaintext)
}

$download = $null
try {
    if ($PSCmdlet.ParameterSetName -eq 'Run') {
        $download = Join-Path ([IO.Path]::GetTempPath()) "finops-eval-diagnostics-$RunId-$([guid]::NewGuid().ToString('n'))"
        New-Item -ItemType Directory -Path $download | Out-Null
        # The artifacts API serves a finished stage's artifact while the rest of the run is still going (gh run download
        # waits for the whole run).
        $repository = gh repo view --json nameWithOwner --jq .nameWithOwner
        if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the current repository.' }
        $artifacts = @(gh api --paginate "repos/$repository/actions/runs/$RunId/artifacts" --jq '.artifacts[] | select(.expired | not) | "\(.id) \(.name)"' |
            Where-Object { $_ -match '^\d+ (live-ai-evaluations|ai-quality-stage|ai-quality-report)-' })
        if ($LASTEXITCODE -ne 0) { throw "Could not list the artifacts of run $RunId." }
        $token = gh auth token
        if ($LASTEXITCODE -ne 0) { throw 'Sign in with gh auth login first.' }
        foreach ($artifact in $artifacts) {
            $id, $name = $artifact -split ' ', 2
            $zip = Join-Path $download "$name.zip"
            Invoke-WebRequest -Uri "https://api.github.com/repos/$repository/actions/artifacts/$id/zip" -OutFile $zip `
                -Headers @{ Authorization = "Bearer $token"; Accept = 'application/vnd.github+json' } | Out-Null
            Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $download $name)
        }
        $token = $null
        $files = @(Get-ChildItem $download -Recurse -Filter 'private-diagnostics.enc.json' | Sort-Object FullName)
        if (-not $files) { throw "Run $RunId has no encrypted diagnostics: every case passed, or EVAL_DIAGNOSTICS_CERT was not set." }
    } else {
        $files = @(Get-Item -LiteralPath $Path)
    }

    $documents = @(foreach ($file in $files) {
        [pscustomobject]@{ Source = $file.Directory.Name; Json = Unprotect-File $file.FullName }
    })
    if ($OutFile) {
        $text = if ($documents.Count -eq 1) { $documents[0].Json } else { '[' + (($documents | ForEach-Object Json) -join ',') + ']' }
        Set-Content -LiteralPath $OutFile -Value $text -NoNewline
    }

    foreach ($document in $documents) {
        $diagnostics = $document.Json | ConvertFrom-Json
        if ($diagnostics.failure) { Write-Host "$($document.Source): suite failure: $($diagnostics.failure)" }
        foreach ($row in $diagnostics.results) {
            [pscustomobject]@{
                Source          = $document.Source
                Id              = $row.id
                Question        = $row.result.question
                Accepted        = $row.result.judge.accepted
                Grounded        = $row.result.judge.grounded
                Complete        = $row.result.judge.complete
                EfficiencyScore = $row.result.judge.efficiencyScore
                Findings        = @($row.result.judge.findings) -join ', '
                JudgeReason     = $row.result.judge.reason
                Failures        = $row.failures -join ' | '
                Answer          = $row.result.answer
                Unfinished      = [bool]$row.unfinishedCapture
            }
        }
    }
} finally {
    if ($download) { Remove-Item -LiteralPath $download -Recurse -Force -ErrorAction SilentlyContinue }
}
