using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AllocHookStudio;

/// <summary>
/// 从 libunity 符号表里取出「分配器入口」的偏移，供原生层做 inline hook。
/// 这些函数是库内直接调用（BL），不在动态符号表里，dlsym 拿不到，只能按偏移改写入入口。
///
/// 目标选择（按 Unity 2022.3 的实际调用链）：
///   MemoryManager::Allocate → TLSAllocator&lt;AllocatorMode0&gt;::Allocate(size, align)
/// 对象级分配真正落在 TLSAllocator 上，MemoryManager 只是外面包了一层标签/回退逻辑。
/// 另有 AllocatorMode1 一套（通常给临时/栈式分配），一并挂上。
/// </summary>
public static class UnitySymbols
{
    public record Offsets(ulong Alloc, ulong Free, ulong Alloc1, ulong Free1, string Detail);

    public static Offsets? Resolve(string symbolsDir, string llvmDir)
    {
        if (string.IsNullOrWhiteSpace(symbolsDir)) return null;

        var so = Path.Combine(symbolsDir, "libunity.so");
        if (!File.Exists(so))
        {
            var found = Directory.EnumerateFiles(symbolsDir, "libunity.so", SearchOption.AllDirectories).FirstOrDefault();
            if (found == null) return null;
            so = found;
        }

        var nm = Path.Combine(llvmDir ?? "", "llvm-nm.exe");
        if (!File.Exists(nm)) nm = Path.Combine(llvmDir ?? "", "llvm-nm");
        if (!File.Exists(nm)) return null;

        var psi = new ProcessStartInfo(nm)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add("--defined-only");
        psi.ArgumentList.Add(so);

        string output;
        try
        {
            using var p = Process.Start(psi)!;
            output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(60000);
        }
        catch { return null; }

        // 形如：
        //   0000000000a834f0 t TLSAllocator<(AllocatorMode)0>::Allocate(unsigned long, int)
        //   0000000000a83590 t TLSAllocator<(AllocatorMode)0>::Deallocate(void*)
        ulong a0 = 0, f0 = 0, a1 = 0, f1 = 0;
        string n0 = "";

        foreach (var line in output.Split('\n'))
        {
            var m = Regex.Match(line, @"^\s*([0-9a-fA-F]+)\s+\S\s+(.+)$");
            if (!m.Success) continue;

            var addr = Convert.ToUInt64(m.Groups[1].Value, 16);
            var name = m.Groups[2].Value.Trim();

            if (name.StartsWith("DynamicHeapAllocator::Allocate(")) { a0 = addr; n0 = name; }
            else if (name.StartsWith("DualThreadAllocator<DynamicHeapAllocator>::TryDeallocate(")) f0 = addr;
            
            
        }

        if (a0 == 0 || f0 == 0) return null;

        var detail = $"{n0} @0x{a0:x}\nDualThreadAllocator::TryDeallocate @0x{f0:x}";
        if (f1 != 0) detail += $"\nDualThreadAllocator::TryDeallocate @0x{f1:x}";

        return new Offsets(a0, f0, a1, f1, detail);
    }

    /// <summary> 生成写到设备上的 allochook_unity.txt </summary>
    public static string ToFileContent(Offsets o)
    {
        var s = $"alloc=0x{o.Alloc:x}\nfree=0x{o.Free:x}\n";
        if (o.Free1 != 0)
            s += $"free1=0x{o.Free1:x}\n";
        return s;
    }
}
