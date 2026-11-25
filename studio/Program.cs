using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using AllocHookStudio;
using Microsoft.AspNetCore.ResponseCompression;

// Windows 控制台禁用 QuickEdit：避免用户误点击黑框时阻塞 Console.WriteLine / Kestrel 响应
if (OperatingSystem.IsWindows())
{
    try
    {
        var hIn = WinConsole.GetStdHandle(-10);
        if (hIn != IntPtr.Zero && hIn != new IntPtr(-1) && WinConsole.GetConsoleMode(hIn, out uint mode))
        {
            const uint ENABLE_QUICK_EDIT_MODE = 0x0040;
            const uint ENABLE_EXTENDED_FLAGS = 0x0080;
            WinConsole.SetConsoleMode(hIn, (mode & ~ENABLE_QUICK_EDIT_MODE) | ENABLE_EXTENDED_FLAGS);
        }
    }
    catch { }
}

var builder = WebApplication.CreateBuilder(args);
// APK 动辄 400MB+，放开请求体上限（仅本地回环地址，风险可控）
builder.WebHost.ConfigureKestrel(o => { o.Limits.MaxRequestBodySize = null; });
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.Providers.Add<BrotliCompressionProvider>();
    o.Providers.Add<GzipCompressionProvider>();
    o.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[] { "application/json", "text/markdown" });
});
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

var app = builder.Build();
app.UseResponseCompression();
app.UseDefaultFiles();
// 开发工具必须禁用缓存：否则浏览器会一直用旧版 app.js，改了等于没改
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        ctx.Context.Response.Headers["Pragma"] = "no-cache";
        ctx.Context.Response.Headers["Expires"] = "0";
    }
});

// 数据目录放在工程目录下（studio/data），而不是 bin 里，避免 clean/rebuild 后快照丢失
var dataRoot = Path.Combine(builder.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataRoot);
var cfgPath = Path.Combine(dataRoot, "config.json");
var store = new Store(Path.Combine(dataRoot, "snapshots"));
var cfg = Config.Load(cfgPath);
int listenPort = cfg.Port;
bool noOpen = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out int p) && p > 0) listenPort = p;
    else if (args[i] == "--no-open") noOpen = true;
}
var adb = new Adb(cfg);
var pending = new ConcurrentDictionary<string, (Snapshot Baseline, DateTime At)>();

// 组装版本戳：用于确认"浏览器连的是不是刚编出来的这个进程"
string BuildStamp()
{
    try
    {
        var loc = System.Reflection.Assembly.GetExecutingAssembly().Location;
        return File.Exists(loc) ? File.GetLastWriteTime(loc).ToString("yyyy-MM-dd HH:mm:ss") : "unknown";
    }
    catch { return "unknown"; }
}
var startedAt = DateTime.Now;

// 把 libunity 里 MemoryManager::Allocate / Deallocate 的偏移写到设备上，
// 原生层据此做 inline hook（这两个函数不在动态符号表，只能按偏移改入口）。
async Task<string> EnsureUnityOffsets(string pkg)
{
    try
    {
        var o = UnitySymbols.Resolve(cfg.SymbolsDir, LlvmDir());
        if (o == null) return "未解析：符号目录里没有 libunity.so，或缺少 llvm-nm";

        var ok = await adb.WriteInAppDir(pkg, "allochook_unity.txt", UnitySymbols.ToFileContent(o));
        return ok ? ("已写入 " + o.Detail.Replace("\n", " | ")) : "写入设备失败";
    }
    catch (Exception e) { return "异常: " + e.Message; }
}

// 疑似标记：区分"真增长"和"池化/抖动"。标记只影响展示，不改变统计口径。
static void ComputeSuspects(List<StackRecord> stacks)
{
    // L1：同一条栈既大量提交又大量归还 → 抖动/池化，不能当泄漏
    foreach (var g in stacks.Where(s => s.Layer == 1).GroupBy(s => string.Join("\u0001", s.Frames)))
    {
        long commit = g.Where(x => x.Kind == 1).Sum(x => x.Bytes);
        long release = g.Where(x => x.Kind == 2).Sum(x => x.Bytes);
        if (commit > 0 && release * 2 >= commit)
            foreach (var s in g) s.Suspect = "churn";
    }

    // L2：已释放字节 ≥ 存活量 × 2 → 明显是复用池
    foreach (var s in stacks.Where(x => x.Layer == 2))
    {
        if (s.Aux > 0 && s.Aux >= Math.Max(s.Bytes, 1) * 2) s.Suspect = "pool";
    }
}

