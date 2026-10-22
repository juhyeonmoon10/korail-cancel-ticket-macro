$ErrorActionPreference = 'Stop'
$packageVersion = '1.0.4258.31'
$packageHash = '56F7F4B8BF9AEE4B8EFEFBBDD4F67D5F74EBD1B100ED0806DA71BF76AF481AA9'
$packageRoot = Join-Path $PSScriptRoot 'packages'
$archivePath = Join-Path $packageRoot ('webview2-' + $packageVersion + '.zip')
$extractPath = Join-Path $packageRoot 'webview2'
$packageUrl = 'https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/' + $packageVersion + '/microsoft.web.webview2.' + $packageVersion + '.nupkg'

New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
$cached = (Test-Path -LiteralPath $archivePath) -and ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -eq $packageHash)
if (-not $cached) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $packageUrl -OutFile $archivePath -UseBasicParsing
}
if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $packageHash) {
    throw 'WebView2 package checksum mismatch. The package was not extracted.'
}
Expand-Archive -LiteralPath $archivePath -DestinationPath $extractPath -Force
foreach ($relative in @('lib\net462\Microsoft.Web.WebView2.Core.dll', 'lib\net462\Microsoft.Web.WebView2.WinForms.dll', 'runtimes\win-x64\native\WebView2Loader.dll', 'LICENSE.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $extractPath $relative))) { throw ('Missing package file: ' + $relative) }
}
Write-Output ('Ready: Microsoft.Web.WebView2 ' + $packageVersion)
