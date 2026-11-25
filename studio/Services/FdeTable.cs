using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace AllocHookStudio;

public sealed record ElfSegment(uint Flags, ulong Offset, ulong Vaddr, ulong Filesz, ulong Memsz, ulong Align);

/// <summary>
/// 从**未剥离的** so 里导出 .eh_frame 的 FDE 区间，用来判断一次 unwind 跳转是否有 CFI 支撑。
///
/// 关键：APK 里的 so 只剥掉了 symtab，.eh_frame 与未剥离版一致；
/// 所以如果某个 PC 在未剥离版里也没有 FDE，unwinder 到这一帧就无路可走，
/// 它给出的"下一帧"只能靠猜 —— 这种情况必须把后面的帧显示成原始地址。
/// </summary>
public class FdeTable
{
    private readonly ulong[] _starts;
    private readonly ulong[] _ends;

    private FdeTable(ulong[] starts, ulong[] ends) { _starts = starts; _ends = ends; }

    public bool HasFde(ulong vma)
    {
        int lo = 0, hi = _starts.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_starts[mid] > vma) hi = mid - 1;
            else if (_ends[mid] <= vma) lo = mid + 1;
            else return true;
        }
        return false;
    }

    private static readonly Dictionary<string, FdeTable?> Cache = new();
    private static readonly ConcurrentDictionary<string, List<ElfSegment>?> PtLoadCache = new();
    private static readonly ConcurrentDictionary<string, string?> CodeFileCache = new();
    private static readonly ConcurrentDictionary<(string File, ulong Vma), bool> CallReturnCache = new();
    private static readonly ConcurrentDictionary<string, Lazy<SafeFileHandle?>> OpenHandles = new();

    private static SafeFileHandle? GetSharedHandle(string file)
    {
        try
        {
            var fi = new FileInfo(file);
            if (!fi.Exists) return null;
            string key = $"{file}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
            var lazy = OpenHandles.GetOrAdd(key, _ => new Lazy<SafeFileHandle?>(() =>
            {
                try
                {
                    return File.OpenHandle(file, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                }
                catch { return null; }
            }));
            var h = lazy.Value;
            if (h == null || h.IsInvalid || h.IsClosed)
            {
                OpenHandles.TryRemove(key, out _);
                return null;
            }
            return h;
        }
        catch { return null; }
    }

    public static async Task<FdeTable?> LoadAsync(string leaf, string symFile, string llvmDir, string cacheRoot)
    {
        var fdeFile = FindUnstripped(leaf, symFile);
        if (fdeFile == null) { DebugLog(cacheRoot, $"{leaf}: no unstripped file (sym={symFile})"); return null; }
        var key = fdeFile + "|" + new FileInfo(fdeFile).LastWriteTimeUtc.Ticks;
        if (Cache.TryGetValue(key, out var hit)) return hit;

        var cachePath = Path.Combine(cacheRoot,
            $"fde_{Path.GetFileNameWithoutExtension(leaf)}_{new FileInfo(fdeFile).Length}_{new FileInfo(fdeFile).LastWriteTimeUtc.Ticks}.bin");
        if (File.Exists(cachePath))
        {
            try { Cache[key] = FromFile(cachePath); return Cache[key]; } catch { }
        }

        var exe = Path.Combine(llvmDir, "llvm-dwarfdump.exe");
        if (!File.Exists(exe)) { Cache[key] = null; DebugLog(cacheRoot, $"{leaf}: no dwarfdump at {exe}"); return null; }
        DebugLog(cacheRoot, $"{leaf}: loading FDE from {fdeFile}");

        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--eh-frame");
        psi.ArgumentList.Add(fdeFile);

        var ranges = new List<(ulong s, ulong e)>();
        var re = new Regex(@"pc=([0-9a-f]{8,16})\.\.\.([0-9a-f]{8,16})", RegexOptions.Compiled);
        try
        {
            using var p = Process.Start(psi)!;
            var outTask = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            var text = await outTask;
            foreach (var line in text.Split('\n'))
            {
                var m = re.Match(line);
                if (!m.Success) continue;
                ranges.Add((Convert.ToUInt64(m.Groups[1].Value, 16), Convert.ToUInt64(m.Groups[2].Value, 16)));
            }
        }
        catch { Cache[key] = null; return null; }

        if (ranges.Count == 0) { Cache[key] = null; DebugLog(cacheRoot, $"{leaf}: 0 FDE parsed"); return null; }
        ranges.Sort((a, b) => a.s != b.s ? a.s.CompareTo(b.s) : a.e.CompareTo(b.e));
        var starts = ranges.Select(r => r.s).ToArray();
        var ends = ranges.Select(r => r.e).ToArray();
        var table = new FdeTable(starts, ends);
        try { ToFile(cachePath, starts, ends); } catch { }
        Cache[key] = table;
        return table;
    }

    /// <summary>
    /// 直接验证"这个 PC 是不是某条 bl/blr 的下一条指令"。
    /// 比检查 FDE 是否存在可靠得多：libunity 里存在覆盖多个函数、CFI 却只写了一套 CFA 的 FDE，
    /// 有 FDE 但帧大小算错，unwinder 一样会给出假帧。
    /// </summary>
    public static bool IsCallReturn(string leaf, string? symFile, ulong vma, string cacheRoot)
    {
        if (vma < 4) return false;
        var file = FindUnstripped(leaf, symFile);
        if (file == null) return true;              // 没有含代码段的本地文件时无法验证，不误伤

        return CallReturnCache.GetOrAdd((file, vma), key =>
        {
            try
            {
                var off = VaddrToFileOffset(key.File, key.Vma - 4);
                if (off < 0) return true;
                var handle = GetSharedHandle(key.File);
                if (handle == null) return true;
                Span<byte> b = stackalloc byte[4];
                if (RandomAccess.Read(handle, b, off) != 4) return true;
                uint ins = (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
                bool bl  = (ins & 0xFC000000u) == 0x94000000u;   // BL  <imm26>
                bool blr = (ins & 0xFFFFFC1Fu) == 0xD63F0000u;   // BLR <Xn>
                return bl || blr;
            }
            catch { return true; }
        });
    }

    /// <summary> 读取并缓存 ELF64 PT_LOAD 程序头（symbols/*.so 与 jniLibs/*.so 均有有效的 p_vaddr/p_memsz/p_flags） </summary>
    public static List<ElfSegment>? GetPtLoads(string? file)
    {
        if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;
        var fi = new FileInfo(file);
        string cacheKey = $"{file}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        return PtLoadCache.GetOrAdd(cacheKey, _ => ReadPtLoads(file));
    }

    private static List<ElfSegment>? ReadPtLoads(string file)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);
            var magic = br.ReadBytes(8);
            if (magic.Length < 8 || magic[0] != 0x7f || magic[1] != (byte)'E' || magic[2] != (byte)'L' || magic[3] != (byte)'F')
                return null;
            if (magic[4] != 2) return null;                // EI_CLASS：只支持 ELF64

            fs.Position = 32; ulong phoff = br.ReadUInt64();
            fs.Position = 54; ushort phentsize = br.ReadUInt16();
            ushort phnum = br.ReadUInt16();
            if (phentsize < 56 || phnum == 0 || phnum > 256) return null;

            var list = new List<ElfSegment>();
            for (int i = 0; i < phnum; ++i)
            {
                fs.Position = (long)(phoff + (ulong)i * phentsize);
                uint type = br.ReadUInt32();
                uint flags = br.ReadUInt32();
                ulong poff = br.ReadUInt64();
                ulong pvaddr = br.ReadUInt64();
                _ = br.ReadUInt64();                       // p_paddr
                ulong filesz = br.ReadUInt64();
                ulong memsz = br.ReadUInt64();
                ulong align = br.ReadUInt64();
                if (type == 1 /* PT_LOAD */)
                    list.Add(new ElfSegment(flags, poff, pvaddr, filesz, memsz, align));
            }
            return list;
        }
        catch { return null; }
    }

    private static bool HasExecutableCodeBytes(string file)
    {
        var loads = GetPtLoads(file);
        if (loads == null) return false;
        return loads.Any(s => (s.Flags & 1 /* PF_X */) != 0 && s.Filesz > 0);
    }

    /// <summary> ELF64: 按 p_vaddr/p_filesz 把虚拟地址换成文件偏移 </summary>
    private static long VaddrToFileOffset(string file, ulong vaddr)
    {
        var loads = GetPtLoads(file);
        if (loads == null) return -1;
        foreach (var s in loads)
        {
            if (s.Filesz > 0 && vaddr >= s.Vaddr && vaddr < s.Vaddr + s.Filesz)
                return (long)(s.Offset + (vaddr - s.Vaddr));
        }
        return -1;
    }

    /// <summary> 优先用工程里的 jniLibs 未剥离版；否则找包含可执行代码段（p_filesz &gt; 0）的同名 so </summary>
    private static void DebugLog(string cacheRoot, string text)
    {
        try { Directory.CreateDirectory(cacheRoot); File.AppendAllText(Path.Combine(cacheRoot, "fde_debug.txt"), text + "\n"); } catch { }
    }

    public static string? FindUnstripped(string leaf, string? symFile)
    {
        if (string.IsNullOrEmpty(symFile)) return null;
        return CodeFileCache.GetOrAdd($"{leaf}|{symFile}", _ => FindUnstrippedCore(leaf, symFile));
    }

    private static string? FindUnstrippedCore(string leaf, string symFile)
    {
        try
        {
            var dir = new DirectoryInfo(symFile).Parent;             // .../symbols/arm64-v8a
            var abiName = dir?.Name ?? "arm64-v8a";
            var root = dir?.Parent?.Parent;                          // .../unityLibrary
            if (root != null && root.Exists)
            {
                var direct = Path.Combine(root.FullName, "src", "main", "jniLibs", abiName, leaf);
                if (File.Exists(direct) && HasExecutableCodeBytes(direct)) return direct;

                var directAlt = Path.Combine(root.FullName, "src", "main", "jniLibs", "arm64-v8a", leaf);
                if (directAlt != direct && File.Exists(directAlt) && HasExecutableCodeBytes(directAlt)) return directAlt;
            }

            // 若 symFile 本身不是 NOBITS 裁剪版（例如 symbols/libil2cpp.so 或直接指向 jniLibs），也可直接用
            if (File.Exists(symFile) && new FileInfo(symFile).Length < 350_000_000 && HasExecutableCodeBytes(symFile))
                return symFile;

            if (root != null && root.Exists)
            {
                var candidates = Directory.EnumerateFiles(root.FullName, leaf, SearchOption.AllDirectories)
                    .Where(f => HasExecutableCodeBytes(f))
                    .OrderBy(f => new FileInfo(f).Length >= 350_000_000 ? 1 : 0)
                    .ThenByDescending(f => new FileInfo(f).Length < 350_000_000 ? new FileInfo(f).Length : 0)
                    .ToList();
                if (candidates.Count > 0) return candidates[0];
            }

            if (File.Exists(symFile) && HasExecutableCodeBytes(symFile))
                return symFile;
        }
        catch { }
        return null;
    }

    private static FdeTable FromFile(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        int n = br.ReadInt32();
        var s = new ulong[n]; var e = new ulong[n];
        for (int i = 0; i < n; ++i) { s[i] = br.ReadUInt64(); e[i] = br.ReadUInt64(); }
        return new FdeTable(s, e);
    }

    private static void ToFile(string path, ulong[] s, ulong[] e)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write(s.Length);
        for (int i = 0; i < s.Length; ++i) { bw.Write(s[i]); bw.Write(e[i]); }
    }
}
