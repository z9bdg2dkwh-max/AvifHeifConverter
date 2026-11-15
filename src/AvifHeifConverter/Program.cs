using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AvifHeifConverter.Models;
using AvifHeifConverter.Services;

namespace AvifHeifConverter
{
    /// <summary>
    /// 命令行核心：用于在打包 WinUI 界面之前，独立验证
    /// 「文件解析 → Gain Map 识别 → 转换 → 输出校验」全链路。
    ///
    /// 用法：
    ///   AvifHeifConverter.Core.exe --engine &lt;引擎目录&gt; inspect &lt;文件...&gt;
    ///   AvifHeifConverter.Core.exe --engine &lt;引擎目录&gt; convert &lt;文件...&gt; [-o 目录] [-q 90] [--heif] [--overwrite]
    /// </summary>
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (args.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            string? engineDir = null;
            var positional = new List<string>();
            var opt = new ConvertOptions();
            string command = "";

            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                switch (a)
                {
                    case "--engine":
                        if (++i < args.Length) engineDir = args[i];
                        break;
                    case "-o":
                        if (++i < args.Length) opt.OutputDirectory = args[i];
                        break;
                    case "-q":
                        if (++i < args.Length && int.TryParse(args[i], out var q)) opt.Quality = q;
                        break;
                    case "--heif":
                        opt.OutputExtension = ".heif";
                        break;
                    case "--heic":
                        opt.OutputExtension = ".heic";
                        break;
                    case "--lossless":
                        opt.Lossless = true;
                        break;
                    case "--overwrite":
                        opt.Overwrite = true;
                        break;
                    case "--chroma":
                        if (++i < args.Length) opt.Chroma = args[i];
                        break;
                    case "--preset":
                        if (++i < args.Length) opt.Preset = args[i];
                        break;
                    case "--gain-cap":
                        if (++i < args.Length &&
                            double.TryParse(args[i], System.Globalization.NumberStyles.Float,
                                            System.Globalization.CultureInfo.InvariantCulture, out var gc))
                            opt.GainCap = gc;
                        break;
                    case "--gain-quality":
                        if (++i < args.Length && int.TryParse(args[i], out var gqArg)) opt.GainQuality = gqArg;
                        break;
                    case "--no-preserve-color":
                        opt.PreserveColorProfile = false;
                        break;
                    case "--gm-hevc":
                        // 增益图重编码为 HEVC(4:4:4)：整份文件全 HEVC，兼容性最好
                        opt.GainMapMode = GainMapCodec.Hevc;
                        break;
                    case "--gm-lossless":
                        opt.GainMapMode = GainMapCodec.Hevc;
                        opt.GainMapLossless = true;
                        break;
                    case "--gm-quality":
                        if (++i < args.Length && int.TryParse(args[i], out var gq))
                        {
                            opt.GainMapMode = GainMapCodec.Hevc;
                            opt.GainMapQuality = gq;
                        }
                        break;
                    case "--gm-item-from":
                        // 用外部 HEIC 的主图 item 替换增益图（自己重算增益图的路线）
                        if (++i < args.Length) opt.GainMapItemFromHeic = args[i];
                        break;
                    case "--gm-meta":
                        // 替换 tmap item 的 ISO 21496-1 元数据
                        if (++i < args.Length) opt.GainMapMetadataFile = args[i];
                        break;
                    case "--gm-headroom":
                        // 给增益图附加 Apple 风格 XMP（线性提亮倍数）
                        if (++i < args.Length &&
                            double.TryParse(args[i], System.Globalization.NumberStyles.Float,
                                            System.Globalization.CultureInfo.InvariantCulture,
                                            out var hr))
                            opt.GainMapHeadroom = hr;
                        break;
                    case "-h":
                    case "--help":
                        PrintUsage();
                        return 0;
                    default:
                        if (command == "") command = a;
                        else positional.Add(a);
                        break;
                }
            }

            if (engineDir == null)
            {
                Console.Error.WriteLine("错误：必须用 --engine 指定转换引擎目录");
                return 1;
            }
            if (positional.Count == 0)
            {
                Console.Error.WriteLine("错误：未指定文件");
                return 1;
            }

            // 批量：也支持传目录
            var files = new List<string>();
            foreach (var p in positional)
            {
                if (Directory.Exists(p))
                    files.AddRange(Directory.GetFiles(p, "*.avif",
                                SearchOption.TopDirectoryOnly)
                                .Concat(Directory.GetFiles(p, "*.heic",
                                SearchOption.TopDirectoryOnly)));
                else if (File.Exists(p))
                    files.Add(p);
                else
                    Console.Error.WriteLine($"跳过（不存在）：{p}");
            }

