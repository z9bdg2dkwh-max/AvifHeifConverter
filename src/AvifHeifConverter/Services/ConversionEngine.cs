using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AvifHeifConverter.Models;

namespace AvifHeifConverter.Services
{
    /// <summary>增益图在输出文件里的编码方式。</summary>
    public enum GainMapCodec
    {
        /// <summary>
        /// 原样搬运源文件的增益图 item（AVIF 里为 AV1）——**字节级零损失**，
        /// 增益图与元数据完全没动。文件因此含 av01 与 hvc1 两种编码。
        /// </summary>
        KeepOriginal,

        /// <summary>
        /// 重新编码为 HEVC(4:4:4)——与苹果自家 HEIC 一致（苹果 HEIC 里增益图必为 hvc1），
        /// 兼容性最好，但增益图不是字节级无损。
        /// </summary>
        Hevc,
    }

    /// <summary>转换选项。</summary>
    public sealed class ConvertOptions
    {
        /// <summary>输出扩展名：.heic 或 .heif。</summary>
        public string OutputExtension { get; set; } = ".heic";

        /// <summary>输出目录。为空表示与源文件同目录。</summary>
        public string? OutputDirectory { get; set; }

        /// <summary>HEVC 质量 0-100，越大越好。null 表示无损模式（-L）。</summary>
        public int? Quality { get; set; } = 90;

        /// <summary>无损编码：像素完全无损失，但体积大、编码慢。</summary>
        public bool Lossless { get; set; }

        /// <summary>
        /// 保留原始色彩描述。
        /// 必须默认 true —— heif-enc 不指定时会降级为 BT.709 + sRGB。
        /// </summary>
        public bool PreserveColorProfile { get; set; } = true;

        /// <summary>覆盖已存在的同名文件。</summary>
        public bool Overwrite { get; set; }

        /// <summary>HDR 增益上限（log2）。null 表示用 HdrMath 的默认值。</summary>
        public double? GainCap { get; set; }

        /// <summary>增益图编码质量 0-100。</summary>
        public int? GainQuality { get; set; }

        /// <summary>HEVC 色度采样：420（默认）/ 422 / 444。</summary>
        public string Chroma { get; set; } = "444";

        /// <summary>x265 preset（空=引擎默认 slow）。</summary>
        public string Preset { get; set; } = "";

        /// <summary>
        /// 保留源文件的 Gain Map（ISO tmap 布局或 Apple auxl 布局）。
        /// 需要在主图转码后做容器重组。
        /// </summary>
        public bool PreserveGainMap { get; set; } = true;

        /// <summary>
        /// 增益图编码方式。默认原样搬运（零损失）；
        /// 若目标设备对混编（av01 + hvc1）挑剔，可切到 Hevc。
        /// </summary>
        public GainMapCodec GainMapMode { get; set; } = GainMapCodec.KeepOriginal;

        /// <summary>增益图转 HEVC 时的质量（1-100）。</summary>
        public int GainMapQuality { get; set; } = 95;

        /// <summary>增益图转 HEVC 时使用无损编码（体积大）。</summary>
        public bool GainMapLossless { get; set; }

        /// <summary>
        /// 用外部 HEIC 里的主图 item 替换增益图。
        /// 用于「自己重算增益图」的路线：先把新增益图编码成独立 HEIC，再注入。
        /// </summary>
        public string? GainMapItemFromHeic { get; set; }

        /// <summary>
        /// 用指定二进制文件替换 tmap item 的数据（ISO 21496-1 元数据）。
        /// 重算增益图时必须同步改写，否则基底与增益图的语义不匹配。
        /// </summary>
        public string? GainMapMetadataFile { get; set; }

        /// <summary>
        /// 给增益图附加 Apple 风格的 XMP（HDRGainMapVersion + HDRGainMapHeadroom）。
        /// 值 = 线性提亮倍数（如 4.21 表示最高可提亮到 4.21 倍）。
        /// 苹果的 HDR HEIC 里增益图都带这段 XMP，iOS 依赖它判断增益能力。
        /// &lt;= 0 表示不附加。
        /// </summary>
        public double GainMapHeadroom { get; set; }

        /// <summary>
        /// 从源文件搬运主图的元数据 item（Exif / 原生 XMP）。
        /// 默认开启——新管线经 PNG 中转，不搬就会全丢。
        /// </summary>
        public bool CopySourceMetadata { get; set; } = true;

        /// <summary>附加的编码器参数（如 "presets=slow"），可为空。</summary>
        public string? ExtraEncoderArgs { get; set; }
    }

