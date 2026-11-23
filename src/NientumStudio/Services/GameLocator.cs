using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace NientumStudio.Services
{
    /// <summary>定位游戏安装目录（优先设置里的路径，其次 Steam 注册表与常见库）。</summary>
    public static class GameLocator
    {
        public static string GameDir { get; private set; } = "";
        public static bool HasGame => IsValid(GameDir);
        public static string CustomChartsDir => HasGame ? Path.Combine(GameDir, "CustomCharts") : "";

        static GameLocator()
        {
            Refresh();
        }

        public static string Refresh()
        {
            GameDir = Resolve(EditorSettings.Current.GamePath);
            return GameDir;
        }

        public static bool IsValid(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return false;
            try { return File.Exists(Path.Combine(dir, "Nientum.exe")); }
            catch { return false; }
        }

        public static string Resolve(string preferred)
        {
            if (IsValid(preferred)) return preferred.TrimEnd(Path.DirectorySeparatorChar);
            foreach (string candidate in Candidates())
            {
                if (IsValid(candidate)) return candidate.TrimEnd(Path.DirectorySeparatorChar);
            }
            return "";
        }

        public static void SetGameDir(string dir)
        {
            if (!IsValid(dir)) return;
            GameDir = dir.TrimEnd(Path.DirectorySeparatorChar);
            EditorSettings.Current.GamePath = GameDir;
            EditorSettings.Current.Save();
        }

        private static IEnumerable<string> Candidates()
        {
            var list = new List<string>();
            try
            {
                string steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
                if (string.IsNullOrEmpty(steam))
                    steam = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
                if (!string.IsNullOrEmpty(steam))
                {
                    steam = steam.Replace('/', '\\');
                    list.Add(Path.Combine(steam, "steamapps", "common", "Liora"));
                    string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                    if (File.Exists(vdf))
                    {
                        foreach (string line in File.ReadLines(vdf))
                        {
                            Match m = Regex.Match(line, "\"path\"\\s+\"(.+?)\"");
                            if (m.Success)
                                list.Add(Path.Combine(m.Groups[1].Value.Replace("\\\\", "\\"), "steamapps", "common", "Liora"));
                        }
                    }
                }
            }
            catch { }
            // 兜底：Steam 默认安装路径（其余库通过注册表与 libraryfolders.vdf 发现）
            list.Add(@"C:\Program Files (x86)\Steam\steamapps\common\Liora");
            return list;
        }
    }
}
