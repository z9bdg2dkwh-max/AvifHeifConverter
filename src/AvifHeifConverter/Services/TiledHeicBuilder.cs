using System;
using System.Buffers.Binary;
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
    /// 把一张大图编码成 **grid 分块** 的 HEIC。
    ///
    /// 为什么必须做：
    ///   HEVC 最高级别是 Level 6.2，单张图像最大亮度像素数 = 35,651,584（约 35.7 MP），
    ///   规范里**不存在 6.3**。超过此值仍按单张编码，编码器会写出 level_idc=189
    ///   （不存在的级别），任何合规解码器都会拒绝 —— 产物变成「结构合法但解不出来」。
    ///   实测：9504×6336（60.2 MP）单张编码后 heif-dec 报
    ///   "Decoding the input data did not give a decompressed image"。
    ///
    ///   厂商的分块源（如索尼 5×4）正是为规避这条限制而存在。因此对本工具而言，
    ///   遇到大图必须**重建分块结构**，而不是组装成一张。
    ///
    /// 产物结构（对齐苹果 IMG_3836）：
    ///   grid derived item（primary，带 ispe/pixi/colr）→ N 个 hvc1 tile item（hidden）
    ///   iref: dimg(grid → tiles)
    ///   grid 描述符 8 字节：version(1)=0 flags(1)=0 rows-1(1) cols-1(1) outW(2) outH(2)
    /// </summary>
    internal static class TiledHeicBuilder
    {
        public const long HevcMaxLumaSamples = 35_651_584;

        public sealed class Result
        {
            public bool Ok { get; init; }
            public string Error { get; init; } = "";
            public int Cols { get; init; }
            public int Rows { get; init; }
            public int TileSize { get; init; }
            /// <summary>分块实际尺寸（矩形分块时两者不同）。</summary>
            public int TileW { get; init; }
            public int TileH { get; init; }
        }

        // ---------------- box 写入工具 ----------------

        private static byte[] BE16(int v) => new[] { (byte)(v >> 8), (byte)v };
        private static byte[] BE32(long v) => new[]
        {
            (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v
        };

        private static byte[] Box(string type, byte[] payload)
        {
            var head = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(0), (uint)(8 + payload.Length));
            Encoding.ASCII.GetBytes(type, 0, 4, head, 4);
            var outBuf = new byte[8 + payload.Length];
            Array.Copy(head, outBuf, 8);
            Array.Copy(payload, 0, outBuf, 8, payload.Length);
            return outBuf;
        }

        private static byte[] IspeBox(int w, int h)
            => Box("ispe", new byte[4].Concat(BE32(w)).Concat(BE32(h)).ToArray());

        private static byte[] PixiBox(params byte[] depths)
            => Box("pixi", new byte[4].Concat(new[] { (byte)depths.Length }).Concat(depths).ToArray());

        /// <summary>
        /// clli（内容亮度信息）box。注意它是**普通 Box**（ISO/IEC 23008-12），不是 FullBox：
        /// 载荷直接是 MaxCLL(2) + MaxPALL(2)，没有 version/flags 四字节。
        /// iPhone 依据它决定 HDR 推到多亮 —— 缺了会显得「有 HDR 但不够亮」。
        /// </summary>
        private static byte[] ClliBox(ushort maxCLL, ushort maxPALL)
            => Box("clli", BE16(maxCLL).Concat(BE16(maxPALL)).ToArray());

        private static byte[] ColrNclxBox(ushort primaries, ushort transfer, ushort matrix,
                                          bool fullRange = true)
        {
            var p = Encoding.ASCII.GetBytes("nclx")
                .Concat(BE16(primaries)).Concat(BE16(transfer)).Concat(BE16(matrix))
                .Concat(new[] { (byte)(fullRange ? 0x80 : 0x00) }).ToArray();
            return Box("colr", p);
        }

        private static byte[] InfeBox(uint id, string type, string name, bool hidden)
        {
            var nb = Encoding.UTF8.GetBytes(name ?? "");
            var payload = new List<byte>();
            // FullBox 布局：version(1) + flags(3)。
            // ★ hidden 标志是 **flags 的最低字节 bit0**，没有独立字节。
            //   曾多写一个 hidden 字节，导致其后 item_ID / item_type 全部右移一字节，
            //   libheif 读出乱码类型并报 "pitm box references a non-existing image"。
            payload.Add(2);                       // version
            payload.Add(0); payload.Add(0);       // flags 高 16 位
            payload.Add((byte)(hidden ? 1 : 0));  // flags 低 8 位
            payload.AddRange(BE16((int)id));      // item_ID
            payload.AddRange(BE16(0));            // item_protection_index
            payload.AddRange(Encoding.ASCII.GetBytes(type.PadRight(4).Substring(0, 4)));
            payload.AddRange(nb);
            payload.Add(0);                       // item_name 的 null 结尾
            return Box("infe", payload.ToArray());
        }

        private static byte[] GridDescriptor(int rows, int cols, int w, int h)
            => new[]
            {
                (byte)0, (byte)0,
                (byte)(rows - 1), (byte)(cols - 1),
                (byte)(w >> 8), (byte)w,
                (byte)(h >> 8), (byte)h
            };

        // ---------------- 主流程 ----------------

        public static async Task<Result> BuildAsync(
            byte[] rgb, int w, int h, string outPath,
            string engineDir, string workDir, int quality,
            Action<string> log, CancellationToken ct = default,
            ushort primaries = 12, ushort maxCLL = 0, ushort maxPALL = 0, string chroma = "444", string preset = "")
        {
            // 1) 选分块尺寸：在「每块不超上限」的前提下，让补边浪费最小
            // ★ 分块尺寸：从固定列表改为「全范围搜索最小补边」。
            //
            //   原来只在 {1024,1536,2048,3072,4096,5120,6336,8192} 里挑，
            //   对 9504×6336 会选中 1024 → 7行×10列=70 块、补到 10240×7168，
            //   **白白多编码 22% 的像素**。
            //   而 9504 与 6336 的最大公约数是 3168：3168×3=9504、3168×2=6336，
            //   即 3168 可以**零补边**，只需 3×2=6 块。
            //
            //   补边相同时取更大的块 —— 块少则进程启动次数少、总工作量小。
            // ★ 按「网格形状」搜索分块，允许**矩形块**。
            //
            //   只允许正方形时，大宽高比的全景会陷入两难：
            //   实测 18939×6281（3:1）被迫选 902×902 → **147 块**，
            //   而 18939×6281 用 3157×3141 只需 6×2=12 块、补边几乎为零。
            //
            //   做法：枚举「列数 × 行数」，块尺寸就取 ceil(w/cols) × ceil(h/rows)，
            //   先求最小补边，再在补边不超容差的候选里挑**块数最少**的。
            long imgPx = (long)w * h;
            const int MaxTiles = 512;
            // 与下面并行编码用的并发度保持一致
            int targetTiles = Math.Clamp(Environment.ProcessorCount - 2, 2, 12);          // 块数上限：再多也没有并行收益，只是徒增进程
            const int MinTileSide = 256;       // 块太小会让编码器效率骤降

            long minWaste = long.MaxValue;
            for (int cols = 1; (long)cols * MinTileSide <= w && cols <= MaxTiles; cols++)
            {
                long tw = (w + cols - 1) / cols;
                for (int rows = 1; (long)rows * MinTileSide <= h && (long)cols * rows <= MaxTiles; rows++)
                {
                    long th = (h + rows - 1) / rows;
                    if (tw * th > HevcMaxLumaSamples) continue;          // 单块超 HEVC 上限
                    if (tw * cols > 65535 || th * rows > 65535) continue; // 网格描述符是 uint16
                    minWaste = Math.Min(minWaste, tw * cols * th * rows - imgPx);
                }
            }
            if (minWaste == long.MaxValue)
                return new Result { Ok = false, Error = "找不到合适的分块尺寸（图像异常大）" };

            // 容差：图像 0.5% —— 微小补边不值得为它多切几十块
            long tolerance = Math.Max(minWaste, imgPx / 200);
            long bestWaste = long.MaxValue;
            int bestTileW = 0, bestTileH = 0, bestColsN = 0, bestRowsN = 0;
            long bestScore = long.MaxValue;
            for (int cols = 1; (long)cols * MinTileSide <= w && cols <= MaxTiles; cols++)
            {
                long tw = (w + cols - 1) / cols;
                for (int rows = 1; (long)rows * MinTileSide <= h && (long)cols * rows <= MaxTiles; rows++)
                {
                    long th = (h + rows - 1) / rows;
                    if (tw * th > HevcMaxLumaSamples) continue;
                    if (tw * cols > 65535 || th * rows > 65535) continue;
                    long waste = tw * cols * th * rows - imgPx;
                    if (waste > tolerance) continue;
                    int count = cols * rows;
                    // ★ 打分制：块数**不少于并行度**为上（否则 worker 闲着纯浪费），
                    //   高出并行度越多越差（多出的轮次不会缩短墙钟时间），
                    //   块数相同时比补边。
                    //   实测教训：只求块数最少时，60MP 选出 2 块 → 12 个 worker 闲着 10 个，
                    //   分块编码反占全流程 59%（7.6 秒）。
                    // 长宽比惩罚：太扁的条带（如 9504×528）会让编码器效率打折，
                    // 按 log2(长宽比) 的绝对值计罚，系数 2.5 使其在与块数同分时落败。
                    double aspect = Math.Abs(Math.Log2((double)tw / th)) * 2.5;
                    long score = (count >= targetTiles
                        ? (count - targetTiles)                    // 够用即可，越接近越好
                        : (long)(targetTiles - count) * 1000)      // 不够用，重罚
                        + (long)aspect;
                    if (score < bestScore || (score == bestScore && waste < bestWaste))
                    {
                        bestScore = score; bestWaste = waste;
                        bestTileW = (int)tw; bestTileH = (int)th;
                        bestColsN = cols; bestRowsN = rows;
                    }
                }
            }
            if (bestTileW == 0)
                return new Result { Ok = false, Error = "找不到合适的分块尺寸（图像异常大）" };

            int colsN = bestColsN, rowsN = bestRowsN;
            long paddedW = (long)colsN * bestTileW, paddedH = (long)rowsN * bestTileH;
            log($"      分块：{rowsN}行×{colsN}列，每块 {bestTileW}×{bestTileH}，" +
                $"补边后 {paddedW}×{paddedH}（浪费 {bestWaste / 1e6:0.#} MP）");

            // 2) 逐块编码（**并行**）
            //
            // 每个分块是完全独立的：各自的 PNG 中间文件、各自的 heif-enc 进程、各自的输出。
            // 原来是串行跑，一块接一块 —— 24 核机器上只用掉 1 个核。
            // 实测 60MP 图（70 块）串行要 46.6 秒，是仅次于源图解码的第二大开销。
            int total = rowsN * colsN;
            var tiles = new (byte[] Data, List<byte[]> Props)?[total];

            // 并发度：给系统留余量，同时避免 N 个 heif-enc 同时抢内存。
            int degree = Math.Clamp(Environment.ProcessorCount - 2, 2, 12);
            log($"      并行编码 {total} 个分块（并发 {degree}）…");

            int done = 0;
            var errLock = new object();
            string? firstError = null;

            var popts = new ParallelOptions
            {
                MaxDegreeOfParallelism = degree,
                CancellationToken = ct,
            };
            await Parallel.ForEachAsync(Enumerable.Range(0, total), popts, async (i, token) =>
            {
                int r = i / colsN, c = i % colsN;
                var tileRgb = ExtractTile(rgb, w, h, c * bestTileW, r * bestTileH, bestTileW, bestTileH);
                string tp = Path.Combine(workDir, $"tile_{i}.png");
                string th = Path.Combine(workDir, $"tile_{i}.heic");
                PngCodec.Write8(tp, bestTileW, bestTileH, 3, tileRgb);

                // 色度采样：heif-enc 默认 420，而源 AVIF 多为 4:4:4，
                // 不显式指定就会丢掉一半色度分辨率（由 Options.Chroma 控制）。
                var tileArgs = new List<string> { tp, "-o", th, "-q", quality.ToString() };
                if (chroma != "420") { tileArgs.Add("-p"); tileArgs.Add($"chroma={chroma}"); }
                if (!string.IsNullOrEmpty(preset)) { tileArgs.Add("-p"); tileArgs.Add($"preset={preset}"); }
                // ★ 全局闸门：限制全机同时运行的 heif-enc 总数。
                //   分块并行（本机 degree=12）与多任务并行叠加曾出现 124+ 个
                //   heif-enc 同时运行，把系统内存压到 0.4 GB、新进程启动失败
                //   （0xC000009A）。此处排队等待，而不是无限制地开新进程。
                await EngineGate.AcquireEncAsync(token).ConfigureAwait(false);
                int rc;
                try
                {
                    rc = await RunAsync(Path.Combine(engineDir, "heif-enc.exe"),
                        tileArgs.ToArray(),
                        engineDir, token).ConfigureAwait(false);
                }
                finally
                {
                    EngineGate.ReleaseEnc();
                }
                if (rc != 0)
                {
                    lock (errLock) firstError ??= $"分块 {i} 编码失败（退出码 {rc}）";
                    return;
                }

                var parsed = ParseSingleItemHeic(th);
                if (parsed == null)
                {
                    lock (errLock) firstError ??= $"分块 {i} 解析失败";
                    return;
                }
                tiles[i] = parsed;

                int n = Interlocked.Increment(ref done);
                if (n % 10 == 0 || n == total) log($"      已编码 {n}/{total} 个分块");
            }).ConfigureAwait(false);

            if (firstError != null)
                return new Result { Ok = false, Error = firstError };
            if (tiles.Any(x => x == null))
                return new Result { Ok = false, Error = "有分块未完成编码" };

            // 并行完成的结果按原顺序（行优先）取回，容器里的 dimg 引用顺序不能乱
            var tileList = new List<(byte[] Data, List<byte[]> Props)>(total);
            foreach (var t in tiles) tileList.Add(t!.Value);

            // 3) 组装容器
            var bytes = Assemble(tileList, colsN, rowsN, bestTileW, bestTileH,
                                 paddedW, paddedH, w, h, primaries, maxCLL, maxPALL);
            File.WriteAllBytes(outPath, bytes);
            return new Result { Ok = true, Cols = colsN, Rows = rowsN,
                    TileSize = Math.Max(bestTileW, bestTileH), TileW = bestTileW, TileH = bestTileH };
        }

        /// <summary>按边缘复制补边，抠出 tile×tile 的 RGB 块。</summary>
        private static byte[] ExtractTile(byte[] rgb, int w, int h, int x0, int y0, int tw, int th)
        {
            var outBuf = new byte[tw * th * 3];
            for (int y = 0; y < th; y++)
            {
                int sy = Math.Min(y0 + y, h - 1);
                for (int x = 0; x < tw; x++)
                {
                    int sx = Math.Min(x0 + x, w - 1);
                    int s = (sy * w + sx) * 3, d = (y * tw + x) * 3;
                    outBuf[d] = rgb[s];
                    outBuf[d + 1] = rgb[s + 1];
                    outBuf[d + 2] = rgb[s + 2];
                }
            }
            return outBuf;
        }

        /// <summary>解析 heif-enc 产出的单图 HEIC，取出 item 数据与 ipco 属性 box。</summary>
        private static (byte[] Data, List<byte[]> Props)? ParseSingleItemHeic(string path)
        {
            try
            {
                var b = File.ReadAllBytes(path);
                var r = new IsoBmffReader(b);
                uint primary = r.ParsePrimaryItemId();
                var iloc = r.ParseIloc();
                if (primary == 0 || !iloc.TryGetValue(primary, out var loc)) return null;
                var data = r.ReadItemBytes(primary, loc);
                if (data == null || data.Length == 0) return null;

                var boxes = r.ParsePropertyBoxes();
                var assoc = r.ParsePropertyAssociations();
                var props = new List<byte[]>();
                if (assoc.TryGetValue(primary, out var idxs))
                {
                    foreach (var raw in idxs)
                    {
                        int i = raw & 0xFFFF;
                        if (i < 1 || i > boxes.Count) continue;
                        var box = boxes[i - 1];
                        var buf = new byte[box.Size];
                        Array.Copy(b, box.Offset, buf, 0, (int)box.Size);
                        props.Add(buf);
                    }
                }
                return (data, props);
            }
            catch { return null; }
        }

        /// <summary>组装完整的 grid HEIC 字节。</summary>
        private static byte[] Assemble(
            List<(byte[] Data, List<byte[]> Props)> tiles,
            int cols, int rows, int tw, int th, long padW, long padH, int outW, int outH,
            ushort primaries, ushort maxCLL = 0, ushort maxPALL = 0)
        {
            // ---- 属性池（按字节去重）----
            var propPool = new List<byte[]>();
            var propIdx = new Dictionary<string, int>();
            int Prop(byte[] box)
            {
                var key = Convert.ToBase64String(box);
                if (!propIdx.TryGetValue(key, out var i))
                {
                    propPool.Add(box);
                    i = propPool.Count;              // 1-based
                    propIdx[key] = i;
                }
                return i;
            }

            // 分块的 colr 统一为 Display P3 + sRGB 传递（与基底一致）；
            // 未指定色域会让显示端按默认推断，实测会造成明显偏色。
            var tileColr = ColrNclxBox(primaries, 13, 6);
            var gridIspe = IspeBox(outW, outH);
            var gridPixi = PixiBox(8, 8, 8);
            var gridColr = ColrNclxBox(primaries, 13, 6);

            var items = new List<(uint Id, string Type, bool Hidden, byte[] Data, List<int> Props)>();
            var refs = new List<(string Type, uint From, List<uint> To)>();

            uint nid = 1;
            uint gridId = nid++;
            // ★ grid 描述符里填的是**真实输出尺寸**（不是补边后的尺寸）。
            //   实测依据：苹果 IMG_3836 的 grid 描述符写 4032×3024（真实尺寸），
            //   而 8×512=4096 的 tile 覆盖范围更大，多出的部分由输出尺寸裁掉。
            //   曾填成补边尺寸（4608×7680），解码器报
            //   "Invalid image size: Decoded image does not have the size signaled in the file"。
            var gridProps = new List<int> { Prop(gridIspe), Prop(gridPixi), Prop(gridColr) };
            // clli 挂在 **grid item** 上（不是各 tile）—— 主图就是这张 grid，
            // 系统读的是它，挂在 tile 上等于没挂。
            if (maxCLL > 0) gridProps.Add(Prop(ClliBox(maxCLL, maxPALL > 0 ? maxPALL : (ushort)Math.Max(1, maxCLL / 4))));
            items.Add((gridId, "grid", false, GridDescriptor(rows, cols, outW, outH), gridProps));

            var tileIds = new List<uint>();
            foreach (var (data, props) in tiles)
            {
                uint id = nid++;
                var idxs = new List<int>();
                foreach (var p in props)
                {
                    // heif-enc 写出的 colr 未必是 P3/sRGB，统一替换保证一致性
                    var box = p.Length >= 4 && Encoding.ASCII.GetString(p, 4, 4) == "colr"
                        ? tileColr : p;
                    idxs.Add(Prop(box));
                }
                items.Add((id, "hvc1", true, data, idxs));
                tileIds.Add(id);
            }
            refs.Add(("dimg", gridId, tileIds));

            // ---- iinf ----
            var iinfInner = new List<byte>();
            iinfInner.AddRange(new byte[4]);
            iinfInner.AddRange(BE16(items.Count));
            foreach (var it in items)
                iinfInner.AddRange(InfeBox(it.Id, it.Type, "", it.Hidden));
            var iinf = Box("iinf", iinfInner.ToArray());

            // ---- iprp ----
            var ipcoInner = new List<byte>();
            foreach (var p in propPool) ipcoInner.AddRange(p);
            var ipco = Box("ipco", ipcoInner.ToArray());

            var ipmaInner = new List<byte>();
            ipmaInner.AddRange(new byte[4]);
            ipmaInner.AddRange(BE32(items.Count));
            foreach (var it in items)
            {
                ipmaInner.AddRange(BE16((int)it.Id));
                ipmaInner.Add((byte)it.Props.Count);
                foreach (var pi in it.Props) ipmaInner.Add((byte)(pi & 0x7F));
            }
            var ipma = Box("ipma", ipmaInner.ToArray());
            var iprp = Box("iprp", ipco.Concat(ipma).ToArray());

            // ---- iref ----
            var irefInner = new List<byte>();
            irefInner.AddRange(new byte[4]);
            foreach (var (type, from, to) in refs)
            {
                var p = BE16((int)from).Concat(BE16(to.Count))
                    .Concat(to.SelectMany(t => BE16((int)t))).ToArray();
                irefInner.AddRange(Box(type, p));
            }
            var iref = Box("iref", irefInner.ToArray());

            // ---- hdlr / pitm ----
            var hdlrPayload = new byte[4].Concat(new byte[4])
                .Concat(Encoding.ASCII.GetBytes("pict")).Concat(new byte[12])
                .Concat(new byte[] { 0 }).ToArray();
            var hdlr = Box("hdlr", hdlrPayload);
            var pitm = Box("pitm", new byte[4].Concat(BE16((int)gridId)).ToArray());

            // ---- iloc（偏移需与 meta 尺寸自洽，迭代两次收敛）----
            byte[] BuildIloc(long dataStart)
            {
                var inner = new List<byte>();
                inner.AddRange(new byte[4]);
                inner.Add(0x44); inner.Add(0x40);            // offset=4B, length=4B, base=4B
                inner.AddRange(BE16(items.Count));
                long cursor = dataStart;
                foreach (var it in items)
                {
                    inner.AddRange(BE16((int)it.Id));
                    inner.AddRange(BE16(0));
                    inner.AddRange(BE32(cursor));
                    inner.AddRange(BE16(1));
                    inner.AddRange(BE32(0));
                    inner.AddRange(BE32(it.Data.Length));
                    cursor += it.Data.Length;
                }
                return Box("iloc", inner.ToArray());
            }

            var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("heic")
                .Concat(BE32(0))
                .Concat(Encoding.ASCII.GetBytes("mif1"))
                .Concat(Encoding.ASCII.GetBytes("miaf")).ToArray());

            long dataStart = ftyp.Length + 8;
            byte[] metaBytes;
            for (int i = 0; i < 4; i++)
            {
                var iloc = BuildIloc(dataStart);
                metaBytes = Box("meta",
                    new byte[4].Concat(hdlr).Concat(iloc).Concat(iinf)
                                .Concat(pitm).Concat(iprp).Concat(iref).ToArray());
                long newStart = ftyp.Length + metaBytes.Length + 8;
                if (newStart == dataStart) break;
                dataStart = newStart;
            }
            metaBytes = Box("meta",
                new byte[4].Concat(hdlr).Concat(BuildIloc(dataStart)).Concat(iinf)
                            .Concat(pitm).Concat(iprp).Concat(iref).ToArray());

            var mdatPayload = new List<byte>();
            foreach (var it in items) mdatPayload.AddRange(it.Data);
            var mdat = Box("mdat", mdatPayload.ToArray());

            return ftyp.Concat(metaBytes).Concat(mdat).ToArray();
        }

        private static async Task<int> RunAsync(string exe, string[] args,
            string engineDir, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            var old = psi.Environment.TryGetValue("PATH", out var v) ? v : "";
            psi.Environment["PATH"] = engineDir + ";" + old;

            using var proc = new Process { StartInfo = psi };
            proc.Start();
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return proc.ExitCode;
        }
    }
}
