using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using VPet_Simulator.Windows.Interface;
using FormsScreen = System.Windows.Forms.Screen;

namespace VPet_Simulator.Plugin.ScreenMonitor
{
    public partial class WinSettings : Window
    {
        private readonly IMainWindow _mw;
        private readonly ScreenMonitorPlugin _plugin;

        private sealed class MonitorOption
        {
            public required string DeviceName { get; init; }
            public required string Display { get; init; }
            public override string ToString() => Display;
        }

        public WinSettings(IMainWindow mw, ScreenMonitorPlugin plugin)
        {
            InitializeComponent();
            _mw = mw;
            _plugin = plugin;

            int intervalMs = GetIntervalMs();
            TbIntervalSeconds.Text = Math.Max(1, intervalMs / 1000).ToString(CultureInfo.InvariantCulture);

            LoadMonitors();
        }

        private string? GetMonitorDeviceName()
        {
            return _mw.Set["screenmonitor"].GetString("monitor_device", null);
        }

        private void SetMonitorDeviceName(string? deviceName)
        {
            _mw.Set["screenmonitor"].SetString("monitor_device", deviceName ?? string.Empty);
        }

        private void LoadMonitors()
        {
            try
            {
                CbMonitor.Items.Clear();

                var screens = FormsScreen.AllScreens;
                for (int i = 0; i < screens.Length; i++)
                {
                    var s = screens[i];
                    string primary = s.Primary ? " (主屏)" : string.Empty;
                    string bounds = $"{s.Bounds.X},{s.Bounds.Y} {s.Bounds.Width}x{s.Bounds.Height}";
                    CbMonitor.Items.Add(new MonitorOption
                    {
                        DeviceName = s.DeviceName,
                        Display = $"{i + 1}: {s.DeviceName}{primary}  [{bounds}]"
                    });
                }

                string? saved = GetMonitorDeviceName();
                var selected = CbMonitor.Items.OfType<MonitorOption>()
                    .FirstOrDefault(o => string.Equals(o.DeviceName, saved, StringComparison.OrdinalIgnoreCase));

                if (selected == null)
                    selected = CbMonitor.Items.OfType<MonitorOption>().FirstOrDefault(o => FormsScreen.AllScreens.FirstOrDefault(sc => sc.DeviceName == o.DeviceName)?.Primary == true)
                               ?? CbMonitor.Items.OfType<MonitorOption>().FirstOrDefault();

                if (selected != null)
                    CbMonitor.SelectedItem = selected;
            }
            catch
            {
                // If enumeration fails for any reason, keep UI usable.
            }
        }

        private int GetIntervalMs()
        {
            // 存在 Setting.lps（全局设置）里，避免依赖存档结构
            return _mw.Set["screenmonitor"].GetInt("interval_ms", 10_000);
        }

        private void SetIntervalMs(int intervalMs)
        {
            _mw.Set["screenmonitor"].SetInt("interval_ms", intervalMs);
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TbIntervalSeconds.Text?.Trim(), out int seconds))
            {
                System.Windows.MessageBox.Show("请输入有效的整数秒数。", "Screen Monitor 设置");
                return;
            }

            seconds = Math.Clamp(seconds, 1, 3600);
            SetIntervalMs(seconds * 1000);

            if (CbMonitor.SelectedItem is MonitorOption opt)
                SetMonitorDeviceName(opt.DeviceName);

            // 让新频率立即生效
            _plugin.ApplySettingsFromMW();

            Close();
        }

        private async void BtnTestCapture_Click(object sender, RoutedEventArgs e)
        {
            BtnTestCapture.IsEnabled = false;
            try
            {
                // 提示：这里测试的是“整屏/显示器”截屏，不依赖大模型
                string? deviceName = null;
                if (CbMonitor.SelectedItem is MonitorOption opt)
                    deviceName = opt.DeviceName;

                var result = await CaptureHelper.CaptureMonitorWithDiagnosticsAsync(deviceName);
                var imageBytes = result.Bytes;
                if (imageBytes == null || imageBytes.Length == 0)
                {
                    System.Windows.MessageBox.Show(
                        "截屏失败。\n\n诊断信息：\n" + result.Diagnostics,
                        "Screen Monitor 设置");
                    return;
                }

                string fileName = $"vpet_screenmonitor_{DateTime.Now:yyyyMMdd_HHmmss}.jpg";
                string fullPath = Path.Combine(Path.GetTempPath(), fileName);
                File.WriteAllBytes(fullPath, imageBytes);

                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{fullPath}\"",
                        UseShellExecute = true
                    });
                }
                catch
                {
                    // ignore; user still has the path
                }

                System.Windows.MessageBox.Show($"截屏已保存：\n{fullPath}", "Screen Monitor 设置");
        #if DEBUG
                // Debug 模式下：额外告知日志文件路径，方便排障。
                System.Windows.MessageBox.Show($"调试日志：\n{DebugLogger.LogFilePath}", "Screen Monitor 调试日志");
        #endif
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"截屏异常：\n{ex}", "Screen Monitor 设置");
            }
            finally
            {
                BtnTestCapture.IsEnabled = true;
            }
        }
    }
}
