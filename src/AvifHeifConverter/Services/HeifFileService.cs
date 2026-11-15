using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AvifHeifConverter.Models;

namespace AvifHeifConverter.Services
{
    /// <summary>
    /// AVIF/HEIF 文件信息服务：读取并解析文件内部结构。
    ///
    /// Gain Map 判定依据（实测确立，勿再改）：
    ///   唯一可靠标志是 iinf 中存在 item_type = "tmap" 的 item。
    ///   **不要**用 auxC / auxl / 搜索 "tmap box" 来判断——ISO 21496-1
    ///   把 Gain Map 元数据存为 iinf 的一个 item，数据可放在 idat 里，
    ///   因此既没有独立 box，也没有 auxC/auxl 引用。
    /// </summary>
    public static class HeifFileService
    {
        private static readonly string[] SupportedExtensions =
            { ".avif", ".heic", ".heif", ".hif" };

        public static bool IsSupported(string path) =>
            SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

        public static string[] SupportedFilter => SupportedExtensions;

        /// <summary>异步读取并解析文件结构。</summary>
        public static Task<HeifFileInfo> AnalyzeAsync(
            string path, string? engineDir = null, CancellationToken ct = default)
            => Task.Run(() => Analyze(path, engineDir, ct), ct);

        public static HeifFileInfo Analyze(
            string path, string? engineDir = null, CancellationToken ct = default)
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) throw new FileNotFoundException("文件不存在", path);

            ct.ThrowIfCancellationRequested();

            // 大文件（全景可达数十 MB）一次性读入最简单可靠
            var bytes = File.ReadAllBytes(path);
            ct.ThrowIfCancellationRequested();

            var reader = new IsoBmffReader(bytes);

            var items = reader.ParseItems();
            var refs = reader.ParseReferences();
            var assoc = reader.ParsePropertyAssociations();
            var props = reader.ParsePropertyBoxes();

            uint primaryId = reader.ParsePrimaryItemId();

            IsoItem? primary = null;
            if (primaryId != 0)
                primary = items.FirstOrDefault(i => i.Id == primaryId) is var m && m.Id != 0
                    ? ToIsoItem(items.First(i => i.Id == primaryId))
                    : null;

            // ---- Gain Map 判定 ----
            // 1) 元数据 item：item_type == "tmap"
            var tmapEntry = items.FirstOrDefault(i => i.Type == "tmap");

            // 2) 图像本体 item：名称 "GMap" 且不是 tmap 本身
            var gmapEntry = items.FirstOrDefault(
                i => i.Name == "GMap" && i.Type != "tmap");

            IsoItem? meta = tmapEntry.Id != 0 || tmapEntry.Type == "tmap"
                ? ToIsoItem(tmapEntry) : null;

            // 若元数据 item 存在，通过 dimg 引用找到真正的增益图本体
            if (meta != null && gmapEntry.Id == 0 && gmapEntry.Type == null)
            {
                foreach (var (type, from, to) in refs)
                {
                    if (type == "dimg" && from == meta.Id)
                    {
                        foreach (var tid in to)
                        {
                            var cand = items.FirstOrDefault(i => i.Id == tid);
                            if (cand.Id != 0 && cand.Id != primaryId)
                            {
                                gmapEntry = cand;
                                break;
                            }
                        }
                        if (gmapEntry.Id != 0) break;
                    }
                }
            }

            IsoItem? gmap = gmapEntry.Id != 0 ? ToIsoItem(gmapEntry) : null;

            // ---- 属性读取 ----
            int width = 0, height = 0, bitDepth = 0;
            var color = new ColorInfo();
            ushort? maxCLL = null, maxPALL = null;

