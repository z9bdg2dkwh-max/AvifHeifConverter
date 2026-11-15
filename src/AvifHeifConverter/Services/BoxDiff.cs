using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AvifHeifConverter.Services
{
    /// <summary>
    /// ISOBMFF 逐字段对比工具（诊断用）。
    ///
    /// 用途：把"能正常解析的文件"与"解析失败的文件"做结构化逐项 diff，
    /// 精确定位唯一差异点，而不是靠猜。
    ///
    /// 对比维度：
    ///   - 顶层 box 树（类型/偏移/长度）
    ///   - meta 内各子 box
    ///   - iloc 每个条目的 method/base/offset/length
    ///   - iinf 的item 列表
    ///   - ipma 关联
    /// </summary>
    internal static class BoxDiff
    {
        public static string Compare(string goodPath, string badPath)
        {
            var good = File.ReadAllBytes(goodPath);
            var bad = File.ReadAllBytes(badPath);

            var sb = new StringBuilder();
            sb.AppendLine($"正常文件: {goodPath} ({good.Length:N0} B)");
            sb.AppendLine($"问题文件: {badPath} ({bad.Length:N0} B)");
            sb.AppendLine(new string('=', 60));

            sb.AppendLine("\n【1】顶层 box 树");
            var gTop = DumpTop(good);
            var bTop = DumpTop(bad);
            sb.AppendLine("  正常: " + string.Join("  ", gTop.Select(x => $"{x.type}@{x.off}+{x.size}")));
            sb.AppendLine("  问题: " + string.Join("  ", bTop.Select(x => $"{x.type}@{x.off}+{x.size}")));

            sb.AppendLine("\n【2】meta 子 box");
            var gMeta = DumpChildren(good, FindBox(good, "meta"));
            var bMeta = DumpChildren(bad, FindBox(bad, "meta"));
            sb.AppendLine($"  正常 ({gMeta.Count}): " +
                string.Join("  ", gMeta.Select(x => $"{x.type}@{x.off}+{x.size}")));
            sb.AppendLine($"  问题 ({bMeta.Count}): " +
                string.Join("  ", bMeta.Select(x => $"{x.type}@{x.off}+{x.size}")));

            sb.AppendLine("\n【3】iloc 条目逐项");
            sb.AppendLine(DiffIloc(good, bad));

            sb.AppendLine("\n【4】iinf item 列表");
            sb.AppendLine(DiffIinf(good, bad));

            return sb.ToString();
        }

        private sealed record BoxInfo(string type, long off, long size);

        private static BoxInfo? FindBox(byte[] d, string type)
        {
            long o = 0;
            while (o + 8 <= d.Length)
            {
                long sz = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan((int)o, 4));
                string t = Encoding.ASCII.GetString(d, (int)o + 4, 4);
                if (sz < 8) return null;
                if (t == type) return new BoxInfo(t, o, sz);
                if (t == "mdat") return null;
                o += sz;
            }
            return null;
        }

        private static List<BoxInfo> DumpTop(byte[] d)
        {
            var list = new List<BoxInfo>();
            long o = 0;
            while (o + 8 <= d.Length)
            {
                long sz = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan((int)o, 4));
                string t = Encoding.ASCII.GetString(d, (int)o + 4, 4);
                if (sz < 8) break;
                list.Add(new BoxInfo(t, o, sz));
                if (t == "mdat") break;
                o += sz;
            }
            return list;
        }

        private static List<BoxInfo> DumpChildren(byte[] d, BoxInfo? parent)
        {
            var list = new List<BoxInfo>();
            if (parent == null) return list;
            long p = parent.off + 8;
            if (parent.type == "meta") p += 4;
            long end = parent.off + parent.size;
            while (p + 8 <= end)
            {
                long sz = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan((int)p, 4));
                string t = Encoding.ASCII.GetString(d, (int)p + 4, 4);
                if (sz < 8 || p + sz > end) break;
                list.Add(new BoxInfo(t, p, sz));
                p += sz;
            }
            return list;
        }

        private static string DiffIloc(byte[] good, byte[] bad)
        {
            var sb = new StringBuilder();
            var g = ParseIloc(good);
            var b = ParseIloc(bad);

            sb.AppendLine($"  正常: {g.Count} 条");
            foreach (var e in g)
                sb.AppendLine($"    id={e.id,-4} m={e.method} base={e.baseOff,-10} " +
                             $"off={e.off,-10} len={e.len,-10} abs={e.baseOff + e.off}");
            sb.AppendLine($"  问题: {b.Count} 条");
            foreach (var e in b)
                sb.AppendLine($"    id={e.id,-4} m={e.method} base={e.baseOff,-10} " +
                             $"off={e.off,-10} len={e.len,-10} abs={e.baseOff + e.off}" +
                             (e.baseOff + e.off + e.len > bad.Length ? "  <<越界!" : ""));

            return sb.ToString();
        }

        private sealed record IlocEntry(uint id, int method, long baseOff, long off, long len);

        private static List<IlocEntry> ParseIloc(byte[] d)
        {
            var list = new List<IlocEntry>();
            var meta = FindBox(d, "meta");
            if (meta == null) return list;

            // 只遍历 meta 的**直接子 box**，避免误匹配 ipma 等box 内部的字节
            long p = meta.off + 8 + 4;      // meta header + FullBox version/flags
            long end = meta.off + meta.size;
            while (p + 8 <= end)
            {
                long sz = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan((int)p, 4));
                string t = Encoding.ASCII.GetString(d, (int)p + 4, 4);
                if (sz < 8 || p + sz > end) break;

                if (t == "iloc") list.AddRange(ParseOneIloc(d, p, sz));
                p += sz;
            }
            return list;
        }

        private static List<IlocEntry> ParseOneIloc(byte[] d, long boxOff, long boxSize)
        {
            var list = new List<IlocEntry>();
            long q = boxOff + 8;
            if (q + 8 > d.Length) return list;

            byte version = d[q];
            int osz = d[q + 4] >> 4, lsz = d[q + 4] & 0x0F;
            int bsz = d[q + 5] >> 4, isz = d[q + 5] & 0x0F;

            int count;
            long p;
            if (version < 2)
            {
                count = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan((int)q + 6, 2));
                p = q + 8;
            }
            else
            {
                count = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan((int)q + 4, 4));
                p = q + 8;
            }

            for (int i = 0; i < count; i++)
            {
                if (p + 8 > d.Length) break;
                uint id = version < 1
                    ? BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan((int)p, 2))
                    : BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan((int)p, 4));
                p += version < 1 ? 2 : 4;

                int dr = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan((int)p, 2)); p += 2;
                int method = dr & 0x0F;

                long baseOff = 0;
                if (bsz > 0)
                {
                    if (version >= 1) { baseOff = ReadUInt(d, ref p, bsz); }
                }

                if (p + 2 > d.Length) break;
                int ec = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan((int)p, 2)); p += 2;

                for (int e = 0; e < ec; e++)
                {
                    if (isz > 0) p += isz;
                    long off = ReadUInt(d, ref p, osz);
                    long len = ReadUInt(d, ref p, lsz);
                    list.Add(new IlocEntry(id, method, baseOff, off, len));
                }
            }
            return list;
        }

        private static long ReadUInt(byte[] d, ref long p, int size)
        {
            long v = 0;
            for (int i = 0; i < size; i++) v = (v << 8) | d[p + i];
            p += size;
            return v;
        }

        private static string DiffIinf(byte[] good, byte[] bad)
        {
            var sb = new StringBuilder();
            sb.AppendLine("  正常: " + string.Join(", ", ParseIinf(good)));
            sb.AppendLine("  问题: " + string.Join(", ", ParseIinf(bad)));
            return sb.ToString();
        }

        private static List<string> ParseIinf(byte[] d)
        {
            var list = new List<string>();
            var meta = FindBox(d, "meta");
            if (meta == null) return list;

            long p = meta.off + 12;
            long end = meta.off + meta.size;
            while (p + 8 <= end)
            {
                long sz = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan((int)p, 4));
                string t = Encoding.ASCII.GetString(d, (int)p + 4, 4);
                if (sz < 8) break;

                if (t == "iinf")
                {
                    long q = p + 8;
                    byte ver = d[q];
                    long r = q + 4;
                    int cnt = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan((int)r, 2));
                    r += 2;
                    for (int i = 0; i < cnt && r + 8 < p + sz; i++)
                    {
                        long isz = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan((int)r, 4));
                        string it = Encoding.ASCII.GetString(d, (int)r + 4, 4);
                        if (isz < 8) break;
                        uint iid = BinaryPrimitives.ReadUInt16BigEndian(
                            d.AsSpan((int)r + 8, 2));
                        string itype = Encoding.ASCII.GetString(d, (int)r + 12, 4);
                        list.Add($"{iid}:{itype}");
                        r += isz;
                    }
                }
                p += sz;
            }
            return list;
        }
    }
}