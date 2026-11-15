using System;
using System.Buffers.Binary;
using System.Threading;

using System.Threading.Tasks;

namespace AvifHeifConverter.Services
{
    /// <summary>
    /// HDR 转换数学。
    ///
    /// 全部常量均经实测自证，不是估计值：
    ///   * SDR 参考白 = 203 nits（BT.2408 图形白）
    ///     自证：源文件 clli 的 MaxCLL = 3248 = 203 × 2^4.0001（元数据的 headroom）
    ///   * 增益上限取 2.2（log2 ≈ 4.6×），对齐苹果 IMG_8208 的 2.074（4.2×）
    ///
    /// 流程：
    ///   PQ 编码基底 → 线性 → 亮度软膝压缩 → 统一缩放（保持色相）
    ///   → 8bit SDR 基底（sRGB 编码）+ 单通道增益图（log2 比值）
    /// </summary>
    internal static class HdrMath
    {
        public const double SdrWhiteNits = 203.0;

        /// <summary>
        /// 高光软膝起点（SDR 白单位）。默认 0.65 由「以苹果 IMG_8208 的 SDR 基底
        /// 统计值为目标做参数拟合」得出（目标：亮度分位 50/90/99/99.9 = 197/212/216/226）。
        /// 属可调项——界面里暴露成「高光压缩」设置。
        /// </summary>
        public const double Knee = 0.65;

        /// <summary>软膝过渡宽度，越大过渡越平缓（高光压缩越强）。</summary>
        public const double KneeScale = 0.50;

        public const double DefaultGainCap = 2.2;

        // 亮度权重（BT.709）
        private const double LwR = 0.2126, LwG = 0.7152, LwB = 0.0722;

        /// <summary>SMPTE ST 2084 PQ EOTF：编码值 [0,1] → 线性（1.0 = 10000 cd/m²）。</summary>
        public static double PqEotf(double e)
        {
            const double m1 = 2610.0 / 16384.0;
            const double m2 = 2523.0 / 4096.0 * 128.0;
            const double c1 = 3424.0 / 4096.0;
            const double c2 = 2413.0 / 4096.0 * 32.0;
            const double c3 = 2392.0 / 4096.0 * 32.0;
            if (e <= 0) return 0;
            if (e >= 1) return 1;
            double ep = Math.Pow(e, 1.0 / m2);
            double num = Math.Max(ep - c1, 0.0);
            double den = c2 - c3 * ep;
            return Math.Pow(num / Math.Max(den, 1e-10), 1.0 / m1);
        }

        /// <summary>IEC 61966-2-1 sRGB OETF：线性 → 编码。</summary>
        public static double SrgbOetf(double x)
        {
            if (x <= 0) return 0;
            if (x >= 1) return 1;
            return x <= 0.0031308 ? 12.92 * x : 1.055 * Math.Pow(x, 1 / 2.4) - 0.055;
        }

        /// <summary>源文件的传递函数类型。</summary>
        public enum SourceTransfer
        {
            /// <summary>SMPTE ST 2084 感知量化（绝对亮度，峰值 10000 cd/m²）。</summary>
            Pq,
            /// <summary>ITU-R BT.2100 混合对数伽马（相对亮度，参考峰值 1000 cd/m²）。</summary>
            Hlg,
        }

        // ---- HLG 常数（ITU-R BT.2100-2 表 5，已对原文核实）----
        private const double HlgA = 0.17883277;
        private const double HlgB = 0.28466892;   // = 1 - 4a
        private const double HlgC = 0.55991073;   // = 0.5 - a·ln(4a)
        private const double HlgLw = 1000.0;      // 参考显示峰值亮度 cd/m²
        private const double HlgLamb = 5.0;       // 环境光 cd/m²（BT.2390）

