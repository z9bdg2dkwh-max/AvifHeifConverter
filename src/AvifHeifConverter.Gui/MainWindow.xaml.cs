using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AvifHeifConverter.Gui.Services;
using AvifHeifConverter.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using WinRT.Interop;
using Windows.Storage.Pickers;

namespace AvifHeifConverter.Gui
{
    public sealed partial class MainWindow : Window
    {
        private readonly List<FileItem> _files = new();
        private CancellationTokenSource? _cts;
        private bool _running;
        private readonly Dictionary<string, long> needCache = new();
        private string _engineDir = "";

        /// <summary>通知是否注册成功。失败时静默跳过，不影响转换。</summary>
        private bool _notificationsReady;

        /// <summary>任务栏进度（ITaskbarList3）。取不到就跳过。</summary>
        private ITaskbarList3? _taskbar;
        private IntPtr _hwnd;

        /// <summary>失败清单写入锁：多张失败文件在各自后台线程上并行归集，写入须串行。</summary>
        private static readonly object _failListLock = new object();

        public MainWindow()
        {
            // ★ 关窗确认走 Win32 消息钩子。
            //   WinUI 3 的 Window **没有可取消的 Closing 事件**
            //   （WindowEventArgs 连 Cancel 属性都没有），只能在首次激活后
            //   挂 WndProc 拦截 WM_CLOSE。
            this.Activated += MainWindow_FirstActivated;
            InitializeComponent();
            Title = "AVIF → HEIC 转换器";

            // ── 把内容延伸到标题栏 ──
            // 默认的系统标题栏固定跟随系统主题，会出现「浅色横条 + 深色内容」的割裂感。
            // 扩展后标题栏由本软件自绘，最小化/最大化/关闭三个系统按钮直接落在
            // 我们自己的底色上，整体观感统一。
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            ApplyTheme(ThemeService.Current);
            OutDirBox.Text = ThemeService.Get("outDir", "");
            FailDirBox.Text = ThemeService.Get("failDir", "");
            OpenFolderCheck.IsChecked = ThemeService.Get("openAfter", "0") == "1";

            // 注意：WinUI 3 的 Window 不是 FrameworkElement，**没有 Loaded 事件**，
            // 必须挂到根元素上（挂在 Window 上会编译报错，而该报错会被
            // XAML 编译器的崩溃掩盖，表现为一句毫无线索的 MSB3073）。
            RootGrid.Loaded += async (_, _) =>
            {
                InitNotifications();
                InitTaskbar();
                _ = NotifySelfTestAsync();
                ResolveEngine();
                UpdateTitleBarInset();      // 窗口就绪后 RightInset 才是有效值
                RestoreWindowBounds();      // 恢复上次的窗口大小与位置
                try
                {
                    var dir = Path.GetDirectoryName(LogFilePath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.AppendAllText(LogFilePath,
                        $"{Environment.NewLine}===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} 新会话 ====={Environment.NewLine}");
                }
                catch { }
                AppendLog("就绪。可把 .avif 文件拖入左侧列表，或点「添加文件」。");
                AppendLog("提示：转换产物请在 iPhone「照片」App 中查看 HDR 效果 ——");
                AppendLog("      微信内置的 HEIC 渲染不做色彩管理，显示异常不代表文件有问题。");

                // 引擎已定位，此处的命令行入队才能触发预扫
                EnqueueCommandLineFiles();

                // 调试用：把界面自身渲染成 PNG。
                // 目的是在无法看见窗口的环境下（例如由脚本启动）也能核对实际配色，
                // 不靠肉眼描述去猜。设置环境变量 AVIFHEIF_SHOT=<路径> 即触发。
                var shot = Environment.GetEnvironmentVariable("AVIFHEIF_SHOT");
                if (!string.IsNullOrWhiteSpace(shot))
                {
                    try
                    {
                        // 等引擎探测与首帧日志落到界面上再截，
                        // 否则截到的是空日志区，看不出引擎到底有没有认到
                        await Task.Delay(1200);
                        await CaptureSelfAsync(shot!);
                        AppendLog("[诊断] 已渲染界面到 " + shot);
                    }
                    catch (Exception ex) { AppendLog("[诊断] 渲染失败：" + ex.Message); }
                    try { Application.Current.Exit(); } catch { }
                }
            };

            Closed += (_, _) =>
            {
                try { _cts?.Cancel(); } catch { }
                try { _cts?.Dispose(); } catch { }
                _cts = null;
                FlushLogNow();   // ★ 把缓冲里尚未刷新的日志落盘
                SaveWindowBounds();
            };
        }

        // ---------------- 引擎定位 ----------------

        /// <summary>
        /// 定位 heif-enc / heif-dec 所在目录。
        /// 优先用程序目录下的 engine\，其次回退到开发机上的引擎路径，
        /// 两者都不存在时明确提示，而不是等转换时才失败。
        /// </summary>
        private void ResolveEngine()
        {
            var candidates = new List<string>
            {
                Path.Combine(AppContext.BaseDirectory, "engine"),
                Path.Combine(AppContext.BaseDirectory, "engine", "ucrt", "ucrt64", "bin"),
                @"D:\WorkBuddy\照片编辑\engine\ucrt\ucrt64\bin",
            };

            foreach (var dir in candidates)
            {
                if (File.Exists(Path.Combine(dir, "heif-enc.exe")) &&
                    File.Exists(Path.Combine(dir, "heif-dec.exe")))
                {
                    _engineDir = dir;
                    EngineBar.IsOpen = false;
                    AppendLog($"[引擎] {dir}");
                    return;
                }
            }

            EngineBar.Severity = InfoBarSeverity.Error;
            EngineBar.Title = "未找到转换引擎";
            EngineBar.Message = "缺少 heif-enc.exe / heif-dec.exe。请把 engine 文件夹放到程序目录下。";
            EngineBar.IsOpen = true;
            AppendLog("[引擎] ✗ 未找到 heif-enc.exe / heif-dec.exe，无法转换。");
        }

        // ---------------- 文件列表 ----------------

        /// <summary>
        /// 把命令行传入的文件/目录加入列表。
        ///
        /// 用途：把文件（或文件夹）拖到 exe 图标上就能直接转，也能给常用目录建快捷方式。
        /// 目录会递归展开，只收 .avif/.tif/.tiff。没有参数时整段直接返回。
        /// </summary>
        private void EnqueueCommandLineFiles()
        {
            string[] args;
            try { args = Environment.GetCommandLineArgs(); }
            catch { return; }
            if (args.Length <= 1) return;

            var paths = new List<string>();
            foreach (var a in args.Skip(1))
            {
                if (string.IsNullOrWhiteSpace(a) || a.StartsWith("--", StringComparison.Ordinal)) continue;
                try
                {
                    if (File.Exists(a))
                    {
                        paths.Add(a);
                    }
                    else if (Directory.Exists(a))
                    {
                        foreach (var f in Directory.EnumerateFiles(a, "*.*", SearchOption.AllDirectories))
                        {
                            var ext = Path.GetExtension(f);
                            if (ext.Equals(".avif", StringComparison.OrdinalIgnoreCase) ||
                                ext.Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
                                ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase))
                                paths.Add(f);
                        }
                    }
                }
                catch { /* 单个路径无效不影响其余 */ }
            }

            if (paths.Count == 0) return;

            AddFiles(paths);
            AppendLog($"[命令行] 从启动参数收到 {paths.Count} 个文件");
        }

