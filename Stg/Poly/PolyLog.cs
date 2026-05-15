using System;
using System.IO;

namespace QjySDK.Stg
{
    /// <summary>
    /// Polymarket 下单 / 结算（redeem）专用日志。
    /// 同步输出到控制台与按日切分的 txt 文件：&lt;BaseDir&gt;/logs/poly/poly_yyyyMMdd.txt
    /// 线程安全，使用 lock 串行化文件写入。
    /// </summary>
    internal static class PolyLog
    {
        private static readonly object _fileLock = new();
        private static string? _logDir;

        private static string LogDir
        {
            get
            {
                if (_logDir != null) return _logDir;
                var dir = Path.Combine(AppContext.BaseDirectory, "logs", "poly");
                try { Directory.CreateDirectory(dir); } catch { }
                _logDir = dir;
                return _logDir;
            }
        }

        public static void Order(string message) => Write("PolyOrder", message);
        public static void Redeem(string message) => Write("PolyRedeem", message);
        public static void Event(string message) => Write("PolyEvent", message);

        public static void Write(string tag, string message)
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{tag}] {message}";
            try { Console.WriteLine(line); } catch { }
            try
            {
                var path = Path.Combine(LogDir, $"poly_{DateTime.Now:yyyyMMdd}.txt");
                lock (_fileLock)
                {
                    File.AppendAllText(path, line + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
