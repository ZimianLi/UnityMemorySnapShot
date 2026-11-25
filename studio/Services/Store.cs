using System.Collections.Concurrent;
using System.Text.Json;

namespace AllocHookStudio;

public class Store
{
    private readonly string _dir;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private const int CacheCapacity = 4;

    private sealed record CachedEntry(long Length, DateTime LastWriteUtc, string Id, string Label, DateTime Time, string Mode, int Stacks, long TotalPssKB);
    private sealed record LruEntry(string Id, long Length, DateTime LastWriteUtc, Snapshot Snap);

    private readonly ConcurrentDictionary<string, CachedEntry> _listCache = new();
    private readonly object _lruLock = new();
    private readonly LinkedList<LruEntry> _lru = new();
    private readonly Dictionary<string, LinkedListNode<LruEntry>> _lruMap = new(StringComparer.OrdinalIgnoreCase);

    public Store(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(_dir);
    }

    public string Dir => _dir;

    private static string SafeId(string id) => Path.GetFileName(id ?? "");

    /// <summary> 将快照紧凑化（Frames/Pcs 替换为字典索引）以便大幅缩减磁盘体积 </summary>
    public static Snapshot Compact(Snapshot snap)
    {
        var frameTable = new List<string>();
        var frameDict = new Dictionary<string, int>(StringComparer.Ordinal);
        var pcTable = new List<ulong>();
        var pcDict = new Dictionary<ulong, int>();

        var compactStacks = new List<StackRecord>(snap.Stacks.Count);
        foreach (var st in snap.Stacks)
        {
            List<int>? fids = null;
            if (st.Frames != null && st.Frames.Count > 0)
            {
                fids = new List<int>(st.Frames.Count);
                foreach (var f in st.Frames)
                {
                    if (!frameDict.TryGetValue(f, out int fid))
                    {
                        fid = frameTable.Count;
                        frameDict[f] = fid;
                        frameTable.Add(f);
                    }
                    fids.Add(fid);
                }
            }

            List<int>? pids = null;
            if (st.Pcs != null && st.Pcs.Count > 0)
            {
                pids = new List<int>(st.Pcs.Count);
                foreach (var pc in st.Pcs)
                {
                    if (!pcDict.TryGetValue(pc, out int pid))
                    {
                        pid = pcTable.Count;
                        pcDict[pc] = pid;
                        pcTable.Add(pc);
                    }
                    pids.Add(pid);
                }
            }

            compactStacks.Add(new StackRecord
            {
                Kind = st.Kind,
                Thread = st.Thread,
                Bytes = st.Bytes,
                Count = st.Count,
                Layer = st.Layer,
                Aux = st.Aux,
                Suspect = st.Suspect,
                Label = st.Label,
                Pcs = null!,
                Frames = null!,
                PcIds = pids,
                FrameIds = fids,
            });
        }

        return new Snapshot
        {
            Id = snap.Id,
            Label = snap.Label,
            Time = snap.Time,
            Pid = snap.Pid,
            Package = snap.Package,
            Device = snap.Device,
            Mode = snap.Mode,
            Format = snap.Format,
            TotalDeltaKB = snap.TotalDeltaKB,
            L2FilteredBytes = snap.L2FilteredBytes,
            L2FilteredCount = snap.L2FilteredCount,
            L2SampledBytes = snap.L2SampledBytes,
            L2FilteredLiveBytes = snap.L2FilteredLiveBytes,
            L2FilteredLiveCount = snap.L2FilteredLiveCount,
            L2SampledLiveBytes = snap.L2SampledLiveBytes,
            L2SampledLiveCount = snap.L2SampledLiveCount,
            NativeHeapAllocBytes = snap.NativeHeapAllocBytes,
            L2Min = snap.L2Min,
            L2Sample = snap.L2Sample,
            FrameTable = frameTable,
            PcTable = pcTable,
            Stacks = compactStacks,
            Pss = snap.Pss,
        };
    }

