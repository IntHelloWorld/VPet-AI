using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinRT;
using static Vortice.Direct3D11.D3D11;
using FormsScreen = System.Windows.Forms.Screen;

namespace VPet_Simulator.Plugin.ScreenMonitor
{
    /// <summary>
    /// 截屏工具类。
    /// 
    /// 设计目标：
    /// - 生产路径优先使用 Windows Graphics Capture（WGC）拿到屏幕图像，性能与兼容性更好。
    /// - 调试/测试时提供更详细的诊断信息，并在必要时回退到 GDI 截屏，保证“可用性”。
    /// 
    /// 注意：
    /// - WGC 相关调用依赖 WinRT + D3D11 互操作，COM 接口声明必须与系统一致，否则会出现“hr=0 但指针为 0”的怪现象。
    /// - 本项目使用 Windows Forms 的 Screen 枚举显示器（仅用于获取显示器 Bounds/DeviceName）。
    /// </summary>
    public static class CaptureHelper
    {
        private const int DefaultTimeoutMs = 1500;
        private const int JpegTargetWidth = 1024;

        private static void DebugLog(string message) => DebugLogger.Log("[Capture] " + message);

        [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [ComImport]
        interface IGraphicsCaptureItemInterop
        {
            int CreateForWindow(IntPtr window, [In] ref Guid iid, out IntPtr result);
            int CreateForMonitor(IntPtr monitor, [In] ref Guid iid, out IntPtr result);
        }

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        private const uint MONITOR_DEFAULTTONEAREST = 2;

        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;

        [DllImport("combase.dll", CharSet = CharSet.Unicode)]
        private static extern int RoGetActivationFactory(IntPtr hstring, ref Guid iid, out IntPtr factory);

        [DllImport("combase.dll", CharSet = CharSet.Unicode)]
        private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

        [DllImport("combase.dll")]
        private static extern int WindowsDeleteString(IntPtr hstring);

        [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

        private static ID3D11Device? _d3dDevice;
        private static IDirect3DDevice? _winrtDevice;

        private static void TryDisableWgcBorder(GraphicsCaptureSession session, StringBuilder? diag = null)
        {
            // On newer Windows builds, WGC exposes GraphicsCaptureSession.IsBorderRequired.
            // Setting it to false can reduce or remove the yellow capture border.
            // Use reflection to stay compatible with older SDK projections where the property may not exist.
            try
            {
                var prop = session.GetType().GetProperty("IsBorderRequired");
                if (prop != null && prop.CanWrite && prop.PropertyType == typeof(bool))
                {
                    prop.SetValue(session, false);
                    diag?.AppendLine("IsBorderRequired: set to false");
                    return;
                }

                diag?.AppendLine("IsBorderRequired: not available");
            }
            catch (Exception ex)
            {
                diag?.AppendLine($"IsBorderRequired: set failed ({ex.GetType().Name}: {ex.Message})");
            }
        }

        /// <summary>
        /// 捕获“当前前台窗口所在显示器”的整屏截图（WGC）。
        /// </summary>
        /// <remarks>
        /// 这是最稳妥的默认策略：与其捕获窗口本身，不如捕获窗口所在的显示器。
        /// 一些窗口（比如 UWP/特殊渲染/受保护内容）直接捕窗口可能拿不到帧。
        /// </remarks>
        public static async Task<byte[]?> CaptureActiveWindowAsync()
        {
            if (!GraphicsCaptureSession.IsSupported())
            throw new NotSupportedException("当前系统不支持 Windows Graphics Capture（WGC）。");

            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;

            DebugLog($"CaptureActiveWindowAsync: 前台窗口句柄 hwnd=0x{hwnd.ToInt64():X}");

            // 更稳妥：捕获“前台窗口所在的显示器”（等价于整屏/整显示器），避免某些窗口无法被捕获导致拿不到帧。
            var hmon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (hmon == IntPtr.Zero) return null;

            DebugLog($"CaptureActiveWindowAsync: 前台窗口所在显示器 hmon=0x{hmon.ToInt64():X}");

            var item = CreateItemForMonitor(hmon);
            if (item == null) return null;

            DebugLog("CaptureActiveWindowAsync: WGC CreateItemForMonitor 成功。");

            return await CaptureItemAsync(item, timeoutMs: DefaultTimeoutMs);
        }

        /// <summary>
        /// 捕获截图（优先使用用户指定显示器；如果未指定或失败，则回退到前台窗口所在显示器）。
        /// </summary>
        public static async Task<byte[]?> CaptureActiveWindowAsync(string? monitorDeviceName, int timeoutMs = 1500)
        {
            DebugLog($"CaptureActiveWindowAsync(带选择): monitorDeviceName={(string.IsNullOrWhiteSpace(monitorDeviceName) ? "<未设置>" : monitorDeviceName)} timeoutMs={timeoutMs}");
            if (!string.IsNullOrWhiteSpace(monitorDeviceName))
            {
                var bytes = await CaptureSelectedMonitorAsync(monitorDeviceName, timeoutMs);
                if (bytes != null && bytes.Length > 0)
                    return bytes;
            }

            // Fallback to the original behavior: capture the monitor nearest to the foreground window.
            return await CaptureActiveWindowAsync();
        }

        /// <summary>
        /// 捕获指定显示器整屏截图（WGC）。
        /// </summary>
        /// <param name="monitorDeviceName">Windows 设备名，例如：\\.\DISPLAY1</param>
        /// <param name="timeoutMs">等待第一帧的超时时间</param>
        public static async Task<byte[]?> CaptureSelectedMonitorAsync(string monitorDeviceName, int timeoutMs = 1500)
        {
            if (string.IsNullOrWhiteSpace(monitorDeviceName))
                return null;

            DebugLog($"CaptureSelectedMonitorAsync: 目标显示器={monitorDeviceName} timeoutMs={timeoutMs}");

            if (!GraphicsCaptureSession.IsSupported())
                throw new NotSupportedException("当前系统不支持 Windows Graphics Capture（WGC）。");

            var screen = FormsScreen.AllScreens.FirstOrDefault(s => string.Equals(s.DeviceName, monitorDeviceName, StringComparison.OrdinalIgnoreCase));
            if (screen == null)
            {
                DebugLog("CaptureSelectedMonitorAsync: 未找到对应显示器（Screen.AllScreens 中不存在）。");
                return null;
            }

            var hmon = GetHMonitorForScreen(screen);
            if (hmon == IntPtr.Zero)
            {
                DebugLog("CaptureSelectedMonitorAsync: MonitorFromPoint 返回 NULL（无法解析 HMONITOR）。");
                return null;
            }

            DebugLog($"CaptureSelectedMonitorAsync: bounds={screen.Bounds.Left},{screen.Bounds.Top} {screen.Bounds.Width}x{screen.Bounds.Height} hmon=0x{hmon.ToInt64():X}");

            var item = CreateItemForMonitor(hmon);
            if (item == null)
            {
                DebugLog("CaptureSelectedMonitorAsync: WGC CreateItemForMonitor 返回 null（上层可选择回退）。");
                return null;
            }

            DebugLog("CaptureSelectedMonitorAsync: 使用 WGC CreateItemForMonitor 成功。");

            return await CaptureItemAsync(item, timeoutMs);
        }

        public static async Task<byte[]?> CapturePrimaryMonitorAsync(int timeoutMs = 1500)
        {
            return await CapturePrimaryMonitorAsync(null, timeoutMs);
        }

        public static async Task<byte[]?> CapturePrimaryMonitorAsync(string? monitorDeviceName, int timeoutMs = 1500)
        {
            if (!string.IsNullOrWhiteSpace(monitorDeviceName))
            {
                var bytes = await CaptureSelectedMonitorAsync(monitorDeviceName, timeoutMs);
                if (bytes != null && bytes.Length > 0)
                    return bytes;
            }

            // 使用当前前台窗口所在的屏幕作为“主屏”近似（对多屏场景更符合用户当前关注的屏幕）
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            var hmon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (hmon == IntPtr.Zero) return null;

            var item = CreateItemForMonitor(hmon);
            if (item == null) return null;
            return await CaptureItemAsync(item, timeoutMs);
        }

        public static async Task<(byte[]? Bytes, string Diagnostics)> CapturePrimaryMonitorWithDiagnosticsAsync(int timeoutMs = 1500)
        {
            return await CaptureMonitorWithDiagnosticsAsync(null, timeoutMs);
        }

        /// <summary>
        /// 捕获截图并返回诊断信息（用于“测试截屏”按钮与排障）。
        /// </summary>
        /// <remarks>
        /// 逻辑：
        /// 1) 优先 WGC 捕获显示器。
        /// 2) 若 WGC 显示器失败，再尝试 WGC 捕获窗口（某些环境下 monitor capture 会失败）。
        /// 3) 仍失败则回退 GDI（为了保证测试可用）。
        /// </remarks>
        public static async Task<(byte[]? Bytes, string Diagnostics)> CaptureMonitorWithDiagnosticsAsync(string? monitorDeviceName, int timeoutMs = 1500)
        {
            DebugLog($"CaptureMonitorWithDiagnosticsAsync: monitorDeviceName={(string.IsNullOrWhiteSpace(monitorDeviceName) ? "<未设置>" : monitorDeviceName)} timeoutMs={timeoutMs}");
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return (null, "GetForegroundWindow 返回 NULL。");

            DebugLog($"CaptureMonitorWithDiagnosticsAsync: hwnd=0x{hwnd.ToInt64():X}");

            var diag = new StringBuilder();

            Screen? selectedScreen = null;
            if (!string.IsNullOrWhiteSpace(monitorDeviceName))
            {
                selectedScreen = FormsScreen.AllScreens.FirstOrDefault(s => string.Equals(s.DeviceName, monitorDeviceName, StringComparison.OrdinalIgnoreCase));
                diag.AppendLine($"RequestedMonitorDevice: {monitorDeviceName}");
                diag.AppendLine($"ResolvedScreen: {(selectedScreen == null ? "NULL" : selectedScreen.DeviceName)}");

                DebugLog($"CaptureMonitorWithDiagnosticsAsync: selectedScreen={(selectedScreen == null ? "NULL" : selectedScreen.DeviceName)}");
            }

            IntPtr hmon;
            if (selectedScreen != null)
            {
                hmon = GetHMonitorForScreen(selectedScreen);
                diag.AppendLine($"MonitorFromPoint(HMONITOR): 0x{hmon.ToInt64():X}");

                DebugLog($"CaptureMonitorWithDiagnosticsAsync: MonitorFromPoint hmon=0x{hmon.ToInt64():X} bounds={selectedScreen.Bounds.Left},{selectedScreen.Bounds.Top} {selectedScreen.Bounds.Width}x{selectedScreen.Bounds.Height}");
            }
            else
            {
                hmon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                diag.AppendLine($"MonitorFromWindow(HMONITOR): 0x{hmon.ToInt64():X}");

                DebugLog($"CaptureMonitorWithDiagnosticsAsync: MonitorFromWindow hmon=0x{hmon.ToInt64():X}");
            }

            if (hmon == IntPtr.Zero)
                return (null, diag + "\n显示器选择返回 NULL HMONITOR。");

            // Try WGC first
            try
            {
                var (item, itemDiag) = TryCreateItemForMonitorWithDiagnostics(hmon);
                if (item == null)
                {
                    DebugLog("CaptureMonitorWithDiagnosticsAsync: WGC(显示器) 返回 NULL，尝试 WGC(窗口)；仍失败则回退 GDI。");
                    // Fallback: try window-based item (some environments fail monitor capture)
                    var (winItem, winDiag) = TryCreateItemForWindowWithDiagnostics(hwnd);
                    if (winItem != null)
                    {
                        DebugLog("CaptureMonitorWithDiagnosticsAsync: 使用 WGC 窗口捕获。");
                        var (bytes, capDiag) = await CaptureItemWithDiagnosticsAsync(winItem, timeoutMs);
                        return (bytes, diag + "\n" + itemDiag + "\n" + winDiag + "\n" + capDiag);
                    }

                    // Final fallback: GDI capture for testability
                    byte[] gdiBytes;
                    if (selectedScreen != null)
                    {
                        DebugLog("CaptureMonitorWithDiagnosticsAsync: 回退到 GDI（仅截取所选显示器）。");
                        gdiBytes = CaptureScreenBoundsGdiJpeg(selectedScreen.Bounds, JpegTargetWidth);
                        return (gdiBytes, diag + "\n" + itemDiag + "\n" + winDiag + "\n" + "回退：已使用 GDI 截取所选显示器。" );
                    }
                    else
                    {
                        DebugLog("CaptureMonitorWithDiagnosticsAsync: 回退到 GDI（截取虚拟桌面）。");
                        gdiBytes = CaptureVirtualScreenGdiJpeg(JpegTargetWidth);
                        return (gdiBytes, diag + "\n" + itemDiag + "\n" + winDiag + "\n" + "回退：已使用 GDI 截取虚拟桌面。" );
                    }
                }

                DebugLog("CaptureMonitorWithDiagnosticsAsync: 使用 WGC 显示器捕获。");
                var (bytes2, capDiag2) = await CaptureItemWithDiagnosticsAsync(item, timeoutMs);
                return (bytes2, diag + "\n" + itemDiag + "\n" + capDiag2);
            }
            catch (Exception ex)
            {
                DebugLog("CaptureMonitorWithDiagnosticsAsync: WGC 异常 -> 回退 GDI。" + ex.GetType().Name + ": " + ex.Message);
                try
                {
                    if (selectedScreen != null)
                    {
                        DebugLog("CaptureMonitorWithDiagnosticsAsync: Falling back to GDI selected-screen capture (exception path).");
                        var gdiBytes = CaptureScreenBoundsGdiJpeg(selectedScreen.Bounds, JpegTargetWidth);
                        return (gdiBytes, diag + "\nWGC 异常：" + ex + "\n回退：已使用 GDI 截取所选显示器。");
                    }
                    else
                    {
                        DebugLog("CaptureMonitorWithDiagnosticsAsync: Falling back to GDI virtual-screen capture (exception path).");
                        var gdiBytes = CaptureVirtualScreenGdiJpeg(JpegTargetWidth);
                        return (gdiBytes, diag + "\nWGC 异常：" + ex + "\n回退：已使用 GDI 截取虚拟桌面。");
                    }
                }
                catch (Exception ex2)
                {
                    DebugLog("CaptureMonitorWithDiagnosticsAsync: GDI fallback exception. " + ex2.GetType().Name + ": " + ex2.Message);
                    return (null, diag + "\nWGC 异常：" + ex + "\nGDI 回退异常：" + ex2);
                }
            }
        }

        /// <summary>
        /// 将 Screen（显示器枚举对象）映射到 Win32 HMONITOR。
        /// </summary>
        private static IntPtr GetHMonitorForScreen(FormsScreen screen)
        {
            // 取该显示器 bounds 内的一个点，交给 user32 解析 HMONITOR。
            int x = screen.Bounds.Left + Math.Min(1, Math.Max(0, screen.Bounds.Width - 1));
            int y = screen.Bounds.Top + Math.Min(1, Math.Max(0, screen.Bounds.Height - 1));
            var pt = new POINT { X = x, Y = y };
            return MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        }

        private static GraphicsCaptureItem? CreateItemForWindow(IntPtr hwnd)
        {
            var interop = GetGraphicsCaptureItemInterop();
            Guid iid = GetGraphicsCaptureItemIid();
            int hr = interop.CreateForWindow(hwnd, ref iid, out var temp);
            if (hr != 0)
                Marshal.ThrowExceptionForHR(hr);
            if (temp == IntPtr.Zero)
                throw new InvalidOperationException("CreateForWindow succeeded but returned a NULL pointer.");

            try
            {
                return MarshalInspectable<GraphicsCaptureItem>.FromAbi(temp);
            }
            finally
            {
                if (temp != IntPtr.Zero)
                    Marshal.Release(temp);
            }
        }

        private static GraphicsCaptureItem? CreateItemForMonitor(IntPtr hmon)
        {
            var interop = GetGraphicsCaptureItemInterop();
            Guid iid = GetGraphicsCaptureItemIid();
            int hr = interop.CreateForMonitor(hmon, ref iid, out var temp);
            if (hr != 0)
                Marshal.ThrowExceptionForHR(hr);
            if (temp == IntPtr.Zero)
                return null;

            try
            {
                return MarshalInspectable<GraphicsCaptureItem>.FromAbi(temp);
            }
            finally
            {
                if (temp != IntPtr.Zero)
                    Marshal.Release(temp);
            }
        }

        public static async Task<byte[]?> CaptureItemAsync(GraphicsCaptureItem item, int timeoutMs = 1500)
        {
            var device = GetOrCreateWinRTDevice();
            if (device == null) return null;

            // 部分 Windows 版本会把屏幕捕获受“隐私权限”约束。
            // 如果系统提供了 GraphicsCaptureAccess，则尝试请求 Programmatic 权限。
            try
            {
                var accessType = Type.GetType("Windows.Graphics.Capture.GraphicsCaptureAccess, Microsoft.Windows.SDK.NET");
                if (accessType != null)
                {
                    var kindType = Type.GetType("Windows.Graphics.Capture.GraphicsCaptureAccessKind, Microsoft.Windows.SDK.NET");
                    var request = accessType.GetMethod("RequestAccessAsync", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (kindType != null && request != null)
                    {
                        // Programmatic = 1（如果 enum 发生变化，这里仍尽量保持兼容）
                        object kind = Enum.ToObject(kindType, 1);
                        object? op = request.Invoke(null, new[] { kind });

                        // 通过 dynamic await IAsyncOperation<GraphicsCaptureAccessStatus>
                        if (op != null)
                        {
                            dynamic dyn = op;
                            var status = await dyn;
                            string? statusText = status?.ToString();
                            if (!string.IsNullOrWhiteSpace(statusText) && statusText.Contains("Denied", StringComparison.OrdinalIgnoreCase))
                                throw new UnauthorizedAccessException($"Graphics capture access denied: {statusText}");
                        }
                    }
                }
            }
            catch
            {
                // 反射失败或 API 不存在：继续执行；部分系统仍可能正常捕获。
            }

            using var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                1,
                item.Size);

            using var session = framePool.CreateCaptureSession(item);
            TryDisableWgcBorder(session);
            var completionSource = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

            framePool.FrameArrived += async (s, a) =>
            {
                using var frame = s.TryGetNextFrame();
                if (frame == null)
                    return;

                try
                {
                    var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);
                    var encodedData = await EncodeBitmapAsync(bitmap);
                    completionSource.TrySetResult(encodedData);
                }
                catch (Exception ex)
                {
                    completionSource.TrySetException(ex);
                }
                finally
                {
                    session.Dispose();
                }
            };

            session.StartCapture();

            var completed = await Task.WhenAny(completionSource.Task, Task.Delay(timeoutMs));
            if (completed != completionSource.Task)
            {
                try { session.Dispose(); } catch { }
                return null;
            }

            return await completionSource.Task;
        }

        private static async Task<(byte[]? Bytes, string Diagnostics)> CaptureItemWithDiagnosticsAsync(GraphicsCaptureItem item, int timeoutMs)
        {
            var diag = new StringBuilder();
            diag.AppendLine($"IsSupported: {GraphicsCaptureSession.IsSupported()}");

            DebugLog($"CaptureItemWithDiagnosticsAsync: start timeoutMs={timeoutMs}");

            try
            {
                diag.AppendLine($"Item.Size: {item.Size.Width}x{item.Size.Height}");
            }
            catch (Exception ex)
            {
                diag.AppendLine($"Item.Size read failed: {ex.GetType().Name}: {ex.Message}");
            }

            string? accessStatus = null;
            try
            {
                var accessType = Type.GetType("Windows.Graphics.Capture.GraphicsCaptureAccess, Microsoft.Windows.SDK.NET");
                if (accessType != null)
                {
                    var kindType = Type.GetType("Windows.Graphics.Capture.GraphicsCaptureAccessKind, Microsoft.Windows.SDK.NET");
                    var request = accessType.GetMethod("RequestAccessAsync", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (kindType != null && request != null)
                    {
                        object kind = Enum.ToObject(kindType, 1);
                        object? op = request.Invoke(null, new[] { kind });
                        if (op != null)
                        {
                            dynamic dyn = op;
                            var status = await dyn;
                            accessStatus = status?.ToString();
                        }
                        else
                        {
                            accessStatus = "RequestAccessAsync 返回了 null。";
                        }
                    }
                    else
                    {
                        accessStatus = "GraphicsCaptureAccess present, but RequestAccessAsync not found.";
                    }
                }
                else
                {
                    accessStatus = "GraphicsCaptureAccess API not found (ok on some builds).";
                }
            }
            catch (Exception ex)
            {
                accessStatus = $"RequestAccessAsync failed: {ex.GetType().Name}: {ex.Message}";
            }
            diag.AppendLine($"AccessStatus: {accessStatus}");

            var device = GetOrCreateWinRTDevice();
            diag.AppendLine($"WinRTDevice: {(device == null ? "NULL" : "OK")}");
            if (device == null)
                return (null, diag.ToString());

            int frameArrivedCount = 0;
            bool gotFrame = false;
            string? firstFrameInfo = null;
            Exception? capturedException = null;

            using var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                1,
                item.Size);

            using var session = framePool.CreateCaptureSession(item);
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sw = Stopwatch.StartNew();

            framePool.FrameArrived += async (s, a) =>
            {
                frameArrivedCount++;
                using var frame = s.TryGetNextFrame();
                if (frame == null)
                    return;

                try
                {
                    gotFrame = true;
                    if (firstFrameInfo == null)
                        firstFrameInfo = $"FirstFrame at {sw.ElapsedMilliseconds}ms; ContentSize={frame.ContentSize.Width}x{frame.ContentSize.Height}";

                    DebugLog($"CaptureItemWithDiagnosticsAsync: FrameArrived #{frameArrivedCount}; {firstFrameInfo}");

                    var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);
                    var encodedData = await EncodeBitmapAsync(bitmap);
                    tcs.TrySetResult(encodedData);
                }
                catch (Exception ex)
                {
                    capturedException = ex;
                    tcs.TrySetException(ex);
                }
                finally
                {
                    try { session.Dispose(); } catch { }
                }
            };

            session.StartCapture();
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            sw.Stop();

            diag.AppendLine($"TimeoutMs: {timeoutMs}");
            diag.AppendLine($"FrameArrivedCount: {frameArrivedCount}");
            diag.AppendLine($"GotFrame: {gotFrame}");
            if (firstFrameInfo != null)
                diag.AppendLine(firstFrameInfo);

            if (completed != tcs.Task)
            {
                try { session.Dispose(); } catch { }
                diag.AppendLine("Result: TIMEOUT (no completed frame)");

                DebugLog($"CaptureItemWithDiagnosticsAsync: TIMEOUT after {timeoutMs}ms; FrameArrivedCount={frameArrivedCount}");
                return (null, diag.ToString());
            }

            try
            {
                var bytes = await tcs.Task;
                diag.AppendLine($"Result: OK (bytes={bytes?.Length ?? 0})");

                DebugLog($"CaptureItemWithDiagnosticsAsync: OK bytes={bytes?.Length ?? 0}");
                return (bytes, diag.ToString());
            }
            catch (Exception ex)
            {
                diag.AppendLine("Result: EXCEPTION");
                diag.AppendLine(ex.ToString());
                if (capturedException != null && !ReferenceEquals(ex, capturedException))
                    diag.AppendLine("CapturedException: " + capturedException);

                DebugLog($"CaptureItemWithDiagnosticsAsync: EXCEPTION {ex.GetType().Name}: {ex.Message}");
                return (null, diag.ToString());
            }
        }

