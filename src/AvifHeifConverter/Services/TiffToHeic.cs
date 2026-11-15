using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AvifHeifConverter.Services
{
    /// <summary>
    /// TIFF → HEIC 转换（SDR 路径）。
    ///
    /// 与 AVIF 通路的关系：
    ///   · AVIF 源带增益图，走「直通」或「PQ→SDR + 增益图」
    ///   · TIFF 源**不带增益图**，且色彩靠 ICC 描述 → 单独一条路径
    ///   下游的分块编码（TiledHeicBuilder）两条路径共用。
    ///
    /// 色彩从 ICC 读出（原色 + 传输曲线），**不猜**：
    ///   传输曲线为 sRGB/BT.709/线性 → 本路径
    ///   若为 PQ/HLG（HDR TIFF）→ 当前明确拒绝，而不是硬编成错的
    /// </summary>
    internal static class TiffToHeic
    {
        public sealed class Options
        {
            public string OutputPath = "";
            public int Quality = 92;
            public string Chroma = "444";
            public string Preset = "";
            public bool Overwrite;
        }

        /// <summary>返回 (成功, 错误说明, 是否因同名跳过)。</summary>
        public static async Task<(bool Ok, string Error, bool Skipped)> RunAsync(
            string tiffPath, Options opt, string engineDir,
            Action<string>? log = null, CancellationToken ct = default)
        {
            void L(string s) => log?.Invoke(s);

            var read = TiffReader.Read(tiffPath);
            if (read == null) return (false, "TIFF 读取失败（格式不受支持或文件损坏）", false);
            var (px, info) = read.Value;
            if (info.Width <= 0 || info.Height <= 0 || info.Samples < 1)
                return (false, "TIFF 尺寸或通道数异常", false);

            var icc = info.IccProfile != null ? IccColor.FromIcc(info.IccProfile, L) : null;
            if (icc == null) return (false, "TIFF 不含 ICC 配置，无法确定色彩（已中止，不猜）", false);

            if (icc.Transfer is 16 or 18)
                return (false, $"检测到 HDR 传输曲线（nclx transfer={icc.Transfer}），HDR 通路尚未接入", false);
            if (!icc.PrimariesIdentified || !icc.TransferIdentified)
                return (false, "ICC 的原色或传输曲线未能识别（已中止，不猜）", false);

            if (!string.IsNullOrWhiteSpace(opt.OutputPath) && File.Exists(opt.OutputPath) && !opt.Overwrite)
                return (false, "已跳过：同名文件已存在（勾选「覆盖同名文件」可覆盖）", true);

            // 16bit → 8bit（右移 8 位）
            int S = info.Samples;
            long total = (long)info.Width * info.Height * 3;
            if (total > int.MaxValue) return (false, "图像过大，超出 32 位索引上限", false);
            var rgb = new byte[total];
            for (int y = 0; y < info.Height; y++)
            {
                if ((y & 0x3F) == 0) ct.ThrowIfCancellationRequested();
                long rs = (long)y * info.Width * S, rd = (long)y * info.Width * 3;
                for (int x = 0; x < info.Width; x++)
                {
                    long s0 = rs + (long)x * S, d0 = rd + (long)x * 3;
                    rgb[d0] = (byte)(px[s0] >> 8);
                    rgb[d0 + 1] = (byte)(px[s0 + Math.Min(1, S - 1)] >> 8);
                    rgb[d0 + 2] = (byte)(px[s0 + Math.Min(2, S - 1)] >> 8);
                }
            }

            string tmp = Path.Combine(Path.GetTempPath(), "tifheic_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                var tr = await TiledHeicBuilder.BuildAsync(
                    rgb, info.Width, info.Height, Path.Combine(tmp, "out.heic"), engineDir, tmp,
                    opt.Quality, L, ct, icc.Primaries, 0, 0, opt.Chroma, opt.Preset).ConfigureAwait(false);
                if (!tr.Ok) return (false, "分块编码失败：" + tr.Error, false);

                string outDir = Path.GetDirectoryName(Path.GetFullPath(opt.OutputPath)) ?? ".";
                Directory.CreateDirectory(outDir);
                File.Move(Path.Combine(tmp, "out.heic"), opt.OutputPath, true);
                L($"✓ 完成：{opt.OutputPath}（{new FileInfo(opt.OutputPath).Length / 1048576.0:0.##} MB）");
                return (true, "", false);
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }
    }
}
