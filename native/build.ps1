# 编译 liballochook.so
#
# 默认走「直接调用 NDK clang」的方式，不需要 cmake/ninja，任何装了 NDK 的机器都能用。
# 如果你有 cmake + ninja，也可以加 -UseCmake 走 CMakePresets.json（方便加文件/改选项）。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File native\build.ps1                 # arm64
#   powershell -ExecutionPolicy Bypass -File native\build.ps1 -Abi all        # 三种 ABI
#   powershell -ExecutionPolicy Bypass -File native\build.ps1 -Ndk "D:\ndk-r25c"
#   powershell -ExecutionPolicy Bypass -File native\build.ps1 -UseCmake

param(
    [ValidateSet("arm64", "armv7", "x86_64", "all")]
    [string]$Abi = "arm64",
    [string]$Ndk = "",
    [switch]$UseCmake,
    [switch]$NoCopy
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here

function Resolve-Ndk {
    param([string]$given)
    if ($given -and (Test-Path $given)) { return (Resolve-Path $given).Path }
    if ($env:ANDROID_NDK -and (Test-Path $env:ANDROID_NDK)) { return $env:ANDROID_NDK }
    $candidates = @(
        "E:\UnityBuilds\2022.3.62f3\Editor\Data\PlaybackEngines\AndroidPlayer\NDK",
        "C:\Program Files\Unity\Hub\Editor\2022.3.62f3\Editor\Data\PlaybackEngines\AndroidPlayer\NDK"
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    throw "找不到 Android NDK，请用 -Ndk 指定，或设置环境变量 ANDROID_NDK"
}

$ndk = Resolve-Ndk $Ndk
$env:ANDROID_NDK = $ndk
Write-Host "NDK: $ndk"

# abiKey -> @(presetName, abiDir, clangPrefix)
$map = @{
    "arm64"  = @("android-arm64", "arm64-v8a",    "aarch64-linux-android21")
    "armv7"  = @("android-armv7", "armeabi-v7a",  "armv7a-linux-androideabi21")
    "x86_64" = @("android-x86_64", "x86_64",      "x86_64-linux-android21")
}
$targets = if ($Abi -eq "all") { @("arm64", "armv7", "x86_64") } else { @($Abi) }

$src = Join-Path $here "src\allochook.cpp"
if (-not (Test-Path $src)) { throw "源码不存在: $src" }

foreach ($t in $targets) {
    $preset  = $map[$t][0]
    $abiDir  = $map[$t][1]
    $clangPre = $map[$t][2]

    if ($UseCmake) {
        Write-Host "== [cmake] $preset =="
        Push-Location $here
        try {
            & cmake --preset $preset
            if ($LASTEXITCODE -ne 0) { throw "cmake configure 失败: $preset（需要 ninja，或改用默认模式）" }
            & cmake --build --preset $preset
            if ($LASTEXITCODE -ne 0) { throw "cmake build 失败: $preset" }
        }
        finally { Pop-Location }
        $so = Join-Path $here "build\$abiDir\liballochook.so"
    }
    else {
        Write-Host "== [clang] $t =="
        $clang = Join-Path $ndk "toolchains\llvm\prebuilt\windows-x86_64\bin\$clangPre-clang++.cmd"
        if (-not (Test-Path $clang)) { throw "找不到 clang: $clang" }

        $outDir = Join-Path $here "build\$abiDir"
        New-Item -ItemType Directory -Force -Path $outDir | Out-Null
        $so = Join-Path $outDir "liballochook.so"

        # -fvisibility=hidden：只导出 allochook_* 三个符号。
        # 否则我们定义的 malloc/free/mmap 会出现在 RTLD_DEFAULT 里，
        # dlsym(RTLD_DEFAULT,"malloc") 会拿到自己，L2 直接变成无限递归。
        & $clang -shared -fPIC -O2 -fno-exceptions -fno-rtti -nostdlib++ `
            -fvisibility=hidden `
            -funwind-tables -fno-omit-frame-pointer -Wall `
            -o $so $src -lunwind -llog
        if ($LASTEXITCODE -ne 0) { throw "编译失败 exit=$LASTEXITCODE" }
    }

    if (-not (Test-Path $so)) { throw "产物不存在: $so" }
    Write-Host ("产出: {0}  ({1:N1} KB)" -f $so, ((Get-Item $so).Length / 1KB))

    if (-not $NoCopy) {
        $dst = Join-Path $root "unity\com.elex.allochook\Plugins\Android\$abiDir"
        New-Item -ItemType Directory -Force -Path $dst | Out-Null
        Copy-Item $so (Join-Path $dst "liballochook.so") -Force
        Write-Host "  已拷贝到 $dst"
    }
}

Write-Host "完成"
