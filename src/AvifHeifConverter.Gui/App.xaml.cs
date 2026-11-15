using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AvifHeifConverter.Gui
{
    public partial class App : Application
    {
        private Window? _window;

        /// <summary>
        /// 启动日志。图形程序出错时不会像控制台那样把异常打出来，
        /// 早先就因此「进程在跑但窗口没出现」，无从判断卡在哪一步。
        /// 这里把关键节点写进 %LOCALAPPDATA%，出问题时有据可查。
        /// </summary>
        internal static void Trace(string msg)
        {
            try
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AvifHeifConverterGui");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "startup.log"),
                    $"{DateTime.Now:HH:mm:ss.fff}  {msg}{Environment.NewLine}");
            }
            catch { /* 日志写不了不能影响启动 */ }
        }

        public App()
        {
            Trace("=== App 构造 ===");
            InitializeComponent();
            Trace("InitializeComponent 完成");
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            Trace("OnLaunched 进入");

            // 未捕获异常兜底：宁可弹窗告知，也不要静默退出让用户摸不着头脑
            UnhandledException += (_, e) =>
            {
                Trace("未捕获异常：" + e.Message + "\n" + e.Exception);
                e.Handled = true;
                try
                {
                    var dlg = new ContentDialog
                    {
                        Title = "发生未处理的错误",
                        Content = e.Exception?.ToString() ?? e.Message,
                        CloseButtonText = "关闭",
                        XamlRoot = _window?.Content?.XamlRoot,
                    };
                    _ = dlg.ShowAsync();
                }
                catch (Exception ex2) { Trace("弹窗也失败了：" + ex2.Message); }
            };

            try
            {
                Services.ThemeService.ApplySaved();
                Trace($"主题已应用：{Services.ThemeService.Current}");
            }
            catch (Exception ex) { Trace("主题应用失败：" + ex.Message); }

            try
            {
                _window = new MainWindow();
                Trace("MainWindow 已构造");
                _window.Activate();
                Trace("MainWindow 已 Activate —— 窗口应当可见");
            }
            catch (Exception ex)
            {
                Trace("MainWindow 创建失败：" + ex);
                throw;
            }
        }
    }
}
