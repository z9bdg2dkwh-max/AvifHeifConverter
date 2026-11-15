using System.Threading;
using System.Threading.Tasks;

namespace AvifHeifConverter.Services
{
    /// <summary>
    /// 全局引擎进程闸门 —— 限制"全机同时运行的 heif-enc 进程数"。
    ///
    /// 为什么需要（2026-10-13 事故复盘）：
    ///   分块编码单任务最多开 degree 个 heif-enc（24 核机器 → 12 个），
    ///   而多任务并行又可达 cpuCap 个 —— 两层相乘曾实测出现 124+ 个 heif-enc
    ///   同时运行、合计占用 20+ GB 内存，把系统可用内存压到 0.4 GB，
    ///   新的 heif-enc 直接启动失败（0xC000009A STATUS_INSUFFICIENT_RESOURCES）。
    ///   在此统一收口：任何 heif-enc 启动前必须先过本闸门。
    ///
    /// 闸门容量 = 12：与"单任务分块并发度"一致 —— 多任务并行时，
    ///   全机编码进程负载也永远不超过"单任务一般"的水平（实测单进程 150–250 MB，
    ///   12 个合计约 1.8–3 GB，为系统留出充足余量）。
    /// </summary>
    internal static class EngineGate
    {
        /// <summary>全机同时允许运行的 heif-enc 进程上限。</summary>
        public const int MaxConcurrentEnc = 12;

        private static readonly SemaphoreSlim _enc = new(MaxConcurrentEnc, MaxConcurrentEnc);

        /// <summary>异步获取一个编码名额（取消时抛出 OperationCanceledException）。</summary>
        public static Task AcquireEncAsync(CancellationToken ct) => _enc.WaitAsync(ct);

        /// <summary>同步获取一个编码名额。</summary>
        public static void AcquireEnc(CancellationToken ct) => _enc.Wait(ct);

        /// <summary>释放编码名额（必须与 Acquire 成对，建议放在 finally）。</summary>
        public static void ReleaseEnc() => _enc.Release();
    }
}
