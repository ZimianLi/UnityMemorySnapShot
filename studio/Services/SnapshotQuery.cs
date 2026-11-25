using System.Text.RegularExpressions;

namespace AllocHookStudio;

public sealed record CategoryMetric(string Name, long Bytes, double Percent);

public sealed record PssGroupSummary(
    string Cat,
    long Bytes,
    double Percent,
    long Vmas,
    int L1StackCount,
    int L2StackCount,
    int SubBucketCount,
    long ResidualBytes,
    long ResidualVmas,
    bool Expandable);

public sealed record SnapshotSummary(
    string Id,
    string Label,
    DateTime Time,
    int Pid,
    string Package,
    string Device,
    string Mode,
    string Format,
    long TotalDeltaKB,
    int StackCount,
    bool HasL1Live,
    bool HasL2Hold,
    int L2Min,
    int L2Sample,
    long L2FilteredBytes,
    long L2FilteredCount,
    long L2SampledBytes,
    long L2FilteredLiveBytes,
    long L2FilteredLiveCount,
    long L2SampledLiveBytes,
    long L2SampledLiveCount,
    long NativeHeapAllocBytes,
    Dictionary<string, long> Pss,
    long L1LiveBytes,
    long L1LiveSegs,
    int L1LiveStackCount,
    long L1CommitBytes,
    long L1ReleaseBytes,
    long L1NetBytes,
    long L1ReserveBytes,
    long L2RawLiveBytes,
    long L2FreedBytes,
    long L2AllocCount,
    long L2EstLiveBytes,
    int L2TotalStacks,
    int L2NonZeroStacks,
    int L2ZeroStacks,
    long SmapsTotalBytes,
    long TotalVmas,
    long EglMtrackBytes,
    long GlMtrackBytes,
    long GpuMtrackBytes,
    long ReconciledPssBytes,
    long DumpsysTotalBytes,
    long PssDiffBytes,
    double PssDiffPct,
    long MallocPssBytes,
    List<CategoryMetric> Categories,
    List<PssGroupSummary> PssGroups);

public sealed record StacksQueryReq(
    string Layer = "1",
    int Page = 1,
    int PageSize = 200,
    string? Search = null,
    string? Category = null,
    bool HideZero = true,
    bool SkipLow = true,
    bool ScaleEst = false,
    string Sort = "bytes");

public sealed record StackRowDto(
    int Index,
    string RowType,
    int Kind,
    int Layer,
    string Thread,
    long Bytes,
    long Aux,
    long Reserve,
    long Commit,
    long Release,
    long Count,
    string Suspect,
    string Label,
    string Tip,
    string Category,
    string EffectiveFrame,
    int EffectiveIndex,
    int SkippedCount,
    List<string> Frames);

public sealed record StacksPageResp(
    string LayerMode,
    int Page,
    int PageSize,
    int TotalRows,
    int TotalPages,
    long FilteredTotalBytes,
    long FilteredPosBytes,
    long FilteredNegBytes,
    int ZeroLiveCount,
    long LegacyFilteredBytes,
    long LegacyFilteredCount,
    List<StackRowDto> Rows);

public sealed record PssNoteDto(string Tag, string Text, long Bytes, double Percent, string CountText, bool Positive = false, bool Negative = false);

public sealed record PssItemDto(
    string RowType,
    string Name,
    string Thread,
    long Bytes,
    double Percent,
    long Count,
    string Suspect,
    string Category,
    string EffectiveFrame,
    int EffectiveIndex,
    int SkippedCount,
    List<string> Frames);

public sealed record PssCategoryResp(
    string Cat,
    long TotalBytes,
    long TotalVmas,
    int Page,
    int PageSize,
    int TotalRows,
    int TotalPages,
    List<PssNoteDto> Notes,
    List<PssItemDto> Rows);

public sealed record DiffReq(
    string A,
    string B,
    int Page = 1,
    int PageSize = 200,
    string? Search = null,
    string Filter = "all",
    string KindFilter = "all",
    bool SkipLow = true);

public sealed record DiffSnapMeta(
    string Id,
    string Label,
    DateTime Time,
    int Pid,
    string Package,
    long TotalPssKB,
    long L1LiveBytes,
    long L2LiveBytes,
    long L2SmallLiveBytes,
    int StackCount);

public sealed record DiffCategoryDelta(string Name, long BytesA, long BytesB, long Delta);

