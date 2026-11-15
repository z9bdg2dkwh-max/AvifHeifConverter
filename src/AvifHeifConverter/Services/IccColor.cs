using System;
using System.Buffers.Binary;
using System.Text;

namespace AvifHeifConverter.Services
{
    /// <summary>
    /// 把 ICC 配置文件里的色彩信息，翻译成 HEIF/HEVC 的 nclx 三元组
    /// （primaries / transfer / matrix）。
    ///
    /// 为什么必须做这一步：TIFF 本身**不写** nclx，它把色彩都放在 ICC 里。
    /// 而 HEIC 的 colr 需要 nclx。不做翻译，就只能靠猜 —— 猜错会让整张图偏色。
    ///
    /// 判定方式：从 ICC 的 rXYZ/gXYZ/bXYZ 推出色度坐标，与已知色域比对；
    /// 从 rTRC/gTRC/bTRC 的 para/curv 参数判断传输曲线。
    /// **不接近任何已知色域时会明确返回"未知"，由调用方决定是否继续。**
    /// </summary>
    internal static class IccColor
    {
        public sealed class Result
        {
            public ushort Primaries = 2;      // nclx colour_primaries
            public ushort Transfer = 13;      // nclx transfer_characteristics
            public ushort Matrix = 6;         // nclx matrix_coefficients
            public bool PrimariesIdentified;
            public bool TransferIdentified;
            public string Detail = "";
        }

        /// <summary>由色度坐标 (x,y) 判定 nclx primaries。容差 0.006。</summary>
        private static (int Code, string Name)? MatchPrimaries(double rx, double ry, double gx, double gy, double bx, double by)
        {
            // 已知色域的三原色色度坐标（D65 白点）
            var sets = new (int Code, string Name, double rx, double ry, double gx, double gy, double bx, double by)[]
            {
                (1,  "BT.709 / sRGB",   0.640, 0.330, 0.300, 0.600, 0.150, 0.060),
                (12, "Display P3",      0.680, 0.320, 0.265, 0.690, 0.150, 0.060),
                (9,  "BT.2020",         0.708, 0.292, 0.170, 0.797, 0.131, 0.046),
                (11, "DCI-P3",          0.680, 0.320, 0.265, 0.690, 0.150, 0.060),
                (5,  "BT.470BG (PAL)",  0.640, 0.330, 0.290, 0.600, 0.150, 0.060),
            };
            double best = double.MaxValue; (int, string)? hit = null;
            foreach (var s in sets)
            {
                double d = Math.Abs(rx - s.rx) + Math.Abs(ry - s.ry)
                         + Math.Abs(gx - s.gx) + Math.Abs(gy - s.gy)
                         + Math.Abs(bx - s.bx) + Math.Abs(by - s.by);
                if (d < best) { best = d; hit = (s.Code, s.Name); }
            }
            // 容差放宽到 0.02/坐标 —— ICC 的 XYZ 是 D50 适配值，与 D65 标称值本就有偏移
            return best <= 0.02 * 6 ? hit : null;
        }

        private static (double x, double y) XyzToXy(int X, int Y, int Z)
        {
            double s = X + Y + Z;
            return s == 0 ? (0, 0) : (X / s, Y / s);
        }

