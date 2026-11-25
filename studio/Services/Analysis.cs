using System.Text;
using System.Text.RegularExpressions;

namespace AllocHookStudio;

public static class Classifier
{
    private static readonly Regex OuterLoopRegex = new(
        @"UpdateFacade|FrameworkUpdateFacade|GameMain_Update|NetFacade_Update|CommunicationManager_Update|ApplicationFacade_Notify|ScriptingInvocation|MonoBehaviour::CallUpdateMethod|BaseBehaviourManager|BehaviourManager::Update|PlayerLoop|libart\.so|Action(_\d+)?_Invoke|VirtualFuncInvoker|VirtualActionInvoker|InterfaceFuncInvoker|InterfaceActionInvoker",
        RegexOptions.Compiled);

    // 饼图归类只剥离底层分配器/容器/循环分发帧，保留 IL2CPP 元数据/反射与 GC 帧，
    // 防止无上层 C# 业务类的启动元数据分配落空到 UnityInitApplication / Other。
    private static readonly Regex PieNoiseRegex = new(
        @"MemoryManager::|UnityDefaultAllocator<|DynamicHeapAllocator|DualThreadAllocator<|TLSAllocator<|BucketAllocator|StackAllocator<|ThreadsafeLinearAllocator<|LocalLowLevelAllocator|baselib::UnityClassic::detail::|MemoryPool|il2cpp::utils::Memory::|il2cpp::utils::MemoryPool::|std::__ndk1::|core::vector_detail::|core::(?:vector|hash_set|base_hash_map|hash_map|list|StringStorageDefault|basic_string|function_ref|pair)<|core::pair<core::hash_set<|dynamic_array<|dynamic_block_array<|vector_map<|stl_allocator<|google::(?:libc_allocator_with_realloc|sparsegroup|dense_hash|sparse_hash)|il2cpp::utils::collections::|StreamedBinaryRead::TransferSTLStyleArray<|TransferPPtr<|\boperator new\b|(?:^|!)(?:malloc|calloc|realloc|free|memalign|posix_memalign|aligned_alloc)(?:\(|$)|Baselib_Memory_|unwindstack::|StackUnwinder::|ResolveUsingLibUnwind|liballochook\.so|libc\.so|\bUNITY_ft_mem_",
        RegexOptions.Compiled);

    /// <summary>
    /// 按业务规则对"全量存活内存"归类：
    /// L1 存量（Kind=4） + L2 持有（Kind=6，采样模式下按 L2Sample 估算） + 阈值以下净存量。
    /// 匹配前剔除底层分配器/容器与外层主循环分发帧，使模块归属落到真实业务层；
    /// 仅旧版快照（既无 Kind=4 也无 Kind=6）回退到 L1 提交 − 归还净额。
    /// </summary>
    public static Dictionary<string, long> Categorize(Snapshot snap, List<CategoryRule> rules, List<string>? ignorePatterns = null)
    {
        var compiled = rules.Select(r => (r.Name, Re: SafeRegex(r.Pattern))).ToList();
        var result = new Dictionary<string, long>();

        bool hasLiveOrHold = snap.Stacks.Any(s => s.Kind == 4 || s.Kind == 6);
        if (!hasLiveOrHold)
            return CategorizeDeltaFallback(snap.Stacks, compiled);

        int sample = Math.Max(snap.L2Sample, 1);

        foreach (var s in snap.Stacks)
        {
            if (s.Kind != 4 && s.Kind != 6) continue;
            if (s.Bytes <= 0) continue;
            long val = (s.Kind == 6 && sample > 1) ? s.Bytes * sample : s.Bytes;
            string cat = MatchStackCategory(s.Frames, compiled);
            result[cat] = result.GetValueOrDefault(cat) + val;
        }

        long smallLive = snap.L2FilteredLiveBytes;
        if (sample <= 1 && snap.L2SampledLiveBytes > 0)
            smallLive += snap.L2SampledLiveBytes;
        if (smallLive > 0)
            result["小对象/阈值以下(无栈)"] = result.GetValueOrDefault("小对象/阈值以下(无栈)") + smallLive;

        return result;
    }