            if (files.Count == 0)
            {
                Console.Error.WriteLine("错误：没有可处理的文件");
                return 1;
            }

            // 属性读取依赖引擎目录下的 heif-info，这里先做一次可用性检查
            var heifInfo = Path.Combine(engineDir, "heif-info.exe");
            if (!File.Exists(heifInfo))
                Console.Error.WriteLine($"警告：未找到 {heifInfo}，" +
                                        "尺寸/位深/色彩可能无法读取（Gain Map 判定不受影响）。");

            return command switch
            {
                "inspect" => await InspectAsync(files, engineDir).ConfigureAwait(false),
                "gainmap" => GainMapReport(files, engineDir),
                "brand" => BrandOnly(files),
                "diff" => DiffCmd(files),
                "convert" => await ConvertAllAsync(files, engineDir, opt).ConfigureAwait(false),
                "convert-hdr" => await ConvertHdrAsync(files, engineDir, opt).ConfigureAwait(false),
                "meminfo" => MemInfoCmd(files),
                "tiffinfo" => TiffInfoCmd(files),
                "tiff2heic" => await Tiff2HeicCmd(files, engineDir, opt).ConfigureAwait(false),
                "tiffdump" => TiffDumpCmd(files),
                "tile" => await TileCmd(files, engineDir, opt).ConfigureAwait(false),
                "meta" => MetaCmd(files, engineDir),
                "merge" => MergeCmd(files, engineDir, opt),
                _ => UnknownCommand(command),
            };
        }

        /// <summary>
        /// tile：把一张 PNG 直接编码成 **grid 分块** 的 HEIC（调试/验证用，
        /// 也是 convert-hdr 处理超过 HEVC 单张上限的大图时所走的那条路径）。
        /// 用法：tile &lt;输入PNG&gt; [-o 输出HEIC]
        /// </summary>
        private static async Task<int> TileCmd(
            List<string> files, string engineDir, ConvertOptions opt)
        {
            if (files.Count < 1)
            {
                Console.Error.WriteLine("用法：tile <输入PNG> [-o 输出HEIC]");
                return 1;
            }
            var src = files[0];
            var outPath = string.IsNullOrWhiteSpace(opt.OutputDirectory)
                ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(src))!,
                               Path.GetFileNameWithoutExtension(src) + ".tiled.heic")
                : Path.Combine(opt.OutputDirectory,
                               Path.GetFileNameWithoutExtension(src) + ".tiled.heic");

            var (w, h, ch, data) = Services.PngCodec.Read16(src);
            Console.WriteLine($"输入 {w}×{h}，{ch} 通道");
            var rgb = new byte[(long)w * h * 3 > int.MaxValue ? 0 : w * h * 3];
            for (int i = 0; i < w * h; i++)
                for (int c = 0; c < 3; c++)
                    rgb[i * 3 + c] = (byte)(data[i * ch + Math.Min(c, ch - 1)] >> 8);

            string tmp = Path.Combine(Path.GetTempPath(), "tilebuild_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                var r = await Services.TiledHeicBuilder.BuildAsync(
                    rgb, w, h, outPath, engineDir, tmp, opt.Quality ?? 92, Console.WriteLine)
                    .ConfigureAwait(false);
                if (!r.Ok) { Console.Error.WriteLine("✗ " + r.Error); return 1; }
                Console.WriteLine($"✓ {outPath}（{new FileInfo(outPath).Length / 1048576.0:0.##} MB，" +
                                  $"{r.Rows}行×{r.Cols}列，每块 {r.TileSize}）");
                return 0;
            }
            finally
            {
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            }
        }

        /// <summary>
        /// meta：打印源文件的增益图元数据与关键色彩信息（诊断用）。
        /// 用法：meta &lt;文件&gt;
        /// </summary>
        private static int MetaCmd(List<string> files, string engineDir)
        {
            if (files.Count < 1) { Console.Error.WriteLine("用法：meta <文件>"); return 1; }
            foreach (var f in files)
            {
                Console.WriteLine(new string('-', 64));
                Console.WriteLine($"■ {Path.GetFileName(f)}");
                var b = File.ReadAllBytes(f);
                var r = new Services.IsoBmffReader(b);

                // 主图色彩（nclx 的传递函数决定后续该走 PQ 还是 SDR 通路）
                var items = r.ParseItems();
                uint primary = r.ParsePrimaryItemId();
                var boxes = r.ParsePropertyBoxes();
                var assoc = r.ParsePropertyAssociations();
                if (assoc.TryGetValue(primary, out var idxs))
                {
                    foreach (var raw in idxs)
                    {
                        int i = raw & 0xFFFF;
                        if (i < 1 || i > boxes.Count) continue;
                        var box = boxes[i - 1];
                        var slice = new byte[box.Size];
                        Array.Copy(b, box.Offset, slice, 0, (int)box.Size);
                        var rr = new Services.IsoBmffReader(slice);
                        var nclx = rr.ReadNclx(rr.FindTopLevel("colr") ?? default);
                        if (nclx != null)
                        {
                            var c = nclx.Value;
                            Console.WriteLine($"  主图 colr: primaries={c.Primaries} transfer={c.Transfer} " +
                                $"matrix={c.Matrix}");
                            Console.WriteLine($"    → 传递函数 {(c.Transfer == 16 ? "PQ（HDR 通路）" :
                                c.Transfer == 18 ? "HLG（HDR 通路，BT.2100）" :
                                "SDR（base_headroom=0 时走直通通路）")}");
                        }
                    }
                }

                // tmap 元数据
                var gm = new Services.GainMapExtractor(b, engineDir).Extract();
                if (!gm.HasGainMap) { Console.WriteLine("  无增益图"); continue; }
                Console.WriteLine($"  tmap item={gm.MetaItemId}  增益图 item={gm.ImageItemId}  " +
                                  $"tile={gm.TileIds.Count}");
                var tmapBytes = gm.Items.FirstOrDefault(x => x.Id == gm.MetaItemId)?.Data;
                if (tmapBytes == null || tmapBytes.Length == 0) { Console.WriteLine("  tmap 数据为空"); continue; }
                Console.WriteLine($"  tmap {tmapBytes.Length} 字节: {BitConverter.ToString(tmapBytes, 0, Math.Min(22, tmapBytes.Length)).Replace("-", " ")}");
                try
                {
                    var md = Services.GainMapMetadata.Parse(tmapBytes);
                    Console.WriteLine($"    multichannel={md.Multichannel} use_base_cs={md.UseBaseColourSpace} " +
                                      $"backward={md.BackwardDirection}");
                    Console.WriteLine($"    base_headroom={md.BaseHdrHeadroom.N / (double)md.BaseHdrHeadroom.D:0.####}  " +
                                      $"alt_headroom={md.AltHdrHeadroom.N / (double)md.AltHdrHeadroom.D:0.####}");
                    for (int i = 0; i < md.Channels.Count; i++)
                    {
                        var c = md.Channels[i];
                        double mn = c.Min.N / (double)c.Min.D, mx = c.Max.N / (double)c.Max.D;
                        double gm2 = c.Gamma.N / (double)c.Gamma.D;
                        Console.WriteLine($"    通道{i}: min={mn:0.####} max={mx:0.####} gamma={gm2:0.####}" +
                                          $"  → 增益 {Math.Pow(2, mn):0.###}~{Math.Pow(2, mx):0.##} 倍");
                    }
                }
                catch (Exception ex) { Console.WriteLine("    元数据解析失败：" + ex.Message); }
            }
            return 0;
        }

        /// <summary>
        /// meminfo：显示系统内存，以及「某张图预计需要多少内存、现在能不能放进来」。
        /// 既是诊断工具，也是并行准入逻辑的验证入口。
        /// </summary>
        private static int MemInfoCmd(List<string> files)
        {
            var mi = Services.MemoryGuard.Query();
            Console.WriteLine($"系统内存：总 {Services.MemoryGuard.Format(mi.Total)}，" +
                              $"可用 {Services.MemoryGuard.Format(mi.Available)}，" +
                              $"已用 {mi.UsedPercent:0.#}%");
            Console.WriteLine($"必须保留：{Services.MemoryGuard.Format((long)(mi.Total * Services.MemoryGuard.ReserveRatio))}" +
                              $"（总内存的 {Services.MemoryGuard.ReserveRatio:P0}）");
            foreach (var f in files)
            {
                long? px = Services.Av1GridDecoder.TryProbePixels(f);
                if (px == null)
                {
                    Console.WriteLine($"  {Path.GetFileName(f)}：尺寸探测失败（将按保守值估算）");
                    continue;
                }
                long need = Services.MemoryGuard.EstimateFor(px.Value);
                bool ok = Services.MemoryGuard.CanAdmit(need, out var now);
                Console.WriteLine($"  {Path.GetFileName(f)}：{px.Value / 1e6:0.#} MP → " +
                                  $"预计需要 {Services.MemoryGuard.Format(need)} → " +
                                  (ok ? "现在可以放进来" : "内存不足，需等待") +
                                  $"（当前可用 {Services.MemoryGuard.Format(now.Available)}）");
            }
            return 0;
        }


        /// <summary>
        /// tiffinfo：读出 TIFF 的完整格式信息（尺寸/位深/压缩/预测器/布局/ICC），
        /// 并试读像素。用于接入新格式前的格式确认 —— 不靠猜。
        /// </summary>
        private static int TiffInfoCmd(List<string> files)
        {
            foreach (var f in files)
            {
                Console.WriteLine($"■ {Path.GetFileName(f)}");
                var info = Services.TiffReader.Probe(f);
                if (info == null) { Console.WriteLine("  ✗ 不是可识别的 TIFF"); continue; }
                Console.WriteLine($"  {info.Summary}");
                Console.WriteLine($"  ICC 配置：{(info.IccProfile != null ? info.IccProfile.Length + " 字节" : "无")}");
                if (info.IccProfile != null) Services.IccColor.FromIcc(info.IccProfile, m => Console.WriteLine(m));
                if (!string.IsNullOrEmpty(info.IccDescription))
                    Console.WriteLine($"  ICC 名称：{info.IccDescription}");
                if (!string.IsNullOrEmpty(info.IccTrcKind))
                    Console.WriteLine($"  传输曲线：{info.IccTrcKind}");
                var r = Services.TiffReader.Read(f);
                if (r == null) { Console.WriteLine("  ✗ 像素读取失败（可能用了暂不支持的压缩/采样格式）"); continue; }
                var px = r.Value.Pixels;
                int stride = Math.Max(1, px.Length / 4096);
                ushort mn = 65535, mx = 0; long sum = 0, cnt = 0;
                for (int i = 0; i < px.Length; i += stride) { var v = px[i]; if (v < mn) mn = v; if (v > mx) mx = v; sum += v; cnt++; }
                Console.WriteLine($"  ✓ 像素可读：{px.Length / 1024 / 1024} M 样本，取值 {mn}~{mx}，均值 {(cnt > 0 ? sum / cnt : 0)}" +
                                  $"（16bit 满量程 0~65535）");
            }
            return 0;
        }


        /// <summary>tiffdump：把读取器解出的像素原样写成 .bin（供与外部实现逐像素比对）。</summary>
        private static int TiffDumpCmd(List<string> files)
        {
            foreach (var f in files)
            {
                var r = Services.TiffReader.Read(f);
                if (r == null) { Console.Error.WriteLine($"  ✗ 读取失败：{f}"); return 1; }
                var px = r.Value.Pixels;
                var buf = new byte[px.Length * 2];
                Buffer.BlockCopy(px, 0, buf, 0, buf.Length);
                string outPath = Path.ChangeExtension(f, ".dump.bin");
                File.WriteAllBytes(outPath, buf);
                Console.WriteLine($"  ✓ {Path.GetFileName(f)} → {Path.GetFileName(outPath)}" +
                                  $"（{r.Value.Info.Width}×{r.Value.Info.Height}×{r.Value.Info.Samples}，{px.Length} 样本）");
            }
            return 0;
        }


        /// <summary>
        /// tiff2heic：把（Lightroom 导出的）TIFF 编成 HEIC。
        ///
        /// 色彩**从 ICC 读出**（原色与传输曲线），不靠猜：
        ///   · 传输曲线为 sRGB/BT.709/线性 → SDR 路径（本命令）
        ///   · 若为 PQ/HLG（HDR TIFF）→ 需走 HDR 通路，当前会明确拒绝而非硬编
        ///
        /// 像素来源是自研 TiffReader（见该文件说明：heif-enc 读得了 TIFF 但给不了像素）。
        /// </summary>
        private static async Task<int> Tiff2HeicCmd(List<string> files, string? engineDir, ConvertOptions opt)
        {
            if (string.IsNullOrEmpty(engineDir)) { Console.Error.WriteLine("错误：必须用 --engine 指定引擎目录"); return 1; }

            // ★ 内存感知的自动并行 —— 与界面同一套判据：
            //   放得下才启动，且放下后系统仍要剩总内存的 10%。
            //   再加 CPU 上限，避免余量充足时几十张一起抢 CPU。
            //   （此前是纯串行：3591 个文件约 6 小时，并行后约 1 小时）
            int cpuCap = Math.Max(2, Environment.ProcessorCount / 2);
            var mi0 = Services.MemoryGuard.Query();
            Console.WriteLine($"并行策略：自动（按内存余量）  系统内存 总 {Services.MemoryGuard.Format(mi0.Total)}，" +
                              $"可用 {Services.MemoryGuard.Format(mi0.Available)}，" +
                              $"必须保留 {Services.MemoryGuard.Format((long)(mi0.Total * Services.MemoryGuard.ReserveRatio))}（10%）");

            int ok = 0, fail = 0, skip = 0, done = 0, inFlight = 0;
            var tasks = new List<Task>();

            foreach (var f in files)
            {
                // 估算本张所需内存（只读 IFD，毫秒级）
                long need;
                var probe = Services.TiffReader.Probe(f);
                need = probe != null && probe.Width > 0 && probe.Height > 0
                     ? Services.MemoryGuard.EstimateFor((long)probe.Width * probe.Height)
                     : (1L << 31);

                // 内存闸门
                bool announced = false;
                while (true)
                {
                    int flying = Volatile.Read(ref inFlight);
                    bool memOk = Services.MemoryGuard.CanAdmit(need, out var mn);
                    if (flying < cpuCap && memOk) break;
                    // 逃生通道：已无任务在跑却仍不够，就不能再等（否则永远轮不上）
                    if (flying == 0)
                    {
                        Console.Error.WriteLine($"  ⚠ 内存余量不足（可用 {Services.MemoryGuard.Format(mn.Available)}，" +
                                                $"本张需 {Services.MemoryGuard.Format(need)}），按单张执行");
                        break;
                    }
                    if (!announced)
                    {
                        Console.WriteLine($"  ⏳ 等待资源：并行 {flying} 张，可用 {Services.MemoryGuard.Format(mn.Available)}");
                        announced = true;
                    }
                    await Task.Delay(300).ConfigureAwait(false);
                }

                Interlocked.Increment(ref inFlight);
                string path = f;
                var t = Task.Run(async () =>
                {
                    try
                    {
                        string outDir = !string.IsNullOrWhiteSpace(opt.OutputDirectory) ? opt.OutputDirectory!
                            : (Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
                        string outPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(path) + ".heic");
                        var (success, err, skipped) = await Services.TiffToHeic.RunAsync(
                            path,
                            new Services.TiffToHeic.Options
                            {
                                OutputPath = outPath,
                                Quality = opt.Quality ?? 92,
                                Chroma = opt.Chroma,
                                Preset = opt.Preset,
                                Overwrite = opt.Overwrite,
                            },
                            engineDir, null).ConfigureAwait(false);
                        int n = Interlocked.Increment(ref done);
                        if (success)
                        { Interlocked.Increment(ref ok); Console.WriteLine($"  [{n}/{files.Count}] ✓ {Path.GetFileName(path)}"); }
                        else if (skipped)
                        { Interlocked.Increment(ref skip); Console.WriteLine($"  [{n}/{files.Count}] ○ 跳过：{Path.GetFileName(path)}"); }
                        else
                        { Interlocked.Increment(ref fail); Console.Error.WriteLine($"  [{n}/{files.Count}] ✗ {Path.GetFileName(path)}：{err}"); }
                    }
                    finally { Interlocked.Decrement(ref inFlight); }
                });
                tasks.Add(t);
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);

            Console.WriteLine($"=== 完成：成功 {ok}，失败 {fail}" +
                              (skip > 0 ? $"，跳过 {skip}" : "") + $"，共 {files.Count} 张 ===");
            return fail == 0 ? 0 : 1;
        }


        private static int UnknownCommand(string cmd)
        {
            Console.Error.WriteLine($"未知命令：{cmd}（可用：inspect / convert / convert-hdr / merge）");
            PrintUsage();
            return 1;
        }

        /// <summary>
        /// convert-hdr：完整 HDR 转换（苹果配置）。
        /// AVIF → HEIC，主图转 8bit SDR 基底 + 单通道增益图 + Apple 风格 XMP。
        /// 已实测：iPhone「照片」可正常打开并显示 HDR。
        /// </summary>
        private static async Task<int> ConvertHdrAsync(
            List<string> files, string engineDir, ConvertOptions opt)
        {
            int okCount = 0, failCount = 0, skipCount = 0;
            foreach (var f in files)
            {
                Console.WriteLine(new string('-', 70));
                Console.WriteLine($"■ {Path.GetFileName(f)}");
                var outDir = string.IsNullOrWhiteSpace(opt.OutputDirectory)
                    ? Path.GetDirectoryName(f)! : opt.OutputDirectory!;
                var outPath = Path.Combine(outDir,
                    Path.GetFileNameWithoutExtension(f) + opt.OutputExtension);

                var (ok, err, _, skippedFlag) = await Services.FullPipeline.RunAsync(
                    f,
                    new Services.FullPipeline.Options
                    {
                        OutputPath = outPath,
                        Quality = opt.Quality ?? 92,
                        GainQuality = opt.GainQuality ?? 90,
                        GainCap = opt.GainCap ?? Services.HdrMath.DefaultGainCap,
                        Chroma = opt.Chroma,
                        Preset = opt.Preset,
                        // ★ 必须传下去 —— 漏传时管线内的同名检查会拦住一切，
                        //   表现为「加了 --overwrite 还是跳过」。
                        Overwrite = opt.Overwrite,
                    },
                    engineDir, Console.WriteLine).ConfigureAwait(false);

                if (ok) okCount++;
                else if (skippedFlag) { Console.Error.WriteLine($"  ○ {err}"); skipCount++; }
                else { Console.Error.WriteLine($"  ✗ 失败：{err}"); failCount++; }

                // 每张之间做一次带压缩的回收：大对象堆默认不压缩，
                // 连续转大图时内存只增不减，最终拖垮机器。
                Services.FullPipeline.CollectLargeObjects();
            }
            Console.WriteLine(new string('-', 70));
            Console.WriteLine($"=== 完成：成功 {okCount}，失败 {failCount}" + (skipCount > 0 ? $"，跳过 {skipCount}" : "") + $"，共 {files.Count} ===");
            return failCount == 0 ? 0 : 1;
        }

        /// <summary>
        /// merge &lt;含增益图的源文件&gt; &lt;目标 HEIC&gt;
        /// 把源文件里的 Gain Map 合并进已有 HEIC（原地改写该 HEIC）。
        /// 用于「HEIC 已由别的工具生成」的场景，例如先做 8bit 主图、
        /// 再把源文件的增益图补进去。
        /// </summary>
        private static int MergeCmd(List<string> files, string engineDir, ConvertOptions opt)
        {
            if (files.Count != 2)
            {
                Console.Error.WriteLine("用法：AvifHeifConverter.Core.exe --engine <引擎目录> " +
                                        "merge <含增益图的源文件> <目标 HEIC>");
                return 1;
            }
            var src = files[0];
            var dst = files[1];
            if (!File.Exists(src)) { Console.Error.WriteLine($"源文件不存在：{src}"); return 1; }
            if (!File.Exists(dst)) { Console.Error.WriteLine($"目标文件不存在：{dst}"); return 1; }

            Console.WriteLine($"源（提供增益图）：{src}");
            Console.WriteLine($"目标（就地改写）：{dst}");
            Console.WriteLine(new string('-', 70));

            var r = Services.ConversionEngine.MergeGainMapInto(
                src, dst, engineDir, opt, s => Console.WriteLine("    " + s));

            if (!r.Success)
            {
                Console.Error.WriteLine("✗ 合并失败：" + r.Error);
                return 1;
            }
            Console.WriteLine($"✓ 完成：新增 {r.ItemsAdded} 个 item，" +
                              $"{r.BytesAppended / 1024.0:0.#} KB（布局：{r.Layout}）");
            return 0;
        }

        /// <summary>inspect：只解析不转换，用于验证 Gain Map 识别是否正确。</summary>
        private static async Task<int> InspectAsync(List<string> files, string engineDir)
        {
            Console.WriteLine("=== 文件结构检查 ===");
            int gainMapCount = 0;

            foreach (var f in files)
            {
                try
                {
                    var info = await HeifFileService.AnalyzeAsync(f, engineDir).ConfigureAwait(false);
                    if (info.HasGainMap) gainMapCount++;

                    Console.WriteLine();
                    Console.WriteLine($"■ {Path.GetFileName(f)}");
                    Console.WriteLine($"    路径      : {f}");
                    Console.WriteLine($"    大小      : {info.FileSize / 1024.0 / 1024.0:0.##} MB");
                    Console.WriteLine($"    尺寸      : {info.Width} × {info.Height}" +
                                      (info.IsGrid ? "（网格分块）" : ""));
                    Console.WriteLine($"    位深      : {info.BitDepth} bit");
                    Console.WriteLine($"    色彩      : {info.Color}");
                    Console.WriteLine($"    HDR       : {(info.Color.IsHdr ? "是" : "否")}" +
                                      (info.MaxCLL != null ? $"  MaxCLL={info.MaxCLL} nits" : "") +
                                      (info.MaxPALL != null ? $"  MaxPALL={info.MaxPALL}" : ""));
                    Console.WriteLine($"    主图 item : {info.PrimaryItem}");
                    Console.WriteLine($"    Gain Map  : {(info.HasGainMap ? "★ 有" : "无")}");
                    if (info.HasGainMap)
                    {
                        Console.WriteLine($"       ├ 元数据 item : {info.GainMapMetaItem}");
                        Console.WriteLine($"       └ 图像   item : {info.GainMapImageItem}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine();
                    Console.WriteLine($"■ {Path.GetFileName(f)}");
                    Console.WriteLine($"    解析失败：{ex.Message}");
                }
            }

            Console.WriteLine();
            Console.WriteLine($"=== 汇总：{files.Count} 个文件，其中 {gainMapCount} 个含 Gain Map ===");
            return 0;
        }

        private static int DiffCmd(List<string> files)
        {
            if (files.Count < 2)
            {
                Console.Error.WriteLine("用法: diff <正常文件> <问题文件>");
                return 1;
            }
            Console.WriteLine(Services.BoxDiff.Compare(files[0], files[1]));
            return 0;
        }

        /// <summary>
        /// brand：方案 A —— 只改 ftyp 品牌（avif → heic），数据零改动。
        /// 输出到源文件同目录，文件名加 _heic 后缀。
        /// </summary>
        private static int BrandOnly(List<string> files)
        {
            Console.WriteLine("=== 品牌改写模式（数据零改动）===");
            Console.WriteLine("原理：AVIF 与 HEIF 同容器，仅 ftyp 品牌不同。");
            Console.WriteLine("      把 major_brand 由 avif 改为 heic，图像数据不动，");
            Console.WriteLine("      因此 Gain Map / Exif / XMP / 色彩描述全部原样保留。");
            Console.WriteLine();

            int ok = 0, fail = 0;
            foreach (var f in files)
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(f))!;
                var stem = Path.GetFileNameWithoutExtension(f);
                var ext = Path.GetExtension(f);
                var dst = Path.Combine(dir, stem + "_heic" + ext);

                Console.WriteLine($"■ {Path.GetFileName(f)}");
                var r = Services.BrandPatcher.PatchAvifToHeic(f, dst);

                if (!r.Success)
                {
                    fail++;
                    Console.WriteLine($"    ✗ {r.Error}");
                    continue;
                }

                var si = new FileInfo(f);
                var di = new FileInfo(dst);
                Console.WriteLine($"    ✓ {r.OldBrand} → {r.NewBrand}");
                Console.WriteLine($"    输出：{dst}");
                Console.WriteLine($"    大小：{di.Length:N0} B（与源文件相同：{di.Length == si.Length}）");

                ok++;
            }

            Console.WriteLine();
            Console.WriteLine($"=== 完成：成功 {ok}，失败 {fail} ===");
            Console.WriteLine("提示：请在 iPhone 上打开输出文件验证是否可正常显示 HDR。");
            return fail == 0 ? 0 : 1;
        }

        /// <summary>
        /// gainmap：导出 Gain Map 提取详情，用于验证「字节级复制」是否拿全了数据。
        /// </summary>
        private static int GainMapReport(List<string> files, string engineDir)
        {
            Console.WriteLine("=== Gain Map 提取检查 ===");

            foreach (var f in files)
            {
                Console.WriteLine();
                Console.WriteLine($"■ {Path.GetFileName(f)}");

                var bytes = File.ReadAllBytes(f);
                var ex = new Services.GainMapExtractor(bytes, engineDir);
                var r = ex.Extract();

                if (!r.HasGainMap)
                {
                    Console.WriteLine("    无 Gain Map");
                    continue;
                }

                Console.WriteLine($"    tmap 元数据 item = {r.MetaItemId}");
                Console.WriteLine($"    增益图本体 item = {r.ImageItemId}" +
                                  $"（{r.TileIds.Count} 个 tile）");
                Console.WriteLine($"    提取状态   = {(r.Complete ? "完整" : "不完整")}");
                Console.WriteLine($"    需复制属性 = {r.NeededProperties.Count}");
                Console.WriteLine();

                long total = 0;
                foreach (var it in r.Items.OrderBy(x => x.Id))
                {
                    total += it.Data.Length;
                    var where = it.WasInIdat ? "idat" : "mdat";
                    Console.WriteLine($"    item {it.Id,-4} type={it.Type,-5} " +
                                      $"len={it.Data.Length,9:N0} B  @{where,-5} " +
                                      $"hidden={it.Hidden} name='{it.Name}' " +
                                      $"props=[{string.Join(",", it.PropertyIndices)}]");
                }
                Console.WriteLine($"    ── 合计 {total:N0} 字节 " +
                                  $"({total / 1024.0 / 1024.0:0.##} MB)");

                // 打印 tmap 元数据的头部字节供人工核对
                var meta = r.Items.FirstOrDefault(x => x.Id == r.MetaItemId);
                if (meta != null && meta.Data.Length > 0)
                {
                    var head = meta.Data.Take(24).ToArray();
                    Console.WriteLine($"    tmap 头部: {BitConverter.ToString(head)}");
                }
            }
            return 0;
        }

        /// <summary>
        /// convert：批量转换并逐个校验输出。
        /// </summary>
        private static async Task<int> ConvertAllAsync(
            List<string> files, string engineDir, ConvertOptions opt)
        {
            var heifEnc = ConversionEngine.PrepareEngine(engineDir, out var err);
            if (heifEnc == null)
            {
                Console.Error.WriteLine("引擎准备失败：" + err);
                return 2;
            }
            Console.WriteLine($"引擎：{heifEnc}");
            Console.WriteLine($"输出：{opt.OutputExtension}  " +
                              $"质量={(opt.Lossless ? "无损" : opt.Quality?.ToString())}  " +
                              $"色彩保留={(opt.PreserveColorProfile ? "是" : "否")}");
            Console.WriteLine(new string('-', 70));

            int ok = 0, fail = 0;
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            foreach (var f in files)
            {
                var name = Path.GetFileName(f);
                Console.WriteLine();
                Console.WriteLine($"■ {name}");

                var r = await ConversionEngine.ConvertAsync(
                    f, opt, engineDir,
                    new Progress<string>(s => Console.WriteLine($"    {s}")),
                    cts.Token)
                    .ConfigureAwait(false);

                if (!r.Success)
                {
                    fail++;
                    Console.WriteLine($"    ✗ 失败：{r.Error}");
                    continue;
                }

                ok++;
                Console.WriteLine($"    ✓ 输出：{r.OutputPath}");

                // 转换后校验
                try
                {
                    var outInfo = await HeifFileService.AnalyzeAsync(r.OutputPath!, engineDir).ConfigureAwait(false);
                    Console.WriteLine($"    校验：{outInfo.Width}×{outInfo.Height} · " +
                                      $"{outInfo.BitDepth}bit · {outInfo.Color}");

                    if (r.SourceInfo != null)
                    {
                        var same = outInfo.Width == r.SourceInfo.Width
                                   && outInfo.Height == r.SourceInfo.Height;
                        Console.WriteLine($"    尺寸一致：{(same ? "是" : "否")}" +
                                          $"（源 {r.SourceInfo.Width}×{r.SourceInfo.Height}）");

                        if (opt.PreserveColorProfile)
                        {
                            var colorSame =
                                outInfo.Color.Primaries == r.SourceInfo.Color.Primaries &&
                                outInfo.Color.Transfer == r.SourceInfo.Color.Transfer;
                            Console.WriteLine($"    色彩一致：{(colorSame ? "是" : "否")}" +
                                              $"（源 primaries={r.SourceInfo.Color.Primaries} " +
                                              $"transfer={r.SourceInfo.Color.Transfer}）");
                        }

                        Console.WriteLine($"    Gain Map：源=" +
                                          $"{(r.SourceInfo.HasGainMap ? "有" : "无")} → " +
                                          $"输出={(outInfo.HasGainMap ? "有" : "无")}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    校验失败：{ex.Message}");
                }
            }

            Console.WriteLine();
            Console.WriteLine(new string('-', 70));
            Console.WriteLine($"=== 完成：成功 {ok}，失败 {fail}，共 {files.Count} ===");
            return fail == 0 ? 0 : 1;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("AVIF → HEIF/HEIC 转换器（命令行核心）");
            Console.WriteLine();
            Console.WriteLine("用法:");
            Console.WriteLine("  AvifHeifConverter.Core.exe --engine <引擎目录> <命令> <文件...> [选项]");
            Console.WriteLine();
            Console.WriteLine("命令:");
            Console.WriteLine("  inspect    只解析文件结构，报告 Gain Map 等信息（不转换）");
            Console.WriteLine("  convert    转换为 HEIF/HEIC");
            Console.WriteLine();
            Console.WriteLine("选项:");
            Console.WriteLine("  --engine <目录>       转换引擎目录（含 heif-enc.exe 与 DLL），必填");
            Console.WriteLine("  -o <目录>             输出目录，默认与源文件同目录");
            Console.WriteLine("  -q <0-100>            质量，默认 90");
            Console.WriteLine("  --lossless无损编码（体积大）");
            Console.WriteLine("  --heif / --heic       输出扩展名，默认 .heic");
            Console.WriteLine("  --overwrite           覆盖同名文件");
            Console.WriteLine("  --no-preserve-color   不保留原始色彩描述（会降级为 BT.709/sRGB）");
            Console.WriteLine();
            Console.WriteLine("说明：");
            Console.WriteLine("  文件参数可传单个文件、通配目录。目录内自动匹配 *.avif / *.heic。");
        }
    }
}