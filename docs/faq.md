# FAQ / 排错

## 1. Studio 点快照后报“没拿到 allochook.bin”

按顺序查：

1. **包里有 .so 吗**
   ```bash
   adb shell pm path com.xxx.yyy
   adb pull <base.apk> app.apk
   unzip -l app.apk | findstr allochook
   ```
   应该有 `lib/arm64-v8a/liballochook.so`。

2. **加载成功了吗**
   ```bash
   adb logcat -s AllocHook
   ```
   期望：
   ```
   AllocHook: dir=/data/data/com.xxx.yyy pid=12345
   AllocHook: module .../libunity.so: plt=480 dyn=1240 patched=9
   ```
   完全没有 `AllocHook:` → 加载失败（上面会有 `load failed: ...`）。

3. **run-as 能用吗**（Studio 靠它写控制文件 / 读 dump）
   ```bash
   adb shell run-as com.xxx.yyy ls
   ```
   报 `run-as: package not debuggable` → 包不是 debuggable，**必须打 Development Build**（或手动 `android:debuggable="true"`）。

## 2. `patched=0` 或 `no plt info`

说明目标 `.so` 里没有 `mmap`/`malloc` 的 `.rela.plt`（`DT_JMPREL`）或 `.rela.dyn`（`GLOB_DAT`/`ABS64`）重定位，GOT 注入失败。

- 确认 `libunity.so` / `libil2cpp.so` 行都出现了。只有其中一个时，另一个库没加载（比如启动极早期 `libil2cpp` 尚未加载），属正常（看门狗会在后台轮询补丁）。
- 如果两个都是 `patched=0`，说明该 Unity 版本把 `mmap` 调用内联/syscall 化了，需要改用 `LD_PRELOAD`（wrap.sh）方式，见下。

## 3. 想改用 LD_PRELOAD（wrap.sh）

`.so` 放进 APK 的 `lib/<abi>/` 后，在 `lib/<abi>/wrap.sh` 写：

```sh
#!/system/bin/sh
export LD_PRELOAD=liballochook.so
exec "$@"
```

要求：`android:extractNativeLibs="true"` + debuggable。**注意 AGP 默认只打包 `.so`**，`wrap.sh` 需要自己注入 APK 或改 gradle。

## 4. 符号解不出来，全是 `libunity.so+0x...` 或 `[未验证]`

Unity 安装目录 `Variations/il2cpp/*/Symbols/libunity.sym.so` **不是**你这次打包的二进制。

正确来源：`Build/export_project/<渠道>/unityLibrary/symbols/<abi>/libunity.so`（Unity 导出 Gradle 工程时自动生成，`.text` 是 NOBITS，只有符号）。Studio 还会自动同目录查找 `unityLibrary/src/main/jniLibs/<abi>/` 下的实体 `.so`，用于校验 16KB 页对齐 ELF（`p_vaddr - p_offset = +0x4000`）的加载基址及返回指令 `BL/BLR` 合法性。

校验方法：

```powershell
# 两边的 build-id 必须一致
llvm-readelf -n <apk里的 libunity.so> | findstr "Build ID"
llvm-readelf -n <symbols/libunity.so>  | findstr "Build ID"
```

## 5. 为什么不用 heapprofd / malloc_debug

它们只 hook libc 的 `malloc/free`。Unity 的 `DynamicHeapAllocator` 和 Boehm GC 默认直接调 `mmap`，完全绕过 libc，所以那些工具看不到。

本工具 L1 hook 非 `libc.so` 模块的 `mmap/mprotect/munmap`，正好覆盖它们；在 `-systemallocator` 模式下同时通过 L2 hook `malloc` 家族覆盖 `Native Heap`。

## 6. 数据量会不会太大

L1（页级）极小：通常只有几十个聚合栈。

L2（`malloc` 级）由原生层无锁 CFI 缓存回溯器与 64 分片零墓碑哈希表在进程内直接聚合（并支持 64KB 页原子计数快速跳过小对象 `free`），Studio 展示时会自动按 `(线程, 符号栈)` 合并重复项、默认隐藏 `存活 = 0` 的已释放栈并分页渲染。

