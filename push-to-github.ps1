# 一键推送到 GitHub
# 用法:  .\push-to-github.ps1                     （默认推送到 pickmelike/codex-token-usage-viewer）
#        .\push-to-github.ps1 -Owner 你的用户名 -Repo 仓库名
param(
    [string]$Owner = "pickmelike",
    [string]$Repo  = "codex-token-usage-viewer"
)
$ErrorActionPreference = "Stop"
$proj = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $proj
$url = "https://github.com/$Owner/$Repo.git"

$remote = git remote get-url origin 2>$null
if ($LASTEXITCODE -ne 0) {
    git remote add origin $url
    Write-Host "已添加远程: $url" -ForegroundColor Green
} else {
    git remote set-url origin $url
    Write-Host "已更新远程: $url" -ForegroundColor Green
}

Write-Host "推送 main 分支..." -ForegroundColor Cyan
git push -u origin main
Write-Host "推送标签 (v1.1 / v1.2)..." -ForegroundColor Cyan
git push origin --tags
Write-Host ""
Write-Host "完成！仓库地址: https://github.com/$Owner/$Repo" -ForegroundColor Green