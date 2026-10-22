$ErrorActionPreference = 'Stop'
$sourceRoot = $PSScriptRoot
$binRoot = Join-Path $sourceRoot 'tests\bin'
$dataRoot = Join-Path $sourceRoot 'tests\data'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Required .NET Framework C# compiler was not found.' }
New-Item -ItemType Directory -Force -Path $binRoot,$dataRoot | Out-Null
$commonArgs = @('/nologo','/target:exe','/platform:x64','/utf8output','/langversion:5',
  '/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll',
  '/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll',
  '/reference:System.Net.Http.dll','/reference:System.Security.dll')
$settingsExe = Join-Path $binRoot 'SettingsTests.exe'
$settingsArgs = $commonArgs + @(('/out:' + $settingsExe), (Join-Path $sourceRoot 'UiTheme.cs'), (Join-Path $sourceRoot 'Settings.cs'), (Join-Path $sourceRoot 'SettingsTests.cs'))
& $compiler @settingsArgs
if ($LASTEXITCODE -ne 0) { throw 'Settings test compilation failed.' }
& $settingsExe $dataRoot
if ($LASTEXITCODE -ne 0) { throw 'Settings mock tests failed.' }
$notificationExe = Join-Path $binRoot 'NotificationTests.exe'
$notificationArgs = $commonArgs + @(('/out:' + $notificationExe), (Join-Path $sourceRoot 'UiTheme.cs'), (Join-Path $sourceRoot 'Settings.cs'), (Join-Path $sourceRoot 'NotificationBridge.cs'), (Join-Path $sourceRoot 'NotificationTests.cs'))
& $compiler @notificationArgs
if ($LASTEXITCODE -ne 0) { throw 'Notification test compilation failed.' }
& $notificationExe
if ($LASTEXITCODE -ne 0) { throw 'Notification mock tests failed.' }
$bookingExe = Join-Path $binRoot 'BookingAttemptTests.exe'
$bookingArgs = $commonArgs + @(('/out:' + $bookingExe), (Join-Path $sourceRoot 'BookingAttempt.cs'), (Join-Path $sourceRoot 'BookingAttemptTests.cs'))
& $compiler @bookingArgs
if ($LASTEXITCODE -ne 0) { throw 'Booking attempt test compilation failed.' }
& $bookingExe (Join-Path $dataRoot 'booking')
if ($LASTEXITCODE -ne 0) { throw 'Booking attempt tests failed.' }
$selectionExe = Join-Path $binRoot 'BookingSelectionTests.exe'
$selectionArgs = $commonArgs + @(('/out:' + $selectionExe), (Join-Path $sourceRoot 'BookingAttempt.cs'), (Join-Path $sourceRoot 'BookingSelection.cs'), (Join-Path $sourceRoot 'BookingSelectionTests.cs'))
& $compiler @selectionArgs
if ($LASTEXITCODE -ne 0) { throw 'Booking selection test compilation failed.' }
& $selectionExe
if ($LASTEXITCODE -ne 0) { throw 'Booking selection tests failed.' }
$diagnosticsExe = Join-Path $binRoot 'SiteDiagnosticsTests.exe'
$diagnosticsArgs = $commonArgs + @(('/out:' + $diagnosticsExe), (Join-Path $sourceRoot 'SiteDiagnostics.cs'), (Join-Path $sourceRoot 'SiteDiagnosticsTests.cs'))
& $compiler @diagnosticsArgs
if ($LASTEXITCODE -ne 0) { throw 'Site diagnostics test compilation failed.' }
& $diagnosticsExe
if ($LASTEXITCODE -ne 0) { throw 'Site diagnostics tests failed.' }
$sleepExe = Join-Path $binRoot 'SleepPreventionTests.exe'
$sleepArgs = $commonArgs + @(('/out:' + $sleepExe), (Join-Path $sourceRoot 'SleepPrevention.cs'), (Join-Path $sourceRoot 'SleepPreventionTests.cs'))
& $compiler @sleepArgs
if ($LASTEXITCODE -ne 0) { throw 'Sleep prevention test compilation failed.' }
& $sleepExe
if ($LASTEXITCODE -ne 0) { throw 'Sleep prevention tests failed.' }
$delayExe = Join-Path $binRoot 'RefreshDelayPolicyTests.exe'
$delayArgs = $commonArgs + @(('/out:' + $delayExe), (Join-Path $sourceRoot 'RefreshDelayPolicy.cs'), (Join-Path $sourceRoot 'RefreshDelayPolicyTests.cs'))
& $compiler @delayArgs
if ($LASTEXITCODE -ne 0) { throw 'Refresh delay test compilation failed.' }
& $delayExe
if ($LASTEXITCODE -ne 0) { throw 'Refresh delay tests failed.' }
$optionsExe = Join-Path $binRoot 'RefreshOptionsTests.exe'
$optionsArgs = $commonArgs + @(('/out:' + $optionsExe), (Join-Path $sourceRoot 'RefreshOptions.cs'), (Join-Path $sourceRoot 'RefreshOptionsTests.cs'))
& $compiler @optionsArgs
if ($LASTEXITCODE -ne 0) { throw 'Refresh options test compilation failed.' }
& $optionsExe
if ($LASTEXITCODE -ne 0) { throw 'Refresh options tests failed.' }
$queueExe = Join-Path $binRoot 'BookingQueueTests.exe'
$queueArgs = $commonArgs + @(('/out:' + $queueExe), (Join-Path $sourceRoot 'BookingAttempt.cs'), (Join-Path $sourceRoot 'RefreshOptions.cs'), (Join-Path $sourceRoot 'BookingQueue.cs'), (Join-Path $sourceRoot 'BookingQueueTests.cs'))
& $compiler @queueArgs
if ($LASTEXITCODE -ne 0) { throw 'Booking queue test compilation failed.' }
& $queueExe (Join-Path $dataRoot 'queue')
if ($LASTEXITCODE -ne 0) { throw 'Booking queue tests failed.' }
$memoryExe = Join-Path $binRoot 'SelectionMemoryTests.exe'
$memoryArgs = $commonArgs + @(('/out:' + $memoryExe), (Join-Path $sourceRoot 'BookingAttempt.cs'), (Join-Path $sourceRoot 'BookingSelection.cs'), (Join-Path $sourceRoot 'RefreshOptions.cs'), (Join-Path $sourceRoot 'BookingQueue.cs'), (Join-Path $sourceRoot 'SelectionMemory.cs'), (Join-Path $sourceRoot 'SelectionMemoryTests.cs'))
& $compiler @memoryArgs
if ($LASTEXITCODE -ne 0) { throw 'Selection memory test compilation failed.' }
& $memoryExe (Join-Path $dataRoot 'selection-memory')
if ($LASTEXITCODE -ne 0) { throw 'Selection memory tests failed.' }
$progressExe = Join-Path $binRoot 'QueueQueryProgressTests.exe'
$progressArgs = $commonArgs + @(('/out:' + $progressExe), (Join-Path $sourceRoot 'QueueQueryProgress.cs'), (Join-Path $sourceRoot 'QueueQueryProgressTests.cs'))
& $compiler @progressArgs
if ($LASTEXITCODE -ne 0) { throw 'Queue query progress test compilation failed.' }
& $progressExe
if ($LASTEXITCODE -ne 0) { throw 'Queue query progress tests failed.' }
$inputExe = Join-Path $binRoot 'NativeInputPolicyTests.exe'
$inputArgs = $commonArgs + @(('/out:' + $inputExe), (Join-Path $sourceRoot 'NativeMouseInput.cs'), (Join-Path $sourceRoot 'NativeInputPolicyTests.cs'))
& $compiler @inputArgs
if ($LASTEXITCODE -ne 0) { throw 'Native input policy test compilation failed.' }
& $inputExe
if ($LASTEXITCODE -ne 0) { throw 'Native input policy tests failed.' }
Write-Output 'All C# tests passed, including a short native Windows sleep-prevention request/release. No real Telegram or Korail requests were made.'