        /// <summary>
        /// HLG 逆 OETF：非线性的 E' → 场景线性 E（0..1）。
        /// BT.2100-2 表 5：E = E'²/3（E'≤0.5）；E = (exp((E'-c)/a)+b)/12（E'&gt;0.5）。
        /// </summary>
        public static double HlgInverseOetf(double ep)
        {
            if (ep <= 0) return 0;
            if (ep >= 1) return 1;
            return ep <= 0.5
                ? ep * ep / 3.0
                : (Math.Exp((ep - HlgC) / HlgA) + HlgB) / 12.0;
        }

        /// <summary>HLG 系统 gamma：γ = 1.2 + 0.42·log10(Lw/1000) − 0.076·log10(Lamb/5)。</summary>
        private static double HlgSystemGamma()
            => 1.2 + 0.42 * Math.Log10(HlgLw / 1000.0)
                   - 0.076 * Math.Log10(HlgLamb / 5.0);

        // BT.2020 亮度权重（HLG 的 OOTF 按规范使用这一组）
        private const double HlgYr = 0.2627, HlgYg = 0.6780, HlgYb = 0.0593;

        /// <summary>亮度软膝压缩（SDR 白单位）。x ≤ Knee 保持线性；之上 tanh 平滑压缩到 1。</summary>
        private static double ToneMapLum(double L)
        {
            if (L <= 0) return 0;
            return L <= Knee ? L : Knee + (1 - Knee) * Math.Tanh((L - Knee) / KneeScale);
        }

        /// <summary>
        /// 抽样估算「这段内容实际需要多大的增益上限」（log2）。
        ///
        /// 为什么需要：固定上限要么浪费量化精度（内容用不满），要么削顶
        /// （内容超出上限）。实测苹果样张 DSC01024 在固定 2.2 下，
        /// 有 1.34% 的像素被截断 —— 而那恰是最亮的高光区；
        /// 落日则只需 1.53，固定 2.2 纯属浪费。
        ///
        /// 抽样步长按短边约取 256 个采样点，开销可忽略。
        /// </summary>
        public static double EstimateGainCap(
            ushort[] src, int w, int h, int channels, SourceTransfer transfer,
            bool multichannel = false, double minCap = 1.0, double maxCap = 4.0)
        {
            double toSdrWhite = 10000.0 / SdrWhiteNits;
            double hlgGamma = HlgSystemGamma();
            double hlgScale = HlgLw / SdrWhiteNits;
            int step = Math.Max(1, Math.Min(w, h) / 256);
            double maxNeed = 0;

            for (int y = 0; y < h; y += step)
            {
                for (int x = 0; x < w; x += step)
                {
                    int si = (y * w + x) * channels;
                    double r, g, b;

                    if (transfer == SourceTransfer.Hlg)
                    {
                        double rs = HlgInverseOetf(src[si] / 65535.0);
                        double gs = HlgInverseOetf(src[si + 1] / 65535.0);
                        double bs = HlgInverseOetf(src[si + 2] / 65535.0);
                        double ys = rs * HlgYr + gs * HlgYg + bs * HlgYb;
                        double k = ys > 1e-8 ? hlgScale * Math.Pow(ys, hlgGamma - 1.0) : hlgScale;
                        r = rs * k; g = gs * k; b = bs * k;
                    }
                    else
                    {
                        r = PqEotf(src[si] / 65535.0) * toSdrWhite;
                        g = PqEotf(src[si + 1] / 65535.0) * toSdrWhite;
                        b = PqEotf(src[si + 2] / 65535.0) * toSdrWhite;
                    }

                    if (multichannel)
                    {
                        maxNeed = Math.Max(maxNeed, Need(r));
                        maxNeed = Math.Max(maxNeed, Need(g));
                        maxNeed = Math.Max(maxNeed, Need(b));
                    }
                    else
                    {
                        maxNeed = Math.Max(maxNeed, Need(r * LwR + g * LwG + b * LwB));
                    }
                }
            }

            // 留 5% 余量再向上取到 0.1，避免边界像素恰好被削
            return Math.Clamp(Math.Ceiling(maxNeed * 1.05 * 10.0) / 10.0, minCap, maxCap);

            static double Need(double v)
            {
                if (v <= 1e-8) return 0;
                double t = ToneMapLum(v);
                return t > 1e-8 ? Math.Log2(v / t) : 0;
            }
        }

