using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AvifHeifConverter.Services
{
    /// <summary>
    /// ISOBMFF（HEIF/AVIF 容器）box 结构解析器。
    ///
    /// 为什么需要自研：libheif 的公开 API **不暴露 item_type = "tmap"**
    /// （ISO 21496-1 Gain Map 元数据）。它是 iinf 中的一个 item，
    /// 要做"完整保留 Gain Map"就必须自己读懂容器结构。
    ///
    /// 实现方式：按需读取整个文件到内存（照片动辄数十 MB，一次性读入最简单可靠），
    /// 然后做严格的边界检查遍历。**全程只读，绝不修改源文件。**
    /// </summary>
    internal sealed class IsoBmffReader
    {
        private readonly byte[] _data;

        public IsoBmffReader(byte[] data) => _data = data;

        // ---------- 基础 box 遍历 ----------

        public readonly struct Box
        {
            public string Type { get; init; }
            public long Offset { get; init; }      // box 起始偏移（含 8 字节头）
            public long Size { get; init; }        // 整个 box 长度
            public long HeaderSize { get; init; }  // 8 或 16
            public long PayloadOffset => Offset + HeaderSize;
            public long PayloadLength => Size - HeaderSize;
        }

        /// <summary>
        /// 读取 mime 类 item 的 content_type（infe v2 里紧跟 item_name 之后）。
        /// 返回 itemId → content_type；非 mime item 不出现在结果里。
        /// </summary>
        public Dictionary<uint, string> ParseContentTypes()
        {
            var map = new Dictionary<uint, string>();
            var iinf = FindInMeta("iinf");
            if (iinf == null) return map;

            long p = iinf.Value.PayloadOffset;
            if (p + 4 > _data.Length) return map;
            byte version = _data[p];
            long q = p + 4 + (version == 0 ? 2 : 4);
            long end = iinf.Value.Offset + iinf.Value.Size;

            while (q + 21 <= end)
            {
                if (!TryReadBox(q, out var infe) || infe.Type != "infe") break;
                long r = infe.PayloadOffset + 4;          // 跳过 version+flags
                if (r + 4 > end) break;
                uint id = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan((int)r, 2));
                r += 4;                                    // item_ID(2) + protection(2)
                string itype = Encoding.ASCII.GetString(_data, (int)r, 4);
                r += 4;
                if (itype.Trim() == "mime")
                {
                    // item_name（null 结尾）之后才是 content_type
                    long n = r;
                    while (n < infe.Offset + infe.Size && _data[n] != 0) n++;
                    n++;                                   // 跳过 null
                    long c = n;
                    while (c < infe.Offset + infe.Size && _data[c] != 0) c++;
                    if (c > n)
                        map[id] = Encoding.UTF8.GetString(_data, (int)n, (int)(c - n));
                }
                q += infe.Size;
            }
            return map;
        }

        // ---------- iloc：item 数据位置 ----------

        /// <summary>一个 item 的数据位置（可含多个 extent）。</summary>
        public sealed class ItemLoc
        {
            public int Method { get; init; }                       // 0=文件偏移 1=idat 2=其他 item
            public long BaseOffset { get; init; }
            public List<(long Offset, long Length)> Extents { get; init; } = new();
            public long TotalLength => Extents.Sum(e => e.Length);
        }

        /// <summary>
        /// 解析 iloc（按 ISO 14496-12 §8.11.3 完整处理 v0/v1/v2、
        /// 可变的 offset/length/base_offset/index 字段宽度、以及**多 extent**）。
        /// </summary>
        public Dictionary<uint, ItemLoc> ParseIloc()
        {
            var map = new Dictionary<uint, ItemLoc>();
            var box = FindInMeta("iloc");
            if (box == null) return map;

            long p = box.Value.PayloadOffset;
            long end = box.Value.Offset + box.Value.Size;
            if (p + 8 > end) return map;

            byte version = _data[p];
            p += 4;                                            // version(1) + flags(3)
            int offsetSize = _data[p] >> 4;
            int lengthSize = _data[p] & 0x0F;
            int baseSize = _data[p + 1] >> 4;
            int indexSize = (version == 1 || version == 2) ? (_data[p + 1] & 0x0F) : 0;
            p += 2;

            long count = version < 2
                ? BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan((int)p, 2))
                : BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan((int)p, 4));
            p += version < 2 ? 2 : 4;

            long ReadField(int size)
            {
                if (size == 0) return 0;
                long v = 0;
                for (int i = 0; i < size; i++) v = (v << 8) | _data[p + i];
                p += size;
                return v;
            }

            for (long i = 0; i < count; i++)
            {
                if (p + 4 > end) break;
                uint id = version < 2
                    ? BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan((int)p, 2))
                    : BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan((int)p, 4));
                p += version < 2 ? 2 : 4;

                int method = 0;
                if (version == 1 || version == 2)
                {
                    method = _data[p + 1] & 0x0F;              // 低 4 位是 construction_method
                    p += 2;
                }
                p += 2;                                        // data_reference_index
                long baseOff = ReadField(baseSize);

                if (p + 2 > end) break;
                int ec = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan((int)p, 2));
                p += 2;

                var ex = new List<(long, long)>();
                for (int j = 0; j < ec; j++)
                {
                    if (indexSize > 0) ReadField(indexSize);
                    long off = ReadField(offsetSize);
                    long len = ReadField(lengthSize);
                    ex.Add((off, len));
                }
                map[id] = new ItemLoc { Method = method, BaseOffset = baseOff, Extents = ex };
            }
            return map;
        }

        /// <summary>读取一个 item 的完整数据字节；越界返回 null。</summary>
        public byte[]? ReadItemBytes(uint itemId, ItemLoc loc)
        {
            var idat = FindInMeta("idat");
            long idatBase = idat?.PayloadOffset ?? 0;

            var parts = new List<byte>((int)Math.Max(0, loc.TotalLength));
            foreach (var (off, len) in loc.Extents)
            {
                if (len <= 0) continue;
                long start = loc.Method == 1 ? idatBase + loc.BaseOffset + off
                                             : loc.BaseOffset + off;
                if (start < 0 || start + len > _data.Length) return null;
                var buf = new byte[len];
                Array.Copy(_data, start, buf, 0, (int)len);
                parts.AddRange(buf);
            }
            return parts.Count > 0 ? parts.ToArray() : null;
        }

        /// <summary>读取一个 box 头；失败返回 false。</summary>
        public bool TryReadBox(long offset, out Box box)
        {
            box = default;
            if (offset < 0 || offset + 8 > _data.Length) return false;

            long size = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan((int)offset, 4));
            string type = Encoding.ASCII.GetString(_data, (int)offset + 4, 4);
            long header = 8;

            if (size == 1)
            {
                if (offset + 16 > _data.Length) return false;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(_data.AsSpan((int)offset + 8, 8));
                header = 16;
            }
            else if (size == 0)
            {
                size = _data.Length - offset; // 延伸到文件末尾
            }

            if (size < header || offset + size > _data.Length) return false;

            box = new Box { Type = type, Offset = offset, Size = size, HeaderSize = header };
            return true;
        }

        /// <summary>遍历容器 box 的直接子 box。</summary>
        public IEnumerable<Box> Children(Box? parentBox)
        {
            if (parentBox == null) yield break;
            var parent = parentBox.Value;

            long p = parent.PayloadOffset;
            // meta 是 FullBox：其 payload 前 4 字节是 version/flags
            if (parent.Type == "meta") p += 4;

            long end = parent.Offset + parent.Size;
            while (p + 8 <= end)
            {
                if (!TryReadBox(p, out var child)) yield break;
                yield return child;
                if (child.Size <= 0) yield break;
                p += child.Size;
            }
        }

        public Box? FindTopLevel(string type)
        {
            long p = 0;
            while (p + 8 <= _data.Length)
            {
                if (!TryReadBox(p, out var b)) return null;
                if (b.Type == type) return b;
                // ★ 不要在这里遇到 mdat 就返回。
                //   ISO 14496-12 允许 meta 出现在 mdat 之后；提前返回会让这类
                //   文件整个转换失败。TryReadBox 会校验 size 边界，按 box.Size
                //   步进本身是安全的，继续扫描不会误读 mdat 内部字节。
                if (b.Size <= 0) return null;
                p += b.Size;
            }
            return null;
        }

        // ---------- meta 子结构 ----------

        public Box? FindInMeta(string type)
        {
            var meta = FindTopLevel("meta");
            if (meta == null) return null;
            foreach (var c in Children(meta.Value))
                if (c.Type == type) return c;
            return null;
        }

        /// <summary>
        /// 在 iprp 内查找直接子 box（如 ipma）。
        /// ipma 嵌在 iprp 里而非 meta 直属，必须单独查——这一点
        /// 是实测苹果 HEIC 与索尼 AVIF 后才确认的。
        /// </summary>
        public Box? FindInIprp(string type)
        {
            var iprp = FindInMeta("iprp");
            if (iprp == null) return null;
            foreach (var c in Children(iprp.Value))
                if (c.Type == type) return c;
            return null;
        }

        // ---------- iinf：item 清单 ----------

        /// <summary>解析 iinf，返回 (itemId, itemType, itemName, hidden)。</summary>
        public List<(uint Id, string Type, string Name, bool Hidden)> ParseItems()
        {
            var result = new List<(uint, string, string, bool)>();
            var iinf = FindInMeta("iinf");
            if (iinf == null) return result;

            long p = iinf.Value.PayloadOffset;
            if (p + 4 > _data.Length) return result;

            byte version = _data[p];
            long q = p + 4;
            if (version == 0)
            {
                if (q + 2 > _data.Length) return result;
                result.Capacity = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan((int)q, 2));
                q += 2;
            }
            else if (version == 1)
            {
                if (q + 4 > _data.Length) return result;
                result.Capacity = (int)BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan((int)q, 4));
                q += 4;
            }
            else
            {
                return result;
            }

            long end = iinf.Value.Offset + iinf.Value.Size;
            while (q + 8 <= end)
            {
                if (!TryReadBox(q, out var infe) || infe.Type != "infe") break;

                ParseInfe(infe, result);
                q += infe.Size;
            }
            return result;
        }

        private void ParseInfe(Box infe, List<(uint, string, string, bool)> into)
        {
            long p = infe.PayloadOffset;
            if (p >= _data.Length) return;

            byte version = _data[p];
            long q = p + 4; // version(1) + flags(3)

            uint id;
            string itemType;
            if (version >= 2)
            {
                if (q + 4 > _data.Length) return;
                id = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan((int)q, 2));
                q += 4; // item_ID(2) + protection(2)
                if (q + 4 > _data.Length) return;
                itemType = Encoding.ASCII.GetString(_data, (int)q, 4);
                q += 4;
            }
            else
            {
                // ★ infe v0/v1 的布局是 item_ID(2) + item_protection_index(2) + item_name，
                //   **没有 item_type 字段**（item_type 是 v2 才引入的）。
                //   曾漏跳 protection 并把随后的 4 字节当类型读 —— 对 v0/v1 文件会读出乱码，
                //   导致 tmap/Exif 等按类型查找的逻辑全部失配。
                if (q + 4 > _data.Length) return;
                id = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan((int)q, 2));
                q += 4;               // item_ID(2) + protection(2)
                itemType = "";        // v0/v1 无类型信息
            }

            string name = "";
            if (version >= 2)
            {
                // item_name 为 null 结尾字符串
                long end = infe.Offset + infe.Size;
                int len = 0;
                while (q + len < end && _data[q + len] != 0) len++;
                name = Encoding.UTF8.GetString(_data, (int)q, len);
                q += len + 1;
            }

            // hidden 标志 = infe 的 flags（24 位大端）最低位。
            // FullBox 布局为 version(1) + flags(3)，故 flags 低字节在 payload+3。
            // ★ 曾经误写成 p+1（那是 flags 的最高字节，恒为 0），
            //   导致源文件里 hidden=true 的增益图被读成 false，
            //   输出文件里增益图不再隐藏（会被当成一张独立图片列出）。
            bool hidden = (_data[p + 3] & 0x01) != 0;

            into.Add((id, itemType, name, hidden));
        }

        // ---------- pitm：主图 item ----------

        public uint ParsePrimaryItemId()
        {
            var pitm = FindInMeta("pitm");
            if (pitm == null) return 0;

            // pitm 是 FullBox：payload 前 4 字节为 version(1) + flags(3)，
            // item_ID 紧随其后。version 0 为 16 位，version 1 为 32 位。
            long p = pitm.Value.PayloadOffset;
            if (p >= _data.Length) return 0;

            byte version = _data[p];
            long idPos = p + 4;

            if (version == 0)
            {
                if (idPos + 2 > _data.Length) return 0;
                return BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan((int)idPos, 2));
            }
            if (idPos + 4 > _data.Length) return 0;
            return BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan((int)idPos, 4));
        }

        // ---------- ipma：item 属性关联 ----------

        /// <summary>
        /// 解析 ipma，返回 itemId -> 属性索引列表（essential 标记合并进高位）。
        /// </summary>
        public Dictionary<uint, List<int>> ParsePropertyAssociations()
        {
            var map = new Dictionary<uint, List<int>>();
            var ipma = FindInIprp("ipma");
            if (ipma == null) return map;

            long p = ipma.Value.PayloadOffset;
            if (p + 4 > _data.Length) return map;

            byte version = _data[p];

            // flags 的最低位决定 property index 的宽度：
            //   0 -> 7 位索引（单字节：1 位 essential + 7 位索引）
            //   1 -> 15 位索引（双字节：1 位 essential + 15 位索引）
            // ★ flags 是 24 位大端，其最低位在**第 3 个字节**（p+3），
            //   不是 p+1（那是最高字节）。曾误读 p+1。
            //   实测（2026-10-07）：苹果 HEIC、iPad AVIF、索尼 AVIF 三者
            //   flags 均为 0x000000，即一律走 7 位分支。此前注释里
            //   「索尼用 15 位」是未经验证的推测，已证伪并更正。
            bool wideIndex = (_data[p + 3] & 0x01) != 0;

            long q = p + 4;

            int entryCount;
            if (version < 1)
            {
                entryCount = (int)BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan((int)q, 4));
                q += 4;
            }
            else
            {
                entryCount = (int)BinaryPrimitives.ReadUInt64BigEndian(_data.AsSpan((int)q, 8));
                q += 8;
            }

            for (int i = 0; i < entryCount; i++)
            {
                uint id;
                if (version < 1)
                {
                    if (q + 2 > _data.Length) break;
                    id = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan((int)q, 2));
                    q += 2;
                }
                else
                {
                    if (q + 4 > _data.Length) break;
                    id = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan((int)q, 4));
                    q += 4;
                }

                if (q >= _data.Length) break;
                int assocCount = _data[q];
                q += 1;

                var list = new List<int>(assocCount);
                for (int k = 0; k < assocCount; k++)
                {
                    if (wideIndex)
                    {
                        if (q + 2 > _data.Length) break;
                        ushort v = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan((int)q, 2));
                        q += 2;
                        bool essential = (v & 0x8000) != 0;
                        list.Add((v & 0x7FFF) | (essential ? 0x10000 : 0));
                    }
                    else
                    {
                        if (q >= _data.Length) break;
                        byte b0 = _data[q];
                        q += 1;
                        // ISO/IEC 14496-12 §8.11.3.1：
                        //   flags&1 == 0 时用 **7 位** 索引，单字节，
                        //   其最高位（bit7）是 essential 标志。
                        //   ★ 曾把 bit7 当成「索引扩展」标志并多吃 1 字节，
                        //     使苹果文件（7 位编码 + essential）整段错位。
                        bool ess = (b0 & 0x80) != 0;
                        list.Add((b0 & 0x7F) | (ess ? 0x10000 : 0));
                    }
                }
                map[id] = list;
            }
            return map;
        }

        // ---------- ipco：属性表 ----------

        public List<Box> ParsePropertyBoxes()
        {
            var list = new List<Box>();
            var ipco = FindInIprp("ipco");
            if (ipco == null) return list;

            long p = ipco.Value.PayloadOffset;
            long end = ipco.Value.Offset + ipco.Value.Size;
            while (p + 8 <= end)
            {
                if (!TryReadBox(p, out var prop)) break;
                list.Add(prop);
                p += prop.Size;
            }
            return list;
        }

        // ---------- 属性读取 ----------

        public (int Width, int Height)? ReadIspe(Box ispe)
        {
            if (ispe.Type != "ispe" || ispe.PayloadLength < 12) return null;
            // ispe 是 FullBox：version+flags 占 4 字节，尺寸在 +4 / +8
            int o = (int)ispe.PayloadOffset + 4;
            int w = (int)BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(o, 4));
            int h = (int)BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(o + 4, 4));
            return (w, h);
        }

        public (ushort Primaries, ushort Transfer, ushort Matrix, bool FullRange)? ReadNclx(Box colr)
        {
            if (colr.Type != "colr" || colr.PayloadLength < 11) return null;
            int o = (int)colr.PayloadOffset;
            if (Encoding.ASCII.GetString(_data, o, 4) != "nclx") return null;

            ushort prim = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(o + 4, 2));
            ushort tr = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(o + 6, 2));
            ushort mat = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(o + 8, 2));
            bool fr = (_data[o + 10] & 0x80) != 0;
            return (prim, tr, mat, fr);
        }

        public (int Channels, byte[] Depths)? ReadPixi(Box pixi)
        {
            if (pixi.Type != "pixi" || pixi.PayloadLength < 2) return null;
            // pixi 是 FullBox：version+flags(4) 后紧跟 num_channels
            int o = (int)pixi.PayloadOffset + 4;
            int n = _data[o];
            if (o + 1 + n > _data.Length) return null;
            var depths = new byte[n];
            Array.Copy(_data, o + 1, depths, 0, n);
            return (n, depths);
        }

        public (ushort MaxCLL, ushort MaxPALL)? ReadClliData(Box clli)
        {
            // clli 是**普通 Box**（ISO/IEC 23008-12），不是 FullBox：
            // payload 直接是 max_content_light_level(2) + max_pic_average_light_level(2)。
            // ★ 曾经按 FullBox 读（+4），会把亮度值读成 0。
            if (clli.Type != "clli" || clli.PayloadLength < 4) return null;
            int o = (int)clli.PayloadOffset;
            return (BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(o, 2)),
                    BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(o + 2, 2)));
        }

        // ---------- iref：item 引用 ----------

        /// <summary>解析 iref，返回 (引用类型, fromId, toIds)。</summary>
        public List<(string RefType, uint From, List<uint> To)> ParseReferences()
        {
            var result = new List<(string, uint, List<uint>)>();
            var iref = FindInMeta("iref");
            if (iref == null) return result;

            long p = iref.Value.PayloadOffset;
            if (p + 4 > _data.Length) return result;

            byte version = _data[p];
            long q = p + 4;
            long end = iref.Value.Offset + iref.Value.Size;

            while (q + 8 <= end)
            {
                if (!TryReadBox(q, out var refBox)) break;

                long r = refBox.PayloadOffset;
                uint from;
                ushort count;

                if (version == 0)
                {
                    if (r + 4 > _data.Length) break;
                    from = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan((int)r, 2));
                    count = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan((int)r + 2, 2));
                    r += 4;
                }
                else
                {
                    if (r + 8 > _data.Length) break;
                    from = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan((int)r, 4));
                    count = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan((int)r + 4, 2));
                    r += 6;
                }

                var to = new List<uint>(count);
                for (int i = 0; i < count; i++)
                {
                    if (version == 0)
                    {
                        if (r + 2 > _data.Length) break;
                        to.Add(BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan((int)r, 2)));
                        r += 2;
                    }
                    else
                    {
                        if (r + 4 > _data.Length) break;
                        to.Add(BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan((int)r, 4)));
                        r += 4;
                    }
                }
                result.Add((refBox.Type, from, to));
                q += refBox.Size;
            }
            return result;
        }

        /// <summary>判断 item 是否为网格图像（其 dimg 引用多个 tile）。</summary>
        public bool IsGridItem(uint itemId, List<(string RefType, uint From, List<uint> To)> refs)
        {
            foreach (var (type, from, to) in refs)
                if (type == "dimg" && from == itemId && to.Count > 1) return true;
            return false;
        }

        public byte[] Data => _data;
    }
}