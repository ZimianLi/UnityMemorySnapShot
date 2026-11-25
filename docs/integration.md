# Unity 工程接入说明

> 目标：**不改业务代码**，只加一个本地 UPM 包，就能在任意 dev 包里抓 native 快照。

## 1. 导入 UPM 包

把仓库里的 `unity/com.elex.allochook` 放到任意固定路径，然后在 `Packages/manifest.json` 的 `dependencies` 里加一行本地包：

```json
{
  "dependencies": {
    "com.elex.allochook": "file:E:/Gitlab/UnityMemorySnapShot/unity/com.elex.allochook"
  }
}
```

相对路径也可以（相对于工程的 `Packages` 目录）：

```json
"com.elex.allochook": "file:../../UnityMemorySnapShot/unity/com.elex.allochook"
```

> ⚠️ 改完 `manifest.json` 必须让 Unity **重新解析一次包**（切回 Unity 窗口、等待包导入完成，必要时重启编辑器）。否则等于没装，后面所有步骤都会"看起来配置了但没生效"。

包内结构：

```
com.elex.allochook/
  package.json
  link.xml                     # 辅助（真正的保命手段是 AlwaysLinkAssembly + 显式引用）
  Runtime/
    Elex.AllocHook.asmdef      # 独立程序集
    AssemblyInfo.cs            # [assembly: AlwaysLinkAssembly] —— 防 IL2CPP 裁剪
    AllocHookLoader.cs         # 自动加载（BeforeSceneLoad + AfterSceneLoad 双入口）
    AllocHookApi.cs            # Snapshot() / Reset()，给 GM 用
  Plugins/Android/allochook_early.aar  # Application.onCreate 之前自动加载 .so
  Plugins/Android/<abi>/liballochook.so(.meta)
  Android~/early/                      # allochook_early.aar 源码
  Editor/AllocHookBuildCheck.cs  # 打包前检查 + 一键修复插件平台设置
```

## 2. 确认 .so 已经进包

包里的 `Plugins/Android/<abi>/liballochook.so` 由 Unity 自动打进 APK，**不需要手动往导出工程里塞**。

- ARM64 设备：`Plugins/Android/arm64-v8a/liballochook.so`
- ARMv7 设备：`Plugins/Android/armeabi-v7a/liballochook.so`（32 位仅支持 LD_PRELOAD 方式，见 FAQ）
- 模拟器：`Plugins/Android/x86_64/liballochook.so`

缺哪个 ABI 就在 `native/build.ps1 -Abi all` 里补编译一次（会自动拷进包）。

### ⚠️ 最容易踩的一步：`.so` 的平台设置

Unity 首次导入 `.so` 时，生成的 `.meta` 可能是 **`Any Platform = 勾选`、`Android = 未勾选`**：

```yaml
platformData:
- first: {Android: Android}
  second:
    enabled: 0        ← 未勾选 → 打包直接跳过，APK 里没有这个 .so
- first: {Any: }
  second:
    enabled: 1
```

`.so` 只对 Android 生效，勾 `Any` 是无效的。两种修法：

1. 菜单 **`Tools → AllocHook → 修复插件平台设置`**（推荐，一次改三个 ABI）；
2. 或手动选中每个 `liballochook.so` → Inspector 里取消 `Any Platform`、勾选 `Android`、并把 `CPU` 设为对应架构。

## 3. 打包要求

| 项 | 要求 | 原因 |
|---|---|---|
| 脚本后端 | IL2CPP 或 Mono 都可以 | 两条路都已验证 |
| ABI | 与设备一致（一般 ARM64） | .so 按 ABI 放置 |
| Development Build | **建议开** | 需要连 Profiler / 抓托管对象；且 `run-as` 需要 debuggable |
| 符号 | 导出 Gradle 工程后 `unityLibrary/symbols/<abi>/` 下要有 `libunity.so`、`libil2cpp.so` | 没有符号就只能看 `libunity.so+0x1234` |

> **符号是怎么来的**：Unity 导出 Gradle 工程时会执行 `CopyFiles .../unityLibrary/symbols/arm64-v8a/libunity.so`，这个副本的 build-id 与打进包的 `.so` **完全一致**，是唯一可用于符号化的文件。Unity 安装目录 `Variations/*/Symbols/libunity.sym.so` 是另一套构建，**不能用**。

在 Studio 里把「符号目录」指向：

