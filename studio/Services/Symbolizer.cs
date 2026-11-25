using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AllocHookStudio;

public class MapRange
{
    public ulong Start { get; set; }
    public ulong End { get; set; }
    public ulong Offset { get; set; }
    public string Perms { get; set; } = "";
    /// <summary> 该段映射对应的 ELF 加载基址（load_bias），使得 VMA = pc - Base。
    /// 多段（如 16KB 对齐 PT_LOAD）且 p_vaddr != p_offset 时，必须使用整个 ELF 实例的
    /// load_bias，而不是单段的 Start - Offset。 </summary>
    public ulong Base { get; set; }
    public string Path { get; set; } = "";
}

public class Symbolizer
{
    private readonly AppConfig _cfg;
    private readonly string _cacheRoot;
    private readonly ConcurrentDictionary<string, string?> _symFileCache = new();

    private static readonly Regex MapsLineRegex = new(
        @"^\s*([0-9a-fA-F]+)-([0-9a-fA-F]+)\s+([rwxps-]{4})\s+([0-9a-fA-F]+)\s+\S+\s+\d+\s+(/\S+\.so)\s*$",
        RegexOptions.Compiled);

    public Symbolizer(AppConfig cfg, string cacheRoot)
    {
        _cfg = cfg;
        _cacheRoot = cacheRoot;
        Directory.CreateDirectory(_cacheRoot);
    }

    private static ulong AlignDown(ulong v, ulong align) => v & ~(align - 1);
    private static ulong AlignUp(ulong v, ulong align) => (v + align - 1) & ~(align - 1);

    /// <summary> 解析 /proc/self/maps，按加载实例聚合并计算每段正确的 ELF load_bias </summary>
    public static (List<MapRange> Ranges, Dictionary<string, ulong> Bases) ParseMaps(string mapsText)
    {
        var ranges = new List<MapRange>();
        var bases = new Dictionary<string, ulong>();

        foreach (var line in mapsText.Split('\n'))
        {
            var m = MapsLineRegex.Match(line);
            if (!m.Success) continue;

            ulong s = Convert.ToUInt64(m.Groups[1].Value, 16);
            ulong e = Convert.ToUInt64(m.Groups[2].Value, 16);
            string perms = m.Groups[3].Value;
            ulong off = Convert.ToUInt64(m.Groups[4].Value, 16);
            string p = m.Groups[5].Value;

            ranges.Add(new MapRange
            {
                Start = s,
                End = e,
                Offset = off,
                Perms = perms,
                Base = s >= off ? s - off : s,
                Path = p
            });
        }

        var ordered = ranges.OrderBy(r => r.Start).ToList();
        foreach (var group in ordered.GroupBy(r => r.Path))
        {
            var clusters = SplitClusters(group.ToList());
            foreach (var c in clusters)
            {
                bool hasExec = c.Any(r => r.Perms.Contains('x'));
                if (hasExec && c[0].Offset == 0)
                {
                    ulong loadBias = c[0].Start;
                    foreach (var r in c) r.Base = loadBias;
                    if (!bases.ContainsKey(group.Key)) bases[group.Key] = loadBias;
                }
            }
            if (!bases.ContainsKey(group.Key) && group.Any())
                bases[group.Key] = group.First().Base;
        }

        return (ordered, bases);
    }

    /// <summary> 将同一路径的 maps 区间按地址与偏移连续性拆分为独立的映射/加载实例（过滤孤立的整文件 r--p 映射） </summary>
    private static List<List<MapRange>> SplitClusters(List<MapRange> sorted)
    {
        var clusters = new List<List<MapRange>>();
        List<MapRange>? cur = null;
        bool curHasExec = false;

        foreach (var r in sorted)
        {
            bool startNew = cur == null;
            if (!startNew && cur != null)
            {
                var prev = cur[^1];
                bool isExec = r.Perms.Contains('x');
                if (r.Start - prev.End > 0x100000) startNew = true;
                else if (r.Offset < prev.Offset) startNew = true;
                else if (!curHasExec && r.Offset == 0 && !isExec) startNew = true;
                else if (curHasExec && r.Offset == 0 && isExec) startNew = true;
            }

            if (startNew)
            {
                cur = new List<MapRange>();
                clusters.Add(cur);
                curHasExec = false;
            }
            cur!.Add(r);
            if (r.Perms.Contains('x')) curHasExec = true;
        }
        return clusters;
    }