        private static async Task<byte[]> EncodeBitmapAsync(SoftwareBitmap bitmap)
        {
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
            encoder.SetSoftwareBitmap(bitmap);
            
            // 缩放处理：将图像缩小以减少 API 传输开销并适应 AI 模型
            uint targetWidth = JpegTargetWidth;
            uint targetHeight = (uint)(JpegTargetWidth * (bitmap.PixelHeight / (double)bitmap.PixelWidth));
            
            encoder.BitmapTransform.ScaledWidth = targetWidth;
            encoder.BitmapTransform.ScaledHeight = targetHeight;
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Linear;

            await encoder.FlushAsync();

            stream.Seek(0);
            using var netStream = stream.AsStreamForRead();
            byte[] bytes = new byte[stream.Size];
            int readTotal = 0;
            while (readTotal < bytes.Length)
            {
                int read = await netStream.ReadAsync(bytes, readTotal, bytes.Length - readTotal);
                if (read == 0) break;
                readTotal += read;
            }
            if (readTotal != bytes.Length)
                Array.Resize(ref bytes, readTotal);
            return bytes;
        }

        private static IGraphicsCaptureItemInterop GetGraphicsCaptureItemInterop()
        {
            const string runtimeClass = "Windows.Graphics.Capture.GraphicsCaptureItem";
            IntPtr hstring = IntPtr.Zero;
            IntPtr factoryPtr = IntPtr.Zero;
            try
            {
                int hr = WindowsCreateString(runtimeClass, runtimeClass.Length, out hstring);
                if (hr < 0)
                    Marshal.ThrowExceptionForHR(hr);

                Guid iid = typeof(IGraphicsCaptureItemInterop).GUID;
                hr = RoGetActivationFactory(hstring, ref iid, out factoryPtr);
                if (hr < 0)
                    Marshal.ThrowExceptionForHR(hr);

                return (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
            }
            finally
            {
                if (factoryPtr != IntPtr.Zero)
                    Marshal.Release(factoryPtr);
                if (hstring != IntPtr.Zero)
                    WindowsDeleteString(hstring);
            }
        }

        private static Guid GetGraphicsCaptureItemIid()
        {
            // Correct IID to request from IGraphicsCaptureItemInterop is the WinRT interface IID:
            // Windows.Graphics.Capture.IGraphicsCaptureItem
            try
            {
                var t = Type.GetType("Windows.Graphics.Capture.IGraphicsCaptureItem, Microsoft.Windows.SDK.NET");
                if (t != null && t.GUID != Guid.Empty)
                    return t.GUID;
            }
            catch
            {
                // ignore
            }

            // Fallback: runtime class GUID (may work on some projections)
            return typeof(GraphicsCaptureItem).GUID;
        }

        private static (GraphicsCaptureItem? Item, string Diagnostics) TryCreateItemForMonitorWithDiagnostics(IntPtr hmon)
        {
            var diag = new StringBuilder();
            diag.AppendLine("CreateItemForMonitor diagnostics:");
            diag.AppendLine($"HMONITOR: 0x{hmon.ToInt64():X}");

            var iid = GetGraphicsCaptureItemIid();
            diag.AppendLine($"RequestedIID: {iid}");

            IntPtr temp = IntPtr.Zero;
            try
            {
                var interop = GetGraphicsCaptureItemInterop();
                int hr = interop.CreateForMonitor(hmon, ref iid, out temp);
                diag.AppendLine($"HRESULT: 0x{hr:X8}");
                diag.AppendLine($"ResultPtr: 0x{temp.ToInt64():X}");

                DebugLog($"WGC CreateForMonitor: hr=0x{hr:X8} ptr=0x{temp.ToInt64():X} iid={iid}");
                if (hr != 0)
                    Marshal.ThrowExceptionForHR(hr);
                if (temp == IntPtr.Zero)
                    return (null, diag.ToString());

                var item = MarshalInspectable<GraphicsCaptureItem>.FromAbi(temp);
                return (item, diag.ToString());
            }
            catch (Exception ex)
            {
                diag.AppendLine("Exception: " + ex);
                return (null, diag.ToString());
            }
            finally
            {
                if (temp != IntPtr.Zero)
                    Marshal.Release(temp);
            }
        }

        private static (GraphicsCaptureItem? Item, string Diagnostics) TryCreateItemForWindowWithDiagnostics(IntPtr hwnd)
        {
            var diag = new StringBuilder();
            diag.AppendLine("CreateItemForWindow diagnostics:");
            diag.AppendLine($"HWND: 0x{hwnd.ToInt64():X}");

            var iid = GetGraphicsCaptureItemIid();
            diag.AppendLine($"RequestedIID: {iid}");

            IntPtr temp = IntPtr.Zero;
            try
            {
                var interop = GetGraphicsCaptureItemInterop();
                int hr = interop.CreateForWindow(hwnd, ref iid, out temp);
                diag.AppendLine($"HRESULT: 0x{hr:X8}");
                diag.AppendLine($"ResultPtr: 0x{temp.ToInt64():X}");

                DebugLog($"WGC CreateForWindow: hr=0x{hr:X8} ptr=0x{temp.ToInt64():X} iid={iid} hwnd=0x{hwnd.ToInt64():X}");
                if (hr != 0)
                    Marshal.ThrowExceptionForHR(hr);
                if (temp == IntPtr.Zero)
                    return (null, diag.ToString());

                var item = MarshalInspectable<GraphicsCaptureItem>.FromAbi(temp);
                return (item, diag.ToString());
            }
            catch (Exception ex)
            {
                diag.AppendLine("Exception: " + ex);
                return (null, diag.ToString());
            }
            finally
            {
                if (temp != IntPtr.Zero)
                    Marshal.Release(temp);
            }
        }

        private static byte[] CaptureVirtualScreenGdiJpeg(int targetWidth)
        {
            int x = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int y = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int height = GetSystemMetrics(SM_CYVIRTUALSCREEN);

            if (width <= 0 || height <= 0)
                throw new InvalidOperationException($"Invalid virtual screen size: {width}x{height}");

            using var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);
            }

