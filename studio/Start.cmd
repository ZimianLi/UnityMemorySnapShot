@echo off
chcp 65001 >nul
cd /d "%~dp0"
title AllocHook Studio Launcher

echo ============================================================
echo   AllocHook Studio - 内存快照网页
echo ============================================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [错误] 未找到 dotnet，请先安装 .NET 8 SDK：
    echo         https://dotnet.microsoft.com/download
    echo.
    pause
    exit /b 1
)

if not exist "%~dp0run.ps1" (
    echo [错误] 当前目录缺少 run.ps1，请把本文件放在 studio 目录下。
    echo.
    pause
    exit /b 1
)

echo [1/2] 正在启动服务（会自动结束旧实例并重新构建）...
start "AllocHookStudio" /min powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1"

echo [2/2] 等待服务就绪...
powershell -NoProfile -Command ^
  "for ($i=0; $i -lt 80; $i++) { try { $r=Invoke-WebRequest 'http://127.0.0.1:7788/index.html' -UseBasicParsing -TimeoutSec 1; if ($r.StatusCode -eq 200) { exit 0 } } catch { }; Start-Sleep -Milliseconds 500 }; exit 1"

if errorlevel 1 (
    echo.
    echo [错误] 40 秒内服务没有就绪。请检查最小化窗口里的构建日志。
    echo.
    pause
    exit /b 1
)

echo 服务已就绪： http://127.0.0.1:7788
if "%AHH_NOBROWSER%"=="" start "" "http://127.0.0.1:7788"

echo.
echo 服务在单独的最小化窗口中运行；关闭那个窗口即停止服务。
echo 本窗口可以直接关闭。
echo.
pause