    /// <summary> 解析 maps 并结合本地 ELF64 PT_LOAD 头进一步精确修正每段 load_bias（兼容 symbols 裁剪头与多页大小） </summary>
    public List<MapRange> ParseRanges(string mapsText)
    {
        var (ranges, _) = ParseMaps(mapsText);
        RefineBasesWithElves(ranges);
        return ranges;
    }

    private void RefineBasesWithElves(List<MapRange> ranges)
    {
        ulong[] pageSizes = { 0x1000UL, 0x4000UL, 0x10000UL };

        foreach (var group in ranges.GroupBy(r => r.Path))
        {
            string leaf = Path.GetFileName(group.Key);
            string? symFile = SymFileFor(leaf);
            string? codeFile = FdeTable.FindUnstripped(leaf, symFile);
            var loads = FdeTable.GetPtLoads(codeFile) ?? FdeTable.GetPtLoads(symFile);
            if (loads == null || loads.Count == 0) continue;

            var execSegs = loads.Where(s => (s.Flags & 1 /* PF_X */) != 0).ToList();
            if (execSegs.Count == 0) continue;
            var firstSeg = loads.OrderBy(s => s.Vaddr).First();

            foreach (var cluster in SplitClusters(group.ToList()))
            {
                var rxList = cluster.Where(r => r.Perms.Contains('x')).ToList();
                if (rxList.Count == 0) continue;

                ulong? resolvedBias = null;
                foreach (var p in pageSizes)
                {
                    foreach (var rx in rxList)
                    {
                        foreach (var es in execSegs)
                        {
                            ulong vStart = AlignDown(es.Vaddr, p);
                            ulong vEnd = AlignUp(es.Vaddr + es.Memsz, p);
                            if (rx.End - rx.Start != vEnd - vStart) continue;
                            if (es.Filesz > 0 && rx.Offset != AlignDown(es.Offset, p)) continue;

                            ulong candBias = rx.Start - vStart;
                            ulong expectedFirst = candBias + AlignDown(firstSeg.Vaddr, p);
                            if (cluster.Any(m => m.Start == expectedFirst))
                            {
                                resolvedBias = candBias;
                                break;
                            }
                        }
                        if (resolvedBias.HasValue) break;
                    }
                    if (resolvedBias.HasValue) break;
                }

                if (resolvedBias.HasValue)
                {
                    foreach (var r in cluster) r.Base = resolvedBias.Value;
                }
            }
        }
    }

