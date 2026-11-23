using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace NientumCustomCharts
{
    /// <summary>
    /// 游戏 Mod 诊断日志：同时输出到 BepInEx Log 与独立文件 BepInEx\plugins\NientumCustomCharts_diag.log，
    /// 方便玩家定位“卡加载界面”、音频解码异常或谱面解析失败等故障。
    /// </summary>
    internal static class DiagLog
    {
        private static readonly object _lock = new object();
        private static readonly string LogPath = ResolveLogPath();
        private static bool _sessionStarted;

        public static string FilePath => LogPath;

        private static string ResolveLogPath()
        {
            try
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrEmpty(dir))
                    return Path.Combine(dir, "NientumCustomCharts_diag.log");
            }
            catch { }
            return "NientumCustomCharts_diag.log";
        }

        public static void InitSession()
        {
            lock (_lock)
            {
                if (_sessionStarted) return;
                _sessionStarted = true;
                try
                {
                    if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1024 * 1024)
                    {
                        string bak = LogPath + ".old";
                        if (File.Exists(bak)) File.Delete(bak);
                        File.Move(LogPath, bak);
                    }
                    File.AppendAllText(LogPath,
                        $"\r\n=== NientumCustomCharts Session Start: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ===\r\n",
                        Encoding.UTF8);
                }
                catch { }
            }
        }

        public static void Info(string msg)
        {
            Plugin.Log?.LogInfo(msg);
            Write("INFO", msg);
        }

        public static void Warn(string msg)
        {
            Plugin.Log?.LogWarning(msg);
            Write("WARN", msg);
        }

        public static void Error(string msg, Exception ex = null)
        {
            string full = ex == null ? msg : $"{msg}\r\n{ex}";
            Plugin.Log?.LogError(full);
            Write("ERROR", full);
        }

        private static void Write(string level, string msg)
        {
            try
            {
                if (!_sessionStarted) InitSession();
                string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {msg}\r\n";
                lock (_lock)
                {
                    File.AppendAllText(LogPath, line, Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
