using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AvifHeifConverter.Services
{
    /// <summary>
    /// 从 heif-info -d 输出中读取 item 的属性关联（ipma）与属性定义（ipco）。
    ///
    /// 为什么不用手写 ipma 二进制解析：
    ///   实测苹果 ipma 的 flags=0（按 ISO 23008-12 应为 7 位索引），
    ///   但实际写入的是 15 位索引，与文档不符；固定按任一种解析都得到
    ///   错误索引。libheif 的输出已与 ipco/iinf 交叉验证一致，故以它为准。
    ///
    /// 输出格式：
    ///   | | associations for item ID: 106
    ///   | | | property index: 22 (essential: true)
    ///
    ///   | | | index: 1
    ///   | | Box: colr -----
    ///   | | size: 548   (header size: 8)
    /// </summary>
    internal static class HeifInfoProperties
    {
        /// <summary>一个属性 box 的定位信息。</summary>
        public sealed class PropertyDef
        {
            /// <summary>ipco 中的序号（从 1 开始）。</summary>
            public int Index { get; init; }
            /// <summary>box 类型，如 ispe / pixi / colr / av1C / hvcC。</summary>
            public string Type { get; init; } = "";
            /// <summary>该 box 在文件中的字节区间。</summary>
            public long Offset { get; init; }
            public long Size { get; init; }
        }

        /// <summary>item 到属性索引列表的关联。</summary>
        public sealed class Associations
        {
            /// <summary>itemId -> 属性索引。</summary>
            public Dictionary<uint, List<int>> ItemToProps { get; } = new();
            /// <summary>属性索引 -> 属性定义。</summary>
            public Dictionary<int, PropertyDef> Props { get; } = new();
        }

        private static readonly Regex ReAssocItem =
            new(@"associations for item ID:\s*(\d+)", RegexOptions.Compiled);

        private static readonly Regex RePropIndex =
            new(@"property index:\s*(\d+)\s*\((essential:\s*(true|false))?\)", RegexOptions.Compiled);

        // ipco 段的行形如："| | | index: 1" / "| | | Box: colr -----" / "| | | size: 548"
        private static readonly Regex ReIpcoIndex =
            new(@"^\|\s*\|\s*\|\s*index:\s*(\d+)\s*$", RegexOptions.Compiled);

        private static readonly Regex RePropBoxType =
            new(@"^\|\s*\|\s*\|\s*Box:\s*(\w{4})\s*-+", RegexOptions.Compiled);

        private static readonly Regex RePropBoxSize =
            new(@"^\|\s*\|\s*\|\s*size:\s*(\d+)\s+\(header size:\s*(\d+)\)", RegexOptions.Compiled);

        /// <summary>解析 heif-info -d 的完整输出。</summary>
        public static Associations? Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            var result = new Associations();
            var lines = text.Split('\n');

            bool inIpco = false, inIpma = false;
            uint curItem = 0;
            bool hasItem = false;
            int pendingIndex = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');

                // ---- 区段切换 ----
                if (line.Contains("Box: ipco"))
                {
                    inIpco = true; inIpma = false;
                    continue;
                }
                if (line.Contains("Box: ipma"))
                {
                    inIpco = false; inIpma = true;
                    hasItem = false;
                    continue;
                }
                if (inIpco && line.Contains("Box: ipma")) { inIpco = false; inIpma = true; continue; }
                if (inIpma && line.Contains("Box: ipco")) { inIpma = false; inIpco = true; continue; }

                // ipma 结束（下一个 Box: 出现）
                if (inIpma && line.Contains("Box: ") && !line.Contains("ipma"))
                {
                    inIpma = false;
                    hasItem = false;
                }

                // ---- ipco：属性定义 ----
                if (inIpco)
                {
                    var mIdx = ReIpcoIndex.Match(line);
                    if (mIdx.Success)
                    {
                        pendingIndex = int.Parse(mIdx.Groups[1].Value);
                        continue;
                    }

                    var mType = RePropBoxType.Match(line);
                    if (mType.Success && pendingIndex > 0)
                    {
                        // 紧随其后的是 size 行
                        for (int k = i + 1; k < Math.Min(i + 3, lines.Length); k++)
                        {
                            var mSize = RePropBoxSize.Match(lines[k].TrimEnd('\r'));
                            if (!mSize.Success) continue;

                            result.Props[pendingIndex] = new PropertyDef
                            {
                                Index = pendingIndex,
                                Type = mType.Groups[1].Value,
                                Size = long.Parse(mSize.Groups[1].Value),
                            };
                            break;
                        }
                        pendingIndex = 0;
                    }
                    continue;
                }

                // ---- ipma：item→属性 关联 ----
                if (inIpma)
                {
                    var mItem = ReAssocItem.Match(line);
                    if (mItem.Success)
                    {
                        curItem = uint.Parse(mItem.Groups[1].Value);
                        hasItem = true;
                        if (!result.ItemToProps.ContainsKey(curItem))
                            result.ItemToProps[curItem] = new List<int>();
                        continue;
                    }

                    var mProp = RePropIndex.Match(line);
                    if (mProp.Success && hasItem)
                    {
                        int pi = int.Parse(mProp.Groups[1].Value);
                        // essential 标志编码进 bit 16（与 IsoBmffReader 约定一致）。
                        // essential = "解码器必须理解此属性，否则拒绝文件"，
                        // 误标会让严格解码器（如 iOS）直接判文件损坏。
                        bool essential = mProp.Groups.Count > 3 &&
                                         mProp.Groups[3].Value == "true";
                        result.ItemToProps[curItem].Add(essential ? pi | 0x10000 : pi);
                        continue;
                    }
                }
            }

            return result.ItemToProps.Count > 0 ? result : null;
        }
    }
}