        /// <summary>
        /// 解析 ICC 并给出 nclx。识别不出的项保持默认值并置 Identified=false，
        /// 由调用方决定是否提示或中止。
        /// </summary>
        public static Result? FromIcc(byte[] icc, Action<string>? log = null)
        {
            if (icc == null || icc.Length < 132) return null;
            int n = (int)BinaryPrimitives.ReadUInt32BigEndian(icc.AsSpan(128, 4));
            if (n <= 0 || n > 128) return null;

            var r = new Result();
            int? rX = null, rY = null, gX = null, gY = null, bX = null, bY = null;
            string trcKind = ""; double gamma = 0, pa = 0, pb = 0, pc = 0, pd = 0;
            int curvPoints = -1;
            string desc = "";

            for (int i = 0; i < n; i++)
            {
                int e = 132 + i * 12;
                if (e + 12 > icc.Length) break;
                string sig = Encoding.ASCII.GetString(icc, e, 4);
                long off = BinaryPrimitives.ReadUInt32BigEndian(icc.AsSpan(e + 4, 4));
                long len = BinaryPrimitives.ReadUInt32BigEndian(icc.AsSpan(e + 8, 4));
                if (off <= 0 || len < 8 || off + len > icc.Length) continue;

                if (sig is "rXYZ" or "gXYZ" or "bXYZ" && len >= 20)
                {
                    int X = BinaryPrimitives.ReadInt32BigEndian(icc.AsSpan((int)off + 8, 4));
                    int Y = BinaryPrimitives.ReadInt32BigEndian(icc.AsSpan((int)off + 12, 4));
                    int Z = BinaryPrimitives.ReadInt32BigEndian(icc.AsSpan((int)off + 16, 4));
                    var (x, y) = XyzToXy(X, Y, Z);
                    if (sig == "rXYZ") { rX = (int)Math.Round(x * 10000); rY = (int)Math.Round(y * 10000); }
                    if (sig == "gXYZ") { gX = (int)Math.Round(x * 10000); gY = (int)Math.Round(y * 10000); }
                    if (sig == "bXYZ") { bX = (int)Math.Round(x * 10000); bY = (int)Math.Round(y * 10000); }
                }
                else if (sig == "desc" && len > 12)
                {
                    int ac = BinaryPrimitives.ReadInt32BigEndian(icc.AsSpan((int)off + 8, 4));
                    if (ac > 0 && off + 12 + ac <= icc.Length)
                        desc = Encoding.ASCII.GetString(icc, (int)off + 12, ac).TrimEnd('\0', ' ');
                }
                else if (sig is "rTRC" or "gTRC" or "bTRC")
                {
                    string kind = Encoding.ASCII.GetString(icc, (int)off, 4);
                    if (kind == "para" && len >= 32)
                    {
                        int fn = BinaryPrimitives.ReadUInt16BigEndian(icc.AsSpan((int)off + 8, 2));
                        trcKind = $"para{fn}";
                        if (fn == 3 && len >= 32)
                        {
                            gamma = BinaryPrimitives.ReadInt32BigEndian(icc.AsSpan((int)off + 12, 4)) / 65536.0;
                            pa = BinaryPrimitives.ReadInt32BigEndian(icc.AsSpan((int)off + 16, 4)) / 65536.0;
                            pb = BinaryPrimitives.ReadInt32BigEndian(icc.AsSpan((int)off + 20, 4)) / 65536.0;
                            pc = BinaryPrimitives.ReadInt32BigEndian(icc.AsSpan((int)off + 24, 4)) / 65536.0;
                            pd = BinaryPrimitives.ReadInt32BigEndian(icc.AsSpan((int)off + 28, 4)) / 65536.0;
                        }
                    }
                    else if (kind == "curv" && len >= 12)
                    {
                        curvPoints = BinaryPrimitives.ReadInt32BigEndian(icc.AsSpan((int)off + 8, 4));
                        trcKind = "curv";
                    }
                }
            }

            // ---- 原色 ----
            // ★ 优先按 ICC 的 desc 名称匹配。
            //   原因：ICC 的 rXYZ/gXYZ/bXYZ 是 **D50 适配后**的值（ICC 连接空间规定），
            //   直接与 D65 下的标称色度坐标比对会对不上（实测 Display P3 偏差达 0.035）。
            //   而 desc 名称本身就是权威答案，且 Adobe 系软件写得规范。
            string dl = desc.ToLowerInvariant();
            (int Code, string Name)? byName = null;
            if (!string.IsNullOrEmpty(dl))
            {
                if (dl.Contains("rec. 2020") || dl.Contains("rec.2020") || dl.Contains("bt.2020")) byName = (9, "BT.2020");
                else if (dl.Contains("display p3")) byName = (12, "Display P3");
                else if (dl.Contains("dci-p3") || dl.Contains("dci p3")) byName = (11, "DCI-P3");
                else if (dl.Contains("srgb") && !dl.Contains("p3")) byName = (1, "BT.709 / sRGB");
                else if (dl.Contains("adobe rgb")) byName = (-1, "AdobeRGB");
            }
            if (byName != null && byName.Value.Code > 0)
            {
                r.Primaries = (ushort)byName.Value.Code;
                r.PrimariesIdentified = true;
                log?.Invoke($"      ICC 原色：{byName.Value.Name}（按 ICC 名称判定，nclx primaries={byName.Value.Code}）");
            }
            else if (byName != null)
            {
                log?.Invoke($"      ICC 原色：{byName.Value.Name} —— HEIF 无对应 nclx 值，需人工确认");
            }
            else if (rX.HasValue && gX.HasValue && bX.HasValue)
            {
                var m = MatchPrimaries(rX.Value / 10000.0, rY!.Value / 10000.0,
                                       gX.Value / 10000.0, gY!.Value / 10000.0,
                                       bX.Value / 10000.0, bY!.Value / 10000.0);
                if (m != null)
                {
                    r.Primaries = (ushort)m.Value.Code;
                    r.PrimariesIdentified = true;
                    log?.Invoke($"      ICC 原色：{m.Value.Name}（nclx primaries={m.Value.Code}）");
                }
                else
                {
                    log?.Invoke("      ICC 原色：不匹配任何已知色域 —— 需人工确认");
                }
            }
            else log?.Invoke("      ICC 缺少原色定义（rXYZ/gXYZ/bXYZ）");

            // ---- 传输曲线 ----
            // para type 3：Y=(aX+b)^γ（X≥d），否则 Y=cX
            //   sRGB:   a=0.948 b=0.052 γ=2.4  c=0.077  ← a=(1/1.055), b=(0.055/1.055)
            //   709:    a=1.099 b=-0.099 γ≈2.222 c=0.081
            if (trcKind == "para3")
            {
                bool isSrgb = Math.Abs(gamma - 2.4) < 0.02 && Math.Abs(pa - 0.948) < 0.01 && Math.Abs(pb - 0.052) < 0.01;
                bool is709 = Math.Abs(gamma - 2.222) < 0.03 && Math.Abs(pa - 1.099) < 0.01;
                if (isSrgb)
                {
                    r.Transfer = 13; r.TransferIdentified = true;
                    log?.Invoke("      ICC 传输曲线：sRGB（nclx transfer=13）");
                }
                else if (is709)
                {
                    r.Transfer = 1; r.TransferIdentified = true;
                    log?.Invoke("      ICC 传输曲线：BT.709（nclx transfer=1）");
                }
                else
                {
                    log?.Invoke($"      ICC 传输曲线：para3 γ={gamma:0.###} a={pa:0.###} b={pb:0.###} —— 未识别，需人工确认");
                }
            }
            else if (trcKind == "curv")
            {
                if (curvPoints == 0)
                {
                    r.Transfer = 8; r.TransferIdentified = true;
                    log?.Invoke("      ICC 传输曲线：线性（curv 0 点 → nclx transfer=8）");
                }
                else if (curvPoints == 1)
                {
                    r.Transfer = 8; r.TransferIdentified = true;
                    log?.Invoke("      ICC 传输曲线：单点（近似线性）");
                }
                else
                {
                    log?.Invoke($"      ICC 传输曲线：curv {curvPoints} 点 —— 需查表，暂按未识别处理");
                }
            }
            else log?.Invoke("      ICC 未提供可识别的传输曲线");

            r.Detail = $"ICC「{(string.IsNullOrEmpty(desc) ? "(无名)" : desc)}」" +
                       $" 原色={(r.PrimariesIdentified ? r.Primaries.ToString() : "未识别")}" +
                       $" 传输={(r.TransferIdentified ? r.Transfer.ToString() : "未识别")}";
            return r;
        }
    }
}