    /// <summary> 兼容重载 </summary>
    public static Dictionary<string, long> Categorize(IEnumerable<StackRecord> stacks, List<CategoryRule> rules)
    {
        var snap = new Snapshot { Stacks = stacks as List<StackRecord> ?? stacks.ToList(), L2Sample = 1 };
        return Categorize(snap, rules, null);
    }

    private static Dictionary<string, long> CategorizeDeltaFallback(
        IEnumerable<StackRecord> stacks,
        List<(string Name, Regex? Re)> compiled)
    {
        var net = new Dictionary<string, (StackRecord Sample, long Bytes)>();
        foreach (var s in stacks)
        {
            if (s.Kind != 1 && s.Kind != 2) continue;
            long delta = s.Kind == 1 ? s.Bytes : -s.Bytes;
            string key = string.Join("\u0001", s.Frames);
            net[key] = net.TryGetValue(key, out var cur)
                ? (cur.Sample, cur.Bytes + delta)
                : (s, delta);
        }

        var result = new Dictionary<string, long>();
        foreach (var kv in net.Values)
        {
            if (kv.Bytes <= 0) continue;
            string cat = MatchStackCategory(kv.Sample.Frames, compiled);
            result[cat] = result.GetValueOrDefault(cat) + kv.Bytes;
        }
        return result;
    }

    public static List<(string Name, Regex? Re)> CompileRules(IEnumerable<CategoryRule> rules)
        => rules.Select(r => (r.Name, Re: SafeRegex(r.Pattern))).ToList();

    public static string MatchStackCategory(IReadOnlyList<string>? frames, List<(string Name, Regex? Re)> compiled)
    {
        if (frames == null || frames.Count == 0) return "Other";
        var filtered = new List<string>(frames.Count);
        for (int i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            if (string.IsNullOrWhiteSpace(f)) continue;
            if (f.EndsWith("[未验证]", StringComparison.Ordinal) || f == "(anon)" || f == "(bad-frame)") continue;
            if (OuterLoopRegex.IsMatch(f) || PieNoiseRegex.IsMatch(f)) continue;
            filtered.Add(f);
        }

        var target = filtered.Count > 0 ? (IEnumerable<string>)filtered : frames;
        string joined = string.Join(";", target);
        foreach (var (name, re) in compiled)
        {
            if (re != null && re.IsMatch(joined)) return name;
        }
        return "Other";
    }

    private static Regex? SafeRegex(string pattern)
    {
        try { return new Regex(pattern, RegexOptions.Compiled); } catch { return null; }
    }
}

public static class Report
{
    public sealed record EffectiveFrame(string Frame, int Index, int SkippedCount);

    private static readonly string[] PssCategoryNames =
    {
        "anon", "malloc", "code", "graphics", "java", "shared",
        "stack", "bss", "jit-cache", "ashmem", "memfd", "file-mmap"
    };

    public static string PssCategoryOf(StackRecord s)
    {
        if (!string.IsNullOrEmpty(s.Label))
        {
            int colon = s.Label.IndexOf(':');
            return colon > 0 ? s.Label[..colon] : s.Label;
        }
        if (s.Frames.Count == 0) return string.IsNullOrEmpty(s.Thread) ? "anon" : s.Thread;
        return (s.Aux >= 0 && s.Aux < PssCategoryNames.Length) ? PssCategoryNames[s.Aux] : "anon";
    }

    public static string? PssSubLabelOf(StackRecord s)
    {
        if (string.IsNullOrEmpty(s.Label)) return null;
        int colon = s.Label.IndexOf(':');
        return (colon > 0 && colon + 1 < s.Label.Length) ? s.Label[(colon + 1)..] : null;
    }

