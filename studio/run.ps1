# 启动 AllocHook Studio（本地 Web 界面）
# 用法：powershell -ExecutionPolicy Bypass -File studio\run.ps1 [-NoBuild] [-Port 7788]

param(
    [switch]$NoBuild,
    [int]$Port = 0
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "未找到 dotnet SDK，请先安装 .NET 8 SDK： https://dotnet.microsoft.com/download"
}

# 先结束上一次启动的实例，否则它占着 7788 端口和 bin 下的 exe，导致"改了没生效"或编译失败
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' OR Name='AllocHookStudio.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -match 'AllocHookStudio' } |
    ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force; Write-Host "已结束旧实例 pid=$($_.ProcessId)" } catch { } }
Start-Sleep -Milliseconds 300

if (-not $NoBuild) {
    Write-Host "== dotnet build =="
    & dotnet build -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw "构建失败" }
}

$args = @("run", "--no-build", "--project", "AllocHookStudio.csproj")
if ($Port -gt 0) { $args += @("--", "--port", "$Port") }

& dotnet @args
