<#
  把早期加载用的 ContentProvider 编成 allochook_early.aar。

  为什么不用 .androidlib：
    Unity 导出 Gradle 工程时，会把包内的 .androidlib 当成子模块写进
    unityLibrary/build.gradle（implementation project('xxx.androidlib')），
    但并不会真正导出这个模块，导致 Gradle 报
    "compileSdkVersion is not specified" 直接构建失败。
    .aar 则走标准的 libs 目录，导出工程能正常处理。

  依赖：Unity 自带 OpenJDK（有 javac / jar）+ Android SDK 的 android.jar
#>
param(
    [string]$JdkBin     = "E:\UnityBuilds\2022.3.62f3\Editor\Data\PlaybackEngines\AndroidPlayer\OpenJDK\bin",
    [string]$SdkDir     = "D:\AndroidSDK",
    [string]$Out        = "E:\Gitlab\UnityMemorySnapShot\unity\com.elex.allochook\Plugins\Android\allochook_early.aar"
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcDir = Join-Path $here "..\unity\com.elex.allochook\Android~\early"
$srcDir = [System.IO.Path]::GetFullPath($srcDir)

$javac = Join-Path $JdkBin "javac.exe"
$jar   = Join-Path $JdkBin "jar.exe"
if (-not (Test-Path $javac)) { throw "找不到 javac: $javac" }
if (-not (Test-Path $jar))   { throw "找不到 jar: $jar" }

# 选版本最高的 android.jar
$androidJar = Get-ChildItem (Join-Path $SdkDir "platforms") -Directory -ErrorAction SilentlyContinue |
    Sort-Object { [int]($_.Name -replace 'android-','') } -Descending |
    ForEach-Object { Join-Path $_.FullName "android.jar" } |
    Where-Object { Test-Path $_ } |
    Select-Object -First 1
if (-not $androidJar) { throw "找不到 android.jar（检查 -SdkDir）" }

$work = Join-Path $env:TEMP "allochook_aar"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $work "classes") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $work "stage") -Force | Out-Null

Write-Host "javac:      $javac"
Write-Host "android.jar: $androidJar"
Write-Host "src:        $srcDir"

& $javac -encoding UTF-8 -source 1.8 -target 1.8 -nowarn -classpath $androidJar -d (Join-Path $work "classes") `
    (Join-Path $srcDir "AllocHookInitProvider.java")
if ($LASTEXITCODE -ne 0) { throw "javac 失败" }

# classes.jar（jar 的 -C 保证条目在根目录）
Push-Location (Join-Path $work "classes")
& $jar cf (Join-Path $work "stage\classes.jar") .
Pop-Location
if ($LASTEXITCODE -ne 0) { throw "jar 失败" }

Copy-Item (Join-Path $srcDir "AndroidManifest.xml") (Join-Path $work "stage\AndroidManifest.xml") -Force

# 打成 .aar（本质是 zip，要求 AndroidManifest.xml 和 classes.jar 在根目录）
if (Test-Path $Out) { Remove-Item $Out -Force }
New-Item -ItemType Directory -Path (Split-Path $Out -Parent) -Force | Out-Null
Push-Location (Join-Path $work "stage")
try {
    Compress-Archive -Path * -DestinationPath ($Out + ".zip") -Force
} finally {
    Pop-Location
}
Move-Item ($Out + ".zip") $Out -Force

$size = [math]::Round((Get-Item $Out).Length / 1KB, 1)
Write-Host "产出: $Out  ($size KB)"
