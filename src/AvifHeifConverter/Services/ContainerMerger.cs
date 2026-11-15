using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AvifHeifConverter.Services
{
    /// <summary>
    /// Gain Map 容器重组器。
    ///
    /// 全部布局细节均经「最小化阶梯实验」+「与苹果原生文件逐字节对照」验证：
    ///   1. 所有 FullBox 的 version+flags 合计 **4 字节**（version 1 + flags 3）。
    ///      infe：size(4) "infe"(4) ver(1) flags(3) item_ID(2) prot(2)
    ///            item_type(4) name_len(1) name(...) —— 空名共 21 字节。
    ///      ★ hidden = flags 最低位，即 payload[3]（曾误读 payload[1]）。
    ///   2. iloc：ver+flags(4) sizes(2) entry_count(2) + 每项 18 字节。
    ///      **读取时必须跳过那 2 字节 sizes 字段**。
    ///   3. ipco / ipma 是 **iprp 的子 box**，不是 meta 的直接子级。
    ///   4. iloc 长度固定 ⇒ delta 可先算出，无循环依赖。
    ///      ★ delta 必须**同时含 ftyp 的长度变化**（补 tmap 品牌会让 ftyp 长 4 字节）。
    ///
    /// ★ 与 iOS 兼容性直接相关的五点（缺一即可能打不开）：
    ///   a. ftyp.compatible_brands 必须声明 **'tmap'**（文件含 tmap item 时）。
    ///   b. 增益图必须挂 **auxC** 属性：urn:com:apple:photo:2020:aux:hdrgainmap。
    ///   c. 增益图必须用 **auxl** 引用指向主图（ISO 23008-12 6.4.5）。
    ///   d. 增益图与其 tile 必须 **hidden=true**，否则读取器会把它们当独立图片解码。
    ///   e. **grpl/altr** 分组必须保留：把 tmap item 与主图声明为同一内容的两种呈现。
    ///      它丢失后，读取器会认为 tmap 是一张无法解码的独立图片。
    ///
    /// 不支持 Gain Map 的现成工具（libheif / ImageMagick / libavif 均已核实），
    /// 故必须自研容器重组。
    /// </summary>
    internal sealed class ContainerMerger
    {
        private readonly byte[] _base;

        /// <summary>目标文件各 item 的 content_type（仅 mime 类），用于重建 infe 时不丢字段。</summary>
        private readonly Dictionary<uint, string> _baseContentTypes;

        public ContainerMerger(byte[] convertedFile)
        {
            _base = convertedFile;
            try { _baseContentTypes = new IsoBmffReader(convertedFile).ParseContentTypes(); }
            catch { _baseContentTypes = new Dictionary<uint, string>(); }
        }

        public sealed class Result
        {
            public bool Success { get; init; }
            public string? Error { get; init; }
            public string OutPath { get; init; } = "";
            public int ItemsAdded { get; init; }
            public int PropsAdded { get; init; }
            public long BytesAppended { get; init; }
            public string Layout { get; init; } = "";
        }

        // ---------------- 主流程 ----------------

        public Result Merge(string outPath, GainMapExtractor.Result gm,
                            List<byte[]> srcPropBytes)
        {
            try
            {
                if (gm.Items.Count == 0) return Fail(outPath, "没有可搬运的 Gain Map 数据");

                var ftyp = FindBox("ftyp");
                var meta = FindBox("meta") ?? throw new InvalidOperationException("缺 meta");
                var mdat = FindBox("mdat") ?? throw new InvalidOperationException("缺 mdat");

                var children = Children(meta);
                var oldItems = ParseIinf();
                var origIloc = ParseIlocOf();

                System.Diagnostics.Debug.WriteLine(
                    $"[merge] base={_base.Length} oldItems={oldItems.Count} " +
                    $"gmItems={gm.Items.Count} tiles={gm.TileIds.Count} " +
                    $"origIloc={origIloc.Count} children={children.Count}");

                if (Environment.GetEnvironmentVariable("HEIF_DEBUG") == "1")
                {
                    Console.WriteLine($"[merge] base={_base.Length} oldItems={oldItems.Count} " +
                        $"gmItems={gm.Items.Count} tiles={gm.TileIds.Count} " +
                        $"origIloc={origIloc.Count} children={children.Count}");
                    foreach (var c in children) Console.WriteLine($"   child {c.type} +{c.size}");
                }

                // ---- 1. 分配新 id ----
                uint next = 1;
                foreach (var it in oldItems) if (it.Id >= next) next = it.Id + 1;

                var idMap = new Dictionary<uint, uint>();
                uint newMetaId = next++; idMap[gm.MetaItemId] = newMetaId;

                uint newImageId = 0;
                if (gm.ImageItemId != 0) { newImageId = next++; idMap[gm.ImageItemId] = newImageId; }

                var newTileIds = new List<uint>();
                foreach (var t in gm.TileIds) { idMap[t] = next; newTileIds.Add(next); next++; }

                // 其余 gm item（如附加的 XMP mime item）也要分配新 id，
                // 否则 iinf / iloc 会把它整条跳过（曾因此导致 XMP 不写入）。
                foreach (var it in gm.Items)
                    if (!idMap.ContainsKey(it.Id)) idMap[it.Id] = next++;

                // ---- 2. 复制属性 box ----
                // ★ 新属性索引必须从「已有属性数 + 1」开始，
                //   否则会与目标文件原有属性**索引冲突**，
                //   导致新 item 指向了别人的属性（如主图的 hvcC），
                //   自身必需的 av1C/hvcC 缺失 → 解码器报 "No 'av1C' box"。
                int existingPropCount = CountIprpProperties(children);
                var propMap = new Dictionary<int, int>();
                var newPropBytes = new List<byte[]>();
                int nextProp = existingPropCount + 1;
                var newAssoc = new Dictionary<uint, List<int>>();

                foreach (var item in gm.Items)
                {
                    var list = new List<int>();
                    foreach (var idx in item.PropertyIndices)
                    {
                        int src = idx & 0xFFFF;
                        if (src < 1 || src > srcPropBytes.Count) continue;
                        if (!propMap.TryGetValue(src, out var mapped))
                        {
                            mapped = nextProp++;
                            propMap[src] = mapped;
                            // ★ 属性字节必须由调用方从**源文件**切好传入。
                            //   若在此处按 box.Offset 从 _base（目标文件）取，
                            //   会读到完全无关的字节。
                            newPropBytes.Add(srcPropBytes[src - 1]);
                        }
                        // 保留源的 essential 标志（bit16）
                        list.Add(mapped | (idx & 0x10000));
                    }
                    // 无属性的 item（如 XMP mime item）不写 ipma 记录 —— 苹果的 mime item
                    // 同样不出现在 ipma 里；写一条「0 个属性」是多余且非标准的。
                    if (list.Count > 0) newAssoc[idMap[item.Id]] = list;
                }

                // ---- 2b. 给增益图补 auxC（Apple HDR Gain Map 识别标识）----
                // 苹果用 auxC 的 aux_type = urn:com:apple:photo:2020:aux:hdrgainmap
                // 标明「这是一张 HDR 增益图」，并置 essential。
                // 缺少它，读取器只会看到一张普通辅助图，不会按增益图合成 HDR。
                // 实测来源：IMG_8208.HEIC 的 ipco 第 8 项（auxC, size=52）。
                if (newImageId != 0)
                {
                    int auxCIndex = nextProp++;
                    newPropBytes.Add(MakeAuxC(GainMapAuxType));
                    if (!newAssoc.TryGetValue(newImageId, out var auxList))
                    {
                        auxList = new List<int>();
                        newAssoc[newImageId] = auxList;
                    }
                    auxList.Add(auxCIndex | 0x10000);   // essential = true（与苹果一致）
                }

                // ---- 3. 构建各表 ----
                uint basePrimaryId = ParsePrimaryId();

                // ftyp 必须重建：只要文件里出现 tmap item，
                // compatible_brands 就必须声明 'tmap' 品牌（ISO 21496-1）。
                // 源 AVIF 与苹果 iOS 18 的 HEIC 都声明了，我们此前漏掉，
                // 这是最致命的一处不合规。
                byte[] ftypBytes = BuildFtyp(ftyp);

                byte[] hdlrBytes = ChildBytes(children, "hdlr") ?? MakeBox("hdlr", new byte[25]);
                byte[] pitmBytes = ChildBytes(children, "pitm") ?? MakeBox("pitm", new byte[6]);
                byte[] iinfBytes = BuildIinf(oldItems, gm, idMap, _baseContentTypes);
                byte[] iprpBytes = BuildIprp(children, newPropBytes, newAssoc);
                byte[] irefBytes = BuildIref(newMetaId, newImageId, newTileIds, basePrimaryId,
                    // cdsc → 增益图：只给「附加在增益图上的元数据」（自生成的 XMP）
                    gm.Items.Where(x => x.Type.Trim() == "mime" && !string.IsNullOrEmpty(x.ContentType)
                                        && !x.LinkToPrimary)
                            .Select(x => idMap.TryGetValue(x.Id, out var v) ? v : 0)
                            .Where(v => v != 0).ToList(),
                    // cdsc → 主图：Exif / 源文件原生 XMP
                    gm.Items.Where(x => x.LinkToPrimary)
                            .Select(x => idMap.TryGetValue(x.Id, out var v) ? v : 0)
                            .Where(v => v != 0).ToList());
                byte[] grplBytes = BuildGrpl(newMetaId, basePrimaryId) ?? Array.Empty<byte>();

                int entryCount = oldItems.Count + gm.Items.Count;
                int ilocLen = 8 + 4 + 2 + 2 + entryCount * 18;

                // ---- 4. 先算 meta 总长（iloc 长度固定 → 无循环依赖）----
                int metaInnerLen = 4 + hdlrBytes.Length + ilocLen + iinfBytes.Length
                                 + pitmBytes.Length + iprpBytes.Length + irefBytes.Length
                                 + grplBytes.Length;
                int newMetaSize = 8 + metaInnerLen;

                // ★ delta 必须同时包含 **ftyp 的变化**。
                //   补 'tmap' 品牌后 ftyp 会长 4 字节，而 ftyp 在 meta 之前，
                //   所有 item 的绝对偏移都要跟着平移。只算 meta 会让整个 mdat
                //   的数据错位 4 字节（heif-info/heif-dec 容错所以看不出来，
                //   但严格读取器会解出花屏或直接拒绝）。
                long ftypDelta = ftypBytes.Length - (ftyp?.Size ?? 0);
                long delta = ftypDelta + (newMetaSize - meta.Size);

                long newDataStart = meta.Offset + ftypDelta + newMetaSize + 8 + mdat.PayloadLength;
                byte[] ilocBytes = BuildIloc(oldItems, origIloc, gm, idMap, delta, newDataStart);

                // ---- 5. 组装 meta ----
                // 顺序对齐源文件/苹果：hdlr → iloc → iinf → pitm → iprp → iref → grpl
                var inner = new List<byte>();
                inner.AddRange(new byte[4]);
                inner.AddRange(hdlrBytes);
                inner.AddRange(ilocBytes);
                inner.AddRange(iinfBytes);
                inner.AddRange(pitmBytes);
                inner.AddRange(iprpBytes);
                inner.AddRange(irefBytes);
                inner.AddRange(grplBytes);
                byte[] metaBox = MakeBox("meta", inner.ToArray());

                // ---- 6. 组装整文件 ----
                long newDataLen = gm.Items.Sum(i => (long)i.Data.Length);
                long newMdatSize = mdat.Size + newDataLen;
                long total = ftypBytes.Length + metaBox.Length + newMdatSize;

                var buf = new byte[total];
                int pos = 0;
                Array.Copy(ftypBytes, 0, buf, pos, ftypBytes.Length); pos += ftypBytes.Length;
                Array.Copy(metaBox, 0, buf, pos, metaBox.Length); pos += metaBox.Length;

                WriteU32(buf, pos, (uint)newMdatSize);
                Encoding.ASCII.GetBytes("mdat").CopyTo(buf, pos + 4);
                pos += 8;
                Array.Copy(_base, mdat.PayloadOffset, buf, pos, (int)mdat.PayloadLength);
                pos += (int)mdat.PayloadLength;

                foreach (var item in gm.Items)
                {
                    if (!idMap.ContainsKey(item.Id)) continue;
                    Array.Copy(item.Data, 0, buf, pos, item.Data.Length);
                    pos += item.Data.Length;
                }

                if (pos != total)
                    throw new InvalidOperationException($"写入 {pos} 与预期 {total} 不符");

                File.WriteAllBytes(outPath, buf);

                return new Result
                {
                    Success = true, OutPath = outPath,
                    ItemsAdded = gm.Items.Count,
                    PropsAdded = newPropBytes.Count,
                    BytesAppended = newDataLen,
                    Layout = gm.LayoutKind,
                };
            }
            catch (Exception ex) { return Fail(outPath, ex.Message); }
        }

        private static Result Fail(string p, string e) =>
            new() { Success = false, Error = e, OutPath = p };

        // ---------------- 各表构建 ----------------

        /// <summary>统计 iprp/ipco 中已有的属性 box 数量。</summary>
        private int CountIprpProperties(List<(string type, long off, long size)> children)
        {
            var iprp = children.FirstOrDefault(c => c.type == "iprp");
            if (iprp.type != "iprp") return 0;

            long p = iprp.off + 8, end = iprp.off + iprp.size;
            while (p + 8 <= end)
            {
                int sz = (int)BinaryPrimitives.ReadUInt32BigEndian(_base.AsSpan((int)p, 4));
                if (sz < 8 || p + sz > end) break;
                string t = Encoding.ASCII.GetString(_base, (int)p + 4, 4);
                if (t == "ipco")
                {
                    int n = 0;
                    long r = p + 8, e2 = p + sz;
                    while (r + 8 <= e2)
                    {
                        int s3 = (int)BinaryPrimitives.ReadUInt32BigEndian(_base.AsSpan((int)r, 4));
                        if (s3 < 8 || r + s3 > e2) break;
                        n++; r += s3;
                    }
                    return n;
                }
                p += sz;
            }
            return 0;
        }

        private static byte[] BuildIinf(
            List<(uint Id, string Type, string Name, bool Hidden)> oldItems,
            GainMapExtractor.Result gm, Dictionary<uint, uint> idMap,
            Dictionary<uint, string>? oldContentTypes = null)
        {
            // 增益图本体与它的 tile 一律 hidden。
            // 依据：ISO/IEC 23008-12 规定 grid 的 tile 应为 hidden item；
            //       苹果的 HEIC（IMG_8208）把增益图 tile 与 grid 全部置 hidden=true，
            //       而部分厂商（索尼 AVIF）留成 false —— 会诱使读取器把它们
            //       当成独立图片去解码。
            var gmParts = new HashSet<uint>(gm.TileIds);
            gmParts.Add(gm.ImageItemId);

            var b = new List<byte>();
            var entries = new List<byte>();          // 先攒条目，最后按**实际条数**写 count
            int entryCount = 0;
            foreach (var it in oldItems)
            {
                // 目标文件自带的 mime item 也要保留 content_type，否则读取器无法识别
                string? ct = it.Type.Trim() == "mime" && oldContentTypes != null &&
                             oldContentTypes.TryGetValue(it.Id, out var v) ? v : null;
                entries.AddRange(BuildInfe(it.Id, it.Type, it.Name, it.Hidden, ct));
                entryCount++;
            }
            foreach (var it in gm.Items)
                if (idMap.TryGetValue(it.Id, out var nid))
                {
                    entries.AddRange(BuildInfe(nid, it.Type, it.Name,
                                               it.Hidden || gmParts.Contains(it.Id),
                                               it.ContentType));
                    entryCount++;
                }

            // ★ 声明数量 = 实际写入数量（曾写成 oldItems.Count + gm.Items.Count，
            //   与实际写入可能不符，会让 iinf 整体错位）
            b.AddRange(new byte[4]);
            b.AddRange(BE16((ushort)entryCount));
            b.AddRange(entries);
            return MakeBox("iinf", b.ToArray());
        }

        private static byte[] BuildInfe(uint id, string type, string name, bool hidden,
                                        string? contentType = null)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name ?? "");
            var ctBytes = contentType == null ? Array.Empty<byte>()
                                              : Encoding.UTF8.GetBytes(contentType);
            int payload = 1 + 3 + 2 + 2 + 4 + nameBytes.Length + 1
                        + (ctBytes.Length > 0 ? ctBytes.Length + 1 : 0);
            var ms = new MemoryStream();
            WriteU32(ms, (uint)(8 + payload));
            ms.Write(Encoding.ASCII.GetBytes("infe"));
            ms.WriteByte(2);
            ms.WriteByte(0); ms.WriteByte(0);
            ms.WriteByte((byte)(hidden ? 1 : 0));
            ms.Write(BE16((ushort)id));
            ms.Write(BE16(0));
            var t = (type ?? "").PadRight(4, ' ');
            ms.Write(Encoding.ASCII.GetBytes(t.Length >= 4 ? t.Substring(0, 4) : "Unkn"));
            ms.Write(nameBytes);
            ms.WriteByte(0);
            if (ctBytes.Length > 0)
            {
                ms.Write(ctBytes);
                ms.WriteByte(0);
            }
            return ms.ToArray();
        }

        private byte[] BuildIloc(
            List<(uint Id, string Type, string Name, bool Hidden)> oldItems,
            Dictionary<uint, (uint baseOff, uint off, uint len)> origIloc,
            GainMapExtractor.Result gm, Dictionary<uint, uint> idMap,
            long delta, long newDataStart)
        {
            var b = new List<byte>();
            var entries = new List<byte>();          // 先攒条目，最后按**实际条数**写 count
            int entryCount = 0;

            foreach (var it in oldItems)
            {
                entries.AddRange(BE16((ushort)it.Id));
                entries.AddRange(BE16(0));
                if (origIloc.TryGetValue(it.Id, out var l))
                {
                    long abs = (long)l.baseOff + delta;
                    if (abs < 0 || abs > uint.MaxValue)
                    {
                        // 偏移溢出说明 delta 推算有误，整表作废（计数同步归零，避免声明数与实际不符）
                        entries.Clear();
                        entryCount = 0;
                        break;
                    }
                    entries.AddRange(BE32((uint)abs));
                    entries.AddRange(BE16(1));
                    entries.AddRange(BE32(l.off));
                    entries.AddRange(BE32(l.len));
                }
                else
                {
                    // ★ 条目长度必须恒为 18 字节：
                    //   item_ID(2) + data_ref(2) + base_offset(4) + extent_count(2) + 偏移(4) + 长度(4)
                    //   曾在这里只写 6 字节（base + count），导致其后所有条目整体错位 ——
                    //   表现为 iloc 里读出 item ID 0 / 256 这类乱码，解码器报
                    //   "pitm box references a non-existing image"。
                    entries.AddRange(BE32(0));
                    entries.AddRange(BE16(0));   // extent_count = 0（无数据位置，条目仍占满 18 字节）
                    entries.AddRange(BE32(0));
                    entries.AddRange(BE32(0));
                }
                entryCount++;
            }

            long cursor = newDataStart;
            foreach (var item in gm.Items)
            {
                if (!idMap.TryGetValue(item.Id, out var nid)) continue;
                entries.AddRange(BE16((ushort)nid));
                entries.AddRange(BE16(0));
                entries.AddRange(BE32((uint)cursor));
                entries.AddRange(BE16(1));
                entries.AddRange(BE32(0));
                entries.AddRange(BE32((uint)item.Data.Length));
                cursor += item.Data.Length;
                entryCount++;
            }

            // ★ 声明数量必须等于实际写入的条目数。
            //   曾经写成 oldItems.Count + gm.Items.Count，而循环里可能因 idMap 缺项
            //   而 continue —— 声明数大于实际数会让 iloc 表整体错位。
            b.AddRange(new byte[4]);
            b.Add(0x44); b.Add(0x40);
            b.AddRange(BE16((ushort)entryCount));
            b.AddRange(entries);
            return MakeBox("iloc", b.ToArray());
        }

        private byte[] BuildIprp(
            List<(string type, long off, long size)> children,
            List<byte[]> newPropBytes, Dictionary<uint, List<int>> newAssoc)
        {
            var ipcoInner = new List<byte>();
            var entries = new List<(uint Id, List<int> Props)>();

            // ★ ipco / ipma 在 iprp 内部
            var iprp = children.FirstOrDefault(c => c.type == "iprp");
            if (iprp.type == "iprp")
            {
                long p = iprp.off + 8, end = iprp.off + iprp.size;
                while (p + 8 <= end)
                {
                    int sz = (int)BinaryPrimitives.ReadUInt32BigEndian(_base.AsSpan((int)p, 4));
                    if (sz < 8 || p + sz > end) break;
                    string t = Encoding.ASCII.GetString(_base, (int)p + 4, 4);
                    if (t == "ipco")
                    {
                        var payload = new byte[sz - 8];
                        Array.Copy(_base, p + 8, payload, 0, sz - 8);
                        ipcoInner.AddRange(payload);
                    }
                    else if (t == "ipma") entries.AddRange(ParseIpma(p, sz));
                    p += sz;
                }
            }

            foreach (var nb in newPropBytes) ipcoInner.AddRange(nb);
            foreach (var kv in newAssoc) entries.Add((kv.Key, kv.Value));

            var ib = new List<byte>();
            ib.AddRange(new byte[4]);
            ib.AddRange(BE32((uint)entries.Count));
            foreach (var (id, props) in entries)
            {
                ib.AddRange(BE16((ushort)id));
                ib.Add((byte)props.Count);
                foreach (var p in props)
                {
                    // ★ 只在该属性**确实**是 essential 时才置 0x80。
                    //   误把全部属性标为 essential 会让 iOS 直接判文件损坏
                    //   （libheif 会容错，所以只在 iOS 上暴露）。
                    int idx = p & 0x7F;
                    bool essential = (p & 0x10000) != 0;
                    ib.Add((byte)(idx | (essential ? 0x80 : 0)));
                }
            }

            return MakeBox("iprp", Concat(MakeBox("ipco", ipcoInner.ToArray()),
                                         MakeBox("ipma", ib.ToArray())));
        }

        private byte[] BuildIref(uint newMetaId, uint newImageId,
                                 List<uint> newTiles, uint primaryId,
                                 List<uint> metaIdsAttachedToGainMap,
                                 List<uint> metaIdsAttachedToPrimary)
        {
            var b = new List<byte>();
            b.AddRange(new byte[4]);

            var iref = FindInMeta("iref");
            if (iref != null)
            {
                long p = iref.Value.PayloadOffset + 4, end = iref.Value.Offset + iref.Value.Size;
                while (p + 8 <= end)
                {
                    int sz = (int)BinaryPrimitives.ReadUInt32BigEndian(_base.AsSpan((int)p, 4));
                    if (sz < 8 || p + sz > end) break;
                    var copy = new byte[sz];
                    Array.Copy(_base, p, copy, 0, sz);
                    b.AddRange(copy);
                    p += sz;
                }
            }

            if (newMetaId != 0 && newImageId != 0)
            {
                var p = new List<byte>();
                p.AddRange(BE16((ushort)newMetaId));
                p.AddRange(BE16(2));
                p.AddRange(BE16((ushort)primaryId));
                p.AddRange(BE16((ushort)newImageId));
                b.AddRange(MakeBox("dimg", p.ToArray()));

                if (newTiles.Count > 0)
                {
                    var p2 = new List<byte>();
                    p2.AddRange(BE16((ushort)newImageId));
                    p2.AddRange(BE16((ushort)newTiles.Count));
                    foreach (var t in newTiles) p2.AddRange(BE16((ushort)t));
                    b.AddRange(MakeBox("dimg", p2.ToArray()));
                }

                // auxl：辅助图 → 主图。ISO/IEC 23008-12 6.4.5 规定两者用 auxl 关联。
                // 苹果的 HEIC 里增益图同时具备 dimg（来自 tmap）与 auxl（指向主图），
                // 两套机制并存；只给 dimg 会让老读取器漏掉增益图。
                var pa = new List<byte>();
                pa.AddRange(BE16((ushort)newImageId));
                pa.AddRange(BE16(1));
                pa.AddRange(BE16((ushort)primaryId));
                b.AddRange(MakeBox("auxl", pa.ToArray()));

                // cdsc：挂在增益图上的元数据（XMP）→ 增益图。
                // 苹果的增益图 item 都带一段 XMP（HDRGainMap:HDRGainMapVersion /
                // HDRGainMapHeadroom），IOS 依赖它判断该增益图的提亮能力。
                foreach (var mId in metaIdsAttachedToGainMap)
                {
                    var pm = new List<byte>();
                    pm.AddRange(BE16((ushort)mId));
                    pm.AddRange(BE16(1));
                    pm.AddRange(BE16((ushort)newImageId));
                    b.AddRange(MakeBox("cdsc", pm.ToArray()));
                }

                // cdsc：挂在**主图**上的元数据（Exif / 源文件原生 XMP）→ 主图。
                // 新管线的 heif-enc 输入是自算的 PNG，PNG 里没有这些，必须从源文件搬过来，
                // 否则输出文件的 Exif/XMP 会全部丢失。
                if (primaryId != 0)
                {
                    foreach (var mId in metaIdsAttachedToPrimary)
                    {
                        var pm = new List<byte>();
                        pm.AddRange(BE16((ushort)mId));
                        pm.AddRange(BE16(1));
                        pm.AddRange(BE16((ushort)primaryId));
                        b.AddRange(MakeBox("cdsc", pm.ToArray()));
                    }
                }
            }
            return MakeBox("iref", b.ToArray());
        }

        /// <summary>Apple HDR Gain Map 的 aux_type 常量（实测自 IMG_8208.HEIC）。</summary>
        private const string GainMapAuxType = "urn:com:apple:photo:2020:aux:hdrgainmap";

        /// <summary>
        /// 重建 ftyp：保留原 major/minor/compatible，并补上必需品牌。
        ///
        /// ★ 关键：文件里只要有 tmap item，compatible_brands 就必须含 'tmap'。
        ///   源 AVIF（iPad 生成）是 avif,mif1,miaf,MA1A,tmap；
        ///   苹果 iOS 18 的 HEIC 是 mif1,MiHB,MiHA,heix,MiHE,MiPr,miaf,heic,tmap。
        ///   我们此前只保留 heif-enc 的 mif1,heix,miaf，等于宣称"我没有 tmap"。
        /// </summary>
        private byte[] BuildFtyp(IsoBmffReader.Box? ftyp)
        {
            string major = "heic";
            uint minor = 0;
            var brands = new List<string>();

            if (ftyp != null)
            {
                long p = ftyp.Value.PayloadOffset;
                int n = (int)ftyp.Value.PayloadLength;
                if (n >= 8)
                {
                    major = Encoding.ASCII.GetString(_base, (int)p, 4).Trim();
                    minor = BinaryPrimitives.ReadUInt32BigEndian(_base.AsSpan((int)p + 4, 4));
                    for (int i = 8; i + 4 <= n; i += 4)
                        brands.Add(Encoding.ASCII.GetString(_base, (int)p + i, 4).Trim());
                }
            }

            // 顺序对齐苹果：mif1 在前
            AddBrand(brands, "mif1");
            AddBrand(brands, major);
            AddBrand(brands, "miaf");
            AddBrand(brands, "tmap");   // ← 缺失的那一项

            var ms = new MemoryStream();
            WriteU32(ms, (uint)(16 + brands.Count * 4));
            ms.Write(Encoding.ASCII.GetBytes("ftyp"));
            ms.Write(Encoding.ASCII.GetBytes(major.PadRight(4, ' ').Substring(0, 4)));
            WriteU32(ms, minor);
            foreach (var b in brands)
                ms.Write(Encoding.ASCII.GetBytes(b.PadRight(4, ' ').Substring(0, 4)));
            return ms.ToArray();
        }

        private static void AddBrand(List<string> brands, string b)
        {
            if (!string.IsNullOrWhiteSpace(b) && !brands.Contains(b)) brands.Add(b);
        }

        /// <summary>
        /// 构建 grpl / altr 替代分组：把 tmap item 与主图声明为「同一内容的两种呈现」。
        ///
        /// 实测字节（源 AVIF，grpl size=36）：
        ///   00 00 00 24 'grpl'                        ← 普通 Box，**没有** version/flags
        ///     00 00 00 1c 'altr' 00 00 00 00          ← altr 是 FullBox
        ///       00 00 00 06                           ← group_id（32 位）
        ///       00 00 00 02                           ← num_entities（32 位）
        ///       00 00 00 05  00 00 00 01              ← entity_id[]（各 **32 位**！）
        ///
        /// 丢弃它会让读取器把 tmap 当成一张无法解码的独立图片。
        /// </summary>
        private byte[]? BuildGrpl(uint tmapId, uint baseId)
        {
            if (tmapId == 0 || baseId == 0) return null;

            var payload = new List<byte>();
            payload.AddRange(BE32(NextGroupId()));
            payload.AddRange(BE32(2));
            payload.AddRange(BE32(tmapId));
            payload.AddRange(BE32(baseId));

            var altr = MakeBox("altr", Concat(new byte[4], payload.ToArray()));
            return MakeBox("grpl", altr);   // grpl 本身不是 FullBox
        }

        /// <summary>取一个未被占用的 entity group id（扫描目标文件已有的 grpl）。</summary>
        private uint NextGroupId()
        {
            uint max = 0;
            var grpl = FindInMeta("grpl");
            if (grpl == null) return 1;

            foreach (var c in Children(grpl.Value))
            {
                long p = c.off + 8 + 4;         // 跳过 box 头 + version/flags
                if (p + 4 > c.off + c.size) continue;
                uint id = BinaryPrimitives.ReadUInt32BigEndian(_base.AsSpan((int)p, 4));
                if (id > max) max = id;
            }
            return max + 1;
        }

        /// <summary>构建 auxC 属性 box（FullBox + null 结尾的 aux_type 字符串）。</summary>
        private static byte[] MakeAuxC(string auxType)
        {
            var t = Encoding.ASCII.GetBytes(auxType);
            var ms = new MemoryStream();
            WriteU32(ms, (uint)(8 + 4 + t.Length + 1));
            ms.Write(Encoding.ASCII.GetBytes("auxC"));
            ms.Write(new byte[4]);      // version(1) + flags(3)
            ms.Write(t, 0, t.Length);
            ms.WriteByte(0);            // null 终止符
            return ms.ToArray();
        }

        // ---------------- 解析 ----------------

        private List<(uint Id, string Type, string Name, bool Hidden)> ParseIinf()
        {
            var list = new List<(uint, string, string, bool)>();
            var box = FindInMeta("iinf");
            if (box == null) return list;

            long q = box.Value.PayloadOffset;
            // iinf：ver+flags(4) + entry_count。v0 为 16 位，v1 为 32 位。
            byte iinfVer = _base[(int)q];
            int cnt;
            long p;
            if (iinfVer == 0)
            {
                cnt = BinaryPrimitives.ReadUInt16BigEndian(_base.AsSpan((int)q + 4, 2));
                p = q + 6;
            }
            else
            {
                cnt = (int)BinaryPrimitives.ReadUInt32BigEndian(_base.AsSpan((int)q + 4, 4));
                p = q + 8;
            }
            long end = box.Value.Offset + box.Value.Size;

            for (int i = 0; i < cnt && p + 21 <= end; i++)
            {
                int sz = (int)BinaryPrimitives.ReadUInt32BigEndian(_base.AsSpan((int)p, 4));
                if (sz < 21 || p + sz > end) break;

                byte infeVer = _base[(int)p + 8];
                uint id = BinaryPrimitives.ReadUInt16BigEndian(_base.AsSpan((int)p + 12, 2));
                bool hidden = (_base[(int)p + 11] & 1) != 0;

                string type, name;
                if (infeVer >= 2)
                {
                    type = Encoding.ASCII.GetString(_base, (int)p + 16, 4);
                    // ★ item_name 是 **null 结尾**字符串，不是「长度前缀」。
                    //   曾误把首字节当长度读，会把名字整段读错、写出乱码 item 名。
                    long ns = p + 20, ne = ns;
                    while (ne < p + sz && _base[(int)ne] != 0) ne++;
                    name = ne > ns
                        ? Encoding.UTF8.GetString(_base, (int)ns, (int)(ne - ns)) : "";
                }
                else
                {
                    // infe v0/v1 无 item_type 字段（v2 才引入），布局为
                    // item_ID(2) + protection(2) + item_name(null 结尾)
                    type = "";
                    long ns = p + 16, ne = ns;
                    while (ne < p + sz && _base[(int)ne] != 0) ne++;
                    name = ne > ns
                        ? Encoding.UTF8.GetString(_base, (int)ns, (int)(ne - ns)) : "";
                }

                list.Add((id, type, name, hidden));
                p += sz;
            }
            return list;
        }

        private Dictionary<uint, (uint baseOff, uint off, uint len)> ParseIlocOf()
        {
            var map = new Dictionary<uint, (uint, uint, uint)>();
            var box = FindInMeta("iloc");
            if (box == null) return map;

            long q = box.Value.PayloadOffset + 4 + 2;   // ★ 跳过 2 字节 sizes
            int cnt = BinaryPrimitives.ReadUInt16BigEndian(_base.AsSpan((int)q, 2));
            long p = q + 2, end = box.Value.Offset + box.Value.Size;

            for (int i = 0; i < cnt && p + 18 <= end; i++)
            {
                uint id = BinaryPrimitives.ReadUInt16BigEndian(_base.AsSpan((int)p, 2)); p += 2;
                p += 2;
                uint baseOff = BinaryPrimitives.ReadUInt32BigEndian(_base.AsSpan((int)p, 4)); p += 4;
                int ec = BinaryPrimitives.ReadUInt16BigEndian(_base.AsSpan((int)p, 2)); p += 2;
                if (ec <= 0) continue;
                if (p + 8 > end) break;
                uint off = BinaryPrimitives.ReadUInt32BigEndian(_base.AsSpan((int)p, 4)); p += 4;
                uint len = BinaryPrimitives.ReadUInt32BigEndian(_base.AsSpan((int)p, 4)); p += 4;
                map[id] = (baseOff, off, len);
            }
            return map;
        }

        private List<(uint, List<int>)> ParseIpma(long boxOff, long boxSize)
        {
            var list = new List<(uint, List<int>)>();
            long p = boxOff + 8;
            byte ver = _base[(int)p];
            // ★ 索引宽度由 **flags 的最低位**决定（0→7 位，1→15 位），
            //   与 version 无关。曾按 version 判断 —— 若遇到 version=0/flags=1
            //   的文件会整段读错。（实测苹果/索尼/本工具产物三者 flags 均为 0）
            bool wide = (_base[(int)p + 3] & 0x01) != 0;
            p += 4;
            uint cnt = ver < 1
                ? BinaryPrimitives.ReadUInt32BigEndian(_base.AsSpan((int)p, 4))
                : (uint)BinaryPrimitives.ReadUInt64BigEndian(_base.AsSpan((int)p, 8));
            p += ver < 1 ? 4 : 8;
            long end = boxOff + boxSize;

            for (uint i = 0; i < cnt && p + 3 < end; i++)
            {
                uint id = ver < 1
                    ? BinaryPrimitives.ReadUInt16BigEndian(_base.AsSpan((int)p, 2))
                    : BinaryPrimitives.ReadUInt32BigEndian(_base.AsSpan((int)p, 4));
                p += ver < 1 ? 2 : 4;
                int n = _base[(int)p++];
                var props = new List<int>();
                for (int k = 0; k < n; k++)
                {
                    if (wide)
                    {
                        if (p + 2 > end) break;
                        ushort v = BinaryPrimitives.ReadUInt16BigEndian(
                            _base.AsSpan((int)p, 2));
                        p += 2;
                        props.Add((v & 0x7FFF) | ((v & 0x8000) != 0 ? 0x10000 : 0));
                    }
                    else
                    {
                        if (p >= end) break;
                        byte b = _base[(int)p++];
                        // 保留 essential 标志（bit 16）
                        props.Add((b & 0x7F) | ((b & 0x80) != 0 ? 0x10000 : 0));
                    }
                }
                list.Add((id, props));
            }
            return list;
        }

        private uint ParsePrimaryId()
        {
            var pitm = FindInMeta("pitm");
            if (pitm == null) return 1;
            return BinaryPrimitives.ReadUInt16BigEndian(
                _base.AsSpan((int)(pitm.Value.PayloadOffset + 4), 2));
        }

        // ---------------- 工具 ----------------

        private IsoBmffReader.Box? FindBox(string type)
        {
            long o = 0;
            while (o + 8 <= _base.Length)
            {
                int sz = (int)BinaryPrimitives.ReadUInt32BigEndian(_base.AsSpan((int)o, 4));
                if (sz < 8) return null;
                string t = Encoding.ASCII.GetString(_base, (int)o + 4, 4);
                if (t == type)
                    return new IsoBmffReader.Box
                    { Type = t, Offset = o, Size = sz, HeaderSize = 8 };
                if (t == "mdat") return null;
                o += sz;
            }
            return null;
        }

        private IsoBmffReader.Box? FindInMeta(string type)
        {
            var meta = FindBox("meta");
            if (meta == null) return null;
            foreach (var c in Children(meta.Value))
                if (c.type == type)
                    return new IsoBmffReader.Box
                    { Type = c.type, Offset = c.off, Size = c.size, HeaderSize = 8 };
            return null;
        }

        private List<(string type, long off, long size)> Children(IsoBmffReader.Box parent)
        {
            var list = new List<(string, long, long)>();
            long p = parent.PayloadOffset;
            if (parent.Type == "meta") p += 4;
            long end = parent.Offset + parent.Size;
            while (p + 8 <= end)
            {
                int sz = (int)BinaryPrimitives.ReadUInt32BigEndian(_base.AsSpan((int)p, 4));
                if (sz < 8 || p + sz > end) break;
                list.Add((Encoding.ASCII.GetString(_base, (int)p + 4, 4), p, sz));
                p += sz;
            }
            return list;
        }

        private byte[]? ChildBytes(List<(string type, long off, long size)> children, string type)
        {
            var c = children.FirstOrDefault(x => x.type == type);
            if (c.type != type) return null;
            var buf = new byte[c.size];
            Array.Copy(_base, c.off, buf, 0, (int)c.size);
            return buf;
        }

        private byte[] SliceBox(IsoBmffReader.Box b)
        {
            var buf = new byte[b.Size];
            Array.Copy(_base, b.Offset, buf, 0, (int)b.Size);
            return buf;
        }

        private static byte[] MakeBox(string type, byte[] payload)
        {
            var ms = new MemoryStream();
            WriteU32(ms, (uint)(8 + payload.Length));
            ms.Write(Encoding.ASCII.GetBytes(type.PadRight(4, ' ').Substring(0, 4)));
            ms.Write(payload);
            return ms.ToArray();
        }

        private static byte[] Concat(params byte[][] parts)
        {
            var ms = new MemoryStream();
            foreach (var p in parts) if (p != null) ms.Write(p, 0, p.Length);
            return ms.ToArray();
        }

        private static byte[] BE16(ushort v) => new[] { (byte)(v >> 8), (byte)v };
        private static byte[] BE32(uint v) => new[]
            { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
        private static void WriteU32(Stream s, uint v) => s.Write(BE32(v));
        private static void WriteU32(byte[] b, int off, uint v)
        {
            b[off] = (byte)(v >> 24); b[off + 1] = (byte)(v >> 16);
            b[off + 2] = (byte)(v >> 8); b[off + 3] = (byte)v;
        }
    }
}