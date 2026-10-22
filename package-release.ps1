param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'),
    [string]$ExistingExe
)
$ErrorActionPreference = 'Stop'
$releaseRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
$releaseExe = Join-Path $releaseRoot 'KorailOneHelper.exe'
if ($ExistingExe) {
    # Preserve the exact binary that was tested or signed; rebuilding changes it.
    $testedExe = (Resolve-Path -LiteralPath $ExistingExe).Path
    if ([System.IO.Path]::GetExtension($testedExe) -ne '.exe') { throw 'ExistingExe must name the verified EXE.' }
    if (-not [String]::Equals($testedExe, $releaseExe, [StringComparison]::OrdinalIgnoreCase)) {
        Copy-Item -LiteralPath $testedExe -Destination $releaseExe -Force
    }
} else {
    & (Join-Path $PSScriptRoot 'build.ps1') -OutputPath $releaseExe
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
}
$publicFiles = @{
    'LICENSE' = 'LICENSE'
    '사용안내.txt' = 'docs\USAGE.txt'
    '검증결과.txt' = 'docs\VERIFICATION.txt'
    'WebView2-LICENSE.txt' = 'WebView2-LICENSE.txt'
    'manual-diagnostic.cmd' = 'manual-diagnostic.cmd'
}
foreach ($entry in $publicFiles.GetEnumerator()) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $entry.Value) -Destination (Join-Path $releaseRoot $entry.Key) -Force
}
$releaseHash = (Get-FileHash -LiteralPath $releaseExe -Algorithm SHA256).Hash
($releaseHash + '  KorailOneHelper.exe') | Set-Content -LiteralPath (Join-Path $releaseRoot 'SHA256.txt') -Encoding UTF8
# Explicit file list: never archive a user's settings, profiles or diagnostics.
$publicPaths = @($releaseExe, (Join-Path $releaseRoot 'SHA256.txt'))
$publicPaths += @($publicFiles.Keys | ForEach-Object { Join-Path $releaseRoot $_ })
$releaseArchive = Join-Path $releaseRoot 'korail-desktop.zip'
Compress-Archive -LiteralPath $publicPaths -DestinationPath $releaseArchive -Force
Get-Item -LiteralPath $releaseExe,$releaseArchive | Select-Object FullName,Length