public sealed record DiffRowDto(
    int Index,
    int Kind,
    int Layer,
    string Thread,
    long BytesA,
    long BytesB,
    long Delta,
    long CountA,
    long CountB,
    string Suspect,
    string Category,
    string EffectiveFrame,
    int EffectiveIndex,
    int SkippedCount,
    List<string> Frames);

public sealed record DiffSummaryDto(
    DiffSnapMeta MetaA,
    DiffSnapMeta MetaB,
    long PssDeltaBytes,
    long L1DeltaBytes,
    long L2DeltaBytes,
    long SmallDeltaBytes,
    long PosSum,
    long NegSum,
    long NetSum,
    int TotalUnfilteredRows);

public sealed record DiffResp(
    DiffSummaryDto Summary,
    List<DiffCategoryDelta> CategoryDeltas,
    int Page,
    int PageSize,
    int TotalRows,
    int TotalPages,
    List<DiffRowDto> Rows);

public static class SnapshotQuery
{
    private sealed class MergedRow
    {
        public int Kind;
        public int Layer;
        public string Thread = "-";
        public long Bytes;
        public long Aux;
        public long Reserve;
        public long Commit;
        public long Release;
        public long Count;
        public string Suspect = "";
        public List<string> Frames = new();
        public string Category = "Other";
        public string EffectiveFrame = "-";
        public int EffectiveIndex;
        public int SkippedCount;
        public string SearchBlob = "";
    }

    private sealed class PssCatBucket
    {
        public string Cat = "";
        public long Total;
        public long Vmas;
        public long ResidualBytes;
        public long ResidualVmas;
        public List<(string Name, long Bytes, long Vmas)> SubBuckets = new();
        public List<MergedRow> L1Stacks = new();
    }

    private sealed class PreparedSnapshot
    {
        public int ConfigVersion;
        public List<MergedRow> L1Live = new();
        public List<MergedRow> L1Window = new();
        public List<MergedRow> L2Hold = new();
        public Dictionary<string, PssCatBucket> PssBuckets = new();
        public SnapshotSummary Summary = null!;
    }

    private static readonly object PrepLock = new();
    private static readonly Dictionary<string, (Snapshot SnapRef, PreparedSnapshot Prep)> PrepCache = new();
    private static int _configVersion = 1;

    private static readonly object DiffCacheLock = new();
    private static (string Key, List<DiffRow> Rows)? _lastDiff;

    public static void BumpConfigVersion()
    {
        Interlocked.Increment(ref _configVersion);
    }

    private static PreparedSnapshot GetPrepared(Snapshot snap, AppConfig cfg)
    {
        int ver = Volatile.Read(ref _configVersion);
        lock (PrepLock)
        {
            if (PrepCache.TryGetValue(snap.Id, out var hit) &&
                ReferenceEquals(hit.SnapRef, snap) &&
                hit.Prep.ConfigVersion == ver)
            {
                return hit.Prep;
            }
        }

        var prep = BuildPrepared(snap, cfg, ver);
        lock (PrepLock)
        {
            if (PrepCache.Count >= 8) PrepCache.Clear();
            PrepCache[snap.Id] = (snap, prep);
        }
        return prep;
    }

