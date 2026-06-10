using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.ViewModels;
using Kil0bitSystemMonitor.Models;

namespace Kil0bitSystemMonitor
{
    public class OverlayWindow : IDisposable
    {
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private readonly WndProcDelegate _wndProc = null!;
        private IntPtr _hWnd;
        private IntPtr _hIcon;

        private readonly MainViewModel _viewModel = null!;
        private readonly ConfigService _config = null!;
        private readonly TelemetryService _telemetry = null!;
        private readonly System.Windows.Threading.Dispatcher _dispatcher = null!;
        private readonly System.Threading.Timer _zOrderTimer = null!;

        private bool _isHovered = false;
        private bool _trackingMouse = false;
        private bool _shellFullscreen = false;
        private bool _appbarRegistered = false;
        private readonly Action<SystemMetrics>? _onMetricsUpdated;
        private readonly System.ComponentModel.PropertyChangedEventHandler? _onConfigPropertyChanged;
        private uint _currentDpi = 96;
        private float _dpiScale = 1.0f;

        // Visibility / fade state
        private byte _currentAlpha = 255;
        private byte _targetAlpha = 255;
        private bool _overlayVisible = true;
        private System.Windows.Threading.DispatcherTimer? _fadeTimer;
        private System.Windows.Threading.DispatcherTimer? _hideDebounceTimer;

        private readonly System.Collections.Generic.Dictionary<string, Font> _fontCache = new();
        private readonly System.Collections.Generic.Dictionary<string, float> _measureCache = new();
        private Brush? _cachedBgBrush;
        private Brush? _cachedAccentBrush;
        private Brush? _cachedLabelBrush;
        private Pen? _cachedHoverPen;
        private Brush? _cachedHoverBrush;
        private Brush? _cachedPodBrush;
        // Per-section label brushes (null = use _cachedLabelBrush)
        private Brush? _cachedNetLabelBrush;
        private Brush? _cachedCpuRamLabelBrush;
        private Brush? _cachedGpuLabelBrush;
        private Brush? _cachedDiskLabelBrush;
        // Per-section accent/metric brushes (null = use _cachedAccentBrush)
        private Brush? _cachedNetAccentBrush;
        private Brush? _cachedCpuRamAccentBrush;
        private Brush? _cachedGpuAccentBrush;
        private Brush? _cachedDiskAccentBrush;
        private Bitmap? _offscreenBitmap;
        private Graphics? _offscreenGraphics;

        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const uint WS_POPUP = 0x80000000;
        private const int WM_NCHITTEST = 0x0084;
        private const int WM_RBUTTONUP = 0x0205;
        private const int HTCAPTION = 2;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_MOUSELEAVE = 0x02A3;
        private const int WM_WINDOWPOSCHANGING = 0x0046;
        private const int WM_WINDOWPOSCHANGED = 0x0047;
        private const int WM_EXITSIZEMOVE = 0x0232;
        private const int WM_DISPLAYCHANGE = 0x007E;
        private const int WM_DPICHANGED = 0x02E0;
        private const int WM_SETTINGCHANGE = 0x001A;
        private const uint TME_LEAVE = 0x00000002;
        public const int WM_SETICON = 0x0080;
        public const int ICON_BIG = 1;
        public const int ICON_SMALL = 0;
        public const int WM_SHOW_SETTINGS = 0x0501;
        private const uint WM_APPBAR_CALLBACK = 0x0502;
        private const uint ABM_NEW = 0x00000000;
        private const uint ABM_REMOVE = 0x00000001;
        private const uint ABN_FULLSCREENAPP = 0x00000002;
        private const uint ABM_WINDOWPOSCHANGED = 0x00000009;
        private const uint GW_HWNDPREV = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS { public IntPtr hwnd; public IntPtr hwndInsertAfter; public int x; public int y; public int cx; public int cy; public uint flags; }

        public OverlayWindow(MainViewModel viewModel, ConfigService config, TelemetryService telemetry)
        {
            try
            {
                _viewModel = viewModel;
                _config = config;
                _telemetry = telemetry;
                _dispatcher = System.Windows.Application.Current.Dispatcher;
                _wndProc = WndProc;

                WNDCLASSEX wc = new WNDCLASSEX();
                wc.cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX));
                wc.style = 0x0008;
                wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc);
                wc.hInstance = GetModuleHandle(null);
                wc.lpszClassName = "Kil0bitOverlayWndClass_Main";
                wc.hCursor = LoadCursor(IntPtr.Zero, 32512);

