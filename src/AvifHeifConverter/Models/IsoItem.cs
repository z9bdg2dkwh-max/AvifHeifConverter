using System;
using System.Collections.Generic;

namespace AvifHeifConverter.Models
{
    /// <summary>
    /// ISO/IEC 23008-12（HEIF/AVIF 容器）中一个 item 的类型。
    /// 依据 ISO 23008-12 §6.5 与 ISO 21496-1（gain map 元数据）。
    /// </summary>
    public enum ItemKind
    {
        Unknown = 0,
        /// <summary>主图 / 派生图图像数据（AV1 编码）。</summary>
        Av01,
        /// <summary>分块网格图像（由 dimg 引用多个 tile 组成）。</summary>
        Grid,
        /// <summary>HEVC 编码图像（已是 HEIC，无需转码）。</summary>
        Hvc1,
        /// <summary>JPEG 编码图像。</summary>
        Jpeg,
        /// <summary>EXIF 元数据。</summary>
        Exif,
        /// <summary>XMP 元数据（mime 类型）。</summary>
        Mime,
        /// <summary>
        /// Gain Map 元数据（ISO 21496-1）。
        /// 这是判定"文件是否含 Gain Map"的唯一可靠标志——
        /// 它是 iinf 中的一个 item，而不是独立 box。
        /// </summary>
        Tmap,
    }

    /// <summary>一个 HEIF/AVIF item（iinf 中的 infe 条目）。</summary>
    public sealed class IsoItem
    {
        public uint Id { get; init; }
        public ItemKind Kind { get; init; }
        /// <summary>item_name，可为空。例如主图为 "Color"，增益图为 "GMap"。</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>是否被标记为 hidden item。增益图本体通常为 true。</summary>
        public bool Hidden { get; init; }

        public override string ToString() =>
            $"item {Id} · {Kind} · name='{Name}'{(Hidden ? " · hidden" : "")}";
    }

    /// <summary>图片的 nclx 色彩描述（ISO 23001-8 CICP）。</summary>
    public sealed class ColorInfo
    {
        public ushort Primaries { get; init; }        // colour_primaries
        public ushort Transfer { get; init; }         // transfer_characteristics
        public ushort Matrix { get; init; }           // matrix_coefficients
        public bool FullRange { get; init; }          // full_range_flag

        public ColorInfo() { }

        public ColorInfo(ushort p, ushort t, ushort m, bool fr)
        {
            Primaries = p; Transfer = t; Matrix = m; FullRange = fr;
        }

        public ColorInfo Clone() => new(Primaries, Transfer, Matrix, FullRange);

        /// <summary>
        /// 色彩描述类型。
        /// </summary>
        public enum ColorProfileKind
        {
            /// <summary>无色度描述。</summary>
            None = 0,
            /// <summary>nclx（CICP 数值型，索尼/安卓常用）。</summary>
            Nclx,
            /// <summary>ICC 配置文件（苹果设备常用，heif-info 显示 prof）。</summary>
            IccProfile,
            /// <summary>CIE 元数据。</summary>
            Cie,
        }

        /// <summary>色彩描述类型。</summary>
        public ColorProfileKind ProfileKind { get; init; } = ColorProfileKind.None;

        /// <summary>ICC 配置文件字节数（仅当 ProfileKind == IccProfile 时有效）。</summary>
        public int IccBytes { get; init; }

        /// <summary>transfer_characteristics 13(sRGB) / 16(PQ) / 18(HLG) 属 HDR。</summary>
        public bool IsHdr => Transfer is 16 or 18;

        /// <summary>
        /// 是否有可读的数值色彩描述。
        /// 苹果设备多用 ICC，此时 primaries/transfer/matrix 均为 0，属正常现象。
        /// </summary>
        public bool HasNumericProfile =>
            ProfileKind == ColorProfileKind.Nclx &&
            (Primaries != 0 || Transfer != 0 || Matrix != 0);

        public override string ToString()
        {
            if (ProfileKind == ColorProfileKind.IccProfile)
                return IccBytes > 0
                    ? $"ICC 配置文件（{IccBytes / 1024.0:0.#} KB）"
                    : "ICC 配置文件";
            if (ProfileKind == ColorProfileKind.Cie)
                return "CIE 色度描述";
            if (ProfileKind == ColorProfileKind.None)
                return "无色度描述";
            return $"nclx: primaries={Primaries}, transfer={Transfer}, " +
                   $"matrix={Matrix}, fullRange={FullRange}";
        }
    }

    /// <summary>文件级解析结果。</summary>
    public sealed class HeifFileInfo
    {
        public string FilePath { get; init; } = string.Empty;
        public long FileSize { get; init; }

        /// <summary>主图 item（pitm 指向）。</summary>
        public IsoItem PrimaryItem { get; init; } = new() { Id = 0, Kind = ItemKind.Unknown };

        public int Width { get; init; }
        public int Height { get; init; }
        public int BitDepth { get; init; }
        public ColorInfo Color { get; init; } = new();

        /// <summary>Gain Map 元数据 item（item_type = tmap），无则为 null。</summary>
        public IsoItem? GainMapMetaItem { get; init; }

        /// <summary>Gain Map 图像本体 item（名称 "GMap" 的 grid/av01），无则为 null。</summary>
        public IsoItem? GainMapImageItem { get; init; }

        public ushort? MaxCLL { get; init; }
        public ushort? MaxPALL { get; init; }

        public int ExifBytes { get; init; }
        public int XmpBytes { get; init; }

        /// <summary>是否为网格分块图像。</summary>
        public bool IsGrid { get; init; }

        /// <summary>是否真正含 Gain Map。</summary>
        public bool HasGainMap => GainMapMetaItem != null;
    }
}