    private static PreparedSnapshot BuildPrepared(Snapshot snap, AppConfig cfg, int configVersion)
    {
        var compiledRules = Classifier.CompileRules(cfg.Rules);
        var ignoreRes = Report.CompileIgnorePatterns(cfg.IgnoreFramePatterns);
        var prep = new PreparedSnapshot { ConfigVersion = configVersion };

        var l1LiveMap = new Dictionary<string, MergedRow>();
        var l1WinMap = new Dictionary<string, MergedRow>();
        var l2Map = new Dictionary<string, MergedRow>();
        var pssStackMaps = new Dictionary<string, Dictionary<string, MergedRow>>();

        long commit = 0, release = 0, reserve = 0;
        long l1LiveBytes = 0, l1LiveSegs = 0;
        long smapsTotal = 0, totalVmas = 0;

        string FrameJoin(List<string> frames) => frames.Count == 0 ? "" : string.Join("\u0001", frames);

        foreach (var s in snap.Stacks)
        {
            var frames = s.Frames ?? new List<string>();
            var thread = s.Thread ?? "-";

            if (s.Kind == 0 || s.Kind == 1 || s.Kind == 2)
            {
                if (s.Kind == 0) reserve += s.Bytes;
                else if (s.Kind == 1) commit += s.Bytes;
                else release += s.Bytes;

                string k = thread + "\u0002" + FrameJoin(frames);
                if (!l1WinMap.TryGetValue(k, out var w))
                {
                    w = new MergedRow
                    {
                        Kind = 1,
                        Layer = 1,
                        Thread = thread,
                        Frames = frames,
                        Suspect = s.Suspect ?? "",
                    };
                    l1WinMap[k] = w;
                }
                if (s.Kind == 0) w.Reserve += s.Bytes;
                else if (s.Kind == 1) w.Commit += s.Bytes;
                else w.Release += s.Bytes;
                w.Bytes = w.Commit - w.Release;
                w.Count += s.Count;
                if (!string.IsNullOrEmpty(s.Suspect)) w.Suspect = s.Suspect;
            }
            else if (s.Kind == 4)
            {
                l1LiveBytes += s.Bytes;
                l1LiveSegs += s.Count;
                string k = thread + "\u0002" + FrameJoin(frames);
                if (!l1LiveMap.TryGetValue(k, out var r))
                {
                    r = new MergedRow
                    {
                        Kind = 4,
                        Layer = 1,
                        Thread = thread,
                        Frames = frames,
                    };
                    l1LiveMap[k] = r;
                }
                r.Bytes += s.Bytes;
                r.Count += s.Count;
            }
            else if (s.Kind == 6)
            {
                string k = thread + "\u0002" + FrameJoin(frames);
                if (!l2Map.TryGetValue(k, out var r))
                {
                    r = new MergedRow
                    {
                        Kind = 6,
                        Layer = 2,
                        Thread = thread,
                        Frames = frames,
                    };
                    l2Map[k] = r;
                }
                r.Bytes += s.Bytes;
                r.Aux += s.Aux;
                r.Count += s.Count;
            }
            else if (s.Kind == 5)
            {
                smapsTotal += s.Bytes;
                totalVmas += s.Count;
                string cat = Report.PssCategoryOf(s);
                if (!prep.PssBuckets.TryGetValue(cat, out var b))
                {
                    b = new PssCatBucket { Cat = cat };
                    prep.PssBuckets[cat] = b;
                }
                b.Total += s.Bytes;
                b.Vmas += s.Count;
                if (frames.Count > 0)
                {
                    if (!pssStackMaps.TryGetValue(cat, out var sm))
                        pssStackMaps[cat] = sm = new Dictionary<string, MergedRow>();
                    string k = thread + "\u0002" + FrameJoin(frames);
                    if (!sm.TryGetValue(k, out var r))
                    {
                        r = new MergedRow
                        {
                            Kind = 5,
                            Layer = 1,
                            Thread = thread,
                            Frames = frames,
                        };
                        sm[k] = r;
                    }
                    r.Bytes += s.Bytes;
                    r.Count += s.Count;
                }
                else
                {
                    var sub = Report.PssSubLabelOf(s);
                    if (sub != null)
                        b.SubBuckets.Add((sub, s.Bytes, s.Count));
                    else
                    {
                        b.ResidualBytes += s.Bytes;
                        b.ResidualVmas += s.Count;
                    }
                }
            }
        }

        void FinalizeRows(IEnumerable<MergedRow> rows)
        {
            foreach (var r in rows)
            {
                var eff = Report.PickEffectiveFrame(r.Frames, ignoreRes);
                r.EffectiveFrame = eff.Frame;
                r.EffectiveIndex = eff.Index;
                r.SkippedCount = eff.SkippedCount;
                r.Category = Classifier.MatchStackCategory(r.Frames, compiledRules);
                r.SearchBlob = r.Thread + "\n" + string.Join('\n', r.Frames);
            }
        }

        prep.L1Live = l1LiveMap.Values.OrderByDescending(x => x.Bytes).ToList();
        prep.L1Window = l1WinMap.Values.OrderByDescending(x => Math.Abs(x.Bytes)).ToList();
        prep.L2Hold = l2Map.Values.ToList();
        foreach (var r in prep.L2Hold)
            r.Suspect = (r.Aux > 0 && r.Aux >= Math.Max(r.Bytes, 1) * 2) ? "pool" : "";
        prep.L2Hold.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));

        FinalizeRows(prep.L1Live);
        FinalizeRows(prep.L1Window);
        FinalizeRows(prep.L2Hold);

        foreach (var (cat, sm) in pssStackMaps)
        {
            var list = sm.Values.OrderByDescending(x => x.Bytes).ToList();
            FinalizeRows(list);
            prep.PssBuckets[cat].L1Stacks = list;
        }
        foreach (var b in prep.PssBuckets.Values)
        {
            b.SubBuckets = b.SubBuckets
                .GroupBy(x => x.Name)
                .Select(g => (g.Key, g.Sum(x => x.Bytes), g.Sum(x => x.Vmas)))
                .OrderByDescending(x => x.Item2)
                .ToList();
        }

        bool hasL1Live = prep.L1Live.Count > 0;
        bool hasL2Hold = prep.L2Hold.Count > 0;
        long l2Live = prep.L2Hold.Sum(x => x.Bytes);
        long l2Freed = prep.L2Hold.Sum(x => x.Aux);
        long l2Count = prep.L2Hold.Sum(x => x.Count);
        int l2Zero = prep.L2Hold.Count(x => x.Bytes <= 0);
        int l2NonZero = prep.L2Hold.Count - l2Zero;
        int sample = Math.Max(snap.L2Sample, 1);

        var cats = Classifier.Categorize(snap, cfg.Rules, cfg.IgnoreFramePatterns);
        long catTotal = Math.Max(cats.Values.Where(v => v > 0).Sum(), 1);
        var catList = cats
            .Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new CategoryMetric(kv.Key, kv.Value, Math.Round(kv.Value * 100.0 / catTotal, 2)))
            .ToList();

        var pss = snap.Pss ?? new Dictionary<string, long>();
        long eglB = pss.GetValueOrDefault("EGL mtrack") * 1024;
        long glB = pss.GetValueOrDefault("GL mtrack") * 1024;
        long gpuB = eglB + glB;
        long reconciled = smapsTotal + gpuB;
        long dumpsysB = pss.GetValueOrDefault("TOTAL PSS") * 1024;
        long pssDiff = reconciled - dumpsysB;
        double pssDiffPct = dumpsysB > 0 ? pssDiff * 100.0 / dumpsysB : 0;
        long mallocPss = prep.PssBuckets.TryGetValue("malloc", out var mb) ? mb.Total : 0;

        long smapsDenom = Math.Max(smapsTotal, 1);
        var pssGroups = prep.PssBuckets.Values
            .OrderByDescending(b => b.Total)
            .Select(b =>
            {
                int l2Cnt = (b.Cat == "malloc" && hasL2Hold) ? prep.L2Hold.Count : 0;
                bool exp = l2Cnt > 0 || b.L1Stacks.Count > 0 || b.SubBuckets.Count > 0;
                return new PssGroupSummary(
                    b.Cat, b.Total, Math.Round(b.Total * 100.0 / smapsDenom, 2), b.Vmas,
                    b.L1Stacks.Count, l2Cnt, b.SubBuckets.Count, b.ResidualBytes, b.ResidualVmas, exp);
            })
            .ToList();

        prep.Summary = new SnapshotSummary(
            snap.Id,
            snap.Label ?? "",
            snap.Time,
            snap.Pid,
            snap.Package ?? "",
            snap.Device ?? "",
            snap.Mode ?? "snapshot",
            snap.Format ?? "bin",
            snap.TotalDeltaKB,
            snap.Stacks.Count,
            hasL1Live,
            hasL2Hold,
            snap.L2Min,
            sample,
            snap.L2FilteredBytes,
            snap.L2FilteredCount,
            snap.L2SampledBytes,
            snap.L2FilteredLiveBytes,
            snap.L2FilteredLiveCount,
            snap.L2SampledLiveBytes,
            snap.L2SampledLiveCount,
            snap.NativeHeapAllocBytes,
            pss,
            l1LiveBytes,
            l1LiveSegs,
            prep.L1Live.Count,
            commit,
            release,
            commit - release,
            reserve,
            l2Live,
            l2Freed,
            l2Count,
            l2Live * sample,
            prep.L2Hold.Count,
            l2NonZero,
            l2Zero,
            smapsTotal,
            totalVmas,
            eglB,
            glB,
            gpuB,
            reconciled,
            dumpsysB,
            pssDiff,
            Math.Round(pssDiffPct, 2),
            mallocPss,
            catList,
            pssGroups);

        return prep;
    }

    public static SnapshotSummary GetSummary(Snapshot snap, AppConfig cfg) => GetPrepared(snap, cfg).Summary;

    public static StacksPageResp QueryStacks(Snapshot snap, AppConfig cfg, StacksQueryReq req)
    {
        var prep = GetPrepared(snap, cfg);
        int pageSize = Math.Clamp(req.PageSize, 10, 1000);
        int page = Math.Max(req.Page, 1);
        int sm = Math.Max(snap.L2Sample, 1);
        int scale = (req.ScaleEst && sm > 1) ? sm : 1;
        string search = (req.Search ?? "").Trim();
        string catFilter = (req.Category ?? "").Trim();
        bool hasSearch = search.Length > 0;
        bool hasCat = catFilter.Length > 0;

        string layerReq = (req.Layer ?? "1").ToLowerInvariant();
        string layerMode;
        IEnumerable<MergedRow> baseRows;
        bool includeL2Synthetic = false;

        if (layerReq == "all")
        {
            layerMode = "all";
            var list = new List<MergedRow>(prep.L1Live.Count + prep.L2Hold.Count);
            list.AddRange(prep.L1Live.Count > 0 ? prep.L1Live : prep.L1Window);
            list.AddRange(prep.L2Hold);
            baseRows = list;
            includeL2Synthetic = prep.L2Hold.Count > 0 || snap.L2FilteredLiveBytes > 0;
        }
        else if (layerReq == "2" && prep.L2Hold.Count > 0)
        {
            layerMode = "l2";
            baseRows = prep.L2Hold;
            includeL2Synthetic = true;
        }
        else if (prep.L1Live.Count > 0)
        {
            layerMode = "l1-live";
            baseRows = prep.L1Live;
        }
        else
        {
            layerMode = "l1-window";
            baseRows = prep.L1Window;
        }

        // 构造候选展示行（含可选的小对象 / 采样跳过无栈净存量行）
        var candidates = new List<(string RowType, MergedRow? Row, long SortBytes, long Bytes, long Aux, long Count, string Label, string Tip, string Cat)>();
        int zeroCount = 0;

        foreach (var r in baseRows)
        {
            if (r.Kind == 6 && r.Bytes <= 0)
            {
                zeroCount++;
                if (req.HideZero) continue;
            }
            if (hasCat && !string.Equals(r.Category, catFilter, StringComparison.Ordinal)) continue;
            if (hasSearch && !r.SearchBlob.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;

            long rowScale = (r.Kind == 6) ? scale : 1;
            long b = r.Bytes * rowScale;
            long a = r.Aux * rowScale;
            candidates.Add(("stack", r, Math.Abs(b), b, a, r.Count, "", "", r.Category));
        }

        if (includeL2Synthetic)
        {
            if (snap.L2FilteredLiveBytes > 0)
            {
                string lbl = $"[阈值以下 (<{snap.L2Min:N0} B) 小对象净存量 · 无栈]";
                string tip = $"累计申请 {snap.L2FilteredBytes:N0} B / {snap.L2FilteredCount:N0} 次（含已释放），未乘采样倍数";
                const string smallCat = "小对象/阈值以下(无栈)";
                if ((!hasCat || catFilter == smallCat) &&
                    (!hasSearch || lbl.Contains(search, StringComparison.OrdinalIgnoreCase) || smallCat.Contains(search, StringComparison.OrdinalIgnoreCase)))
                {
                    candidates.Add(("filtered-live", null, snap.L2FilteredLiveBytes, snap.L2FilteredLiveBytes, 0, snap.L2FilteredLiveCount, lbl, tip, smallCat));
                }
            }
            if (sm > 1 && !req.ScaleEst && snap.L2SampledLiveBytes > 0)
            {
                string lbl = $"[1/{sm} 采样跳过净存量 · 无栈]";
                string tip = $"1/{sm} 采样跳过的净存量（实测 usable，累计跳过 {snap.L2SampledBytes:N0} B）";
                if (!hasCat && (!hasSearch || lbl.Contains(search, StringComparison.OrdinalIgnoreCase)))
                {
                    candidates.Add(("sampled-live", null, snap.L2SampledLiveBytes, snap.L2SampledLiveBytes, 0, snap.L2SampledLiveCount, lbl, tip, "Other"));
                }
            }
        }

        string sortKey = (req.Sort ?? "bytes").ToLowerInvariant();
        candidates.Sort((x, y) => sortKey switch
        {
            "freed" => y.Aux.CompareTo(x.Aux) != 0 ? y.Aux.CompareTo(x.Aux) : y.SortBytes.CompareTo(x.SortBytes),
            "count" => y.Count.CompareTo(x.Count) != 0 ? y.Count.CompareTo(x.Count) : y.SortBytes.CompareTo(x.SortBytes),
            _ => y.SortBytes.CompareTo(x.SortBytes),
        });

        long totalBytes = 0, posBytes = 0, negBytes = 0;
        for (int i = 0; i < candidates.Count; i++)
        {
            long b = candidates[i].Bytes;
            totalBytes += b;
            if (b >= 0) posBytes += b;
            else negBytes += b;
        }

        int totalRows = candidates.Count;
        int totalPages = Math.Max(1, (totalRows + pageSize - 1) / pageSize);
        if (page > totalPages) page = totalPages;
        int start = (page - 1) * pageSize;
        int end = Math.Min(start + pageSize, totalRows);

        var outRows = new List<StackRowDto>(Math.Max(0, end - start));
        for (int i = start; i < end; i++)
        {
            var c = candidates[i];
            if (c.Row != null)
            {
                var r = c.Row;
                string effFrame = req.SkipLow ? r.EffectiveFrame : (r.Frames.Count > 0 ? r.Frames[0] : "-");
                int effIdx = req.SkipLow ? r.EffectiveIndex : 0;
                int skipped = req.SkipLow ? r.SkippedCount : 0;
                outRows.Add(new StackRowDto(
                    i + 1, "stack", r.Kind, r.Layer, r.Thread,
                    c.Bytes, c.Aux, r.Reserve, r.Commit, r.Release, c.Count,
                    r.Suspect, "", "", c.Cat,
                    effFrame, effIdx, skipped, r.Frames));
            }
            else
            {
                outRows.Add(new StackRowDto(
                    i + 1, c.RowType, 6, 2, "无栈",
                    c.Bytes, 0, 0, 0, 0, c.Count,
                    "", c.Label, c.Tip, c.Cat,
                    c.Label, 0, 0, new List<string>()));
            }
        }

        return new StacksPageResp(
            layerMode, page, pageSize, totalRows, totalPages,
            totalBytes, posBytes, negBytes, zeroCount,
            snap.L2FilteredLiveBytes == 0 ? snap.L2FilteredBytes : 0,
            snap.L2FilteredLiveBytes == 0 ? snap.L2FilteredCount : 0,
            outRows);
    }

    public static PssCategoryResp? QueryPssCategory(
        Snapshot snap, AppConfig cfg, string cat,
        int page = 1, int pageSize = 100, string? search = null,
        bool hideZero = true, bool skipLow = true, bool scaleEst = false)
    {
        var prep = GetPrepared(snap, cfg);
        if (!prep.PssBuckets.TryGetValue(cat, out var bucket)) return null;

        pageSize = Math.Clamp(pageSize, 10, 1000);
        page = Math.Max(page, 1);
        string q = (search ?? "").Trim();
        bool hasSearch = q.Length > 0;
        int sm = Math.Max(snap.L2Sample, 1);
        bool useScale = scaleEst && sm > 1;
        long scale = useScale ? sm : 1;
        long catDenom = Math.Max(bucket.Total, 1);

        var notes = new List<PssNoteDto>();
        var items = new List<PssItemDto>();

        if (cat == "malloc" && prep.L2Hold.Count > 0)
        {
            long l2Raw = prep.Summary.L2RawLiveBytes;
            long estL2 = l2Raw * scale;
            long flb = snap.L2FilteredLiveBytes;
            long flc = snap.L2FilteredLiveCount;
            long fb = snap.L2FilteredBytes;
            long fc = snap.L2FilteredCount;
            long slb = snap.L2SampledLiveBytes;
            long slc = snap.L2SampledLiveCount;
            long sb = snap.L2SampledBytes;

            if (snap.L2Min > 0 || flb > 0 || fb > 0)
            {
                notes.Add(new PssNoteDto(
                    "小对象",
                    $"阈值以下(<{snap.L2Min:N0}B) 净存量（累计申请 {fb:N0} B / {fc:N0} 次）",
                    flb, Math.Round(flb * 100.0 / catDenom, 2), flc.ToString("N0"), Positive: flb > 0));
            }
            if (sm > 1 && (slb > 0 || sb > 0))
            {
                notes.Add(new PssNoteDto(
                    "采样净存",
                    useScale
                        ? $"1/{sm} 采样跳过净存量（已计入下方×{sm}估算，实测净存 {slb:N0} B，累计跳过 {sb:N0} B）"
                        : $"1/{sm} 采样跳过净存量（实测 usable，累计跳过 {sb:N0} B）",
                    useScale ? 0 : slb, useScale ? 0 : Math.Round(slb * 100.0 / catDenom, 2), slc.ToString("N0")));
            }
            long tracked = useScale ? (estL2 + flb) : (l2Raw + flb + slb);
            long unattr = bucket.Total - tracked;
            if (unattr >= 0)
            {
                notes.Add(new PssNoteDto(
                    "残余",
                    "未归因 / Scudo 元数据碎片 / 预热前分配",
                    unattr, Math.Round(unattr * 100.0 / catDenom, 2), "-"));
            }
            else
            {
                notes.Add(new PssNoteDto(
                    "说明",
                    $"L2 虚拟分配总额超出物理 PSS {-unattr:N0} B（未触页或 Scudo 延迟归还）",
                    unattr, 0, "-", Negative: true));
            }
            if (hideZero && prep.Summary.L2ZeroStacks > 0)
            {
                notes.Add(new PssNoteDto(
                    "过滤",
                    $"已隐藏 {prep.Summary.L2ZeroStacks:N0} 条存活=0 的已释放栈",
                    0, 0, prep.Summary.L2ZeroStacks.ToString("N0")));
            }
        }
        else if (bucket.ResidualBytes > 0)
        {
            notes.Add(new PssNoteDto(
                "残余",
                bucket.L1Stacks.Count > 0 ? $"未命中提交区间 / 预热前 {cat} 残余" : $"其他 / 溢出 {cat} 残余",
                bucket.ResidualBytes, Math.Round(bucket.ResidualBytes * 100.0 / catDenom, 2), bucket.ResidualVmas.ToString("N0")));
        }

        foreach (var sub in bucket.SubBuckets)
        {
            if (hasSearch && !sub.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            items.Add(new PssItemDto(
                "sub-bucket", sub.Name, "子桶", sub.Bytes, Math.Round(sub.Bytes * 100.0 / catDenom, 2),
                sub.Vmas, "", "", sub.Name, 0, 0, new List<string>()));
        }

        foreach (var sr in bucket.L1Stacks)
        {
            if (hasSearch && !sr.SearchBlob.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            string eff = skipLow ? sr.EffectiveFrame : (sr.Frames.Count > 0 ? sr.Frames[0] : "-");
            items.Add(new PssItemDto(
                "l1-stack", eff, sr.Thread, sr.Bytes, Math.Round(sr.Bytes * 100.0 / catDenom, 2),
                sr.Count, "", sr.Category, eff, skipLow ? sr.EffectiveIndex : 0, skipLow ? sr.SkippedCount : 0, sr.Frames));
        }

        if (cat == "malloc" && prep.L2Hold.Count > 0)
        {
            foreach (var lr in prep.L2Hold)
            {
                if (hideZero && lr.Bytes <= 0) continue;
                if (hasSearch && !lr.SearchBlob.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                long val = lr.Bytes * scale;
                string eff = skipLow ? lr.EffectiveFrame : (lr.Frames.Count > 0 ? lr.Frames[0] : "-");
                items.Add(new PssItemDto(
                    "l2-stack", eff, useScale ? "L2 估算" : "L2 栈", val, Math.Round(val * 100.0 / catDenom, 2),
                    lr.Count, lr.Suspect, lr.Category, eff, skipLow ? lr.EffectiveIndex : 0, skipLow ? lr.SkippedCount : 0, lr.Frames));
            }
        }

        int totalRows = items.Count;
        int totalPages = Math.Max(1, (totalRows + pageSize - 1) / pageSize);
        if (page > totalPages) page = totalPages;
        int start = (page - 1) * pageSize;
        var pageItems = items.Skip(start).Take(pageSize).ToList();

        return new PssCategoryResp(cat, bucket.Total, bucket.Vmas, page, pageSize, totalRows, totalPages, notes, pageItems);
    }

    public static DiffResp QueryDiff(Snapshot a, Snapshot b, AppConfig cfg, DiffReq req)
    {
        var sumA = GetSummary(a, cfg);
        var sumB = GetSummary(b, cfg);

        string diffKey = $"{a.Id}|{a.Stacks.Count}|{b.Id}|{b.Stacks.Count}";
        List<DiffRow> allRows;
        lock (DiffCacheLock)
        {
            if (_lastDiff != null && _lastDiff.Value.Key == diffKey)
            {
                allRows = _lastDiff.Value.Rows;
            }
            else
            {
                allRows = Store.Diff(a, b);
                _lastDiff = (diffKey, allRows);
            }
        }

        var ignoreRes = Report.CompileIgnorePatterns(cfg.IgnoreFramePatterns);
        string search = (req.Search ?? "").Trim();
        bool hasSearch = search.Length > 0;
        string filter = (req.Filter ?? "all").ToLowerInvariant();
        string kindFilter = (req.KindFilter ?? "all").ToLowerInvariant();

        var filtered = new List<DiffRow>();
        long posSum = 0, negSum = 0;

        foreach (var r in allRows)
        {
            // PSS 路径空栈桶不混入栈增量列表（PSS 差值在顶部汇总卡片展示）
            if (r.Kind == 5 && (r.Frames == null || r.Frames.Count == 0)) continue;
            if (kindFilter == "l2" && r.Kind != 3 && r.Kind != 6) continue;
            if (kindFilter == "l1" && (r.Kind == 3 || r.Kind == 6)) continue;

            if (r.Delta > 0) posSum += r.Delta;
            else if (r.Delta < 0) negSum += r.Delta;

            if (filter == "pos" && r.Delta <= 0) continue;
            if (filter == "neg" && r.Delta >= 0) continue;

            if (hasSearch)
            {
                bool hit = (r.Thread ?? "").Contains(search, StringComparison.OrdinalIgnoreCase);
                if (!hit && r.Frames != null)
                {
                    for (int i = 0; i < r.Frames.Count; i++)
                    {
                        if (r.Frames[i].Contains(search, StringComparison.OrdinalIgnoreCase)) { hit = true; break; }
                    }
                }
                if (!hit) continue;
            }
            filtered.Add(r);
        }

        int pageSize = Math.Clamp(req.PageSize, 10, 1000);
        int page = Math.Max(req.Page, 1);
        int totalRows = filtered.Count;
        int totalPages = Math.Max(1, (totalRows + pageSize - 1) / pageSize);
        if (page > totalPages) page = totalPages;
        int start = (page - 1) * pageSize;
        int end = Math.Min(start + pageSize, totalRows);
        var compiledRules = Classifier.CompileRules(cfg.Rules);

        var outRows = new List<DiffRowDto>(Math.Max(0, end - start));
        for (int i = start; i < end; i++)
        {
            var r = filtered[i];
            var frames = r.Frames ?? new List<string>();
            var eff = req.SkipLow
                ? Report.PickEffectiveFrame(frames, ignoreRes)
                : new Report.EffectiveFrame(frames.Count > 0 ? frames[0] : "-", 0, 0);
            int layer = (r.Kind == 3 || r.Kind == 6) ? 2 : 1;
            string cat = frames.Count > 0 ? Classifier.MatchStackCategory(frames, compiledRules) : "";
            outRows.Add(new DiffRowDto(
                i + 1, r.Kind, layer, r.Thread ?? "-",
                r.BytesA, r.BytesB, r.Delta, r.CountA, r.CountB,
                r.Suspect ?? "", cat,
                eff.Frame, eff.Index, eff.SkippedCount, frames));
        }

        var catMapA = sumA.Categories.ToDictionary(c => c.Name, c => c.Bytes);
        var catMapB = sumB.Categories.ToDictionary(c => c.Name, c => c.Bytes);
        var catDeltas = catMapA.Keys.Union(catMapB.Keys)
            .Select(k =>
            {
                long va = catMapA.GetValueOrDefault(k);
                long vb = catMapB.GetValueOrDefault(k);
                return new DiffCategoryDelta(k, va, vb, vb - va);
            })
            .Where(x => x.Delta != 0 || x.BytesA > 0 || x.BytesB > 0)
            .OrderByDescending(x => Math.Abs(x.Delta))
            .Take(14)
            .ToList();

        var summary = new DiffSummaryDto(
            new DiffSnapMeta(sumA.Id, sumA.Label, sumA.Time, sumA.Pid, sumA.Package, sumA.Pss.GetValueOrDefault("TOTAL PSS"), sumA.L1LiveBytes, sumA.L2RawLiveBytes, sumA.L2FilteredLiveBytes, sumA.StackCount),
            new DiffSnapMeta(sumB.Id, sumB.Label, sumB.Time, sumB.Pid, sumB.Package, sumB.Pss.GetValueOrDefault("TOTAL PSS"), sumB.L1LiveBytes, sumB.L2RawLiveBytes, sumB.L2FilteredLiveBytes, sumB.StackCount),
            (sumB.Pss.GetValueOrDefault("TOTAL PSS") - sumA.Pss.GetValueOrDefault("TOTAL PSS")) * 1024,
            sumB.L1LiveBytes - sumA.L1LiveBytes,
            sumB.L2RawLiveBytes - sumA.L2RawLiveBytes,
            sumB.L2FilteredLiveBytes - sumA.L2FilteredLiveBytes,
            posSum,
            negSum,
            posSum + negSum,
            allRows.Count);

        return new DiffResp(summary, catDeltas, page, pageSize, totalRows, totalPages, outRows);
    }
}
