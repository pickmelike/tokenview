@echo off
chcp 65001 >nul
title 上传 Codex Token 用量查看器 到 GitHub
echo ============================================
echo   Codex Token 用量查看器 - GitHub 上传
echo ============================================
echo.
cd /d "%~dp0"
if exist "..\codex-usage-viewer\.git" cd /d "..\codex-usage-viewer"

:: 使用 Clash 代理 (127.0.0.1:7890)
set HTTPS_PROXY=http://127.0.0.1:7890
set HTTP_PROXY=http://127.0.0.1:7890

echo [1/3] 添加远程仓库...
git remote add origin https://github.com/pickmelike/codex-token-usage-viewer.git 2>nul

echo [2/3] 推送 main 分支...
echo     如果弹出 GitHub 登录/授权窗口，请点击 Authorize 授权
git -c http.sslBackend=openssl push -u origin main
if errorlevel 1 goto :fail

echo [3/3] 推送版本标签 (v1.1, v1.2)...
git -c http.sslBackend=openssl push origin --tags
if errorlevel 1 goto :fail

echo.
echo ============================================
echo   ✅ 上传成功！
echo   仓库地址: https://github.com/pickmelike/codex-token-usage-viewer
echo ============================================
pause
exit /b 0

:fail
echo.
echo ============================================
echo   ❌ 上传失败
echo   可能原因: 代理未开启 / 未授权 / 仓库名冲突
echo   提示: 如果弹出授权窗口请点 Authorize；
echo         若打不开授权页，请在 Clash 中开启「系统代理」后重试
echo ============================================
pause
exit /b 1