        /// <summary>Build 的结果。</summary>
        public sealed class BuildResult
        {
            public byte[] BaseRgb = Array.Empty<byte>();
            /// <summary>增益图：单通道为 w*h 字节；多通道为 w*h*3（RGB 交错）。</summary>
            public byte[] GainMap = Array.Empty<byte>();
            /// <summary>增益图通道数（1 或 3）。</summary>
            public int GainChannels = 1;
            public double GainCap;
        }

        /// <summary>
        /// 由 PQ / HLG 编码的基底生成 SDR 8bit 基底 + 8bit 增益图。
        /// </summary>
        /// <param name="pq">16bit 量程的源基底（RGB 交错）</param>
        /// <param name="w">宽</param>
        /// <param name="h">高</param>
        /// <param name="channels">源通道数（取前 3 个当 RGB）</param>
        /// <param name="gainCap">增益上限（log2）</param>
        /// <param name="multichannel">
        /// 是否输出**三通道**增益图。
        ///
        /// 两者的区别在「SDR 基底怎么算」：
        ///   · 单通道：按**亮度**统一缩放三个通道（保持色相，高光会整体压暗）
        ///   · 多通道：对**三个通道各自**做同一条软膝曲线 ——
        ///     高光处各通道压缩程度不同，饱和度自然下降；
        ///     再由逐通道增益把原始饱和度还原回来。
        ///
        /// 苹果原生文件（IMG_8208 / DSC01024）与索尼机内 AVIF 都是多通道，
        /// 多通道是更贴近厂商做法的形态。
        /// </param>
        public static BuildResult Build(
            ushort[] pq, int w, int h, int channels, double gainCap = DefaultGainCap,
            SourceTransfer transfer = SourceTransfer.Pq,
            bool multichannel = false,
            CancellationToken ct = default,
            Action<double>? onProgress = null)
        {
            long nL = (long)w * h;
            if (nL > int.MaxValue / 3L)
                throw new ArgumentOutOfRangeException(nameof(w),
                    $"图像 {w}×{h} 超出 32 位索引上限（{int.MaxValue / 3L / 1e6:0} MP）");
            int n = (int)nL;
            var baseRgb = new byte[n * 3];
            var gain = new byte[n * (multichannel ? 3 : 1)];
            double toSdrWhite = 10000.0 / SdrWhiteNits;
            double invCap = 1.0 / gainCap;
            double hlgGamma = HlgSystemGamma();
            double hlgScale = HlgLw / SdrWhiteNits;

            // 每处理这么多像素报告一次进度（60MP 全流程要几分钟，没有反馈界面会像卡死）
            int chunk = Math.Max(1, n / 100);

            byte Enc(double linear) =>
                (byte)Math.Clamp(SrgbOetf(linear) * 255.0 + 0.5, 0, 255);

            byte GainByte(double alt, double bs)
            {
                double nl = alt > 1e-8 && bs > 1e-8 ? Math.Log2(alt / bs) : 0.0;
                // 只允许「提亮」方向：压暗由 SDR 基底自身承担（与单通道口径一致）
                return (byte)Math.Clamp(Math.Clamp(nl, 0.0, gainCap) * invCap * 255.0 + 0.5, 0, 255);
            }

            // ★ 按行带并行：逐像素运算彼此独立，而这一步是当前最大的单线程瓶颈
            //   （60MP 实测 10.6 秒、占全流程 46%）。24 核上并行后约 1 秒。
            //   每个线程写自己那段行区间，输出数组不重叠，无需加锁；
            //   进度用原子计数汇总。
            int done = 0;
            Parallel.For(0, h, new ParallelOptions { CancellationToken = ct }, y =>
            {
                long rowStart = (long)y * w;
                for (long i = rowStart; i < rowStart + w; i++)
                {
                    if ((i & 0x3FFF) == 0 && Interlocked.Increment(ref done) % chunk == 0)
                        onProgress?.Invoke((double)i / n);
                    int ii = (int)i;
                    int si = ii * channels;
                double r, g, b;

                if (transfer == SourceTransfer.Hlg)
                {
                    // HLG：先逆 OETF 得场景线性，再套 OOTF（按规范用 BT.2020 亮度权重）。
                    // OOTF 是「逐像素整体」运算，不能像 PQ 那样逐通道独立解码。
                    double rs = HlgInverseOetf(pq[si] / 65535.0);
                    double gs = HlgInverseOetf(pq[si + 1] / 65535.0);
                    double bs = HlgInverseOetf(pq[si + 2] / 65535.0);
                    double ys = rs * HlgYr + gs * HlgYg + bs * HlgYb;
                    double k = ys > 1e-8
                        ? hlgScale * Math.Pow(ys, hlgGamma - 1.0)
                        : hlgScale;
                    r = rs * k; g = gs * k; b = bs * k;
                }
                else
                {
                    r = PqEotf(pq[si] / 65535.0) * toSdrWhite;
                    g = PqEotf(pq[si + 1] / 65535.0) * toSdrWhite;
                    b = PqEotf(pq[si + 2] / 65535.0) * toSdrWhite;
                }

                    int o = ii * 3;

                    if (multichannel)
                    {
                        // 逐通道压缩：各自过同一条软膝曲线
                        double br = ToneMapLum(r), bg = ToneMapLum(g), bb = ToneMapLum(b);
                        baseRgb[o] = Enc(br);
                        baseRgb[o + 1] = Enc(bg);
                        baseRgb[o + 2] = Enc(bb);
                        gain[o] = GainByte(r, br);
                        gain[o + 1] = GainByte(g, bg);
                        gain[o + 2] = GainByte(b, bb);
                    }
                    else
                    {
                        double L = r * LwR + g * LwG + b * LwB;

                        // ★ SDR 亮度 = 对「原始亮度」做「软膝压缩」（单调递增，上界 < 1）。
                        //   曾经写成「先按增益上限除一次、再压缩」，结果 L 在 (1, 2^cap] 区间内
                        //   被压成恒定值 0.94 —— 整段高光削顶（约 203–933 nits 全平）。
                        double ls = ToneMapLum(L);

                        double scale = L > 1e-8 ? ls / L : 1.0;
                        baseRgb[o] = Enc(r * scale);
                        baseRgb[o + 1] = Enc(g * scale);
                        baseRgb[o + 2] = Enc(b * scale);

                        // 增益 = 还原原始亮度所需的对数比；超出上限的部分由 SDR 基底承担（有损，属预期）
                        gain[ii] = GainByte(L, ls);
                    }
                }
            });

            return new BuildResult
            {
                BaseRgb = baseRgb,
                GainMap = gain,
                GainChannels = multichannel ? 3 : 1,
                GainCap = gainCap,
            };
        }
    }