```
<工程>\Build\export_project\<渠道>\unityLibrary\symbols\arm64-v8a
```

支持 `symbols.zip`（解压后指向目录即可）。

## 4. 验证加载（三步，缺一不可）

### 4.1 包里有 .so 吗

```powershell
Add-Type -AssemblyName System.IO.Compression.FileSystem
$z = [System.IO.Compression.ZipFile]::OpenRead("<apk路径>")
$z.Entries | Where-Object { $_.FullName -match "allochook" } | Select-Object FullName, Length
$z.Dispose()
```

期望出现 `lib/arm64-v8a/liballochook.so`。

### 4.2 托管代码还在吗（防 IL2CPP 裁剪）

```powershell
Add-Type -AssemblyName System.IO.Compression.FileSystem
$z = [System.IO.Compression.ZipFile]::OpenRead("<apk路径>")
$e = $z.Entries | Where-Object { $_.FullName -match "global-metadata.dat$" } | Select-Object -First 1
$tmp = "$env:TEMP\gm.dat"; [System.IO.Compression.ZipFileExtensions]::ExtractToFile($e, $tmp, $true); $z.Dispose()
$txt = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($tmp))
"AllocHookLoader : " + $txt.Contains("AllocHookLoader")
Remove-Item $tmp
```

必须是 `True`。为 `False` 说明整个 `Elex.AllocHook` 被裁掉了，看第 7.2 节。

### 4.3 运行期日志

```bash
adb logcat -c
# 启动游戏
adb logcat -d | findstr AllocHook
```

期望：

```
[AllocHook] loaded v100, dataDir=/data/data/com.xxx.yyy
AllocHook : dir=/data/data/com.xxx.yyy pid=12345
AllocHook : module .../libunity.so: plt=480 dyn=1240 patched=9
AllocHook : module .../libil2cpp.so: plt=610 dyn=890 patched=11
```

| 现象 | 含义 / 处理 |
|---|---|
| 一条都没有 | 托管代码被裁（4.2）或 .so 没进包（4.1 / 第 7.1 节） |
| `patched=0` / `no plt info` | 该库没走 PLT/GOT，换策略（FAQ 第 2、3 条） |
| 只有 `dir=` 没有 `module` 行 | 库已加载，但此刻 `libunity/libil2cpp` 还没加载完，进场景后再看 |

确认落盘文件：

```bash
adb shell run-as com.xxx.yyy ls -l allochook*
# allochook_init.txt / allochook_maps.txt（启动即有）
# allochook.txt / allochook.bin（15 秒后出现）
```

## 5. 已验证工程示例（lds_client）

`E:\Gitlab\lds_client` 已按下面的方式接入并通过验证，可作为模板。

### 5.1 改动清单（3 处）

| 文件 | 改动 |
|---|---|
| `Packages/manifest.json` | 加 `"com.elex.allochook": "file:E:/.../unity/com.elex.allochook"` |
| `Assets/Scripts/Game/GameMain.asmdef` | `references` 加 `"Elex.AllocHook"` |
| `Assets/Scripts/Game/Schedule/GameMain.cs` | `Awake()` 开头加 `Elex.AllocHook.AllocHookLoader.EnsureLoaded();` |

后两处是**防裁剪的显式引用**，见第 7.2 节。

### 5.2 已废弃的旧接入方式（不要再这样做）

早期为快速验证用过下面这套临时方案，现已全部删除：

| 旧做法 | 问题 |
|---|---|
| 手写 `Assets/.../AllocHookLoader.cs` | 与包内实现重复，升级易不一致 |
| 把 `.so` 拷到 `Export/Android/.../Append/.../jniLibs` | 依赖打包脚本拷贝时机，换渠道/清工程就失效 |
| `wrap.sh` + `LD_PRELOAD` | AGP 默认只打包 `.so`，`wrap.sh` 会被过滤掉 |
| 输出写到 `/data/data/<pkg>/files/` | 该目录不一定存在，`open` 直接 ENOENT，一个文件都写不出 |

### 5.3 回滚

删掉 `manifest.json` 那一行，并同步删掉 `GameMain.asmdef` 的引用和 `GameMain.Awake` 里的调用（三处要一起删，否则编译报错）。

## 6. 更早的 hook 时机（可选，但强烈建议）

默认路径（C# `RuntimeInitializeOnLoadMethod`）启动得太晚：Unity 引擎初始化、首场景加载期间提交的内存完全看不到，实测这部分能有 200~300MB。

