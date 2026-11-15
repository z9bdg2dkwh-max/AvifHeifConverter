using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace AvifHeifConverter.Services
{
    /// <summary>
    /// 方案 A：只改 ftyp 的品牌声明，不动任何图像数据。
    ///
    /// 原理：AVIF 与 HEIF 是同一容器（ISOBMFF），差别主要是 ftyp 里声明的 brand。
    /// 把 major_brand 从 'avif' 改成 'heic'，并把 'avif' 从兼容品牌列表移除 /
    /// 换成 'mif1'（HEIF 通用品牌），让系统按 HEIF 解析。
    ///
    /// 优势：
    ///   - 图像数据一个字节都不动→ **不可能损坏文件**
    ///   - Gain Map、Exif、XMP、色彩描述全部原样保留（它们本来就在文件里）
    ///   - 不存在偏移重排问题
    ///
    /// 风险：HEIC 品牌通常意味着 HEVC 编码，而文件内是 AV1。
    /// 部分系统可能校验"品牌与实际编码是否匹配"而拒绝。
    /// 这需要实机验证。
    /// </summary>
    public static class BrandPatcher
    {
        public sealed class Result
        {
            public bool Success { get; init; }
            public string? Error { get; init; }
            public string OutPath { get; init; } = "";
            public string OldBrand { get; init; } = "";
            public string NewBrand { get; init; } = "";
            public int SizeDelta { get; init; }
        }

        /// <summary>
        /// 把 AVIF 的品牌改写为 HEIC。数据部分完全不动。
        /// </summary>
        public static Result PatchAvifToHeic(string srcPath, string dstPath,
                                           bool removeAvifCompatible = true)
        {
            try
            {
                var data = File.ReadAllBytes(srcPath);

                // ftyp 必须存在，且总长度可容纳 brand 改写
                if (data.Length < 12 ||
                    data[4] != 'f' || data[5] != 't' || data[6] != 'y' || data[7] != 'p')
                {
                    return new Result
                    {
                        Success = false, Error = "文件缺少 ftyp box，无法改写品牌",
                        OutPath = dstPath
                    };
                }

                int ftypSize = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4));
                string oldMajor = Encoding.ASCII.GetString(data, 8, 4);

                // major_brand: avif → heic
                Encoding.ASCII.GetBytes("heic").CopyTo(data, 8);

                // 兼容品牌列表：从第 16 字节到 ftyp 结束
                int compatStart = 16;
                int compatEnd = ftypSize;
                int compatCount = (compatEnd - compatStart) / 4;

                // 在兼容品牌里把 'avif' 换成 'mif1'（HEIF 通用品牌）
                int replaced = 0;
                for (int i = 0; i < compatCount; i++)
                {
                    int off = compatStart + i * 4;
                    string b = Encoding.ASCII.GetString(data, off, 4);
                    if (b == "avif")
                    {
                        Encoding.ASCII.GetBytes("mif1").CopyTo(data, off);
                        replaced++;
                        if (removeAvifCompatible) break;
                    }
                }

                File.WriteAllBytes(dstPath, data);

                return new Result
                {
                    Success = true,
                    OutPath = dstPath,
                    OldBrand = oldMajor,
                    NewBrand = "heic",
                    SizeDelta = 0,
                };
            }
            catch (Exception ex)
            {
                return new Result
                {
                    Success = false, Error = ex.Message, OutPath = dstPath
                };
            }
        }
    }
}