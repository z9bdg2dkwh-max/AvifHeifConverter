using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AvifHeifConverter.Services
{
    /// <summary>
    /// 直接用 dav1d 解码 AVIF 主图，绕开 heif-dec 的 PNG 输出。
    ///
    /// 动机（实测）：
    ///   heif-dec 写一张 60MP 的 16bit PNG 要 deflate 压缩 361 MB，耗时 **59.7 秒**；
    ///   若输出不压缩的 TIFF 只要 2.1 秒 —— 说明**慢的不是解码，是 PNG 压缩**。
    ///   而 heif-dec 的其他输出格式都不可用（TIFF 有 bug：每行只写 62.5%；
    ///   Y4M 是 8bit 4:2:0，丢精度又丢色度；其余有损）。
    ///
    /// 做法：
    ///   ① 从 AVIF 里按 iloc 提取各 AV1 分块的 OBU 数据
    ///   ② 每个分块单独交给 dav1d（--muxer yuv，原始 YUV 不压缩），可并行
    ///   ③ 自行做 BT.601 全范围 YCbCr→RGB，再按网格拼装
    ///
    /// 正确性（已逐位验证）：
    ///   与 heif-dec 的 PNG 通路比对，R/G/B 三通道 **最大差值 0、完全一致占比 100%**。
    ///
    /// ★ YCbCr→RGB 的 G 通道最易写错（本人踩过）：
    ///     G = Y - [2(1-kb)kb/kg]·Cb' - [2(1-kr)kr/kg]·Cr'
    ///   若两项对调，现象极具迷惑性 —— R、B 仍逐位正确，只有 G 全错，
    ///   整体一致率恰好停在 **66.667%（2/3）**。
    /// </summary>
    internal static class Av1GridDecoder
    {
        public sealed class Result
        {
            public int W, H, Channels;
            public ushort[] Data = Array.Empty<ushort>();
        }

        // ---------------- 对外入口 ----------------

        /// <summary>
        /// 尝试直解。返回 null 表示「这种形态没被支持」，调用方应回退 PNG 通路。
        /// 不抛异常（除取消外），任何解析/解码异常都转为 null + 日志。
        /// </summary>
        public static async Task<Result?> TryDecodeAsync(
            string avifPath, string engineDir, ushort matrixCoefficients, bool fullRange,
            Action<string> log, CancellationToken ct)
        {
            string dav1d = Path.Combine(engineDir, "dav1d.exe");
            if (!File.Exists(dav1d)) { log("      直解跳过：引擎内无 dav1d.exe"); return null; }
            if (matrixCoefficients is not (1 or 5 or 6 or 9 or 10))
            { log($"      直解跳过：矩阵系数 {matrixCoefficients} 未支持"); return null; }
            if (!fullRange)
            { log("      直解跳过：非全范围（需额外换算）"); return null; }

            byte[] file;
            try { file = File.ReadAllBytes(avifPath); }
            catch { return null; }

            List<Item> items;
            Dictionary<uint, List<(long Off, long Len)>> iloc;
            List<(string Type, uint From, List<uint> To)> irefs;
            // 三个解析器分别捕获 —— 合在一起时出错说不清是哪一步。
            try { items = ParseItems(file); }
            catch (Exception ex) { log($"      直解跳过：iinf 解析失败（{ex.Message}）"); return null; }
            try { iloc = ParseIloc(file); }
            catch (Exception ex) { log($"      直解跳过：iloc 解析失败（{ex.Message}）"); return null; }
            try { irefs = ParseIref(file); }
            catch (Exception ex) { log($"      直解跳过：iref 解析失败（{ex.Message}）"); return null; }

            // 主图：优先 grid；否则单个 av01
            uint primary = items.FirstOrDefault(i => i.Type == "grid" && i.Name == "Color")?.Id
                        ?? items.FirstOrDefault(i => i.Type == "grid")?.Id
                        ?? 0;

            uint gridId = primary;
            List<uint> tiles;
            int outW, outH, cols, rows;

            if (gridId != 0)
            {
                var g = items.First(x => x.Id == gridId);
                if (!iloc.TryGetValue(gridId, out var gext) || gext.Count == 0)
                { log("      直解跳过：grid 无 iloc 记录"); return null; }
                var gd = ParseGridDescriptor(file, gext[0].Off, gext[0].Len);
                if (gd == null) { log("      直解跳过：grid 描述符无法解析"); return null; }
                (rows, cols, outW, outH) = gd.Value;

                tiles = irefs.Where(r => r.Type == "dimg" && r.From == gridId)
                             .SelectMany(r => r.To).ToList();
                if (tiles.Count == 0) { log("      直解跳过：grid 无 dimg 分块"); return null; }

                // ★ 实测：索尼机内 AVIF 的 dimg 比实际分块数多出**首项**一个坏值
                //   （9504×6336 / 1920×1600 应为 5×4=20 块，却给了 21 项，
                //   且首项为重复 ID）。首项不是网格左上角，实测第 2 项才是。
                //   这里按「取末尾 rows*cols 项」校正。
                int need = rows * cols;
                if (tiles.Count > need)
                    tiles = tiles.Skip(tiles.Count - need).ToList();
                if (tiles.Count != need)
                { log($"      直解跳过：分块数 {tiles.Count} 与网格 {rows}×{cols}={need} 不符"); return null; }
            }
            else
            {
                var single = items.FirstOrDefault(i => i.Type == "av01" && i.Name == "Color");
                if (single == null) { log("      直解跳过：找不到主图 av01 项"); return null; }
                var one = items.First(x => x.Id == single.Id);
                gridId = one.Id;
                tiles = new List<uint> { one.Id };
                rows = cols = 1;
                outW = outH = 0;   // 由解码结果确定
            }

            log($"      直解模式：{(rows * cols > 1 ? $"{rows}行×{cols}列网格" : "单张")}，" +
                $"共 {tiles.Count} 个 AV1 分块");

            // 各分块的数据切片
            var blobs = new List<byte[]>();
            foreach (var tid in tiles)
            {
                if (!iloc.TryGetValue(tid, out var exts) || exts.Count == 0) return null;
                long total = exts.Sum(e => e.Len);
                if (total <= 0 || total > 256L * 1024 * 1024) return null;
                var buf = new byte[total];
                long p = 0;
                foreach (var (o, l) in exts)
                {
                    if (o < 0 || l <= 0 || o + l > file.Length) return null;
                    Array.Copy(file, o, buf, p, l);
                    p += l;
                }
                blobs.Add(buf);
            }

            // ★ 分块尺寸与位深从 `ispe` / `pixi` 属性读 ——
            //   不能靠 dav1d 的输出（实测它的信息行只有帧率，没有尺寸），
            //   也不能靠文件长度反推（宽高比未知，4160×6240 这种竖幅会算错）。
            var propMap = ParsePropertyMap(file);
            var geo = ReadItemGeometry(file, tiles[0], propMap);
            if (geo == null) { log("      直解跳过：读不到分块尺寸（ispe/pixi）"); return null; }
            int tileW = geo.Value.W, tileH = geo.Value.H;
            int depthGeo = geo.Value.Depths.Length > 0 ? geo.Value.Depths[0] : 10;
            if (depthGeo is not (8 or 10 or 12 or 16))
            { log($"      直解跳过：位深 {depthGeo} 未支持"); return null; }
            if ((long)tileW * tileH > 64L * 1024 * 1024)
            { log($"      直解跳过：分块尺寸异常 {tileW}×{tileH}"); return null; }

            // ---- 尺寸与换算系数（必须在并行循环之前准备好）----
            int tw = tileW, th = tileH, depth0 = depthGeo;
            if (outW == 0) { outW = tw; outH = th; }
            if (outW <= 0 || outH <= 0) return null;
            if ((long)outW * outH > int.MaxValue / 3) return null;

            long per444 = (long)tw * th * 3 * (depth0 > 8 ? 2 : 1);   // 4:4:4 的分块字节数
            int shift = depth0 >= 16 ? 0 : 16 - depth0;
            int maxv = (1 << depth0) - 1;
            int mid = 1 << (depth0 - 1);
            var (kr, kb) = MatrixCoefficients(matrixCoefficients);
            double kg = 1 - kr - kb;
            double cR = 2 * (1 - kr), cB = 2 * (1 - kb);
            double gCb = 2 * (1 - kb) * kb / kg, gCr = 2 * (1 - kr) * kr / kg;
            bool is16 = depth0 > 8;
            int pc0 = tw * th;

            // ★ 边解码边换算：不保存所有分块的 YUV。
            //   238MP / 294 块时，全留会多占 1.4 GB（实测峰值一度到 6 GB）；
            //   每块换算完即释放，峰值只与并发度有关。
            var outData = new ushort[(long)outW * outH * 3];
            int degree = Math.Clamp(Environment.ProcessorCount - 2, 2, 16);
            string? err = null;
            int done = 0;
            var work = Path.Combine(Path.GetTempPath(), "avifdav1d_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);

            try
            {
                var popts = new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = ct };
                await Parallel.ForEachAsync(Enumerable.Range(0, blobs.Count), popts, async (i, token) =>
                {
                    string op = Path.Combine(work, $"t{i}.obu");
                    string yp = Path.Combine(work, $"t{i}.yuv");
                    File.WriteAllBytes(op, blobs[i]);
                    var r = await RunDav1dAsync(dav1d, op, yp, token).ConfigureAwait(false);
                    File.Delete(op);
                    if (!r.Ok) { lock (work) err ??= r.Error; return; }
                    try
                    {
                        var yuv = File.ReadAllBytes(yp);
                        File.Delete(yp);
                        // 长度必须正好等于 4:4:4 的预期值；不等说明是 4:2:0 等本版本不支持的形态
                        if (yuv.LongLength != per444)
                        { lock (work) err ??= $"分块不是 4:4:4（期望 {per444} 字节，实得 {yuv.LongLength}）"; return; }

                        ConvertTile(yuv, outData, (i / cols) * th, (i % cols) * tw,
                                    tw, th, outW, outH, depth0, shift, maxv, mid,
                                    cR, cB, gCb, gCr, is16, token);
                        Interlocked.Increment(ref done);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { lock (work) err ??= ex.Message; }
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            finally
            {
                try { Directory.Delete(work, true); } catch { }
            }

            if (err != null) { log($"      直解失败（{err}），回退 PNG 通路"); return null; }
            if (done != blobs.Count) { log("      直解有分块未完成，回退 PNG 通路"); return null; }

            log($"      直解完成：{outW}×{outH}，{depth0}bit，" +
                $"BT.{matrixCoefficients switch { 1 => "709", 5 or 6 => "601", 9 or 10 => "2020", _ => "?" }}");
            return new Result { W = outW, H = outH, Channels = 3, Data = outData };
        }

        /// <summary>
        /// 把一块 dav1d 输出的 4:4:4 YUV 换算成 RGB 并写入整图缓冲。
        /// 各分块写入互不重叠的区域，可并行调用。
        /// </summary>
        private static void ConvertTile(byte[] yuv, ushort[] outData,
            int r0, int c0, int w, int h, int outW, int outH, int depth,
            int shift, int maxv, int mid,
            double cR, double cB, double gCb, double gCr, bool is16, CancellationToken ct)
        {
            int pc = w * h;
            int yEnd = Math.Min(h, outH - r0);
            int xEnd = Math.Min(w, outW - c0);
            for (int y = 0; y < yEnd; y++)
            {
                if ((y & 0xFF) == 0) ct.ThrowIfCancellationRequested();
                int oy = r0 + y;
                for (int x = 0; x < xEnd; x++)
                {
                    double Y, Cb, Cr;
                    if (is16)
                    {
                        int b1 = ((y * w) + x) * 2;
                        int b2 = ((pc + (y * w)) + x) * 2;
                        int b3 = ((pc * 2 + (y * w)) + x) * 2;
                        Y = BinaryPrimitives.ReadUInt16LittleEndian(yuv.AsSpan(b1, 2));
                        Cb = BinaryPrimitives.ReadUInt16LittleEndian(yuv.AsSpan(b2, 2));
                        Cr = BinaryPrimitives.ReadUInt16LittleEndian(yuv.AsSpan(b3, 2));
                    }
                    else
                    {
                        int b = (y * w) + x;
                        Y = yuv[b]; Cb = yuv[pc + b]; Cr = yuv[pc * 2 + b];
                    }
                    Cb -= mid; Cr -= mid;
                    long o = ((long)oy * outW + c0 + x) * 3;
                    outData[o] = ToU16(Y + cR * Cr, maxv, shift);
                    outData[o + 1] = ToU16(Y - gCb * Cb - gCr * Cr, maxv, shift);
                    outData[o + 2] = ToU16(Y + cB * Cb, maxv, shift);
                }
            }
        }

        private static ushort ToU16(double v, int maxv, int shift)
        {
            int iv = (int)Math.Floor(v + 0.5);
            if (iv < 0) iv = 0; else if (iv > maxv) iv = maxv;
            return (ushort)(iv << shift);
        }

        private static (double Kr, double Kb) MatrixCoefficients(ushort mc) => mc switch
        {
            1 => (0.2126, 0.0722),     // BT.709
            9 or 10 => (0.2627, 0.0593), // BT.2020 NCL / CL
            _ => (0.299, 0.114),       // BT.601（5 为 BT.470BG，系数相同）
        };

        // ---------------- dav1d ----------------

        private static async Task<(bool Ok, string Error)> RunDav1dAsync(
            string dav1d, string input, string output, CancellationToken ct)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(dav1d)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(input);
            psi.ArgumentList.Add("-o"); psi.ArgumentList.Add(output);
            psi.ArgumentList.Add("--muxer"); psi.ArgumentList.Add("yuv");
            psi.ArgumentList.Add("--threads"); psi.ArgumentList.Add("4");
            // 不加 --quiet：需要从它的信息行里读回尺寸与位深（输出被捕获，不会打扰用户）

            using var proc = new System.Diagnostics.Process { StartInfo = psi };
            proc.Start();
            using var reg = ct.Register(() => { try { if (!proc.HasExited) proc.Kill(true); } catch { } });

            string so = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            string se = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            if (proc.ExitCode != 0)
                return (false, $"dav1d 退出码 {proc.ExitCode}：{Trunc(se)}");
            if (!File.Exists(output))
                return (false, "dav1d 未产出文件");
            // ★ 尺寸与位深不从这里推断 —— 实测 dav1d 的信息行只有帧率与耗时，
            //   没有尺寸。改由调用方从 ispe / pixi 属性读取（准确且不依赖宽高比）。
            return (true, "");
        }

        private static string Trunc(string s)
        {
            s = s.Trim().Replace('\n', ' ');
            return s.Length > 120 ? s[..120] : s;
        }

        // ---------------- 属性（ispe / pixi）----------------

        /// <summary>
        /// 解析 iprp/ipco + ipma，返回「item → 属性索引列表」。
        /// ipco 里的 box 按出现顺序编号（从 1 开始），ipma 建立 item 到这些编号的关联。
        /// </summary>
        private static Dictionary<uint, List<int>> ParsePropertyMap(byte[] d)
        {
            var map = new Dictionary<uint, List<int>>();
            var loc = Find(d, "meta", "iprp", "ipma");
            if (loc == null) return map;
            var (off, _) = loc.Value;
            int ver = d[(int)off];
            long p = off + 4;
            // ★ ipma 的 entry_count 是 **32 位**（所有版本都是），
            //   按 16 位读会得到 0，进而一个属性都关联不上。
            long count = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan((int)p, 4));
            p += 4;

            for (long i = 0; i < count; i++)
            {
                uint id = ver < 1
                    ? BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan((int)p, 2))
                    : BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan((int)p, 4));
                p += ver < 1 ? 2 : 4;
                int ac = d[(int)p]; p += 1;
                var list = new List<int>();
                for (int k = 0; k < ac; k++)
                {
                    if (ver < 1) { list.Add(d[(int)p] & 0x7F); p += 1; }
                    else
                    {
                        int v = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan((int)p, 2));
                        list.Add(v & 0x7FFF); p += 2;
                    }
                }
                map[id] = list;
            }
            return map;
        }

        /// <summary>按属性索引取该 item 的 ispe（宽高）与 pixi（位深）。</summary>
        private static (int W, int H, int[] Depths)? ReadItemGeometry(
            byte[] d, uint itemId, Dictionary<uint, List<int>> map)
        {
            if (!map.TryGetValue(itemId, out var idxs)) return null;
            var ipco = Find(d, "meta", "iprp", "ipco");
            if (ipco == null) return null;

            var props = new List<(string Type, long Off, long Len)>();
            long p = ipco.Value.Off, end = ipco.Value.End;
            while (p + 8 <= end)
            {
                if (!Box(d, p, out var t, out var pl, out var ln, out var tot)) break;
                props.Add((t, pl, ln));
                if (tot <= 0) break;
                p += tot;
            }

            int w = 0, h = 0;
            int[]? depths = null;
            foreach (var idx in idxs)
            {
                if (idx < 1 || idx > props.Count) continue;
                var (t, po, pl2) = props[idx - 1];
                if (t == "ispe" && pl2 >= 12)
                {
                    w = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan((int)po + 4, 4)) is var ww ? (int)ww : 0;
                    h = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan((int)po + 8, 4));
                }
                else if (t == "pixi" && pl2 >= 6)
                {
                    int n = d[(int)po + 4];
                    if (n is > 0 and < 8 && pl2 >= 5 + n)
                    {
                        depths = new int[n];
                        for (int k = 0; k < n; k++) depths[k] = d[(int)po + 5 + k];
                    }
                }
            }
            if (w <= 0 || h <= 0) return null;
            return (w, h, depths ?? new[] { 10, 10, 10 });
        }

        /// <summary>
        /// 只读容器头，快速探测主图像素数（不解码，毫秒级）。
        /// 供并行转换做内存准入估算用。
        /// 拿不到（结构异常 / 不支持的形态）时返回 null，调用方按保守值处理。
        /// </summary>
        public static long? TryProbePixels(string avifPath)
        {
            try
            {
                var file = File.ReadAllBytes(avifPath);
                var items = ParseItems(file);
                var iloc = ParseIloc(file);
                var irefs = ParseIref(file);

                // 优先用 grid 描述符（里面直接写了输出宽高，最省事）
                var grid = items.FirstOrDefault(i => i.Type == "grid" && i.Name == "Color")
                        ?? items.FirstOrDefault(i => i.Type == "grid");
                if (grid != null && iloc.TryGetValue(grid.Id, out var gext) && gext.Count > 0)
                {
                    var gd = ParseGridDescriptor(file, gext[0].Off, gext[0].Len);
                    if (gd != null) return (long)gd.Value.OutW * gd.Value.OutH;
                }

                // 单张：读它的 ispe
                var single = items.FirstOrDefault(i => i.Type == "av01" && i.Name == "Color")
                          ?? items.FirstOrDefault(i => i.Type == "av01");
                if (single != null)
                {
                    var geo = ReadItemGeometry(file, single.Id, ParsePropertyMap(file));
                    if (geo != null) return (long)geo.Value.W * geo.Value.H;
                }
            }
            catch { /* 探测失败交给调用方兜底 */ }
            return null;
        }

        // ---------------- ISO BMFF 解析 ----------------

        private sealed class Item
        {
            public uint Id;
            public string Type = "";
            public string Name = "";
        }

        private static bool Box(byte[] d, long off, out string type, out long payload, out long len, out long total)
        {
            type = ""; payload = 0; len = 0; total = 0;
            if (off < 0 || off + 8 > d.Length) return false;
            long size = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan((int)off, 4));
            type = Encoding.ASCII.GetString(d, (int)off + 4, 4);
            long hdr = 8;
            if (size == 1)
            {
                if (off + 16 > d.Length) return false;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan((int)off + 8, 8));
                hdr = 16;
            }
            else if (size == 0) size = d.Length - off;
            if (size < hdr || off + size > d.Length) return false;
            payload = off + hdr;
            len = size - hdr;
            total = size;
            return true;
        }

        private static (long Off, long End)? FindChild(byte[] d, long off, long end, string type)
        {
            long p = off;
            while (p + 8 <= end)
            {
                if (!Box(d, p, out var t, out var pl, out var ln, out var tot)) return null;
                if (t == type) return (pl, pl + ln);
                if (tot <= 0) return null;
                p += tot;
            }
            return null;
        }

        /// <summary>按路径查找嵌套 box。meta 是 FullBox，其子盒从载荷 +4 起（这点极易漏）。</summary>
        private static (long Off, long End)? Find(byte[] d, params string[] path)
        {
            long off = 0, end = d.Length;
            foreach (var name in path)
            {
                var r = FindChild(d, off, end, name);
                if (r == null) return null;
                (off, end) = r.Value;
                if (name == "meta") off += 4;   // version + flags
            }
            return (off, end);
        }

        private static List<Item> ParseItems(byte[] d)
        {
            var loc = Find(d, "meta", "iinf");
            if (loc == null) return new List<Item>();
            var (off, end) = loc.Value;
            int ver = d[(int)off];
            long p = off + 4;
            int count;
            if (ver == 0) { count = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan((int)p, 2)); p += 2; }
            else { count = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan((int)p, 4)); p += 4; }

            var list = new List<Item>();
            for (int i = 0; i < count && p + 8 <= end; i++)
            {
                if (!Box(d, p, out var t, out var pl, out var ln, out var tot)) break;
                if (t == "infe")
                {
                    int v = d[(int)pl];
                    uint id = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan((int)pl + 4, 2));
                    // ★ infe 布局：version+flags(4) item_ID(2) item_protection_index(2)
                    //   item_type(4) item_name(...)
                    //   漏掉 item_protection_index 会让类型与名字各错 2 字节。
                    string ty = v >= 2 ? Encoding.ASCII.GetString(d, (int)pl + 8, 4) : "";
                    int no = (int)pl + (v >= 2 ? 12 : 6);
                    int z = Array.IndexOf(d, (byte)0, no);
                    string nm = z > no ? Encoding.UTF8.GetString(d, no, z - no) : "";
                    list.Add(new Item { Id = id, Type = ty, Name = nm });
                }
                if (tot <= 0) break;
                p += tot;
            }
            return list;
        }

        private static Dictionary<uint, List<(long Off, long Len)>> ParseIloc(byte[] d)
        {
            var result = new Dictionary<uint, List<(long, long)>>();
            var loc = Find(d, "meta", "iloc");
            if (loc == null) return result;
            var (off, _) = loc.Value;
            int ver = d[(int)off];
            int offSize = d[(int)off + 4] >> 4;
            int lenSize = d[(int)off + 4] & 0xF;
            int baseSize = d[(int)off + 5] >> 4;
            // ★ iloc 字段顺序：
            //     version(1) flags(3) │ offset_size|length_size(1) │ base_offset_size|index_size(1) │ item_count
            //   计数在 **off+6**，不是 off+4。从 off+4 读会把尺寸字段当成计数，
            //   条目数被读成很大的值 → 循环迅速越界（实测报「iloc 解析失败：索引超出数组边界」）。
            long p = off + 6;
            long count;
            if (ver < 2) { count = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan((int)p, 2)); p += 2; }
            else { count = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan((int)p, 4)); p += 4; }

            for (long i = 0; i < count; i++)
            {
                uint id = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan((int)p, 2)); p += 2;
                if (ver is 1 or 2) p += 2;   // construction_method
                p += 2;                       // data_reference_index
                long bse = ReadInt(d, p, baseSize); p += baseSize;
                int ec = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan((int)p, 2)); p += 2;
                var exts = new List<(long, long)>();
                for (int k = 0; k < ec; k++)
                {
                    long eo = ReadInt(d, p, offSize); p += offSize;
                    long el = ReadInt(d, p, lenSize); p += lenSize;
                    exts.Add((bse + eo, el));
                }
                result[id] = exts;
            }
            return result;
        }

        private static long ReadInt(byte[] d, long p, int n)
        {
            long v = 0;
            for (int i = 0; i < n; i++) v = (v << 8) | d[(int)p + i];
            return v;
        }

        private static List<(string Type, uint From, List<uint> To)> ParseIref(byte[] d)
        {
            var list = new List<(string, uint, List<uint>)>();
            var loc = Find(d, "meta", "iref");
            if (loc == null) return list;
            var (off, end) = loc.Value;
            int ver = d[(int)off];
            long p = off + 4;
            while (p + 8 <= end)
            {
                if (!Box(d, p, out var t, out var pl, out var ln, out var tot)) break;
                if (t.Length != 4 || t.Trim() == "") break;
                if (ver == 0)
                {
                    uint frm = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan((int)pl, 2));
                    var tos = new List<uint>();
                    for (long q = pl + 2; q + 2 <= pl + ln; q += 2)
                        tos.Add(BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan((int)q, 2)));
                    list.Add((t, frm, tos));
                }
                else
                {
                    uint frm = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan((int)pl, 4));
                    var tos = new List<uint>();
                    for (long q = pl + 4; q + 4 <= pl + ln; q += 4)
                        tos.Add(BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan((int)q, 4)));
                    list.Add((t, frm, tos));
                }
                if (tot <= 0) break;
                p += tot;
            }
            return list;
        }

        /// <summary>
        /// grid 描述符（HEIF 定义，共 8 字节）：
        ///   version(1) flags(1) rows-1(1) cols-1(1) output_width(2) output_height(2)
        /// ★ 注意没有 4 字节的 version+flags —— 只有各 1 字节。
        ///   按「FullBox + 4」去读会让后续字段整体偏移 2 字节。
        /// </summary>
        private static (int Rows, int Cols, int OutW, int OutH)? ParseGridDescriptor(byte[] d, long off, long len)
        {
            if (len < 8) return null;
            int rows = d[(int)off + 2] + 1;
            int cols = d[(int)off + 3] + 1;
            int ow = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan((int)off + 4, 2));
            int oh = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan((int)off + 6, 2));
            if (rows <= 0 || cols <= 0 || ow <= 0 || oh <= 0) return null;
            return (rows, cols, ow, oh);
        }
    }
}
