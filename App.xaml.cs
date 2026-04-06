using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Navigation;
using System;

namespace Kil0bitSystemMonitor
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();
            
            // Set a unique identity for the taskbar icon to bypass caching
            Kil0bitSystemMonitor.Helpers.Win32Helper.SetCurrentProcessExplicitAppUserModelID("Kil0bit.SystemMonitor.Main.v1");
        }

        /// <summary>
        /// Invoked when the application is launched normally by the end user.  Other entry points
        /// will be used such as when the application is launched to open a specific file.
        /// </summary>
        /// <param name="e">Details about the launch request and process.</param>
        private Window? m_dummyWindow;
        private OverlayWindow? m_overlay;
        private Kil0bitSystemMonitor.Services.TelemetryService? m_telemetry;
        private static System.Threading.Mutex? s_mutex;
        public static SettingsWindow? SettingsWindow { get; private set; }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const uint WM_SHOW_SETTINGS = 0x8001; // WM_APP + 1

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            // Robust single-instance check using Mutex
            bool createdNew;
            s_mutex = new System.Threading.Mutex(true, "Local\\Kil0bitSystemMonitor_SingleInstance_Mutex", out createdNew);
            
            if (!createdNew)
            {
                // Try to find the existing window to show settings before exiting
                IntPtr existingWnd = FindWindow("Kil0bitOverlayWndClass_Main", null);
                if (existingWnd != IntPtr.Zero)
                {
                    SendMessage(existingWnd, WM_SHOW_SETTINGS, IntPtr.Zero, IntPtr.Zero);
                }
                s_mutex.Dispose();
                System.Environment.Exit(0);
                return;
            }

            if (m_overlay != null) return;
            
            var config = new Kil0bitSystemMonitor.Services.ConfigService();
            


            // Keep a silent WinUI 3 window to sustain the WinAppSDK message pump
            m_dummyWindow = new Window();
            m_dummyWindow.Closed += (s, args) => App_Exit(s!, null!);

            IntPtr dummyHWnd = WinRT.Interop.WindowNative.GetWindowHandle(m_dummyWindow);

            // Immediately hide — without this, WinUI 3 materialises the HWND and shows
            // a blank black rectangle on screen (the portable black window bug).
            Kil0bitSystemMonitor.Helpers.Win32Helper.ShowWindow(dummyHWnd, 0); // SW_HIDE
            int exStyle = Kil0bitSystemMonitor.Helpers.Win32Helper.GetWindowLong(dummyHWnd, Kil0bitSystemMonitor.Helpers.Win32Helper.GWL_EXSTYLE);
            Kil0bitSystemMonitor.Helpers.Win32Helper.SetWindowLongPtr(dummyHWnd, Kil0bitSystemMonitor.Helpers.Win32Helper.GWL_EXSTYLE,
                (IntPtr)(exStyle | (int)Kil0bitSystemMonitor.Helpers.Win32Helper.WS_EX_TOOLWINDOW)); // Remove from Alt+Tab & taskbar

            string iconPng = System.IO.Path.Combine(AppContext.BaseDirectory, "icon.png");
            Kil0bitSystemMonitor.Helpers.Win32Helper.SetAppIcon(dummyHWnd, iconPng);

            m_telemetry = new Kil0bitSystemMonitor.Services.TelemetryService(config);
            var viewModel = new Kil0bitSystemMonitor.ViewModels.MainViewModel();
            viewModel.Config = config.Config;
            
            // Launch the pure Win32 GDI+ Overlay bypassing WinUI 3 composition
            m_overlay = new OverlayWindow(viewModel, config, m_telemetry);

            // SMART LAUNCH: Open settings if not a background startup
            string[] args = System.Environment.GetCommandLineArgs();
            bool isStartup = System.Linq.Enumerable.Contains(args, "--startup");
            if (!isStartup)
            {
                OpenSettings(viewModel, config);
            }
        }

        public static void OpenSettings(Kil0bitSystemMonitor.ViewModels.MainViewModel viewModel, Kil0bitSystemMonitor.Services.ConfigService config)
        {
            if (SettingsWindow != null)
            {
                SettingsWindow.Activate();
                return;
            }

            SettingsWindow = new SettingsWindow(viewModel, config);
            SettingsWindow.Closed += (s, e) => { SettingsWindow = null; };
            SettingsWindow.Activate();
        }

        private void App_Exit(object sender, object e)
        {
            try
            {
                m_overlay?.Dispose();
                m_telemetry?.Dispose();
                s_mutex?.ReleaseMutex();
                s_mutex?.Dispose();
            }
            catch { }
            finally
            {
                // Tell the WinUI 3 message loop to stop — without this the process
                // stays alive after the window is closed (the portable exit bug).
                Application.Current.Exit();
            }
        }

        /// <summary>
        /// Invoked when Navigation to a certain page fails
        /// </summary>
        /// <param name="sender">The Frame which failed navigation</param>
        /// <param name="e">Details about the navigation failure</param>
        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }
    }
}