包内已有两条更早的通道：

### 6.1 ContentProvider（推荐）

包里有一个预编译好的 `Plugins/Android/allochook_early.aar`，里面是 `AllocHookInitProvider`（静态块里 `System.loadLibrary("allochook")`）。Unity 会像普通 `.aar` 插件一样把它打进 APK，AGP 再把 provider 合并进最终 manifest。ContentProvider 在 **`Application.onCreate` 之前**实例化，因此 `.so` 的构造函数早于 Unity 引擎初始化。

- 不需要改任何游戏代码；
- 不需要额外配置；
- 与现有 C# 加载器共存（`System.loadLibrary` 幂等）。

> ⚠️ **千万不要用 `.androidlib` 目录形态**。Unity 导出 Gradle 工程时，会把包内的 `.androidlib` 写成
> `implementation project('xxx.androidlib')`，但**并不会真正导出这个模块**，导致 Gradle 直接失败：
>
> ```
> unityLibrary/build.gradle line: 88
> > compileSdkVersion is not specified. Please add it to build.gradle
> ```
>
> 必须用 `.aar`。源码在 `Android~/early/`（`~` 结尾的目录 Unity 不会导入），改完后重新编译：
>
> ```powershell
> powershell -ExecutionPolicy Bypass -File E:\Gitlab\UnityMemorySnapShot\tools\build-aar.ps1
> ```
>
> 脚本用 Unity 自带 OpenJDK 的 `javac`/`jar` 编译，不依赖 Android Studio。

> ⚠️ **钩子函数不能与 libc 同名（踩过的坑）**
>
> 早期版本把钩子直接命名为 `mmap` / `malloc` / `free`，结果 `.so` 内部的调用（包括编译器为
> `__thread` 生成的 `__emutls_get_address`）也被绑定到自己的钩子上：
>
> ```
> #01 __emutls_get_address+444   liballochook.so
> #02 recordEx(...)              liballochook.so
> #03 mmap+196                   liballochook.so
> ```
>
> `emutls` 分配 TLS 控制块时要 `malloc`，于是调到我们自己的 `malloc`，在 L2 关闭时直接拿到 NULL，
> 进程无征兆退出（无 tombstone、`appDiedLocked reason=null`）。表现为"运行一段时间后第一次分配就崩"，
> 很容易误判成"引擎初始化期间不能改 GOT"。
>
> 现在所有钩子都改名为 `hook_*`（`hook_mmap` / `hook_mprotect` / `hook_munmap` / `hook_malloc` / …），
> 本 DSO 内**不存在**任何 libc 同名符号，只通过 `.rela.plt` / `.rela.dyn` 改写非系统应用及插件 `.so` 的 GOT 槽位（故意跳过 `libc.so` 与系统/unwinder 库，避免 `dlopen` 或线程退出期间进入回溯导致死锁）。
>
> 另保留 `patch_delay_ms`（默认 `500`ms）与 `warmup_ms`（默认 `500`ms），给动态链接器留出完成重定位的时间；如果你的项目启动异常，可以调大：
>
> ```ini
> patch_delay_ms=3000
> warmup_ms=1500
> ```
>
> 💡 原生采集层内置**无锁 `.eh_frame_hdr` + 65,536 槽 Seqlock CFI 规则缓存回溯器**（`fastUnwindCore`）、64 分片零墓碑指针表 + 64KB 页快速跳过计数以及 `prctl` 线程名缓存，避免了 `_Unwind_Backtrace` 逐帧触发 `dl_iterate_phdr` 全局锁与 `/proc` 文件系统调用，因此在 `-systemallocator` 模式下即使把 `l2_min_bytes` 设成 `1024` 或 `256`、`l2_sample=1` 也能流畅进入游戏。

验证：

```bash
adb logcat -d | findstr AllocHook
# 期望顺序：AllocHook: dir=...（早期加载）→ 随后出现各 .so 的 module ... plt=... dyn=... patched=...
```

> 如果 logcat 里 AllocHook 仍然出现在 Unity 日志之后，说明 provider 没被合并：解包 APK 看 `AndroidManifest.xml` 里有没有 `<provider android:name="com.elex.allochook.early.AllocHookInitProvider">`，以及 `lib/` 下是否有 `liballochook.so`。

### 6.2 JDWP 注入（最早，但依赖 ROM）