                try
                {
                    string iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "icon.png");
                    if (System.IO.File.Exists(iconPath))
                    {
                        using (var bmp = new System.Drawing.Bitmap(iconPath)) _hIcon = bmp.GetHicon();
                    }
                }
                catch { }

                wc.hIcon = _hIcon;
                wc.hIconSm = _hIcon;
                RegisterClassEx(ref wc);

                int x = (int)_config.Config.X;
                int y = (int)_config.Config.Y;
                if (x < -10000 || x > 10000 || y < -10000 || y > 10000) { x = 100; y = 100; }

                _hWnd = CreateWindowEx(WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW, "Kil0bitOverlayWndClass_Main", "Kil0bit System Monitor Overlay", WS_POPUP, x, y, 300, 32, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
                if (_hWnd == IntPtr.Zero) throw new Exception("Failed to create window");

                if (_hIcon != IntPtr.Zero) { SendMessage(_hWnd, WM_SETICON, (IntPtr)ICON_BIG, _hIcon); SendMessage(_hWnd, WM_SETICON, (IntPtr)ICON_SMALL, _hIcon); }

                _currentDpi = GetDpiForWindow(_hWnd);
                if (_currentDpi == 0) _currentDpi = 96;
                _dpiScale = _currentDpi / 96.0f;

                // Disable DWM animations to prevent flickering during Task View zoom transitions
                int disableTransitions = 1;
                Win32Helper.DwmSetWindowAttribute(_hWnd, 3, ref disableTransitions, sizeof(int));

                // Snapping setup (Only attach parent handle if snapping is enabled at launch)
                if (_config.Config.StickToTaskbar)
                {
                    AttachToTaskbar();
                }
                else
                {
                    AlignToTaskbarCenter();
                }
                ShowWindow(_hWnd, 5);
                UpdateCachedColors();
                UpdateLayer();

                _onMetricsUpdated = (m) => {
                    _dispatcher.BeginInvoke(() => {
                        _viewModel.Metrics = m;
                        // Only re-render if visible or transitioning
                        if (_targetAlpha > 0 || _currentAlpha > 0) UpdateLayer();
                    });
                };
                _telemetry.MetricsUpdated += _onMetricsUpdated;
                _zOrderTimer = new System.Threading.Timer(EnforceZOrder, null, 0, 500);

                _onConfigPropertyChanged = (s, e) => {
                    _dispatcher.BeginInvoke(() => {
                        if (e.PropertyName == nameof(_config.Config.AccentColorHex) || e.PropertyName == nameof(_config.Config.LabelColorHex) || e.PropertyName == nameof(_config.Config.BackgroundColorHex) || e.PropertyName == nameof(_config.Config.PodColorHex) || e.PropertyName == nameof(_config.Config.FontFamily)
                            || e.PropertyName == nameof(_config.Config.NetLabelColorHex) || e.PropertyName == nameof(_config.Config.CpuRamLabelColorHex) || e.PropertyName == nameof(_config.Config.GpuLabelColorHex) || e.PropertyName == nameof(_config.Config.DiskLabelColorHex)
                            || e.PropertyName == nameof(_config.Config.NetAccentColorHex) || e.PropertyName == nameof(_config.Config.CpuRamAccentColorHex) || e.PropertyName == nameof(_config.Config.GpuAccentColorHex) || e.PropertyName == nameof(_config.Config.DiskAccentColorHex))
                        {
                            ClearCaches();
                            UpdateCachedColors();
                        }
                        if (e.PropertyName == nameof(_config.Config.StickToTaskbar))
                        {
                            // Snapping toggle handled dynamically to clear/restore HWND parent
                            if (_config.Config.StickToTaskbar)
                            {
                                AttachToTaskbar();
                            }
                            else
                            {
                                // Remove taskbar owner so it floats freely
                                Win32Helper.SetWindowLongPtr(_hWnd, Win32Helper.GWL_HWNDPARENT, IntPtr.Zero);
                                UnregisterAppBar();
                                AlignToTaskbarCenter();
                            }
                        }
                        if (e.PropertyName == nameof(_config.Config.ShowOverlay) || e.PropertyName == nameof(_config.Config.HideOnFullscreen) || e.PropertyName == nameof(_config.Config.StickToTaskbar) || e.PropertyName == nameof(_config.Config.ShowPods) || e.PropertyName == nameof(_config.Config.ShowBackground) || e.PropertyName == nameof(_config.Config.AlwaysOnTop))
                        {
                            UpdateVisibility();
                            // One-time Z-order update for smooth transition
                            IntPtr zOrder = _config.Config.AlwaysOnTop ? Win32Helper.HWND_TOPMOST : Win32Helper.HWND_NOTOPMOST;
                            SetWindowPos(_hWnd, zOrder, 0, 0, 0, 0, Win32Helper.SWP_NOMOVE | Win32Helper.SWP_NOSIZE | Win32Helper.SWP_NOACTIVATE | 0x0040);
                        }
                        UpdateLayer();
                    });
                };
                _config.Config.PropertyChanged += _onConfigPropertyChanged;
                if (!_config.Config.ShowOverlay) { ShowWindow(_hWnd, 0); _overlayVisible = false; _currentAlpha = 0; _targetAlpha = 0; }
            }
            catch { throw; }
        }

        private void EnforceZOrder(object? state)
        {
            _dispatcher.BeginInvoke(() =>
            {
                bool show = ShouldShowOverlay();

                if (show)
                {
                    _hideDebounceTimer?.Stop();
                    if (_targetAlpha != 255) { _targetAlpha = 255; StartFade(); }
                }
                else
                {
                    // Debounce hide by 800ms to prevent flickering during shell animations
                    if (_hideDebounceTimer == null)
                    {
                        _hideDebounceTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
                        _hideDebounceTimer.Tick += (s, e) => { _hideDebounceTimer.Stop(); if (!ShouldShowOverlay()) { _targetAlpha = 0; StartFade(); } };
                    }
                    if (!_hideDebounceTimer.IsEnabled && _targetAlpha != 0) _hideDebounceTimer.Start();
                }

                // Enforce TOPMOST Z-order only if the taskbar is not the foreground active window.
                // Re-asserting TOPMOST while the taskbar is active and managing its Z-order causes blinking.
                // However, we must enforce it when other windows (like Task View) are active to keep the overlay visible.
                if (_overlayVisible && _config.Config.AlwaysOnTop)
                {
                    IntPtr fg = GetForegroundWindow();
                    StringBuilder sb = new StringBuilder(256);
                    Win32Helper.GetClassName(fg, sb, sb.Capacity);
                    string fgClass = sb.ToString();

                    if (fgClass != "Shell_TrayWnd" && fgClass != "Shell_SecondaryTrayWnd")
                    {
                        // Smart check: Only re-assert TOPMOST if we are NOT already the top-most window.
                        IntPtr prev = GetWindow(_hWnd, GW_HWNDPREV);
                        if (prev != IntPtr.Zero)
                        {
                            SetWindowPos(_hWnd, Win32Helper.HWND_TOPMOST, 0, 0, 0, 0, Win32Helper.SWP_NOMOVE | Win32Helper.SWP_NOSIZE | Win32Helper.SWP_NOACTIVATE | 0x0040);
                        }
                    }
                }
            });
        }

        private void AttachToTaskbar()
        {
            IntPtr taskbarHwnd = Win32Helper.FindWindow("Shell_TrayWnd", null!);
            if (taskbarHwnd != IntPtr.Zero)
            {
                Win32Helper.SetWindowLongPtr(_hWnd, Win32Helper.GWL_HWNDPARENT, taskbarHwnd);
                RegisterAppBar();
                AlignToTaskbarCenter();
            }
        }

        private void RegisterAppBar() { if (_appbarRegistered || _hWnd == IntPtr.Zero) return; APPBARDATA abd = new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)), hWnd = _hWnd, uCallbackMessage = WM_APPBAR_CALLBACK }; SHAppBarMessage(ABM_NEW, ref abd); _appbarRegistered = true; }
        private void UnregisterAppBar() { if (!_appbarRegistered || _hWnd == IntPtr.Zero) return; APPBARDATA abd = new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)), hWnd = _hWnd }; SHAppBarMessage(ABM_REMOVE, ref abd); _appbarRegistered = false; }
        private void UpdateVisibility()
        {
            bool show = ShouldShowOverlay();
            if (show)
            {
                _hideDebounceTimer?.Stop();
                _targetAlpha = 255;
                StartFade();
            }
            else
            {
                if (_hideDebounceTimer == null)
                {
                    _hideDebounceTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                    _hideDebounceTimer.Tick += (s, e) => { _hideDebounceTimer.Stop(); if (!ShouldShowOverlay()) { _targetAlpha = 0; StartFade(); } };
                }
                if (!_hideDebounceTimer.IsEnabled && _targetAlpha != 0) _hideDebounceTimer.Start();
            }
        }

        // Starts the fade timer if not already running.
        private void StartFade()
        {
            if (_fadeTimer == null)
            {
                _fadeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                _fadeTimer.Tick += (s, e) => FadeTick();
            }
            if (!_fadeTimer.IsEnabled) _fadeTimer.Start();
        }

        // Steps _currentAlpha toward _targetAlpha at ~150ms for a full 0↔255 transition.
        private void FadeTick()
        {
            const int step = 30; // 255/30 ≈ 9 frames × 16ms ≈ 144ms
            if (_currentAlpha < _targetAlpha)
            {
                // Fading in — make sure window is shown before first pixel appears
                if (!_overlayVisible) { ShowWindow(_hWnd, 5); _overlayVisible = true; }
                _currentAlpha = (byte)Math.Min(255, _currentAlpha + step);
            }
            else if (_currentAlpha > _targetAlpha)
            {
                _currentAlpha = (byte)Math.Max(0, _currentAlpha - step);
            }

            // Reblit the existing bitmap with the new alpha — no re-render needed
            if (_offscreenBitmap != null) SetBitmap(_offscreenBitmap);

            if (_currentAlpha == _targetAlpha)
            {
                _fadeTimer!.Stop();
                // Only call ShowWindow(0) once we are fully transparent to avoid blink
                if (_currentAlpha == 0 && _overlayVisible) { ShowWindow(_hWnd, 0); _overlayVisible = false; }
            }
        }

        private bool ShouldShowOverlay()
        {
            if (!_config.Config.ShowOverlay) return false;

            if (_config.Config.HideOnFullscreen)
            {
                IntPtr fg = GetForegroundWindow();
                // Priority: If we are in the shell (Task View, Desktop), always show
                if (IsShellWindow(fg)) return true;

                // ABN_FULLSCREENAPP fired — shell says a fullscreen app is covering the taskbar
                if (_shellFullscreen) return false;

                // Taskbar rect collapsed = autohide triggered by a fullscreen/exclusive app
                IntPtr taskbarHwnd = Win32Helper.FindWindow("Shell_TrayWnd", null!);
                if (taskbarHwnd != IntPtr.Zero && Win32Helper.GetWindowRect(taskbarHwnd, out Win32Helper.RECT tbRect))
                    if ((tbRect.Bottom - tbRect.Top) <= 4 || (tbRect.Right - tbRect.Left) <= 4) return false;

                // Fallback: check foreground window — catches windowed-fullscreen games that never
                // fire ABN_FULLSCREENAPP (most modern titles, browser F11, video players, etc.)
                if (fg != IntPtr.Zero && fg != _hWnd)
                {
                    // Maximized windows on no-taskbar displays cover the full monitor rect; require borderless to qualify as fullscreen.
                    const long WS_CAPTION = 0x00C00000L;
                    long style = Win32Helper.GetWindowLong(fg, Win32Helper.GWL_STYLE);
                    bool hasCaption = (style & WS_CAPTION) != 0;

                    // Check if foreground window covers the whole monitor
                    if (!hasCaption && Win32Helper.GetWindowRect(fg, out Win32Helper.RECT fgRect))
                    {
                        IntPtr hMon = MonitorFromWindow(fg, 1); // MONITOR_DEFAULTTONEAREST
                        MONITORINFO mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFO)) };
                        if (GetMonitorInfo(hMon, ref mi))
                        {
                            var s = mi.rcMonitor;
                            if (fgRect.Left <= s.Left && fgRect.Top <= s.Top &&
                                fgRect.Right >= s.Right && fgRect.Bottom >= s.Bottom)
                            {
                                return false;
                            }
                        }
                    }
                }
            }

            return true;
        }

        private bool IsShellWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return false;
            StringBuilder sb = new StringBuilder(256);
            Win32Helper.GetClassName(hWnd, sb, sb.Capacity);
            string cls = sb.ToString();

            // Core Windows Shell and UWP overlay window classes
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd" ||
                cls == "MultitaskingViewFrame" || cls == "TaskView" || cls == "Windows.UI.Core.CoreWindow" ||
                cls == "XamlExplorerViewHostWindow" || cls == "DesktopWindowXamlSource" ||
                cls == "Windows.UI.Input.InputSite.WindowClass" || cls == "PopupHost")
                return true;

            try
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid != 0)
                {
                    IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    if (hProcess != IntPtr.Zero)
                    {
                        try
                        {
                            uint size = 1024;
                            StringBuilder buffer = new StringBuilder((int)size);
                            if (QueryFullProcessImageName(hProcess, 0, buffer, ref size))
                            {
                                string fullPath = buffer.ToString();
                                string pname = System.IO.Path.GetFileNameWithoutExtension(fullPath).ToLowerInvariant();
                                return (pname == "explorer" || pname == "shellexperiencehost" ||
                                        pname == "startmenuexperiencehost" || pname == "searchhost" ||
                                        pname == "dwm");
                            }
                        }
                        finally
                        {
                            CloseHandle(hProcess);
                        }
                    }
                    else
                    {
                        // OpenProcess failed (Access Denied / protected process).
                        // Highly protected UWP/system processes (like StartMenuExperienceHost or SYSTEM processes)
                        // are definitely shell/system windows, not standard user apps or games.
                        if (Marshal.GetLastWin32Error() == 5) // ERROR_ACCESS_DENIED
                        {
                            return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        private void AlignToTaskbarCenter()
        {
            if (!_config.Config.StickToTaskbar) { SetWindowPos(_hWnd, IntPtr.Zero, (int)_config.Config.X, (int)_config.Config.Y, 0, 0, 0x0001 | 0x0004 | 0x0010); return; }
            IntPtr taskbar = Win32Helper.FindWindow("Shell_TrayWnd", null!);
            if (taskbar != IntPtr.Zero && Win32Helper.GetWindowRect(taskbar, out Win32Helper.RECT tb))
            {
                int h = tb.Bottom - tb.Top;
                int oh = (int)((_config.Config.ShowPods ? 36 : 32) * _dpiScale * (float)_config.Config.ScaleFactor);
                int cy = tb.Top + (h - oh) / 2;
                SetWindowPos(_hWnd, IntPtr.Zero, (int)_config.Config.X, cy, 0, 0, 0x0001 | 0x0004 | 0x0010);
                _config.Config.Y = cy;
            }
        }

        // Reserve: worst-case string to measure for stable column width. Null = use live Value width.
        private class MetricItem { public string Label { get; set; } = ""; public string Value { get; set; } = ""; public string? Reserve { get; set; } = null; }

        private void UpdateLayer()
        {
            if (_targetAlpha == 0 && _currentAlpha == 0) return;
            var columns = PrepareMetricsData();
            float scale = _dpiScale * (float)_config.Config.ScaleFactor;
            float textScale = (float)_config.Config.ScaleFactor;
            bool pods = _config.Config.ShowPods;
            string fontName = _config.Config.FontFamily;
            if (string.IsNullOrEmpty(fontName) || fontName == "Default") fontName = "Segoe UI";
            System.Drawing.FontStyle style = _config.Config.IsTextBold ? System.Drawing.FontStyle.Bold : System.Drawing.FontStyle.Regular;
            Font font = GetCachedFont(fontName, 8.5f * textScale, style);

            int h = (int)((pods ? 36 : 32) * scale); // pods get 4px extra height for top/bottom breathing room
            float gap = 2 * scale;                          // label→value gap
            float podGap = Math.Max(0, _config.Config.ColumnSpacing) * scale;  // user-controlled column spacing
            float pad = (pods ? 4 : 0) * scale;             // pod inner horizontal padding

            float[] widths = new float[columns.Count];
            float total = 2 * scale;                         // left outer margin
            for (int i = 0; i < columns.Count; i++)
            {
                var col = columns[i];
                float GetItemWidth(MetricItem? item) {
                    if (item == null) return 0;
                    // Use the reserve string width when available so the column never resizes on value change
                    float valW = item.Reserve != null ? GetCachedMeasure(item.Reserve, font) : GetCachedMeasure(item.Value, font);
                    return GetCachedMeasure(item.Label, font) + gap + valW;
                }

                widths[i] = Math.Max(GetItemWidth(col.Top), GetItemWidth(col.Bottom)) + (pad * 2);

                total += widths[i] + podGap;
            }
            total = total - podGap + (2 * scale);           // right outer margin (was 4)

            int w = (int)Math.Max(20, total);
            EnsureOffscreenBuffer(w, h);
            if (_offscreenGraphics == null || _offscreenBitmap == null) return;

            _offscreenGraphics.Clear(Color.Transparent);
            RenderBackground(_offscreenGraphics, w, h, scale);
            RenderHoverEffect(_offscreenGraphics, w, h, scale);

            Brush vBrush = _cachedAccentBrush ?? Brushes.White;
            Brush lBrush = _cachedLabelBrush ?? Brushes.Cyan;
            bool ownPBrush = _cachedPodBrush == null;
            Brush pBrush = _cachedPodBrush ?? new SolidBrush(Color.FromArgb(15, 255, 255, 255));
            using var pPen = new Pen(Color.FromArgb(20, 255, 255, 255), 1);

            // Section brushes: fall back to global brush when per-section color is not set
            Brush netLBrush  = _cachedNetLabelBrush    ?? lBrush;
            Brush cpuLBrush  = _cachedCpuRamLabelBrush ?? lBrush;
            Brush gpuLBrush  = _cachedGpuLabelBrush    ?? lBrush;
            Brush dskLBrush  = _cachedDiskLabelBrush   ?? lBrush;
            Brush netVBrush  = _cachedNetAccentBrush    ?? vBrush;
            Brush cpuVBrush  = _cachedCpuRamAccentBrush ?? vBrush;
            Brush gpuVBrush  = _cachedGpuAccentBrush    ?? vBrush;
            Brush dskVBrush  = _cachedDiskAccentBrush   ?? vBrush;

            float cx = 2 * scale;                          // start drawing from left margin (was 4)
            for (int i = 0; i < columns.Count; i++)
            {
                var col = columns[i];
                if (pods)
                {
                    using (var path = CreateRoundedRectPath((int)cx, (int)(2 * scale), (int)widths[i], (int)(h - 4 * scale), (int)(6 * scale)))
                    { _offscreenGraphics.FillPath(pBrush, path); _offscreenGraphics.DrawPath(pPen, path); }
                }

                // Pick the correct label and accent brushes for this column's section
                Brush sectionLBrush = col.Section == SectionNet    ? netLBrush
                                    : col.Section == SectionCpuRam ? cpuLBrush
                                    : col.Section == SectionGpu    ? gpuLBrush
                                    : dskLBrush;
                Brush sectionVBrush = col.Section == SectionNet    ? netVBrush
                                    : col.Section == SectionCpuRam ? cpuVBrush
                                    : col.Section == SectionGpu    ? gpuVBrush
                                    : dskVBrush;

                float contentX = cx + pad;
                // Fix: calculate y positions so both text rows are fully contained within h
                float lineH = font.Height;
                float totalTextH = lineH * 2 + (2 * scale);
                float y1 = (h - totalTextH) / 2f;
                float y2 = y1 + lineH + (2 * scale);

                Action<MetricItem, float> drawItem = (item, y) => {
                    float lw = GetCachedMeasure(item.Label, font);
                    _offscreenGraphics.DrawString(item.Label, font, sectionLBrush, contentX, y);
                    _offscreenGraphics.DrawString(item.Value, font, sectionVBrush, contentX + lw + gap, y);
                };

                if (col.Top != null && col.Bottom != null)
                {
                    drawItem(col.Top, y1);
                    drawItem(col.Bottom, y2);
                }
                else
                {
                    var item = col.Top ?? col.Bottom;
                    if (item != null) drawItem(item, (h - font.Height) / 2f);
                }
                cx += widths[i] + podGap;
            }
            SetBitmap(_offscreenBitmap);
            if (ownPBrush) pBrush.Dispose();
        }

        private string FormatDiskSpeed(float kbps)
        {
            if (kbps >= 1024 * 1024) return $"{(kbps / 1024f / 1024f):F1} GB/s";
            if (kbps >= 1024f) return $"{(kbps / 1024f):F1} MB/s";
            return $"{kbps:F0} KB/s";
        }

        // Section indices for per-section brush lookup: 0=Net, 1=CPU/RAM, 2=GPU, 3=Disk
        private const int SectionNet = 0, SectionCpuRam = 1, SectionGpu = 2, SectionDisk = 3;

        private System.Collections.Generic.List<(MetricItem? Top, MetricItem? Bottom, int Section)> PrepareMetricsData()
        {
            bool compact = (_config.Config.DisplayStyle ?? "Text") == "Compact";
            var m = _viewModel.Metrics; var c = _config.Config;

            MetricItem Pct(string f, string cp, string v)  => new MetricItem { Label = compact ? cp : f, Value = v, Reserve = "100%" };
            MetricItem Temp(string f, string cp, string v) => new MetricItem { Label = compact ? cp : f, Value = v, Reserve = "100°" };
            // Reserve "1023 MB/s": widest net format before switching to GB/s (M glyph is wider than K)
            MetricItem Net(string f, string cp, string v)  => new MetricItem { Label = compact ? cp : f, Value = v, Reserve = "1023 MB/s" };

            var list = new System.Collections.Generic.List<(MetricItem?, MetricItem?, int)>();

            if (c.ShowNetUp || c.ShowNetDown)
                list.Add((c.ShowNetUp ? Net("UP ", "U", m.NetUpText) : null, c.ShowNetDown ? Net("DN ", "D", m.NetDownText) : null, SectionNet));

            if (c.ShowCpu || c.ShowRam)
                list.Add((c.ShowCpu ? Pct("CPU", "C", $"{(int)m.CpuUsage}%") : null, c.ShowRam ? Pct("RAM", "R", $"{(int)m.RamPercent}%") : null, SectionCpuRam));

            string tempStr = m.GpuTemperature > 0 ? $"{(int)m.GpuTemperature}°" : "N/A";
            if (c.ShowGpu || c.ShowTemp)
                list.Add((c.ShowGpu ? Pct("GPU", "G", $"{(int)m.GpuUsage}%") : null, c.ShowTemp ? Temp("TMP", "T", tempStr) : null, SectionGpu));

            if (c.ShowVram)
            {
                MetricItem vram;
                if (m.VramTotalBytes > 0 && (c.VramDisplayStyle ?? "Percent") == "Used / Total")
                {
                    float usedGb = m.VramUsedBytes / 1073741824f;
                    float totalGb = m.VramTotalBytes / 1073741824f;
                    // Reserve the used field at full capacity so the column never resizes
                    vram = new MetricItem { Label = compact ? "V" : "VRM", Value = $"{usedGb:F1}/{totalGb:F1} GB", Reserve = $"{totalGb:F1}/{totalGb:F1} GB" };
                }
                else
                {
                    vram = new MetricItem { Label = compact ? "V" : "VRM", Value = m.VramTotalBytes > 0 ? $"{(int)m.VramPercent}%" : "N/A", Reserve = "100%" };
                }
                list.Add((vram, null, SectionGpu));
            }

            if (c.ShowDisk || c.ShowDiskSpeed)
            {
                if (m.Disks != null && m.Disks.Count > 0)
                {
                    foreach (var d in m.Disks)
                    {
                        // Clean name: "0 C: D:" -> "C"
                        string letter = d.Name;
                        int colonIdx = letter.IndexOf(':');
                        if (colonIdx > 0) letter = letter.Substring(colonIdx - 1, 1);
                        else if (letter.Length > 0) letter = letter.Substring(0, 1);

                        string cdkLabel = letter.ToUpper() + "DK";

                        list.Add((
                            c.ShowDisk ? Pct(cdkLabel, letter, $"{(int)d.SpacePercent}%") : null,
                            c.ShowDiskSpeed ? Pct("SPD", "S", $"{(int)d.ActivityPercent}%") : null,
                            SectionDisk
                        ));
                    }
                }
            }

            return list;
        }

        private void EnsureOffscreenBuffer(int w, int h)
        {
            if (_offscreenBitmap == null || _offscreenBitmap.Width != w || _offscreenBitmap.Height != h)
            {
                _offscreenGraphics?.Dispose(); _offscreenBitmap?.Dispose();
                _offscreenBitmap = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                _offscreenGraphics = Graphics.FromImage(_offscreenBitmap);
                _offscreenGraphics.SmoothingMode = SmoothingMode.AntiAlias;
                _offscreenGraphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            }
        }

        private void RenderBackground(Graphics g, int w, int h, float s) { if (!_config.Config.ShowBackground || _cachedBgBrush == null) return; using (var p = CreateRoundedRectPath(0, 0, w, h, (int)(12 * s))) g.FillPath(_cachedBgBrush, p); }
        private void RenderHoverEffect(Graphics g, int w, int h, float s) { if (!_isHovered || _cachedHoverBrush == null || _cachedHoverPen == null) return; using (var p = CreateRoundedRectPath(0, 0, w - 1, h - 1, (int)(12 * s))) { g.FillPath(_cachedHoverBrush, p); g.DrawPath(_cachedHoverPen, p); } }
        private GraphicsPath CreateRoundedRectPath(int x, int y, int w, int h, int r) { GraphicsPath p = new GraphicsPath(); if (r <= 0) { p.AddRectangle(new Rectangle(x, y, w, h)); return p; } p.AddArc(x, y, r, r, 180, 90); p.AddArc(x + w - r, y, r, r, 270, 90); p.AddArc(x + w - r, y + h - r, r, r, 0, 90); p.AddArc(x, y + h - r, r, r, 90, 90); p.CloseFigure(); return p; }
        private Font GetCachedFont(string f, float s, System.Drawing.FontStyle st) { string k = $"{f}_{s}_{st}"; if (!_fontCache.TryGetValue(k, out var font)) { font = new Font(f, s, st); _fontCache[k] = font; } return font; }
        private void UpdateCachedColors()
        {
            _cachedBgBrush?.Dispose(); _cachedAccentBrush?.Dispose(); _cachedLabelBrush?.Dispose(); _cachedPodBrush?.Dispose(); _cachedHoverPen?.Dispose(); _cachedHoverBrush?.Dispose();
            _cachedNetLabelBrush?.Dispose(); _cachedCpuRamLabelBrush?.Dispose(); _cachedGpuLabelBrush?.Dispose(); _cachedDiskLabelBrush?.Dispose();
            _cachedNetAccentBrush?.Dispose(); _cachedCpuRamAccentBrush?.Dispose(); _cachedGpuAccentBrush?.Dispose(); _cachedDiskAccentBrush?.Dispose();
            _cachedBgBrush = new SolidBrush(HexToColor(_config.Config.BackgroundColorHex ?? "#B4141414"));
            _cachedAccentBrush = new SolidBrush(HexToColor(_config.Config.AccentColorHex ?? "#FFFFFF"));
            _cachedLabelBrush = new SolidBrush(HexToColor(_config.Config.LabelColorHex ?? "#00CCFF"));
            _cachedPodBrush = new SolidBrush(HexToColor(_config.Config.PodColorHex ?? "#0FFFFFFF"));
            _cachedHoverPen = new Pen(Color.FromArgb(20, 255, 255, 255));
            _cachedHoverBrush = new SolidBrush(Color.FromArgb(25, 255, 255, 255));
            // Per-section label brushes: only create if a custom color is set
            _cachedNetLabelBrush    = string.IsNullOrEmpty(_config.Config.NetLabelColorHex)    ? null : new SolidBrush(HexToColor(_config.Config.NetLabelColorHex));
            _cachedCpuRamLabelBrush = string.IsNullOrEmpty(_config.Config.CpuRamLabelColorHex) ? null : new SolidBrush(HexToColor(_config.Config.CpuRamLabelColorHex));
            _cachedGpuLabelBrush    = string.IsNullOrEmpty(_config.Config.GpuLabelColorHex)    ? null : new SolidBrush(HexToColor(_config.Config.GpuLabelColorHex));
            _cachedDiskLabelBrush   = string.IsNullOrEmpty(_config.Config.DiskLabelColorHex)   ? null : new SolidBrush(HexToColor(_config.Config.DiskLabelColorHex));
            // Per-section accent brushes: only create if a custom color is set
            _cachedNetAccentBrush    = string.IsNullOrEmpty(_config.Config.NetAccentColorHex)    ? null : new SolidBrush(HexToColor(_config.Config.NetAccentColorHex));
            _cachedCpuRamAccentBrush = string.IsNullOrEmpty(_config.Config.CpuRamAccentColorHex) ? null : new SolidBrush(HexToColor(_config.Config.CpuRamAccentColorHex));
            _cachedGpuAccentBrush    = string.IsNullOrEmpty(_config.Config.GpuAccentColorHex)    ? null : new SolidBrush(HexToColor(_config.Config.GpuAccentColorHex));
            _cachedDiskAccentBrush   = string.IsNullOrEmpty(_config.Config.DiskAccentColorHex)   ? null : new SolidBrush(HexToColor(_config.Config.DiskAccentColorHex));
        }

        private float GetCachedMeasure(string t, Font f) { if (_offscreenGraphics == null) return 0; string k = $"{t}_{f.Name}_{f.Size}_{f.Style}"; if (!_measureCache.TryGetValue(k, out var w)) { w = _offscreenGraphics.MeasureString(t, f, PointF.Empty, StringFormat.GenericTypographic).Width; _measureCache[k] = w; } return w; }
        private void ClearCaches() { foreach (var f in _fontCache.Values) f.Dispose(); _fontCache.Clear(); _measureCache.Clear(); }
        private void SetBitmap(Bitmap bitmap)
        {
            IntPtr windowDC = GetWindowDC(_hWnd); IntPtr memDC = CreateCompatibleDC(windowDC); IntPtr hBitmap = IntPtr.Zero; IntPtr oldBitmap = IntPtr.Zero;
            try
            {
                hBitmap = bitmap.GetHbitmap(Color.FromArgb(0)); oldBitmap = SelectObject(memDC, hBitmap);
                SIZE size = new SIZE { cx = bitmap.Width, cy = bitmap.Height }; POINT ps = new POINT { x = 0, y = 0 }; POINT tp;
                if (Win32Helper.GetWindowRect(_hWnd, out Win32Helper.RECT wr)) tp = new POINT { x = wr.Left, y = wr.Top }; else tp = new POINT { x = (int)_config.Config.X, y = (int)_config.Config.Y };
                BLENDFUNCTION b = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = _currentAlpha, AlphaFormat = 1 };
                UpdateLayeredWindow(_hWnd, windowDC, ref tp, ref size, memDC, ref ps, 0, ref b, 2);
            }
            finally { if (hBitmap != IntPtr.Zero) { SelectObject(memDC, oldBitmap); DeleteObject(hBitmap); } DeleteDC(memDC); ReleaseDC(_hWnd, windowDC); }
        }

        private Color HexToColor(string hex)
        {
            try { hex = hex.Replace("#", ""); if (hex.Length == 8) return Color.FromArgb(int.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber), int.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber), int.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber), int.Parse(hex.Substring(6, 2), System.Globalization.NumberStyles.HexNumber));
                if (hex.Length == 6) return Color.FromArgb(255, int.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber), int.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber), int.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber)); } catch { } return Color.White;
        }

        public void Dispose()
        {
            try { _telemetry.MetricsUpdated -= _onMetricsUpdated; _config.Config.PropertyChanged -= _onConfigPropertyChanged; _zOrderTimer?.Dispose(); _fadeTimer?.Stop(); UnregisterAppBar(); ClearCaches(); _offscreenGraphics?.Dispose(); _offscreenBitmap?.Dispose(); _cachedBgBrush?.Dispose(); _cachedAccentBrush?.Dispose(); _cachedLabelBrush?.Dispose(); _cachedPodBrush?.Dispose(); _cachedHoverPen?.Dispose(); _cachedHoverBrush?.Dispose(); _cachedNetLabelBrush?.Dispose(); _cachedCpuRamLabelBrush?.Dispose(); _cachedGpuLabelBrush?.Dispose(); _cachedDiskLabelBrush?.Dispose(); _cachedNetAccentBrush?.Dispose(); _cachedCpuRamAccentBrush?.Dispose(); _cachedGpuAccentBrush?.Dispose(); _cachedDiskAccentBrush?.Dispose(); if (_hWnd != IntPtr.Zero) DestroyWindow(_hWnd); if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon); } catch { }
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == 0x0084) return (IntPtr)1;
            if (msg == 0x0010) return IntPtr.Zero; // WM_CLOSE — ignore, overlay is not closeable
            if (msg == WM_WINDOWPOSCHANGING && _config.Config.StickToTaskbar)
            {
                WINDOWPOS pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                IntPtr taskbar = Win32Helper.FindWindow("Shell_TrayWnd", "");
                if (taskbar != IntPtr.Zero && Win32Helper.GetWindowRect(taskbar, out Win32Helper.RECT tb)) { int oh = (int)((_config.Config.ShowPods ? 36 : 32) * _dpiScale * (float)_config.Config.ScaleFactor); pos.y = tb.Top + (tb.Bottom - tb.Top - oh) / 2; Marshal.StructureToPtr(pos, lParam, false); }
            }
            if (msg == WM_WINDOWPOSCHANGED) { if (_appbarRegistered) { APPBARDATA abd = new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)), hWnd = _hWnd }; SHAppBarMessage(ABM_WINDOWPOSCHANGED, ref abd); } return IntPtr.Zero; }
            if (msg == WM_APPBAR_CALLBACK) { if ((uint)wParam.ToInt32() == ABN_FULLSCREENAPP) { _shellFullscreen = (lParam != IntPtr.Zero); _dispatcher.BeginInvoke(UpdateVisibility); } return IntPtr.Zero; }
            if (msg == WM_EXITSIZEMOVE) { if (Win32Helper.GetWindowRect(hWnd, out Win32Helper.RECT r)) { _config.Config.X = r.Left; _config.Config.Y = r.Top; _config.SaveConfig(); } }
            if (msg == WM_SHOW_SETTINGS) { _dispatcher.BeginInvoke(() => App.OpenSettings(_viewModel, _config)); return IntPtr.Zero; }
            if (msg == WM_DPICHANGED) { _currentDpi = (uint)(wParam.ToInt32() & 0xFFFF); _dpiScale = _currentDpi / 96.0f; ClearCaches(); AlignToTaskbarCenter(); UpdateLayer(); return IntPtr.Zero; }
            if (msg == WM_DISPLAYCHANGE || msg == WM_SETTINGCHANGE) { AlignToTaskbarCenter(); UpdateLayer(); return IntPtr.Zero; }
            if (msg == WM_MOUSEMOVE) { if (!_trackingMouse) { TRACKMOUSEEVENT tme = new TRACKMOUSEEVENT { cbSize = (uint)Marshal.SizeOf(typeof(TRACKMOUSEEVENT)), dwFlags = TME_LEAVE, hwndTrack = hWnd }; TrackMouseEvent(ref tme); _trackingMouse = true; _isHovered = true; UpdateLayer(); } }
            if (msg == WM_MOUSELEAVE) { _trackingMouse = false; _isHovered = false; UpdateLayer(); }
            if (msg == WM_LBUTTONDBLCLK) { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskmgr") { UseShellExecute = true }); return IntPtr.Zero; }
            if (msg == WM_LBUTTONDOWN) { if (_config.Config.LockPosition) return IntPtr.Zero; ReleaseCapture(); SendMessage(hWnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero); return IntPtr.Zero; }
            if (msg == WM_RBUTTONUP)
            {
                if (Win32Helper.GetCursorPos(out Win32Helper.POINT pt))
                {
                    SetPreferredAppMode(2); AllowDarkModeForWindow(hWnd, true); FlushMenuThemes();
                    IntPtr hMenu = CreatePopupMenu();
                    AppendMenu(hMenu, 0, 1001, "Settings");
                    AppendMenu(hMenu, 0, 1002, "Task Manager");
                    AppendMenu(hMenu, 0x0800, 0, null);
                    AppendMenu(hMenu, (_config.Config.AlwaysOnTop ? 0x0008U : 0), 1008, "Keep on Top");
                    AppendMenu(hMenu, (_config.Config.HideOnFullscreen ? 0x0008U : 0), 1009, "Hide in Fullscreen");
                    AppendMenu(hMenu, (_config.Config.LockPosition ? 0x0008U : 0), 1006, "Lock Position");
                    AppendMenu(hMenu, (_config.Config.StickToTaskbar ? 0x0008U : 0), 1007, "Snap to Taskbar");
                    AppendMenu(hMenu, 0x0800, 0, null);
                    AppendMenu(hMenu, 0, 1003, "About");
                    AppendMenu(hMenu, 0x0800, 0, null);
                    AppendMenu(hMenu, 0, 1004, "Exit");
                    SetForegroundWindow(hWnd);

                    Win32Helper.GetWindowRect(hWnd, out Win32Helper.RECT wr);
                    IntPtr hMon = MonitorFromWindow(hWnd, 1);
                    MONITORINFO mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFO)) };
                    GetMonitorInfo(hMon, ref mi);

                    int my;
                    uint alignFlag;
                    // If the overlay is in the bottom half of the screen, pop the menu UP
                    if (wr.Top > (mi.rcWork.Top + mi.rcWork.Bottom) / 2)
                    {
                        my = wr.Top - 4;
                        alignFlag = 0x0020; // TPM_BOTTOMALIGN
                    }
                    else
                    {
                        my = wr.Bottom + 4;
                        alignFlag = 0x0000; // TPM_TOPALIGN
                    }

                    int ch = TrackPopupMenuEx(hMenu, 0x0100 | 0x0002 | alignFlag, pt.X, my, hWnd, IntPtr.Zero);
                    DestroyMenu(hMenu);
                    if (ch == 1001) _dispatcher.BeginInvoke(() => App.OpenSettings(_viewModel, _config));
                    else if (ch == 1006) { _config.Config.LockPosition = !_config.Config.LockPosition; _config.SaveConfig(); }
                    else if (ch == 1007) { _config.Config.StickToTaskbar = !_config.Config.StickToTaskbar; _config.SaveConfig(); }
                    else if (ch == 1008) { _config.Config.AlwaysOnTop = !_config.Config.AlwaysOnTop; _config.SaveConfig(); }
                    else if (ch == 1009) { _config.Config.HideOnFullscreen = !_config.Config.HideOnFullscreen; _config.SaveConfig(); }
                    else if (ch == 1002) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskmgr") { UseShellExecute = true });
                    else if (ch == 1003) _dispatcher.BeginInvoke(() => { App.OpenSettings(_viewModel, _config); App.SettingsWindow?.SelectSection("About"); });
                    else if (ch == 1004) _dispatcher.BeginInvoke(() => App.Quit());
                }
                return IntPtr.Zero;
            }
            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct WNDCLASSEX { public uint cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra; public int cbWndExtra; public IntPtr hInstance; public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground; public string lpszMenuName; public string lpszClassName; public IntPtr hIconSm; }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx; public int cy; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x; public int y; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp; public byte BlendFlags; public byte SourceConstantAlpha; public byte AlphaFormat; }
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(int ex, string cl, string nm, uint st, int x, int y, int w, int h, IntPtr p, IntPtr m, IntPtr i, IntPtr lp);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr ha, int x, int y, int cx, int cy, uint f);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint c);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string? n);
        [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr i, int n);
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr hd, ref POINT pd, ref SIZE ps, IntPtr hs, ref POINT pr, int c, ref BLENDFUNCTION b, int f);
        [DllImport("user32.dll")] static extern IntPtr GetWindowDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr hd);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr h);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr h);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr h, IntPtr o);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
        [StructLayout(LayoutKind.Sequential)] struct TRACKMOUSEEVENT { public uint cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }
        [DllImport("user32.dll")] static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr m, uint f, uint id, string? n);
        [DllImport("user32.dll")] static extern int TrackPopupMenuEx(IntPtr m, uint f, int x, int y, IntPtr h, IntPtr t);
        [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr m);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("uxtheme.dll", EntryPoint = "#133")] static extern bool AllowDarkModeForWindow(IntPtr h, bool a);
        [DllImport("uxtheme.dll", EntryPoint = "#135")] static extern int SetPreferredAppMode(int m);
        [DllImport("uxtheme.dll", EntryPoint = "#136")] static extern void FlushMenuThemes();
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint f);
        [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public uint cbSize; public Win32Helper.RECT rcMonitor; public Win32Helper.RECT rcWork; public uint dwFlags; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFO m);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyIcon(IntPtr h);
        [StructLayout(LayoutKind.Sequential)] struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public Win32Helper.RECT rc; public IntPtr lParam; }
        [DllImport("shell32.dll", CallingConvention = CallingConvention.StdCall)] static extern IntPtr SHAppBarMessage(uint m, ref APPBARDATA d);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr hObject);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    }
}
