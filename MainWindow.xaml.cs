using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using WinRT.Interop;
using System.Linq;
using System.Drawing;
using System.IO;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.ViewModels;
using Kil0bitSystemMonitor.Services;
using Microsoft.UI;

namespace Kil0bitSystemMonitor
{
    public sealed partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly TelemetryService _telemetry;
        private readonly ConfigService _config;
        private readonly AppWindow _appWindow;
        private readonly IntPtr _hWnd;
        private bool _isDragging = false;
        private Windows.Graphics.PointInt32 _lastPointerPos;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer _zOrderTimer;

        public MainWindow()
        {
            this.InitializeComponent();
            this.SystemBackdrop = null;
            
            _viewModel = new MainViewModel();
            this.RootGrid.DataContext = _viewModel;

            _config = new ConfigService();
            _viewModel.Config = _config.Config;

            _telemetry = new TelemetryService(_config);
            _telemetry.MetricsUpdated += (metrics) => 
            {
                DispatcherQueue.TryEnqueue(() => _viewModel.Metrics = metrics);
            };

            _hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd));
            
            // Set taskbar icon with multi-size support
            string iconPng = Path.Combine(AppContext.BaseDirectory, "icon.png");
            string iconIco = Path.Combine(AppContext.BaseDirectory, "icon.ico");
            Win32Helper.SetAppIcon(_hWnd, iconPng); // This now auto-prefers .ico if available
            if (File.Exists(iconIco)) try { _appWindow.SetIcon(iconIco); } catch { }
            
            // Z-Order Enforcement Timer (Windows 11 fix)
            _zOrderTimer = this.DispatcherQueue.CreateTimer();
            _zOrderTimer.Interval = TimeSpan.FromMilliseconds(500);
            _zOrderTimer.Tick += (s, e) => {
                // Keep the top-level owned window strictly on top
                Win32Helper.SetWindowPos(_hWnd, Win32Helper.HWND_TOPMOST, 0, 0, 0, 0, 0x0002 | 0x0001 | 0x0010 | 0x0040);
            };
            _zOrderTimer.Start();
            
            _config.Config.PropertyChanged += (s, e) => {
                if (e.PropertyName == nameof(AppConfig.ScaleFactor) || e.PropertyName == nameof(AppConfig.StickToTaskbar))
                {
                    DispatcherQueue.TryEnqueue(() => AnchorToTaskbar());
                }
            };

            ConfigureWindow();
            AnchorToTaskbar();
            SetupContextMenu();
        }

        private void SetupContextMenu()
        {
            var menu = new MenuFlyout();
            var settingsItem = new MenuFlyoutItem { Text = "Settings" };
            settingsItem.Click += (s, e) => OpenSettings();
            menu.Items.Add(settingsItem);

            var exitItem = new MenuFlyoutItem { Text = "Exit" };
            exitItem.Click += (s, e) => Application.Current.Exit();
            menu.Items.Add(exitItem);

            RootGrid.ContextFlyout = menu;
        }

        private void OpenSettings()
        {
            App.OpenSettings(_viewModel, _config);
        }

        private void ConfigureWindow()
        {
            // Set Presenter and strip chrome
            // Use Overlapped for basic behavior (CompactOverlay adds too much chrome)
            _appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            if (_appWindow.Presenter is OverlappedPresenter overlappedPresenter)
            {
                overlappedPresenter.IsResizable = false;
                overlappedPresenter.IsAlwaysOnTop = true;
                overlappedPresenter.SetBorderAndTitleBar(false, false);
            }
            _appWindow.IsShownInSwitchers = false;
            
            // Allow transparency to flow through
            this.ExtendsContentIntoTitleBar = true;

            // High-level Win32 styles for transparency, interaction, and topmost status
            int exStyle = Win32Helper.GetWindowLong(_hWnd, Win32Helper.GWL_EXSTYLE);
            Win32Helper.SetWindowLongPtr(_hWnd, Win32Helper.GWL_EXSTYLE, (IntPtr)(exStyle | (int)Win32Helper.WS_EX_TOOLWINDOW | (int)Win32Helper.WS_EX_NOACTIVATE | (int)Win32Helper.WS_EX_LAYERED));
            
            // Set ColorKey to #010101 (nearly black) for perfect punch-through without pink edges
            Win32Helper.SetLayeredWindowAttributes(_hWnd, 0x00010101, 0, Win32Helper.LWA_COLORKEY);

            // Set POPUP style and strip CAPTION/SYSMENU
            int style = Win32Helper.GetWindowLong(_hWnd, Win32Helper.GWL_STYLE);
            Win32Helper.SetWindowLongPtr(_hWnd, Win32Helper.GWL_STYLE, (IntPtr)((style & ~0xCF0000) | Win32Helper.WS_POPUP));

            // Windows 11 Specific: Disable rounded corners
            int cornerPreference = Win32Helper.DWMWCP_DONOTROUND;
            Win32Helper.DwmSetWindowAttribute(_hWnd, Win32Helper.DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));

            // Modern Blur/Transparency through DWM
            Win32Helper.MARGINS margins = new Win32Helper.MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            Win32Helper.DwmExtendFrameIntoClientArea(_hWnd, ref margins);

            this.SystemBackdrop = null;

            // Force a style refresh
            Win32Helper.SetWindowPos(_hWnd, IntPtr.Zero, 0, 0, 0, 0, 0x0020 | 0x0002 | 0x0001 | 0x0040); 
        }

        private void AnchorToTaskbar()
        {
            if (!_config.Config.StickToTaskbar) return;

            IntPtr taskbarHwnd = Win32Helper.FindWindow("Shell_TrayWnd", "");
            
            if (taskbarHwnd != IntPtr.Zero)
            {
                // Set the taskbar as the OWNER (GWLP_HWNDPARENT), NOT the parent.
                Win32Helper.SetWindowLongPtr(_hWnd, Win32Helper.GWL_HWNDPARENT, taskbarHwnd);

                if (Win32Helper.GetWindowRect(taskbarHwnd, out Win32Helper.RECT rect))
                {
                    // Scale dimensions based on user preference
                    int baseWidth = 180;
                    int baseHeight = 32;
                    int scaledWidth = (int)(baseWidth * _config.Config.ScaleFactor);
                    int scaledHeight = (int)(baseHeight * _config.Config.ScaleFactor);

                    // Screen coordinates because we are a top-level window
                    // Center vertically in the taskbar, and keep a fixed distance from the right
                    int x = rect.Right - (int)(400 * _config.Config.ScaleFactor); 
                    int y = rect.Top + (rect.Height - scaledHeight) / 2;

                    _appWindow.Resize(new Windows.Graphics.SizeInt32(scaledWidth, scaledHeight));
                    _appWindow.Move(new Windows.Graphics.PointInt32(x, y));
                    
                    _config.Config.X = x;
                    _config.Config.Y = y;
                }
            }
        }
        private void RootGrid_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (e.GetCurrentPoint(RootGrid).Properties.IsLeftButtonPressed)
            {
                _isDragging = true;
                RootGrid.CapturePointer(e.Pointer);
                if (Win32Helper.GetCursorPos(out Win32Helper.POINT pt))
                {
                    _lastPointerPos = new Windows.Graphics.PointInt32(pt.X, pt.Y);
                }
            }
        }

        private void RootGrid_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_isDragging)
            {
                if (Win32Helper.GetCursorPos(out Win32Helper.POINT pt))
                {
                    int deltaX = pt.X - _lastPointerPos.X;
                    int deltaY = pt.Y - _lastPointerPos.Y;
                    
                    if (deltaX == 0 && deltaY == 0) return;

                    var currentWindowPos = _appWindow.Position;
                    var newX = currentWindowPos.X + deltaX;
                    var newY = currentWindowPos.Y + deltaY;
                    
                    _appWindow.Move(new Windows.Graphics.PointInt32(newX, newY));
                    
                    _config.Config.X = newX;
                    _config.Config.Y = newY;
                    _config.SaveConfig();
                    
                    _lastPointerPos = new Windows.Graphics.PointInt32(pt.X, pt.Y);
                }
            }
        }

        private void RootGrid_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                RootGrid.ReleasePointerCapture(e.Pointer);
            }
        }

        private void RootGrid_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
        {
            var menu = RootGrid.ContextFlyout as MenuFlyout;
            if (menu != null)
            {
                menu.ShowAt(RootGrid, e.GetPosition(RootGrid));
            }
        }
    }
}