    public static List<Regex> CompileIgnorePatterns(IEnumerable<string>? patterns)
    {
        var list = new List<Regex>();
        foreach (var p in patterns ?? AppConfig.DefaultIgnoreFramePatterns())
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            try { list.Add(new Regex(p, RegexOptions.Compiled)); } catch { }
        }
        return list;
    }

    public static EffectiveFrame PickEffectiveFrame(IReadOnlyList<string>? frames, IReadOnlyList<Regex>? ignoreRegexes = null)
    {
        if (frames == null || frames.Count == 0) return new EffectiveFrame("-", 0, 0);
        var regexes = ignoreRegexes ?? CompileIgnorePatterns(null);
        for (int i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            if (string.IsNullOrWhiteSpace(f)) continue;
            if (f.EndsWith("[未验证]", StringComparison.Ordinal) || f == "(anon)" || f == "(bad-frame)") continue;
            bool ignored = false;
            for (int r = 0; r < regexes.Count; r++)
            {
                if (regexes[r].IsMatch(f)) { ignored = true; break; }
            }
            if (!ignored) return new EffectiveFrame(f, i, i);
        }
        return new EffectiveFrame(frames[0], 0, 0);
    }

    private static List<StackRecord> MergeByThreadAndFrames(IEnumerable<StackRecord> source, bool isL2 = false)
    {
        var map = new Dictionary<string, StackRecord>();
        foreach (var s in source)
        {
            string key = $"{s.Kind}\u0002{s.Thread}\u0002{string.Join("\u0001", s.Frames)}";
            if (!map.TryGetValue(key, out var cur))
            {
                map[key] = new StackRecord
                {
                    Kind = s.Kind,
                    Layer = s.Layer,
                    Thread = s.Thread,
                    Bytes = s.Bytes,
                    Count = s.Count,
                    Aux = s.Aux,
                    Suspect = s.Suspect,
                    Label = s.Label,
                    Frames = s.Frames,
                    Pcs = s.Pcs,
                };
            }
            else
            {
                cur.Bytes += s.Bytes;
                cur.Count += s.Count;
                cur.Aux += s.Aux;
            }
        }
        if (isL2)
        {
            foreach (var r in map.Values)
                r.Suspect = (r.Aux > 0 && r.Aux >= Math.Max(r.Bytes, 1) * 2) ? "pool" : "";
        }
        return map.Values.ToList();
    }

    public static string Build(Snapshot snap, List<CategoryRule> rules, List<string>? ignorePatterns = null)
    {
        var ignoreRes = CompileIgnorePatterns(ignorePatterns);
        var sb = new StringBuilder();
        sb.AppendLine($"# Native Memory Snapshot `{snap.Id}`");
        sb.AppendLine();
        sb.AppendLine($"- 时间：{snap.Time:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- 包名：{snap.Package}");
        sb.AppendLine($"- 设备：{snap.Device}");
        sb.AppendLine($"- 模式：{snap.Mode}");
        sb.AppendLine($"- pid：{snap.Pid}");
        if (snap.Pss.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## dumpsys meminfo（KB）");
            sb.AppendLine();
            sb.AppendLine("| 项 | 值 |");
            sb.AppendLine("|---|---:|");
            foreach (var kv in snap.Pss.OrderByDescending(k => k.Value))
                sb.AppendLine($"| {kv.Key} | {kv.Value:N0} |");
        }

        sb.AppendLine();
        sb.AppendLine("## 内存状态汇总");
        sb.AppendLine();
        long commit = snap.Stacks.Where(s => s.Kind == 1).Sum(s => s.Bytes);
        long release = snap.Stacks.Where(s => s.Kind == 2).Sum(s => s.Bytes);
        long reserve = snap.Stacks.Where(s => s.Kind == 0).Sum(s => s.Bytes);
        sb.AppendLine("| 语义 | 字节 | 说明 |");
        sb.AppendLine("|---|---:|---|");
        sb.AppendLine($"| 提交 | {commit:N0} | 真正占用物理内存（mmap 带读写权限 / mprotect 提交页） |");
        sb.AppendLine($"| 归还 | {release:N0} | munmap 释放的**已提交**页，或 mprotect 改回 PROT_NONE |");
        sb.AppendLine($"| **净提交** | **{commit - release:N0}** | 提交 − 归还，等于本次快照期间的净增长 |");
        sb.AppendLine($"| 预留 | {reserve:N0} | mmap PROT_NONE，只占地址空间，不计入 PSS |");

        var liveRecs = MergeByThreadAndFrames(snap.Stacks.Where(s => s.Kind == 4));
        if (liveRecs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## L1 存量（直接 mmap/mprotect 的匿名/GC 页，不含 libc malloc）");
            sb.AppendLine();
            sb.AppendLine($"- 合计 {liveRecs.Sum(s => s.Bytes):N0} B，共 {liveRecs.Sum(s => s.Count):N0} 段");
            sb.AppendLine("- 归因对象是\"最后提交这块页的调用栈\"（Boehm GC / Baselib 等直接 mmap 区），与 L2（`Native Heap` / `malloc`）互不重叠");
            sb.AppendLine();
            sb.AppendLine("| 业务提交点 | 驻留 B | 段数 | 线程 |");
            sb.AppendLine("|---|---:|---:|---|");
            foreach (var s in liveRecs.OrderByDescending(x => x.Bytes).Take(20))
            {
                var eff = PickEffectiveFrame(s.Frames, ignoreRes);
                string label = eff.SkippedCount > 0 ? $"{eff.Frame} (+{eff.SkippedCount})" : eff.Frame;
                sb.AppendLine($"| {label} | {s.Bytes:N0} | {s.Count} | {s.Thread} |");
            }
        }

        var pssRecs = snap.Stacks.Where(s => s.Kind == 5).ToList();
        long mallocPssB = pssRecs.Where(s => PssCategoryOf(s) == "malloc").Sum(s => s.Bytes);

        var l2 = MergeByThreadAndFrames(snap.Stacks.Where(s => s.Kind == 6), isL2: true);
        if (l2.Count > 0)
        {
            long l2Live = l2.Sum(s => s.Bytes);
            long l2Freed = l2.Sum(s => s.Aux);
            int sample = Math.Max(snap.L2Sample, 1);
            long l2Est = l2Live * sample;
            long nativeHeapB = snap.Pss.GetValueOrDefault("Native Heap") * 1024;
            long totalPssB = snap.Pss.GetValueOrDefault("TOTAL PSS") * 1024;
            long smallLiveB = snap.L2FilteredLiveBytes;
            long sampledLiveB = snap.L2SampledLiveBytes;
            long l2AccountedB = l2Live + smallLiveB + sampledLiveB;

            sb.AppendLine();
            sb.AppendLine("## 当前持有（L2 malloc / 分配器，对应 Native Heap / malloc 分区）");
            sb.AppendLine();
            sb.AppendLine($"- 实采大块持有 {l2Live:N0} B　已释放 {l2Freed:N0} B");
            if (sample > 1)
                sb.AppendLine($"- 采样口径 1/{sample}（估算大块存活 ≈ {l2Est:N0} B，估算已释放 ≈ {l2Freed * sample:N0} B）");
            if (snap.L2Min > 0 || smallLiveB > 0 || snap.L2FilteredBytes > 0)
                sb.AppendLine($"- **阈值以下(<{snap.L2Min:N0} B) 净存量**：{smallLiveB:N0} B / {snap.L2FilteredLiveCount:N0} 项（累计申请 {snap.L2FilteredBytes:N0} B / {snap.L2FilteredCount:N0} 次）");
            if (sample > 1 && (sampledLiveB > 0 || snap.L2SampledBytes > 0))
                sb.AppendLine($"- **采样跳过净存量**：{sampledLiveB:N0} B / {snap.L2SampledLiveCount:N0} 项（累计跳过 {snap.L2SampledBytes:N0} B）");
            if (snap.NativeHeapAllocBytes > 0)
                sb.AppendLine($"- 进程内 `mallinfo().uordblks`：{snap.NativeHeapAllocBytes:N0} B");

            var recon = new List<string>();
            if (mallocPssB > 0) recon.Add($"K_PSS(malloc) {mallocPssB:N0} B（覆盖 {(sample > 1 ? l2Est : l2AccountedB) * 100.0 / mallocPssB:F1}%）");
            if (nativeHeapB > 0) recon.Add($"dumpsys Native Heap {nativeHeapB:N0} B（覆盖 {(sample > 1 ? l2Est : l2AccountedB) * 100.0 / nativeHeapB:F1}%）");
            if (totalPssB > 0) recon.Add($"TOTAL PSS {totalPssB:N0} B（占比 {(sample > 1 ? l2Est : l2AccountedB) * 100.0 / totalPssB:F1}%）");
            if (recon.Count > 0)
                sb.AppendLine($"- 对账：{string.Join("；", recon)}");
            sb.AppendLine("- `-systemallocator` 下，L2 对应 `Native Heap`（`[anon:libc_malloc]`），L1 对应直接 `mmap/mprotect` 的 `anon`/GC 区，两者是**互补分区**而非包含关系");
            sb.AppendLine();
            sb.AppendLine("| 业务分配点 | 存活 B | 已释放 B | 次数 | 线程 | 疑似 |");
            sb.AppendLine("|---|---:|---:|---:|---|---|");
            foreach (var s in l2.OrderByDescending(x => x.Bytes).Take(20))
            {
                var eff = PickEffectiveFrame(s.Frames, ignoreRes);
                string label = eff.SkippedCount > 0 ? $"{eff.Frame} (+{eff.SkippedCount})" : eff.Frame;
                sb.AppendLine($"| {label} | {s.Bytes:N0} | {s.Aux:N0} | {s.Count} | {s.Thread} | {(s.Suspect == "pool" ? "池化" : "")} |");
            }
        }

        if (pssRecs.Count > 0)
        {
            long pssTotal = pssRecs.Sum(s => s.Bytes);
            long dumpsysBytes = snap.Pss.GetValueOrDefault("TOTAL PSS") * 1024;
            long eglB = snap.Pss.GetValueOrDefault("EGL mtrack") * 1024;
            long glB = snap.Pss.GetValueOrDefault("GL mtrack") * 1024;
            long gpuMtrackB = eglB + glB;
            long reconciledB = pssTotal + gpuMtrackB;

            sb.AppendLine();
            sb.AppendLine("## PSS 分层对账（smaps Pss + GPU mtrack）");
            sb.AppendLine();
            sb.AppendLine($"- Σ smaps PSS：{pssTotal:N0} B（共 {pssRecs.Sum(s => s.Count):N0} 个 VMA）");
            if (gpuMtrackB > 0)
                sb.AppendLine($"- GPU mtrack（EGL + GL）：{gpuMtrackB:N0} B（EGL {eglB:N0} B + GL {glB:N0} B）");
            if (dumpsysBytes > 0)
            {
                long delta = reconciledB - dumpsysBytes;
                string deltaStr = $"{(delta >= 0 ? "+" : "")}{delta:N0}";
                string pctStr = $"{(delta >= 0 ? "+" : "")}{delta * 100.0 / dumpsysBytes:F1}%";
                sb.AppendLine($"- smaps + GPU 合计：{reconciledB:N0} B vs dumpsys TOTAL PSS {dumpsysBytes:N0} B（差额 {deltaStr} B / {pctStr}）");
            }
            sb.AppendLine();

            var grouped = pssRecs
                .GroupBy(PssCategoryOf)
                .Select(g => new
                {
                    Cat = g.Key,
                    Total = g.Sum(x => x.Bytes),
                    Vmas = g.Sum(x => x.Count),
                    Items = g.ToList(),
                })
                .OrderByDescending(g => g.Total)
                .ToList();

            sb.AppendLine("| 分区 | PSS B | 占比 | VMA 数 | 明细 |");
            sb.AppendLine("|---|---:|---:|---:|---|");
            foreach (var g in grouped)
            {
                double pct = pssTotal > 0 ? g.Total * 100.0 / pssTotal : 0;
                int stackCnt = MergeByThreadAndFrames(g.Items.Where(x => x.Frames.Count > 0)).Count;
                int subCnt = g.Items.Count(x => x.Frames.Count == 0 && PssSubLabelOf(x) != null);
                var parts = new List<string>();
                if (g.Cat == "malloc" && l2.Count > 0) parts.Add($"{l2.Count} 条 L2 栈");
                if (stackCnt > 0) parts.Add($"{stackCnt} 条 L1 栈");
                if (subCnt > 0) parts.Add($"{subCnt} 个子桶");
                string detail = parts.Count > 0 ? string.Join(" + ", parts) : "路径分桶";
                sb.AppendLine($"| **{g.Cat}** | {g.Total:N0} | {pct:F1}% | {g.Vmas} | {detail} |");
            }

            if (mallocPssB > 0 && l2.Count > 0)
            {
                int sample = Math.Max(snap.L2Sample, 1);
                long l2Live = l2.Sum(s => s.Bytes);
                long l2Est = l2Live * sample;
                long smallLive = snap.L2FilteredLiveBytes;
                long sampledLive = snap.L2SampledLiveBytes;
                long tracked = (sample > 1) ? (l2Est + smallLive) : (l2Live + smallLive + sampledLive);
                long unattr = Math.Max(0, mallocPssB - tracked);
                sb.AppendLine();
                sb.AppendLine($"### `malloc` 分区对账明细（smaps PSS {mallocPssB:N0} B）");
                sb.AppendLine();
                // 顶部先放汇总/小对象/残余，再列大块堆栈
                sb.AppendLine($"- 阈值以下(<{snap.L2Min:N0} B) 净存量：{smallLive:N0} B / {snap.L2FilteredLiveCount:N0} 项（累计申请 {snap.L2FilteredBytes:N0} B / {snap.L2FilteredCount:N0} 次）");
                if (sample > 1 && (sampledLive > 0 || snap.L2SampledBytes > 0))
                    sb.AppendLine($"- 采样跳过净存量（实测 usable）：{sampledLive:N0} B / {snap.L2SampledLiveCount:N0} 项（已计入×{sample}估算）");
                sb.AppendLine(tracked <= mallocPssB
                    ? $"- 残余（未归因 / Scudo 碎片 / 预热前）：{unattr:N0} B"
                    : $"- 注：L2 虚拟分配总额超出物理 PSS {(tracked - mallocPssB):N0} B（常见于未触页虚拟分配或 Scudo 延迟归还）");
                sb.AppendLine($"- L2 已追踪大块存活：{(sample > 1 ? l2Est : l2Live):N0} B{(sample > 1 ? $"（实采 {l2Live:N0} B × {sample}）" : "")}");
            }

            foreach (var g in grouped.Where(x => x.Items.Any(r => r.Frames.Count > 0 || PssSubLabelOf(r) != null)))
            {
                bool hasStacks = g.Items.Any(r => r.Frames.Count > 0);
                var subBuckets = g.Items
                    .Where(r => r.Frames.Count == 0 && PssSubLabelOf(r) != null)
                    .GroupBy(r => PssSubLabelOf(r)!)
                    .Select(sg => new StackRecord
                    {
                        Kind = 5,
                        Label = sg.First().Label,
                        Bytes = sg.Sum(x => x.Bytes),
                        Count = sg.Sum(x => x.Count),
                        Aux = sg.First().Aux
                    })
                    .OrderByDescending(r => r.Bytes)
                    .ToList();
                var mergedPssStacks = MergeByThreadAndFrames(g.Items.Where(x => x.Frames.Count > 0));
                long residual = g.Items.Where(r => r.Frames.Count == 0 && PssSubLabelOf(r) == null).Sum(r => r.Bytes);
                long residualVmas = g.Items.Where(r => r.Frames.Count == 0 && PssSubLabelOf(r) == null).Sum(r => r.Count);

                sb.AppendLine();
                sb.AppendLine($"### `{g.Cat}` 分区明细（共 {g.Total:N0} B）");
                sb.AppendLine();
                sb.AppendLine("| 名称 / 业务提交点 | 类型 | PSS B | VMA 数 |");
                sb.AppendLine("|---|---|---:|---:|");
                if (residual > 0)
                {
                    string resTitle = hasStacks
                        ? $"（未命中提交区间 / 预热前 {g.Cat} 残余）"
                        : $"（其他 / 溢出 {g.Cat} 残余）";
                    sb.AppendLine($"| {resTitle} | 残余 | {residual:N0} | {residualVmas} |");
                }
                foreach (var sub in subBuckets)
                {
                    sb.AppendLine($"| {PssSubLabelOf(sub)} | 子桶 | {sub.Bytes:N0} | {sub.Count} |");
                }
                foreach (var s in mergedPssStacks.OrderByDescending(x => x.Bytes).Take(15))
                {
                    var eff = PickEffectiveFrame(s.Frames, ignoreRes);
                    string label = eff.SkippedCount > 0 ? $"{eff.Frame} (+{eff.SkippedCount})" : eff.Frame;
                    sb.AppendLine($"| {label} | L1 栈 | {s.Bytes:N0} | {s.Count} |");
                }
            }
        }

        var catDict = Classifier.Categorize(snap, rules, ignorePatterns);
        bool hasLiveOrHold = snap.Stacks.Any(s => s.Kind == 4 || s.Kind == 6);
        sb.AppendLine();
        sb.AppendLine(hasLiveOrHold
            ? "## 按模块分类（全量存活：L1 存量 + L2 持有）"
            : "## 按模块分类（净驻留 = 提交 − 归还，预留不计）");
        sb.AppendLine();
        sb.AppendLine("| 分类 | 字节 |");
        sb.AppendLine("|---|---:|");
        foreach (var kv in catDict.OrderByDescending(k => k.Value))
            sb.AppendLine($"| {kv.Key} | {kv.Value:N0} |");

        sb.AppendLine();
        sb.AppendLine("## Top 栈");
        sb.AppendLine();
        var top = MergeByThreadAndFrames(snap.Stacks.Where(s => s.Frames.Count > 0))
            .OrderByDescending(s => Math.Abs(s.Bytes))
            .Take(30)
            .ToList();
        int idx = 0;
        foreach (var s in top)
        {
            idx++;
            var eff = PickEffectiveFrame(s.Frames, ignoreRes);
            sb.AppendLine($"### [{idx}] {KindName(s.Kind)} thread={s.Thread} {s.Bytes:N0} B / {s.Count} 次 — {eff.Frame}");
            sb.AppendLine("```");
            for (int fi = 0; fi < s.Frames.Count; fi++)
            {
                string tag = (fi == eff.Index && eff.SkippedCount > 0) ? "[业务归因] " : "";
                sb.AppendLine($"{tag}{s.Frames[fi]}");
            }
            sb.AppendLine("```");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static string KindName(int k) => k switch
    {
        0 => "预留",
        1 => "提交",
        2 => "归还",
        3 => "L2(旧)",
        4 => "存量",
        5 => "PSS",
        6 => "持有",
        _ => "?"
    };
}
