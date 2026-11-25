using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace AllocHookStudio;

public class DumpHeader
{
    public int Pid { get; set; }
    public long Ts { get; set; }
    public string Level { get; set; } = "pages";
    public int MaxFrames { get; set; }
    public int MinBytes { get; set; }
    public int L2Enabled { get; set; }
    public int L2Min { get; set; }
    public int L2Sample { get; set; }
    public long L2FilteredBytes { get; set; }
    public long L2FilteredCount { get; set; }
    public long L2SampledBytes { get; set; }
    public long L2FilteredLiveBytes { get; set; }
    public long L2FilteredLiveCount { get; set; }
    public long L2SampledLiveBytes { get; set; }
    public long L2SampledLiveCount { get; set; }
    public long NativeHeapAllocBytes { get; set; }
    public List<ModuleInfo> Mods { get; set; } = new();
}

public class ModuleInfo
{
    public string p { get; set; } = "";
    public string b { get; set; } = "0x0";
}

public class RawRecord
{
    public int Kind { get; set; }
    public string Thread { get; set; } = "-";
    public long Bytes { get; set; }
    public long Count { get; set; }
    public long Aux { get; set; }        // L2：已释放字节
    public ulong[] Pcs { get; set; } = Array.Empty<ulong>();
}

public static class DumpParser
{
    public static bool IsAhk1(byte[] data) =>
        data.Length >= 12 && data[0] == 'A' && data[1] == 'H' && data[2] == 'K' && data[3] == '1';

    /// <summary> 解析 allochook.bin（格式见 docs/data-format.md） </summary>
    public static (DumpHeader Header, List<RawRecord> Records) Parse(byte[] data)
    {
        if (data.Length < 12) throw new InvalidDataException($"dump 太小（{data.Length} 字节）");
        if (!IsAhk1(data))
            throw new InvalidDataException("不是 AHK1 格式: " + Preview(data));

        int pos = 4;
        int version = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos)); pos += 4;
        int hdrLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos)); pos += 4;
        if (hdrLen < 0 || pos + hdrLen > data.Length)
            throw new InvalidDataException($"headerLen 越界: {hdrLen}");

        var hdrJson = Encoding.UTF8.GetString(data, pos, hdrLen);
        pos += hdrLen;

        var header = JsonSerializer.Deserialize<DumpHeader>(hdrJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                     ?? new DumpHeader();

        var recs = new List<RawRecord>();
        if (version >= 3)
        {
            if (pos + 4 > data.Length) throw new InvalidDataException("v3 缺少线程表长度");
            uint threadCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos)); pos += 4;
            var threads = new string[threadCount];
            for (uint i = 0; i < threadCount; i++)
            {
                if (pos >= data.Length) throw new InvalidDataException("v3 线程表截断");
                int nl = data[pos++];
                if (pos + nl > data.Length) throw new InvalidDataException("v3 线程名截断");
                threads[i] = nl > 0 ? Encoding.UTF8.GetString(data, pos, nl) : "-";
                pos += nl;
            }

            if (pos + 4 > data.Length) throw new InvalidDataException("v3 缺少 PC 字典长度");
            uint pcCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos)); pos += 4;
            if ((long)pcCount * 8 > data.Length - pos) throw new InvalidDataException("v3 PC 字典截断");
            var pcDict = new ulong[pcCount];
            for (uint i = 0; i < pcCount; i++)
            {
                pcDict[i] = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos));
                pos += 8;
            }

            while (pos + 29 <= data.Length)
            {
                int kind = data[pos]; pos += 1;
                ushort threadIdx = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos)); pos += 2;
                int nf = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos)); pos += 2;
                long bytes = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos)); pos += 8;
                long count = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos)); pos += 8;
                long aux = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos)); pos += 8;

                if (nf < 0 || nf > 64 || pos + nf * 4 > data.Length) break;

                var pcs = nf == 0 ? Array.Empty<ulong>() : new ulong[nf];
                for (int i = 0; i < nf; i++)
                {
                    uint pcIdx = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
                    pos += 4;
                    pcs[i] = pcIdx < pcDict.Length ? pcDict[pcIdx] : 0;
                }

                string thread = threadIdx < threads.Length ? threads[threadIdx] : "-";
                recs.Add(new RawRecord { Kind = kind, Thread = thread, Bytes = bytes, Count = count, Aux = aux, Pcs = pcs });
            }
            return (header, recs);
        }

        while (pos + 20 <= data.Length)
        {
            int kind = data[pos]; pos += 1;
            int nl = data[pos]; pos += 1;
            string thread = nl > 0 ? Encoding.UTF8.GetString(data, pos, nl) : "-";
            pos += nl;

            int nf = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos)); pos += 2;
            long bytes = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos)); pos += 8;
            long count = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos)); pos += 8;

            // v2 起多一个 aux（L2 已释放字节）
            long aux = 0;
            if (version >= 2) { aux = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos)); pos += 8; }

            if (nf < 0 || nf > 64 || pos + nf * 8 > data.Length) break;

            var pcs = new ulong[nf];
            for (int i = 0; i < nf; i++) { pcs[i] = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos)); pos += 8; }

            recs.Add(new RawRecord { Kind = kind, Thread = thread, Bytes = bytes, Count = count, Aux = aux, Pcs = pcs });
        }
        return (header, recs);
    }

    /// <summary> 出错时给出前若干字节的可读预览，便于判断（例如 run-as 的报错被当成文件内容） </summary>
    public static string Preview(byte[] data)
    {
        int n = Math.Min(data.Length, 160);
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            byte b = data[i];
            sb.Append(b >= 32 && b < 127 ? (char)b : '·');
        }
        return sb.ToString().Replace("\n", " ").Replace("\r", " ");
    }

    /// <summary>
    /// 解析旧版文本 dump（每行：kind [thread] nf bytes count pc...），
    /// 用于设备上装的还是老 .so、没有 allochook.bin 的情况。
    /// </summary>
    public static (DumpHeader Header, List<RawRecord> Records) ParseText(string text)
    {
        var recs = new List<RawRecord>();
        var header = new DumpHeader { Level = "pages", MaxFrames = 32 };

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 5) continue;

            // 兼容两种： kind tname nf bytes count pcs...  /  kind nf bytes count pcs...
            int idx = 4;
            if (!int.TryParse(t[1], out _)) idx = 5;
            if (t.Length <= idx) continue;

            if (!int.TryParse(t[0], out int kind)) continue;
            if (!long.TryParse(t[idx - 1], out long count)) continue;
            if (!long.TryParse(t[idx - 2], out long bytes)) continue;

            var pcs = new List<ulong>();
            for (int i = idx; i < t.Length; i++)
                if (ulong.TryParse(t[i], System.Globalization.NumberStyles.HexNumber, null, out var pc)) pcs.Add(pc);
            if (pcs.Count == 0) continue;

            recs.Add(new RawRecord
            {
                Kind = kind,
                Thread = idx == 5 ? t[1] : "-",
                Bytes = bytes,
                Count = count,
                Pcs = pcs.ToArray(),
            });
        }
        return (header, recs);
    }
}
