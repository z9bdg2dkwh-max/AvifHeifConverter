using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AvifHeifConverter.Services
{
    /// <summary>
    /// 完整 HDR 转换管线（苹果配置）——把原 Python 三步合并为一。
    ///
    ///   源 AVIF
    ///     → heif-dec 解码主图（16bit PNG，保留 10bit 精度）
    ///     → HdrMath：PQ 线性化 → 亮度软膝压缩 → 8bit SDR 基底 + 单通道增益图
    ///     → heif-enc 分别编码（基底 P3+sRGB 传递；增益图 8bit 单通道）
    ///     → 修正增益图的 colr 为「未指定」（否则 iOS 按 sRGB 解码会伽马扭曲）
    ///     → 生成 ISO 21496-1 元数据（62 字节）
    ///     → 容器重组：tmap + dimg + auxl + auxC + cdsc(XMP) + grpl
    ///
    /// 关键结论（全部经 iPhone 实测）：
    ///   * 含增益图时主图必须 8bit
    ///   * 增益图必须带 Apple 风格 XMP（HDRGainMapHeadroom），否则 iOS 不合成
    ///   * 增益图 colr 必须为 2/2/2（未指定）
    /// </summary>
    internal static class FullPipeline
    {
        public sealed class Options
        {
            public string OutputPath = "";
            public double GainCap = HdrMath.DefaultGainCap;
            public int Quality = 92;
            public int GainQuality = 95;

            /// <summary>
            /// 是否输出三通道增益图（苹果与索尼原生文件的做法）。
            /// true：逐通道色调映射 + 逐通道增益，高光处自然降饱和、再由增益还原；
            /// false：按亮度统一缩放（早期单通道方案，体积更小）。
            /// 环境变量 AVIFHEIF_MULTICHANNEL=0/1 可强制覆盖，便于 A/B 对比。
            /// </summary>
            public bool MultichannelGainMap { get; set; } = MultichannelDefault;

            /// <summary>
            /// 按内容自动匹配增益上限（推荐）。
            /// true 时忽略 <see cref="GainCap"/> 的具体数值，改为实测该内容需要多少增益；
            /// false 时严格使用 <see cref="GainCap"/>。
            /// </summary>
            public bool AutoGainCap { get; set; } = true;

            /// <summary>
            /// HEVC 色度采样：420（默认，heif-enc 自带默认值）/ 422 / 444。
            /// ★ 源 AVIF 普遍是 4:4:4，而 heif-enc 默认降到 4:2:0 ——
            ///   不显式指定就等于平白丢掉一半色度分辨率。
            ///   4:4:4 的 iPhone 兼容性尚未验证，故默认维持 420。
            /// </summary>
            public string Chroma { get; set; } = "444";

            /// <summary>
            /// 覆盖已存在的同名输出。
            /// 默认 false —— **绝不静默覆盖**：同名时跳过并明确告知。
            /// （此前 FullPipeline 无此检查，界面转换会把已有文件无声覆盖掉。）
            /// </summary>
            public bool Overwrite { get; set; }

            /// <summary>
            /// x265 编码 preset。heif-enc 的默认是 **slow** —— 慢但压缩率好。
            /// 换 faster 档可大幅提速，代价是体积略增。空串表示用引擎默认（slow）。
            /// </summary>
            public string Preset { get; set; } = "";

            private static bool MultichannelDefault =>
                Environment.GetEnvironmentVariable("AVIFHEIF_MULTICHANNEL") != "0";

            /// <summary>
            /// 步骤级进度回调：(当前步, 总步数, 说明)。
            /// 单张大图（如 60MP）整个流程要几分钟，只靠「第几个文件」的进度界面
            /// 会长时间一动不动，用户无法区分「在跑」和「卡死」。
            /// 用属性而非字段：核心项目自身不会赋值，字段形式会触发 CS0649。
            /// </summary>
            public Action<int, int, string>? OnStep { get; set; }
        }

        /// <summary>总步骤数（供进度显示用）。</summary>
        public const int TotalSteps = 5;

        public static async Task<(bool Ok, string Error, string Log, bool Skipped)> RunAsync(
            string srcAvif, Options opt, string engineDir,
            Action<string>? log = null, CancellationToken ct = default)
        {
            var sb = new StringBuilder();
            void Log(string s) { sb.AppendLine(s); log?.Invoke(s); }
            void Step(int n, string s) { Log(s); opt.OnStep?.Invoke(n, TotalSteps, s); }

            // ★ 同名保护：默认**绝不覆盖**已有输出。
            //   界面此前直接走这里，而这里没有检查 —— 末尾的 File.Move(..., true)
            //   会把同名文件无声覆盖掉（真实的数据丢失风险）。
            //   现改为：存在即跳过并说明，用户在界面勾选「覆盖同名文件」后才放行。
            //   放在最前面，省掉一次白跑（大图要几十秒）。
            if (!opt.Overwrite && !string.IsNullOrWhiteSpace(opt.OutputPath) &&
                File.Exists(opt.OutputPath))
            {
                Log($"      已跳过：同名文件已存在（勾选「覆盖同名文件」可覆盖）");
                return (false, "已跳过：同名文件已存在", sb.ToString(), true);
            }

            string? tmp = null;
            try
            {
                tmp = Path.Combine(Path.GetTempPath(), "avifhdr_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tmp);

                // 1) 解码主图 → 16bit PNG
                Step(1, "[1/5] 解码源文件主图 …");

                // ★ 中间格式优先用 TIFF，而不是 PNG。
                //   heif-dec 写一张 60MP 的 16bit PNG 要 deflate 压缩 361 MB，
                //   实测耗时 59.7 秒 —— 而这与解码无关，AV1 解码本身只要 2 秒。
                //   TIFF 不压缩，同样内容 2.1 秒，且位深与源一致（10bit）。
                //   实测 heif-dec 的 PNG 是把 10bit 左移 6 位写成 16bit，
                //   （曾为此实现过 TiffCodec 读取器，后查明 heif-dec 的 TIFF 输出本身有 bug，已删除。）
                string base16 = Path.Combine(tmp, "base16.tif");
                int rc = await RunAsync(Path.Combine(engineDir, "heif-dec.exe"),
                    new[] { srcAvif, base16 }, engineDir, Log, ct).ConfigureAwait(false);
                if (rc != 0)
                    return (false, $"heif-dec 解码失败（退出码 {rc}）", sb.ToString(), false);

                // ★ heif-dec 的命名规律（实测）：
                //   · 主图是**单张**时不带序号 → base16.tif
                //   · 主图是 **grid 分块**时，组装好的完整图是 base16-**1**.tif，
                //     随后 base16-2.tif… 是各个 tile 的单独输出。
                //   曾只检查无序号的那个，导致所有分块源（如索尼 DSC02480，5×4 分块）
                //   一律报「heif-dec 解码失败（退出码 0）」—— 其实解码成功了，
                //   只是文件带 -1 后缀。
                if (!File.Exists(base16))
                {
                    var numbered = Path.Combine(tmp, "base16-1.tif");
                    if (File.Exists(numbered))
                    {
                        base16 = numbered;
                        Log("      源主图为分块(grid)结构，组装结果在 base16-1.tif");
                    }
                }
                if (!File.Exists(base16))
                    return (false, "heif-dec 未产出预期的 TIFF（既无 base16.tif 也无 base16-1.tif）",
                            sb.ToString(), false);

                // ★ 关于「改用 TIFF 提速」——**实测不可行，已放弃**（2026-10-08）。
                //   动机：写 60MP 的 16bit PNG 要 deflate 压缩 361 MB，实测 59.7 秒，
                //         而同内容 TIFF 只要 2.1 秒。
                //   结论：heif-dec 的 TIFF 输出有 bug —— 它按 10bit 计算条带长度
                //         （9504×3×10/8 = 35640 字节/行），却按 **16bit** 写入数据
                //         （实际需 9504×3×2 = 57024 字节）。结果是**每行只写了 62.5%**，
                //         即每行前 5940 个像素正确、其余 37.5% 丢失。
                //   证据：逐条带比对 PNG，条带 i 与 PNG 第 i 行前 5940 像素
                //         **逐位一致（最大差 0）**，但之后即断。
                //   ⇒ 除非 heif-dec 修掉这个 bug，否则只能继续用 PNG。
                var prof = ProbeSource(srcAvif, engineDir, Log);
                Log($"      源：primaries={prof.Primaries} transfer={prof.Transfer} " +
                    $"base_headroom={prof.BaseHeadroom:0.###}");
                // ★ 优先走 dav1d 直解（省掉 heif-dec 写 PNG 的 60 秒压缩开销）。
                //   任何「不被支持」的形态（非全范围 / 未知矩阵 / 非 4:4:4 / 结构异常）
                //   都会返回 null，此处自动回退 PNG 通路 —— 宁可慢，不能转不出来。
                int w, h, ch;
                ushort[] pq;
                var fast = await Av1GridDecoder.TryDecodeAsync(
                    srcAvif, engineDir, prof.Matrix, prof.FullRange, Log, ct).ConfigureAwait(false);
                if (fast != null)
                {
                    w = fast.W; h = fast.H; ch = fast.Channels; pq = fast.Data;
                    Log($"      {w}×{h}，{ch} 通道（dav1d 直解）");
                }
                else
                {
                    string base16Png = Path.Combine(tmp, "base16.png");
                    rc = await RunAsync(Path.Combine(engineDir, "heif-dec.exe"),
                        new[] { srcAvif, base16Png }, engineDir, Log, ct).ConfigureAwait(false);
                    if (!File.Exists(base16Png))
                    {
                        var numbered = Path.Combine(tmp, "base16-1.png");
                        if (File.Exists(numbered)) base16Png = numbered;
                    }
                    if (!File.Exists(base16Png))
                        return (false, "heif-dec 未产出预期的 PNG", sb.ToString(), false);

                    (w, h, ch, pq) = PngCodec.Read16(base16Png);
                    Log($"      {w}×{h}，{ch} 通道（PNG 通路）");
                }

                // ★ 超大图护栏：整条管线的像素索引用的是 int（i*3、i*channels），
                //   一旦 w*h*3 超过 int.MaxValue 就会**静默溢出**、算出错乱的图。
                //   这里显式拦住并给出可读的原因，而不是让它崩在半路。
                long pxCount = (long)w * h;
                const long MaxPixels = int.MaxValue / 3L;      // ≈ 715 MP
                if (pxCount > MaxPixels)
                    return (false,
                        $"图像过大：{pxCount / 1e6:0.#} MP，本版本上限 {MaxPixels / 1e6:0} MP" +
                        $"（{w}×{h}）。该限制来自像素索引的 32 位寻址。", sb.ToString(), false);

                // 内存预估：源像素数组(6B) + SDR 基底(3B) + 增益图(3B) ≈ 12 字节/像素，
                // 但大对象堆的碎片会让实测值明显更高 —— 按 238MP 实测标定为 18 字节/像素。
                double memGB = pxCount * 18.0 / (1024 * 1024 * 1024);
                Log($"      约需内存 {memGB:0.0} GB（估算，实测可能略高）");
                if (memGB > 10.0)
                    Log($"      ⚠ 该图偏大，转换前建议关闭其他占内存的程序");

                // HEVC 单张图像上限：Level 6.2 封顶，最大亮度像素数 35,651,584（约 35.7 MP）。
                // 超过就必须按 grid 分块编码（厂商的分块源正是为此而存在）。
                long samples = (long)w * h;
                bool needTiling = samples > TiledHeicBuilder.HevcMaxLumaSamples;
                if (needTiling)
                    Log($"      {samples / 1e6:0.#} MP 超过 HEVC 单张上限 " +
                        $"{TiledHeicBuilder.HevcMaxLumaSamples / 1e6:0.#} MP → 将按 grid 分块编码");

                // 2) 探测源特征，决定走哪条通路
                if (!prof.HasGainMap)
                {
                    return (false,
                        "源文件不含 HDR 增益图，无需走 HDR 转换通路。" +
                        "（普通 AVIF→HEIC 请使用 convert 命令）", sb.ToString(), false);
                }

                byte[] baseRgb;
                byte[]? computedGainMap = null;
                int computedGainChannels = 1;
                double computedGainCap = opt.GainCap;
                bool reuseSourceGainMap;

                if (prof.IsSdrBase)
                {
                    // ── SDR 基底直通通路 ──
                    // 基底本身就是 SDR 成品（base_headroom=0 且传递函数非 PQ），
                    // 增益图负责把它提升到 HDR。此处**不能**做 PQ 线性化 ——
                    // 曾无条件按 PQ 处理，把这类照片压成「暗部死黑 + 高光爆掉」。
                    Step(2, "[2/5] 基底为 SDR 成品 → 直通处理（不做 PQ 线性化，沿用源增益图与元数据）");
                    baseRgb = new byte[w * h * 3];
                    for (int i = 0; i < w * h; i++)
                        for (int c = 0; c < 3; c++)
                            baseRgb[i * 3 + c] = (byte)(pq[i * ch + Math.Min(c, ch - 1)] >> 8);
                    reuseSourceGainMap = true;
                    Log($"      源 XMP headroom = 2^{prof.AltHeadroom:0.###} = " +
                        $"{Math.Pow(2, prof.AltHeadroom):0.##}×");
                }
                else
                {
                    // ── HDR 基底通路（PQ 或 HLG）──
                    var xfer = prof.Transfer == 18
                        ? HdrMath.SourceTransfer.Hlg : HdrMath.SourceTransfer.Pq;
                    // 按内容自动匹配增益上限：固定值要么削顶要么浪费量化精度。
                    // 实测苹果样张在固定 2.2 下有 1.34% 高光被截断，落日却只需 1.53。
                    double effCap = opt.GainCap;
                    if (opt.AutoGainCap)
                    {
                        effCap = await Task.Run(() => HdrMath.EstimateGainCap(
                            pq, w, h, ch, xfer, opt.MultichannelGainMap), ct).ConfigureAwait(false);
                        Log($"      内容实测所需增益上限 log2={effCap:0.#}" +
                            $"（用户设定 {opt.GainCap:0.##}，已按内容自动匹配）");
                    }
                    Step(2, $"[2/5] 计算 SDR 基底与增益图" +
                        $"（源传递函数 {(xfer == HdrMath.SourceTransfer.Hlg ? "HLG" : "PQ")}，" +
                        $"增益上限 log2={effCap:0.##}）…");
                    double lastPct = -1;
                    var built = await Task.Run(() => HdrMath.Build(
                        pq, w, h, ch, effCap, xfer, opt.MultichannelGainMap, ct,
                        pct =>
                        {
                            int p = (int)(pct * 100);
                            if (p >= lastPct + 10 || p == 100)
                            {
                                lastPct = p;
                                Log($"      色调映射进行中 … {p}%");
                                opt.OnStep?.Invoke(2, TotalSteps, $"色调映射 {p}%");
                            }
                        }), ct).ConfigureAwait(false);
                    baseRgb = built.BaseRgb;
                    computedGainMap = built.GainMap;
                    computedGainChannels = built.GainChannels;
                    computedGainCap = built.GainCap;

                    // 源像素数组（60MP 约 361 MB）此后不再需要，尽早释放。
                    // 它与后续的基底/增益图叠加时会同时占用数百 MB。
                    pq = null!;
                    CollectLargeObjects();
                    reuseSourceGainMap = false;
                }

                // 3) 编码基底（色域沿用源文件 —— 硬标 P3 会让 BT.2020 等源整体偏色）
                string baseHeic = Path.Combine(tmp, "base.heic");
                if (needTiling)
                {
                    Step(3, "[3/5] 按 grid 分块编码基底（超过 HEVC 单张上限）…");
                    var tr = await TiledHeicBuilder.BuildAsync(
                        baseRgb, w, h, baseHeic, engineDir, tmp, opt.Quality, Log, ct,
                        prof.Primaries,
                        prof.HasClli ? prof.MaxCLL : (ushort)0,
                        prof.HasClli ? prof.MaxPALL : (ushort)0,
                        opt.Chroma, opt.Preset).ConfigureAwait(false);
                    if (!tr.Ok) return (false, "分块基底编码失败：" + tr.Error, sb.ToString(), false);
                    Log($"      基底分块完成：{tr.Rows}行×{tr.Cols}列，每块 {tr.TileW}×{tr.TileH}");
                }
                else
                {
                    Step(3, $"[3/5] 编码基底（8bit / primaries={prof.Primaries} / sRGB 传递）…");
                    string base8 = Path.Combine(tmp, "base8.png");
                    PngCodec.Write8(base8, w, h, 3, baseRgb);
                    var encArgs = new System.Collections.Generic.List<string>
                    {
                        base8, "-o", baseHeic, "-q", opt.Quality.ToString(),
                        "--colour_primaries", prof.Primaries.ToString(),
                        "--transfer_characteristic", "13",
                        "--matrix_coefficients", "6",
                    };
                    AppendClli(encArgs, prof, Log);
                    if (opt.Chroma != "420")
                    {
                        encArgs.Add("-p"); encArgs.Add($"chroma={opt.Chroma}");
                        Log($"      色度采样：{opt.Chroma}（非默认值，可保留更多色度细节）");
                    }
                    if (!string.IsNullOrEmpty(opt.Preset))
                    {
                        encArgs.Add("-p"); encArgs.Add($"preset={opt.Preset}");
                        Log($"      编码 preset：{opt.Preset}");
                    }
                    // ★ 全局闸门：限制全机同时运行的 heif-enc 总数（见 EngineGate 注释）
                    await EngineGate.AcquireEncAsync(ct).ConfigureAwait(false);
                    try
                    {
                        rc = await RunAsync(Path.Combine(engineDir, "heif-enc.exe"),
                            encArgs, engineDir, Log, ct).ConfigureAwait(false);
                    }
                    finally { EngineGate.ReleaseEnc(); }
                    if (rc != 0) return (false, $"基底编码失败（退出码 {rc}）", sb.ToString(), false);
                }

                // 4) 增益图 + 元数据
                var opts = new ConvertOptions { GainMapHeadroom = 0 };
                if (reuseSourceGainMap)
                {
                    // SDR 基底通路：增益图与 ISO 元数据**原样沿用源文件** ——
                    // 它本来就是把 SDR 基底提升到 HDR 的那张图，重算反而会破坏语义。
                    Step(4, "[4/5] 沿用源文件的增益图与元数据（不重算）");
                    opts.GainMapHeadroom = Math.Pow(2, prof.AltHeadroom);
                    opts.CopySourceMetadata = true;
                }
                else
                {
                    Step(4, "[4/5] 编码增益图（8bit HEVC）…");
                    int gW = w, gH = h;
                    byte[] gmToEncode = computedGainMap!;
                    int gmCh = computedGainChannels;
                    string gmHeic = Path.Combine(tmp, "gm.heic");

                    // ★ 超限时按 2 的幂**降采样**（不是分块）。
                    //   曾经改用分块（想保住增益图分辨率），但产物在 iPhone 上打不开 ——
                    //   增益图由「单张 item」变成「grid + 多个 tile」是结构级改动，
                    //   iOS 对该形态的接受度未经充分验证，故回退到稳妥的降采样。
                    //   苹果官方文档亦称增益图「通常为主图 1/4 分辨率」。
                    while ((long)gW * gH > TiledHeicBuilder.HevcMaxLumaSamples)
                    {
                        gmToEncode = Downscale2x(gmToEncode, gW, gH, gmCh, out gW, out gH);
                        Log($"      增益图降采样至 {gW}×{gH}");
                    }

                    {
                        string gm8 = Path.Combine(tmp, "gm8.png");
                        PngCodec.Write8(gm8, gW, gH, gmCh, gmToEncode);

                        // 单通道是灰度图，不受色彩矩阵影响；三通道要走 RGB→YCbCr，
                        // 必须显式指定矩阵，否则编解码两端的换算不一致会串色。
                        var gmArgs = gmCh == 3
                            ? new[] { gm8, "-o", gmHeic, "-q", opt.GainQuality.ToString(),
                                      "--colour_primaries", "2", "--transfer_characteristic", "2",
                                      "--matrix_coefficients", "6" }
                            : new[] { gm8, "-o", gmHeic, "-q", opt.GainQuality.ToString() };
                        if (opt.Chroma != "420")
                            gmArgs = gmArgs.Concat(new[] { "-p", $"chroma={opt.Chroma}" }).ToArray();
                        // ★ 全局闸门：限制全机同时运行的 heif-enc 总数（见 EngineGate 注释）
                        await EngineGate.AcquireEncAsync(ct).ConfigureAwait(false);
                        try
                        {
                            rc = await RunAsync(Path.Combine(engineDir, "heif-enc.exe"),
                                gmArgs, engineDir, Log, ct).ConfigureAwait(false);
                        }
                        finally { EngineGate.ReleaseEnc(); }
                        if (rc != 0) return (false, $"增益图编码失败（退出码 {rc}）", sb.ToString(), false);
                    }

                    computedGainMap = null;
                    CollectLargeObjects();

                    int patched = PatchGainMapColr(gmHeic, gmCh == 3 ? (ushort)6 : (ushort)2);
                    Log($"      增益图 colr 修正 {patched} 处 → prim=2 trc=2 " +
                        $"mat={(gmCh == 3 ? 6 : 2)}（线性增益值不可标 sRGB）" +
                        $"；通道数 {(gmCh == 3 ? "3（逐通道增益）" : "1")}");

                    string metaBin = Path.Combine(tmp, "meta.bin");
                    File.WriteAllBytes(metaBin,
                        GainMapMetadata.ForSdrBase(computedGainCap, gmCh == 3).Serialize());
                    opts.GainMapItemFromHeic = gmHeic;
                    opts.GainMapMetadataFile = metaBin;
                    opts.GainMapHeadroom = Math.Pow(2, computedGainCap);
                }

                // 5) 容器重组（含 Apple 风格 XMP —— 缺它 iOS 不会合成增益图）
                Step(5, "[5/5] 注入容器（含 Apple 风格 XMP）…");
                var r = ConversionEngine.MergeGainMapInto(srcAvif, baseHeic, engineDir, opts, Log);
                if (!r.Success) return (false, r.Error ?? "容器注入失败", sb.ToString(), false);

                // 输出路径处理：
                //  * 相对路径时 GetDirectoryName 返回空串，CreateDirectory("") 会抛异常 → 先判空
                //  * 临时目录在 C:、输出常在 D:，File.Move 跨卷需显式走「复制+删除」
                string? outDir = Path.GetDirectoryName(Path.GetFullPath(opt.OutputPath));
                if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
                MoveAcrossVolumes(baseHeic, opt.OutputPath);
                Log($"✓ 完成：{opt.OutputPath}（{new FileInfo(opt.OutputPath).Length / 1048576.0:0.##} MB）");
                return (true, "", sb.ToString(), false);
            }
            catch (Exception ex)
            {
                return (false, ex.Message, sb.ToString(), false);
            }
            finally
            {
                // 调试用：设 AVIFHEIF_KEEPTEMP=1 可保留中间文件（基底 PNG / 增益图 PNG / 编码中间件），
                // 便于事后核对增益图数值、colr、元数据等。
                try
                {
                    bool keep = Environment.GetEnvironmentVariable("AVIFHEIF_KEEPTEMP") == "1";
                    if (!keep && tmp != null && Directory.Exists(tmp)) Directory.Delete(tmp, true);
                    else if (keep && tmp != null) Log($"      [调试] 中间文件已保留：{tmp}");
                }
                catch { /* 临时目录清理失败不影响结果 */ }
            }
        }

        /// <summary>源文件的关键特征，决定后续走哪条通路。</summary>
        internal sealed class SourceProfile
        {
            public ushort Primaries = 12;      // 默认 Display P3
            public ushort Transfer = 16;       // 默认 PQ
            public ushort Matrix = 6;
            /// <summary>全范围标记。dav1d 直解只支持全范围，其余走 PNG 通路。</summary>
            public bool FullRange = true;
            public double BaseHeadroom;        // 0 表示基底本身是 SDR
            public double AltHeadroom;         // 增益图能达到的对数域上限
            public bool HasGainMap;
            public uint GainMapItemId;
            /// <summary>源文件的内容亮度信息（clli）。有则原样带到产物上 ——
            /// iPhone 会依据它决定 HDR 推出多亮，缺了会导致 HDR 显得偏暗。</summary>
            public ushort MaxCLL, MaxPALL;
            public bool HasClli;
            /// <summary>基底是 SDR 成品（base_headroom≈0 且传递函数非 PQ/HLG）。</summary>
            public bool IsSdrBase;
        }

        /// <summary>
        /// 探测源文件的色域/传递函数与增益图元数据。
        ///
        /// 为什么必须探测：源文件存在两种截然不同的布局 ——
        ///   · HDR 基底：transfer=PQ，base_headroom&gt;0（如 DSC01024/00888）
        ///     → 需要「PQ 线性化 + 色调映射」生成 SDR 基底
        ///   · SDR 基底：transfer=BT.709/sRGB，base_headroom=0（如 DSC02480）
        ///     → 基底本身就是 SDR 成品，**直接沿用**，增益图也原样保留
        /// 曾经无条件按 PQ 处理，把 SDR 基底的照片压成「暗部死黑 + 高光爆掉」。
        /// </summary>
        internal static SourceProfile ProbeSource(string srcPath, string engineDir, Action<string> log)
        {
            var prof = new SourceProfile();
            try
            {
                var b = File.ReadAllBytes(srcPath);
                var r = new IsoBmffReader(b);

                // 主图 colr（从 ipco 里取，box 已切好，需单独解析）
                uint primary = r.ParsePrimaryItemId();
                var boxes = r.ParsePropertyBoxes();
                var assoc = r.ParsePropertyAssociations();
                if (assoc.TryGetValue(primary, out var idxs))
                {
                    bool colrDone = false;
                    foreach (var raw in idxs)
                    {
                        int i = raw & 0xFFFF;
                        if (i < 1 || i > boxes.Count) continue;
                        var box = boxes[i - 1];
                        if (box.Size <= 0 || box.Size > int.MaxValue) continue;
                        var slice = new byte[box.Size];
                        Array.Copy(b, box.Offset, slice, 0, (int)box.Size);
                        var rr = new IsoBmffReader(slice);

                        var top = rr.FindTopLevel("colr");
                        if (top != null)
                        {
                            var nclx = rr.ReadNclx(top.Value);
                            if (nclx != null)
                            {
                                prof.Primaries = nclx.Value.Primaries;
                                prof.Transfer = nclx.Value.Transfer;
                                prof.Matrix = nclx.Value.Matrix;
                                prof.FullRange = nclx.Value.FullRange;
                            }
                        }
                        // 内容亮度信息（clli）：原样带到产物，否则 iOS 可能按保守亮度渲染
                        var cb = rr.FindTopLevel("clli");
                        if (cb != null)
                        {
                            var cl = rr.ReadClliData(cb.Value);
                            if (cl != null)
                            {
                                prof.MaxCLL = cl.Value.MaxCLL;
                                prof.MaxPALL = cl.Value.MaxPALL;
                                prof.HasClli = cl.Value.MaxCLL > 0;
                            }
                        }
                        // ★ 不能读到 colr 就 break —— clli 通常排在 colr **之后**，
                        //   提前跳出会导致 clli 永远读不到（曾因此漏传内容亮度信息，
                        //   表现为「有 HDR 但不够亮」）。属性只有寥寥几个，全扫完即可。
                        colrDone = top != null;
                    }
                }

                var gm = new GainMapExtractor(b, engineDir).Extract();
                prof.HasGainMap = gm.HasGainMap;
                prof.GainMapItemId = gm.ImageItemId;
                if (gm.HasGainMap)
                {
                    var metaItem = gm.Items.FirstOrDefault(x => x.Id == gm.MetaItemId);
                    var tb = metaItem?.Data;
                    if (tb != null && tb.Length >= 62)
                    {
                        var md = GainMapMetadata.Parse(tb);
                        prof.BaseHeadroom = md.BaseHdrHeadroom.N / (double)md.BaseHdrHeadroom.D;
                        prof.AltHeadroom = md.AltHdrHeadroom.N / (double)md.AltHdrHeadroom.D;
                    }
                }
                prof.IsSdrBase = prof.HasGainMap && prof.BaseHeadroom <= 0.001
                                 && prof.Transfer != 16;   // PQ 基底不会是 SDR
            }
            catch (Exception ex)
            {
                log("      ⚠ 源特征探测失败（按默认 PQ 通路处理）：" + ex.Message);
            }
            return prof;
        }

        /// <summary>
        /// 灰度图 2×2 均值降采样。用于把超过 HEVC 单张上限的增益图缩到限制内
        /// （苹果官方文档亦称增益图「通常为主图 1/4 分辨率」）。
        /// 奇数边时最后一列/行直接复制，避免丢像素。
        /// </summary>
        internal static byte[] Downscale2x(byte[] src, int w, int h, int channels,
            out int nw, out int nh)
        {
            nw = Math.Max(1, (w + 1) / 2);
            nh = Math.Max(1, (h + 1) / 2);
            var dst = new byte[nw * nh * channels];
            for (int y = 0; y < nh; y++)
            {
                int y0 = Math.Min(y * 2, h - 1), y1 = Math.Min(y * 2 + 1, h - 1);
                for (int x = 0; x < nw; x++)
                {
                    int x0 = Math.Min(x * 2, w - 1), x1 = Math.Min(x * 2 + 1, w - 1);
                    for (int c = 0; c < channels; c++)
                    {
                        int s = src[(y0 * w + x0) * channels + c] + src[(y0 * w + x1) * channels + c]
                              + src[(y1 * w + x0) * channels + c] + src[(y1 * w + x1) * channels + c];
                        dst[(y * nw + x) * channels + c] = (byte)((s + 2) / 4);
                    }
                }
            }
            return dst;
        }

        /// <summary>
        /// 跨卷移动文件。File.Move 在源与目标不同盘符时会失败（Win32 MoveFile 限制），
        /// 这里退化为「复制 + 删除」。
        /// </summary>
        private static void MoveAcrossVolumes(string src, string dst)
        {
            try
            {
                File.Move(src, dst, true);
            }
            catch (IOException)
            {
                File.Copy(src, dst, true);
                try { File.Delete(src); } catch { /* 临时文件删不掉不影响结果 */ }
            }
        }

        /// <summary>
        /// 把增益图 HEIC 里所有 colr 的 nclx 改成 prim=2 trc=2 mat=2。
        /// 增益图存的是线性增益值，标成 sRGB 会让读取器按 sRGB 曲线解码（gain^2.2 扭曲）。
        /// colr 是定长 box，原地改写不影响任何偏移。
        /// </summary>
        /// <summary>
        /// 触发一次「带压缩」的完整回收。
        ///
        /// 60MP 图的中间数组都在大对象堆（≥85KB 即入 LOH），而 LOH 默认**不压缩**，
        /// 释放后留下的空洞无法复用 —— 连续转多张时内存只增不减，最终卡死。
        /// 在几个关键节点主动回收一次，能把峰值压下来。
        /// </summary>
        internal static void CollectLargeObjects()
        {
            try
            {
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                    System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            }
            catch { /* 回收失败不影响正确性 */ }
        }

        /// <summary>
        /// 把源文件的 clli（内容亮度信息）加到 heif-enc 参数里。
        ///
        /// 为什么必须带：iPhone 依据 clli 判断内容有多亮、决定 HDR 推到什么程度。
        /// 缺失时系统会按保守值渲染 —— 表现为「HDR 有，但不够亮」。
        /// </summary>
        private static void AppendClli(List<string> args, SourceProfile prof, Action<string> log)
        {
            if (!prof.HasClli) return;
            // MaxPALL 为 0 时给一个合理值：取 MaxCLL 的四分之一（工具不接受 0）
            ushort pall = prof.MaxPALL > 0 ? prof.MaxPALL : (ushort)Math.Max(1, prof.MaxCLL / 4);
            args.Add("--clli");
            args.Add($"{prof.MaxCLL},{pall}");
            log($"      内容亮度信息 clli: MaxCLL={prof.MaxCLL} MaxPALL={pall}" +
                (prof.MaxPALL > 0 ? "" : "（源 MaxPALL 为 0，按 MaxCLL/4 估算）"));
        }

        private static int PatchGainMapColr(string path, ushort matrix = 2)
        {
            var b = File.ReadAllBytes(path);
            int n = 0;
            for (int i = 0; i + 18 <= b.Length; i++)
            {
                if (b[i] != 'c' || b[i + 1] != 'o' || b[i + 2] != 'l' || b[i + 3] != 'r') continue;
                if (b[i + 4] != 'n' || b[i + 5] != 'c' || b[i + 6] != 'l' || b[i + 7] != 'x') continue;
                b[i + 8] = 0; b[i + 9] = 2;          // primaries = 2
                b[i + 10] = 0; b[i + 11] = 2;        // transfer = 2
                b[i + 12] = (byte)(matrix >> 8); b[i + 13] = (byte)matrix;   // matrix
                n++;
            }
            if (n > 0) File.WriteAllBytes(path, b);
            return n;
        }

        private static async Task<int> RunAsync(string exe, IEnumerable<string> args,
            string engineDir, Action<string> log, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            var old = psi.Environment.TryGetValue("PATH", out var v) ? v : "";
            psi.Environment["PATH"] = engineDir + ";" + old;

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            // ★ 取消时必须**真正杀掉子进程**。
            //   之前只把 ct 传给 WaitForExitAsync —— 那只中断「等待」，
            //   heif-enc / heif-dec 会继续跑到自然结束，界面显示「已停止」
            //   但 CPU 仍在满载，用户会以为程序没反应。
            using var reg = ct.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
                catch { /* 进程可能刚好自己退出 */ }
            });

            var so = proc.StandardOutput.ReadToEndAsync();
            var se = proc.StandardError.ReadToEndAsync();
            try
            {
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                throw;
            }
            var outText = await so.ConfigureAwait(false);
            var errText = await se.ConfigureAwait(false);
            foreach (var line in (outText + "\n" + errText).Split('\n'))
                if (!string.IsNullOrWhiteSpace(line)) log("      " + line.TrimEnd());
            return proc.ExitCode;
        }
    }
}
