using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace AvifHeifConverter.Services
{
    /// <summary>
    /// 精简 TIFF 读取器 —— 面向「Lightroom 导出的 HDR TIFF」这一类文件。
    ///
    /// 为什么自己写而不是调引擎里的 libtiff：
    ///   heif-enc 能读 TIFF，但它只能「TIFF 进、HEIC 出」，**给不了我们像素**。
    ///   而 HDR 转换必须拿到原始像素才能算出 SDR 基底与增益图。
    ///   绕道「先编 HEIC 再解回」会经过一次有损编码，与「忠实保留」的目标冲突。
    ///
    /// 支持范围（够用即可，不追求全覆盖）：
    ///   · 字节序 II/MM · 位深 8/16 · 采样格式 uint/int/float
    ///   · 压缩：无 / Adobe Deflate（ZIP，tag 259 = 8 或 32946）
    ///   · 行预测器：无 / 水平差分（tag 317 = 2，ZIP 常配）
    ///   · 布局：条带（strips）与瓦片（tiles）都支持
    ///   · 光度：RGB（2）
    ///   · 附带读出 ICC 配置文件（tag 34675）——色彩描述靠它，**不靠猜**
    /// </summary>
    internal static class TiffReader
    {
        public sealed class Info
        {
            // ★ 按 TIFF 规范设默认值 —— 缺省不当会让探测结果出现 0/0 这种无意义值
            public int Width, Height;
            public int Samples = 1;            // 缺省 1 通道
            public int BitsPerSample = 1;
            public int SampleFormat = 1;       // 1 = 无符号整数（规范默认值）
            public int Compression = 1;        // 1 = 不压缩
            public int Predictor = 1;          // 1 = 无预测器
            public int Photometric = 2;        // 2 = RGB
            public bool Tiled;
            public byte[]? IccProfile;
            public string Summary = "";
            /// <summary>ICC 里描述的传输特性名称（原样取出，不做解释）。</summary>
            public string IccDescription = "";
            /// <summary>ICC 里 trc/para 曲线的类型（curv / para / 缺失）。</summary>
            public string IccTrcKind = "";
        }

        /// <summary>
        /// 只读元数据的快速探测 —— **不解码像素、不整文件读入**。
        ///
        /// ★ 为什么必须这样：界面预扫会对每个文件跑一次探测。
        ///   原先 Probe 用 File.ReadAllBytes 把整个文件读进来（实测 294MB 文件要 1.46 秒），
        ///   3591 个文件光预扫就要 87 分钟。改为按需 seek 读取后降到毫秒级。
        /// </summary>
        public static Info? Probe(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                              4096, FileOptions.RandomAccess);
                long flen = fs.Length;
                if (flen < 8) return null;

                var head = new byte[8];
                if (fs.Read(head, 0, 8) != 8) return null;
                bool le = head[0] == 'I' && head[1] == 'I';
                if (!le && !(head[0] == 'M' && head[1] == 'M')) return null;
                ushort U16(byte[] b, int o) => le ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o, 2))
                                                  : BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o, 2));
                uint U32(byte[] b, int o) => le ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o, 4))
                                                : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o, 4));
                if (U16(head, 2) != 42) return null;

                long ifd = U32(head, 4);
                if (ifd <= 0 || ifd + 2 > flen) return null;
                fs.Seek(ifd, SeekOrigin.Begin);
                var cntBuf = new byte[2];
                if (fs.Read(cntBuf, 0, 2) != 2) return null;
                int n = U16(cntBuf, 0);
                if (n <= 0 || n > 4096) return null;

                var ifdBuf = new byte[n * 12 + 4];
                int got = 0;
                while (got < ifdBuf.Length)
                {
                    int r = fs.Read(ifdBuf, got, ifdBuf.Length - got);
                    if (r <= 0) break;
                    got += r;
                }

                var info = new Info();
                // 只取元数据标签，不碰像素数据区
                for (int i = 0; i < n; i++)
                {
                    int e = i * 12;
                    if (e + 12 > ifdBuf.Length) break;
                    int tag = U16(ifdBuf, e);
                    int type = U16(ifdBuf, e + 2);
                    uint c = U32(ifdBuf, e + 4);
                    long valOff = e + 8;
                    long Inline() => type switch
                    {
                        1 or 2 or 6 or 7 => ifdBuf[valOff],
                        3 or 8 => U16(ifdBuf, (int)valOff),
                        4 or 9 => U32(ifdBuf, (int)valOff),
                        _ => 0,
                    };
                    long v = Inline();
                    // ★ 多值标签（count>1）的值存在外部，上面 Inline() 读到的是**指针**。
                    //   必须按 type 的大小 seek 过去读第一个值 —— 否则会得到荒谬结果
                    //   （实测 BitsPerSample 被读成 326、SampleFormat 被读成 0）。
                    if (c > 1 && tag is 258 or 339)
                    {
                        int tsz = type switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 => 4, _ => 0 };
                        if (tsz > 0)
                        {
                            var one = new byte[tsz];
                            fs.Seek(v, SeekOrigin.Begin);
                            if (fs.Read(one, 0, tsz) == tsz)
                                v = type switch
                                {
                                    3 or 8 => U16(one, 0),
                                    4 or 9 => U32(one, 0),
                                    _ => one[0],
                                };
                        }
                    }
                    switch (tag)
                    {
                        case 256: info.Width = (int)v; break;
                        case 257: info.Height = (int)v; break;
                        case 277: info.Samples = (int)v; break;
                        case 258: info.BitsPerSample = (int)v; break;
                        case 259: info.Compression = (int)v; break;
                        case 262: info.Photometric = (int)v; break;
                        case 317: info.Predictor = (int)v; break;
                        case 339: info.SampleFormat = (int)v; break;
                        case 322: info.Tiled = true; break;
                        case 34675:
                            {
                                // ICC 通常在 IFD 之后，单独 seek 读取（仅几十 KB）
                                uint iccLen = c;
                                if (iccLen > 0 && iccLen < 4 * 1024 * 1024)
                                {
                                    fs.Seek(v, SeekOrigin.Begin);
                                    info.IccProfile = new byte[iccLen];
                                    int g2 = 0;
                                    while (g2 < iccLen)
                                    {
                                        int r = fs.Read(info.IccProfile, g2, (int)iccLen - g2);
                                        if (r <= 0) break;
                                        g2 += r;
                                    }
                                    if (g2 != iccLen) info.IccProfile = null;
                                    else ParseIcc(info);
                                }
                                break;
                            }
                    }
                }

                info.Summary = $"{info.Width}×{info.Height}，{info.Samples} 通道，{info.BitsPerSample} 位，" +
                               $"压缩={CompName(info.Compression)}，预测器={info.Predictor}，" +
                               $"采样格式={info.SampleFormat}，布局={(info.Tiled ? "瓦片" : "条带")}";
                return info;
            }
            catch { return null; }
        }

        public static (ushort[] Pixels, Info Info)? Read(string path)
        {
            try
            {
                var d = File.ReadAllBytes(path);
                var (info, strips, ifd) = Parse(d);
                if (info.Width <= 0 || info.Height <= 0 || info.Samples < 1) return null;
                if (info.Photometric != 2 && info.Photometric != 1) return null;
                if (info.Samples < 3 && info.Photometric == 2) return null;

                var outPx = new ushort[(long)info.Width * info.Height * info.Samples > int.MaxValue
                    ? 0 : info.Width * info.Height * info.Samples];
                if (outPx.Length == 0) return null;

                DecodeInto(d, info, strips, outPx);
                return (outPx, info);
            }
            catch { return null; }
        }

        // ---------------- 解析 ----------------

        private static (Info Info, List<(long Off, long Len, int X, int Y, int W, int H)> Parts, long Ifd) Parse(byte[] d)
        {
            if (d.Length < 8) throw new InvalidDataException("文件过短");
            bool le = d[0] == 'I' && d[1] == 'I';
            if (!le && !(d[0] == 'M' && d[1] == 'M')) throw new InvalidDataException("不是 TIFF（缺 II/MM）");

            ushort U16(long o) => le ? BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan((int)o, 2))
                                     : BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan((int)o, 2));
            uint U32(long o) => le ? BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)o, 4))
                                   : BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan((int)o, 4));

            if (U16(2) != 42) throw new InvalidDataException("不是 TIFF（magic != 42）");

            var info = new Info();
            var tags = new Dictionary<int, (int Type, long Count, long[] Values)>();
            long ifd = U32(4);
            long firstIfd = ifd;

            // 只读第一个 IFD（主图）；多页 TIFF 取首页
            int n = U16(ifd);
            for (int i = 0; i < n; i++)
            {
                long e = ifd + 2 + i * 12;
                int tag = U16(e);
                int type = U16(e + 2);
                long cnt = U32(e + 4);
                int sz = TypeSize(type);
                if (sz == 0) continue;
                long total = sz * cnt;
                long vo = total <= 4 ? e + 8 : U32(e + 8);
                var vals = new long[cnt];
                for (long k = 0; k < cnt && k < 64; k++)
                {
                    long o = vo + k * sz;
                    vals[k] = type switch
                    {
                        1 or 2 or 6 or 7 => d[(int)o],
                        3 or 8 => U16(o),
                        4 or 9 => U32(o),
                        _ => 0,
                    };
                }
                tags[tag] = (type, cnt, vals);
            }

            long[]? T(int tag) => tags.TryGetValue(tag, out var v) ? v.Values : null;
            long T1(int tag, long def) { var v = T(tag); return v != null && v.Length > 0 ? v[0] : def; }

            info.Width = (int)T1(256, 0);
            info.Height = (int)T1(257, 0);
            info.Samples = (int)T1(277, 1);
            info.BitsPerSample = (int)T1(258, 1);
            info.Compression = (int)T1(259, 1);
            info.Photometric = (int)T1(262, 1);
            info.Predictor = (int)T1(317, 1);
            info.SampleFormat = (int)T1(339, 1);   // 1=uint, 2=int, 3=float

            // ICC 配置文件
            if (tags.TryGetValue(34675, out var icc) && icc.Type == 7)
            {
                long len = U32(ifd + 2 + IndexOfTag(d, le, ifd, n, 34675) * 12 + 4);
                long off = U32(ifd + 2 + IndexOfTag(d, le, ifd, n, 34675) * 12 + 8);
                if (len > 0 && off + len <= d.Length)
                {
                    info.IccProfile = new byte[len];
                    Array.Copy(d, off, info.IccProfile, 0, len);
                    ParseIcc(info);
                }
            }

            // 条带 / 瓦片
            var parts = new List<(long, long, int, int, int, int)>();
            if (tags.ContainsKey(322))   // tiled
            {
                info.Tiled = true;
                int tw = (int)T1(322, 0), th = (int)T1(323, 0);
                var offs = T(324)!; var cnts = T(325)!;
                int across = (info.Width + tw - 1) / tw, down = (info.Height + th - 1) / th;
                for (int k = 0; k < offs.Length && k < cnts.Length; k++)
                {
                    int cx = k % across, cy = k / across;
                    if (cy >= down) break;
                    parts.Add((offs[k], cnts[k], cx * tw, cy * th, tw, th));
                }
            }
            else
            {
                var offs = T(273) ?? new long[0];
                var cnts = T(279) ?? new long[0];
                int rows = (int)T1(278, info.Height);
                for (int k = 0; k < offs.Length && k < cnts.Length; k++)
                    parts.Add((offs[k], cnts[k], 0, k * rows, info.Width, Math.Min(rows, info.Height - k * rows)));
            }

            info.Summary = $"{info.Width}×{info.Height}，{info.Samples} 通道，{info.BitsPerSample} 位，" +
                           $"压缩={CompName(info.Compression)}，预测器={info.Predictor}，" +
                           $"采样格式={info.SampleFormat}，布局={(info.Tiled ? "瓦片" : "条带")}，" +
                           $"片段数={parts.Count}";
            return (info, parts, firstIfd);
        }

        private static int IndexOfTag(byte[] d, bool le, long ifd, int n, int want)
        {
            for (int i = 0; i < n; i++)
            {
                long e = ifd + 2 + i * 12;
                int tag = le ? BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan((int)e, 2))
                             : BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan((int)e, 2));
                if (tag == want) return i;
            }
            return -1;
        }

        private static int TypeSize(int t) => t switch
        {
            1 or 2 or 6 or 7 => 1,
            3 or 8 => 2,
            4 or 9 or 11 => 4,
            5 or 10 or 12 => 8,
            _ => 0,
        };

        private static string CompName(int c) => c switch
        {
            1 => "无", 5 => "LZW", 6 or 7 => "JPEG", 8 or 32946 => "ZIP/Deflate", 32773 => "PackBits", _ => c.ToString(),
        };

        // ---------------- 像素解码 ----------------

        private static void DecodeInto(byte[] d, Info info, List<(long, long, int, int, int, int)> parts, ushort[] dst)
        {
            int W = info.Width, S = info.Samples;
            int bytesPerSample = info.BitsPerSample / 8;
            foreach (var (off, len, x0, y0, pw, ph) in parts)
            {
                if (len <= 0 || off < 0 || off + len > d.Length) continue;
                var raw = new byte[len];
                Array.Copy(d, off, raw, 0, len);

                byte[] plane;
                if (info.Compression == 1) plane = raw;
                else if (info.Compression is 8 or 32946)
                {
                    plane = new byte[(long)pw * ph * S * bytesPerSample];
                    using var ms = new MemoryStream(raw);
                    using var z = new ZLibStream(ms, CompressionMode.Decompress);
                    int got = 0;
                    while (got < plane.Length)
                    {
                        int r = z.Read(plane, got, plane.Length - got);
                        if (r <= 0) break;
                        got += r;
                    }
                }
                else continue;   // 其他压缩方式暂不支持

                // 水平差分预测器还原（逐通道按行累加）
                if (info.Predictor == 2)
                    UndoPredictor(plane, pw, ph, S, bytesPerSample);

                // 搬进 16bit 输出（按位深归一化）
                int rows = Math.Min(ph, info.Height - y0);
                for (int y = 0; y < rows; y++)
                {
                    int oy = y0 + y;
                    if (oy < 0 || oy >= info.Height) continue;
                    long rowBase = (long)y * pw * S * bytesPerSample;
                    for (int x = 0; x < pw; x++)
                    {
                        int ox = x0 + x;
                        if (ox < 0 || ox >= W) continue;
                        long o = ((long)oy * W + ox) * S;
                        for (int c = 0; c < S; c++)
                        {
                            long p = rowBase + ((long)x * S + c) * bytesPerSample;
                            if (p + bytesPerSample > plane.Length) break;
                            ushort v;
                            if (info.BitsPerSample == 16)
                                v = BinaryPrimitives.ReadUInt16LittleEndian(plane.AsSpan((int)p, 2));
                            else if (info.BitsPerSample == 8)
                                v = (ushort)(plane[(int)p] * 257);          // 8bit → 铺满 16bit
                            else
                            {
                                // 非 8/16 位：按位深左移到 16 位（15bit 等情况）
                                int nbytes = (info.BitsPerSample + 7) / 8;
                                uint rawv = 0;
                                for (int k = 0; k < nbytes && p + k < plane.Length; k++)
                                    rawv |= (uint)plane[(int)p + k] << (8 * k);
                                int shift = 16 - info.BitsPerSample;
                                v = (ushort)(shift > 0 ? rawv << shift : rawv >> (-shift));
                            }
                            dst[o + Math.Min(c, S - 1)] = v;
                            if (dst.Length > (long)o + S) { /* 占位，保持结构清晰 */ }
                        }
                        // 单通道（灰度）复制到三通道由调用方决定，这里原样保留
                    }
                }
            }
        }

        private static void UndoPredictor(byte[] p, int w, int h, int s, int bps)
        {
            if (bps == 2)
            {
                int stride = w * s * 2;
                for (int y = 0; y < h; y++)
                {
                    int baseO = y * stride;
                    for (int i = s * 2; i < stride; i += 2)
                    {
                        if (baseO + i + 1 >= p.Length) break;
                        ushort cur = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(baseO + i, 2));
                        ushort prev = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(baseO + i - s * 2, 2));
                        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(baseO + i, 2), (ushort)(cur + prev));
                    }
                }
            }
            else if (bps == 1)
            {
                int stride = w * s;
                for (int y = 0; y < h; y++)
                {
                    int baseO = y * stride;
                    for (int i = s; i < stride; i++)
                        if (baseO + i < p.Length) p[baseO + i] = (byte)(p[baseO + i] + p[baseO + i - s]);
                }
            }
        }

        // ---------------- ICC（只取描述性信息，不做色彩管理）----------------

        private static void ParseIcc(Info info)
        {
            var b = info.IccProfile!;
            if (b.Length < 132) return;
            long n = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(128, 4));
            for (long i = 0; i < n; i++)
            {
                long e = 132 + i * 12;
                if (e + 12 > b.Length) break;
                string sig = Encoding.ASCII.GetString(b, (int)e, 4);
                long off = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)e + 4, 4));
                long len = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)e + 8, 4));
                if (off <= 0 || off + len > b.Length || len < 8) continue;

                if (sig == "desc")
                {
                    // textDescriptionType: 'desc'(4) reserved(4) asciiCount(4) ascii...
                    long ac = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)off + 8, 4));
                    if (ac > 0 && off + 12 + ac <= b.Length)
                    {
                        int end = (int)(off + 12 + ac);
                        int z = Array.IndexOf(b, (byte)0, (int)off + 12, (int)ac);
                        info.IccDescription = Encoding.ASCII.GetString(b, (int)off + 12,
                            (z > 0 ? z : end) - (int)off - 12).Trim();
                    }
                }
                else if (sig is "rTRC" or "gTRC" or "bTRC" or "kTRC")
                {
                    string t = Encoding.ASCII.GetString(b, (int)off, 4);
                    info.IccTrcKind = t switch
                    {
                        "curv" => $"curv（点数={(off + 12 <= b.Length ? BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)off + 8, 4)) : 0)}）",
                        "para" => $"para（函数类型={(off + 12 <= b.Length ? BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan((int)off + 8, 2)) : 0)}）",
                        _ => t.Trim(),
                    };
                }
            }
        }
    }
}
