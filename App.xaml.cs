using System;
using System.Windows;
using System.Runtime.InteropServices;

namespace Kil0bitSystemMonitor
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : System.Windows.Application
    {
        public App()
        {
            this.InitializeComponent();
            
            // Set a unique identity for the taskbar icon to bypass caching
            Kil0bitSystemMonitor.Helpers.Win32Helper.SetCurrentProcessExplicitAppUserModelID("Kil0bit.SystemMonitor.Main.v3");
        }

        private Window? m_dummyWindow;
        private OverlayWindow? m_overlay;
        private Kil0bitSystemMonitor.Services.TelemetryService? m_telemetry;
        private static System.Threading.Mutex? s_mutex;
        public static SettingsWindow? SettingsWindow { get; private set; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const uint WM_SHOW_SETTINGS = 0x0501; // Must match OverlayWindow.WM_SHOW_SETTINGS

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

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
            
            m_dummyWindow = new Window();
            m_dummyWindow.Title = "Kil0bit System Monitor Host";
            m_dummyWindow.Width = 0;
            m_dummyWindow.Height = 0;
            m_dummyWindow.WindowStyle = WindowStyle.None;
            m_dummyWindow.ShowInTaskbar = false;
            m_dummyWindow.Opacity = 0;
            
            m_dummyWindow.Show();
            m_dummyWindow.Hide();

            IntPtr dummyHWnd = new System.Windows.Interop.WindowInteropHelper(m_dummyWindow).Handle;

            m_telemetry = new Kil0bitSystemMonitor.Services.TelemetryService(config);
            var viewModel = new Kil0bitSystemMonitor.ViewModels.MainViewModel();
            viewModel.Config = config.Config;

            string iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "icon.ico");
            if (!System.IO.File.Exists(iconPath)) iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "icon.png");
            
            Kil0bitSystemMonitor.Helpers.Win32Helper.SetAppIcon(dummyHWnd, iconPath);
            
            m_overlay = new OverlayWindow(viewModel, config, m_telemetry);

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
                if (SettingsWindow.WindowState == WindowState.Minimized)
                    SettingsWindow.WindowState = WindowState.Normal;
                return;
            }

            SettingsWindow = new SettingsWindow(viewModel, config);
            SettingsWindow.Closed += (s, e) => { SettingsWindow = null; };
            SettingsWindow.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                m_overlay?.Dispose();
                m_telemetry?.Dispose();
                m_dummyWindow?.Close();
                s_mutex?.ReleaseMutex();
                s_mutex?.Dispose();
            }
            catch { }
            base.OnExit(e);
        }

        public static void Quit()
        {
            Current.Shutdown();
        }
    }
}