    /// <summary> 将紧凑快照（或旧版全量快照）水合为内存标准 Snapshot，并在堆中复用相同帧字符串引用 </summary>
    public static Snapshot Hydrate(Snapshot snap)
    {
        var ft = snap.FrameTable;
        var pt = snap.PcTable;
        Dictionary<string, string>? legacyIntern = ft == null ? new Dictionary<string, string>(StringComparer.Ordinal) : null;

        foreach (var st in snap.Stacks)
        {
            if (st.FrameIds != null && ft != null)
            {
                var list = new List<string>(st.FrameIds.Count);
                for (int i = 0; i < st.FrameIds.Count; i++)
                {
                    int idx = st.FrameIds[i];
                    list.Add((uint)idx < (uint)ft.Count ? ft[idx] : "");
                }
                st.Frames = list;
            }
            else if (st.Frames == null)
            {
                st.Frames = new List<string>();
            }
            else if (legacyIntern != null && st.Frames.Count > 0)
            {
                for (int i = 0; i < st.Frames.Count; i++)
                {
                    var f = st.Frames[i];
                    if (legacyIntern.TryGetValue(f, out var interned)) st.Frames[i] = interned;
                    else legacyIntern[f] = f;
                }
            }

            if (st.PcIds != null && pt != null)
            {
                var list = new List<ulong>(st.PcIds.Count);
                for (int i = 0; i < st.PcIds.Count; i++)
                {
                    int idx = st.PcIds[i];
                    list.Add((uint)idx < (uint)pt.Count ? pt[idx] : 0UL);
                }
                st.Pcs = list;
            }
            else if (st.Pcs == null)
            {
                st.Pcs = new List<ulong>();
            }

            st.FrameIds = null;
            st.PcIds = null;
        }

        snap.FrameTable = null;
        snap.PcTable = null;
        return snap;
    }

