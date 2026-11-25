# 数据格式

## 1. `allochook.bin`（主格式，小端）

```
偏移        类型        内容
0x00       char[4]     "AHK1"
0x04       uint32      version = 3（兼容解析 v1 / v2）
0x08       uint32      headerLen
0x0C       byte[]      headerJson（UTF-8，长度 headerLen）
...        v3 字典与记录（见下）
```

`headerJson`：

```json
{
  "pid": 12345,
  "ts": 1763541234,
  "level": "pages+l2",
  "maxFrames": 32,
  "minBytes": 4096,
  "holdMin": 256,
  "holdFrames": 10,
  "l2Enabled": 1,
  "l2Min": 1024,
  "l2Sample": 1,
  "l2FilteredBytes": 329448834,
  "l2FilteredCount": 4844078,
  "l2SampledBytes": 0,
  "l2FilteredLiveBytes": 58313392,
  "l2FilteredLiveCount": 885556,
  "l2SampledLiveBytes": 0,
  "l2SampledLiveCount": 0,
  "nativeHeapAllocBytes": 566548632,
  "mods": [
    { "p": "/data/app/.../lib/arm64/libunity.so", "b": "0x7727f8600000" },
    { "p": "/data/app/.../lib/arm64/libil2cpp.so", "b": "0x771d0a0000" }
  ]
}
```

`mods` 是快照时刻各模块 `dlpi_addr`（用于参考，实际符号化优先按 `allochook_maps.txt` + 本地 ELF `PT_LOAD` 重建精确 `load_bias`）。

### 记录类型（kind）

| kind | 含义 | `bytes` | `count` | `aux` |
|---:|---|---|---|---|
| 0 | 预留（`mmap PROT_NONE`） | 预留字节 | 次数 | 0 |
| 1 | 提交（可写/匿名提交页；只读文件映射已过滤） | 提交字节 | 次数 | 0 |
| 2 | 归还（`munmap` 已提交页，或 `mprotect` 改回 `PROT_NONE`） | 归还字节 | 次数 | 0 |
| 3 | ~~L2 malloc~~（已废弃） | 当前存活字节 | 分配次数 | 已释放字节 |
| 4 | **L1 存量**（当前仍处于已提交状态的页） | 当前已提交字节 | 区间段数 | 0 |
| 5 | **PSS 分桶**（`smaps Pss + SwapPss`） | Pss 字节 | VMA 数 | 所属主分类枚举（`0=anon`..`11=file-mmap`） |
| 6 | **持有者**（轻量 L2，`malloc` 家族） | 当前存活字节 | 分配次数 | 已释放字节 |

> **L2 过滤与净存量统计**（写在 bin 头里）：
> - `l2FilteredBytes` / `l2FilteredCount`：低于 `l2_min_bytes`（网页默认 `4096`）的累计申请，只累加字节、不抓栈；
> - `l2FilteredLiveBytes` / `l2FilteredLiveCount`：基于 `malloc_usable_size` 跟踪的 `< l2_min_bytes` **净存量（申请 − 释放）**；
> - `l2SampledBytes` / `l2SampledLiveBytes` / `l2SampledLiveCount`：1/N 采样跳过分配的累计字节与实测净存量；
> - `nativeHeapAllocBytes`：快照时刻进程内 `mallinfo().uordblks`。
>
> **为什么有了 L1 还要 kind=6**：在 `-systemallocator` 模式下，L1（`mmap/mprotect`）主要反映 Boehm GC、Baselib 等直接向内核申请的 `anon` 页；而 L2（`malloc` 家族）追踪 `Native Heap`（`[anon:libc_malloc]`）。两者统计的是**互补且互不重叠的 PSS 分区**（不是 `L2 ⊂ L1`），L2 通过 `ptr → 栈` 给出 `Native Heap` 的真实持有者。

`kind=5`（PSS 分桶）主分类枚举与 `nf == 0` 时的 `tname` 约定：
- 主分类（`0..11`）：`0=anon`、`1=malloc`、`2=code`、`3=graphics`、`4=java`、`5=shared`、`6=stack`、`7=bss`、`8=jit-cache`、`9=ashmem`、`10=memfd`、`11=file-mmap`；
- 细化子分桶（`nf == 0` 时写在 `tname` 中，格式 `<主分类>:<子项>`）：
  - `code:libil2cpp.so (<perms>)`、`code:libunity.so (<perms>)`、`code:<plugin>.so`、`code:<apk/jar>`、`code:font (.ttf/.ttc)`、`code:oat/dex/vdex`
  - `java:Dalvik堆`、`java:dex`、`java:LinearAlloc`、`java:art`、`java:其他Java/ART`
  - `file-mmap:<文件名>`（如 `file-mmap:global-metadata.dat`、`file-mmap:mscorlib.dll-resources.dat`）

