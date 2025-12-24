using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Timers;
using System.Windows;
using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;

namespace VPet_Simulator.Plugin.ScreenMonitor
{
    public class ScreenMonitorPlugin : MainPlugin
    {
        private const string SettingKeyApiKey = "API Key";
        private const string SettingKeyBaseUrl = "Base Url";
        private const string SettingKeyModelName = "Model Name";

        private static void DebugLog(string message) => DebugLogger.Log("[插件] " + message);

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
                System.Windows.MessageBox.Show(
                    "屏幕监控的设置已整合到主设置界面。\n\n请打开：设置 → 图形 → 屏幕监控\n\n（如需排障：可在主设置里点击“打开调试日志”）",
                    "屏幕监控设置");
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

            // API settings
            string apiKey = MW.Set["screenmonitor"].GetString(SettingKeyApiKey, string.Empty) ?? string.Empty;
            _visionClient.ApiKey = apiKey.Trim();

            string baseUrl = MW.Set["screenmonitor"].GetString(SettingKeyBaseUrl, string.Empty) ?? string.Empty;
            baseUrl = baseUrl.Trim();
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                // 保持用户设置的 ApiEndpoint 原样。
                // VisionAPIClient 会基于 ApiEndpoint + ModelName 自动规范化/拼接最终 URL（例如 Gemini generateContent）。
                _visionClient.ApiEndpoint = baseUrl;
            }

            string modelName = MW.Set["screenmonitor"].GetString(SettingKeyModelName, string.Empty) ?? string.Empty;
            modelName = modelName.Trim();
            if (!string.IsNullOrWhiteSpace(modelName))
            {
                _visionClient.ModelName = modelName;
            }

            // 可选：用户指定要截取的显示器（DeviceName，例如 \\.\DISPLAY1）
            var dev = MW.Set["screenmonitor"].GetString("monitor_device", null);
            _monitorDeviceName = string.IsNullOrWhiteSpace(dev) ? null : dev;

            DebugLog($"应用设置：间隔毫秒={_monitorTimer.Interval} 显示器={(string.IsNullOrWhiteSpace(_monitorDeviceName) ? "<未设置>" : _monitorDeviceName)} 密钥={(string.IsNullOrWhiteSpace(_visionClient.ApiKey) ? "<空>" : "<已设置>")} 接口={_visionClient.ApiEndpoint} 模型={_visionClient.ModelName}");
        }

        private static string NormalizeToChatCompletionsEndpoint(string baseUrlOrEndpoint)
        {
            string s = baseUrlOrEndpoint.Trim();
            if (string.IsNullOrWhiteSpace(s))
                return "https://api.openai.com/v1/chat/completions";

            // 允许用户直接粘贴完整的 endpoint。
            if (s.Contains("/chat/completions", StringComparison.OrdinalIgnoreCase))
                return s;

            // BaseUrl 可能是：
            // - https://api.openai.com
            // - https://api.openai.com/v1
            // - https://example.com/openai（OpenAI 兼容代理）
            s = s.TrimEnd('/');
            if (s.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                return s + "/chat/completions";

            return s + "/v1/chat/completions";
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
                DebugLog($"定时检测：窗口切换 -> '{activeWindowTitle}' 显示器={(string.IsNullOrWhiteSpace(_monitorDeviceName) ? "<未设置>" : _monitorDeviceName)}");
                
                try
                {
                    byte[]? imageBytes = await CaptureHelper.CaptureActiveWindowAsync(_monitorDeviceName);
                    if (imageBytes == null || imageBytes.Length == 0)
                    {
                        DebugLogger.Log("截屏结果为空（无图像数据），跳过本次检测。");
                        return;
                    }
                    if (imageBytes != null)
                    {
                        DebugLog($"定时检测：截屏字节数={imageBytes.Length}");
                        string base64Image = Convert.ToBase64String(imageBytes);
                        
                        // 调用 AI 进行分析 (流式)
                        var sayInfo = new SayInfoWithStream();
                        MW.Main.Dispatcher.Invoke(() =>
                        {
                            MW.Main.SayRnd(sayInfo);
                        });

                        await _visionClient.AnalyzeImageStreamAsync(base64Image, activeWindowTitle, sayInfo);
                    }
                }
                catch (Exception ex)
                {
                    DebugLog("定时检测：异常 " + ex.GetType().Name + ": " + ex.Message);
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
