param([string]$OutputPath = (Join-Path $PSScriptRoot 'dist\KorailOneHelper.exe'))
$ErrorActionPreference = 'Stop'
$buildRoot = $PSScriptRoot
$pkg = Join-Path $buildRoot 'packages\webview2'
$outFile = [System.IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path ([System.IO.Path]::GetDirectoryName($outFile)) -Force | Out-Null
$argsList = @('/nologo','/target:winexe','/platform:x64','/optimize+','/utf8output','/langversion:5',('/out:' + $outFile),('/win32manifest:' + (Join-Path $buildRoot 'app.manifest')),
  '/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll','/reference:System.Net.Http.dll','/reference:System.Security.dll',
  ('/reference:' + (Join-Path $pkg 'lib\net462\Microsoft.Web.WebView2.Core.dll')),('/reference:' + (Join-Path $pkg 'lib\net462\Microsoft.Web.WebView2.WinForms.dll')))
$embedded = @{
 'Microsoft.Web.WebView2.Core.dll' = (Join-Path $pkg 'lib\net462\Microsoft.Web.WebView2.Core.dll')
 'Microsoft.Web.WebView2.WinForms.dll' = (Join-Path $pkg 'lib\net462\Microsoft.Web.WebView2.WinForms.dll')
 'WebView2Loader.dll' = (Join-Path $pkg 'runtimes\win-x64\native\WebView2Loader.dll')
 'WebView2-LICENSE.txt' = (Join-Path $pkg 'LICENSE.txt')
}
$publicAssets = @('core.js','content.js','korail-site.js','site-block.js','mouse-booking.js','mouse-refresh.js','mouse-more.js','queue-query.js','welcome.html','demo.html','demo.js','card-demo.html','card-demo.js')
foreach ($assetName in $publicAssets) {
    $assetPath = Join-Path (Join-Path $buildRoot 'assets') $assetName
    if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) { throw ('Missing public asset: ' + $assetName) }
    $embedded[$assetName] = $assetPath
}
foreach ($item in $embedded.GetEnumerator()) { $argsList += '/resource:' + $item.Value + ',bundle.' + $item.Key }
$argsList += Join-Path $buildRoot 'Program.cs'
$argsList += Join-Path $buildRoot 'UiTheme.cs'
$argsList += Join-Path $buildRoot 'DesktopShell.cs'
$argsList += Join-Path $buildRoot 'Settings.cs'
$argsList += Join-Path $buildRoot 'NotificationBridge.cs'
$argsList += Join-Path $buildRoot 'BookingAttempt.cs'
$argsList += Join-Path $buildRoot 'BookingSelection.cs'
$argsList += Join-Path $buildRoot 'BookingQueue.cs'
$argsList += Join-Path $buildRoot 'BookingQueueDialog.cs'
$argsList += Join-Path $buildRoot 'BookingQueueHost.cs'
$argsList += Join-Path $buildRoot 'BookingQueueSelfTests.cs'
$argsList += Join-Path $buildRoot 'QueueQueryProgress.cs'
$argsList += Join-Path $buildRoot 'SelectionMemory.cs'
$argsList += Join-Path $buildRoot 'SelectionMemoryHost.cs'
$argsList += Join-Path $buildRoot 'MouseBookingHost.cs'
$argsList += Join-Path $buildRoot 'MouseRefreshHost.cs'
$argsList += Join-Path $buildRoot 'MousePauseHost.cs'
$argsList += Join-Path $buildRoot 'MousePauseSelfTests.cs'
$argsList += Join-Path $buildRoot 'MouseMoreHost.cs'
$argsList += Join-Path $buildRoot 'MouseMoreSelfTests.cs'
$argsList += Join-Path $buildRoot 'RefreshDelayPolicy.cs'
$argsList += Join-Path $buildRoot 'RefreshOptions.cs'
$argsList += Join-Path $buildRoot 'RefreshSettingsDialog.cs'
$argsList += Join-Path $buildRoot 'RefreshLimitSelfTests.cs'
$argsList += Join-Path $buildRoot 'NativeMouseInput.cs'
$argsList += Join-Path $buildRoot 'SleepPrevention.cs'
$argsList += Join-Path $buildRoot 'SleepPreventionHost.cs'
$argsList += Join-Path $buildRoot 'SiteDiagnostics.cs'
$argsList += Join-Path $buildRoot 'SiteDiagnosticsHost.cs'
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' @argsList
if ($LASTEXITCODE -ne 0) { throw 'C# build failed.' }
Get-Item -LiteralPath $outFile | Select-Object FullName,Length