        private void AddFiles(IEnumerable<string> paths)
        {
            int added = 0;
            var fresh = new List<FileItem>();
            foreach (var p in paths)
            {
                if (!p.EndsWith(".avif", StringComparison.OrdinalIgnoreCase) &&
                    !p.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) &&
                    !p.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase)) continue;
                if (_files.Any(x => string.Equals(x.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
                var item = new FileItem(p);
                _files.Add(item);
                fresh.Add(item);
                added++;
            }
            RefreshList();
            if (added > 0)
            {
                AppendLog($"[列表] 新增 {added} 个文件");
                if (_engineDir.Length > 0) _ = PreScanAsync(fresh);
            }
        }

        /// <summary>
        /// 后台预扫：提前判定源文件能不能转、值不值得转。
        /// 关键作用是避免「跑完整条管线（大图几分钟）才在最后一步报『无增益图』」。
        /// </summary>
        private async Task PreScanAsync(List<FileItem> items)
        {
            foreach (var it in items)
            {
                var item = it;
                string path = item.Path;
                try
                {
                    // ★ 源分两类：AVIF（HDR 通路）与 TIFF（ICC 驱动的通路）
                    bool isTiff = path.EndsWith(".tif", StringComparison.OrdinalIgnoreCase)
                               || path.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase);
                    string status;
                    bool skip = false;
                    if (isTiff)
                    {
                        var ti = await Task.Run(() => TiffReader.Probe(path)).ConfigureAwait(true);
                        if (ti == null) { status = "✗ TIFF 无法读取"; skip = true; }
                        else
                        {
                            var icc = ti.IccProfile != null ? IccColor.FromIcc(ti.IccProfile) : null;
                            if (icc == null) { status = "✗ 无 ICC 色彩描述"; skip = true; }
                            else if (icc.Transfer is 16 or 18)
                            { status = "✗ HDR TIFF（尚未支持，请按 SDR 导出）"; skip = true; }
                            else if (!icc.PrimariesIdentified || !icc.TransferIdentified)
                            { status = "✗ ICC 色彩未识别"; skip = true; }
                            else
                            {
                                string pn = icc.Primaries == 12 ? "Display P3" : icc.Primaries == 9 ? "Rec.2020" : "sRGB";
                                status = $"✓ TIFF · {ti.Width}×{ti.Height} · {pn} · 可转";
                            }
                        }
                    }
                    else
                    {
                        var prof = await Task.Run(() =>
                            FullPipeline.ProbeSource(path, _engineDir, _ => { })).ConfigureAwait(true);
                        if (!prof.HasGainMap)
                        {
                            // 无增益图 ≠ 不能转。
                            // 这类是「整图 PQ / SDR 直出」的 AVIF（如 Lightroom HDR 导出），
                            // 不需要基底+增益图那套，直接容器重封装即可，走直转通路。
                            // 判据不能只看 HasGainMap —— 之前在这里判死，导致整批图被跳过。
                            string kind = prof.Transfer == 16 ? "PQ 直出"
                                        : prof.Transfer == 18 ? "HLG 直出"
                                        : "SDR 直出";
                            status = $"✓ {kind} · 直转";
                            item.NeedsDirectConvert = true;
                        }
                        else
                        {
                            string kind = prof.Transfer == 18 ? "HLG 基底"
                                        : prof.IsSdrBase ? "SDR 基底"
                                        : "PQ 基底";
                            status = $"✓ {kind} · 可转";
                        }

                        // ★ 尺寸预检：HEVC Level 7.2 硬上限 142,606,336 个亮度采样。
                        //   超限的图 x265 直接拒绝，转也是白转（还要先解码几十秒）。
                        //   在这里标出来，用户一眼就知道哪些图需要先裁切/缩放。
                        var pxPre = Av1GridDecoder.TryProbePixels(path);
                        if (pxPre is > 142_606_336L)
                        {
                            skip = true;
                            status = $"✗ 尺寸超 HEVC 上限：{pxPre.Value:N0} 像素" +
                                     $"（超 {pxPre.Value * 100.0 / 142_606_336L - 100:0.#}%，需先裁切/缩放）";
                        }
                    }

                    // 输出已存在也提前提示（--overwrite 会覆盖，但用户应当知情）
                    try
                    {
                        var outDir = OutDirBox.Text.Trim();
                        var tgt = outDir.Length > 0
                            ? Path.Combine(outDir, Path.GetFileNameWithoutExtension(path) + ".heic")
                            : Path.Combine(Path.GetDirectoryName(path) ?? "",
                                           Path.GetFileNameWithoutExtension(path) + ".heic");
                        if (File.Exists(tgt)) status += " · 输出已存在";
                    }
                    catch { }

                    item.Status = status;
                    item.ShouldSkip = skip;
                    item.RowOpacity = skip ? 0.45 : 1.0;
                }
                catch (Exception ex)
                {
                    item.Status = "检查失败：" + ex.Message;
                }
            }
            AppendLog($"[预扫] 完成 {items.Count} 个文件");
        }

        private void RefreshList()
        {
            FileList.ItemsSource = null;
            FileList.ItemsSource = _files.ToList();
            CountText.Text = $"{_files.Count} 个文件";
            StartButton.IsEnabled = !_running && _files.Count > 0 && _engineDir.Length > 0;
        }

