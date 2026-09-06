# Codex Token 用量可视化 - 一键构建脚本
# 用法:  .\build.ps1                （输出到 .\bin\CodexTokenUsageViewer.exe）
#        .\build.ps1 -Output D:\xx\App.exe
param(
    [string]$Output = (Join-Path $PSScriptRoot "bin\CodexTokenUsageViewer.exe")
)
$ErrorActionPreference = "Stop"
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "未找到 csc.exe（需要 .NET Framework 4.x）: $csc" }
$fw   = Split-Path $csc
$src  = Join-Path $PSScriptRoot "CodexTokenUsageViewer.cs"
$ico  = Join-Path $PSScriptRoot "app.ico"
$outDir = Split-Path $Output -Parent
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

& $csc /nologo /target:winexe /platform:anycpu /optimize+ "/win32icon:$ico" "/out:$Output" `
    "/r:System.dll" "/r:System.Core.dll" "/r:System.Windows.Forms.dll" "/r:System.Drawing.dll" `
    "/r:System.Xml.dll" "/r:$fw\System.Web.Extensions.dll" $src
if ($LASTEXITCODE -ne 0) { throw "编译失败 (exit=$LASTEXITCODE)" }
Write-Host "构建成功: $Output"