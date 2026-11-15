using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace AvifHeifConverter.Gui
{
    /// <summary>
    /// 文件队列里的一项。
    ///
    /// 除了路径，还带一个「体检结论」：加入列表后会后台预扫源文件，
    /// 提前标出「没有增益图 / 不是 HDR」这类注定失败的情况 ——
    /// 否则要等整条管线跑完（大图几分钟）才在最后一步报错，白等一场。
    /// </summary>
    public sealed class FileItem : INotifyPropertyChanged
    {
        public FileItem(string path)
        {
            Path = path;
            Name = System.IO.Path.GetFileName(path);
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists) SizeText = fi.Length >= 1024 * 1024
                    ? $"{fi.Length / 1048576.0:0.#} MB"
                    : $"{fi.Length / 1024.0:0} KB";
            }
            catch { SizeText = ""; }
        }

        public string Path { get; }
        public string Name { get; }
        public string SizeText { get; } = "";

        private string _status = "待检查…";
        /// <summary>体检结论，显示在文件名的右侧。</summary>
        public string Status
        {
            get => _status;
            set { if (_status != value) { _status = value; OnPropertyChanged(); } }
        }

        private bool _skip;
        /// <summary>预扫判定为「不值当转」（如无增益图），界面上置灰提示。</summary>
        public bool ShouldSkip
        {
            get => _skip;
            set { if (_skip != value) { _skip = value; OnPropertyChanged(); } }
        }

        private double _opacity = 1.0;
        public double RowOpacity
        {
            get => _opacity;
            set { if (System.Math.Abs(_opacity - value) > 0.001) { _opacity = value; OnPropertyChanged(); } }
        }

        private bool _direct;
        /// <summary>
        /// 预扫判定：源文件无 HDR 增益图，应走**直转通路**
        /// （容器重封装，保留原始色彩/位深），而非基底+增益图的 HDR 管线。
        ///
        /// 典型来源：Lightroom 以 HDR 模式导出的「整图 PQ」AVIF ——
        /// 这类文件结构上就是一张 PQ 编码的图，没有基底与增益图之分，
        /// 走 HDR 管线会被判定「无增益图」而拒绝。
        /// </summary>
        public bool NeedsDirectConvert
        {
            get => _direct;
            set { if (_direct != value) { _direct = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
