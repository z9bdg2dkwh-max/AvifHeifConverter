using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AvifHeifConverter.Services
{
    /// <summary>
    /// Gain Map 容器重组：把源文件里的 Gain Map 相关 item 原样搬到新文件。
    ///
    /// 为什么必须自己做：
    ///   heif-enc / libheif 都不支持 ISO 21496-1，转换时 Gain Map 必然丢失。
    ///   但 Gain Map 在容器里是**独立 item**，数据本身可以是 AV1 或 HEVC。
    ///   做法：解码主图 → 重编码 HEVC → 把 Gain Map 的 item 字节原样写回。
    ///   字节级复制 = 数学无损，比重新编码可靠。
    ///
    /// 需要搬运的 item 闭包（实测索尼与苹果结构一致）：
    ///   1) tmap 元数据 item（item_type="tmap"，数据通常在 idat 内）
    ///   2) 增益图本体 item（grid/av01，名称 GMap，通常 hidden）
    ///   3) 增益图 grid 的全部 tile item（通过 dimg 引用）
    ///
    /// 数据位置来源：HeifInfoLocator（解析 heif-info -d 输出）。
    /// 不用手写 iloc 二进制解析——不同厂商字段宽度与 ISO 文档不一致，
    /// 实测反复解析失败，而 heif-info 输出已交叉验证可靠。
    /// </summary>
    internal sealed class GainMapExtractor
    {
        private readonly IsoBmffReader _r;
        private readonly byte[] _src;
        private readonly Dictionary<uint, HeifInfoLocator.ItemLocation>? _locator;
        private readonly HeifInfoProperties.Associations? _props;

        public GainMapExtractor(byte[] src, string engineDir)
        {
            _src = src;
            _r = new IsoBmffReader(src);

            // heif-info -d 一次调用同时提供两样东西：
            //   iloc 段 -> item 数据位置
            //   ipma 段 -> item 属性关联
            // 避免重复启动子进程，也避免用静态字段传递中间结果。
            var dump = HeifInfoLocator.Dump(engineDir, src);
            if (dump != null)
            {
                _locator = HeifInfoLocator.ParseIloc(dump);
                _props = HeifInfoProperties.Parse(dump);
            }
        }

        /// <summary>一个待搬运的 item，含其原始数据与依赖。</summary>
        public sealed class ExtractedItem
        {
            public uint Id { get; init; }
            public string Type { get; init; } = "";
            public string Name { get; init; } = "";
            public bool Hidden { get; init; }
            /// <summary>原始 item 数据字节。</summary>
            public byte[] Data { get; init; } = Array.Empty<byte>();
            /// <summary>true 表示数据原本在 idat 内。</summary>
            public bool WasInIdat { get; init; }
            /// <summary>该 item 的属性索引（来自 ipma）。</summary>
            public List<int> PropertyIndices { get; init; } = new();

            /// <summary>
            /// mime 类 item 的 content_type（如 application/rdf+xml）。
            /// ★ ISO 14496-12：infe v2 里 item_type='mime' 时必须紧跟 item_name 之后
            ///   再写一个以 null 结尾的 content_type，否则读取器无法识别该 item。
            /// </summary>
            public string? ContentType { get; init; }

            /// <summary>
            /// 该 item 的 cdsc 引用应指向**主图**（而非增益图）。
            /// 用于 Exif / 原图 XMP 这类本该挂在主图上的元数据 —— 否则合并时
            /// 它们会被当成增益图的附件，甚至整条丢失。
            /// </summary>
            public bool LinkToPrimary { get; init; }
        }

        public sealed class Result
        {
            public bool HasGainMap { get; init; }
            /// <summary>提取是否完整（数据位置全部拿到）。</summary>
            public bool Complete { get; set; }
            public uint MetaItemId { get; init; }
            /// <summary>增益图本体 item 的 id。</summary>
            public uint ImageItemId { get; init; }
            /// <summary>增益图 grid 的 tile item id 列表。</summary>
            public List<uint> TileIds { get; init; } = new();
            /// <summary>需要搬运的全部 item。</summary>
            public List<ExtractedItem> Items { get; init; } = new();
            /// <summary>需要复制的 ipco 属性 box。</summary>
            public List<IsoBmffReader.Box> NeededProperties { get; init; } = new();

            /// <summary>布局类型描述（用于日志呈现）。</summary>
            public string LayoutKind { get; set; } = "ISO tmap";
        }

        public Result Extract()
        {
            var items = _r.ParseItems();
            var refs = _r.ParseReferences();
            var assoc = _r.ParsePropertyAssociations();
            var _propsUnused = _r.ParsePropertyBoxes();

            var tmap = items.FirstOrDefault(i => i.Type == "tmap");
            if (tmap.Type != "tmap") return new Result { HasGainMap = false };
            bool isTmapLayout = true;

            uint metaId = tmap.Id;
            uint primaryId = _r.ParsePrimaryItemId();

            // tmap 的 dimg 引用：[主图, 增益图]；增益图是既非 tmap 也非主图的那个
            uint imageId = 0;
            foreach (var (type, from, to) in refs)
            {
                if (type != "dimg" || from != metaId) continue;
                foreach (var tid in to)
                {
                    if (tid != metaId && tid != primaryId) { imageId = tid; break; }
                }
                if (imageId != 0) break;
            }

            var tiles = new List<uint>();
            if (imageId != 0)
            {
                foreach (var (type, from, to) in refs)
                {
                    if (type == "dimg" && from == imageId) { tiles.AddRange(to); break; }
                }
            }

            var need = new HashSet<uint>();
            need.Add(metaId);
            if (imageId != 0) need.Add(imageId);
            foreach (var t in tiles) need.Add(t);

            var result = new Result
            {
                HasGainMap = true,
                MetaItemId = metaId,
                ImageItemId = imageId,
                TileIds = tiles,
                LayoutKind = isTmapLayout ? "ISO tmap" : "Apple auxl",
            };

            var assocFromLib = _props?.ItemToProps;
            var ipcoBoxes = _r.ParsePropertyBoxes();

            int missing = 0;

            foreach (var id in need)
            {
                var meta = items.FirstOrDefault(i => i.Id == id);
                bool known = items.Any(i => i.Id == id);
                if (!known) { missing++; continue; }

                var (data, inIdat) = ReadItemData(id);
                if (data == null || data.Length == 0) { missing++; continue; }

                // 属性索引：优先用 heif-info 的关联表（已验证可靠），
                // 回退到自研 ipma 解析。
                List<int> props4Item;
                if (assocFromLib != null && assocFromLib.TryGetValue(id, out var libProps))
                    props4Item = new List<int>(libProps);
                else if (assoc.TryGetValue(id, out var pv))
                    props4Item = new List<int>(pv);
                else
                    props4Item = new List<int>();

                result.Items.Add(new ExtractedItem
                {
                    Id = id,
                    Type = meta.Type ?? "",
                    Name = meta.Name ?? "",
                    Hidden = meta.Hidden,
                    Data = data,
                    WasInIdat = inIdat,
                    PropertyIndices = props4Item,
                });

                foreach (var idx in props4Item)
                {
                    int real = idx & 0xFFFF;
                    if (real >= 1 && real <= ipcoBoxes.Count)
                        result.NeededProperties.Add(ipcoBoxes[real - 1]);
                }
            }

            result.Complete = missing == 0 && result.Items.Count == need.Count;
            return result;
        }

        /// <summary>
        /// 提取挂在**主图**上的元数据 item（Exif / XMP）。
        ///
        /// 为什么需要：新管线的 heif-enc 输入是自算的 PNG，PNG 里没有 Exif/XMP，
        /// 直接转出来的 HEIC 会把源文件的全部元数据丢光（实测 Exif 880B + XMP 6467B 全丢）。
        /// 故必须从源文件把这两类 item 一并搬过去，并用 cdsc 挂回主图。
        /// </summary>
        public List<ExtractedItem> ExtractMetadataItems()
        {
            var items = _r.ParseItems();
            var refs = _r.ParseReferences();
            uint primary = _r.ParsePrimaryItemId();
            var contentTypes = _r.ParseContentTypes();
            var ipcoBoxes = _r.ParsePropertyBoxes();
            var assocFromLib = _props?.ItemToProps;
            var assocSelf = _r.ParsePropertyAssociations();

            // 通过 cdsc 关联到主图的 item
            var ids = new List<uint>();
            foreach (var (type, from, to) in refs)
                if (type == "cdsc" && to.Contains(primary) && from != primary)
                    ids.Add(from);

            var res = new List<ExtractedItem>();
            foreach (var id in ids)
            {
                var meta = items.FirstOrDefault(i => i.Id == id);
                if (meta.Id != id) continue;
                if (meta.Type != "Exif" && meta.Type.Trim() != "mime") continue;

                var (data, inIdat) = ReadItemData(id);
                if (data == null || data.Length == 0) continue;

                List<int> props4Item;
                if (assocFromLib != null && assocFromLib.TryGetValue(id, out var libProps))
                    props4Item = new List<int>(libProps);
                else if (assocSelf.TryGetValue(id, out var pv))
                    props4Item = new List<int>(pv);
                else
                    props4Item = new List<int>();

                res.Add(new ExtractedItem
                {
                    Id = id,
                    Type = meta.Type.Trim(),
                    Name = meta.Name ?? "",
                    Hidden = true,
                    Data = data,
                    WasInIdat = inIdat,
                    PropertyIndices = props4Item,
                    ContentType = meta.Type.Trim() == "mime"
                        ? (contentTypes.TryGetValue(id, out var ct) ? ct : "application/rdf+xml")
                        : null,
                    LinkToPrimary = true,
                });
            }
            return res;
        }

        /// <summary>
        /// 按 heif-info 给出的位置读取 item 数据。
        /// method 0 = mdat（偏移为文件绝对位置）
        /// method 1 = idat（偏移相对 idat payload 起点）
        /// </summary>
        private (byte[]? Data, bool InIdat) ReadItemData(uint itemId)
        {
            if (_locator == null || !_locator.TryGetValue(itemId, out var loc))
                return (null, false);

            var idatBox = _r.FindInMeta("idat");
            long idatBase = idatBox?.PayloadOffset ?? 0;

            var parts = new List<byte>((int)loc.TotalLength);
            foreach (var (off, len) in loc.Extents)
            {
                if (len <= 0) continue;

                long start = loc.Method == 1
                    ? idatBase + loc.BaseOffset + off
                    : loc.BaseOffset + off;

                // ★ 越界必须显式失败：静默 continue 会「成功」返回一个被截断的 item，
                //   合并进新文件后表现为解码器报错，而调用方完全看不到原因。
                if (start < 0 || start + len > _src.Length)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[gm] item {itemId} extent 越界：start={start} len={len} file={_src.Length}");
                    return (null, false);
                }

                var buf = new byte[len];
                Array.Copy(_src, start, buf, 0, (int)len);
                parts.AddRange(buf);
            }

            return parts.Count > 0 ? (parts.ToArray(), loc.Method == 1) : (null, false);
        }
    }
}