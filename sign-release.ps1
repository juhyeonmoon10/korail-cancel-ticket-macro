param(
    [Parameter(Mandatory=$true)][string]$ExePath,
    [Parameter(Mandatory=$true)][string]$CertificateThumbprint,
    [Parameter(Mandatory=$true)][string]$SignToolPath
)
$ErrorActionPreference = 'Stop'
# Uses an existing trusted publisher certificate. Does not create certificates,
# install trust roots, change application-control policy, or collect passwords.
$releaseFile = (Resolve-Path -LiteralPath $ExePath).Path
$signTool = (Resolve-Path -LiteralPath $SignToolPath).Path
$thumbprint = $CertificateThumbprint.Replace(' ', '').ToUpperInvariant()
if ($thumbprint -notmatch '^[0-9A-F]{40}$') { throw 'Use the SHA-1 thumbprint of your existing code signing certificate.' }
if ([System.IO.Path]::GetExtension($releaseFile) -ne '.exe') { throw 'ExePath must name the release EXE.' }
$certificate = Get-Item -LiteralPath ('Cert:\CurrentUser\My\' + $thumbprint)
if (-not $certificate.HasPrivateKey) { throw 'The certificate does not have an accessible private key.' }
if ($certificate.NotAfter -le (Get-Date) -or $certificate.NotBefore -gt (Get-Date)) { throw 'The signing certificate is not currently valid.' }
if ($certificate.Subject -eq $certificate.Issuer) { throw 'A self-signed certificate does not establish public publisher trust.' }
if (-not ($certificate.EnhancedKeyUsageList | Where-Object { $_.ObjectId.Value -eq '1.3.6.1.5.5.7.3.3' })) { throw 'The certificate is not valid for code signing.' }
& $signTool sign /sha1 $thumbprint /fd SHA256 /tr 'https://timestamp.digicert.com' /td SHA256 $releaseFile
if ($LASTEXITCODE -ne 0) { throw 'Signing failed. No application-control settings were changed.' }
& $signTool verify /pa /all /v $releaseFile
if ($LASTEXITCODE -ne 0) { throw 'Authenticode verification failed.' }
$signature = Get-AuthenticodeSignature -LiteralPath $releaseFile
if ($signature.Status -ne 'Valid') { throw ('The resulting signature is not trusted: ' + $signature.Status) }
Get-FileHash -LiteralPath $releaseFile -Algorithm SHA256
Write-Output 'Release signed and verified. Rebuild the release ZIP and checksum after signing.'
