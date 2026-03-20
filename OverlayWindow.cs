using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.ViewModels;

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
        private readonly System.Threading.Timer _zOrderTimer = null!;
        private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher = null!;

        private bool _isHovered = false;
        private bool _trackingMouse = false;
        private SettingsWindow? _settingsWindow;
        private uint _currentDpi = 96;
        private float _dpiScale = 1.0f;
        
        // GDI+ Cache
        private readonly System.Collections.Generic.Dictionary<string, Font> _fontCache = new();
        private readonly System.Collections.Generic.Dictionary<string, float> _measureCache = new();
        private Brush? _cachedBgBrush;
        private Brush? _cachedAccentBrush;
        private Brush? _cachedIconBrush;
        private Pen? _cachedHoverPen;
        private Brush? _cachedHoverBrush;
        private Bitmap? _offscreenBitmap;
        private Graphics? _offscreenGraphics;

        // Win32 Constants
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const uint WS_POPUP = 0x80000000;

        private const int WM_NCHITTEST = 0x0084;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_COMMAND = 0x0111;
        private const int HTCAPTION = 2;
        private const int HTCLIENT = 1;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_MOUSELEAVE = 0x02A3;

        private const int WM_WINDOWPOSCHANGING = 0x0046;
        private const int WM_EXITSIZEMOVE = 0x0232;
        private const int WM_DISPLAYCHANGE = 0x007E;
        private const int WM_DPICHANGED = 0x02E0;
        private const int WM_SETTINGCHANGE = 0x001A;
        private const uint TME_LEAVE = 0x00000002;
        public const int WM_SETICON = 0x0080;
        public const int ICON_BIG = 1;
        public const int ICON_SMALL = 0;

        public const int WM_SHOW_SETTINGS = 0x0501; // WM_APP + 1

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public uint flags;
        }

        public OverlayWindow(MainViewModel viewModel, ConfigService config, TelemetryService telemetry)
        {
            try
            {
                _viewModel = viewModel;
                _config = config;
                _telemetry = telemetry;

                // Capture the WinUI dispatcher NOW while we are on the UI thread.
                // WndProc runs on a separate Win32 thread where GetForCurrentThread() returns null.
                _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()!
                              ?? Microsoft.UI.Dispatching.DispatcherQueueController.CreateOnCurrentThread().DispatcherQueue;

                _wndProc = WndProc;

                WNDCLASSEX wc = new WNDCLASSEX();
                wc.cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX));
                wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc);
                wc.hInstance = GetModuleHandle(null);
                wc.lpszClassName = "Kil0bitOverlayWndClass_Main";
                wc.hCursor = LoadCursor(IntPtr.Zero, 32512); // IDC_ARROW

                // Use the reliable HICON method from icon.png for the Win32 class
                IntPtr hIcon = IntPtr.Zero;
                try
                {
                    string iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "icon.png");
                    if (System.IO.File.Exists(iconPath))
                    {
                        using (var bmp = new System.Drawing.Bitmap(iconPath))
                        {
                            _hIcon = bmp.GetHicon();
                        }
                    }
                }
                catch { }

                wc.hIcon = _hIcon;
                wc.hIconSm = _hIcon;

                ushort regResult = RegisterClassEx(ref wc);

                // We must NOT destroy hIcon here if we want the class to use it, 
                // but for WS_SETICON later we might need to be careful.
                // However, the class registration usually takes ownership or copies.
                // For safety in this specific app's lifecycle, we'll keep it until the window is destroyed.

                int x = (int)_config.Config.X;
                int y = (int)_config.Config.Y;
                if (x < -10000 || x > 10000 || y < -10000 || y > 10000)
                {
                    x = 100; y = 100;
                }

                _hWnd = CreateWindowEx(
                    0x00080000 | 0x00000008 | 0x00000080, // WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW
                    "Kil0bitOverlayWndClass_Main",
                    "Kil0bit System Monitor Overlay",
                    0x80000000, // WS_POPUP
                    (int)_config.Config.X, (int)_config.Config.Y, 300, 35,
                    IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

                if (_hWnd != IntPtr.Zero)
                {
                    // Reinforce icons for the specific window handle
                    if (_hIcon != IntPtr.Zero)
                    {
                        SendMessage(_hWnd, WM_SETICON, (IntPtr)ICON_BIG, _hIcon);
                        SendMessage(_hWnd, WM_SETICON, (IntPtr)ICON_SMALL, _hIcon);
                    }
                }
                else
                {
                    int err = Marshal.GetLastWin32Error();
                    // Window creation logic

                }

                // Context set

                
                // Initialize DPI
                _currentDpi = GetDpiForWindow(_hWnd);
                if (_currentDpi == 0) _currentDpi = 96;
                _dpiScale = _currentDpi / 96.0f;
                IntPtr taskbarHwnd = Win32Helper.FindWindow("Shell_TrayWnd", "");
                if (taskbarHwnd != IntPtr.Zero)
                {
                    Win32Helper.SetWindowLongPtr(_hWnd, Win32Helper.GWL_HWNDPARENT, taskbarHwnd);
                }

                ShowWindow(_hWnd, 5); // SW_SHOW
                
                // Set initial Y to taskbar center if it's reasonably close
                AlignToTaskbarCenter();
                UpdateCachedColors();
                UpdateLayer();

                _telemetry.MetricsUpdated += (m) =>
                {
                    _dispatcher.TryEnqueue(() => 
                    {
                        _viewModel.Metrics = m;
                        UpdateLayer();
                    });
                };

                // Enforce TopMost Z-order against Win11 taskbar
                _zOrderTimer = new System.Threading.Timer(EnforceZOrder, null, 0, 500);

                _config.Config.PropertyChanged += (s, e) =>
                {
                    _dispatcher.TryEnqueue(() => {
                        if (e.PropertyName == nameof(_config.Config.AccentColorHex) || 
                            e.PropertyName == nameof(_config.Config.IconColorHex) || 
                            e.PropertyName == nameof(_config.Config.BackgroundColorHex) ||
                            e.PropertyName == nameof(_config.Config.FontFamily))
                        {
                            ClearCaches();
                            UpdateCachedColors();
                        }
                        
                        UpdateLayer();
                        if (e.PropertyName == nameof(_config.Config.ShowOverlay) || 
                            e.PropertyName == nameof(_config.Config.HideOnFullscreen) || 
                            e.PropertyName == nameof(_config.Config.StickToTaskbar) || 
                            e.PropertyName == nameof(_config.Config.ShowBackground))
                        {
                            UpdateLayer();
                            UpdateVisibility();
                        }
                    });
                };

                // Initial visibility
                if (!_config.Config.ShowOverlay) ShowWindow(_hWnd, 0);
            }
            catch (Exception)
            {
                // Silently fail for production
            }
        }

        private void EnforceZOrder(object? state)
        {
            _dispatcher.TryEnqueue(() => 
            {
                // UpdateVisibility already does the checks and applies ShowWindow
                UpdateVisibility();
                
                // Only enforce TopMost if we should be visible
                bool isFullscreen = _config.Config.HideOnFullscreen && IsForegroundWindowFullScreen();
                bool isTaskbarVisible = !_config.Config.StickToTaskbar || IsTaskbarVisible();
                if (_config.Config.ShowOverlay && !isFullscreen && isTaskbarVisible)
                {
                    SetWindowPos(_hWnd, (IntPtr)(-1), 0, 0, 0, 0, 0x0002 | 0x0001 | 0x0010 | 0x0040); // HWND_TOPMOST, SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE|SWP_SHOWWINDOW
                }
            });
        }

        private void UpdateVisibility()
        {
            bool isFullscreen = _config.Config.HideOnFullscreen && IsForegroundWindowFullScreen();
            bool isTaskbarVisible = !_config.Config.StickToTaskbar || IsTaskbarVisible();
            bool shouldBeVisible = _config.Config.ShowOverlay && !isFullscreen && isTaskbarVisible;
            
            ShowWindow(_hWnd, shouldBeVisible ? 5 : 0);
        }

        private bool IsForegroundWindowFullScreen()
        {
            IntPtr hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero || hWnd == _hWnd) return false;

            // Don't hide if taskbar or desktop is focused
            IntPtr shellWnd = Win32Helper.FindWindow("Shell_TrayWnd", null!);
            if (hWnd == shellWnd) return false;
            
            IntPtr desktopWnd = Win32Helper.GetDesktopWindow();
            if (hWnd == desktopWnd) return false;

            if (Win32Helper.GetWindowRect(hWnd, out Win32Helper.RECT rect))
            {
                IntPtr hMonitor = MonitorFromWindow(hWnd, 2); // MONITOR_DEFAULTTONEAREST
                MONITORINFO mi = new MONITORINFO();
                mi.cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFO));
                if (GetMonitorInfo(hMonitor, ref mi))
                {
                    // Check if foreground window matches monitor bounds
                    return rect.Left == mi.rcMonitor.Left &&
                           rect.Top == mi.rcMonitor.Top &&
                           rect.Right == mi.rcMonitor.Right &&
                           rect.Bottom == mi.rcMonitor.Bottom;
                }
            }
            return false;
        }

        private bool IsTaskbarVisible()
        {
            IntPtr taskbarHwnd = Win32Helper.FindWindow("Shell_TrayWnd", null!);
            if (taskbarHwnd == IntPtr.Zero) return false;

            IntPtr hMonitor = MonitorFromWindow(taskbarHwnd, 2); // MONITOR_DEFAULTTONEAREST
            MONITORINFO mi = new MONITORINFO();
            mi.cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFO));
            if (GetMonitorInfo(hMonitor, ref mi))
            {
                // If rcWork matches rcMonitor, the taskbar is not occupying any space (hidden/auto-hide)
                bool isHidden = mi.rcWork.Left == mi.rcMonitor.Left &&
                               mi.rcWork.Top == mi.rcMonitor.Top &&
                               mi.rcWork.Right == mi.rcMonitor.Right &&
                               mi.rcWork.Bottom == mi.rcMonitor.Bottom;
                return !isHidden;
            }
            return true;
        }

        private void AlignToTaskbarCenter()
        {
            if (!_config.Config.StickToTaskbar) 
            {
                // In free-floating mode, just ensure current position is applied
                SetWindowPos(_hWnd, IntPtr.Zero, (int)_config.Config.X, (int)_config.Config.Y, 0, 0, 0x0001 | 0x0004 | 0x0010); // SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE
                return;
            }

            IntPtr taskbarHwnd = Win32Helper.FindWindow("Shell_TrayWnd", null!);
            if (taskbarHwnd != IntPtr.Zero && Win32Helper.GetWindowRect(taskbarHwnd, out Win32Helper.RECT tbRect))
            {
                int tbHeight = tbRect.Bottom - tbRect.Top;
                int overlayHeight = 35; // Standard height
                int centerY = tbRect.Top + (tbHeight - overlayHeight) / 2;
                
                SetWindowPos(_hWnd, IntPtr.Zero, (int)_config.Config.X, centerY, 0, 0, 0x0001 | 0x0004 | 0x0010); // SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE
                _config.Config.Y = centerY;
                _config.SaveConfig();
            }
        }

        private void UpdateLayer()
        {
            bool isIcon = _config.Config.DisplayStyle == "Icon";
            bool isCompact = _config.Config.DisplayStyle == "Compact";
            
            var allRows = new System.Collections.Generic.List<string>();

            // Network
            if (_config.Config.ShowNetUp) {
                string prefix = isCompact ? "U: " : (isIcon ? "\uE898 " : "UP: ");
                allRows.Add(prefix + _viewModel.Metrics.NetUpText);
            }
            if (_config.Config.ShowNetDown) {
                string prefix = isCompact ? "D: " : (isIcon ? "\uE896 " : "DN: ");
                allRows.Add(prefix + _viewModel.Metrics.NetDownText);
            }

            // CPU
            if (_config.Config.ShowCpu) {
                string prefix = isCompact ? "C: " : (isIcon ? "\uE950 " : "CPU: ");
                allRows.Add(prefix + _viewModel.CpuText);
            }

            // RAM
            if (_config.Config.ShowRam) {
                string prefix = isCompact ? "R: " : (isIcon ? "\uE8AE " : "RAM: ");
                allRows.Add(prefix + _viewModel.RamPercentText);
            }

            // GPU
            if (_config.Config.ShowGpu) {
                string prefix = isCompact ? "G: " : (isIcon ? "\uE7F1 " : "GPU: ");
                allRows.Add(prefix + _viewModel.GpuText);
            }

            // Temp
            if (_config.Config.ShowTemp) {
                string prefix = isCompact ? "T: " : (isIcon ? "\uE9CA " : "TEM: ");
                allRows.Add(prefix + _viewModel.GpuTempText);
            }

            // Disk Space & Usage
            if (_config.Config.ShowDisk)
            {
                string tPrefix = isCompact ? "S: " : (isIcon ? "\uE7F0 " : "DSK: ");
                allRows.Add(tPrefix + _viewModel.Metrics.DiskSpaceText);
                string bPrefix = isCompact ? "B: " : (isIcon ? "\uE9D9 " : "BSY: ");
                allRows.Add(bPrefix + $"{_viewModel.Metrics.DiskUsage:F0}%");
            }

            var columns = new System.Collections.Generic.List<(string Top, string Bottom)>();
            for (int i = 0; i < allRows.Count; i += 2)
            {
                string t = allRows[i];
                string b = (i + 1 < allRows.Count) ? allRows[i + 1] : "";
                columns.Add((t, b));
            }

            // Ensure we have a graphics object for measurement
            if (_offscreenGraphics == null)
            {
                _offscreenBitmap ??= new Bitmap(1, 1);
                _offscreenGraphics = Graphics.FromImage(_offscreenBitmap);
            }

            string fontName = _config.Config.FontFamily;
            if (string.IsNullOrEmpty(fontName) || fontName == "Default") fontName = SystemFonts.CaptionFont?.Name ?? "Segoe UI";
            
            // Apply DPI scaling AND user scale factor to font sizes
            float effectiveScale = _dpiScale * (float)_config.Config.ScaleFactor;
            float baseFontSize = 8.5f * effectiveScale;
            float iconFontSize = 7.5f * effectiveScale;

            FontStyle textStyle = _config.Config.IsTextBold ? FontStyle.Bold : FontStyle.Regular;
            FontStyle iconStyle = _config.Config.IsIconBold ? FontStyle.Bold : FontStyle.Regular;

            Font textFont = GetCachedFont(fontName, baseFontSize, textStyle);
            Font iconFontMeasure = GetCachedFont("Segoe MDL2 Assets", iconFontSize, iconStyle);

            // Calculate individual widths for each column based on content type
            float[] colWidths = new float[columns.Count];
            float internalPadding = 2 * effectiveScale; 
            float columnGap = 5 * effectiveScale;      

            for (int i = 0; i < columns.Count; i++)
            {
                var col = columns[i];
                float labelWidth;
                if (isIcon)
                {
                    labelWidth = GetCachedMeasure("\uE950", iconFontMeasure);
                }
                else
                {
                    string sampleLabel = "CPU: ";
                    string primary = !string.IsNullOrEmpty(col.Top) ? col.Top : col.Bottom;
                    int colonIdx = primary.IndexOf(':');
                    if (colonIdx > 0) sampleLabel = primary.Substring(0, colonIdx + 1);

                    labelWidth = GetCachedMeasure(sampleLabel.TrimEnd(), textFont);
                }

                string maxValStr = "100%";
                string combined = (col.Top + col.Bottom);
                if (combined.Contains("/s") || combined.Contains("bps")) maxValStr = "99.9 Mbps";
                else if (combined.Contains("°C") || combined.Contains("TEM:") || combined.Contains("BSY:")) maxValStr = "100°C";
                else if (combined.Contains("%")) maxValStr = "100%";
                
                float valueWidth = GetCachedMeasure(maxValStr, textFont);
                colWidths[i] = labelWidth + internalPadding + valueWidth + columnGap;
            }

            int height = (int)(32 * effectiveScale); // Reduced base height slightly for tighter fit
            float totalWidth = 5 * effectiveScale;
            foreach (var cw in colWidths) totalWidth += cw;
            int width = (int)Math.Max(20, totalWidth);

            // Re-create offscreen buffer if size changed
            if (_offscreenBitmap == null || _offscreenBitmap.Width != width || _offscreenBitmap.Height != height)
            {
                _offscreenGraphics?.Dispose();
                _offscreenBitmap?.Dispose();
                _offscreenBitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                _offscreenGraphics = Graphics.FromImage(_offscreenBitmap);
                _offscreenGraphics.SmoothingMode = SmoothingMode.AntiAlias;
                _offscreenGraphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            }

            _offscreenGraphics.Clear(Color.FromArgb(1, 0, 0, 0));

            if (_config.Config.ShowBackground && _cachedBgBrush != null)
            {
                int r = (int)(12 * effectiveScale); // Synced with hover effect
                using (GraphicsPath bgPath = new GraphicsPath())
                {
                    bgPath.AddArc(0, 0, r, r, 180, 90);
                    bgPath.AddArc(width - r, 0, r, r, 270, 90);
                    bgPath.AddArc(width - r, height - r, r, r, 0, 90);
                    bgPath.AddArc(0, height - r, r, r, 90, 90);
                    bgPath.CloseFigure();
                    _offscreenGraphics.FillPath(_cachedBgBrush, bgPath);
                }
            }

            if (_isHovered && _cachedHoverBrush != null && _cachedHoverPen != null)
            {
                using (GraphicsPath path = new GraphicsPath())
                {
                    int d = (int)(12 * _dpiScale);
                    Rectangle r = new Rectangle(0, 0, width - 1, height - 1);
                    path.AddArc(r.X, r.Y, d, d, 180, 90);
                    path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                    path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                    path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                    path.CloseFigure();
                    _offscreenGraphics.FillPath(_cachedHoverBrush, path);
                    _offscreenGraphics.DrawPath(_cachedHoverPen, path);
                }
            }

            Brush valueBrush = _cachedAccentBrush ?? Brushes.White;
            Brush iconBrush = _cachedIconBrush ?? Brushes.Aqua;
            Font iconFont = GetCachedFont("Segoe MDL2 Assets", iconFontSize, iconStyle);

            float yTop = 0 * effectiveScale; 
            float yBot = 15 * effectiveScale; 
            float currentX = 5 * effectiveScale;
            
            for (int i = 0; i < columns.Count; i++)
            {
                var col = columns[i];

                if (!string.IsNullOrEmpty(col.Top) && !string.IsNullOrEmpty(col.Bottom))
                {
                    RenderMetric(_offscreenGraphics, col.Top, currentX, yTop, isIcon, iconFont, textFont, iconBrush, valueBrush);
                    RenderMetric(_offscreenGraphics, col.Bottom, currentX, yBot, isIcon, iconFont, textFont, iconBrush, valueBrush);
                }
                else if (!string.IsNullOrEmpty(col.Top))
                {
                    // Center vertically if only one item in column
                    float yCenter = 8.5f * _dpiScale; 
                    RenderMetric(_offscreenGraphics, col.Top, currentX, yCenter, isIcon, iconFont, textFont, iconBrush, valueBrush);
                }
                
                currentX += colWidths[i];
            }

            SetBitmap(_offscreenBitmap);
        }

        private void RenderMetric(Graphics g, string raw, float x, float y, bool isIcon, Font iconFont, Font textFont, Brush iconBrush, Brush valueBrush)
        {
            if (isIcon && raw.Length > 1)
            {
                string icon = raw.Substring(0, 1);
                string val = raw.Substring(1).Trim();
                
                float iconWidth = GetCachedMeasure(icon, iconFont);
                g.DrawString(icon, iconFont, iconBrush, new PointF(x, y + (2.5f * _dpiScale)));
                
                g.DrawString(val, textFont, valueBrush, new PointF(x + iconWidth + (2 * _dpiScale), y));
            }
            else
            {
                int colonIdx = raw.IndexOf(':');
                if (colonIdx > 0)
                {
                    string label = raw.Substring(0, colonIdx + 1);
                    string val = raw.Substring(colonIdx + 1).Trim();
                    
                    float labelWidth = GetCachedMeasure(label.TrimEnd(), textFont);
                    g.DrawString(label, textFont, iconBrush, new PointF(x, y));
                    g.DrawString(val, textFont, valueBrush, new PointF(x + labelWidth + (2 * _dpiScale), y));
                }
                else
                {
                    g.DrawString(raw, textFont, valueBrush, new PointF(x, y));
                }
            }
        }

        private Color HexToColor(string hex)
        {
            try
            {
                hex = hex.Replace("#", "");
                if (hex.Length == 8)
                {
                    int a = int.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
                    int r = int.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
                    int g = int.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
                    int b = int.Parse(hex.Substring(6, 2), System.Globalization.NumberStyles.HexNumber);
                    return Color.FromArgb(a, r, g, b);
                }
                if (hex.Length == 6)
                {
                    int r = int.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
                    int g = int.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
                    int b = int.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
                    return Color.FromArgb(255, r, g, b);
                }
            }
            catch { }
            return Color.White;
        }

        private Font GetCachedFont(string family, float size, FontStyle style)
        {
            string key = $"{family}_{size}_{style}";
            if (!_fontCache.TryGetValue(key, out var font))
            {
                font = new Font(family, size, style);
                _fontCache[key] = font;
            }
            return font;
        }

        private void UpdateCachedColors()
        {
            _cachedBgBrush?.Dispose();
            _cachedAccentBrush?.Dispose();
            _cachedIconBrush?.Dispose();
            _cachedHoverPen?.Dispose();
            _cachedHoverBrush?.Dispose();

            _cachedBgBrush = new SolidBrush(HexToColor(_config.Config.BackgroundColorHex ?? "#B4141414"));
            _cachedAccentBrush = new SolidBrush(HexToColor(_config.Config.AccentColorHex ?? "#FFFFFF"));
            _cachedIconBrush = new SolidBrush(HexToColor(_config.Config.IconColorHex ?? "#00CCFF"));
            _cachedHoverPen = new Pen(Color.FromArgb(20, 255, 255, 255));
            _cachedHoverBrush = new SolidBrush(Color.FromArgb(25, 255, 255, 255));
        }

        private DateTime _lastCacheClear = DateTime.Now;

        private float GetCachedMeasure(string text, Font font)
        {
            if (_offscreenGraphics == null) return 0;

            // Prevent indefinite growth of measurement cache by clearing it periodically
            if ((DateTime.Now - _lastCacheClear).TotalMinutes > 5)
            {
                _measureCache.Clear();
                _lastCacheClear = DateTime.Now;
            }

            string key = $"{text}_{font.Name}_{font.Size}_{font.Style}";
            if (!_measureCache.TryGetValue(key, out var width))
            {
                width = _offscreenGraphics.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width;
                _measureCache[key] = width;
            }
            return width;
        }

        private void ClearCaches()
        {
            foreach (var font in _fontCache.Values) font.Dispose();
            _fontCache.Clear();
            _measureCache.Clear();
        }

        public void ShowSettings()
        {
            _dispatcher.TryEnqueue(() => 
            {
                if (_settingsWindow == null)
                {
                    _settingsWindow = new SettingsWindow(_viewModel, _config);
                    _settingsWindow.Closed += (s, e) => _settingsWindow = null;
                }
                _settingsWindow.Activate();
                Win32Helper.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(_settingsWindow));
            });
        }

        public void Dispose()
        {
            try
            {
                ClearCaches();
                _offscreenGraphics?.Dispose();
                _offscreenBitmap?.Dispose();
                _cachedBgBrush?.Dispose();
                _cachedAccentBrush?.Dispose();
                _cachedIconBrush?.Dispose();
                _cachedHoverPen?.Dispose();
                _cachedHoverBrush?.Dispose();
                
                if (_hWnd != IntPtr.Zero)
                {
                    DestroyWindow(_hWnd);
                    _hWnd = IntPtr.Zero;
                }

                if (_hIcon != IntPtr.Zero)
                {
                    DestroyIcon(_hIcon);
                    _hIcon = IntPtr.Zero;
                }
            }
            catch { }
        }

        private void SetBitmap(Bitmap bitmap)
        {
            IntPtr windowDC = GetWindowDC(_hWnd);
            IntPtr memDC = CreateCompatibleDC(windowDC);
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;

            try
            {
                hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
                oldBitmap = SelectObject(memDC, hBitmap);

                SIZE size = new SIZE { cx = bitmap.Width, cy = bitmap.Height };
                POINT pointSource = new POINT { x = 0, y = 0 };
                POINT topPos = new POINT { x = (int)_config.Config.X, y = (int)_config.Config.Y };
                
                BLENDFUNCTION blend = new BLENDFUNCTION();
                blend.BlendOp = 0; // AC_SRC_OVER
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = 1; // AC_SRC_ALPHA

                bool result = UpdateLayeredWindow(_hWnd, windowDC, ref topPos, ref size, memDC, ref pointSource, 0, ref blend, 2); // ULW_ALPHA
                if (!result)
                {
                    int err = Marshal.GetLastWin32Error();
                    // Layer update complete

                }
            }
            catch (Exception)
            {
                // Silently fail

            }
            finally
            {
                if (hBitmap != IntPtr.Zero)
                {
                    SelectObject(memDC, oldBitmap);
                    DeleteObject(hBitmap);
                }
                DeleteDC(memDC);
                ReleaseDC(_hWnd, windowDC);
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == 0x0084) // WM_NCHITTEST
            {
                return (IntPtr)1; // HTCLIENT
            }
            if (msg == WM_WINDOWPOSCHANGING && _config.Config.StickToTaskbar)
            {
                // Force vertical center to taskbar only when set
                WINDOWPOS pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                IntPtr taskbarHwnd = Win32Helper.FindWindow("Shell_TrayWnd", "");
                if (taskbarHwnd != IntPtr.Zero && Win32Helper.GetWindowRect(taskbarHwnd, out Win32Helper.RECT tbRect))
                {
                    int tbHeight = tbRect.Bottom - tbRect.Top;
                    int overlayHeight = 35; 
                    int centerY = tbRect.Top + (tbHeight - overlayHeight) / 2;
                    pos.y = centerY;
                    Marshal.StructureToPtr(pos, lParam, false);
                }
            }
            if (msg == WM_EXITSIZEMOVE)
            {
                // Final resting place after user drag
                if (Win32Helper.GetWindowRect(hWnd, out Win32Helper.RECT rect))
                {
                    _config.Config.X = rect.Left;
                    _config.Config.Y = rect.Top;
                    _config.SaveConfig();
                }
            }
            if (msg == WM_SHOW_SETTINGS)
            {
                ShowSettings();
                return IntPtr.Zero;
            }
            if (msg == WM_DPICHANGED)
            {
                _currentDpi = (uint)(wParam.ToInt32() & 0xFFFF);
                _dpiScale = _currentDpi / 96.0f;
                ClearCaches();
                AlignToTaskbarCenter();
                UpdateLayer();
                return IntPtr.Zero;
            }
            if (msg == WM_DISPLAYCHANGE || msg == WM_SETTINGCHANGE)
            {
                AlignToTaskbarCenter();
                UpdateLayer();
                return IntPtr.Zero;
            }
            if (msg == WM_MOUSEMOVE)
            {
                if (!_trackingMouse)
                {
                    TRACKMOUSEEVENT tme = new TRACKMOUSEEVENT();
                    tme.cbSize = (uint)Marshal.SizeOf(typeof(TRACKMOUSEEVENT));
                    tme.dwFlags = TME_LEAVE;
                    tme.hwndTrack = hWnd;
                    tme.dwHoverTime = 0;
                    TrackMouseEvent(ref tme);
                    _trackingMouse = true;
                    _isHovered = true;
                    UpdateLayer();
                }
            }
            if (msg == WM_MOUSELEAVE)
            {
                _trackingMouse = false;
                _isHovered = false;
                UpdateLayer();
            }
            if (msg == WM_LBUTTONDOWN)
            {
                if (_config.Config.LockPosition) return IntPtr.Zero;

                ReleaseCapture();
                SendMessage(hWnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                return IntPtr.Zero;
            }
            if (msg == WM_RBUTTONUP)
            {
                if (Win32Helper.GetCursorPos(out Win32Helper.POINT pt))
                {
                    // Enable dark mode for Win32 menus via undocumented uxtheme ordinals
                    SetPreferredAppMode(2); // PreferredAppMode::ForceDark
                    AllowDarkModeForWindow(hWnd, true);
                    FlushMenuThemes();                    IntPtr hMenu = CreatePopupMenu();
                    AppendMenu(hMenu, 0x0000, 1001, "Settings");
                    AppendMenu(hMenu, 0x0000, 1002, "Task Manager");
                    AppendMenu(hMenu, 0x0800, 0, null); // Separator
                    
                    uint lockFlags = _config.Config.LockPosition ? 0x0008U : 0x0000U;
                    uint snapFlags = _config.Config.StickToTaskbar ? 0x0008U : 0x0000U;
                    AppendMenu(hMenu, lockFlags | 0x0000U, 1006, "Lock Position");
                    AppendMenu(hMenu, snapFlags | 0x0000U, 1007, "Snap to Taskbar");
                    
                    AppendMenu(hMenu, 0x0800, 0, null); // Separator
                    AppendMenu(hMenu, 0x0000, 1003, "About");
                    AppendMenu(hMenu, 0x0800, 0, null); // Separator
                    AppendMenu(hMenu, 0x0000, 1004, "Exit");

                    SetForegroundWindow(hWnd);

                    // Anchor menu 4px above the taskbar top edge
                    int menuY = pt.Y;
                    IntPtr taskbarHwnd = Win32Helper.FindWindow("Shell_TrayWnd", "");
                    if (taskbarHwnd != IntPtr.Zero && Win32Helper.GetWindowRect(taskbarHwnd, out Win32Helper.RECT tbRect))
                    {
                        menuY = tbRect.Top - 4;
                    }

                    int chosen = TrackPopupMenuEx(hMenu, 0x0020 | 0x0002 | 0x0100, pt.X, menuY, hWnd, IntPtr.Zero);
                    DestroyMenu(hMenu);

                    if (chosen == 1001) // Settings
                    {
                        _dispatcher.TryEnqueue(() =>
                        {
                            if (_settingsWindow == null)
                            {
                                _settingsWindow = new SettingsWindow(_viewModel, _config);
                                _settingsWindow.Closed += (s, e) => _settingsWindow = null;
                                _settingsWindow.Activate();
                            }
                            else
                            {
                                _settingsWindow.Activate();
                                IntPtr handle = WinRT.Interop.WindowNative.GetWindowHandle(_settingsWindow);
                                Win32Helper.ShowWindow(handle, Win32Helper.SW_RESTORE);
                                Win32Helper.SetForegroundWindow(handle);
                            }
                        });
                    }
                    else if (chosen == 1006) // Toggle Lock
                    {
                        _config.Config.LockPosition = !_config.Config.LockPosition;
                        _config.SaveConfig();
                    }
                    else if (chosen == 1007) // Toggle Snap
                    {
                        _config.Config.StickToTaskbar = !_config.Config.StickToTaskbar;
                        _config.SaveConfig();
                    }
                    else if (chosen == 1002) // Task Manager
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskmgr") { UseShellExecute = true });
                    }
                    else if (chosen == 1003) // About
                    {
                        _dispatcher.TryEnqueue(() =>
                        {
                            if (_settingsWindow == null)
                            {
                                _settingsWindow = new SettingsWindow(_viewModel, _config);
                                _settingsWindow.Closed += (s, e) => _settingsWindow = null;
                                _settingsWindow.Activate();
                            }
                            else
                            {
                                _settingsWindow.Activate();
                                IntPtr handle = WinRT.Interop.WindowNative.GetWindowHandle(_settingsWindow);
                                Win32Helper.ShowWindow(handle, Win32Helper.SW_RESTORE);
                                Win32Helper.SetForegroundWindow(handle);
                            }
                            _settingsWindow.SelectSection("About");
                        });
                    }
                    else if (chosen == 1004) // Exit
                    {
                        _dispatcher.TryEnqueue(() => Microsoft.UI.Xaml.Application.Current.Exit());
                    }
                }
                return IntPtr.Zero;
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }



        // --- P/Invokes ---
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SIZE
        {
            public int cx;
            public int cy;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern ushort RegisterClassEx(ref WNDCLASSEX pcWndClassEx);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll")]
        static extern IntPtr GetWindowDC(IntPtr hWnd);



        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll")]
        static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll")]
        static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool DestroyWindow(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        struct TRACKMOUSEEVENT
        {
            public uint cbSize;
            public uint dwFlags;
            public IntPtr hwndTrack;
            public uint dwHoverTime;
        }

        [DllImport("user32.dll")]
        static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

        // Win32 menu P/Invokes
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string? lpNewItem);

        [DllImport("user32.dll")]
        static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

        [DllImport("user32.dll")]
        static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        // Dark mode uxtheme ordinals (Windows 1903+)
        [DllImport("uxtheme.dll", EntryPoint = "#133")]
        static extern bool AllowDarkModeForWindow(IntPtr hWnd, bool allow);

        [DllImport("uxtheme.dll", EntryPoint = "#135")]
        static extern int SetPreferredAppMode(int mode);

        [DllImport("uxtheme.dll", EntryPoint = "#136")]
        static extern void FlushMenuThemes();

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public uint cbSize;
            public Win32Helper.RECT rcMonitor;
            public Win32Helper.RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("shcore.dll")]
        static extern int GetProcessDpiAwareness(IntPtr hprocess, out int awareness);
    }
}
