using System;
using System.IO;

namespace MaterialOutboundApi.Infrastructure
{
    internal static class ApiPerformanceLog
    {
        private static readonly object Sync = new object();

        public static void Write(
            string requestId,
            string operation,
            long elapsedMilliseconds,
            string details)
        {
            try
            {
                string directory = Path.Combine(
                    AppRuntime.DataDirectory,
                    "Logs");
                Directory.CreateDirectory(directory);
                string safeDetails = (details ?? string.Empty)
                    .Replace("\r", " ")
                    .Replace("\n", " ");
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                              " requestId=" + requestId +
                              " operation=" + operation +
                              " elapsedMs=" + elapsedMilliseconds +
                              " " + safeDetails + Environment.NewLine;
                lock (Sync)
                {
                    File.AppendAllText(
                        Path.Combine(
                            directory,
                            "performance-" +
                            DateTime.Now.ToString("yyyyMMdd") + ".log"),
                        line);
                }
            }
            catch
            {
                // 性能日志失败不能影响 U8 业务。
            }
        }
    }
}
