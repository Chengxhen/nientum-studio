using System;
using System.IO;
using System.Text;

namespace NientumStudio.Services
{
    /// <summary>
    /// 编辑器诊断日志，落盘至程序运行目录的 studio_diag.log，方便排查启动、音频、Bank 与谱面加载问题。
    /// </summary>
    public static class AppLog
    {
        private static readonly object _lock = new();
        private static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "studio_diag.log");
        private static bool _initialized;

        public static string FilePath => LogPath;

        public static void InitSession()
        {
            lock (_lock)
            {
                if (_initialized) return;
                _initialized = true;
                try
                {
                    if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 2 * 1024 * 1024)
                    {
                        string bak = LogPath + ".old";
                        if (File.Exists(bak)) File.Delete(bak);
                        File.Move(LogPath, bak);
                    }
                    File.AppendAllText(LogPath,
                        $"\r\n=== NientumStudio Session Start: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | BaseDir={AppDomain.CurrentDomain.BaseDirectory} ===\r\n",
                        Encoding.UTF8);
                }
                catch { }
            }
        }

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);
        public static void Error(string message, Exception ex = null)
        {
            string full = ex == null ? message : $"{message}\r\n{ex}";
            Write("ERROR", full);
        }

        private static void Write(string level, string message)
        {
            try
            {
                if (!_initialized) InitSession();
                string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}\r\n";
                lock (_lock)
                {
                    File.AppendAllText(LogPath, line, Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