口径约定（重要）：

- **1 − 2 = 窗口内净增长**；0（预留）不计入。
- **6（L2）与 1/2/4（L1）在 `-systemallocator` 下分别对应 `Native Heap (malloc)` 与直接 `mmap/mprotect` 的 `anon`/GC 分区，永不相加**。
- **4（存量）** 是"当前仍处于已提交状态的页"，按最后提交它的调用栈归属。
- **5（PSS）** 全部记录之和 == 进程 `smaps` 总 PSS（含 `Pss + SwapPss`）；有调用栈的是命中了 L1 分配区间的（跨 VMA 重叠区间按字节占比拆分归因，`aux` 记录主分类 ID），`nf == 0` 的按 `tname`（主分类或 `<主分类>:<子项>`）归桶；再加上 `dumpsys meminfo` 的 `EGL mtrack + GL mtrack` 即逼近 `TOTAL PSS`。

每条 `record`（`version = 3` 载荷顺序：线程字符串表 → 去重 PC 字典 → 记录数组）：

```
线程字符串表：
  uint32  threadCount
  repeat threadCount:
    uint8   nameLen
    byte[]  nameBytes   // 线程名或 PSS 主分类/子桶名

去重 PC 字典：
  uint32  pcCount
  uint64  pcs[pcCount]  // 去重后的绝对虚拟地址

每条 record（v3）：
  uint8   kind          // 0=预留  1=提交  2=归还  4=L1存量  5=PSS分桶  6=L2持有
  uint16  threadIdx     // 线程字符串表下标
  uint16  nframes
  uint64  bytes         // 该栈累计/驻留字节数（同一条 PC 栈已聚合）
  uint64  count         // 出现次数 / VMA 数 / 区间段数
  uint64  aux           // kind=6 为已释放字节；kind=5 有栈记录为 VMA 主分类 ID；其它为 0
  uint32  pcIdx[nframes]// PC 字典下标，pcIdx[0] 最靠近分配点
```

（`version = 2` 旧格式在每条记录内直接携带 `uint8 nameLen + thread + ... + uint64 pcs[nframes]`，`DumpParser` 自动兼容解析。）

### 解包

```powershell
dotnet studio\bin\Debug\net8.0\AllocHookStudio.dll --unpack allochook.bin allochook.json
```

输出 JSON 里每个 record 带 `kind / thread / bytes / count / pcs`，`pcs` 为十六进制绝对地址。由于 Android 16KB 页对齐 ELF 的代码段可能存在 `p_vaddr != p_offset`（例如 `+0x4000`），且进程内可能同时存在整文件只读映射，不能简单用 `pc - (start - offset)` 换算，Studio 会结合 `allochook_maps.txt` 与本地 ELF 的 `PT_LOAD` 节表自动匹配加载实例与精确 `load_bias` 后再做 `addr2line` 与 `BL/BLR` 校验。

## 2. `allochook.txt`（兼容/肉眼查看）

```
kind thread nframes bytes count pc0 pc1 pc2 ...
0    UnityMain_ 12 524284 1 7727f8635c 7727f7fb90 ...
```

字段与 bin（除 `aux` 外）一一对应，地址为十六进制。

## 3. `allochook_maps.txt`

首行记录 `# allochook pid=<pid> ts=<ts>`，随后为 `/proc/self/maps` 原样 dump，用于：
- 校验 `maps` 与 `bin` 来自同一进程；
- 按加载实例聚类并结合 ELF `PT_LOAD` 精确计算 `load_bias`；
- 识别匿名区（`(anon)`）与文件映射。

## 4. `allochook_init.txt`

只有一行，写入成功即代表钩子初始化成功：

```
pid=12345 dir=/data/data/com.xxx
```

## 5. 快照产物（Studio）

```
studio/data/snapshots/
  20261124_160326.json    # Snapshot 结构：id/time/pid/package/mode/pss/l2* 统计/stacks[]
  20261124_160326.md      # 报告：dumpsys meminfo、L1/L2 存量、PSS 分层对账、业务模块分类、Top 30 栈
  20261124_160326.maps    # 当次 /proc/self/maps（供列表中点 ↻ 重新符号化）
```

`stacks[]` 里每项：

```json
{
  "kind": 6,
  "layer": 2,
  "thread": "UnityMain",
  "bytes": 33554463,
  "count": 4,
  "aux": 29360221,
  "suspect": "",
  "label": "",
  "pcs": [512334892892, "..."],
  "frames": ["libunity.so!MemoryManager::LowLevelAllocate(unsigned long, unsigned long)", "..."]
}
```

`frames` 已符号化（并经过 `BL/BLR` 返回指令校验，不可信帧保留 `<so>+0x... [未验证]`），`pcs` 为原始虚拟地址（支持换符号表后原地 `↻` 重解）。
