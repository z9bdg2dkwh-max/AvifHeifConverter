using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace AvifHeifConverter.Services
{
    /// <summary>
    /// 极简 PNG 编解码。
    ///
    /// 为什么不用现成库：
    ///   System.Drawing / WPF 的 PNG 编码器只有 8bit，读 16bit PNG 会**静默截断**；
    ///   而 HDR 管线需要完整的 10bit 精度（heif-dec 输出的是 16bit PNG，
    ///   保存的是 10bit 值左移 6 位），截断后暗部精度会明显受损。
    ///   故自研：读 16bit / 写 8bit，够用且无精度陷阱。
    /// </summary>
    internal static class PngCodec
    {
        // ---------------- 读 ----------------

        /// <summary>读 PNG，返回 (宽, 高, 通道数, 16bit 量程数据)。8bit 输入会左移到 16bit 量程。</summary>
        /// <summary>
        /// 只暴露 PNG 中 IDAT 块载荷的流：按块步进，跳到 IDAT 就直接把载荷喂给 zlib。
        ///
        /// 为什么不先把整个文件读进内存、再把 IDAT 拼成一个 MemoryStream：
        /// 一张 60MP 的 16bit PNG 约 119 MB，那样会同时存在
        /// 「文件字节 + IDAT 副本 + 解压结果 + 解压结果副本」**四份**数据，
        /// 解码峰值接近 1 GB。流式之后只剩输出数组本身。
        /// </summary>
        private sealed class IdatStream : Stream
        {
            private readonly Stream _src;
            private long _left;          // 当前 IDAT 块剩余字节
            private bool _skipCrc;       // 上一个 IDAT 块数据读完后，需先跳过它的 4 字节 CRC
            private bool _done;

            public IdatStream(Stream src) => _src = src;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
            public override void SetLength(long v) => throw new NotSupportedException();
            public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count)
            {
                while (!_done)
                {
                    if (_left == 0)
                    {
                        if (!NextChunk()) return 0;
                        continue;
                    }
                    int want = (int)Math.Min(count, _left);
                    int got = _src.Read(buffer, offset, want);
                    if (got <= 0) { _done = true; return 0; }
                    _left -= got;
                    return got;
                }
                return 0;
            }

            /// <summary>步进到下一个 IDAT 块；遇到 IEND 或非 IDAT 则跳过。</summary>
            private bool NextChunk()
            {
                // ★ 上一块 IDAT 的数据读完后，要先吃掉它的 4 字节 CRC，
                //   否则后面的块头会整体错位 4 字节（曾在此处踩过）。
                if (_skipCrc) { Skip(4); _skipCrc = false; }

                Span<byte> hdr = stackalloc byte[8];
                while (true)
                {
                    if (!ReadExact(hdr)) { _done = true; return false; }
                    int len = (int)BinaryPrimitives.ReadUInt32BigEndian(hdr[..4]);
                    string type = System.Text.Encoding.ASCII.GetString(hdr[4..8]);
                    if (type == "IEND") { _done = true; return false; }
                    if (type == "IDAT")
                    {
                        _left = len;
                        _skipCrc = true;
                        return len > 0;
                    }
                    Skip(len + 4);   // 数据 + CRC
                }
            }

            private bool ReadExact(Span<byte> buf)
            {
                int read = 0;
                while (read < buf.Length)
                {
                    int n = _src.Read(buf[read..]);
                    if (n <= 0) return false;
                    read += n;
                }
                return true;
            }

            private void Skip(long n)
            {
                Span<byte> tmp = stackalloc byte[4096];
                while (n > 0)
                {
                    int want = (int)Math.Min(n, tmp.Length);
                    int got = _src.Read(tmp[..want]);
                    if (got <= 0) return;
                    n -= got;
                }
            }
        }

        public static (int W, int H, int C, ushort[] Data) Read16(string path)
        {
            using var fs = File.OpenRead(path);
            Span<byte> sig = stackalloc byte[8];
            if (fs.Read(sig) != 8 || sig[0] != 0x89 || sig[1] != 'P' || sig[2] != 'N' || sig[3] != 'G')
                throw new InvalidDataException("不是 PNG 文件");

            int w = 0, h = 0, depth = 0, ctype = 0;
            // 先扫到 IHDR（它一定在最前面），拿到尺寸后才能算行跨距
            Span<byte> hdr = stackalloc byte[8];
            while (true)
            {
                if (fs.Read(hdr) != 8) throw new InvalidDataException("PNG 结构不完整（缺少 IHDR）");
                int len = (int)BinaryPrimitives.ReadUInt32BigEndian(hdr[..4]);
                string type = System.Text.Encoding.ASCII.GetString(hdr[4..8]);
                if (type == "IHDR")
                {
                    var ih = new byte[len];
                    if (fs.Read(ih, 0, len) != len) throw new InvalidDataException("IHDR 读取失败");
                    w = (int)BinaryPrimitives.ReadUInt32BigEndian(ih.AsSpan(0, 4));
                    h = (int)BinaryPrimitives.ReadUInt32BigEndian(ih.AsSpan(4, 4));
                    depth = ih[8];
                    ctype = ih[9];
                    fs.Seek(4, SeekOrigin.Current);   // CRC
                    break;
                }
                if (type == "IEND") throw new InvalidDataException("PNG 中未找到 IHDR");
                fs.Seek(len + 4, SeekOrigin.Current);
            }

            int c = ctype switch { 0 => 1, 2 => 3, 4 => 2, 6 => 4, _ => throw new InvalidDataException($"不支持的 PNG 颜色类型 {ctype}") };
            int bpp = c * (depth / 8);
            long total = (long)w * h * c;
            if (total > int.MaxValue)
                throw new InvalidDataException($"图像过大（{w}×{h}×{c}）");

            int stride = w * bpp;
            var cur = new byte[stride];
            var prev = new byte[stride];
            var data = new ushort[w * h * c];

            using (var zs = new ZLibStream(new IdatStream(fs), CompressionMode.Decompress))
            {
                int o = 0;                       // 写入 data 的位置
                var ftBuf = new byte[1];
                for (int y = 0; y < h; y++)
                {
                    // ★ PNG 每行 = 1 字节滤波类型 + stride 字节像素数据。
                    //   漏读那个字节会整条数据错位（曾在此处踩过）。
                    ReadFully(zs, ftBuf, 1);
                    ReadFully(zs, cur, stride);
                    Unfilter(ftBuf[0], cur, prev, bpp, stride);
                    for (int i = 0; i < stride; i += bpp, o += c)
                        for (int k = 0; k < c; k++)
                            data[o + k] = depth == 16
                                ? (ushort)((cur[i + k * 2] << 8) | cur[i + k * 2 + 1])
                                : (ushort)(cur[i + k] * 257);
                    (prev, cur) = (cur, prev);   // 交换而非复制
                }
            }
            return (w, h, c, data);
        }

        private static void ReadFully(Stream s, byte[] buf, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = s.Read(buf, read, count - read);
                if (n <= 0) throw new InvalidDataException("PNG 像素数据不完整");
                read += n;
            }
        }

        private static void Unfilter(int ft, byte[] cur, byte[] prev, int bpp, int stride)
        {
            switch (ft)
            {
                case 0: break;
                case 1:
                    for (int i = bpp; i < stride; i++) cur[i] = (byte)(cur[i] + cur[i - bpp]);
                    break;
                case 2:
                    for (int i = 0; i < stride; i++) cur[i] = (byte)(cur[i] + prev[i]);
                    break;
                case 3:
                    for (int i = 0; i < stride; i++)
                    {
                        int a = i >= bpp ? cur[i - bpp] : 0;
                        cur[i] = (byte)(cur[i] + ((a + prev[i]) >> 1));
                    }
                    break;
                case 4:
                    for (int i = 0; i < stride; i++)
                    {
                        int a = i >= bpp ? cur[i - bpp] : 0;
                        int bb = prev[i];
                        int cc = i >= bpp ? prev[i - bpp] : 0;
                        int pp = a + bb - cc;
                        int pa = Math.Abs(pp - a), pb = Math.Abs(pp - bb), pc = Math.Abs(pp - cc);
                        int pr = (pa <= pb && pa <= pc) ? a : (pb <= pc ? bb : cc);
                        cur[i] = (byte)(cur[i] + pr);
                    }
                    break;
                default:
                    throw new InvalidDataException($"不支持的 PNG filter {ft}");
            }
        }

        // ---------------- 写 ----------------

        /// <summary>写 8bit PNG。channels=3 为 RGB（color type 2），1 为灰度（color type 0）。</summary>
        public static void Write8(string path, int w, int h, int channels, byte[] data)
        {
            using var fs = File.Create(path);
            fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

            var ihdr = new byte[13];
            BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0), (uint)w);
            BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)h);
            ihdr[8] = 8;                                   // bit depth
            ihdr[9] = channels == 1 ? (byte)0 : (byte)2;   // color type
            WriteChunk(fs, "IHDR", ihdr);

            int stride = w * channels;
            using var ms = new MemoryStream();
            using (var ds = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                var row = new byte[stride + 1];
                for (int y = 0; y < h; y++)
                {
                    row[0] = 0;                                // filter: None
                    Array.Copy(data, y * stride, row, 1, stride);
                    ds.Write(row, 0, row.Length);
                }
            }
            WriteChunk(fs, "IDAT", ms.ToArray());
            WriteChunk(fs, "IEND", Array.Empty<byte>());
        }

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
            s.Write(len);

            var t = System.Text.Encoding.ASCII.GetBytes(type);
            s.Write(t);
            s.Write(data);            // ★ PNG 顺序是「长度|类型|数据|CRC」

            var crcInput = new byte[4 + data.Length];
            Array.Copy(t, 0, crcInput, 0, 4);
            Array.Copy(data, 0, crcInput, 4, data.Length);
            Span<byte> crc = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(crcInput));
            s.Write(crc);
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        private static uint Crc32(byte[] data)
        {
            uint c = 0xFFFFFFFF;
            foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFF;
        }
    }
}
