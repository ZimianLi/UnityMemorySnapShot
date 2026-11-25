using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AllocHookStudio;

public class AdbDevice
{
    public string Serial { get; set; } = "";
    public string State { get; set; } = "";
}

public class Adb
{
    private readonly AppConfig _cfg;
    public Adb(AppConfig cfg) => _cfg = cfg;

    private string? _exe;
    private string Exe
    {
        get
        {
            if (_exe != null) return _exe;
            var configured = string.IsNullOrWhiteSpace(_cfg.AdbPath) ? "adb" : _cfg.AdbPath;
            if (File.Exists(configured)) { _exe = configured; return _exe; }
            foreach (var guess in new[]
            {
                @"D:\AndroidSDK\platform-tools\adb.exe",
                @"C:\AndroidSDK\platform-tools\adb.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Android\Sdk\platform-tools\adb.exe"),
            })
            {
                if (File.Exists(guess)) { _exe = guess; return _exe; }
            }
            _exe = configured;   // 交给 PATH
            return _exe;
        }
    }

    public async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        var psi = new ProcessStartInfo(Exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } }
        return (p.HasExited ? p.ExitCode : -1, await so, await se);
    }

    /// <summary> 拉取二进制（exec-out 不做换行转换） </summary>
    public async Task<byte[]> ExecOutBytes(params string[] args)
    {
        var psi = new ProcessStartInfo(Exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        using var ms = new MemoryStream();
        var copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
        var err = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } }
        await copy;
        return ms.ToArray();
    }

    public async Task<List<AdbDevice>> Devices()
    {
        var list = new List<AdbDevice>();
        var (code, o, _) = await Run("devices");
        if (code != 0) return list;
        foreach (var line in o.Split('\n'))
        {
            var s = line.Trim();
            if (s.Length == 0 || s.StartsWith("List of devices")) continue;
            var parts = s.Split('\t', ' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) list.Add(new AdbDevice { Serial = parts[0], State = parts[1] });
        }
        return list;
    }

    public Task<(int, string, string)> Install(string apk) => Run("install", "-r", apk);
    public Task<(int, string, string)> ForceStop(string pkg) => Run("shell", "am", "force-stop", pkg);
    public Task<(int, string, string)> Launch(string pkg) =>
        Run("shell", "monkey", "-p", pkg, "-c", "android.intent.category.LAUNCHER", "1");

    /// <summary> 解析启动 Activity（LAUNCHER） </summary>
    public async Task<string?> ResolveLauncherActivity(string pkg)
    {
        var act = await Run("shell", "cmd", "package", "resolve-activity", "--brief",
                            "-c", "android.intent.category.LAUNCHER", pkg);
        var activity = act.Out.Split('\n').Select(x => x.Trim())
            .LastOrDefault(x => x.Contains('/') && x.StartsWith(pkg, StringComparison.Ordinal));
        return activity;
    }

    /// <summary> 解析 UnityActivity（优先带 unityplayer.UnityActivity 元数据或 MainActivity/UnityPlayerActivity，再回退到 LAUNCHER） </summary>
    public async Task<string?> ResolveUnityActivity(string pkg)
    {
        var dump = await Run("shell", "dumpsys", "package", pkg);
        string? candidate = null;
        if (dump.Code == 0 && !string.IsNullOrWhiteSpace(dump.Out))
        {
            var lines = dump.Out.Split('\n');
            string? currentAct = null;
            var allActs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                // 匹配形如 "com.elex.lds.gp/.MainActivity" 或 "com.elex.lds.gp/com.unity3d.player.UnityPlayerActivity"
                var m = Regex.Match(line, $@"\b({Regex.Escape(pkg)}/[A-Za-z0-9_.$]+)");
                if (m.Success)
                {
                    currentAct = m.Groups[1].Value;
                    allActs.Add(currentAct);
                }
                if (currentAct != null && line.Contains("unityplayer.UnityActivity", StringComparison.Ordinal))
                {
                    candidate = currentAct;
                    break;
                }
            }
            if (candidate == null && allActs.Count > 0)
            {
                candidate = allActs.FirstOrDefault(a =>
                    a.EndsWith("/.MainActivity", StringComparison.OrdinalIgnoreCase) ||
                    a.EndsWith(".MainActivity", StringComparison.OrdinalIgnoreCase) ||
                    a.Contains("UnityPlayerActivity", StringComparison.OrdinalIgnoreCase) ||
                    a.Contains("UnityActivity", StringComparison.OrdinalIgnoreCase));
            }
        }
        if (candidate != null) return candidate;
        return await ResolveLauncherActivity(pkg) ?? $"{pkg}/.MainActivity";
    }

    /// <summary> 解析目标包在设备上的原生库 ABI 子目录（arm64 / arm / x86_64） </summary>
    public async Task<(string? LibPath, string? AbiDir, string? Error)> ResolveAllochookLibPath(string pkg)
    {
        var pmPath = await Run("shell", "pm", "path", pkg);
        var baseApk = pmPath.Out.Split('\n').Select(x => x.Trim())
            .FirstOrDefault(x => x.StartsWith("package:") && x.Contains("base.apk"))
            ?.Substring("package:".Length);
        if (baseApk == null) return (null, null, "取不到 APK 路径（应用未安装？）");

        var appBase = baseApk[..^"/base.apk".Length];
        var cpuAbi = (await Run("shell", "getprop", "ro.product.cpu.abi")).Out.Trim();
        var preferred = cpuAbi switch
        {
            "arm64-v8a" => "arm64",
            "armeabi-v7a" or "armeabi" => "arm",
            "x86_64" => "x86_64",
            _ => "arm64"
        };

        var order = new[] { preferred, "arm64", "arm", "x86_64" }.Distinct().ToArray();
        foreach (var dir in order)
        {
            var p = $"{appBase}/lib/{dir}/liballochook.so";
            var ls = await Run("shell", "ls", p);
            if (ls.Code == 0 && !ls.Out.Contains("No such file"))
                return (p, dir, null);
        }
        return (null, null, $"APK 里没有 liballochook.so（已检查 {appBase}/lib/{{{string.Join(",", order)}}}）");
    }

    /// <summary> 带 Unity 命令行参数启动（-systemallocator 调试包用） </summary>
    public async Task<(int, string, string)> LaunchWithUnityArgs(string pkg, string args, string? activity = null)
    {
        var act = string.IsNullOrWhiteSpace(activity) ? await ResolveUnityActivity(pkg) : activity;
        return await Run("shell", "am", "start", "-n", act!, "-e", "unity", args);
    }

    public async Task<string> Pid(string pkg)
    {
        var (_, o, _) = await Run("shell", "pidof", pkg);
        return o.Trim();
    }

    /// <summary> 在应用私有目录创建一个控制文件（run-as 的 cwd 就是 /data/data/&lt;pkg&gt;） </summary>
    public async Task<bool> TouchInAppDir(string pkg, string fileName)
    {
        var (code, _, err) = await Run("shell", "run-as", pkg, "touch", fileName);
        if (code != 0) { Console.WriteLine($"[adb] touch {fileName} failed: {err}"); }
        return code == 0;
    }

    /// <summary> 往应用私有目录写一个文本文件（base64 传输，避免引号和换行被 shell 吃掉） </summary>
    public async Task<bool> WriteInAppDir(string pkg, string fileName, string content)
    {
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content));
        var cmd = $"run-as {pkg} sh -c 'echo {b64} | base64 -d > {fileName}'";
        var (code, _, err) = await Run("shell", cmd);
        if (code != 0) Console.WriteLine($"[adb] write {fileName} failed: {err}");
        return code == 0;
    }

    public async Task<byte[]> CatFromAppDir(string pkg, string fileName)
        => await ExecOutBytes("exec-out", "run-as", pkg, "cat", fileName);

    public async Task<Dictionary<string, long>> MemInfo(string pkg)
    {
        var d = new Dictionary<string, long>();
        var (code, o, _) = await Run("shell", "dumpsys", "meminfo", pkg);
        if (code != 0) return d;

        bool inAppSummary = false;
        foreach (var rawLine in o.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed == "App Summary") { inAppSummary = true; continue; }
            if (trimmed == "Objects" || trimmed == "SQL" || trimmed == "DATABASES") { inAppSummary = false; }

            var totals = Regex.Matches(line, @"(TOTAL PSS|TOTAL RSS|TOTAL SWAP PSS):\s+(\d+)");
            if (totals.Count > 0)
            {
                foreach (Match tm in totals)
                    d[tm.Groups[1].Value] = long.Parse(tm.Groups[2].Value);
                inAppSummary = false;
                continue;
            }

            if (inAppSummary)
            {
                // App Summary 冒号行（仅取 Pss(KB) 列；Unknown: 仅右侧有 Rss(KB)，不误取）
                var ms = Regex.Match(line, @"^\s*(Java Heap|Native Heap|Code|Stack|Graphics|Private Other|System):\s{1,20}(\d+)");
                if (ms.Success)
                    d.TryAdd(ms.Groups[1].Value, long.Parse(ms.Groups[2].Value));
                continue;
            }

            // 详细表行（EGL mtrack / GL mtrack / Unknown / .so mmap / Other mmap 等）
            var m2 = Regex.Match(line, @"^\s*([A-Za-z.][A-Za-z0-9. _-]*?)\s+(\d+)\s+(\d+)\s+(\d+)");
            if (m2.Success)
            {
                var key = m2.Groups[1].Value.Trim();
                if (key != "TOTAL")
                    d[key] = long.Parse(m2.Groups[2].Value);
            }
        }
        return d;
    }
}
