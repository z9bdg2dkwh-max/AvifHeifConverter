using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace AvifHeifConverter.Services
{
    /// <summary>
    /// 把源文件的增益图单独取出并**重新编码为 HEVC（4:4:4）**，
    /// 使输出文件成为「全 HEVC」——与苹果自带转换产物（IMG_8208.HEIC）一致。
    ///
    /// 为什么可能需要这一步：
    ///   AVIF 的增益图是 AV1（av01）item。把主图转成 HEVC 后，若整份文件按
    ///   heic/heix 品牌声明、却仍混着 av01 item，就构成"品牌与编解码器不一致"，
    ///   严格读取器可能直接拒绝。经查证，苹果自家产出的 HEIC 里
    ///   增益图一律是 hvc1（HEVC），从未出现 av01。
    ///
    /// 代价：增益图会被重新编码，不再字节级无损。
    ///       所以这是**可选开关**；默认仍走"原样搬运、零损失"。
    ///
    /// 流程：
    ///   源文件 →（临时把主项指向增益图、并隐藏其它图像）
    ///          → heif-dec 导出 PNG
    ///          → heif-enc 编码 HEVC 4:4:4 / 10bit
    ///          → 从结果里取出 hvc1 item 与其属性 box
    /// </summary>
    internal static class GainMapHevcTranscoder
    {
        public sealed class Result
        {
            public byte[] ItemData { get; init; } = Array.Empty<byte>();
            /// <summary>按 ipma 顺序排列的属性 box 字节。</summary>
            public List<byte[]> PropertyBoxes { get; init; } = new();
            /// <summary>与 PropertyBoxes 对应的 1-based 索引（含 essential 位 0x10000）。</summary>
            public List<int> PropertyIndices { get; init; } = new();
            public int Width { get; init; }
            public int Height { get; init; }
            public int BitDepth { get; init; }
        }

        /// <summary>
        /// 返回 null 表示失败（调用方应回退为"原样搬运"）。
        /// </summary>
        public static Result? Transcode(
            byte[] srcBytes, uint gainMapItemId, string engineDir,
            int quality, bool lossless, Action<string>? log)
        {
            string? dir = null;
            try
            {
                if (gainMapItemId == 0) return null;

                dir = Path.Combine(Path.GetTempPath(), "avifgm_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);

                // ---- 1) 造一份"只看得见增益图"的临时 AVIF ----
                var solo = Patch((byte[])srcBytes.Clone(), gainMapItemId);
                if (solo == null) { log?.Invoke("增益图转码：无法定位 pitm / infe"); return null; }
                string soloPath = Path.Combine(dir, "solo.avif");
                File.WriteAllBytes(soloPath, solo);

                // ---- 2) 导出增益图像素 ----
                string pngPath = Path.Combine(dir, "gm.png");
                int rc = Run(Path.Combine(engineDir, "heif-dec.exe"), engineDir,
                    new[] { "--with-aux", "--no-colons", soloPath, pngPath }, log);
                if (!File.Exists(pngPath))
                {
                    var alt = Directory.GetFiles(dir, "gm-*.png")
                                       .OrderByDescending(f => new FileInfo(f).Length)
                                       .FirstOrDefault();
                    if (alt == null)
                    {
                        log?.Invoke($"增益图导出失败（heif-dec rc={rc}）");
                        return null;
                    }
                    pngPath = alt;
                }

                // ---- 3) 编码为 HEVC 4:4:4 / 10bit ----
                string hevcPath = Path.Combine(dir, "gm.heic");
                var encArgs = new List<string> { pngPath, "-o", hevcPath, "--hevc", "-b", "10" };
                if (lossless)
                {
                    encArgs.Add("-L");
                }
                else
                {
                    encArgs.Add("-q");
                    encArgs.Add(Math.Clamp(quality, 1, 100).ToString());
                }
                encArgs.Add("-p");
                encArgs.Add("chroma=444");   // 保留 4:4:4，避免色度抽样损失

                // ★ 全局闸门：限制全机同时运行的 heif-enc 总数（见 EngineGate 注释）
                EngineGate.AcquireEnc(CancellationToken.None);
                try
                {
                    rc = Run(Path.Combine(engineDir, "heif-enc.exe"), engineDir, encArgs, log);
                }
                finally { EngineGate.ReleaseEnc(); }
                if (rc != 0 || !File.Exists(hevcPath))
                {
                    log?.Invoke($"增益图 HEVC 编码失败（rc={rc}）");
                    return null;
                }

                // ---- 4) 从结果里取出 item 数据与属性 ----
                var res = ExtractSingle(File.ReadAllBytes(hevcPath), engineDir, log);
                if (res == null) log?.Invoke("增益图 HEVC 结果解析失败");
                return res;
            }
            catch (Exception ex)
            {
                log?.Invoke("增益图转码异常：" + ex.Message);
                return null;
            }
            finally
            {
                try { if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, true); }
                catch { /* 临时目录清理失败不影响结果 */ }
            }
        }

        // ---------------- 内部：临时文件改写 ----------------

        /// <summary>
        /// 把 pitm 指向增益图 item，并隐藏其余全部 item。
        /// 这样 heif-dec 只会导出一张图（增益图本身）。
        /// 纯字节改写，**不触碰源文件**（在副本上做）。
        /// </summary>
        private static byte[]? Patch(byte[] b, uint gainMapItemId)
        {
            try
            {
                var r = new IsoBmffReader(b);
                var pitm = r.FindInMeta("pitm");
                var iinf = r.FindInMeta("iinf");
                if (pitm == null || iinf == null) return null;

                // pitm 的 item_ID：payload(ver/flags 4) 之后
                long idPos = pitm.Value.PayloadOffset + 4;
                byte ver = b[(int)pitm.Value.PayloadOffset];
                if (ver == 0)
                    BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan((int)idPos, 2), (ushort)gainMapItemId);
                else
                    BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan((int)idPos, 4), gainMapItemId);

                // 遍历 iinf 里的 infe，清掉目标 item 的 hidden，其余置 hidden
                long p = iinf.Value.PayloadOffset + 4;
                int cnt = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan((int)p, 2));
                p += 2;
                long end = iinf.Value.Offset + iinf.Value.Size;
                int seen = 0;
                while (p + 21 <= end && seen < cnt)
                {
                    int sz = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)p, 4));
                    if (sz < 21 || p + sz > end) break;
                    uint id = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan((int)p + 12, 2));
                    // flags 低字节在 box 起始 +8(头) +3
                    b[(int)p + 11] = (byte)(id == gainMapItemId ? 0x00 : 0x01);
                    p += sz;
                    seen++;
                }
                return b;
            }
            catch { return null; }
        }

        // ---------------- 内部：单图 HEIC 解析 ----------------

        private static Result? ExtractSingle(byte[] bytes, string engineDir, Action<string>? log)
        {
            var r = new IsoBmffReader(bytes);
            uint primary = r.ParsePrimaryItemId();
            if (primary == 0) return null;

            var props = r.ParsePropertyBoxes();
            var assoc = r.ParsePropertyAssociations();

            var locator = HeifInfoLocator.Locate(engineDir, bytes);
            if (locator == null || !locator.TryGetValue(primary, out var loc)) return null;

            var idat = r.FindInMeta("idat");
            long idatBase = idat?.PayloadOffset ?? 0;

            var data = new List<byte>();
            foreach (var (off, len) in loc.Extents)
            {
                if (len <= 0) continue;
                long start = loc.Method == 1 ? idatBase + loc.BaseOffset + off : loc.BaseOffset + off;
                if (start < 0 || start + len > bytes.Length) return null;
                var chunk = new byte[len];
                Array.Copy(bytes, start, chunk, 0, (int)len);
                data.AddRange(chunk);
            }
            if (data.Count == 0) return null;

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

            // 尺寸/位深（从 ispe / pixi 读，读不到则给 0）
            int w = 0, h = 0, depth = 0;
            foreach (var raw in indices)
            {
                var box = props[(raw & 0xFFFF) - 1];
                if (box.Type == "ispe")
                {
                    var d = r.ReadIspe(box);
                    if (d != null) { w = d.Value.Width; h = d.Value.Height; }
                }
                else if (box.Type == "pixi")
                {
                    var d = r.ReadPixi(box);
                    if (d != null && d.Value.Depths.Length > 0) depth = d.Value.Depths[0];
                }
            }

            return new Result
            {
                ItemData = data.ToArray(),
                PropertyBoxes = boxes,
                PropertyIndices = indices,
                Width = w, Height = h, BitDepth = depth,
            };
        }

        // ---------------- 内部：子进程 ----------------

        private static int Run(string exe, string engineDir, IEnumerable<string> args,
                               Action<string>? log)
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
            var so = proc.StandardOutput.ReadToEnd();
            var se = proc.StandardError.ReadToEnd();
            proc.WaitForExit(600_000);

            if (log != null)
            {
                foreach (var line in (so + "\n" + se).Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line)) log("    " + line.TrimEnd());
            }
            return proc.ExitCode;
        }
    }
}