    private static MapRange? FindRange(List<MapRange> ranges, ulong pc)
    {
        int lo = 0, hi = ranges.Count - 1, best = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (ranges[mid].Start <= pc) { best = mid; lo = mid + 1; } else hi = mid - 1;
        }
        for (int i = best; i >= 0 && i >= best - 8; i--)
            if (pc >= ranges[i].Start && pc < ranges[i].End) return ranges[i];
        return null;
    }

    private string? SymFileFor(string leaf)
    {
        if (string.IsNullOrWhiteSpace(_cfg.SymbolsDir) || !Directory.Exists(_cfg.SymbolsDir)) return null;
        return _symFileCache.GetOrAdd(leaf, l =>
        {
            string? direct = null;
            if (l.Contains("libil2cpp")) direct = Path.Combine(_cfg.SymbolsDir, "libil2cpp.so");
            else if (l.Contains("libunity")) direct = Path.Combine(_cfg.SymbolsDir, "libunity.so");
            if (direct != null && File.Exists(direct)) return direct;

            // 允许符号目录是多层结构（如 symbols/arm64-v8a/）
            foreach (var f in Directory.EnumerateFiles(_cfg.SymbolsDir, l, SearchOption.AllDirectories))
                return f;
            return null;
        });
    }

    /// <summary> 不可信帧只显示原始地址（模块 + ELF 内偏移），不显示可能是猜出来的符号名 </summary>
    public static string RawFrame(ulong pc, List<MapRange> ranges)
    {
        var rg = FindRange(ranges, pc);
        if (rg == null) return $"0x{pc:x} [未验证]";
        ulong vma = pc >= rg.Base ? pc - rg.Base : 0;
        return $"{Path.GetFileName(rg.Path)}+0x{vma:x} [未验证]";
    }

    /// <summary>
    /// 按唯一返回 PC 集合批量校验是否紧跟在 bl/blr 之后（只计算一次/PC，避免对重复栈帧反复查询）。
    /// </summary>
    public HashSet<ulong> CheckUntrustedPcs(IEnumerable<ulong> returnPcs, List<MapRange> ranges)
    {
        var bad = new HashSet<ulong>();
        foreach (var pc in returnPcs)
        {
            if (pc == 0) continue;
            var rg = FindRange(ranges, pc);
            if (rg == null) { bad.Add(pc); continue; }
            string leaf = Path.GetFileName(rg.Path);
            string? symFile = SymFileFor(leaf);
            if (symFile == null) continue;            // 没有本地文件时无法验证，不误伤

            ulong vma = pc >= rg.Base ? pc - rg.Base : 0;
            if (!FdeTable.IsCallReturn(leaf, symFile, vma, _cacheRoot)) bad.Add(pc);
        }
        return bad;
    }

    /// <summary>
    /// 找出"不可信帧"：从第 1 帧起，每帧都必须是某条 bl/blr 的下一条指令；否则就是 unwinder 猜出来的。
    /// 这些帧不应显示符号名，只显示原始地址。
    /// </summary>
    public HashSet<int> UntrustedFrames(IReadOnlyList<ulong> pcs, List<MapRange> ranges)
    {
        var bad = new HashSet<int>();
        if (pcs.Count < 2) return bad;

        // 第 0 帧是 unwinder 的当前 IP，本来就不是"调用返回地址"，不校验。
        // 从第 1 帧起，每帧都必须是某条 bl/blr 的下一条指令；否则就是 unwinder 猜出来的。
        for (int i = 1; i < pcs.Count; ++i)
        {
            var rg = FindRange(ranges, pcs[i]);
            if (rg == null) { bad.Add(i); continue; }
            string leaf = Path.GetFileName(rg.Path);
            string? symFile = SymFileFor(leaf);
            if (symFile == null) continue;            // 没有本地文件时无法验证，不误伤

            ulong vma = pcs[i] >= rg.Base ? pcs[i] - rg.Base : 0;
            if (!FdeTable.IsCallReturn(leaf, symFile, vma, _cacheRoot)) bad.Add(i);
        }
        return bad;
    }

    public Task<HashSet<int>> UntrustedFramesAsync(ulong[] pcs, string mapsText, string llvmDir)
    {
        var ranges = ParseRanges(mapsText);
        return Task.FromResult(UntrustedFrames(pcs, ranges));
    }

    public Task<Dictionary<ulong, string>> SymbolizeAsync(IEnumerable<ulong> pcs, string mapsText, string llvmDir)
        => SymbolizeAsync(pcs, ParseRanges(mapsText), llvmDir);

    public async Task<Dictionary<ulong, string>> SymbolizeAsync(IEnumerable<ulong> pcs, List<MapRange> ranges, string llvmDir)
    {
        var byModule = new Dictionary<string, Dictionary<ulong, List<ulong>>>();  // module -> (symAddr -> pcs)
        var result = new Dictionary<ulong, string>();

        foreach (var pc in pcs.Distinct())
        {
            var rg = FindRange(ranges, pc);
            if (rg == null) { result[pc] = "(anon)"; continue; }
            string leaf = Path.GetFileName(rg.Path);
            // 用该 ELF 加载实例的 load_bias 计算 VMA
            ulong off = pc >= rg.Base ? pc - rg.Base : 0;

            if (off > 0x20000000) { result[pc] = "(bad-frame)"; continue; }

            if (!byModule.TryGetValue(leaf, out var map)) byModule[leaf] = map = new Dictionary<ulong, List<ulong>>();
            ulong symAddr = off > 4 ? off - 4 : off;
            if (!map.TryGetValue(symAddr, out var list)) map[symAddr] = list = new List<ulong>(1);
            list.Add(pc);
        }

        foreach (var (leaf, map) in byModule)
        {
            string? symFile = SymFileFor(leaf);
            if (symFile == null)
            {
                foreach (var (addr, pcList) in map)
                    foreach (var pc in pcList) result[pc] = $"{leaf}+0x{addr:x}";
                continue;
            }

            var cache = LoadCache(leaf, symFile);
            var missing = map.Keys.Where(a => !cache.ContainsKey(a)).ToList();

            if (missing.Count > 0 && !string.IsNullOrEmpty(llvmDir))
            {
                var a2l = Path.Combine(llvmDir, "llvm-addr2line.exe");
                if (!File.Exists(a2l)) a2l = Path.Combine(llvmDir, "llvm-addr2line");
                if (File.Exists(a2l))
                {
                    var names = await RunAddr2Line(a2l, symFile, missing);
                    for (int k = 0; k < missing.Count && k < names.Count; k++) cache[missing[k]] = names[k];
                    SaveCache(leaf, symFile, cache);
                }
            }

            foreach (var (addr, pcList) in map)
            {
                string val = cache.TryGetValue(addr, out var n) && !string.IsNullOrEmpty(n) && n != "??"
                    ? $"{leaf}!{n}"
                    : $"{leaf}+0x{addr:x}";
                foreach (var pc in pcList) result[pc] = val;
            }
        }

        return result;
    }

    private static async Task<List<string>> RunAddr2Line(string exe, string obj, List<ulong> addrs)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-e"); psi.ArgumentList.Add(obj);
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("-C");

        using var p = Process.Start(psi)!;
        var writeTask = Task.Run(async () =>
        {
            await using var sw = p.StandardInput;
            var sb = new StringBuilder(Math.Min(addrs.Count * 18, 65536));
            for (int i = 0; i < addrs.Count; i++)
            {
                sb.Append("0x").Append(addrs[i].ToString("x")).Append('\n');
                if (sb.Length >= 32768)
                {
                    await sw.WriteAsync(sb.ToString());
                    sb.Clear();
                }
            }
            if (sb.Length > 0) await sw.WriteAsync(sb.ToString());
            await sw.FlushAsync();
        });
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        await Task.WhenAll(writeTask, outTask, errTask, p.WaitForExitAsync());
        var lines = outTask.Result.Replace("\r", "").Split('\n');

        var names = new List<string>(addrs.Count);
        for (int i = 0; i < lines.Length; i += 2)
        {
            var fn = i < lines.Length ? lines[i].Trim() : "";
            names.Add(string.IsNullOrWhiteSpace(fn) ? "??" : fn);
        }
        return names;
    }

    private string CachePath(string leaf, string symFile)
    {
        var fi = new FileInfo(symFile);
        string key = $"{leaf}_{fi.Length}_{fi.LastWriteTimeUtc.Ticks}";
        foreach (var c in Path.GetInvalidFileNameChars()) key = key.Replace(c, '_');
        return Path.Combine(_cacheRoot, key + ".json");
    }

    private Dictionary<ulong, string> LoadCache(string leaf, string symFile)
    {
        try
        {
            var path = CachePath(leaf, symFile);
            if (!File.Exists(path)) return new Dictionary<ulong, string>();
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            var d = new Dictionary<ulong, string>();
            if (raw != null) foreach (var kv in raw) d[Convert.ToUInt64(kv.Key, 16)] = kv.Value;
            return d;
        }
        catch { return new Dictionary<ulong, string>(); }
    }

    private void SaveCache(string leaf, string symFile, Dictionary<ulong, string> cache)
    {
        try
        {
            var raw = cache.ToDictionary(kv => kv.Key.ToString("x"), kv => kv.Value);
            File.WriteAllText(CachePath(leaf, symFile), JsonSerializer.Serialize(raw));
        }
        catch { }
    }
}