        private async void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            picker.FileTypeFilter.Add(".avif");
            picker.FileTypeFilter.Add(".tif");
            picker.FileTypeFilter.Add(".tiff");
            InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            // async void 里未捕获的异常会直接终结进程，凡是 await 都要接住
            try
            {
                var picked = await picker.PickMultipleFilesAsync();
                if (picked != null) AddFiles(picked.Select(f => f.Path));
            }
            catch (Exception ex) { AppendLog("[添加] 打开文件选择器失败：" + ex.Message); }
        }

        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in FileList.SelectedItems.Cast<FileItem>().ToList()) _files.Remove(item);
            RefreshList();
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            _files.Clear();
            RefreshList();
        }

        private void FileList_DragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        }

        private async void FileList_Drop(object sender, DragEventArgs e)
        {
            try
            {
                if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
                    return;
                var items = await e.DataView.GetStorageItemsAsync();
                AddFiles(items.OfType<Windows.Storage.StorageFile>().Select(f => f.Path));
            }
            catch (Exception ex) { AppendLog("[拖放] 失败：" + ex.Message); }
        }

        // ---------------- 设置 ----------------

        // 建议值（界面上的「建议 XX」与之对应，点击即恢复）。
        // 依据：
        //   基底质量 92 —— 照片存档的画质/体积平衡点
        //   增益图质量 90 —— 增益图本身是平滑的过渡图，90 足够且体积可控
        //   增益上限 2.2 —— 苹果原生文件的 HDRGainMapHeadroom 实测为 4.21（线性）
        //                   即 log2 ≈ 2.07，取 2.2 略高一点，HDR 感更明显
        private const int RecQuality = 92;
        private const int RecGmQuality = 90;
        private const double RecGainCap = 2.2;

        private void ResetQuality_Click(object sender, RoutedEventArgs e)
            => QualitySlider.Value = RecQuality;

        private void ResetGmQuality_Click(object sender, RoutedEventArgs e)
            => GmQualitySlider.Value = RecGmQuality;

        private void ResetGainCap_Click(object sender, RoutedEventArgs e)
            => GainCapSlider.Value = RecGainCap;

        /// <summary>
        /// 画质预设：一键设定质量与增益图质量。
        /// 选「自定义」不动滑块，交给用户自己调。
        /// </summary>
        private void QualityPresetBox_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (QualitySlider == null) return;
            switch (QualityPresetBox.SelectedIndex)
            {
                case 0: QualitySlider.Value = 92; GmQualitySlider.Value = 90; break;  // 均衡
                case 1: QualitySlider.Value = 97; GmQualitySlider.Value = 95; break;  // 高画质
                case 2: QualitySlider.Value = 82; GmQualitySlider.Value = 80; break;  // 小体积
                default: break;                                                        // 自定义
            }
            if (QualityPresetBox.SelectedIndex != 3)
                AppendLog($"  画质预设：{((ComboBoxItem)QualityPresetBox.SelectedItem).Content}" +
                          $"（质量 {QualitySlider.Value:0}，增益图 {GmQualitySlider.Value:0}）");
        }

        private void QualitySlider_ValueChanged(object s, RangeBaseValueChangedEventArgs e)
            => QualityText.Text = ((int)e.NewValue).ToString();

        private void GmQualitySlider_ValueChanged(object s, RangeBaseValueChangedEventArgs e)
            => GmQualityText.Text = ((int)e.NewValue).ToString();

        private void GainCapSlider_ValueChanged(object s, RangeBaseValueChangedEventArgs e)
            => GainCapText.Text = e.NewValue.ToString("0.0");

        /// <summary>自动匹配时，手动滑块置灰 —— 避免「改了没反应」的困惑。</summary>
        private void AutoCapCheck_Changed(object sender, RoutedEventArgs e)
        {
            bool auto = AutoCapCheck.IsChecked == true;
            GainCapSlider.IsEnabled = !auto;
            GainCapReset.IsEnabled = !auto;
            GainCapText.Opacity = auto ? 0.4 : 0.8;
        }

        private async void PickOutDir_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            try
            {
                var dir = await picker.PickSingleFolderAsync();
                if (dir != null)
                {
                    OutDirBox.Text = dir.Path;
                    ThemeService.Put("outDir", dir.Path);
                }
            }
            catch (Exception ex) { AppendLog("[输出目录] 打开选择器失败：" + ex.Message); }
        }

        private async void PickFailDir_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            try
            {
                var dir = await picker.PickSingleFolderAsync();
                if (dir != null)
                {
                    FailDirBox.Text = dir.Path;
                    ThemeService.Put("failDir", dir.Path);
                }
            }
            catch (Exception ex) { AppendLog("[失败归集] 打开选择器失败：" + ex.Message); }
        }

        /// <summary>
        /// 把转换失败的源文件复制到「失败归集目录」，并追加一行失败清单。
        ///
        /// 为什么需要：批量转换几百张时，失败的几张混在刷屏日志里，无法快速定位。
        /// 归集后：打开该目录即见失败文件本体，_转换失败清单.txt 里有完整原因与源路径。
        ///
        /// 注意：本方法在转换任务的后台线程上调用（不占 UI 线程）；
        ///       复制/写清单失败只记日志，绝不影响主流程。
        /// </summary>
        private void TryCollectFailedFile(string srcPath, string failDir, string reason)
        {
            if (string.IsNullOrWhiteSpace(failDir)) return;
            try
            {
                Directory.CreateDirectory(failDir);
                string dst = Path.Combine(failDir, Path.GetFileName(srcPath));
                if (!File.Exists(dst))
                {
                    File.Copy(srcPath, dst);
                    string fd = failDir;
                    DispatcherQueue.TryEnqueue(() => AppendLog($"  ↳ 失败文件已复制到：{Path.Combine(fd, Path.GetFileName(srcPath))}"));
                }

                // ★ 并发写保护（2026-10-16 修复）：
                //   多张失败文件在各自的后台线程上并行归集 —— 无锁的 AppendAllText
                //   会互相把清单文件占住（实测报错「file being used by another
                //   process」，该条记录直接丢失）。lock 串行化同进程写入；循环
                //   重试兜住外部进程（杀毒/索引等）的一瞬占用。
                string listPath = Path.Combine(failDir, "_转换失败清单.txt");
                var utf8Bom = new UTF8Encoding(true);
                string listLine =
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{Path.GetFileName(srcPath)}\t{reason}\t{srcPath}\r\n";
                lock (_failListLock)
                {
                    for (int attempt = 1; ; attempt++)
                    {
                        try
                        {
                            if (!File.Exists(listPath))
                            {
                                File.AppendAllText(listPath,
                                    "时间\t\t\t文件\t失败原因\t源路径\r\n" + new string('-', 100) + "\r\n",
                                    utf8Bom);
                            }
                            File.AppendAllText(listPath, listLine, utf8Bom);
                            break;
                        }
                        catch (IOException) when (attempt < 3)
                        {
                            Thread.Sleep(50);   // 仍失败则由外层 catch 兜底并记日志
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() => AppendLog($"  ↳ （失败文件归集未能完成：{ex.Message}）"));
            }
        }

        private void ThemeButton_Click(object sender, RoutedEventArgs e)
        {
            var next = ThemeService.Current switch
            {
                ElementTheme.Default => ElementTheme.Light,
                ElementTheme.Light => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
            ThemeService.Set(next);
            ApplyTheme(next);
        }

        private void ApplyTheme(ElementTheme theme)
        {
            if (Content is FrameworkElement root) root.RequestedTheme = theme;
            // WinUI 3 的 FrameworkElement 没有 ToolTip 属性，必须走附加属性
            ToolTipService.SetToolTip(ThemeButton, theme switch
            {
                ElementTheme.Light => "当前：浅色（点击切换）",
                ElementTheme.Dark => "当前：深色（点击切换）",
                _ => "当前：跟随系统（点击切换）",
            });

            // 扩展标题栏后，那三个系统按钮由我们负责配色 —— 不设的话它们在深色下
            // 会保持浅色描边，仍然突兀。
            bool dark = theme switch
            {
                ElementTheme.Dark => true,
                ElementTheme.Light => false,
                _ => Application.Current.RequestedTheme == ApplicationTheme.Dark,
            };
            ApplyCaptionButtonColors(dark);
            UpdateTitleBarInset();
        }

        /// <summary>按当前主题给系统标题栏按钮（最小化/最大化/关闭）配色。</summary>
        private void ApplyCaptionButtonColors(bool dark)
        {
            try
            {
                // ★ 设置窗口/任务栏图标。
                //   WinUI 3 免打包应用里 Window 没有 Icon 属性，必须走 AppWindow.SetIcon。
                //   路径用 AppContext.BaseDirectory：图标随程序一起复制到输出目录。
                try
                {
                    var ico = System.IO.Path.Combine(AppContext.BaseDirectory, "AppIcon.ico");
                    if (System.IO.File.Exists(ico)) AppWindow.SetIcon(ico);
                }
                catch { /* 图标设置失败不影响功能 */ }
                
                var tb = AppWindow.TitleBar;
                var fg = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
                tb.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
                tb.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
                tb.ButtonForegroundColor = fg;
                tb.ButtonInactiveForegroundColor = dark
                    ? Windows.UI.Color.FromArgb(255, 120, 120, 120)
                    : Windows.UI.Color.FromArgb(255, 130, 130, 130);
                // 悬停/按下用半透明叠加，深浅主题下都自然
                tb.ButtonHoverBackgroundColor = dark
                    ? Windows.UI.Color.FromArgb(30, 255, 255, 255)
                    : Windows.UI.Color.FromArgb(20, 0, 0, 0);
                tb.ButtonHoverForegroundColor = fg;
                tb.ButtonPressedBackgroundColor = dark
                    ? Windows.UI.Color.FromArgb(50, 255, 255, 255)
                    : Windows.UI.Color.FromArgb(35, 0, 0, 0);
                tb.ButtonPressedForegroundColor = fg;
            }
            catch (Exception ex) { App.Trace("标题栏配色失败：" + ex.Message); }
        }

        /// <summary>
        /// 标题栏右侧要让出系统按钮的宽度，否则主题按钮会被压住。
        /// RightInset 由系统给出，随 DPI/缩放变化，故每次都重新取。
        /// </summary>
        private void UpdateTitleBarInset()
        {
            try
            {
                double inset = AppWindow.TitleBar.RightInset;
                if (inset > 0)
                    ThemeButton.Margin = new Thickness(0, 0, inset + 4, 0);
            }
            catch { /* 取不到就保持默认边距 */ }
        }

        // ---------------- 窗口大小与位置记忆 ----------------

        private void RestoreWindowBounds()
        {
            try
            {
                if (ThemeService.Get("winW") is not { Length: > 0 } ws ||
                    ThemeService.Get("winH") is not { Length: > 0 } hs) return;
                if (!int.TryParse(ws, out int w) || !int.TryParse(hs, out int h)) return;
                if (w < 600 || h < 400) return;                    // 明显不合理，忽略

                AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));

                // 位置可能落在已拔掉的显示器上，做一次粗略的合理性检查
                if (int.TryParse(ThemeService.Get("winX"), out int x) &&
                    int.TryParse(ThemeService.Get("winY"), out int y) &&
                    x > -20000 && x < 20000 && y > -20000 && y < 20000)
                {
                    AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
                }
            }
            catch (Exception ex) { App.Trace("恢复窗口尺寸失败：" + ex.Message); }
        }

        private void SaveWindowBounds()
        {
            try
            {
                if (AppWindow.Presenter is not Microsoft.UI.Windowing.OverlappedPresenter p ||
                    p.State != Microsoft.UI.Windowing.OverlappedPresenterState.Restored)
                    return;                                        // 最大化/最小化时不覆盖已存尺寸
                ThemeService.Put("winW", AppWindow.Size.Width.ToString());
                ThemeService.Put("winH", AppWindow.Size.Height.ToString());
                ThemeService.Put("winX", AppWindow.Position.X.ToString());
                ThemeService.Put("winY", AppWindow.Position.Y.ToString());
            }
            catch { /* 存不下来不影响关闭 */ }
        }

        // ---------------- 通知与任务栏进度 ----------------

        /// <summary>
        /// 注册系统通知。批量转几十张图时人不会一直守着窗口，
        /// 转完弹一条通知比界面上换一行状态有用。
        ///
        /// 免打包应用走 SDK 自动生成的 AUMID，注册可能失败——失败就安静降级。
        /// </summary>
        private void InitNotifications()
        {
            try
            {
                var mgr = AppNotificationManager.Default;
                mgr.NotificationInvoked += (_, _) => { };
                mgr.Register();
                _notificationsReady = true;
                App.Trace("通知已注册");
            }
            catch (Exception ex)
            {
                _notificationsReady = false;
                App.Trace("通知注册失败：" + ex.Message);
            }
        }

        /// <summary>转换结束发一条通知，口径与界面状态栏一致。</summary>
        private void NotifyDone(int ok, int fail, int skipped, double seconds)
        {
            if (!_notificationsReady) return;

            try
            {
                bool clean = fail == 0;
                string skipNote = skipped > 0 ? $"，跳过 {skipped}" : "";
                var notification = new AppNotificationBuilder()
                    .AddText(clean ? "转换完成" : "转换结束")
                    .AddText($"成功 {ok}，失败 {fail}{skipNote}，用时 {seconds:0.#} 秒")
                    .BuildNotification();

                AppNotificationManager.Default.Show(notification);
                App.Trace($"转换完成通知已发送：成功 {ok}，失败 {fail}");
            }
            catch (Exception ex)
            {
                App.Trace("发送通知失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 初始化任务栏进度。WinUI 3 的 AppWindow 没有进度 API，只能走 shell 的
        /// ITaskbarList3；窗口最小化后仍能看到进度，这是它唯一但足够实用的价值。
        /// </summary>
        private void InitTaskbar()
        {
            try
            {
                var clsid = new Guid("56FDF344-FD6D-11d0-958A-006097C9A090");
                var type = Type.GetTypeFromCLSID(clsid);
                if (type == null) return;

                _taskbar = Activator.CreateInstance(type) as ITaskbarList3;
                _taskbar?.HrInit();
                _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            }
            catch (Exception ex)
            {
                _taskbar = null;
                App.Trace("任务栏进度初始化失败：" + ex.Message);
            }
        }

        /// <summary>把 Progress（0~1）同步到任务栏。</summary>
        private void SyncTaskbarProgress()
        {
            if (_taskbar == null || _hwnd == IntPtr.Zero) return;

            try
            {
                double v = Progress.Value;
                if (v <= 0)
                {
                    _taskbar.SetProgressState(_hwnd, TaskbarProgressState.NoProgress);
                    return;
                }
                _taskbar.SetProgressState(_hwnd, TaskbarProgressState.Normal);
                _taskbar.SetProgressValue(_hwnd, (ulong)Math.Round(v * 1000), 1000);
            }
            catch
            {
                // 画不出来不影响转换
            }
        }

        private void ClearTaskbarProgress()
        {
            if (_taskbar == null || _hwnd == IntPtr.Zero) return;
            try { _taskbar.SetProgressState(_hwnd, TaskbarProgressState.NoProgress); } catch { }
        }

        /// <summary>
        /// 通知自检：设置 AVIFHEIF_NOTIFY_TEST=1，启动后真发一条通知并把结果写进日志。
        ///
        /// 「Register 成功」只说明注册没报错；通知被专注助手拦截、AUMID 没落到开始菜单，
        /// 都会表现为「注册成功但看不到」。这个钩子用来区分这两种情况。
        /// </summary>
        private async Task NotifySelfTestAsync()
        {
            var flag = Environment.GetEnvironmentVariable("AVIFHEIF_NOTIFY_TEST");
            if (string.IsNullOrEmpty(flag)) return;

            await Task.Delay(2000);

            if (!_notificationsReady)
            {
                App.Trace("通知自检：跳过（未注册成功）");
            }
            else
            {
                try
                {
                    var n = new AppNotificationBuilder()
                        .AddText("AVIF 通知自检")
                        .AddText("看到这条，说明转换完成通知可用")
                        .BuildNotification();
                    AppNotificationManager.Default.Show(n);
                    App.Trace("通知自检：Show 已调用，无异常");
                }
                catch (Exception ex)
                {
                    App.Trace("通知自检：Show 抛异常 —— " + ex.Message);
                }
            }

            await Task.Delay(1500);
            try { Application.Current.Exit(); } catch { }
        }

        // ---------------- 转换 ----------------

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_running || _files.Count == 0) return;
            if (_engineDir.Length == 0) { ResolveEngine(); if (_engineDir.Length == 0) return; }

            _running = true;
            // 旧的 CTS 先释放（每次转换都新建一个，不释放会累积句柄）
            try { _cts?.Dispose(); } catch { }
            _cts = new CancellationTokenSource();
            StartButton.IsEnabled = false;
            CancelButton.IsEnabled = true;
            StartButtonText.Text = "转换中…";
            MainWindow_UpdateTitle();

            // ★ 整个转换过程包在 try/catch/finally 里。
            //   本方法是 async void —— **未捕获的异常会直接终结进程**，
            //   而且一旦中途抛出，后面的 UI 复位代码会被跳过，
            //   按钮将永远卡在「转换中…」。所以既要有兜底 catch，也要把复位放进 finally。
            try
            {

            var files = _files.ToList();
            var outDir = OutDirBox.Text.Trim();
            var failDir = FailDirBox.Text.Trim();   // 失败文件归集目录（空 = 不归集）
            ThemeService.Put("outDir", outDir);
            ThemeService.Put("failDir", failDir);
            ThemeService.Put("openAfter", OpenFolderCheck.IsChecked == true ? "1" : "0");

            // ★ 所有 UI 值必须在这里（UI 线程上）一次性取出。
            //   WinUI 的控件是 DependencyObject，**跨线程访问会抛
            //   RPC_E_WRONG_THREAD (0x8001010E)** —— 而并行转换用的是线程池线程。
            //   曾经漏取下面两个复选框，导致并行时每一张都在构造参数时立刻失败
            //   （33 张 0.4 秒全挂，日志刷满 0x8001010E）。
            int quality = (int)QualitySlider.Value;
            int gmQuality = (int)GmQualitySlider.Value;
            double gainCap = GainCapSlider.Value;
            bool useMultichannel = MultichannelCheck.IsChecked == true;
            bool useAutoCap = AutoCapCheck.IsChecked == true;
            bool openAfter = OpenFolderCheck.IsChecked == true;
            // 色度采样：0=444（默认）1=422 2=420
            bool overwriteExisting = OverwriteCheck?.IsChecked == true;
            string preset = "";   // x265 preset（界面暂不暴露，走引擎默认 slow）
            string chroma = (ChromaBox?.SelectedIndex ?? 0) switch
            {
                1 => "422",
                2 => "420",
                _ => "444",
            };

            int ok = 0, fail = 0, skipped = 0;
            var lastOut = "";
            var sw = Stopwatch.StartNew();

            // ---- ③ 磁盘空间预检 ----
            // 输出 HEIC 通常与源 AVIF 同量级，分块编码+中间文件还会额外占一些，按 3 倍估。
            try
            {
                long need = files.Where(e => !e.ShouldSkip)
                                 .Sum(e => new FileInfo(e.Path).Length) * 3;
                string probeDir = outDir.Length > 0 ? outDir
                    : Path.GetDirectoryName(files.FirstOrDefault(e => !e.ShouldSkip)?.Path ?? "C:////") ?? "C:////";
                // 输出目录可能还不存在，向上找到第一个存在的父目录
                while (!Directory.Exists(probeDir) && Path.GetDirectoryName(probeDir) is string up && up != probeDir)
                    probeDir = up;
                var drive = new DriveInfo(Path.GetPathRoot(probeDir) ?? "C:////");
                long free = drive.AvailableFreeSpace;
                double needGB = need / 1073741824.0, freeGB = free / 1073741824.0;
                AppendLog($"  磁盘预检：预计需 {needGB:0.00} GB，{drive.Name} 可用 {freeGB:0.00} GB");
                if (need > free)
                {
                    AppendLog($"  ✗ 磁盘空间不足，已中止（避免写到一半失败）");
                    StatusText.Text = "磁盘空间不足，已中止";
                    _running = false; StartButton.IsEnabled = true; CancelButton.IsEnabled = false;
                    StartButtonText.Text = "开始转换"; MainWindow_UpdateTitle();
                    return;
                }
            }
            catch (Exception ex) { AppendLog($"  （磁盘预检跳过：{ex.Message}）"); }

            // ---- ② 并行转换 ----
            // 各文件完全独立（各自的输出路径、各自的临时目录），可安全并行。
            // 内存随之成倍增加，所以张数由界面控制、默认 1 张。
            // 并行档位：0=自动（按内存余量动态决定）1/2/3=固定张数
            int parSel = ParallelCountBox?.SelectedIndex ?? 0;
            bool autoParallel = parSel <= 0;
            int par = autoParallel ? 1 : Math.Clamp(parSel, 1, 3);
            var queue = new List<(FileItem Entry, int Idx)>();
            int seq = 0;
            foreach (var entry in files)
            {
                seq++;
                if (entry.ShouldSkip)
                {
                    skipped++;
                    AppendLog("");
                    AppendLog($"──── [{seq}/{files.Count}] {entry.Name} ── 已跳过：{entry.Status}");
                    continue;
                }

                // ★ 兜底：预扫是异步即发即忘的，用户添加完文件立刻点「开始」时
                //   NeedsDirectConvert 可能还没被置位。此时现场补一次探测，
                //   否则这批「无增益图」的 AVIF 会误入 HDR 通路并全部报错。
                //   探测只读容器头，毫秒级；Status 仍是「待检查…」即表示未扫完。
                if (!entry.NeedsDirectConvert &&
                    entry.Status.StartsWith("待检查", StringComparison.Ordinal) &&
                    !entry.Path.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) &&
                    !entry.Path.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        // ★ 必须在后台线程探测：ProbeSource 会整读源文件（几十 MB），
                        //   直接放在 UI 线程会把窗口拖到「未响应」（批量补判时尤其严重）。
                        var prof2 = await Task.Run(() =>
                            FullPipeline.ProbeSource(entry.Path, _engineDir, _ => { })).ConfigureAwait(true);
                        if (!prof2.HasGainMap)
                        {
                            entry.NeedsDirectConvert = true;
                            entry.Status = "✓ 直转（未预扫，转换时补判）";
                        }
                    }
                    catch { /* 探测失败就按原路走，由管线自己报错 */ }
                }

                queue.Add((entry, seq));
            }
            if (autoParallel)
            {
                // 预探每张图的内存需求（只读容器头，毫秒级）
                needCache.Clear();
                long total = 0;
                foreach (var (qe, _) in queue)
                {
                    // ★ 按扩展名选探测方式（两者都只读元数据，毫秒级）：
                    //   AVIF 走容器头(grid 描述符/ispe)，TIFF 走 IFD。
                    //   此前只认 AVIF —— TIFF 探测失败会回退成保守的 2GB 估值，
                    //   导致整批被强制串行（3591 个文件要多等几小时）。
                    long? px;
                    if (qe.Path.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) ||
                        qe.Path.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase))
                    {
                        var ti = TiffReader.Probe(qe.Path);
                        px = ti != null && ti.Width > 0 && ti.Height > 0
                            ? (long)ti.Width * ti.Height : null;
                    }
                    else px = Av1GridDecoder.TryProbePixels(qe.Path);
                    long need = px != null ? MemoryGuard.EstimateFor(px.Value) : (1L << 31);
                    needCache[qe.Path] = need;
                    total += need;
                }
                var mi = MemoryGuard.Query();
                AppendLog($"  并行策略：自动（按内存余量）");
                AppendLog($"    系统内存 总 {MemoryGuard.Format(mi.Total)}，可用 {MemoryGuard.Format(mi.Available)}" +
                          $"，必须保留 {MemoryGuard.Format((long)(mi.Total * MemoryGuard.ReserveRatio))}（{MemoryGuard.ReserveRatio * 100:0.#}%）");
                AppendLog($"    本批 {queue.Count} 张，若全部同时跑需 {MemoryGuard.Format(total)}" +
                          $"（会按余量自动排队，不会占满）");
            }
            else if (par > 1) AppendLog($"  并行 {par} 张转换");
            if (chroma != "444") AppendLog($"  色度采样：{chroma}（默认 4:4:4）");

            int finished = 0;
            int inFlight = 0;
            using var sem = new SemaphoreSlim(par);
            var tasks = new List<Task>();

            // ★ 内存看护：转换全程每 3 秒采样系统内存，分级预警（级别变化时才输出，不刷屏）。
            //   为什么需要：准入闸门只在「启动新任务」时检查；若任务已全部启动，
            //   用户中途打开大程序（Lightroom/浏览器等）时没有感知。看护负责实时告知，
            //   配合 ReserveRatio=15% 的保留水位，防止内存被叠加占满。
            using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            var memoryWatch = Task.Run(async () =>
            {
                int lastLevel = 0;   // 0=正常 1=紧张 2=危险
                while (!watchCts.IsCancellationRequested)
                {
                    try { await Task.Delay(3000, watchCts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    var pr = MemoryGuard.GetPressure(out var mi);
                    int level = pr == MemoryGuard.Pressure.Critical ? 2
                              : pr == MemoryGuard.Pressure.Tight ? 1 : 0;
                    if (level == lastLevel) continue;
                    lastLevel = level;
                    string avail = MemoryGuard.Format(mi.Available);
                    if (level == 2)
                        DispatcherQueue.TryEnqueue(() => AppendLog(
                            $"  ⚠ 内存危险：可用仅 {avail}！已停止放行新任务 —— 强烈建议立刻关闭其他大程序（当前在跑的图片会先完成）"));
                    else if (level == 1)
                        DispatcherQueue.TryEnqueue(() => AppendLog(
                            $"  ⚠ 内存紧张：可用 {avail} —— 已暂停放行新任务，内存释放后会自动继续；也可主动关闭其他程序加速恢复"));
                    else
                        DispatcherQueue.TryEnqueue(() => AppendLog($"  ✓ 内存已恢复：可用 {avail}，继续放行新任务"));
                }
            });

            foreach (var (entry, fileIdx) in queue)
            {
                if (_cts.IsCancellationRequested) break;
                // ★ 点「停止」后 CancellationToken 立即进入取消态，
                //   SemaphoreSlim.WaitAsync(token) 会**直接抛 OperationCanceledException**。
                //   这里不接住的话，异常会从 async void 事件处理器逃出去 —— 进程当场崩溃
                //   （用户实测「点停止就闪退」）。
                if (autoParallel)
                {
                    // ★ 内存闸门：放得下才启动。
                    //   判据直接落在系统可用内存上 —— 放下这一张后仍要剩总内存的 10%。
                    //   同时给个 CPU 上限，避免余量大时把几十张一起塞进来抢 CPU。
                    long need = needCache.TryGetValue(entry.Path, out var nd) ? nd : (1L << 31);
                    // ★ 任务并行上限收紧（原为 CPU/2=12）：每个任务内部还有分块编码
                    //   （最多 12 个 heif-enc 子进程），两层相乘曾实测出现 124+ 个
                    //   heif-enc 并发运行、把系统内存压到 0.4 GB、新进程启动失败
                    //   （0xC000009A）。编码进程侧另有全局闸门（EngineGate），
                    //   这里从任务侧再收一道：24 核 → 4 个任务并行。
                    int cpuCap = Math.Max(2, Environment.ProcessorCount / 6);
                    bool announced = false;
                    while (!_cts.IsCancellationRequested)
                    {
                        int flying = Volatile.Read(ref inFlight);
                        bool memOk = MemoryGuard.CanAdmit(need, out var mnow);
                        if (flying < cpuCap && memOk) break;
                        // ★ 逃生通道：若已无任务在跑、内存却仍不够（例如系统被别的
                        //   程序占满），就**不能再等了** —— 否则这一张永远轮不上，
                        //   表现为进度条卡死在某一张。此时按单张硬跑并明确提示。
                        if (flying == 0)
                        {
                            DispatcherQueue.TryEnqueue(() => AppendLog(
                                $"  ⚠ 内存余量不足（可用 {MemoryGuard.Format(mnow.Available)}，本张需 {MemoryGuard.Format(need)}），" +
                                $"但已无任务在跑 —— 按单张执行，请勿同时运行其他大程序"));
                            break;
                        }
                        if (!announced)
                        {
                            DispatcherQueue.TryEnqueue(() => AppendLog(
                                $"  ⏳ 等待资源：当前并行 {flying} 张，可用 {MemoryGuard.Format(mnow.Available)}，" +
                                $"本张需 {MemoryGuard.Format(need)} —— 等前一批释放"));
                            announced = true;
                        }
                        try { await Task.Delay(400, _cts.Token).ConfigureAwait(true); }
                        catch (OperationCanceledException) { break; }
                    }
                    if (_cts.IsCancellationRequested) break;
                    Interlocked.Increment(ref inFlight);
                }
                else
                {
                    try { await sem.WaitAsync(_cts.Token).ConfigureAwait(true); }
                    catch (OperationCanceledException) { break; }
                }

                string shortName = entry.Name;
                string f = entry.Path;
                var target = outDir.Length > 0
                    ? Path.Combine(outDir, Path.GetFileNameWithoutExtension(f) + ".heic")
                    : Path.Combine(Path.GetDirectoryName(f) ?? "", Path.GetFileNameWithoutExtension(f) + ".heic");

                AppendLog("");
                AppendLog($"──── [{fileIdx}/{files.Count}] {shortName} ────");

                var t = Task.Run(async () =>
                {
                    try
                    {
                        bool isTiffFile = f.EndsWith(".tif", StringComparison.OrdinalIgnoreCase)
                                       || f.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase);
                        bool success; string err; bool skippedFlag;
                        if (!isTiffFile && entry.NeedsDirectConvert)
                        {
                            // ── 直转通路（无增益图的整图 PQ / SDR AVIF）──
                            // 这类文件不需要基底+增益图那套，直接交给 ConversionEngine
                            // 做容器重封装：heif-enc 原地读 AVIF、出 HEIC，色彩与位深原样保留。
                            var r = await ConversionEngine.ConvertAsync(
                                f,
                                new ConvertOptions
                                {
                                    OutputExtension = ".heic",
                                    OutputDirectory = outDir.Length > 0 ? outDir : null,
                                    Quality = quality,
                                    Chroma = chroma,
                                    Preset = preset,
                                    Overwrite = overwriteExisting,
                                    PreserveColorProfile = true,
                                },
                                _engineDir,
                                new Progress<string>(s => DispatcherQueue.TryEnqueue(() => AppendLog("  " + s))),
                                _cts.Token).ConfigureAwait(false);
                            success = r.Success;
                            err = r.Error ?? "";
                            skippedFlag = r.Skipped;   // ★ 输出已存在 = 跳过（与主通路一致），不再误算失败
                        }
                        else if (isTiffFile)
                        {
                            var (ts, te, tsk) = await TiffToHeic.RunAsync(
                                f,
                                new TiffToHeic.Options
                                {
                                    OutputPath = target,
                                    Quality = quality,
                                    Chroma = chroma,
                                    Preset = preset,
                                    Overwrite = overwriteExisting,
                                },
                                _engineDir,
                                s2 => DispatcherQueue.TryEnqueue(() => AppendLog(s2)),
                                _cts.Token).ConfigureAwait(false);
                            success = ts; err = te; skippedFlag = tsk;
                        }
                        else
                        {
                        var (s1, e1, _, sk1) = await FullPipeline.RunAsync(
                            f,
                            new FullPipeline.Options
                            {
                                OutputPath = target,
                                Quality = quality,
                                GainQuality = gmQuality,
                                GainCap = gainCap,
                                MultichannelGainMap = useMultichannel,
                                AutoGainCap = useAutoCap,
                                Chroma = chroma,
                                Overwrite = overwriteExisting,
                                OnStep = (step, total, msg) => DispatcherQueue.TryEnqueue(() =>
                                {
                                    double within = (double)(step - 1) / total;
                                    int fin = Volatile.Read(ref finished);
                                    Progress.Value = Math.Min(1.0, ((double)fin + within) / Math.Max(1, queue.Count));
                                    StatusText.Text = $"[{fileIdx}/{files.Count}] {shortName} — {msg}" +
                                        (par > 1 ? $"（并行 {par}）" : "");
                                    SyncTaskbarProgress();
                                }),
                            },
                            _engineDir,
                            s => DispatcherQueue.TryEnqueue(() => AppendLog(s)),
                            _cts.Token).ConfigureAwait(false);
                        success = s1; err = e1; skippedFlag = sk1;
                        }

                        if (success)
                        {
                            Interlocked.Increment(ref ok);
                            lastOut = target;
                        }
                        else if (skippedFlag)
                        {
                            // 同名跳过不是失败，单独计数，避免汇总里吓人
                            Interlocked.Increment(ref skipped);
                            DispatcherQueue.TryEnqueue(() => AppendLog($"  ○ {err}"));
                        }
                        else
                        {
                            Interlocked.Increment(ref fail);
                            DispatcherQueue.TryEnqueue(() => AppendLog($"  ✗ 失败：{err}"));
                            TryCollectFailedFile(f, failDir, err);   // ★ 归集失败文件（后台线程执行）
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        DispatcherQueue.TryEnqueue(() => AppendLog("  已取消（该文件已中断，子进程已停止）"));
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref fail);
                        DispatcherQueue.TryEnqueue(() => AppendLog($"  ✗ 异常：{ex.Message}"));
                        TryCollectFailedFile(f, failDir, "异常：" + ex.Message);
                    }
                    finally
                    {
                        var fin = Interlocked.Increment(ref finished);
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            Progress.Value = Math.Min(1.0, (double)fin / Math.Max(1, queue.Count));
                            SyncTaskbarProgress();
                        });
                        FullPipeline.CollectLargeObjects();
                        if (autoParallel) Interlocked.Decrement(ref inFlight);
                        else sem.Release();
                    }
                });
                tasks.Add(t);
            }

            try { await Task.WhenAll(tasks).ConfigureAwait(true); }
            catch (OperationCanceledException) { }

            watchCts.Cancel();   // ★ 停掉内存看护
            try { await memoryWatch.ConfigureAwait(true); } catch { }

            sw.Stop();
            Progress.Value = _cts.IsCancellationRequested ? 0 : 1;

            AppendLog("");
            string skipNote = skipped > 0 ? $"，跳过 {skipped}" : "";
            AppendLog($"══════ 完成：成功 {ok}，失败 {fail}{skipNote}，共 {files.Count} 张，" +
                      $"用时 {sw.Elapsed.TotalSeconds:0.#} 秒 ══════");
            StatusText.Text = $"完成：成功 {ok}，失败 {fail}{skipNote}，" +
                              $"用时 {sw.Elapsed.TotalSeconds:0.#} 秒";

            // 收尾：撤掉任务栏进度，并发一条系统通知
            ClearTaskbarProgress();
            NotifyDone(ok, fail, skipped, sw.Elapsed.TotalSeconds);

            if (ok > 0 && openAfter)
            {
                var dir = Path.GetDirectoryName(lastOut);
                if (!string.IsNullOrEmpty(dir)) OpenFolder(dir);
            }
            }
            catch (OperationCanceledException)
            {
                AppendLog("");
                AppendLog("已停止（当前文件已中断，子进程已停止）");
            }
            catch (Exception ex)
            {
                // 兜底：只记录，绝不向外抛（抛出去就是闪退）
                AppendLog("");
                AppendLog($"✗ 未预期的错误：{ex.GetType().Name} — {ex.Message}");
            }
            finally
            {
                // 无论正常结束、取消还是出错，UI 都必须复位
                _running = false;
                StartButton.IsEnabled = true;
                CancelButton.IsEnabled = false;
                StartButtonText.Text = "开始转换";
                Progress.Value = _cts?.IsCancellationRequested == true ? 0 : Progress.Value;
                ClearTaskbarProgress();
                MainWindow_UpdateTitle();
                // 转换结束释放 CTS，避免句柄累积
                try { _cts?.Dispose(); _cts = null; } catch { }
            }
        }

        // ---------------- 关窗确认（Win32 消息钩子）----------------
        //  WinUI 3 没有可取消的关窗事件，只能子类化窗口过程拦 WM_CLOSE。
        private const int GWLP_WNDPROC = -4;
        private const uint WM_CLOSE = 0x0010;

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr newLong);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr CallWindowProc(IntPtr prev, IntPtr hWnd, uint msg, IntPtr w, IntPtr l);

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
        private WndProcDelegate? _wndProc;      // 必须持有引用，否则会被 GC 回收导致崩溃
        private IntPtr _origWndProc = IntPtr.Zero;

        private void MainWindow_FirstActivated(object sender, WindowActivatedEventArgs e)
        {
            if (_origWndProc != IntPtr.Zero) return;      // 只挂一次
            this.Activated -= MainWindow_FirstActivated;
            try
            {
                IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                _wndProc = WndProcHook;
                IntPtr ptr = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_wndProc);
                _origWndProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC, ptr);
            }
            catch { _origWndProc = IntPtr.Zero; }
        }

        private IntPtr WndProcHook(IntPtr hWnd, uint msg, IntPtr w, IntPtr l)
        {
            if (msg == WM_CLOSE && _running && !_forceClose)
            {
                // 转换中：先问，不直接放行
                DispatcherQueue.TryEnqueue(ConfirmCloseWhileRunning);
                return IntPtr.Zero;      // 吞掉这次 WM_CLOSE
            }
            return CallWindowProc(_origWndProc, hWnd, msg, w, l);
        }

        private bool _forceClose;

        private bool _closeDialogOpen;   // 防重入：WinUI 同时只允许一个 ContentDialog

        private async void ConfirmCloseWhileRunning()
        {
            if (_closeDialogOpen) return;
            _closeDialogOpen = true;
            try
            {
            var dlg = new ContentDialog
            {
                Title = "正在转换",
                Content = "转换尚未完成。关闭窗口会立即中断，当前文件可能只写入了一半。\n\n确定要关闭吗？",
                PrimaryButtonText = "停止并关闭",
                CloseButtonText = "继续转换",
                XamlRoot = Content.XamlRoot,
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            {
                try { _cts?.Cancel(); } catch { }
                _forceClose = true;
                Close();
            }
            }
            catch (Exception)
            {
                // ★ WinUI 同一时刻只允许一个 ContentDialog；重复触发（例如连点两次关闭）
                //   时 ShowAsync 会抛异常 —— 不接住就是闪退。
            }
            finally { _closeDialogOpen = false; }
        }

        private void MainWindow_UpdateTitle()
            => Title = _running ? "AVIF → HEIC 转换器（转换中…）" : "AVIF → HEIC 转换器";

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            try { _cts?.Cancel(); AppendLog("正在停止（当前文件结束后中断）…"); } catch { }
        }

        private void OpenFolder(string path)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
            catch (Exception ex) { AppendLog("打开文件夹失败：" + ex.Message); }
        }

        // ---------------- 日志 ----------------

        // ---------------- 日志 ----------------

        /// <summary>日志文件路径：出错时可直接把这个文件发给对方。</summary>
        internal static readonly string LogFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AvifHeifConverterGui", "run.log");

        // ---------------- 日志写入（内存缓冲 + 节流刷新）----------------
        //
        // ★ 为什么不再「每条直接 LogBox.Text += s」：
        //   TextBox.Text 每次赋值都要重建整段文本并重排渲染，日志越长成本越高（O(n²)）。
        //   批量转换时每秒产生几十条日志，日志涨到数 MB 后，UI 线程被字符串复制
        //   和逐条同步写盘拖住 —— 实测表现为整窗「未响应」（Not Responding）。
        //   现在改为：每条只写入内存缓冲；每 250ms 把缓冲批量刷一次
        //   （界面保留最近 MaxLogBoxChars 个字符，文件日志始终全量）。
        private readonly object _logLock = new();
        private readonly StringBuilder _logPending = new();
        private readonly object _logFileLock = new();
        private int _logFlushScheduled = 0;
        private const int LogFlushIntervalMs = 250;
        private const int MaxLogBoxChars = 120_000;

        private void AppendLog(string s)
        {
            // 进缓冲即打时间戳（与文件日志格式一致：HH:mm:ss  文本）
            lock (_logLock)
            {
                _logPending.Append(DateTime.Now.ToString("HH:mm:ss"))
                           .Append("  ").Append(s).AppendLine();
            }
            // 已有批量刷新在等待时无需再排期 —— 缓冲内容会被它一起带走
            if (Interlocked.CompareExchange(ref _logFlushScheduled, 1, 0) != 0) return;
            _ = FlushLogAsync();
        }

        private async Task FlushLogAsync()
        {
            try
            {
                // ★ ConfigureAwait(false)：取缓冲、写文件都在线程池执行，
                //   不回到 UI 线程（本方法可能由 UI 线程启动）。
                await Task.Delay(LogFlushIntervalMs).ConfigureAwait(false);

                string batch;
                lock (_logLock)
                {
                    batch = _logPending.ToString();
                    _logPending.Clear();
                    Interlocked.Exchange(ref _logFlushScheduled, 0);
                }
                if (batch.Length == 0) return;

                // 1) 落盘（各次 flush 串行写，失败不影响主流程）
                WriteLogToFile(batch);

                // 2) 刷界面：一批一次赋值，并限制最大长度
                DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        var t = LogBox.Text + batch;
                        if (t.Length > MaxLogBoxChars)
                        {
                            t = t[^MaxLogBoxChars..];
                            int nl = t.IndexOf('\n');
                            if (nl >= 0) t = t[(nl + 1)..];
                        }
                        LogBox.Text = t;
                        LogBox.SelectionStart = t.Length;
                    }
                    catch { }
                });
            }
            catch { /* 日志失败不影响主流程 */ }
        }

        /// <summary>关窗时把缓冲里尚未刷新的日志立即落盘（避免丢最后几秒日志）。</summary>
        private void FlushLogNow()
        {
            string rest;
            lock (_logLock)
            {
                rest = _logPending.ToString();
                _logPending.Clear();
                Interlocked.Exchange(ref _logFlushScheduled, 0);
            }
            if (rest.Length > 0) WriteLogToFile(rest);
        }

        private void WriteLogToFile(string text)
        {
            try
            {
                lock (_logFileLock)
                {
                    var dir = Path.GetDirectoryName(LogFilePath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.AppendAllText(LogFilePath, text);
                }
            }
            catch { }
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e) => LogBox.Text = "";

        private void CopyLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(LogBox.Text);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
                AppendLog("[日志] 已复制到剪贴板");
            }
            catch (Exception ex) { AppendLog("[日志] 复制失败：" + ex.Message); }
        }

        private void OpenLogFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!File.Exists(LogFilePath))
                {
                    AppendLog("[日志] 文件尚未生成：" + LogFilePath);
                    return;
                }
                Process.Start(new ProcessStartInfo("explorer.exe",
                    $"/select,\"{LogFilePath}\"") { UseShellExecute = true });
            }
            catch (Exception ex) { AppendLog("[日志] 打开失败：" + ex.Message); }
        }

        /// <summary>
        /// 把根容器渲染成 PNG（调试用）。
        /// RenderTargetBitmap 取的是视觉树的真实像素，能如实反映主题资源解析结果，
        /// 用于在无法直接观察窗口时核对配色。
        /// </summary>
        private async Task CaptureSelfAsync(string path)
        {
            var rtb = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            await rtb.RenderAsync(RootGrid);
            var buf = await rtb.GetPixelsAsync();

            int w = rtb.PixelWidth, h = rtb.PixelHeight;
            var reader = Windows.Storage.Streams.DataReader.FromBuffer(buf);
            var bgra = new byte[buf.Length];
            reader.ReadBytes(bgra);

            // 输出格式是 BGRA8，转成 PNG 用的 RGB
            var rgb = new byte[w * h * 3];
            for (int i = 0; i < w * h; i++)
            {
                rgb[i * 3] = bgra[i * 4 + 2];
                rgb[i * 3 + 1] = bgra[i * 4 + 1];
                rgb[i * 3 + 2] = bgra[i * 4];
            }
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            PngCodec.Write8(path, w, h, 3, rgb);
        }
    }

    /// <summary>任务栏进度状态（shellapi.h 的 TBPFLAG）。</summary>
    internal enum TaskbarProgressState
    {
        NoProgress = 0x0,
        Indeterminate = 0x1,
        Normal = 0x2,
        Error = 0x4,
        Paused = 0x8,
    }

    /// <summary>
    /// ITaskbarList3 的最小投影：只声明到 SetProgressState 为止。
    ///
    /// COM 接口按 vtable 顺序调用，方法必须从 ITaskbarList → ITaskbarList2 →
    /// ITaskbarList3 依次列出，**不能只写用到的那两个**，否则调到的会是别的方法。
    /// 缩略图工具栏等方法用不到，可以省略。
    /// </summary>
    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [System.Runtime.InteropServices.InterfaceType(
        System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ITaskbarList3
    {
        // ---- ITaskbarList ----
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);

        // ---- ITaskbarList2 ----
        void MarkFullscreenWindow(IntPtr hwnd,
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
            bool fFullscreen);

        // ---- ITaskbarList3 ----
        void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
        void SetProgressState(IntPtr hwnd, TaskbarProgressState tbpFlags);
    }
}
