param([string]$OutputPath = (Join-Path $PSScriptRoot 'BatteryMonitor.exe'))

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'The .NET Framework C# compiler was not found.'
}
$sources = 'Hid.cs', 'Batteries.cs', 'Program.cs' | ForEach-Object { Join-Path $PSScriptRoot $_ }
$icon = Join-Path $PSScriptRoot 'BatteryMonitor.ico'
& $compiler /nologo /utf8output /codepage:65001 /target:winexe /platform:anycpu /optimize+ "/out:$OutputPath" "/win32icon:$icon" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll @sources
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)." }
