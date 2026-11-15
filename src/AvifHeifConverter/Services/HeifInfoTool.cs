using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AvifHeifConverter.Services
{
    /// <summary>
    /// 通过调用 libheif 自带的 heif-info 读取图片属性。
    ///
    /// 为什么用子进程而不是 P/Invoke：
    ///   直调 libheif.dll 的 C API 曾反复出问题——get_primary_image_handle
    ///   的out 参数在 .NET 侧触发 0xC0000005 访问违规，
    ///   read_from_memory_without_copy 又必须配 reader 对象。
    ///   heif-info 是 libheif 官方工具，其输出已在多个真实文件上交叉验证
    ///   （尺寸/位深/色彩/Gain Map item 均正确），故用它作为属性来源。
    ///
    /// 注：Gain Map 判定**不用**本类——item_type="tmap" libheif 的
    ///   常规输出不展示，由自研 IsoBmffReader 负责（已验证正确）。
    /// </summary>
    internal static class HeifInfoTool
    {
        private static readonly Regex ReSize =
            new(@"image:\s*(\d+)\s*x\s*(\d+)", RegexOptions.Compiled);

        private static readonly Regex ReBitDepth =
            new(@"bit depth:\s*(\d+)", RegexOptions.Compiled);

        private static readonly Regex RePrimaries =
            new(@"colour primaries:\s*(\d+)", RegexOptions.Compiled);

        private static readonly Regex ReTransfer =
            new(@"transfer characteristics:\s*(\d+)", RegexOptions.Compiled);

        private static readonly Regex ReMatrix =
            new(@"matrix coefficients:\s*(\d+)", RegexOptions.Compiled);

        private static readonly Regex ReFullRange =
            new(@"full range flag:\s*(\d+)", RegexOptions.Compiled);

        private static readonly Regex ReMaxCLL =
            new(@"MaxCLL=(\d+)", RegexOptions.Compiled);

        private static readonly Regex ReMaxPALL =
            new(@"MaxFALL=(\d+)", RegexOptions.Compiled);

        private static readonly Regex ReMime =
            new(@"MIME type:\s*(\S+)", RegexOptions.Compiled);

        private static readonly Regex ReProfileKind =
            new(@"color profile:\s*(\w+)", RegexOptions.Compiled);

        public sealed class Result
        {
            public int Width { get; init; }
            public int Height { get; init; }
            public int BitDepth { get; init; }
            public ushort Primaries { get; init; }
            public ushort Transfer { get; init; }
            public ushort Matrix { get; init; }
            public bool FullRange { get; init; }
            public ushort? MaxCLL { get; init; }
            public ushort? MaxPALL { get; init; }
            public string MimeType { get; init; } = "";
            /// <summary>nclx / prof / ienc / 空。</summary>
            public string ProfileKindRaw { get; init; } = "";
            public bool HasColorInfo { get; init; }
        }

        /// <summary>读取并解析 heif-info 输出。失败返回 null。</summary>
        public static Result? Query(string engineDir, byte[] fileBytes, int timeoutMs = 120_000)
        {
            var exe = Path.Combine(engineDir, "heif-info.exe");
            if (!File.Exists(exe)) return null;

            // heif-info 走 ANSI fopen，中文路径会失败 -> 复制到 ASCII 临时路径
            string tmp = Path.Combine(Path.GetTempPath(),
                        "heifinfo_" + Guid.NewGuid().ToString("N") + ".avif");
            try
            {
                File.WriteAllBytes(tmp, fileBytes);

                var psi = new ProcessStartInfo(exe)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                psi.ArgumentList.Add(tmp);

                var existing = psi.Environment.TryGetValue("PATH", out var old) ? old : "";
                psi.Environment["PATH"] = engineDir + ";" + existing;

                using var proc = new Process { StartInfo = psi };
                proc.Start();

                var stdout = proc.StandardOutput.ReadToEnd();
                var stderr = proc.StandardError.ReadToEnd();

                if (!proc.WaitForExit(timeoutMs))
                {
                    try { proc.Kill(true); } catch { }
                    return null;
                }

                // heif-info 找不到时输出到 stderr
                var text = stdout.Length > 0 ? stdout : stderr;
                return Parse(text);
            }
            catch
            {
                return null;
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        public static Result? Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            var mSize = ReSize.Match(text);
            if (!mSize.Success) return null;

            int w = int.Parse(mSize.Groups[1].Value);
            int h = int.Parse(mSize.Groups[2].Value);

            int bpp = M(ReBitDepth, text);
            ushort prim = (ushort)M(RePrimaries, text);
            ushort tr = (ushort)M(ReTransfer, text);
            ushort mat = (ushort)M(ReMatrix, text);
            bool fr = M(ReFullRange, text) == 1;

            var mCll = ReMaxCLL.Match(text);
            ushort? cll = mCll.Success ? (ushort?)(ushort.Parse(mCll.Groups[1].Value)) : null;

            var mFall = ReMaxPALL.Match(text);
            ushort? pall = mFall.Success ? (ushort?)(ushort.Parse(mFall.Groups[1].Value)) : null;

            var mMime = ReMime.Match(text);
            var mProfile = ReProfileKind.Match(text);

            return new Result
            {
                Width = w,
                Height = h,
                BitDepth = bpp,
                Primaries = prim,
                Transfer = tr,
                Matrix = mat,
                FullRange = fr,
                MaxCLL = cll,
                MaxPALL = pall,
                MimeType = mMime.Success ? mMime.Groups[1].Value : "",
                ProfileKindRaw = mProfile.Success ? mProfile.Groups[1].Value : "",
                HasColorInfo = prim != 0 || tr != 0 || mat != 0,
            };
        }

        private static int M(Regex re, string text)
        {
            var m = re.Match(text);
            return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : 0;
        }
    }
}