            // 主图属性
            if (assoc.TryGetValue(primaryId, out var propIdx) && propIdx.Count > 0)
            {
                foreach (var idx in propIdx)
                {
                    int real = idx & 0xFFFF;
                    if (real < 1 || real > props.Count) continue;
                    var box = props[real - 1];

                    switch (box.Type)
                    {
                        case "ispe":
                            var ispe = reader.ReadIspe(box);
                            if (ispe != null) { width = ispe.Value.Width; height = ispe.Value.Height; }
                            break;
                        case "colr":
                            var nclx = reader.ReadNclx(box);
                            if (nclx != null)
                                color = new ColorInfo(nclx.Value.Primaries, nclx.Value.Transfer,
                                                       nclx.Value.Matrix, nclx.Value.FullRange);
                            break;
                        case "pixi":
                            var pixi = reader.ReadPixi(box);
                            if (pixi != null && pixi.Value.Depths.Length > 0)
                                bitDepth = pixi.Value.Depths.Max();
                            break;
                        case "clli":
                            var clli = reader.ReadClliData(box);
                            if (clli != null) { maxCLL = clli.Value.MaxCLL; maxPALL = clli.Value.MaxPALL; }
                            break;
                    }
                }
            }

            bool isGrid = reader.IsGridItem(primaryId, refs);

            // 尺寸/位深/色彩以 libheif 官方工具 heif-info 为准。
            // 原因：自研 ipma 解析在苹果 HEIC 上读出的属性索引不可靠
            //（苹果写flags=0 但实际用 15 位索引，与文档不符）；
            // 而 Gain Map 判定仍由自研解析器负责，因为 libheif
            // 完全不暴露 item_type="tmap"。
            if (engineDir != null)
            {
                var q = HeifInfoTool.Query(engineDir, bytes);
                if (q != null)
                {
                    width = q.Width;
                    height = q.Height;
                    bitDepth = q.BitDepth;

                    // heif-info 的 "color profile:" 字段给出类型：
                    //   nclx  = 数值型（primaries/transfer/matrix 有效）
                    //   prof  = ICC 配置文件（苹果设备常见，数值列为 0 属正常）
                    color = BuildColor(q);
                    maxCLL = q.MaxCLL;
                    maxPALL = q.MaxPALL;
                }
            }

            return new HeifFileInfo
            {
                FilePath = path,
                FileSize = fi.Length,
                PrimaryItem = primary ?? new IsoItem { Id = primaryId, Kind = ItemKind.Unknown },
                Width = width,
                Height = height,
                BitDepth = bitDepth,
                Color = color,
                GainMapMetaItem = meta,
                GainMapImageItem = gmap,
                MaxCLL = maxCLL,
                MaxPALL = maxPALL,
                IsGrid = isGrid,
            };
        }

        /// <summary>把 heif-info 的输出转成ColorInfo，含色彩描述类型判定。</summary>
        private static ColorInfo BuildColor(HeifInfoTool.Result q)
        {
            var kind = q.ProfileKindRaw switch
            {
                "nclx" => ColorInfo.ColorProfileKind.Nclx,
                "prof" => ColorInfo.ColorProfileKind.IccProfile,
                "ienc" => ColorInfo.ColorProfileKind.Cie,
                _ => ColorInfo.ColorProfileKind.None,
            };

            // 苹果用 ICC 时 primaries/transfer/matrix 均为 0，
            // 此时保留 0 值并标记 ProfileKind，由界面正确呈现。
            return new ColorInfo(q.Primaries, q.Transfer, q.Matrix, q.FullRange)
            {
                ProfileKind = kind,
            };
        }

        private static IsoItem ToIsoItem((uint Id, string Type, string Name, bool Hidden) e) => new()
        {
            Id = e.Id,
            Kind = MapKind(e.Type),
            Name = e.Name,
            Hidden = e.Hidden,
        };

        public static ItemKind MapKind(string? fourCc) => fourCc switch
        {
            "av01" => ItemKind.Av01,
            "grid" => ItemKind.Grid,
            "hvc1" or "hev1" => ItemKind.Hvc1,
            "jpeg" or "jpg " => ItemKind.Jpeg,
            "Exif" => ItemKind.Exif,
            "mime" => ItemKind.Mime,
            "tmap" => ItemKind.Tmap,
            _ => ItemKind.Unknown,
        };
    }
}