Studio 侧边栏的 **「⚡ 注入并启动（最早 hook）」** 按钮走的是：

```
am start -D  (进程创建但停在等调试器)
  → adb forward jdwp:<pid>
  → JDWP: System.load(APK 自带 liballochook.so)
  → resume
```

优点：注入时**一行 App 代码都没执行**，连引擎初始化都覆盖；不需要 root、不需要重打包。
限制：

| 限制 | 说明 |
|---|---|
| 必须 debuggable | Development Build 满足 |
| ROM 是否允许 JDWP 方法调用 | **实测华为/HarmonyOS 直接拒绝**（所有线程 `INVALID_THREAD`），原生 Android 正常 |
| 需要 `extractNativeLibs=true` | 否则 `.so` 只是 APK 内条目，没有可 `load` 的绝对路径 |

Studio 会在失败时回传 `debug` 字段（ID 大小、线程数、失败原因），便于判断是该 ROM 的限制还是别的配置问题。

### 6.3 三档时机对比

| 方式 | 生效时刻 | 覆盖率 | 依赖 |
|---|---|---|---|
| C# 自动加载（默认） | 引擎初始化之后 | 看不到引擎初始化 | 无 |
| **ContentProvider** | `Application.onCreate` 之前 | 覆盖引擎初始化 | 无 |
| JDWP 注入 | 进程创建、未执行任何代码 | 最全 | ROM 允许方法调用 |

## 7. 接入踩坑清单（实测）

按"症状 → 原因 → 处理"排列，都是这套工具实际踩过的。

### 7.1 APK 里没有 `liballochook.so`

- **症状**：`4.1` 检查为空；Studio 点快照提示"设备上没有 dump 文件"。
- **原因**：`.so.meta` 里 `Android.enabled = 0`，只勾了 `Any Platform`（见第 2 节）。
- **处理**：`Tools → AllocHook → 修复插件平台设置`，或在 Inspector 手动勾 Android + 设 CPU；改完重新打包。
- **验证**：`Tools → AllocHook → 输出接入检查` 应打印
  `[AllocHook] OK arm64-v8a\liballochook.so (Android=on, CPU=ARM64)`；未勾选时会打印红色 `✗ ... 的 Android 平台未勾选`。

### 7.2 .so 在包里，但运行期一条 AllocHook 日志都没有

- **症状**：`4.1` 通过，`4.2` 为 `False`；应用私有目录只有 Studio 写的 `allochook.snap`，没有 `allochook_init.txt`。
- **原因**：`Elex.AllocHook` 程序集没有任何业务代码引用，`Managed Stripping Level = Medium/High` 时被 IL2CPP **整体删除**（连 `[RuntimeInitializeOnLoadMethod]` 一起）。
- **处理**（三件套，缺一不可）：
  1. 包内 `Runtime/AssemblyInfo.cs`：`[assembly: AlwaysLinkAssembly]`；
  2. `GameMain.asmdef` 的 `references` 加 `"Elex.AllocHook"`；
  3. `GameMain.Awake()` 里显式调用 `Elex.AllocHook.AllocHookLoader.EnsureLoaded();`。
- **说明**：`link.xml` 放在 UPM 包根目录**不保证**生效，别只靠它。
- **验证**：`4.2` 返回 `True`。

### 7.3 换了符号表，但旧快照还是解不出函数名

- **原因**：旧版本快照只存了符号化后的字符串，没存原始地址和 maps。
- **处理**：重新抓一份；新快照会保存 `原始地址 + <id>.maps`，之后可直接点列表里的 `↻` 重新解析，不用重抓。

### 7.4 `run-as` 失败

- **症状**：`adb shell run-as <pkg> ls` 报 `package not debuggable`。
- **原因**：包不是 debuggable。
- **处理**：打 **Development Build**（或手动 `android:debuggable="true"`）。

### 7.5 手工排查时被 `adb exec-out` 误导

- **现象**：`cat aaa.bin` 返回的其实是 `cat: aaa.bin: No such file or directory`（stderr 被合并进 stdout），看起来像"文件有内容但格式不对"。
- **说明**：Studio 已做识别（会提示"设备上没有 dump 文件"并附内容预览）；手工排查时留意这一点。

### 7.6 Studio 报"端口被占用 / 改了没生效"