    public sealed class ConvertResult
    {
        public bool Success { get; init; }

        /// <summary>输出已存在且未勾选覆盖时为 true —— 属「跳过」而非「失败」。</summary>
        public bool Skipped { get; init; }

        public string InputPath { get; init; } = "";
        public string? OutputPath { get; init; }
        public string? Error { get; init; }
        public string? Log { get; init; }
        public HeifFileInfo? SourceInfo { get; init; }
        public TimeSpan Elapsed { get; init; }

        /// <summary>是否保留了 Gain Map。</summary>
        public bool GainMapPreserved { get; init; }

        public long OutputSize { get; init; }
    }

    /// <summary>
    /// AVIF → HEIF/HEIC 转换引擎。
    ///
    /// 技术要点（均经实测验证）：
    /// 1. AVIF 与 HEIF 是同一容器（ISOBMFF），差异在图像编码：
    ///    AVIF 用 AV1，HEIC 用 HEVC。转换 = 解码 AV1 → 重编码 HEVC。
    /// 2. **色彩描述必须显式指定**，否则 heif-enc 默认降级为 BT.709 + sRGB，
    ///    会破坏 P3/PQ 的 HDR 信息。
    /// 3. Gain Map（ISO 21496-1）以 item 形式存于 iinf，libheif 公开 API
    ///    完全不暴露它。本程序通过自研 ISOBMFF 解析保留该 item 字节。
    /// </summary>
    public static class ConversionEngine
    {
        /// <summary>
        /// 构建 heif-enc 的完整路径，并把其目录加入当前进程 DLL 搜索路径。
        ///
        /// 必须设 DLL 目录：libheif.dll 有 30+ 个依赖（libstdc++/libgcc/
        /// libtiff/libaom/...），缺任一都会静默失败。
        /// </summary>
        public static string? PrepareEngine(string? engineDir, out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(engineDir) || !Directory.Exists(engineDir))
            {
                error = $"转换引擎目录不存在：{engineDir}";
                return null;
            }

            var enc = Path.Combine(engineDir, "heif-enc.exe");
            if (!File.Exists(enc))
            {
                error = $"未找到 heif-enc.exe：{enc}";
                return null;
            }

            return enc;
        }

        // mingw 运行时 DLL 需能被找到。转换走的是 heif-enc 子进程，
        // 由 RunAsync 往子进程 PATH 注入引擎目录即可，无需在此设置。
        //（曾在此调用 SetDllDirectory，是P/Invoke 方案遗留，现已移除。）

