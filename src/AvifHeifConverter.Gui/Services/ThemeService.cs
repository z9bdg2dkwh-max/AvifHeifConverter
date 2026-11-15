using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.UI.Xaml;

namespace AvifHeifConverter.Gui.Services
{
    /// <summary>
    /// 主题与偏好的持久化。
    ///
    /// 免打包（WindowsPackageType=None）的应用不能用 ApplicationData.Current，
    /// 故自行在 %LOCALAPPDATA% 下维护一个 JSON 设置文件。
    /// 写入采用「临时文件 + 替换」，避免中途崩溃留下半个文件。
    /// </summary>
    internal static class ThemeService
    {
        private static readonly string Dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "AvifHeifConverterGui");
        private static readonly string FilePath = Path.Combine(Dir, "settings.json");

        public static ElementTheme Current { get; private set; } = ElementTheme.Default;

        /// <summary>读取并应用已保存的主题。</summary>
        public static void ApplySaved() => Current = Parse(Load("theme"));

        public static void Set(ElementTheme theme)
        {
            Current = theme;
            Save("theme", theme switch
            {
                ElementTheme.Light => "light",
                ElementTheme.Dark => "dark",
                _ => "system",
            });
        }

        public static string Get(string key, string fallback = "")
            => Load(key) is { Length: > 0 } v ? v : fallback;

        public static void Put(string key, string value) => Save(key, value);

        private static ElementTheme Parse(string s) => s switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        // ---------------- 存储 ----------------

        private static string Load(string key)
        {
            try
            {
                if (!File.Exists(FilePath)) return "";
                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(FilePath));
                return map != null && map.TryGetValue(key, out var v) ? v ?? "" : "";
            }
            catch { return ""; }
        }

        private static void Save(string key, string value)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                Dictionary<string, string> map = new();
                if (File.Exists(FilePath))
                {
                    try
                    {
                        map = JsonSerializer.Deserialize<Dictionary<string, string>>(
                            File.ReadAllText(FilePath)) ?? new Dictionary<string, string>();
                    }
                    catch { map = new Dictionary<string, string>(); }
                }
                map[key] = value;

                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(map,
                    new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, FilePath, true);
            }
            catch { /* 设置存不下来不应影响主流程 */ }
        }
    }
}
