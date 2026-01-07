using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
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
        private const string SettingKeySystemPrompt = "System Prompt";

        // TTS 设置键
        private const string SettingKeyTTSEnabled = "TTS Enabled";
        private const string SettingKeyTTSApiKey = "TTS API Key";
        private const string SettingKeyTTSModel = "TTS Model";
        private const string SettingKeyTTSVoice = "TTS Voice";

        private static void DebugLog(string message) => DebugLogger.Log("[插件] " + message);

        private System.Timers.Timer _monitorTimer;
        private string _lastWindowTitle = string.Empty;
        private VisionAPIClient _visionClient;
        private TTSClient _ttsClient;
        private volatile bool _isProcessing;
        private string? _monitorDeviceName;
        private volatile bool _isPaused;
        private System.Windows.Controls.MenuItem? _toggleMenuItem;

        public override string PluginName => "ScreenMonitor";

        public ScreenMonitorPlugin(IMainWindow mainwin) : base(mainwin)
        {
            _monitorTimer = new System.Timers.Timer(10000);
            _monitorTimer.Elapsed += OnMonitorTimerElapsed;
            _monitorTimer.AutoReset = false;
            _visionClient = new VisionAPIClient();
            _ttsClient = new TTSClient();
        }

        public override void LoadPlugin()
        {
            ApplySettingsFromMW();
            _monitorTimer.Start();
        }

        public override void LoadDIY()
        {
            // 在右键菜单中添加暂停/恢复监控选项（只添加一次）
            if (_toggleMenuItem == null)
            {
                var menuText = _isPaused ? "恢复屏幕监控" : "暂停屏幕监控";
                MW.Main.ToolBar.AddMenuButton(VPet_Simulator.Core.ToolBar.MenuType.Setting,
                    menuText,
                    ToggleMonitoring);

                // 查找刚添加的菜单项并保存引用
                foreach (var item in MW.Main.ToolBar.MenuSetting.Items)
                {
                    if (item is System.Windows.Controls.MenuItem menuItem &&
                        (menuItem.Header.ToString() == "暂停屏幕监控" || menuItem.Header.ToString() == "恢复屏幕监控"))
                    {
                        _toggleMenuItem = menuItem;
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// 切换监控状态（暂停/恢复）
        /// </summary>
        private void ToggleMonitoring()
        {
            _isPaused = !_isPaused;

            MW.Main.ToolBar.Visibility = System.Windows.Visibility.Collapsed;

            if (_isPaused)
            {
                DebugLog("用户手动暂停屏幕监控");
                MW.Main.Say("屏幕监控已暂停，我不会再偷看你的屏幕啦~");
            }
            else
            {
                DebugLog("用户手动恢复屏幕监控");
                MW.Main.Say("屏幕监控已恢复，让我看看你在做什么~");
            }

            // 更新菜单项文本
            if (_toggleMenuItem != null)
            {
                _toggleMenuItem.Header = _isPaused ? "恢复屏幕监控" : "暂停屏幕监控";
            }
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

            // 读取系统提示词
            string defaultPrompt = "你是一个可爱的桌宠,正在观察用户的屏幕。请根据用户当前的活动窗口和屏幕截图,给出一段简短、有趣且符合桌宠身份的吐槽或鼓励。使用中文回复,字数控制在30字以内。";
            string systemPrompt = MW.Set["screenmonitor"].GetString(SettingKeySystemPrompt, defaultPrompt) ?? defaultPrompt;
            systemPrompt = systemPrompt.Trim();
            if (!string.IsNullOrWhiteSpace(systemPrompt))
            {
                _visionClient.SystemPrompt = systemPrompt;
            }

            // 可选：用户指定要截取的显示器（DeviceName，例如 \\.\DISPLAY1）
            var dev = MW.Set["screenmonitor"].GetString("monitor_device", null);
            _monitorDeviceName = string.IsNullOrWhiteSpace(dev) ? null : dev;

            // 读取暂停状态（GetBool 找不到时默认返回 false）
            _isPaused = MW.Set["screenmonitor"].GetBool("is_paused");

            // TTS 设置
            _ttsClient.Enabled = MW.Set["screenmonitor"].GetBool(SettingKeyTTSEnabled);
            string ttsApiKey = MW.Set["screenmonitor"].GetString(SettingKeyTTSApiKey, string.Empty) ?? string.Empty;
            _ttsClient.ApiKey = ttsApiKey.Trim();

            string ttsModel = MW.Set["screenmonitor"].GetString(SettingKeyTTSModel, "qwen3-tts-flash") ?? "qwen3-tts-flash";
            _ttsClient.ModelName = ttsModel.Trim();

            string ttsVoice = MW.Set["screenmonitor"].GetString(SettingKeyTTSVoice, "Cherry") ?? "Cherry";
            _ttsClient.Voice = ttsVoice.Trim();

            DebugLog($"应用设置：间隔毫秒={_monitorTimer.Interval} 显示器={(string.IsNullOrWhiteSpace(_monitorDeviceName) ? "<未设置>" : _monitorDeviceName)} 密钥={(string.IsNullOrWhiteSpace(_visionClient.ApiKey) ? "<空>" : "<已设置>")} 接口={_visionClient.ApiEndpoint} 模型={_visionClient.ModelName} 提示词长度={_visionClient.SystemPrompt.Length} 暂停状态={_isPaused} TTS启用={_ttsClient.Enabled} TTS音色={_ttsClient.Voice}");
        }

        private async void OnMonitorTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            if (_isProcessing || _isPaused)
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

                        // 订阅文本生成完成事件，用于 TTS
                        sayInfo.Event_Finish += async (fullText) =>
                        {
                            await PlayTTSAsync(fullText);
                        };

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

        /// <summary>
        /// 调用 TTS 合成并播放语音
        /// </summary>
        private async Task PlayTTSAsync(string text)
        {
            if (!_ttsClient.Enabled || string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            try
            {
                DebugLog($"开始 TTS 合成：{text.Substring(0, Math.Min(50, text.Length))}...");

                var audioPath = await _ttsClient.SynthesizeToFileAsync(text);
                if (!string.IsNullOrEmpty(audioPath))
                {
                    DebugLog($"TTS 合成成功，开始播放：{audioPath}");

                    // 在 UI 线程上播放音频
                    MW.Main.Dispatcher.Invoke(() =>
                    {
                        try
                        {
                            MW.Main.PlayVoice(new Uri(audioPath));
                        }
                        catch (Exception ex)
                        {
                            DebugLog($"播放音频失败：{ex.Message}");
                        }
                    });

                    // 延迟删除临时文件（等待播放完成）
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(30000); // 等待 30 秒后删除
                        TTSClient.CleanupTempFile(audioPath);
                    });
                }
                else
                {
                    DebugLog("TTS 合成失败，未返回音频文件");
                }
            }
            catch (Exception ex)
            {
                DebugLog($"TTS 播放异常：{ex.GetType().Name}: {ex.Message}");
            }
        }

        public override void EndGame()
        {
            _monitorTimer.Stop();
            _monitorTimer.Dispose();
        }

        public override void Save()
        {
            // 保存暂停状态
            MW.Set["screenmonitor"].SetBool("is_paused", _isPaused);
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
