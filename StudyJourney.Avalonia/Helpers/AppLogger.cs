using System;
using System.IO;

namespace StudyJourney.Avalonia.Helpers
{
    /// <summary>
    /// 轻量日志工具：Debug 输出 + 可选文件日志。
    /// 替代散落各处的空 catch（静默吞异常），便于排障。
    /// </summary>
    public static class AppLogger
    {
        private static readonly object _lock = new();
        private static string? _logPath;
        private const long MaxLogBytes = 1 * 1024 * 1024;   // 超过 1MB 轮转，避免无限增长
        private const long RotateCheckEveryBytes = 64 * 1024;   // #14：写入期每累计 ~64KB 检查一次轮转

        // #14 修复：原轮转只在启动时检查一次，写满 1MB 后直到重启都不再轮转（日志无限增长）
        private static long _bytesSinceCheck;

        /// <summary>启用文件日志（写日志到 exe 目录 logs/app.log）</summary>
        public static void EnableFileLogging()
        {
            try
            {
                var dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                Directory.CreateDirectory(dir);
                _logPath = System.IO.Path.Combine(dir, "app.log");
                RotateIfNeeded();
            }
            catch { /* 无法创建日志目录时仅 Debug 输出 */ }
        }

        /// <summary>日志文件超限时重命名为 app.log.1（覆盖旧轮转）</summary>
        private static void RotateIfNeeded()
        {
            if (_logPath == null) return;
            try
            {
                var fi = new FileInfo(_logPath);
                if (fi.Exists && fi.Length > MaxLogBytes)
                {
                    var bak = _logPath + ".1";
                    if (File.Exists(bak)) File.Delete(bak);
                    File.Move(_logPath, bak);
                }
            }
            catch { }
        }

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);
        public static void Error(string message, Exception? ex = null)
        {
            Write("ERROR", message + (ex != null ? $" | {ex.GetType().Name}: {ex.Message}" : ""));
            if (ex != null) System.Diagnostics.Debug.WriteLine(ex.ToString());
        }

        private static void Write(string level, string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}";
            System.Diagnostics.Debug.WriteLine(line);
            if (_logPath == null) return;
            try
            {
                lock (_lock)
                {
                    File.AppendAllText(_logPath, line + Environment.NewLine);
                    // #14：写入期轮转检查（字符数近似字节数，作为阈值判断足够）
                    _bytesSinceCheck += line.Length + 2;
                    if (_bytesSinceCheck >= RotateCheckEveryBytes)
                    {
                        _bytesSinceCheck = 0;
                        RotateIfNeeded();
                    }
                }
            }
            catch { /* 日志写入失败不影响主流程 */ }
        }
    }
}