> 注意：`⏺ 开始录制 / 停止并快照` 会重置计数以对比窗口净增量（仅作用于增量事件，L1 存量 `K_LIVE` 与 L2 持有始终为当前存活视图）；若要对比两份状态，推荐直接抓两次 **Snapshot** 后用 **Diff** 对比。

## 7. 线程名是 `-`

L1 读 `/proc/self/task/<tid>/comm`、L2 调 `prctl(PR_GET_NAME)`（每 512 次刷新一次），读取失败时回退为 `-`。常见于极早期或受限上下文，不影响归属分析。

## 8. 快照里出现 `(bad-frame)` / `(anon)` / `[未验证]`

- `(anon)`：该地址落在匿名映射里（不是任何 `.so`），通常是 JIT/GC/栈。
- `(bad-frame)`：计算得到的模块内偏移越界，已忽略。
- `[未验证]`：从第 1 帧起，Studio 会检查本地 `.so` 中该返回地址前一条指令是否为 `BL/BLR`；若不是，说明是 unwinder 在缺失 CFI 时的猜测帧，只保留 `<so>+0x... [未验证]`，开启「跳过底层分配/容器帧」时也会自动跨过未验证帧与底层噪声帧。

## 9. 分类规则在哪改

界面左侧 **「5. 分类规则（饼图 / 报告）」** 直接编辑「分类名 + 正则」，点「保存规则」即写入 `studio/data/config.json` 并立刻对当前快照重新归类，不需要重启服务。

规则统计口径为**全量存活内存（`L1 存量 + L2 持有 + 阈值以下净存量`）**：匹配前会先过滤底层分配器/容器帧和 `UpdateFacade` / `NetFacade_Update` / `PlayerLoop` 等外层主循环分发帧，再按**顺序**匹配剩余业务调用栈文本，命中第一条就停止；都不命中归为 `Other`。常用写法：

| 分类 | 正则 |
|---|---|
| 某个业务模块 | `Mail\|ECMail\|Chat` |
| Meta 配置 | `MetaManager\|MetaBase\|ConfigFormatter` |
| 引擎渲染 | `Camera::\|RenderManager\|ShaderLab\|GfxDevice\|ParticleSystem` |
| 托管 GC | `String_FastAllocateString\|List_1_\|GC_\|Boehm\|GarbageCollector` |
| 第三方 SDK | `Firebase\|Crashlytics\|Adjust\|Bugly\|UWA\|sqlite3` |

若想调整「跳过底层分配/容器帧」的过滤范围，可直接编辑 `studio/data/config.json` 中的 `IgnoreFramePatterns` 字段。

## 10. 如何与 PSS 对账

原生采集层每次快照都会直接解析进程内 `/proc/self/smaps`（`Pss + SwapPss`）并联合 `dumpsys meminfo`：

- **L1（`anon` 等）**：命中 L1 提交区间表的页会按重叠字节占比拆分归因到对应提交栈，未命中部分落入 `anon` 残余桶；
- **L2（`malloc` / `Native Heap`）**：在 **PSS 对账** 中展开 `malloc` 时，顶部会先列出**阈值以下净存量**、**采样跳过净存量**与**未归因 / Scudo 碎片残余**，下面紧跟按业务帧聚合后的 L2 持有栈；
- **系统/文件分桶**：`code`（可展开到 `libil2cpp.so` / `libunity.so` 各权限段与插件 `.so`）、`java`（`Dalvik堆` / `dex` / `LinearAlloc` / `art`）、`file-mmap`（如 `global-metadata.dat`）、`bss`、`graphics`、`stack`、`shared`、`jit-cache`、`ashmem`/`memfd` 逐项累加，**全部 `smaps` 分区之和再加上 `EGL mtrack + GL mtrack` 即可直接与 `dumpsys TOTAL PSS` 对账**。
