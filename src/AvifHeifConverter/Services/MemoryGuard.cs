using System;
using System.Runtime.InteropServices;

namespace AvifHeifConverter.Services
{
    /// <summary>
    /// 内存探测与准入控制。
    ///
    /// 目标：并行转换时**按实际内存余量动态决定能同时跑几张**，
    /// 尽量吃满系统余量，但**始终给系统留出 10%**，绝不占满导致卡死。
    ///
    /// 为什么不直接用「固定并行张数」：每张图的占用差异极大 ——
    /// 实测 60MP 峰值约 1.4 GB、238MP 约 4.8 GB（约 7 倍）。
    /// 固定 3 张时，转 238MP 会要 14 GB；固定 1 张又浪费了小图的并行机会。
    /// </summary>
    internal static class MemoryGuard
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        public readonly struct MemInfo
        {
            public long Total { get; init; }
            public long Available { get; init; }
            /// <summary>系统整体已用百分比。</summary>
            public double UsedPercent => Total > 0 ? 100.0 * (Total - Available) / Total : 0;
        }

        public static MemInfo Query()
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref m))
                return new MemInfo { Total = (long)m.ullTotalPhys, Available = (long)m.ullAvailPhys };
            // 兜底：拿不到系统信息时按 16 GB / 8 GB 可用估，宁保守不激进
            return new MemInfo { Total = 16L << 30, Available = 8L << 30 };
        }

        /// <summary>
        /// 必须给系统留出的余量比例。
        ///
        /// ★ 2026-10-13 由 10% 提高至 15%：用户常在转换时同时开其他程序
        ///   （Lightroom、浏览器等），10% 的余量（本机约 3.2 GB）留得太薄，
        ///   容易在"转换放行 + 用户开大程序"叠加时触顶。15%（约 4.7 GB）
        ///   给中途开程序留出缓冲，配合「内存看护」实时提示，防止爆满。
        /// </summary>
        public const double ReserveRatio = 0.15;

        /// <summary>内存压力分级。</summary>
        public enum Pressure
        {
            /// <summary>正常（可用 ≥ 20%）。</summary>
            Normal,
            /// <summary>紧张（可用 &lt; 20%）：转换已暂停放行新任务，用户应留意。</summary>
            Tight,
            /// <summary>危险（可用 &lt; 10%）：逼近极限，用户应立刻关闭其他大程序。</summary>
            Critical,
        }

        /// <summary>查询当前内存压力等级（供转换期间的实时看护使用）。</summary>
        public static Pressure GetPressure(out MemInfo info)
        {
            info = Query();
            double availRatio = info.Total > 0 ? (double)info.Available / info.Total : 0;
            if (availRatio < 0.10) return Pressure.Critical;
            if (availRatio < 0.20) return Pressure.Tight;
            return Pressure.Normal;
        }

        /// <summary>
        /// 单张转换的峰值内存估算。
        ///
        /// 按实测标定为 **20 字节/像素** + 约 0.35 GB 固定开销（运行时 + 引擎子进程 + 中间文件）。
        ///
        /// ★ 刻意取保守值：准入控制中**低估内存是危险的**（会放太多任务进来，
        ///   反而把系统压满）。实测对照：
        ///   26MP 实测 0.6 GB / 本式 0.9 GB；60MP 实测 1.4 GB / 本式 1.55 GB；
        ///   238MP 实测 4.8 GB / 本式 5.1 GB —— 始终略高于实测，留安全边际。
        /// </summary>
        public static long EstimateFor(long pixels)
            => (long)(0.35e9 + pixels * 20.0);

        /// <summary>
        /// 能否再放一张进来。
        /// 判据直接落在「可用内存」上：放下这一张之后，系统仍要剩总内存的 10%。
        /// </summary>
        public static bool CanAdmit(long needBytes, out MemInfo info)
        {
            info = Query();
            long reserve = (long)(info.Total * ReserveRatio);
            return info.Available - needBytes >= reserve;
        }

        public static string Format(long bytes) => $"{bytes / 1073741824.0:0.0} GB";
    }
}
