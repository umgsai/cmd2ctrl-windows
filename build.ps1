# Build with the C# compiler shipped with Windows (.NET Framework 4.8), no .NET SDK required.
# NOTE: keep this file ASCII-only. PowerShell 5 parses scripts without BOM as ANSI/GBK and
# multi-byte characters in comments can swallow the following line.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }

$icon = Join-Path $root 'icon.png'
if (-not (Test-Path $icon)) { throw "Missing icon file: $icon" }

# Generate multi-size app.ico from icon.png to use as the exe file icon
& (Join-Path $root 'make-icon.ps1')
$ico = Join-Path $root 'app.ico'
if (-not (Test-Path $ico)) { throw "Icon conversion failed: $ico" }

& $csc /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 `
    /out:"$root\CmdKeyMapper.exe" `
    /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    /resource:"$icon" `
    /win32icon:"$ico" `
    "$root\Program.cs"

if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }
Write-Host "Built: $root\CmdKeyMapper.exe"
