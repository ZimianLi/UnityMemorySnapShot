using System.Net.Sockets;
using System.Text;

namespace AllocHookStudio;

/// <summary>
/// 最小 JDWP 客户端：只做一件事——在 App 启动的极早期（am start -D 挂起状态）把 .so 塞进进程。
/// 走的是 debuggable App 的调试通道，不需要 root、不需要重打包。
/// </summary>
public sealed class JdwpClient : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _s;
    private int _nextId = 1;

    private int _fieldIdSize = 4;
    private int _methodIdSize = 4;
    private int _objectIdSize = 4;
    private int _refTypeIdSize = 4;

    /// <summary> 诊断信息（注入失败时回传，便于定位） </summary>
    public string DebugInfo { get; private set; } = "";

    private ulong _systemClass;
    private ulong _systemLoadMethod;
    private ulong _systemCmMethod;
    private ulong _runtimeClass;
    private ulong _getRuntimeMethod;
    private ulong _loadMethod;
    private ulong _mainThread;
    private readonly List<ulong> _threads = new();
    private readonly Queue<(byte CmdSet, byte Cmd, byte[] Body)> _events = new();
    private readonly List<(byte Kind, int Id)> _activeRequests = new();
    private bool _eventFallbackTried;
    private bool _resumed;

    private JdwpClient(TcpClient tcp, NetworkStream s)
    {
        _tcp = tcp;
        _s = s;
    }

    public static JdwpClient Attach(int port, int timeoutMs = 8000)
    {
        var tcp = new TcpClient();
        var connect = tcp.ConnectAsync("127.0.0.1", port);
        if (!connect.Wait(timeoutMs)) throw new TimeoutException("连接 JDWP 超时（端口未转发或进程未挂起）");

        var s = tcp.GetStream();
        s.ReadTimeout = timeoutMs;
        s.WriteTimeout = timeoutMs;

        var hs = Encoding.ASCII.GetBytes("JDWP-Handshake");
        s.Write(hs, 0, hs.Length);
        s.Flush();

        var buf = new byte[14];
        ReadExact(s, buf, 14);
        if (Encoding.ASCII.GetString(buf) != "JDWP-Handshake")
            throw new InvalidDataException("JDWP 握手失败");

        var c = new JdwpClient(tcp, s);
        c.Init();
        return c;
    }

    private void Init()
    {
        // IDSizes
        var ids = Send(1, 7, Array.Empty<byte>());
        int p = 0;
        _fieldIdSize = ReadInt(ids, ref p);
        _methodIdSize = ReadInt(ids, ref p);
        _objectIdSize = ReadInt(ids, ref p);
        _refTypeIdSize = ReadInt(ids, ref p);
        int frameIdSize = ReadInt(ids, ref p);
        DebugInfo = $"ids: field={_fieldIdSize} method={_methodIdSize} obj={_objectIdSize} ref={_refTypeIdSize} frame={frameIdSize}";

        // 全挂起：am start -D 时 VM 可能仍在"等待调试器"状态，只做一次 VM.Suspend，绝不再对 main 双重挂起
        try { Send(1, 8, Array.Empty<byte>()); DebugInfo += " | suspend ok"; }
        catch (Exception ex) { DebugInfo += " | suspend fail: " + ex.Message; }

        // java.lang.System.load(String) —— 静态方法，不需要先拿 Runtime 实例
        _systemClass = FindClass("Ljava/lang/System;") ?? throw new Exception("找不到 java.lang.System");
        var sm = FindMethods(_systemClass);
        foreach (var (id, name, sig) in sm)
        {
            if (name == "load" && sig == "(Ljava/lang/String;)V") _systemLoadMethod = id;
            else if (name == "currentTimeMillis" && sig == "()J") _systemCmMethod = id;
        }
        if (_systemLoadMethod == 0) throw new Exception("System.load(String) 未找到");

        // 兜底：java.lang.Runtime.getRuntime().load(String)
        _runtimeClass = FindClass("Ljava/lang/Runtime;") ?? 0;
        if (_runtimeClass != 0)
        {
            foreach (var (id, name, sig) in FindMethods(_runtimeClass))
            {
                if (name == "getRuntime" && sig == "()Ljava/lang/Runtime;") _getRuntimeMethod = id;
                if (name == "load" && sig == "(Ljava/lang/String;)V") _loadMethod = id;
            }
        }

        // 线程列表，优先选名字叫 "main" 的
        var th = Send(1, 4, Array.Empty<byte>()); // AllThreads
        int t = 0;
        int tc = ReadInt(th, ref t);
        if (tc <= 0) throw new Exception("取不到线程列表");
        for (int i = 0; i < tc; i++) _threads.Add(ReadObjId(th, ref t));

        _mainThread = _threads[0];
        foreach (var tid in _threads)
        {
            try
            {
                var nb = new List<byte>(); WriteObjId(nb, tid);
                var nr = Send(11, 1, nb.ToArray());   // ThreadReference.Name
                int np = 0;
                string nm = ReadString(nr, ref np);
                if (nm == "main") { _mainThread = tid; break; }
            }
            catch { }
        }

        int mainSc = GetSuspendCount(_mainThread);
        DebugInfo += $" | threads={tc} chosen=0x{_mainThread:x}(sc={mainSc})";

        // 优先仅在 main 线程探测；避免误跳转到后台线程导致 main 挂起计数失衡
        if (ProbeThread(_mainThread, out string mainErr))
        {
            DebugInfo += $" | probe ok main=0x{_mainThread:x}";
            return;
        }
        DebugInfo += $" | probe main fail: {mainErr}";

        // 某些 ROM（如华为 Android 12）在非 JDWP 事件挂起态拒绝 InvokeMethod：
        // 先彻底释放 main 挂起计数并用 ThreadOnly 事件把 main 精确停在 METHOD_ENTRY/CLASS_PREPARE 上
        if (TryParkOnJdwpEvent() && ProbeThread(_mainThread, out string parkedErr))
        {
            DebugInfo += $" | probe ok after event main=0x{_mainThread:x}";
            return;
        }

        // 仅当主线程事件兜底仍不可用时，才回退尝试其他线程
        string lastOtherErr = "";
        foreach (var tid in _threads)
        {
            if (tid == _mainThread) continue;
            if (ProbeThread(tid, out lastOtherErr))
            {
                _mainThread = tid;
                DebugInfo += $" | fallback probe ok thread=0x{tid:x}";
                return;
            }
        }
        DebugInfo += " | probe fail all threads: " + lastOtherErr;
    }

    private int GetSuspendCount(ulong threadId)
    {
        try
        {
            var b = new List<byte>();
            WriteObjId(b, threadId);
            var r = Send(11, 12, b.ToArray()); // ThreadReference.SuspendCount
            int p = 0;
            return ReadInt(r, ref p);
        }
        catch { return -1; }
    }

    private void ResumeThread(ulong threadId)
    {
        var b = new List<byte>();
        WriteObjId(b, threadId);
        Send(11, 3, b.ToArray()); // ThreadReference.Resume
    }

    private bool ProbeThread(ulong tid, out string err)
    {
        err = "";
        if (_systemCmMethod == 0) return true;
        try
        {
            var cb = new List<byte>();
            WriteRefId(cb, _systemClass);
            WriteObjId(cb, tid);
            WriteMethodId(cb, _systemCmMethod);
            WriteInt(cb, 0);
            WriteInt(cb, 0);
            Send(3, 3, cb.ToArray());
            return true;
        }
        catch (Exception ex)
        {
            err = ex.Message;
            return false;
        }
    }

    private int? SetEventRequest(byte eventKind, byte suspendPolicy, ulong threadOnly = 0)
    {
        try
        {
            var b = new List<byte> { eventKind, suspendPolicy };
            if (threadOnly != 0)
            {
                WriteInt(b, 1);
                b.Add(3); // ThreadOnly modifier
                WriteObjId(b, threadOnly);
            }
            else
            {
                WriteInt(b, 0);
            }
            var r = Send(15, 1, b.ToArray()); // EventRequest.Set
            int p = 0;
            int reqId = ReadInt(r, ref p);
            _activeRequests.Add((eventKind, reqId));
            return reqId;
        }
        catch (Exception ex)
        {
            DebugInfo += $" | EventReq({eventKind},th=0x{threadOnly:x}) err: {ex.Message}";
            return null;
        }
    }

    private void ClearActiveEventRequests()
    {
        if (_activeRequests.Count == 0) return;
        foreach (var (k, reqId) in _activeRequests.ToList())
        {
            try
            {
                var b = new List<byte> { k };
                WriteInt(b, reqId);
                Send(15, 2, b.ToArray()); // EventRequest.Clear
            }
            catch { }
        }
        _activeRequests.Clear();
    }

    private bool TryDequeueTargetEvent(ulong targetThread, bool requireTargetThread, out byte hitKind, out ulong hitThread)
    {
        hitKind = 0;
        hitThread = 0;
        while (_events.Count > 0)
        {
            var ev = _events.Dequeue();
            if (ev.CmdSet == 64 && ev.Cmd == 100 && TryExtractSuspendedEventThread(ev.Body, out byte kind, out ulong tid))
            {
                if (!requireTargetThread || tid == targetThread)
                {
                    hitKind = kind;
                    hitThread = tid;
                    return true;
                }
            }
        }
        return false;
    }

    private bool TryParkOnJdwpEvent()
    {
        if (_eventFallbackTried) return false;
        _eventFallbackTried = true;

        ulong targetMain = _mainThread;
        bool usedThreadOnly = true;

        // 优先用 ThreadOnly(3) 精准绑定 main 线程注册 METHOD_ENTRY(40)，避免后台线程抢先触发
        if (SetEventRequest(40, 2, targetMain) == null)
        {
            if (SetEventRequest(8, 2, targetMain) == null)
            {
                usedThreadOnly = false;
                if (SetEventRequest(40, 2, 0) == null)
                    SetEventRequest(8, 2, 0);
            }
        }
        if (_activeRequests.Count == 0) return false;

        _events.Clear();
        try { Send(1, 9, Array.Empty<byte>()); } // VirtualMachine.Resume
        catch (Exception ex)
        {
            DebugInfo += " | event fallback VM.Resume err: " + ex.Message;
            ClearActiveEventRequests();
            return false;
        }

        ulong eventThread = 0;
        byte hitKind = 0;
        int prevTimeout = _s.ReadTimeout;
        try
        {
            // 若 main 在 am start -D + VM.Suspend 后仍有多余挂起计数，先通过 SuspendCount(11/12) 精确解挂到 0
            for (int step = 0; step < 12; step++)
            {
                if (TryDequeueTargetEvent(targetMain, usedThreadOnly, out hitKind, out eventThread))
                    break;
                int sc = GetSuspendCount(targetMain);
                if (TryDequeueTargetEvent(targetMain, usedThreadOnly, out hitKind, out eventThread))
                    break;
                if (sc <= 0) break;
                DebugInfo += $" | drain main sc={sc}";
                ResumeThread(targetMain);
            }

            if (eventThread == 0)
            {
                _s.ReadTimeout = 4000;
                var deadline = DateTime.UtcNow.AddMilliseconds(4000);
                while (DateTime.UtcNow < deadline)
                {
                    if (_events.Count == 0)
                    {
                        var pkt = ReadOnePacket();
                        if ((pkt.Flags & 0x80) == 0)
                            _events.Enqueue((pkt.CmdSet, pkt.Cmd, pkt.Body));
                    }

                    if (TryDequeueTargetEvent(targetMain, usedThreadOnly, out hitKind, out eventThread))
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            DebugInfo += " | wait event err: " + ex.Message;
        }
        finally
        {
            _s.ReadTimeout = prevTimeout;
            ClearActiveEventRequests();
        }

        if (eventThread != 0)
        {
            _mainThread = eventThread;
            DebugInfo += $" | event parked kind={hitKind} thread=0x{eventThread:x}(sc={GetSuspendCount(eventThread)})";
            return true;
        }
        DebugInfo += " | event fallback timeout";
        return false;
    }

    private bool TryExtractSuspendedEventThread(byte[] body, out byte eventKind, out ulong threadId)
    {
        eventKind = 0;
        threadId = 0;
        if (body.Length < 10) return false;
        int p = 0;
        byte suspendPolicy = body[p++];
        int count = ReadInt(body, ref p);
        if (suspendPolicy == 0 || count <= 0 || p >= body.Length) return false;

        eventKind = body[p++];
        if (p + 4 + _objectIdSize > body.Length) return false;
        _ = ReadInt(body, ref p); // requestId
        // SINGLE_STEP(1), BREAKPOINT(2), EXCEPTION(4), THREAD_START(6), CLASS_PREPARE(8), METHOD_ENTRY(40), VM_START(90)
        if (eventKind is 1 or 2 or 4 or 6 or 8 or 40 or 90)
        {
            threadId = ReadObjId(body, ref p);
            return threadId != 0;
        }
        return false;
    }

    private ulong? FindClass(string sig)
    {
        var b = new List<byte>();
        WriteString(b, sig);
        var r = Send(1, 2, b.ToArray());        // ClassesBySignature
        int p = 0;
        int n = ReadInt(r, ref p);
        if (n <= 0) return null;
        p += 1;                                  // refTypeTag
        return ReadRefId(r, ref p);
    }

    private List<(ulong Id, string Name, string Sig)> FindMethods(ulong cls)
    {
        var b = new List<byte>();
        WriteRefId(b, cls);
        var r = Send(2, 5, b.ToArray());         // ReferenceType.Methods
        int p = 0;
        int n = ReadInt(r, ref p);
        var list = new List<(ulong, string, string)>(n);
        for (int i = 0; i < n; i++)
        {
            ulong id = ReadMethodId(r, ref p);
            string name = ReadString(r, ref p);
            string sig = ReadString(r, ref p);
            _ = ReadInt(r, ref p);               // modBits
            list.Add((id, name, sig));
        }
        return list;
    }

    /// <summary> 在目标进程里加载 .so </summary>
    public void LoadLibrary(string path)
    {
        // CreateString
        var sb = new List<byte>();
        WriteString(sb, path);
        var strReply = Send(1, 11, sb.ToArray());
        int sp = 0;
        ulong strId = ReadObjId(strReply, ref sp);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            // 优先：System.load(path)（静态方法，最稳）
            try
            {
                var call = new List<byte>();
                WriteRefId(call, _systemClass);
                WriteObjId(call, _mainThread);
                WriteMethodId(call, _systemLoadMethod);
                WriteInt(call, 1);
                call.Add((byte)'L'); WriteObjId(call, strId);
                WriteInt(call, 0);
                Send(3, 3, call.ToArray());           // ClassType.InvokeMethod
                DebugInfo += " | System.load ok";
                return;
            }
            catch (Exception ex)
            {
                DebugInfo += " | System.load 失败: " + ex.Message;
            }

            // 兜底：Runtime.getRuntime().load(path)
            if (_runtimeClass != 0 && _getRuntimeMethod != 0 && _loadMethod != 0)
            {
                try
                {
                    var call1 = new List<byte>();
                    WriteRefId(call1, _runtimeClass);
                    WriteObjId(call1, _mainThread);
                    WriteMethodId(call1, _getRuntimeMethod);
                    WriteInt(call1, 0);
                    WriteInt(call1, 0);
                    var r1 = Send(3, 3, call1.ToArray());
                    int p1 = 0;
                    byte tag = r1[p1++];
                    if (tag != 'L') throw new Exception("getRuntime() 返回异常 tag=" + tag);
                    ulong runtimeObj = ReadObjId(r1, ref p1);

                    var call2 = new List<byte>();
                    WriteObjId(call2, runtimeObj);
                    WriteObjId(call2, _mainThread);
                    WriteMethodId(call2, _loadMethod);
                    WriteInt(call2, 1);
                    call2.Add((byte)'L'); WriteObjId(call2, strId);
                    WriteInt(call2, 0);
                    Send(9, 6, call2.ToArray());      // ObjectReference.InvokeMethod
                    DebugInfo += " | Runtime.load ok";
                    return;
                }
                catch (Exception ex)
                {
                    DebugInfo += " | Runtime.load 失败: " + ex.Message;
                }
            }

            if (attempt == 0 && TryParkOnJdwpEvent())
                continue;
            break;
        }

        throw new Exception("System.load 与 Runtime.load 都不可用: " + DebugInfo);
    }

    /// <summary> 恢复所有线程并校验 suspendCount 归零，确保 App 退出 Waiting For Debugger </summary>
    public void Resume()
    {
        if (_resumed) return;
        _resumed = true;
        try
        {
            ClearActiveEventRequests();
            try { Send(1, 9, Array.Empty<byte>()); } catch { }

            var threads = new HashSet<ulong>(_threads) { _mainThread };
            try
            {
                var th = Send(1, 4, Array.Empty<byte>());
                int t = 0;
                int tc = ReadInt(th, ref t);
                for (int i = 0; i < tc; i++) threads.Add(ReadObjId(th, ref t));
            }
            catch { }

            // 先确保 main 线程彻底恢复到 0，再清其他线程
            for (int iter = 0; iter < 16; iter++)
            {
                int sc = GetSuspendCount(_mainThread);
                if (sc <= 0) break;
                ResumeThread(_mainThread);
            }
            foreach (var tid in threads)
            {
                if (tid == _mainThread || tid == 0) continue;
                for (int iter = 0; iter < 16; iter++)
                {
                    int sc = GetSuspendCount(tid);
                    if (sc <= 0) break;
                    ResumeThread(tid);
                }
            }
            DebugInfo += $" | resumed(mainSc={GetSuspendCount(_mainThread)})";
        }
        catch { /* 断开时可能已恢复 */ }
    }

    public void Dispose()
    {
        try { Resume(); } catch { }
        try { _s.Dispose(); } catch { }
        try { _tcp.Dispose(); } catch { }
    }

    // ---------------- 协议编解码 ----------------

    private (int Id, byte Flags, byte CmdSet, byte Cmd, int Err, byte[] Body) ReadOnePacket()
    {
        var hdr = new byte[11];
        ReadExact(_s, hdr, 11);
        int len = BEInt(hdr, 0);
        int id = BEInt(hdr, 4);
        byte flags = hdr[8];
        byte cmdSet = hdr[9];
        byte cmd = hdr[10];
        int err = (hdr[9] << 8) | hdr[10];
        var body = new byte[Math.Max(0, len - 11)];
        if (body.Length > 0) ReadExact(_s, body, body.Length);
        return (id, flags, cmdSet, cmd, err, body);
    }

    private byte[] Send(byte cmdSet, byte cmd, byte[] data)
    {
        int id = _nextId++;
        using var ms = new MemoryStream();
        WriteIntBE(ms, 11 + data.Length);
        WriteIntBE(ms, id);
        ms.WriteByte(0);
        ms.WriteByte(cmdSet);
        ms.WriteByte(cmd);
        ms.Write(data, 0, data.Length);
        var pkt = ms.ToArray();
        _s.Write(pkt, 0, pkt.Length);
        _s.Flush();

        while (true)
        {
            var r = ReadOnePacket();
            if ((r.Flags & 0x80) == 0)
            {
                // VM 主动推送事件包（如 VM_START / CLASS_PREPARE），缓存起来避免破坏当前请求响应
                _events.Enqueue((r.CmdSet, r.Cmd, r.Body));
                if (r.CmdSet == 64 && r.Cmd == 100 && r.Body.Length >= 6)
                    DebugInfo += $" | vm_event(kind={r.Body[5]})";
                continue;
            }
            if (r.Id != id) continue;
            if (r.Err != 0) throw new Exception($"JDWP 命令失败 cmd={cmdSet}/{cmd} err=0x{r.Err:x}");
            return r.Body;
        }
    }

    private static void ReadExact(NetworkStream s, byte[] buf, int len)
    {
        int off = 0;
        while (off < len)
        {
            int n = s.Read(buf, off, len - off);
            if (n <= 0) throw new EndOfStreamException("JDWP 连接被关闭");
            off += n;
        }
    }

    private static int BEInt(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];

    private static void WriteIntBE(Stream s, int v)
    {
        s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
    }

    private static void WriteInt(List<byte> b, int v)
    {
        b.Add((byte)(v >> 24)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 8)); b.Add((byte)v);
    }

    private static int ReadInt(byte[] b, ref int o)
    {
        int v = BEInt(b, o); o += 4; return v;
    }

    private static void WriteString(List<byte> b, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        WriteInt(b, bytes.Length);
        b.AddRange(bytes);
    }

    private static string ReadString(byte[] b, ref int o)
    {
        int n = ReadInt(b, ref o);
        var s = Encoding.UTF8.GetString(b, o, n);
        o += n;
        return s;
    }

    private void WriteRefId(List<byte> b, ulong v) { WriteId(b, v, _refTypeIdSize); }
    private ulong ReadRefId(byte[] b, ref int o) => ReadId(b, ref o, _refTypeIdSize);
    private void WriteObjId(List<byte> b, ulong v) { WriteId(b, v, _objectIdSize); }
    private ulong ReadObjId(byte[] b, ref int o) => ReadId(b, ref o, _objectIdSize);
    private void WriteMethodId(List<byte> b, ulong v) { WriteId(b, v, _methodIdSize); }
    private ulong ReadMethodId(byte[] b, ref int o) => ReadId(b, ref o, _methodIdSize);

    private static void WriteId(List<byte> b, ulong v, int size)
    {
        for (int i = size - 1; i >= 0; i--) b.Add((byte)(v >> (i * 8)));
    }

    private static ulong ReadId(byte[] b, ref int o, int size)
    {
        ulong v = 0;
        for (int i = 0; i < size; i++) v = (v << 8) | b[o + i];
        o += size;
        return v;
    }
}
