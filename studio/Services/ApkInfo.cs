using System.IO.Compression;
using System.Text;

namespace AllocHookStudio;

/// <summary>
/// 直接从 APK 里解析 AndroidManifest 的 package 属性。
/// APK 内的 AndroidManifest.xml 是二进制 AXML，这里做最小实现：
/// 只解析 string pool + 第一个 START_ELEMENT(&lt;manifest&gt;) 的 package 属性。
/// </summary>
public static class ApkInfo
{
    public static (string? Package, string? Error) ReadPackage(string apkPath)
    {
        try
        {
            if (!File.Exists(apkPath)) return (null, "文件不存在");

            using var zip = ZipFile.OpenRead(apkPath);
            var entry = zip.GetEntry("AndroidManifest.xml");
            if (entry == null) return (null, "APK 内没有 AndroidManifest.xml");

            using var ms = new MemoryStream();
            entry.Open().CopyTo(ms);
            return (ParseManifest(ms.ToArray()), null);
        }
        catch (Exception e)
        {
            return (null, e.Message);
        }
    }

    private static string? ParseManifest(byte[] data)
    {
        if (data.Length < 8) return null;
        int pos = 0;
        ushort type = U16(data, pos);
        if (type != 0x0003) return null;           // RES_XML_TYPE

        ushort headerSize = U16(data, pos + 2);
        pos += headerSize;                          // 跳过文件头

        StringPool? pool = null;

        while (pos + 8 <= data.Length)
        {
            ushort chunkType = U16(data, pos);
            ushort chunkHeaderSize = U16(data, pos + 2);
            uint chunkSize = U32(data, pos + 4);
            if (chunkSize < 8 || pos + chunkSize > data.Length) break;

            if (chunkType == 0x0001)                // RES_STRING_POOL_TYPE
            {
                pool = StringPool.Parse(data, pos, chunkHeaderSize, (int)chunkSize);
            }
            else if (chunkType == 0x0102)           // RES_XML_START_ELEMENT_TYPE
            {
                if (pool == null) return null;
                var pkg = ParseStartElement(data, pos + chunkHeaderSize, pool);
                if (pkg != null) return pkg;
            }

            pos += (int)chunkSize;
        }
        return null;
    }

    private static string? ParseStartElement(byte[] data, int pos, StringPool pool)
    {
        try
        {
            // 传入的 pos 已经是 ResXMLTree_attrExt 的起点（chunkStart + headerSize）
            uint nameIdx = U32(data, pos + 4);
            ushort attrStart = U16(data, pos + 8);
            ushort attrSize = U16(data, pos + 10);
            ushort attrCount = U16(data, pos + 12);
            if (attrSize == 0) attrSize = 20;

            string elemName = pool.Get(nameIdx) ?? "";
            int attrBase = pos + attrStart;

            for (int i = 0; i < attrCount; i++)
            {
                int a = attrBase + i * attrSize;
                if (a + 20 > data.Length) break;

                uint attrNameIdx = U32(data, a + 4);
                uint rawValue = U32(data, a + 8);
                byte dataType = data[a + 15];
                uint dataValue = U32(data, a + 16);

                string attrName = pool.Get(attrNameIdx) ?? "";
                if (attrName != "package") continue;
                if (elemName != "manifest") continue;

                if (rawValue != 0xFFFFFFFF) return pool.Get(rawValue);
                if (dataType == 0x03) return pool.Get(dataValue);
            }
            return null;
        }
        catch { return null; }
    }

    private static ushort U16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
    private static uint U32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

    private class StringPool
    {
        private readonly List<string> _items = new();

        public string? Get(uint idx) => idx < _items.Count ? _items[(int)idx] : null;

        public static StringPool? Parse(byte[] data, int chunkStart, int headerSize, int chunkSize)
        {
            try
            {
                var p = new StringPool();
                // ResStringPool_header 固定布局（相对 chunk 起点）：
                //   +8 stringCount, +12 styleCount, +16 flags, +20 stringsStart, +24 stylesStart
                uint count = U32(data, chunkStart + 8);
                uint flags = U32(data, chunkStart + 16);
                uint stringsStart = U32(data, chunkStart + 20);
                bool utf8 = (flags & 0x100) != 0;

                int offsetsBase = chunkStart + headerSize;      // 紧跟头部的偏移数组
                int stringsBase = chunkStart + (int)stringsStart; // 相对 chunk 起点

                for (int i = 0; i < count; i++)
                {
                    uint off = U32(data, offsetsBase + i * 4);
                    int sp = stringsBase + (int)off;
                    if (sp >= data.Length) { p._items.Add(""); continue; }

                    if (utf8)
                    {
                        int n = data[sp++];
                        if ((n & 0x80) != 0) { n = ((n & 0x7F) << 8) | data[sp++]; }
                        int n2 = data[sp++];
                        if ((n2 & 0x80) != 0) { n2 = ((n2 & 0x7F) << 8) | data[sp++]; }
                        if (sp + n2 > data.Length) { p._items.Add(""); continue; }
                        p._items.Add(Encoding.UTF8.GetString(data, sp, n2));
                    }
                    else
                    {
                        int n = data[sp] | (data[sp + 1] << 8);
                        sp += 2;
                        if ((n & 0x8000) != 0) { n = ((n & 0x7FFF) << 16) | (data[sp] | (data[sp + 1] << 8)); sp += 2; }
                        if (sp + n * 2 > data.Length) { p._items.Add(""); continue; }
                        p._items.Add(Encoding.Unicode.GetString(data, sp, n * 2));
                    }
                }
                return p;
            }
            catch { return null; }
        }
    }
}
