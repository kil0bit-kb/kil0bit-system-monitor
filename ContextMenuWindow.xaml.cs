using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Windowing;
using WinRT.Interop;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.ViewModels;
using System;
using System.Runtime.InteropServices;

namespace Kil0bitSystemMonitor
{
    /// <summary>
    /// A transparent 1x1 WinUI 3 window used only to host a MenuFlyout as a proper dark-themed context menu.
    /// </summary>
    public sealed partial class ContextMenuWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly ConfigService _config;
        private readonly AppWindow _appWindow;
        private readonly IntPtr _hWnd;

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public ContextMenuWindow(MainViewModel viewModel, ConfigService config)
        {
            this.InitializeComponent();
            _viewModel = viewModel;
            _config = config;

            _hWnd = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd));

            // Strip all chrome: no borders, no title, hidden from switcher
            if (_appWindow.Presenter is OverlappedPresenter p)
            {
                p.IsResizable = false;
                p.SetBorderAndTitleBar(false, false);
            }
            _appWindow.IsShownInSwitchers = false;
            this.ExtendsContentIntoTitleBar = true;

            int exStyle = Win32Helper.GetWindowLong(_hWnd, Win32Helper.GWL_EXSTYLE);
            Win32Helper.SetWindowLongPtr(_hWnd, Win32Helper.GWL_EXSTYLE,
                (IntPtr)(exStyle | (int)Win32Helper.WS_EX_TOOLWINDOW | (int)Win32Helper.WS_EX_NOACTIVATE));

            // Apply DWM dark mode so flyout uses dark
            int dark = 1;
            DwmSetWindowAttribute(_hWnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

            // Force WinUI element theme to dark so the flyout renders dark
            RootGrid.RequestedTheme = ElementTheme.Dark;
        }

        /// <summary>
        /// Shows a dark WinUI 3 MenuFlyout at the specified screen coordinates then closes this window.
        /// </summary>
        public void ShowContextMenu(int screenX, int screenY)
        {
            // Position the tiny window right at cursor - acts as the anchor for the flyout
            _appWindow.Resize(new Windows.Graphics.SizeInt32(1, 1));
            _appWindow.Move(new Windows.Graphics.PointInt32(screenX, screenY - 1));

            this.Activate();

            var menu = new MenuFlyout
            {
                Placement = FlyoutPlacementMode.TopEdgeAlignedLeft
            };

            var settingsItem = new MenuFlyoutItem { Text = "Settings", Icon = new SymbolIcon(Symbol.Setting) };
            settingsItem.Click += (s, e) =>
            {
                App.OpenSettings(_viewModel, _config);
                this.Close();
            };

            var taskMgrItem = new MenuFlyoutItem { Text = "Task Manager", Icon = new FontIcon { Glyph = "\uE9F9" } };
            taskMgrItem.Click += (s, e) =>
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskmgr") { UseShellExecute = true });
                this.Close();
            };

            var aboutItem = new MenuFlyoutItem { Text = "About", Icon = new SymbolIcon(Symbol.Help) };
            aboutItem.Click += (s, e) =>
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/kalbhor/kil0bit-system-monitor") { UseShellExecute = true });
                this.Close();
            };

            var exitItem = new MenuFlyoutItem { Text = "Exit", Icon = new SymbolIcon(Symbol.Cancel) };
            exitItem.Click += (s, e) => Application.Current.Exit();

            menu.Items.Add(settingsItem);
            menu.Items.Add(taskMgrItem);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(aboutItem);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(exitItem);

            // Close this host window when the flyout dismisses without selection
            menu.Closed += (s, e) =>
            {
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => this.Close());
            };

            menu.ShowAt(RootGrid, new Windows.Foundation.Point(0, 0));
        }
    }
}