string LlvmDir()
{
    if (!string.IsNullOrWhiteSpace(cfg.LlvmDir) && Directory.Exists(cfg.LlvmDir)) return cfg.LlvmDir;
    foreach (var guess in new[]
    {
        Path.Combine(cfg.SymbolsDir ?? "", "..\\..\\..\\..\\..\\NDK\\toolchains\\llvm\\prebuilt\\windows-x86_64\\bin"),
        @"E:\UnityBuilds\2022.3.62f3\Editor\Data\PlaybackEngines\AndroidPlayer\NDK\toolchains\llvm\prebuilt\windows-x86_64\bin",
    })
    {
        try { if (Directory.Exists(guess) && File.Exists(Path.Combine(guess, "llvm-addr2line.exe"))) return Path.GetFullPath(guess); } catch { }
    }
    return "";
}

// ---------------- API ----------------
app.MapGet("/api/version", () => Results.Ok(new
{
    build = BuildStamp(),
    startedAt,
    contentRoot = app.Environment.ContentRootPath,
    dataRoot,
    port = listenPort,
    pid = Environment.ProcessId,
}));

app.MapGet("/api/state", async () =>
{
    var devices = await adb.Devices();
    var llvm = LlvmDir();
    return Results.Json(new
    {
        config = cfg,
        devices,
        llvmDir = llvm,
        symbolsDirOk = !string.IsNullOrWhiteSpace(cfg.SymbolsDir) && Directory.Exists(cfg.SymbolsDir),
        snapshots = store.List(),
    });
});

app.MapPost("/api/config", (AppConfig c) =>
{
    cfg.AdbPath = c.AdbPath; cfg.PackageName = c.PackageName; cfg.ApkDir = c.ApkDir;
    cfg.SymbolsDir = c.SymbolsDir; cfg.LlvmDir = c.LlvmDir; cfg.Rules = c.Rules ?? CategoryRule.Defaults();
    if (c.IgnoreFramePatterns != null) cfg.IgnoreFramePatterns = c.IgnoreFramePatterns;
    Config.Save(cfgPath, cfg);
    SnapshotQuery.BumpConfigVersion();
    return Results.Ok(new { ok = true });
});

object ListApks(string? dir)
{
    if (string.IsNullOrWhiteSpace(dir))
        return new { ok = false, dir = "", error = "未填写 APK 目录", files = Array.Empty<object>() };

    if (!Directory.Exists(dir))
        return new { ok = false, dir, error = "目录不存在", files = Array.Empty<object>() };

    var files = Directory.EnumerateFiles(dir, "*.apk", SearchOption.TopDirectoryOnly)
        .Select(f => new FileInfo(f))
        .OrderByDescending(f => f.LastWriteTime)
        .Select(f => new { path = f.FullName, name = f.Name, mb = Math.Round(f.Length / 1024.0 / 1024.0, 1), time = f.LastWriteTime })
        .ToList();

    return new { ok = true, dir, error = files.Count == 0 ? "该目录下没有 .apk" : "", files };
}

// 用已保存的目录
app.MapGet("/api/apks", () => Results.Ok(ListApks(cfg.ApkDir)));

// 用界面上当前输入的目录（不强制先保存）
app.MapPost("/api/apks", (ApkDirReq r) => Results.Ok(ListApks(r.Dir)));

// 上传本地 APK（浏览器只能通过上传访问任意路径的文件）
app.MapPost("/api/upload", async (HttpRequest request, string? name) =>
{
    try
    {
        var dir = Path.Combine(dataRoot, "uploads");
        Directory.CreateDirectory(dir);

        var safe = Path.GetFileName(string.IsNullOrWhiteSpace(name) ? "upload.apk" : name);
        if (!safe.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) safe += ".apk";

        // 同名文件覆盖前先删，避免旧包残留
        var target = Path.Combine(dir, safe);
        if (File.Exists(target)) File.Delete(target);

        await using (var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            await request.Body.CopyToAsync(fs);
        }

        var mb = Math.Round(new FileInfo(target).Length / 1024.0 / 1024.0, 1);
        return Results.Ok(new { ok = true, path = target, name = safe, mb });
    }
    catch (Exception e)
    {
        return Results.BadRequest(new { ok = false, error = e.Message });
    }
});

app.MapPost("/api/install", async (InstallReq req) =>
{
    var (code, o, e) = await adb.Install(req.Apk);
    var (pkg, _) = ApkInfo.ReadPackage(req.Apk);
    return Results.Ok(new { ok = code == 0, output = o + e, package = pkg });
});

// 从 APK 里读包名，避免手填
app.MapPost("/api/apk-info", (ApkPathReq r) =>
{
    var (pkg, err) = ApkInfo.ReadPackage(r.Path ?? "");
    return Results.Ok(new { ok = pkg != null, package = pkg, error = err });
});

