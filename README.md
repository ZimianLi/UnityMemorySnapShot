# UnityMemorySnapShot

Unity 原生内存快照工具链：**按调用栈归因**的 native 内存分析工具，用来还原 Android 运行时进程 PSS 细节，回答"这几百 MB 到底是谁申请、谁持有的"。

它不依赖 Unity 的 Memory Profiler（那条路在 `Native > Unity Subsystems > Unrooted` 就断了），而是在非系统 `.so` 的 `mmap / mprotect / munmap`（L1 页级）与 `malloc` 家族（L2，`-systemallocator` 模式）上挂钩子抓调用栈，结合进程内 `/proc/self/smaps` 逐 VMA 精确对齐 `PSS`，再用 Unity 打包时产出的**匹配符号**还原到 `类名_方法名_m<hash>`。

## 组成

```
native/                   C++ 采集层（源码 + CMake + 一键编译脚本）
unity/com.elex.allochook  UPM 包：加载器 + ContentProvider 早期注入 + 预编译 .so + 打包前检查
studio/                   AllocHookStudio：本地 Web 界面（安装/启动/快照/符号化/堆栈归因/饼图/PSS 对账/Diff）
docs/                     接入说明、数据格式、FAQ
```

## 三步上手

```powershell
# 1) 编 native（需要 Android NDK；会自动拷到 UPM 包的 Plugins/Android 下）
powershell -ExecutionPolicy Bypass -File native\build.ps1

# 2) 启动 Studio（首次会自动 dotnet build，然后打开浏览器）
powershell -ExecutionPolicy Bypass -File studio\run.ps1
```

3) 在网页里：填包名 / APK 目录 → 安装 → 启动 → 点 **📷 Snapshot**。

Unity 工程侧的接入见 [docs/integration.md](docs/integration.md)。

## 采集到的是什么

| 通道 | hook / 数据源 | 覆盖内容 | 对应 PSS 分区 |
|---|---|---|---|
| **L1（直接 mmap / GC）** | `mmap` / `mmap64` / `mprotect` / `munmap`（非 `libc.so` 的应用与插件 `.so`） | IL2CPP Boehm GC 托管堆、`Baselib` / 线性分配器、普通包下的 Unity `DynamicHeapAllocator` arena（已自动过滤只读文件映射） | `anon` 等（与 L2 互补不重叠） |
| **L2（Native 堆持有）** | `malloc` / `calloc` / `realloc` / `memalign` / `posix_memalign` / `aligned_alloc` / `free`（仅 `-systemallocator` 开启） | `libc` 堆上当前存活对象（含 `< l2_min_bytes` 与采样跳过的**净存量**统计，释放只按指针扣减） | `malloc`（`Native Heap`） |
| **PSS 分层对账** | 进程内 `/proc/self/smaps`（`Pss + SwapPss`） + `dumpsys meminfo` | 命中 L1 区间的按重叠比例归因到提交栈；其余按路径细分 `malloc`、`anon`、`code`（精确到 `libil2cpp.so`/`libunity.so` 段权限与各插件 `.so`）、`java`、`file-mmap`（如 `global-metadata.dat`）、`bss`、`graphics`、`stack`、`shared`、`jit-cache`、`ashmem`/`memfd`；并联合 `EGL/GL mtrack` 与 `TOTAL PSS` 对账 | 全进程 PSS |

看不到的底层/系统细节（例如 GPU `EGL/GL mtrack` 驱动内存、未进 L2 堆栈的 `< l2_min_bytes` 小对象或系统共享页）会在 **PSS 对账** 和 **Native 堆持有** 中单独列明净存量/分桶，不留黑盒。

## 和 Unity Memory Profiler 的分工

| 问题 | 用谁 |
|---|---|
| 哪个 C# 对象被谁引用、为什么没释放 | Memory Profiler（托管对象图） |
| 这块 native 内存是谁申请的、走的哪条渲染/加载路径、在 PSS 中占多少 | **本工具** |

## 界面视图与可配置项

- **分配堆栈**：展示两个互补的存量视图——**直接 mmap / GC (L1)** 与 **Native 堆持有 (L2 malloc)**；支持自动跳过底层分配器/STL 容器/IL2CPP 泛型元数据包装帧直接显示业务归因帧、按 `(线程, 符号栈)` 自动合并同栈记录、默认隐藏 `存活 = 0` 的已释放栈，并把 `< 阈值` 小对象净存量直接并入 L2 列表排序。
- **模块饼图**：按 **全量已抓取存活内存（`L1 存量 + L2 持有 + 阈值以下净存量`）** 统计，匹配前自动剥离底层分配器与 `UpdateFacade` / `PlayerLoop` 等外层主循环分发帧，按左侧第 5 块「分类规则」汇总到业务模块；修改保存后写入 `studio/data/config.json` 并立即重算。
- **PSS 对账**：以可展开的分层视图展示进程内 `smaps Pss + SwapPss` 与 `GPU mtrack`，展开 `malloc` / `anon` 时将**阈值以下净存量**与**残余**置顶，`code` / `java` / `file-mmap` 可展开查看具体 `.so` / `.dat` 文件明细。
- **底层帧过滤规则**：`studio/data/config.json` 中的 `IgnoreFramePatterns` 控制「跳过底层分配/容器帧」要穿透的包装函数，可按项目自定义扩展。
- **ECharts 走本地**：`studio/wwwroot/lib/echarts.min.js` 已内置（v5.4.3，约 1MB），**不访问外网**；即使这个文件被删掉，界面也会自动降级成内置的 Canvas 饼图，功能不受影响。

## 输出产物

每次快照在 `studio/data/snapshots/` 下生成三份：

- `<id>.json` —— 全量结构化快照（栈 + 字节 + 线程 + 原始 PC + `dumpsys meminfo`），可直接喂给 AI
- `<id>.md` —— 人读/AI 读的报告（`dumpsys meminfo`、L1/L2 存量、PSS 分层对账、模块分类、Top 30 业务栈）
- `<id>.maps` —— 快照时刻 `/proc/self/maps`（供之后点击列表 `↻` 用新符号表重新解析）
- 设备端原始 `allochook.bin` 可用 `dotnet studio\bin\Debug\net8.0\AllocHookStudio.dll --unpack <bin> [out.json]` 解包查看

格式说明见 [docs/data-format.md](docs/data-format.md)。
