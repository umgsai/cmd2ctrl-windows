# 使用 Windows 自带的 C# 编译器（.NET Framework 4.8）编译，无需安装 .NET SDK
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }

$icon = Join-Path $root 'icon.png'
if (-not (Test-Path $icon)) { throw "缺少图标文件: $icon" }

& $csc /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 `
    /out:"$root\CmdKeyMapper.exe" `
    /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    /resource:"$icon" `
    "$root\Program.cs"

if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码 $LASTEXITCODE" }
Write-Host "已生成: $root\CmdKeyMapper.exe"