        /// <summary>
        /// 转换单个文件。
        /// </summary>
        public static async Task<ConvertResult> ConvertAsync(
            string inputPath,
            ConvertOptions options,
            string engineDir,
            IProgress<string>? log = null,
            CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            HeifFileInfo? info = null;
            var sb = new StringBuilder();

            void Log(string s)
            {
                sb.AppendLine(s);
                log?.Report(s);
            }

            try
            {
                // 1) 先解析源文件，拿到颜色信息与 Gain Map 情况
                info = HeifFileService.Analyze(inputPath, engineDir, ct);
                Log($"源文件：{Path.GetFileName(inputPath)} · {info.Width}×{info.Height} · " +
                    $"{info.BitDepth}bit · {info.Color}");
                Log(info.HasGainMap
                    ? $"检测到 Gain Map：元数据 item {info.GainMapMetaItem?.Id} + " +
                      $"图像 item {info.GainMapImageItem?.Id}（将保留）"
                    : "未检测到 Gain Map");

                if (options.PreserveColorProfile && info.Width > 0)
                {
                    Log($"保留色彩描述：primaries={info.Color.Primaries} " +
                        $"transfer={info.Color.Transfer} matrix={info.Color.Matrix}");
                }

                // 2) 准备引擎
                var heifEnc = PrepareEngine(engineDir, out var prepErr);
                if (heifEnc == null)
                {
                    Log("引擎准备失败：" + prepErr);
                    return new ConvertResult
                    {
                        Success = false, InputPath = inputPath, Error = prepErr,
                        Log = sb.ToString(), SourceInfo = info,
                        Elapsed = sw.Elapsed
                    };
                }

                // 3) 组装命令
                var outDir = string.IsNullOrWhiteSpace(options.OutputDirectory)
                    ? Path.GetDirectoryName(inputPath)!
                    : options.OutputDirectory!;
                Directory.CreateDirectory(outDir);

                var outPath = Path.Combine(
                    outDir,
                    Path.GetFileNameWithoutExtension(inputPath) + options.OutputExtension);

                if (File.Exists(outPath) && !options.Overwrite)
                {
                    // ★ 与主通路（FullPipeline）行为保持一致：输出已存在且未勾选覆盖 = 跳过，
                    //   不是失败。此前这里按「失败」返回，导致批量汇总出现假失败
                    //   （实测 485 张批次里 3 张直转文件被误报失败，用户无从快速分辨）。
                    var msg = "已跳过：同名文件已存在（勾选「覆盖同名文件」可覆盖）";
                    Log($"      {msg}");
                    return new ConvertResult
                    {
                        Success = false, Skipped = true, InputPath = inputPath, Error = msg,
                        Log = sb.ToString(), SourceInfo = info, OutputPath = outPath,
                        Elapsed = sw.Elapsed
                    };
                }

                // ★ 不要手工给路径加引号。
                // 参数经 ProcessStartInfo.ArgumentList 传递，.NET 会按 Windows
                // 命令行规则自动转义 —— 手工再包一层引号，引号会被当作路径的
                // 一部分传进 heif-enc，导致 "Cannot open JPEG file"。
                // （实测：'"a b.avif"' 退出码 1，'a b.avif' 退出码 0，同一文件）
                var args = new List<string>
                {
                    inputPath,
                    "-o", outPath
                };

                if (options.Lossless)
                {
                    args.Add("-L");
                }
                else
                {
                    int q = Math.Clamp(options.Quality ?? 90, 1, 100);
                    args.Add("-q");
                    args.Add(q.ToString());
                }

                // 关键：不指定就会把 P3/PQ 降级成 BT.709/sRGB。
                // 但仅在源文件确实有 nclx 数值描述时才传——苹果用 ICC 配置文件，
                // 此时 primaries/transfer/matrix 全为 0，照传会让 heif-enc 失败。
                if (options.PreserveColorProfile &&
                    info.Width > 0 &&
                    info.Color.ProfileKind == ColorInfo.ColorProfileKind.Nclx)
                {
                    args.Add("--colour_primaries");
                    args.Add(info.Color.Primaries.ToString());
                    args.Add("--transfer_characteristic");
                    args.Add(info.Color.Transfer.ToString());
                    args.Add("--matrix_coefficients");
                    args.Add(info.Color.Matrix.ToString());
                }
                else if (options.PreserveColorProfile && info.Width > 0)
                {
                    Log("源文件使用 ICC 配置文件（非 nclx），"
                        + "由 heif-enc 直接搬运ICC，不覆盖色彩参数");
                }

                // ★ 尺寸预检：HEVC Level 7.2 硬性上限 142,606,336 个亮度采样，
                //   超出时 x265 编码器直接拒绝（19008×12672 = 2.4 亿，超 69%）。
                //   在这里提前判掉，能省下「先解码 AV1 再失败」的几十秒无用功，
                //   并且把原因讲清楚，而不是抛一个笼统的「退出码 1」。
                const long HevcLumaSampleLimit = 142_606_336L;
                if (info.Width > 0 && info.Height > 0)
                {
                    long luma = (long)info.Width * info.Height;
                    if (luma > HevcLumaSampleLimit)
                    {
                        var msg = $"图片尺寸超出 HEVC 编码上限：" +
                                  $"{info.Width}×{info.Height} = {luma:N0} 个亮度采样，" +
                                  $"上限 {HevcLumaSampleLimit:N0}（HEVC Level 7.2）。" +
                                  $"高出 {luma * 100.0 / HevcLumaSampleLimit - 100:0.#}% —— " +
                                  $"HEVC 格式本身装不下，与软件无关。" +
                                  $"如需转换请先裁切或缩放到约 {Math.Floor(Math.Sqrt(
                                      HevcLumaSampleLimit * (double)info.Width / info.Height))}×" +
                                  $"{Math.Floor(Math.Sqrt(
                                      HevcLumaSampleLimit * (double)info.Height / info.Width))} 以内。";
                        Log("✗ " + msg);
                        return new ConvertResult
                        {
                            Success = false, InputPath = inputPath, Error = msg,
                            Log = sb.ToString(), SourceInfo = info,
                            Elapsed = sw.Elapsed
                        };
                    }
                }

                // 色度采样：heif-enc 透传给编码器，参数名不带 x265: 前缀
                // （带前缀会报 "Unsupported x265 encoder parameter: chroma"）。
                // 仅当非默认 4:2:0 时才传——420 是引擎默认，传了属于多余参数。
                // 注意：无损模式下 chroma 由 -L 决定，不叠加，避免参数冲突。
                var chroma = string.IsNullOrWhiteSpace(options.Chroma)
                    ? "420" : options.Chroma.Trim();
                if (!options.Lossless && chroma != "420")
                {
                    args.Add("-p");
                    args.Add($"chroma={chroma}");
                }
                Log($"色度采样：{chroma}" + (options.Lossless ? "（无损模式由 -L 决定）" : ""));

                // x265 preset：慢速 preset 体积更小，但编码时间显著增加。
                if (!options.Lossless && !string.IsNullOrWhiteSpace(options.Preset))
                {
                    args.Add("-p");
                    args.Add($"preset={options.Preset.Trim()}");
                    Log($"x265 preset：{options.Preset.Trim()}");
                }

                if (!string.IsNullOrWhiteSpace(options.ExtraEncoderArgs))
                    args.Add(options.ExtraEncoderArgs);

                Log($"执行：heif-enc {string.Join(' ', args.Select(QuoteSafe))}");

                // 4) 执行
                //   ★ heif-enc 有个坑：它**失败时也返回退出码 0**
                //   （实测「Image too large for x265」时退出码为 0），
                //   所以不能只看退出码，必须同时收集输出、检查产物是否存在。
                var lastErrLine = "";
                var exit = await RunAsync(heifEnc, args, p =>
                {
                    if (p.StartsWith("Could not", StringComparison.OrdinalIgnoreCase) ||
                        p.StartsWith("Error", StringComparison.OrdinalIgnoreCase) ||
                        p.Contains("problem", StringComparison.OrdinalIgnoreCase) ||
                        p.Contains("unsupported", StringComparison.OrdinalIgnoreCase))
                        lastErrLine = p;
                    Log(p);
                }, ct).ConfigureAwait(false);

                if (exit != 0 || !File.Exists(outPath))
                {
                    // 把引擎的原话带出来，而不是只说「退出码 N」——
                    // 之前信息丢失，用户排不出是尺寸超限还是别的原因。
                    var detail = string.IsNullOrWhiteSpace(lastErrLine)
                        ? $"（引擎无错误输出，退出码 {exit}）"
                        : lastErrLine;
                    var msg = $"转换失败：{detail}";
                    Log("✗ " + msg);
                    // 失败时清掉可能的半截产物，避免用户误以为转换成功。
                    try { if (File.Exists(outPath)) File.Delete(outPath); } catch { /* 删不掉就算了 */ }
                    return new ConvertResult
                    {
                        Success = false, InputPath = inputPath, OutputPath = outPath,
                        Error = msg, Log = sb.ToString(), SourceInfo = info,
                        Elapsed = sw.Elapsed
                    };
                }

                var outSize = new FileInfo(outPath).Length;

                // ★ 产物有效性校验：heif-enc 偶尔会「报了错还留个空壳」，
                //   只判 File.Exists 会把空文件当成成功。这里核对最小结构：
                //   ISO-BMFF 至少要有 ftyp box，且首 4 字节是大端长度（≥8）。
                if (outSize < 32 || !LooksLikeIsoBmff(outPath))
                {
                    var msg = $"转换失败：引擎产出的文件不是有效图片（{outSize} 字节）" +
                              (string.IsNullOrWhiteSpace(lastErrLine) ? "" : $"：{lastErrLine}");
                    Log("✗ " + msg);
                    try { File.Delete(outPath); } catch { /* 删不掉就算了 */ }
                    return new ConvertResult
                    {
                        Success = false, InputPath = inputPath, OutputPath = outPath,
                        Error = msg, Log = sb.ToString(), SourceInfo = info,
                        Elapsed = sw.Elapsed
                    };
                }

                Log($"主图转换完成：{outSize / 1024.0 / 1024.0:0.##} MB，" +
                    $"{sw.Elapsed.TotalSeconds:0.#} 秒");

                // 5) 若源文件有 Gain Map，做容器重组把它搬回新文件
                bool gmDone = false;
                if (options.PreserveGainMap && info?.HasGainMap == true)
                {
                    Log("正在重组容器以保留 Gain Map ...");
                    var gmRes = MergeGainMap(inputPath, outPath, engineDir, options, Log);

                    if (gmRes.Success)
                    {
                        gmDone = true;
                        outSize = new FileInfo(outPath).Length;
                        Log($"Gain Map 已保留（{gmRes.Layout} 布局，" +
                            $"新增 {gmRes.ItemsAdded} 个 item，" +
                            $"{gmRes.BytesAppended / 1024.0:0.#} KB）");
                    }
                    else
                    {
                        Log("⚠ Gain Map 保留失败：" + gmRes.Error);
                        Log("输出文件仍可用，但缺少 Gain Map（动态范围可能变窄）");
                    }
                }

                return new ConvertResult
                {
                    Success = true,
                    InputPath = inputPath,
                    OutputPath = outPath,
                    Log = sb.ToString(),
                    SourceInfo = info,
                    Elapsed = sw.Elapsed,
                    OutputSize = outSize,
                    GainMapPreserved = gmDone,
                };
            }
            catch (OperationCanceledException)
            {
                Log("已取消");
                return new ConvertResult
                {
                    Success = false, InputPath = inputPath, Error = "已取消",
                    Log = sb.ToString(), SourceInfo = info, Elapsed = sw.Elapsed
                };
            }
            catch (Exception ex)
            {
                Log("异常：" + ex.Message);
                return new ConvertResult
                {
                    Success = false, InputPath = inputPath, Error = ex.Message,
                    Log = sb.ToString(), SourceInfo = info, Elapsed = sw.Elapsed
                };
            }
        }

        /// <summary>
        /// 把源文件的 Gain Map 搬运到已转换的新文件中。
        /// 步骤：提取 Gain Map item → 重建目标容器的 iinf/ipco/ipma/iloc/iref。
        /// </summary>
        /// <summary>
        /// 把 <paramref name="srcPath"/> 里的 Gain Map 合并进已经转好的
        /// <paramref name="dstPath"/>（原地改写）。
        /// 供「先用别的工具生成目标 HEIC、再补增益图」的场景使用。
        /// </summary>
        internal static ContainerMerger.Result MergeGainMapInto(
            string srcPath, string dstPath, string engineDir, ConvertOptions options,
            Action<string> log)
            => MergeGainMap(srcPath, dstPath, engineDir, options, log);

        private static ContainerMerger.Result MergeGainMap(
            string srcPath, string dstPath, string engineDir, ConvertOptions options,
            Action<string> log)
        {
            try
            {
                var srcBytes = File.ReadAllBytes(srcPath);
                var dstBytes = File.ReadAllBytes(dstPath);

                // 防护：目标文件若已含 tmap item，说明它已经被注入过增益图，
                // 再注入一次会产生「双份 tmap / 双份增益图」，读取器行为不可预期。
                if (ContainsItemType(dstBytes, "tmap"))
                    return new ContainerMerger.Result
                    {
                        Success = false,
                        Error = "目标文件已含 tmap item（增益图已注入过）。" +
                                "请从干净的基底重新合并，不要在结果上重复注入。",
                        OutPath = dstPath
                    };

                var extractor = new GainMapExtractor(srcBytes, engineDir);
                var gm = extractor.Extract();

                if (!gm.HasGainMap)
                    return new ContainerMerger.Result
                    {
                        Success = false, Error = "源文件无 Gain Map", OutPath = dstPath
                    };
                if (!gm.Complete)
                    return new ContainerMerger.Result
                    {
                        Success = false, Error = "Gain Map 数据提取不完整", OutPath = dstPath
                    };

                // 源 ipco 属性 box → 切成字节（必须从 srcBytes 切，不能用目标文件）
                var srcReader = new IsoBmffReader(srcBytes);
                var srcPropBytes = srcReader.ParsePropertyBoxes()
                    .Select(b => SliceFrom(srcBytes, b))
                    .ToList();

                // 可选：把增益图重编码为 HEVC，使整份文件"全 HEVC"（苹果同款）
                if (options.GainMapMode == GainMapCodec.Hevc && gm.ImageItemId != 0)
                {
                    log($"增益图转 HEVC(4:4:4){(options.GainMapLossless ? "·无损" : $"，质量 {options.GainMapQuality}")} ...");
                    var tr = GainMapHevcTranscoder.Transcode(
                        srcBytes, gm.ImageItemId, engineDir,
                        options.GainMapQuality, options.GainMapLossless, log);
                    if (tr == null)
                    {
                        log("⚠ 增益图转 HEVC 失败，回退为原样保留（零损失）");
                    }
                    else
                    {
                        gm = ReplaceGainMapWithHevc(gm, tr, srcPropBytes);
                        log($"增益图已转为 HEVC：{tr.Width}×{tr.Height} · {tr.BitDepth}bit · " +
                            $"{tr.ItemData.Length / 1024.0:0.#} KB");
                    }
                }

                // 可选：用外部 HEIC 的图 item 替换增益图（自己重算增益图的路线）
                if (!string.IsNullOrWhiteSpace(options.GainMapItemFromHeic) && gm.ImageItemId != 0)
                {
                    var ext = LoadExternalImageItem(options.GainMapItemFromHeic!, engineDir);
                    if (ext == null)
                    {
                        log("⚠ 外部增益图读取失败，保留原增益图");
                    }
                    else
                    {
                        gm = ReplaceGainMapWithExternal(gm, ext, srcPropBytes);
                        log($"增益图已替换为外部 item：{ext.Data.Length / 1024.0:0.#} KB " +
                            $"（类型 {ext.Type}）");
                    }
                }

                // 可选：替换 tmap 元数据
                if (!string.IsNullOrWhiteSpace(options.GainMapMetadataFile) &&
                    File.Exists(options.GainMapMetadataFile))
                {
                    var meta = File.ReadAllBytes(options.GainMapMetadataFile!);
                    if (meta.Length > 0 && ReplaceTmapData(gm, meta))
                        log($"tmap 元数据已替换：{meta.Length} 字节");
                    else
                        log("⚠ tmap 元数据替换失败");
                }

                // 可选：给增益图附加 Apple 风格 XMP（HDRGainMapVersion + HDRGainMapHeadroom）
                if (options.GainMapHeadroom > 0 && gm.ImageItemId != 0)
                {
                    uint xmpId = gm.Items.Max(x => x.Id) + 1;
                    var xmp = BuildAppleGainMapXmp(options.GainMapHeadroom);
                    gm.Items.Add(new GainMapExtractor.ExtractedItem
                    {
                        Id = xmpId,
                        Type = "mime",
                        Name = "",
                        Hidden = true,
                        Data = xmp,
                        WasInIdat = false,
                        ContentType = "application/rdf+xml",
                        PropertyIndices = new List<int>(),
                    });
                    log($"已为增益图附加 XMP：HDRGainMapHeadroom = {options.GainMapHeadroom:0.######} " +
                        $"（{xmp.Length} 字节）");
                }

                // 可选：从源文件搬运主图的元数据 item（Exif / XMP）。
                // 新管线的 heif-enc 输入是自算的 PNG，把这些一起搬过去，
                // 否则输出文件的 Exif/XMP 会全部丢失（实测丢失 Exif 880B + XMP 6467B）。
                if (options.CopySourceMetadata)
                {
                    var metaItems = extractor.ExtractMetadataItems();
                    int added = 0;
                    long bytes = 0;
                    foreach (var mi in metaItems)
                    {
                        if (gm.Items.Any(x => x.Id == mi.Id)) continue;
                        gm.Items.Add(mi);
                        added++; bytes += mi.Data.Length;
                    }
                    if (added > 0)
                        log($"已从源文件搬运 {added} 个元数据 item（Exif/XMP，共 {bytes / 1024.0:0.#} KB）");
                    else
                        log("源文件无 Exif/XMP 元数据需要搬运");
                }

                var merger = new ContainerMerger(dstBytes);
                return merger.Merge(dstPath, gm, srcPropBytes);
            }
            catch (Exception ex)
            {
                return new ContainerMerger.Result
                {
                    Success = false, Error = ex.Message, OutPath = dstPath
                };
            }
        }

        /// <summary>目标文件里是否已存在指定类型的 item（用于防重复注入）。</summary>
        private static bool ContainsItemType(byte[] bytes, string itemType)
        {
            try
            {
                var r = new IsoBmffReader(bytes);
                return r.ParseItems().Any(i => i.Type == itemType);
            }
            catch { return false; }
        }

        /// <summary>
        /// 生成 Apple 风格的增益图 XMP。
        /// 格式取自苹果原生 IMG_3836.HEIC 的增益图 XMP（131072 = iOS 18 版本号）。
        /// </summary>
        private static byte[] BuildAppleGainMapXmp(double headroom)
        {
            string text =
                "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\" x:xmptk=\"XMP Core 6.0.0\">\n" +
                "   <rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n" +
                "      <rdf:Description rdf:about=\"\"\n" +
                "            xmlns:HDRGainMap=\"http://ns.apple.com/HDRGainMap/1.0/\">\n" +
                "         <HDRGainMap:HDRGainMapVersion>131072</HDRGainMap:HDRGainMapVersion>\n" +
                "         <HDRGainMap:HDRGainMapHeadroom>" +
                headroom.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) +
                "</HDRGainMap:HDRGainMapHeadroom>\n" +
                "      </rdf:Description>\n" +
                "   </rdf:RDF>\n" +
                "</x:xmpmeta>\n";
            return Encoding.UTF8.GetBytes(text);
        }

        /// <summary>从外部文件读出的一个图像 item（数据 + 属性），用于替换增益图。</summary>
        private sealed class ExternalImageItem
        {
            public string Type { get; init; } = "";
            public byte[] Data { get; init; } = Array.Empty<byte>();
            public List<byte[]> PropertyBoxes { get; init; } = new();
            public List<int> PropertyIndices { get; init; } = new();
        }

        /// <summary>
        /// 读取外部 HEIC/AVIF 的**主图 item**（含其 ipco 属性 box）。
        /// 用于把「自己重算并单独编码的增益图」注入到目标文件。
        /// </summary>
        private static ExternalImageItem? LoadExternalImageItem(string path, string engineDir)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var r = new IsoBmffReader(bytes);
                uint primary = r.ParsePrimaryItemId();
                if (primary == 0) return null;

                var items = r.ParseItems();
                // 注意：items 是元组列表（值类型），FirstOrDefault 返回 default 元组而非 null，
                // 其 Type 字段即为 null，故下面的 ?? "" 是必要且充分的 —— 不需要额外的空判断。
                string type = items.FirstOrDefault(i => i.Id == primary).Type ?? "";
                if (string.IsNullOrWhiteSpace(type)) return null;

                var locator = HeifInfoLocator.Locate(engineDir, bytes);
                if (locator == null || !locator.TryGetValue(primary, out var loc)) return null;

                var idat = r.FindInMeta("idat");
                long idatBase = idat?.PayloadOffset ?? 0;
                var data = new List<byte>();
                foreach (var (off, len) in loc.Extents)
                {
                    if (len <= 0) continue;
                    long start = loc.Method == 1 ? idatBase + loc.BaseOffset + off
                                                 : loc.BaseOffset + off;
                    if (start < 0 || start + len > bytes.Length) return null;
                    var chunk = new byte[len];
                    Array.Copy(bytes, start, chunk, 0, (int)len);
                    data.AddRange(chunk);
                }
                if (data.Count == 0) return null;

                var props = r.ParsePropertyBoxes();
                var assoc = r.ParsePropertyAssociations();
                var boxes = new List<byte[]>();
                var indices = new List<int>();
                if (assoc.TryGetValue(primary, out var idxs))
                {
                    foreach (var raw in idxs)
                    {
                        int real = raw & 0xFFFF;
                        if (real < 1 || real > props.Count) continue;
                        var box = props[real - 1];
                        var buf = new byte[box.Size];
                        Array.Copy(bytes, box.Offset, buf, 0, (int)box.Size);
                        boxes.Add(buf);
                        indices.Add(boxes.Count | (raw & 0x10000));
                    }
                }

                return new ExternalImageItem
                {
                    Type = type,
                    Data = data.ToArray(),
                    PropertyBoxes = boxes,
                    PropertyIndices = indices,
                };
            }
            catch { return null; }
        }

        /// <summary>把增益图 item 换成外部提供的那个（含属性），并丢弃原 grid 的 tile。</summary>
        private static GainMapExtractor.Result ReplaceGainMapWithExternal(
            GainMapExtractor.Result gm, ExternalImageItem ext, List<byte[]> srcPropBytes)
        {
            int baseIdx = srcPropBytes.Count;
            foreach (var pb in ext.PropertyBoxes) srcPropBytes.Add(pb);

            var newIndices = new List<int>();
            foreach (var idx in ext.PropertyIndices)
                newIndices.Add(((idx & 0xFFFF) + baseIdx) | (idx & 0x10000));

            var tiles = new HashSet<uint>(gm.TileIds);
            var items = new List<GainMapExtractor.ExtractedItem>();
            foreach (var it in gm.Items)
            {
                if (tiles.Contains(it.Id)) continue;
                if (it.Id == gm.ImageItemId)
                {
                    items.Add(new GainMapExtractor.ExtractedItem
                    {
                        Id = it.Id,
                        Type = ext.Type,
                        Name = it.Name,
                        Hidden = true,
                        Data = ext.Data,
                        WasInIdat = false,
                        PropertyIndices = newIndices,
                    });
                }
                else items.Add(it);
            }

            return new GainMapExtractor.Result
            {
                HasGainMap = true,
                Complete = true,
                MetaItemId = gm.MetaItemId,
                ImageItemId = gm.ImageItemId,
                TileIds = new List<uint>(),
                Items = items,
                LayoutKind = gm.LayoutKind + " + 外部重算增益图",
            };
        }

        /// <summary>替换 tmap item 的数据（ISO 21496-1 元数据字节）。</summary>
        private static bool ReplaceTmapData(GainMapExtractor.Result gm, byte[] meta)
        {
            int i = gm.Items.FindIndex(x => x.Id == gm.MetaItemId);
            if (i < 0) return false;
            var old = gm.Items[i];
            gm.Items[i] = new GainMapExtractor.ExtractedItem
            {
                Id = old.Id,
                Type = old.Type,
                Name = old.Name,
                Hidden = old.Hidden,
                Data = meta,
                WasInIdat = false,
                PropertyIndices = old.PropertyIndices,
            };
            return true;
        }

        /// <summary>
        /// 用 HEVC 增益图替换掉原来的（AV1）增益图 item：
        /// 换 item 类型、数据与属性，并丢掉原 grid 的 tile。
        /// HEVC 属性 box 追加到 srcPropBytes 尾部，索引同步平移。
        /// </summary>
        private static GainMapExtractor.Result ReplaceGainMapWithHevc(
            GainMapExtractor.Result gm, GainMapHevcTranscoder.Result tr,
            List<byte[]> srcPropBytes)
        {
            int baseIdx = srcPropBytes.Count;
            foreach (var pb in tr.PropertyBoxes) srcPropBytes.Add(pb);

            var newIndices = new List<int>();
            foreach (var idx in tr.PropertyIndices)
            {
                int real = (idx & 0xFFFF) + baseIdx;      // 指向追加到尾部的属性
                newIndices.Add(real | (idx & 0x10000));   // 保留 essential 标志
            }

            var tiles = new HashSet<uint>(gm.TileIds);
            var items = new List<GainMapExtractor.ExtractedItem>();
            foreach (var it in gm.Items)
            {
                if (tiles.Contains(it.Id)) continue;      // 原 grid 的 tile 全部丢弃
                if (it.Id == gm.ImageItemId)
                {
                    items.Add(new GainMapExtractor.ExtractedItem
                    {
                        Id = it.Id,
                        Type = "hvc1",
                        Name = it.Name,
                        Hidden = true,
                        Data = tr.ItemData,
                        WasInIdat = false,
                        PropertyIndices = newIndices,
                    });
                }
                else items.Add(it);
            }

            return new GainMapExtractor.Result
            {
                HasGainMap = true,
                Complete = true,
                MetaItemId = gm.MetaItemId,
                ImageItemId = gm.ImageItemId,
                TileIds = new List<uint>(),
                Items = items,
                LayoutKind = gm.LayoutKind + " + HEVC 增益图",
            };
        }

        /// <summary>从指定字节数组中切出 box 的完整字节。</summary>
        private static byte[] SliceFrom(byte[] src, IsoBmffReader.Box b)
        {
            var buf = new byte[b.Size];
            Array.Copy(src, b.Offset, buf, 0, (int)b.Size);
            return buf;
        }

        /// <summary>运行外部进程并逐行回传输出。</summary>
        private static async Task<int> RunAsync(
            string exe, List<string> args, Action<string> onOutput, CancellationToken ct)
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

            // 让子进程也能找到 mingw DLL
            var dir = Path.GetDirectoryName(exe)!;
            var existing = psi.Environment.TryGetValue("PATH", out var old) ? old : "";
            psi.Environment["PATH"] = dir + ";" + existing;

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            var outTask = Task.Run(() => ReadAllAsync(proc.StandardOutput, onOutput, ct), ct);
            var errTask = Task.Run(() => ReadAllAsync(proc.StandardError, onOutput, ct), ct);

            try
            {
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { /* 已退出则忽略 */ }
                throw;
            }

            await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
            return proc.ExitCode;
        }

        private static async Task ReadAllAsync(StreamReader reader, Action<string> cb, CancellationToken ct)
        {
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line)) cb(line);
            }
        }

        /// <summary>
        /// 判断文件是否真的是 ISO-BMFF（HEIF/AVIF 容器）。
        /// 只看头部 16 字节：第 5-8 字节必须是 "ftyp"。
        /// </summary>
        private static bool LooksLikeIsoBmff(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                Span<byte> head = stackalloc byte[16];
                int n = fs.Read(head);
                if (n < 8) return false;
                // ISO-BMFF 首 box 必须是 ftyp
                return head[4] == 'f' && head[5] == 't' && head[6] == 'y' && head[7] == 'p';
            }
            catch { return false; }
        }

        // 日志展示用：把整条命令行拼成可读字符串。
        // 注意这里只影响日志外观，真正传给进程的是 ArgumentList 里的原始值。
        private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;
        private static string QuoteSafe(string s) => s;
    }
}