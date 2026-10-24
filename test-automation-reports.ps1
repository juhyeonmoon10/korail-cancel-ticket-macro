$ErrorActionPreference = 'Stop'
$sourceRoot = $PSScriptRoot
$binRoot = Join-Path $sourceRoot 'tests\bin'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Required .NET Framework C# compiler was not found.' }
New-Item -ItemType Directory -Force -Path $binRoot | Out-Null
$testExe = Join-Path $binRoot 'AutomationReportsTests.exe'
$compileArgs = @('/nologo','/target:exe','/platform:x64','/utf8output','/langversion:5',
  '/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll',
  '/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll',
  '/reference:System.Net.Http.dll','/reference:System.Security.dll',('/out:' + $testExe),
  (Join-Path $sourceRoot 'UiTheme.cs'), (Join-Path $sourceRoot 'Settings.cs'),
  (Join-Path $sourceRoot 'AutomationReports.cs'), (Join-Path $sourceRoot 'AutomationReportsTests.cs'))
& $compiler @compileArgs
if ($LASTEXITCODE -ne 0) { throw 'Automation report test compilation failed.' }
& $testExe
if ($LASTEXITCODE -ne 0) { throw 'Automation report tests failed.' }