    /// <summary>
    /// ISO 21496-1 增益图元数据（62 字节单通道 / 142 字节三通道）。
    /// 布局经 DSC01024.avif 的 142 字节实测回环验证（序列化结果与原字节完全一致）。
    /// </summary>
    internal sealed class GainMapMetadata
    {
        public byte Version = 0;
        public ushort MinimumVersion = 0;
        public ushort WriterVersion = 0;
        public bool Multichannel;
        public bool UseBaseColourSpace = true;
        public bool BackwardDirection;
        public (uint N, uint D) BaseHdrHeadroom = (0, 1);
        public (uint N, uint D) AltHdrHeadroom = (0, 1);
        /// <summary>每通道：min/max/gamma/baseOffset/altOffset，各为 (N, D)。</summary>
        public readonly System.Collections.Generic.List<Chan> Channels = new();

        public struct Chan
        {
            public (int N, uint D) Min, Max;
            public (uint N, uint D) Gamma;
            public (int N, uint D) BaseOffset, AltOffset;
        }

        public byte[] Serialize()
        {
            int n = Multichannel ? 3 : 1;
            var buf = new byte[22 + n * 40];
            int p = 0;
            buf[p++] = Version;
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(p), MinimumVersion); p += 2;
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(p), WriterVersion); p += 2;
            byte flags = 0;
            if (Multichannel) flags |= 0x80;
            if (UseBaseColourSpace) flags |= 0x40;
            if (BackwardDirection) flags |= 0x04;
            buf[p++] = flags;
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(p), BaseHdrHeadroom.N); p += 4;
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(p), BaseHdrHeadroom.D); p += 4;
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(p), AltHdrHeadroom.N); p += 4;
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(p), AltHdrHeadroom.D); p += 4;
            for (int i = 0; i < n; i++)
            {
                var c = Channels.Count > i ? Channels[i] : Channels[0];
                BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(p), c.Min.N); p += 4;
                BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(p), c.Min.D); p += 4;
                BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(p), c.Max.N); p += 4;
                BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(p), c.Max.D); p += 4;
                BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(p), c.Gamma.N); p += 4;
                BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(p), c.Gamma.D); p += 4;
                BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(p), c.BaseOffset.N); p += 4;
                BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(p), c.BaseOffset.D); p += 4;
                BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(p), c.AltOffset.N); p += 4;
                BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(p), c.AltOffset.D); p += 4;
            }
            return buf;
        }

        /// <summary>
        /// 构造 SDR 基底 + 增益图的元数据（base_headroom=0，alt=增益上限）。
        /// </summary>
        /// <param name="gainCapLog2">增益上限（log2）</param>
        /// <param name="multichannel">
        /// true 时写 3 组通道参数（共 142 字节），与三通道增益图配套；
        /// false 时写 1 组（共 62 字节）。
        /// </param>
        public static GainMapMetadata ForSdrBase(double gainCapLog2, bool multichannel = false)
        {
            uint cap = (uint)Math.Round(gainCapLog2 * 10000);
            var m = new GainMapMetadata
            {
                Multichannel = multichannel,
                UseBaseColourSpace = true,
                BackwardDirection = false,
                BaseHdrHeadroom = (0, 1),
                AltHdrHeadroom = (cap, 10000),
            };
            int nch = multichannel ? 3 : 1;
            for (int i = 0; i < nch; i++)
            {
                m.Channels.Add(new Chan
                {
                    Min = (0, 1),
                    Max = ((int)cap, 10000),
                    Gamma = (1, 1),
                    BaseOffset = (1, 100000),
                    AltOffset = (1, 100000),
                });
            }
            return m;
        }

        /// <summary>
        /// 解析 ISO 21496-1 元数据字节。布局与 <see cref="Serialize"/> 互逆，
        /// 已对 DSC01024.avif 的 142 字节做过字节级回环校验。
        /// </summary>
        public static GainMapMetadata Parse(byte[] d)
        {
            int p = 0;
            var m = new GainMapMetadata { Version = d[p++] };
            m.MinimumVersion = (ushort)((d[p] << 8) | d[p + 1]); p += 2;
            m.WriterVersion = (ushort)((d[p] << 8) | d[p + 1]); p += 2;
            byte flags = d[p++];
            m.Multichannel = (flags & 0x80) != 0;
            m.UseBaseColourSpace = (flags & 0x40) != 0;
            m.BackwardDirection = (flags & 0x04) != 0;

            uint R32() { uint v = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p)); p += 4; return v; }
            int R32S() { int v = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(p)); p += 4; return v; }

            m.BaseHdrHeadroom = (R32(), R32());
            m.AltHdrHeadroom = (R32(), R32());

            int nch = m.Multichannel ? 3 : 1;
            for (int i = 0; i < nch; i++)
            {
                var c = new Chan
                {
                    Min = (R32S(), R32()),
                    Max = (R32S(), R32()),
                    Gamma = (R32(), R32()),
                    BaseOffset = (R32S(), R32()),
                    AltOffset = (R32S(), R32()),
                };
                m.Channels.Add(c);
            }
            return m;
        }
    }
}
