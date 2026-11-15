using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AvifHeifConverter.Services
{
    /// <summary>
    /// 从 libheif 的 heif-info -d 输出中读取 item 的数据位置。
    ///
    /// 为什么不用手写 iloc 二进制解析：
    ///   iloc 的字段宽度在不同厂商实现里与ISO 23008-12 文档不完全一致
    ///   （实测苹果 iloc version 1 的 item_ID / construction_method
    ///   宽度与文档不符，反复解析失败）。
    ///   heif-info 是 libheif 官方工具，其输出已与 ipco/iinf 交叉验证一致，
    ///   用它作为位置来源可彻底避免"猜字节布局"。
    ///
    /// 输出格式（每item 6~7 行）：
    ///   | item ID: 106
    ///   |   construction method: 1
    ///   |   data_reference_index: 0
    ///   |   base_offset: 0
    ///   |   extents: 24,62
    /// </summary>
    internal static class HeifInfoLocator
    {
        /// <summary>一个 item 的数据位置。</summary>
        public sealed class ItemLocation
        {
            public uint ItemId { get; init; }
            /// <summary>0 = 数据在 mdat（偏移为文件绝对位置）；1 = 数据在 idat。</summary>
            public int Method { get; init; }
            public long BaseOffset { get; init; }
            /// <summary>各extent 的 (偏移, 长度)。多数 item 只有 1 个。</summary>
            public List<(long Offset, long Length)> Extents { get; init; } = new();

            public long TotalLength => Extents.Sum(e => e.Length);
        }

        private static readonly Regex ReItemId =
            new(@"^\|\s*item ID:\s*(\d+)\s*$", RegexOptions.Compiled);

        private static readonly Regex ReMethod =
            new(@"construction method:\s*(\d+)", RegexOptions.Compiled);

        private static readonly Regex ReBaseOffset =
            new(@"base_offset:\s*(\d+)", RegexOptions.Compiled);

        private static readonly Regex ReExtents =
            new(@"extents:\s*(.+?)\s*$", RegexOptions.Compiled);

        /// <summary>
        /// 调用 heif-info -d 并返回其完整文本输出。
        /// 同一份输出同时含iloc（数据位置）与 ipma（属性关联）两段，
        /// 由 ParseIloc / HeifInfoProperties 分别消费，避免重复启动子进程。
        /// </summary>
        public static string? Dump(string engineDir, byte[] fileBytes, int timeoutMs = 120_000)
        {
            var exe = Path.Combine(engineDir, "heif-info.exe");
            if (!File.Exists(exe)) return null;

            // heif-info 走 ANSI fopen，中文路径会失败 -> ASCII 临时文件
            string tmp = Path.Combine(Path.GetTempPath(),
                        "heifloc_" + Guid.NewGuid().ToString("N") + ".avif");
            try
            {
                File.WriteAllBytes(tmp, fileBytes);

                var psi = new ProcessStartInfo(exe)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                psi.ArgumentList.Add("-d");
                psi.ArgumentList.Add(tmp);

                var existing = psi.Environment.TryGetValue("PATH", out var old) ? old : "";
                psi.Environment["PATH"] = engineDir + ";" + existing;

                using var proc = new Process { StartInfo = psi };
                proc.Start();

                var stdout = proc.StandardOutput.ReadToEnd();
                proc.StandardError.ReadToEnd();

                if (!proc.WaitForExit(timeoutMs))
                {
                    try { proc.Kill(true); } catch { }
                    return null;
                }
                return stdout;
            }
            catch
            {
                return null;
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        /// <summary>调用 heif-info -d 并解析出全部 item 位置。返回 null 表示失败。</summary>
        public static Dictionary<uint, ItemLocation>? Locate(
            string engineDir, byte[] fileBytes, int timeoutMs = 120_000)
            => ParseIloc(Dump(engineDir, fileBytes, timeoutMs));

        public static Dictionary<uint, ItemLocation>? ParseIloc(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            var map = new Dictionary<uint, ItemLocation>();

            bool inIloc = false;
            uint curId = 0;
            int curMethod = 0;
            long curBase = 0;
            var curExt = new List<(long, long)>();

            void Flush()
            {
                if (curId != 0)
                {
                    map[curId] = new ItemLocation
                    {
                        ItemId = curId,
                        Method = curMethod,
                        BaseOffset = curBase,
                        Extents = new List<(long, long)>(curExt),
                    };
                }
                curExt.Clear();
            }

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');

                if (line.Contains("Box: iloc"))
                {
                    inIloc = true;
                    continue;
                }
                if (inIloc && line.Contains("Box: ") && !line.Contains("iloc"))
                {
                    Flush();
                    inIloc = false;
                    curId = 0;
                    continue;
                }
                if (!inIloc) continue;

                var mId = ReItemId.Match(line);
                if (mId.Success)
                {
                    Flush();
                    curId = uint.Parse(mId.Groups[1].Value);
                    curMethod = 0;
                    curBase = 0;
                    continue;
                }

                if (curId == 0) continue;

                var mMethod = ReMethod.Match(line);
                if (mMethod.Success)
                {
                    curMethod = int.Parse(mMethod.Groups[1].Value);
                    continue;
                }

                var mBase = ReBaseOffset.Match(line);
                if (mBase.Success)
                {
                    curBase = long.Parse(mBase.Groups[1].Value);
                    continue;
                }

                var mExt = ReExtents.Match(line);
                if (mExt.Success)
                {
                    // 格式: "off,len off,len ..."（空格分隔多 extent）
                    var parts = mExt.Groups[1].Value.Split(
                        new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var part in parts)
                    {
                        var kv = part.Split(',');
                        if (kv.Length == 2
                            && long.TryParse(kv[0], out var o)
                            && long.TryParse(kv[1], out var l))
                        {
                            curExt.Add((o, l));
                        }
                    }
                    continue;
                }
            }
            Flush();

            return map.Count > 0 ? map : null;
        }
    }
}