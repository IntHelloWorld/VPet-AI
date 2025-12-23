using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Timers;
using System.Windows;
using VPet_Simulator.Windows.Interface;

namespace VPet_Simulator.Plugin.ScreenMonitor
{
    public class ScreenMonitorPlugin : MainPlugin
    {
        private static void DebugLog(string message) => DebugLogger.Log("[Plugin] " + message);

        private System.Timers.Timer _monitorTimer;
        private string _lastWindowTitle = string.Empty;
        private VisionAPIClient _visionClient;
        private volatile bool _isProcessing;
        private string? _monitorDeviceName;

        public override string PluginName => "ScreenMonitor";

        public ScreenMonitorPlugin(IMainWindow mainwin) : base(mainwin)
        {
            _monitorTimer = new System.Timers.Timer(10000);
            _monitorTimer.Elapsed += OnMonitorTimerElapsed;
            _monitorTimer.AutoReset = false;
            _visionClient = new VisionAPIClient();
            // 注意：实际使用时需要从设置中读取 API Key
            // _visionClient.ApiKey = "YOUR_API_KEY"; 
        }

        public override void LoadPlugin()
        {
            ApplySettingsFromMW();
            _monitorTimer.Start();
        }

        public override void Setting()
        {
            try
            {
                // 由设置窗口调用 ApplySettingsFromMW() 让新频率立即生效
                var win = new WinSettings(MW, this);
                if (System.Windows.Application.Current != null && System.Windows.Application.Current.MainWindow != null)
                    win.Owner = System.Windows.Application.Current.MainWindow;
                win.ShowDialog();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.ToString(), "Screen Monitor 设置");
            }
        }

        public void ApplySettingsFromMW()
        {
            // 使用 Setting.lps 存储：MW.Set["screenmonitor"].interval_ms
            int intervalMs = MW.Set["screenmonitor"].GetInt("interval_ms", 10_000);
            intervalMs = Math.Clamp(intervalMs, 1_000, 3_600_000);
            _monitorTimer.Interval = intervalMs;

            // 可选：用户指定要截取的显示器（DeviceName，例如 \\.\DISPLAY1）
            var dev = MW.Set["screenmonitor"].GetString("monitor_device", null);
            _monitorDeviceName = string.IsNullOrWhiteSpace(dev) ? null : dev;

            DebugLog($"ApplySettingsFromMW: intervalMs={_monitorTimer.Interval} monitor_device={(string.IsNullOrWhiteSpace(_monitorDeviceName) ? "<null>" : _monitorDeviceName)}");
        }

        private async void OnMonitorTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            if (_isProcessing)
            {
                _monitorTimer.Start();
                return;
            }

            _isProcessing = true;
            var activeWindowTitle = ActiveWindowHelper.GetActiveWindowTitle();
            if (activeWindowTitle != _lastWindowTitle && !string.IsNullOrEmpty(activeWindowTitle) && activeWindowTitle != "Unknown")
            {
                _lastWindowTitle = activeWindowTitle;
                DebugLog($"OnMonitorTimerElapsed: window changed -> '{activeWindowTitle}', monitor_device={(string.IsNullOrWhiteSpace(_monitorDeviceName) ? "<null>" : _monitorDeviceName)}");
                
                try
                {
                    byte[]? imageBytes = await CaptureHelper.CaptureActiveWindowAsync(_monitorDeviceName);
                    if (imageBytes == null || imageBytes.Length == 0)
                    {
                        DebugLogger.Log("Capture returned null/empty bytes; skipping this tick.");
                        return;
                    }
                    if (imageBytes != null)
                    {
                        DebugLog($"OnMonitorTimerElapsed: capture bytes={imageBytes.Length}");
                        string base64Image = Convert.ToBase64String(imageBytes);
                        
                        // 调用 AI 进行分析
                        var comment = await _visionClient.AnalyzeImageAsync(base64Image, activeWindowTitle);
                        
                        // 让桌宠说话
                        MW.Main.Dispatcher.Invoke(() =>
                        {
                            MW.Main.Say(comment);
                        });
                    }
                }
                catch (Exception ex)
                {
                    DebugLog("OnMonitorTimerElapsed: exception " + ex.GetType().Name + ": " + ex.Message);
                    Console.WriteLine($"Screen Monitor Error: {ex.Message}");
                }
            }

            _isProcessing = false;
            _monitorTimer.Start();
        }

        public override void EndGame()
        {
            _monitorTimer.Stop();
            _monitorTimer.Dispose();
        }
    }

    public static class ActiveWindowHelper
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        public static string GetActiveWindowTitle()
        {
            const int nChars = 256;
            StringBuilder buff = new StringBuilder(nChars);
            IntPtr handle = GetForegroundWindow();

            if (GetWindowText(handle, buff, nChars) > 0)
            {
                return buff.ToString();
            }
            return "Unknown";
        }
    }
}