// 统一启动：支持 HookTiming (standard | jdwp) × Mode (normal | systemallocator) 正交组合
async Task<IResult> LaunchAppAsync(LaunchReq? req, string defaultTiming = "standard")
{
    try
    {
        var pkg = (req?.Pkg ?? "").Trim();
        if (string.IsNullOrEmpty(pkg)) pkg = cfg.PackageName;
        if (string.IsNullOrWhiteSpace(pkg)) return Results.BadRequest(new { error = "请先填写包名" });
        if (pkg != cfg.PackageName) { cfg.PackageName = pkg; Config.Save(cfgPath, cfg); }

        var timing = string.Equals(req?.HookTiming ?? defaultTiming, "jdwp", StringComparison.OrdinalIgnoreCase)
            ? "jdwp" : "standard";
        var sysAlloc = string.Equals(req?.Mode, "systemallocator", StringComparison.OrdinalIgnoreCase);
        var l2Min = Math.Clamp(req?.MinBytes ?? 4096, 0, 64 * 1024 * 1024);
        var l2Frames = Math.Clamp(req?.Frames ?? 16, 1, 32);
        var l2Sample = Math.Clamp(req?.Sample ?? 8, 1, 1024);
        int patchDelayMs = Math.Clamp(req?.PatchDelayMs ?? (timing == "jdwp" ? 0 : 500), 0, 60000);
        int warmupMs = Math.Clamp(req?.WarmupMs ?? 500, 0, 60000);

        var cfgText =
            $"l2={(sysAlloc ? 1 : 0)}\n" +
            $"l2_min_bytes={l2Min}\n" +
            $"l2_frames={l2Frames}\n" +
            $"l2_sample={l2Sample}\n" +
            "hold=0\n" +
            $"patch_delay_ms={patchDelayMs}\n" +
            $"warmup_ms={warmupMs}\n";
        await adb.WriteInAppDir(pkg, "allochook.cfg", cfgText);

        var offsets = await EnsureUnityOffsets(pkg);

        if (timing == "standard")
        {
            await adb.ForceStop(pkg);
            await Task.Delay(300);
            var (code, o, e) = sysAlloc
                ? await adb.LaunchWithUnityArgs(pkg, "-systemallocator")
                : await adb.Launch(pkg);
            return Results.Ok(new
            {
                ok = code == 0,
                output = o + e,
                package = pkg,
                hookTiming = timing,
                mode = sysAlloc ? "systemallocator" : "normal",
                l2 = sysAlloc,
                l2Min,
                l2Frames,
                l2Sample,
                patchDelayMs,
                warmupMs,
                unityOffsets = offsets,
            });
        }

        // JDWP 最早时机：am start -D 挂起 → JDWP 注入 APK 自带的 liballochook.so → resume
        var (libPath, abiDir, libErr) = await adb.ResolveAllochookLibPath(pkg);
        if (libPath == null)
            return Results.BadRequest(new { error = libErr ?? "APK 里没有 liballochook.so（检查 UPM 包与 ABI）" });

        var activity = sysAlloc
            ? await adb.ResolveUnityActivity(pkg)
            : (await adb.ResolveLauncherActivity(pkg) ?? await adb.ResolveUnityActivity(pkg));
        if (string.IsNullOrWhiteSpace(activity))
            return Results.BadRequest(new { error = "找不到启动 Activity" });

        await adb.ForceStop(pkg);
        await Task.Delay(400);

        var start = sysAlloc
            ? await adb.Run("shell", "am", "start", "-D", "-n", activity, "-e", "unity", "-systemallocator")
            : await adb.Run("shell", "am", "start", "-D", "-n", activity);
        if (start.Code != 0)
            return Results.BadRequest(new { error = "am start -D 失败", activity, output = start.Out + start.Err });

        string pid = "";
        for (int i = 0; i < 40 && pid == ""; i++)
        {
            await Task.Delay(150);
            pid = (await adb.Run("shell", "pidof", pkg)).Out.Trim()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        }
        if (pid == "") return Results.BadRequest(new { error = "进程未起来（该 ROM 可能不支持 -D）", activity });

        const int port = 8700;
        await adb.Run("forward", "--remove", "tcp:" + port);
        var fwd = await adb.Run("forward", "tcp:" + port, "jdwp:" + pid);
        if (fwd.Code != 0)
            return Results.BadRequest(new { error = "adb forward 失败", output = fwd.Out + fwd.Err, pid, activity });

        string inject, debug = "";
        JdwpClient? jdwp = null;
        try
        {
            jdwp = JdwpClient.Attach(port);
            jdwp.LoadLibrary(libPath);
            jdwp.Resume();
            debug = jdwp.DebugInfo;
            inject = "ok";
        }
        catch (Exception ex)
        {
            inject = "fail: " + ex.Message;
            if (jdwp != null)
            {
                try { jdwp.Resume(); } catch { }
                debug = jdwp.DebugInfo;
            }
        }
        finally
        {
            try { jdwp?.Dispose(); } catch { }
            try { await adb.Run("forward", "--remove", "tcp:" + port); } catch { }
        }

        await Task.Delay(1800);
        var log = await adb.Run("logcat", "-d", "-t", "1200");
        var hits = log.Out.Split('\n').Where(l => l.Contains("AllocHook")).TakeLast(12).ToList();

        return Results.Ok(new
        {
            ok = inject == "ok",
            package = pkg,
            hookTiming = timing,
            mode = sysAlloc ? "systemallocator" : "normal",
            l2 = sysAlloc,
            l2Min,
            l2Frames,
            l2Sample,
            patchDelayMs,
            warmupMs,
            abi = abiDir,
            activity,
            pid,
            libPath,
            inject,
            debug,
            log = hits,
            unityOffsets = offsets,
        });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
}

// 兼容旧版 /api/inject-launch，同时复用统一启动实现
app.MapPost("/api/inject-launch", (LaunchReq? req) => LaunchAppAsync(req, defaultTiming: "jdwp"));

app.MapPost("/api/inject-stop", async (PkgReq? req) =>
{
    var pkg = (req?.Pkg ?? "").Trim();
    if (string.IsNullOrEmpty(pkg)) pkg = cfg.PackageName;
    if (!string.IsNullOrWhiteSpace(pkg)) await adb.ForceStop(pkg);
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/launch", (LaunchReq? req) => LaunchAppAsync(req, defaultTiming: "standard"));

// 供界面显示 / 手动刷新偏移
app.MapPost("/api/unity-offsets", async (PkgReq? req) =>
{
    var pkg = (req?.Pkg ?? "").Trim();
    if (string.IsNullOrEmpty(pkg)) pkg = cfg.PackageName;
    if (string.IsNullOrWhiteSpace(pkg)) return Results.BadRequest(new { error = "请先填写包名" });
    var detail = await EnsureUnityOffsets(pkg);
    return Results.Ok(new { ok = !detail.StartsWith("未解析") && !detail.StartsWith("写入失败"), detail });
});

// 基于唯一 PC 批量校验与符号化，将 488k 次检查压缩到 16k 次并避免对每条栈重复构造字符串
static List<StackRecord> SymbolizeRecords(
    List<RawRecord> records,
    Symbolizer sym,
    List<MapRange> mapRanges,
    Dictionary<ulong, string> symMap,
    int maxFrames,
    out int unresolved,
    out int unverifiedTotal)
{
    var returnPcs = new HashSet<ulong>();
    foreach (var r in records)
    {
        for (int i = 1; i < r.Pcs.Length; i++)
            if (r.Pcs[i] != 0) returnPcs.Add(r.Pcs[i]);
    }
    var badPcs = sym.CheckUntrustedPcs(returnPcs, mapRanges);
    var rawCache = new Dictionary<ulong, string>(badPcs.Count);
    foreach (var pc in badPcs) rawCache[pc] = Symbolizer.RawFrame(pc, mapRanges);

    int capFrames = maxFrames > 0 ? maxFrames : 32;
    unresolved = 0;
    unverifiedTotal = 0;
    var list = new List<StackRecord>(records.Count);

    foreach (var r in records)
    {
        bool isPss = r.Kind == 5;
        if (isPss && r.Pcs.Length == 0)
        {
            list.Add(new StackRecord
            {
                Kind = r.Kind,
                Layer = 1,
                Thread = "",
                Label = r.Thread,
                Bytes = r.Bytes,
                Count = r.Count,
                Aux = r.Aux,
                Pcs = new List<ulong>(),
                Frames = new List<string>(),
            });
            continue;
        }

        int take = Math.Min(r.Pcs.Length, capFrames);
        var frames = new List<string>(take);
        for (int idx = 0; idx < take; idx++)
        {
            ulong pc = r.Pcs[idx];
            if (idx >= 1 && badPcs.Contains(pc))
            {
                unverifiedTotal++;
                frames.Add(rawCache[pc]);
            }
            else
            {
                frames.Add(symMap.TryGetValue(pc, out var n) ? n : $"0x{pc:x}");
            }
        }
        if (frames.Count > 0 && (frames[0].StartsWith("(") || frames[0].Contains("+0x"))) unresolved++;

        list.Add(new StackRecord
        {
            Kind = r.Kind,
            Layer = (r.Kind == 3 || r.Kind == 6) ? 2 : 1,
            Thread = r.Thread,
            Label = "",
            Bytes = r.Bytes,
            Count = r.Count,
            Aux = r.Aux,
            Pcs = r.Pcs.ToList(),
            Frames = frames,
        });
    }
    return list;
}

app.MapPost("/api/snapshot", async (SnapshotReq req) =>
{
    try
    {
        var pkg = (req.Pkg ?? "").Trim();
        if (string.IsNullOrEmpty(pkg)) pkg = cfg.PackageName;
        if (string.IsNullOrWhiteSpace(pkg)) return Results.BadRequest(new { error = "请先填写包名" });
        if (pkg != cfg.PackageName) { cfg.PackageName = pkg; Config.Save(cfgPath, cfg); }

        // 保证设备上有最新的分配器偏移（下次启动生效；本次快照也用它判断 hook 是否装上了）
        await EnsureUnityOffsets(pkg);

        var mode = (req.Mode ?? "snapshot").ToLowerInvariant();

        if (mode == "record-start")
        {
            await adb.TouchInAppDir(pkg, "allochook.reset");
            await Task.Delay(400);
            var pss0 = await adb.MemInfo(pkg);
            pending[pkg] = (new Snapshot { Pss = pss0 }, DateTime.Now);
            return Results.Ok(new { ok = true, message = "录制已开始（计数已清零），操作结束后点『停止录制』" });
        }

        // snapshot / record-stop：写信号后精确轮询 allochook.snap 消失（新 .so 在 dumpAll 完成后才删文件）
        await adb.TouchInAppDir(pkg, "allochook.snap");
        var sw = Stopwatch.StartNew();
        bool snapRemoved = false;
        await Task.Delay(60);
        while (sw.ElapsedMilliseconds < 8000)
        {
            var (chkCode, _, _) = await adb.Run("shell", "run-as", pkg, "test", "-f", "allochook.snap");
            if (chkCode != 0) { snapRemoved = true; break; }
            await Task.Delay(80);
        }
        if (!snapRemoved) await Task.Delay(400);

        // 并发拉取 allochook.bin、allochook_maps.txt、dumpsys meminfo
        var binTask = adb.CatFromAppDir(pkg, "allochook.bin");
        var mapsTask = adb.CatFromAppDir(pkg, "allochook_maps.txt");
        var pssTask = adb.MemInfo(pkg);
        await Task.WhenAll(binTask, mapsTask, pssTask);

        var bin = await binTask;
        var mapsBytes = await mapsTask;
        var pss = await pssTask;

        // 兼容老 .so：可能先删了 allochook.snap 还在写 bin，若首次读失败则稍等重试一次
        if (!DumpParser.IsAhk1(bin) && sw.ElapsedMilliseconds < 1200)
        {
            await Task.Delay(900);
            bin = await adb.CatFromAppDir(pkg, "allochook.bin");
            mapsBytes = await adb.CatFromAppDir(pkg, "allochook_maps.txt");
        }

        DumpHeader header;
        List<RawRecord> records;
        string fmt;

        if (DumpParser.IsAhk1(bin))
        {
            try
            {
                (header, records) = DumpParser.Parse(bin);
                fmt = "bin";
            }
            catch
            {
                await Task.Delay(600);
                bin = await adb.CatFromAppDir(pkg, "allochook.bin");
                (header, records) = DumpParser.Parse(bin);
                fmt = "bin";
            }
        }
        else
        {
            // 兼容：设备上装的还是老 .so（只写 txt）
            var txtBytes = await adb.CatFromAppDir(pkg, "allochook.txt");
            (header, records) = DumpParser.ParseText(System.Text.Encoding.UTF8.GetString(txtBytes));
            fmt = "txt";

            if (records.Count == 0)
            {
                string binText = System.Text.Encoding.UTF8.GetString(bin);
                string txtText = System.Text.Encoding.UTF8.GetString(txtBytes);

                string hint;
                if (binText.Contains("No such file") || txtText.Contains("No such file"))
                    hint = "设备上没有 dump 文件：确认安装的是带 liballochook.so 的包，且启动过 App（logcat 应出现 AllocHook 行）";
                else if (binText.Contains("run-as") || txtText.Contains("run-as"))
                    hint = "run-as 失败：该包不是 debuggable（需要 Development Build）";
                else if (binText.Trim().Length == 0 && txtText.Trim().Length == 0)
                    hint = "dump 是空的：钩子还没产出数据（启动后等 15 秒再试）";
                else
                    hint = "dump 内容异常";

                var ls = await adb.Run("shell", "run-as", pkg, "ls", "-l", ".");
                var logCat = await adb.Run("logcat", "-d", "-t", "3000");
                var logHits = logCat.Out.Split('\n')
                    .Where(l => l.Contains("AllocHook"))
                    .TakeLast(15)
                    .ToList();

                return Results.BadRequest(new
                {
                    error = hint,
                    binPreview = DumpParser.Preview(bin),
                    txtPreview = DumpParser.Preview(txtBytes),
                    files = ls.Out,
                    logLines = logHits.Count > 0 ? string.Join("\n", logHits) : "(logcat 里没有 AllocHook 相关日志 → .so 未被加载)",
                });
            }
        }

        string maps = System.Text.Encoding.UTF8.GetString(mapsBytes);
        var pidLine = maps.Split('\n').FirstOrDefault(l => l.StartsWith("# allochook pid="));
        if (pidLine != null && header.Pid > 0 && !pidLine.Contains("pid=" + header.Pid))
        {
            await Task.Delay(300);
            mapsBytes = await adb.CatFromAppDir(pkg, "allochook_maps.txt");
            maps = System.Text.Encoding.UTF8.GetString(mapsBytes);
        }

        var sym = new Symbolizer(cfg, Path.Combine(dataRoot, "symcache"));
        var mapRanges = sym.ParseRanges(maps);
        var allPcs = records.SelectMany(r => r.Pcs).Where(p => p != 0).Distinct().ToList();
        var symMap = await sym.SymbolizeAsync(allPcs, mapRanges, LlvmDir());

        string snapId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        if (File.Exists(Path.Combine(store.Dir, snapId + ".json")))
            snapId += "_" + DateTime.Now.Millisecond.ToString("D3");

        var stacks = SymbolizeRecords(records, sym, mapRanges, symMap, header.MaxFrames, out _, out _);
        ComputeSuspects(stacks);

        var snap = new Snapshot
        {
            Id = snapId,
            Label = (req.Label ?? "").Trim(),
            Time = DateTime.Now,
            Mode = mode == "record-stop" ? "record" : "snapshot",
            Pid = header.Pid,
            Package = pkg,
            Format = fmt,
            Pss = pss,
            L2FilteredBytes = header.L2FilteredBytes,
            L2FilteredCount = header.L2FilteredCount,
            L2SampledBytes = header.L2SampledBytes,
            L2FilteredLiveBytes = header.L2FilteredLiveBytes,
            L2FilteredLiveCount = header.L2FilteredLiveCount,
            L2SampledLiveBytes = header.L2SampledLiveBytes,
            L2SampledLiveCount = header.L2SampledLiveCount,
            NativeHeapAllocBytes = header.NativeHeapAllocBytes,
            L2Min = header.L2Min,
            L2Sample = header.L2Sample,
            Stacks = stacks,
        };

        if (mode == "record-stop" && pending.TryRemove(pkg, out var baseInfo))
        {
            long a = baseInfo.Baseline.Pss.GetValueOrDefault("TOTAL PSS");
            long b = snap.Pss.GetValueOrDefault("TOTAL PSS");
            snap.TotalDeltaKB = b - a;
        }

        store.Save(snap);
        File.WriteAllText(Path.Combine(store.Dir, snap.Id + ".md"), Report.Build(snap, cfg.Rules, cfg.IgnoreFramePatterns));
        // 保存 maps，供"换符号表后重新解析"使用
        File.WriteAllText(Path.Combine(store.Dir, snap.Id + ".maps"), maps);

        return Results.Ok(new { ok = true, id = snap.Id, stacks = snap.Stacks.Count, format = fmt, pss = snap.Pss.GetValueOrDefault("TOTAL PSS") });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message, type = ex.GetType().Name });
    }
});

app.MapGet("/api/snapshots", () => Results.Ok(store.List()));
app.MapGet("/api/snapshot/{id}", (string id) =>
{
    var s = store.Load(id);
    return s == null ? Results.NotFound() : Results.Ok(s);
});
app.MapGet("/api/snapshot/{id}/summary", (string id) =>
{
    var s = store.Load(id);
    return s == null ? Results.NotFound() : Results.Ok(SnapshotQuery.GetSummary(s, cfg));
});
app.MapGet("/api/snapshot/{id}/stacks", (
    string id,
    string? layer,
    int? page,
    int? pageSize,
    string? search,
    string? category,
    bool? hideZero,
    bool? skipLow,
    bool? scaleEst,
    string? sort) =>
{
    var s = store.Load(id);
    if (s == null) return Results.NotFound();
    var req = new StacksQueryReq(
        Layer: layer ?? "1",
        Page: page ?? 1,
        PageSize: pageSize ?? 200,
        Search: search,
        Category: category,
        HideZero: hideZero ?? true,
        SkipLow: skipLow ?? true,
        ScaleEst: scaleEst ?? false,
        Sort: sort ?? "bytes");
    return Results.Ok(SnapshotQuery.QueryStacks(s, cfg, req));
});
app.MapGet("/api/snapshot/{id}/pss/{cat}", (
    string id,
    string cat,
    int? page,
    int? pageSize,
    string? search,
    bool? hideZero,
    bool? skipLow,
    bool? scaleEst) =>
{
    var s = store.Load(id);
    if (s == null) return Results.NotFound();
    var res = SnapshotQuery.QueryPssCategory(
        s, cfg, cat,
        page ?? 1, pageSize ?? 100, search,
        hideZero ?? true, skipLow ?? true, scaleEst ?? false);
    return res == null ? Results.NotFound() : Results.Ok(res);
});
app.MapGet("/api/snapshot/{id}/report", (string id) =>
{
    var sid = Path.GetFileName(id);
    var p = Path.Combine(store.Dir, sid + ".md");
    return File.Exists(p) ? Results.Text(File.ReadAllText(p), "text/markdown; charset=utf-8") : Results.NotFound();
});
app.MapGet("/api/snapshot/{id}/raw-preview", (string id) =>
{
    var s = store.Load(id);
    if (s == null) return Results.NotFound();
    return Results.Ok(new
    {
        s.Id, s.Label, s.Time, s.Pid, s.Package, s.Device, s.Mode, s.Format,
        s.TotalDeltaKB, s.L2Min, s.L2Sample,
        s.L2FilteredBytes, s.L2FilteredCount, s.L2SampledBytes,
        s.L2FilteredLiveBytes, s.L2FilteredLiveCount, s.L2SampledLiveBytes, s.L2SampledLiveCount,
        s.NativeHeapAllocBytes,
        s.Pss,
        totalStacks = s.Stacks.Count,
        previewNote = s.Stacks.Count > 50 ? $"仅预览前 50 条栈（共 {s.Stacks.Count:N0} 条），完整数据请点「下载 .json」" : null,
        stacksPreview = s.Stacks.Take(50).ToList(),
    });
});
app.MapGet("/api/snapshot/{id}/download", (string id, string? format) =>
{
    var sid = Path.GetFileName(id);
    bool isMd = string.Equals(format, "md", StringComparison.OrdinalIgnoreCase);
    var p = Path.Combine(store.Dir, sid + (isMd ? ".md" : ".json"));
    if (!File.Exists(p)) return Results.NotFound();
    return Results.File(
        p,
        contentType: isMd ? "text/markdown; charset=utf-8" : "application/json; charset=utf-8",
        fileDownloadName: sid + (isMd ? ".md" : ".json"));
});

// 换符号表后，对已有快照重新解析（依赖快照里保存的原始地址 + maps）
app.MapPost("/api/snapshot/{id}/resymbolize", async (string id) =>
{
    try
    {
        var s = store.Load(id);
        if (s == null) return Results.NotFound(new { error = "快照不存在" });

        var sid = Path.GetFileName(id);
        var mapsPath = Path.Combine(store.Dir, sid + ".maps");
        if (!File.Exists(mapsPath))
            return Results.BadRequest(new { error = "这份快照没有保存 maps（旧版本抓的），请重新抓一次" });

        var pcs = s.Stacks.SelectMany(x => x.Pcs ?? new List<ulong>()).Where(p => p != 0).Distinct().ToList();
        if (pcs.Count == 0)
            return Results.BadRequest(new { error = "快照里没有原始地址（旧版本抓的），请重新抓一次" });

        var maps = File.ReadAllText(mapsPath);
        var sym = new Symbolizer(cfg, Path.Combine(dataRoot, "symcache"));
        var mapRanges = sym.ParseRanges(maps);
        var symMap = await sym.SymbolizeAsync(pcs, mapRanges, LlvmDir());

        var returnPcs = new HashSet<ulong>();
        foreach (var st in s.Stacks)
        {
            if (st.Pcs == null) continue;
            for (int i = 1; i < st.Pcs.Count; i++)
                if (st.Pcs[i] != 0) returnPcs.Add(st.Pcs[i]);
        }
        var badPcs = sym.CheckUntrustedPcs(returnPcs, mapRanges);
        var rawCache = new Dictionary<ulong, string>(badPcs.Count);
        foreach (var pc in badPcs) rawCache[pc] = Symbolizer.RawFrame(pc, mapRanges);

        int unresolved = 0;
        int unverified = 0;
        foreach (var st in s.Stacks)
        {
            if (st.Kind == 5 && st.Pcs.Count == 0)
            {
                if (string.IsNullOrEmpty(st.Label) && !string.IsNullOrEmpty(st.Thread))
                {
                    st.Label = st.Thread;
                    st.Thread = "";
                }
                st.Frames = new List<string>();
                continue;
            }

            int take = Math.Min(st.Pcs.Count, 32);
            var frames = new List<string>(take);
            for (int idx = 0; idx < take; idx++)
            {
                ulong pc = st.Pcs[idx];
                if (idx >= 1 && badPcs.Contains(pc))
                {
                    unverified++;
                    frames.Add(rawCache[pc]);
                }
                else
                {
                    frames.Add(symMap.TryGetValue(pc, out var n) ? n : $"0x{pc:x}");
                }
            }
            st.Frames = frames;
            if (st.Frames.Count > 0 && (st.Frames[0].StartsWith("(") || st.Frames[0].Contains("+0x"))) unresolved++;
        }

        ComputeSuspects(s.Stacks);
        store.Save(s);
        File.WriteAllText(Path.Combine(store.Dir, s.Id + ".md"), Report.Build(s, cfg.Rules, cfg.IgnoreFramePatterns));

        return Results.Ok(new { ok = true, stacks = s.Stacks.Count, unresolved, unverified, symbolsDir = cfg.SymbolsDir });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapDelete("/api/snapshot/{id}", (string id) =>
{
    var sid = Path.GetFileName(id);
    store.Invalidate(sid);
    foreach (var ext in new[] { ".json", ".md", ".maps" })
    {
        var p = Path.Combine(store.Dir, sid + ext);
        if (File.Exists(p)) File.Delete(p);
    }
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/diff", (DiffReq req) =>
{
    var a = store.Load(req.A);
    var b = store.Load(req.B);
    if (a == null || b == null) return Results.BadRequest(new { error = "快照不存在" });
    return Results.Ok(SnapshotQuery.QueryDiff(a, b, cfg, req));
});

app.MapGet("/api/snapshot/{id}/categories", (string id) =>
{
    var s = store.Load(id);
    if (s == null) return Results.NotFound();
    return Results.Ok(Classifier.Categorize(s, cfg.Rules, cfg.IgnoreFramePatterns));
});

// ---------------- CLI: 解包 ----------------
if (args.Length > 0 && args[0] == "--unpack")
{
    var bin = args[1];
    var outp = args.Length > 2 ? args[2] : Path.ChangeExtension(bin, ".json");
    var data = File.ReadAllBytes(bin);
    var (h, recs) = DumpParser.Parse(data);
    var mods = h.Mods.Select(m => new { m.p, b = m.b });
    var json = JsonSerializer.Serialize(new
    {
        header = new { h.Pid, h.Ts, h.Level, h.MaxFrames, h.MinBytes, mods },
        records = recs.Select(r => new { kind = r.Kind, thread = r.Thread, bytes = r.Bytes, count = r.Count, pcs = r.Pcs.Select(p => $"0x{p:x}") }),
    }, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(outp, json);
    Console.WriteLine($"unpacked -> {outp}  ({recs.Count} records)");
    return;
}

var url = $"http://127.0.0.1:{listenPort}";
Console.WriteLine($"AllocHookStudio 启动: {url}  (build={BuildStamp()}, pid={Environment.ProcessId})");
if (!noOpen)
{
    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
}

try
{
    app.Run(url);
}
catch (IOException e)
{
    Console.WriteLine();
    Console.WriteLine($"!! 端口 {listenPort} 被占用（很可能是之前启动的旧版 Studio 还在跑）");
    Console.WriteLine($"!! {e.Message}");
    Console.WriteLine("!! 处理：任务管理器结束 dotnet/AllocHookStudio 进程，或改 studio/data/config.json 里的 port");
    Console.WriteLine("!! 提示：旧进程会继续用旧代码响应浏览器，看起来就像“改了没生效”");
}
catch (Exception e)
{
    Console.WriteLine("启动失败: " + e);
}

// ---------------- DTO ----------------
public record InstallReq(string Apk);
public record ApkDirReq(string? Dir);
public record ApkPathReq(string? Path);
public record PkgReq(string? Pkg);
public record LaunchReq(
    string? Pkg,
    string? Mode = null,
    string? HookTiming = null,
    int? MinBytes = null,
    int? Sample = null,
    int? Frames = null,
    int? PatchDelayMs = null,
    int? WarmupMs = null);
public record SnapshotReq(string? Label, string? Mode, string? Pkg);

public static class WinConsole
{
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}

public static class Config
{
    private static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };
    public static AppConfig Load(string path)
    {
        if (!File.Exists(path)) return new AppConfig();
        try
        {
            var c = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path)) ?? new AppConfig();
            if (c.Rules == null || c.Rules.Count == 0) c.Rules = CategoryRule.Defaults();
            if (c.IgnoreFramePatterns == null || c.IgnoreFramePatterns.Count == 0)
                c.IgnoreFramePatterns = AppConfig.DefaultIgnoreFramePatterns();
            return c;
        }
        catch { return new AppConfig(); }
    }
    public static void Save(string path, AppConfig cfg)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(cfg, Opt));
    }
}