            Bitmap final = bmp;
            if (targetWidth > 0 && width > targetWidth)
            {
                int targetHeight = (int)(targetWidth * (height / (double)width));
                var resized = new Bitmap(targetWidth, Math.Max(1, targetHeight), PixelFormat.Format24bppRgb);
                using (var g2 = Graphics.FromImage(resized))
                {
                    g2.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g2.CompositingQuality = CompositingQuality.HighQuality;
                    g2.SmoothingMode = SmoothingMode.HighQuality;
                    g2.DrawImage(bmp, 0, 0, resized.Width, resized.Height);
                }
                final = resized;
            }

            try
            {
                using var ms = new MemoryStream();
                final.Save(ms, ImageFormat.Jpeg);
                return ms.ToArray();
            }
            finally
            {
                if (!ReferenceEquals(final, bmp))
                    final.Dispose();
            }
        }

        private static byte[] CaptureScreenBoundsGdiJpeg(Rectangle bounds, int targetWidth)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0)
                throw new InvalidOperationException($"Invalid screen bounds size: {bounds.Width}x{bounds.Height}");

            using var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, new System.Drawing.Size(bounds.Width, bounds.Height), CopyPixelOperation.SourceCopy);
            }

            Bitmap final = bmp;
            if (targetWidth > 0 && bounds.Width > targetWidth)
            {
                int targetHeight = (int)(targetWidth * (bounds.Height / (double)bounds.Width));
                var resized = new Bitmap(targetWidth, Math.Max(1, targetHeight), PixelFormat.Format24bppRgb);
                using (var g2 = Graphics.FromImage(resized))
                {
                    g2.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g2.CompositingQuality = CompositingQuality.HighQuality;
                    g2.SmoothingMode = SmoothingMode.HighQuality;
                    g2.DrawImage(bmp, 0, 0, resized.Width, resized.Height);
                }
                final = resized;
            }

            try
            {
                using var ms = new MemoryStream();
                final.Save(ms, ImageFormat.Jpeg);
                return ms.ToArray();
            }
            finally
            {
                if (!ReferenceEquals(final, bmp))
                    final.Dispose();
            }
        }

        private static IDirect3DDevice? GetOrCreateWinRTDevice()
        {
            if (_winrtDevice != null)
                return _winrtDevice;

            // 创建 D3D11 设备 (BGRA 支持是 WPF/捕获常见要求)
            var flags = DeviceCreationFlags.BgraSupport;
            var result = D3D11CreateDevice(
                adapter: null,
                driverType: DriverType.Hardware,
                flags: flags,
                featureLevels: null,
                device: out ID3D11Device device,
                immediateContext: out _);
            if (result.Failure)
                result.CheckError();
            _d3dDevice = device;

            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var winrtDevicePtr);
            if (hr < 0)
                Marshal.ThrowExceptionForHR(hr);

            try
            {
                _winrtDevice = MarshalInspectable<IDirect3DDevice>.FromAbi(winrtDevicePtr);
                return _winrtDevice;
            }
            finally
            {
                if (winrtDevicePtr != IntPtr.Zero)
                    Marshal.Release(winrtDevicePtr);
            }
        }
    }
}
