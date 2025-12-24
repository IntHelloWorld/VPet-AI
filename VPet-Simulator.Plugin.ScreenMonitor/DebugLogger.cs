using System;
using System.Diagnostics;
using System.IO;

namespace VPet_Simulator.Plugin.ScreenMonitor
{
    /// <summary>
    /// 调试日志器（仅 DEBUG 生效）。
    /// 
    /// 说明：
    /// - 本项目是 WPF 应用/插件，通常没有控制台窗口，Console.WriteLine 在很多情况下不可见。
    /// - 因此 Debug 日志统一落盘到 %TEMP%，保证用户能拿到日志用于排障。
    /// </summary>
    internal static class DebugLogger
    {
        private static readonly object _lock = new();

        /// <summary>
        /// 调试日志路径：%TEMP%\vpet_screenmonitor_debug.log
        /// </summary>
        internal static string LogFilePath => Path.Combine(Path.GetTempPath(), "vpet_screenmonitor_debug.log");

        [Conditional("DEBUG")]
        internal static void Log(string message)
        {
            try
            {
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
                Debug.WriteLine("[屏幕监控] " + line);

                lock (_lock)
                {
                    File.AppendAllText(LogFilePath, line + Environment.NewLine);
                }
            }
            catch
            {
                // 日志写入不应影响主流程
            }
        }

        [Conditional("DEBUG")]
        internal static void LogException(string context, Exception ex)
        {
            Log(context + ": " + ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex);
        }
    }
}