- **原因**：上一次启动的 Studio 还占着 `7788` 端口并锁着 `bin/*.exe`（编译会报 `MSB3027 文件被锁定`）。
- **处理**：`run.ps1` 现在启动前会自动结束旧实例；手动清理：

```powershell
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' OR Name='AllocHookStudio.exe'" |
  Where-Object { $_.CommandLine -match 'AllocHookStudio' } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
```

### 7.7 页面右上角显示"服务端是旧版本"或服务端无响应

- **原因**：浏览器连的是旧进程（无 `/api/version`）或后台控制台进程已停止（页面每 15 秒通过心跳检测并在顶部提示）。另 Studio 已在启动时自动禁用 Windows 控制台 QuickEdit 模式，避免误点控制台导致日志输出挂起。
- **处理**：按 7.6 结束旧进程后重启 `run.ps1`；页面已强制 `no-store`，一般不需要手动刷缓存。

## 8. 可选：游戏内 GM 按钮

```csharp
using Elex.AllocHook;

AllocHookApi.Snapshot();   // 抓一次

AllocHookApi.Reset();      // 录制：先清零
// ... 做操作 ...
AllocHookApi.Snapshot();   // 得到区间增量
```

> 包是独立程序集，**调用方 asmdef 需要加 `Elex.AllocHook` 引用**（`GameMain.asmdef` / `HotFixProject.asmdef`）。

不接 GM 也可以：Studio 通过 `run-as` 往应用私有目录写 `allochook.snap` 控制文件，Native 侧 200ms 轮询触发，等价于上面两个调用。

## 9. 配置项

在 `/data/data/<pkg>/allochook.cfg` 写：

```ini
min_bytes=4096        # L1 忽略小于该值的提交，默认 4KB
max_frames=32         # L1 栈深度，默认 32
dump_interval=15      # 文本快照周期（秒）
dump_binary=1         # 是否写 allochook.bin
patch_delay_ms=500    # .so 加载后延迟打 GOT 补丁的毫秒数
warmup_ms=500         # 启动预热期（期间不做回溯）
l2=0                  # 是否开启 L2 malloc 家族钩子（网页“启动 systemallocator”自动设为 1）
l2_min_bytes=4096     # L2 抓取调用栈的字节阈值
l2_frames=16          # L2 栈深（1~32）
l2_sample=8           # L2 采样比例 1/N（1 = 全采）
```

也可以在 C# 里调用 `AllocHookLoader.WriteConfig("min_bytes=8192")` 写入，**重启应用生效**。

## 10. 不要带进正式包

- 本工具只用于开发/排查；正式包请移除 UPM 包（或至少不上传 `.so`）。
- 它会给应用与三方插件 `.so` 的 GOT 打补丁（L1 拦截 `mmap/mprotect/munmap`，`-systemallocator` 模式额外拦截 `malloc` 家族），没有理由留在线上。
- 移除包后如提示 `AllocHookApi` 找不到，说明还有 GM 代码在引用，按 5.3 一并删掉。

## 两种启动方式（网页按钮）

- **启动（线上口径）**：普通启动，只抓 L1 页级数据，不 hook `malloc`；数据可与线上包对比。
- **启动（systemallocator）**：自动写入 `l2=1` 并以 `-e unity -systemallocator` 启动，开启轻量 L2（`malloc` 持有者视图）。
  这会改变被测对象的分配路径（Unity 自有堆 → `libc malloc`），数据**不能与线上口径直接比较**，但能回答"按调用栈谁在持有"。

L2 默认 `l2_min_bytes=4096`、`l2_frames=16`（最大 `32`）、`l2_sample=8`（1/8 采样）。这些值已放到网页"启动"区域的 **L2 阈值(字节)** / **栈深** / **采样 1/N** 输入框，改完直接点启动即自动写入设备，并保存在浏览器本地（下次打开会恢复）。

被阈值过滤和采样跳过的**净存量（`申请 - 释放`，基于 `malloc_usable_size` 实测）**与累计申请量写在快照头里：在网页 **「Native 堆持有 (L2 malloc)」** 列表中，阈值以下净存量会直接作为无栈行参与字节排序，并在 **「PSS 对账」** 展开 `malloc` 时置顶展示。需要更细定位时可调小阈值或把采样设为 `1`（如 `1024` / `1` 或 `256` / `1`）。

也可以绕过网页，直接在设备的 `allochook.cfg` 里写：

```ini
l2=1
l2_min_bytes=1024
l2_frames=16
l2_sample=1
```