    private void PutLru(string id, long length, DateTime lastWriteUtc, Snapshot snap)
    {
        lock (_lruLock)
        {
            if (_lruMap.TryGetValue(id, out var existing))
            {
                _lru.Remove(existing);
                _lruMap.Remove(id);
            }
            var node = new LinkedListNode<LruEntry>(new LruEntry(id, length, lastWriteUtc, snap));
            _lru.AddFirst(node);
            _lruMap[id] = node;
            while (_lru.Count > CacheCapacity)
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _lruMap.Remove(last.Value.Id);
            }
        }
    }

    public void Invalidate(string id)
    {
        var sid = SafeId(id);
        lock (_lruLock)
        {
            if (_lruMap.TryGetValue(sid, out var node))
            {
                _lru.Remove(node);
                _lruMap.Remove(sid);
            }
        }
        var path = Path.Combine(_dir, sid + ".json");
        _listCache.TryRemove(path, out _);
    }

    public void Save(Snapshot snap)
    {
        var sid = SafeId(snap.Id);
        var path = Path.Combine(_dir, sid + ".json");
        var tmpPath = path + ".tmp";
        var compact = Compact(snap);
        using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
        {
            JsonSerializer.Serialize(fs, compact, Json);
        }
        File.Move(tmpPath, path, overwrite: true);

        try
        {
            var fi = new FileInfo(path);
            long totalPss = snap.Pss != null ? snap.Pss.GetValueOrDefault("TOTAL PSS") : 0;
            _listCache[path] = new CachedEntry(fi.Length, fi.LastWriteTimeUtc, snap.Id, snap.Label ?? "", snap.Time, snap.Mode ?? "", snap.Stacks?.Count ?? 0, totalPss);
            PutLru(sid, fi.Length, fi.LastWriteTimeUtc, snap);
        }
        catch { }
    }

    public Snapshot? Load(string id)
    {
        var sid = SafeId(id);
        var p = Path.Combine(_dir, sid + ".json");
        if (!File.Exists(p))
        {
            Invalidate(sid);
            return null;
        }

        var fi = new FileInfo(p);
        lock (_lruLock)
        {
            if (_lruMap.TryGetValue(sid, out var hit) &&
                hit.Value.Length == fi.Length &&
                hit.Value.LastWriteUtc == fi.LastWriteTimeUtc)
            {
                _lru.Remove(hit);
                _lru.AddFirst(hit);
                return hit.Value.Snap;
            }
        }

        Snapshot? snap;
        using (var fs = File.OpenRead(p))
        {
            snap = JsonSerializer.Deserialize<Snapshot>(fs);
        }
        if (snap == null) return null;
        Hydrate(snap);
        PutLru(sid, fi.Length, fi.LastWriteTimeUtc, snap);
        return snap;
    }

    public List<object> List()
    {
        var files = Directory.EnumerateFiles(_dir, "*.json").ToList();
        var fileSet = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        foreach (var k in _listCache.Keys)
        {
            if (!fileSet.Contains(k)) _listCache.TryRemove(k, out _);
        }

        var items = new List<CachedEntry>();
        foreach (var f in files)
        {
            try
            {
                var fi = new FileInfo(f);
                if (_listCache.TryGetValue(f, out var cached) &&
                    cached.Length == fi.Length &&
                    cached.LastWriteUtc == fi.LastWriteTimeUtc)
                {
                    items.Add(cached);
                    continue;
                }

                using var stream = File.OpenRead(f);
                using var doc = JsonDocument.Parse(stream);
                var r = doc.RootElement;
                long totalPss = 0;
                if (r.TryGetProperty("Pss", out var pssEl) && pssEl.ValueKind == JsonValueKind.Object &&
                    pssEl.TryGetProperty("TOTAL PSS", out var tpEl) && tpEl.TryGetInt64(out var tpVal))
                {
                    totalPss = tpVal;
                }
                var entry = new CachedEntry(
                    fi.Length,
                    fi.LastWriteTimeUtc,
                    Path.GetFileNameWithoutExtension(f),
                    r.TryGetProperty("Label", out var l) ? (l.GetString() ?? "") : "",
                    r.TryGetProperty("Time", out var t) ? t.GetDateTime() : DateTime.MinValue,
                    r.TryGetProperty("Mode", out var m) ? (m.GetString() ?? "") : "",
                    r.TryGetProperty("Stacks", out var s) ? s.GetArrayLength() : 0,
                    totalPss);
                _listCache[f] = entry;
                items.Add(entry);
            }
            catch { }
        }

        return items
            .OrderByDescending(x => x.Time)
            .Select(x => (object)new { id = x.Id, label = x.Label, time = x.Time, mode = x.Mode, stacks = x.Stacks, totalPssKB = x.TotalPssKB })
            .ToList();
    }

    /// <summary> 两份快照做差：按 符号栈 + 线程 + kind 匹配 </summary>
    public static List<DiffRow> Diff(Snapshot a, Snapshot b)
    {
        string KeyOf(StackRecord s) => s.Kind + "|" + s.Thread + "|" + string.Join(";", s.Frames);

        var mapA = new Dictionary<string, (int Kind, string Thread, long Bytes, long Count, string Suspect, List<string> Frames)>(a.Stacks.Count);
        foreach (var s in a.Stacks)
        {
            var key = KeyOf(s);
            if (mapA.TryGetValue(key, out var cur))
                mapA[key] = (cur.Kind, cur.Thread, cur.Bytes + s.Bytes, cur.Count + s.Count,
                    !string.IsNullOrEmpty(s.Suspect) ? s.Suspect : cur.Suspect, cur.Frames);
            else
                mapA[key] = (s.Kind, s.Thread, s.Bytes, s.Count, s.Suspect ?? "", s.Frames);
        }

        var mapB = new Dictionary<string, (int Kind, string Thread, long Bytes, long Count, string Suspect, List<string> Frames)>(b.Stacks.Count);
        foreach (var s in b.Stacks)
        {
            var key = KeyOf(s);
            if (mapB.TryGetValue(key, out var cur))
                mapB[key] = (cur.Kind, cur.Thread, cur.Bytes + s.Bytes, cur.Count + s.Count,
                    !string.IsNullOrEmpty(s.Suspect) ? s.Suspect : cur.Suspect, cur.Frames);
            else
                mapB[key] = (s.Kind, s.Thread, s.Bytes, s.Count, s.Suspect ?? "", s.Frames);
        }

        var rows = new List<DiffRow>(Math.Max(mapA.Count, mapB.Count));
        foreach (var kv in mapA)
        {
            mapB.TryGetValue(kv.Key, out var rb);
            bool hasB = mapB.ContainsKey(kv.Key);
            string suspect = (hasB && !string.IsNullOrEmpty(rb.Suspect)) ? rb.Suspect : kv.Value.Suspect;
            rows.Add(new DiffRow
            {
                Key = kv.Key,
                Kind = kv.Value.Kind,
                Thread = kv.Value.Thread,
                BytesA = kv.Value.Bytes,
                BytesB = hasB ? rb.Bytes : 0,
                CountA = kv.Value.Count,
                CountB = hasB ? rb.Count : 0,
                Suspect = suspect,
                Frames = hasB ? rb.Frames : kv.Value.Frames,
            });
        }
        foreach (var kv in mapB)
        {
            if (mapA.ContainsKey(kv.Key)) continue;
            rows.Add(new DiffRow
            {
                Key = kv.Key,
                Kind = kv.Value.Kind,
                Thread = kv.Value.Thread,
                BytesA = 0,
                BytesB = kv.Value.Bytes,
                CountA = 0,
                CountB = kv.Value.Count,
                Suspect = kv.Value.Suspect,
                Frames = kv.Value.Frames,
            });
        }
        return rows.OrderByDescending(r => Math.Abs(r.Delta)).ToList();
    }
}
