using Microsoft.UI.Xaml;
using Kil0bitSystemMonitor.ViewModels;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Helpers;
using Microsoft.UI.Xaml.Controls;
using System;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using System.Linq;
using System.Drawing;
using System.IO;

namespace Kil0bitSystemMonitor
{
    public sealed partial class SettingsWindow : Window
    {
        private MainViewModel _viewModel;
        private ConfigService _config;

        public SettingsWindow(MainViewModel viewModel, ConfigService config)
        {
            _viewModel = viewModel;
            _config = config;
            try 
            {
                this.InitializeComponent();
                this.ExtendsContentIntoTitleBar = true;
                this.SetTitleBar(AppTitleBar);

                if (this.Content is FrameworkElement fe)
                {
                    fe.DataContext = _viewModel;
                }

                // Set taskbar icon
                string iconPng = Path.Combine(AppContext.BaseDirectory, "icon.png");
                string iconIco = Path.Combine(AppContext.BaseDirectory, "icon.ico");
                
                IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                Win32Helper.SetAppIcon(hWnd, iconPng);
                if (File.Exists(iconIco)) try { this.AppWindow.SetIcon(iconIco); } catch { }

                PopulateGpuList();
                PopulateDiskList();
                PopulateNetworkList();
                EnsureValidSelections();
                this.Activated += SettingsWindow_Activated;
                this.Closed += SettingsWindow_Closed;
            }
            catch (Exception)
            {
                // Silently fail for production
            }
        }

        private void PopulateNetworkList()
        {
            try
            {
                var adapters = TelemetryService.GetAvailableNetworkAdapters();
                foreach (var adapter in adapters)
                {
                    NetAdapterCombo.Items.Add(new ComboBoxItem { Content = adapter });
                }
            }
            catch { }
        }

        private void SetFixedSize(int width, int height)
        {
            var appWindow = this.AppWindow;
            if (appWindow != null)
            {
                appWindow.Resize(new SizeInt32 { Width = width, Height = height });
                var presenter = appWindow.Presenter as OverlappedPresenter;
                if (presenter != null)
                {
                    presenter.IsResizable = false;
                    presenter.IsMaximizable = false;
                }
            }
        }

        private void PopulateGpuList()
        {
            try
            {
                var gpus = TelemetryService.GetAvailableGpus();
                foreach (var gpu in gpus)
                {
                    GpuAdapterCombo.Items.Add(new ComboBoxItem { Content = gpu });
                }
            }
            catch { }
        }

        private void EnsureValidSelections()
        {
            try
            {
                // Force migration of old 'All' values if they no longer exist in UI
                if (_config.Config.NetworkAdapter == "All") _config.Config.NetworkAdapter = "Default";
                if (_config.Config.GpuAdapter == "All") _config.Config.GpuAdapter = "Default";
                if (_config.Config.DiskDrive == "Default") _config.Config.DiskDrive = "All";

                if (NetAdapterCombo.SelectedIndex == -1 && NetAdapterCombo.Items.Count > 0) NetAdapterCombo.SelectedIndex = 0;
                if (GpuAdapterCombo.SelectedIndex == -1 && GpuAdapterCombo.Items.Count > 0) GpuAdapterCombo.SelectedIndex = 0;
                if (DiskDriveCombo.SelectedIndex == -1 && DiskDriveCombo.Items.Count > 0) DiskDriveCombo.SelectedIndex = 0;
            }
            catch { }
        }

        private void PopulateDiskList()
        {
            try
            {
                var disks = TelemetryService.GetAvailableDisks();
                foreach (var disk in disks)
                {
                    DiskDriveCombo.Items.Add(new ComboBoxItem { Content = disk });
                }
            }
            catch { }
        }

        private void SettingsRoot_Loaded(object sender, RoutedEventArgs e)
        {
        }

        private void SettingsWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            this.Activated -= SettingsWindow_Activated;
            try 
            {
                var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
                var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
                
                if (appWindow != null)
                {
                    appWindow.Resize(new Windows.Graphics.SizeInt32(900, 600));

                    // Re-force icon for taskbar robustness
                    string iconPng = Path.Combine(AppContext.BaseDirectory, "icon.png");
                    string iconIco = Path.Combine(AppContext.BaseDirectory, "icon.ico");
                    Win32Helper.SetAppIcon(hWnd, iconPng);
                    if (File.Exists(iconIco)) try { appWindow.SetIcon(iconIco); } catch { }

                    // Force Immersive Dark Mode for the title bar system buttons
                    int darkMode = 1;
                    Win32Helper.DwmSetWindowAttribute(hWnd, Win32Helper.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
                    
                    if (this.Content is FrameworkElement root)
                    {
                        root.RequestedTheme = ElementTheme.Dark;
                    }

                    if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                    {
                        presenter.IsResizable = false;
                        presenter.IsMaximizable = false;
                        presenter.IsMinimizable = true;
                        presenter.IsAlwaysOnTop = false;
                    }
                }
            }
            catch (Exception)
            {
                // Silently fail

            }
        }

        public void SelectSection(string tag)
        {
            try
            {
                if (GeneralSection != null) GeneralSection.Visibility = tag == "General" ? Visibility.Visible : Visibility.Collapsed;
                if (MonitoringSection != null) MonitoringSection.Visibility = tag == "Monitoring" ? Visibility.Visible : Visibility.Collapsed;
                if (AppearanceSection != null) AppearanceSection.Visibility = tag == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
                if (AboutSection != null) AboutSection.Visibility = tag == "About" ? Visibility.Visible : Visibility.Collapsed;
                
                // Update NavigationView selection if possible
                foreach (var item in SettingsNav.MenuItems.OfType<NavigationViewItem>())
                {
                    if (item.Tag?.ToString() == tag)
                    {
                        SettingsNav.SelectedItem = item;
                        break;
                    }
                }
            }
            catch { }
        }

        private void HomeCard_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag)
            {
                foreach (var item in SettingsNav.MenuItems)
                {
                    if (item is NavigationViewItem navItem && navItem.Tag?.ToString() == tag)
                    {
                        SettingsNav.SelectedItem = navItem;
                        break;
                    }
                }
            }
        }

        private void SettingsNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            try 
            {
                if (args.SelectedItemContainer != null)
                {
                    string tag = args.SelectedItemContainer?.Tag?.ToString() ?? string.Empty;
                    
                    if (HomeSection != null) HomeSection.Visibility = tag == "Home" ? Visibility.Visible : Visibility.Collapsed;
                    if (GeneralSection != null) GeneralSection.Visibility = tag == "General" ? Visibility.Visible : Visibility.Collapsed;
                    if (MonitoringSection != null) MonitoringSection.Visibility = tag == "Monitoring" ? Visibility.Visible : Visibility.Collapsed;
                    if (AppearanceSection != null) AppearanceSection.Visibility = tag == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
                    if (AboutSection != null) AboutSection.Visibility = tag == "About" ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            catch { }
        }

        private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0) return;
            var item = e.AddedItems[0] as ComboBoxItem;
            if (item == null) return;

            string themeName = item.Content?.ToString() ?? "Default";
            
            // Apply presets without overriding if user manually changed colors (optional, but requested as "themes")
            switch (themeName)
            {
                case "Cyberpunk":
                    _config.Config.AccentColorHex = "#FF00FF";
                    _config.Config.IconColorHex = "#FFFF00";
                    _config.Config.BackgroundColorHex = "#B4200020";
                    _config.Config.IsTextBold = true;
                    _config.Config.IsIconBold = true;
                    break;
                case "Matrix":
                    _config.Config.AccentColorHex = "#32CD32";
                    _config.Config.IconColorHex = "#00FF00";
                    _config.Config.BackgroundColorHex = "#B4001000";
                    _config.Config.FontFamily = "Consolas";
                    _config.Config.IsTextBold = true;
                    break;
                case "Stealth":
                    _config.Config.AccentColorHex = "#AAAAAA";
                    _config.Config.IconColorHex = "#444444";
                    _config.Config.BackgroundColorHex = "#64101010";
                    _config.Config.IsTextBold = false;
                    _config.Config.IsIconBold = false;
                    break;
                case "Synthwave":
                    _config.Config.AccentColorHex = "#BD00FF"; // Neon Purple
                    _config.Config.IconColorHex = "#00E0FF";   // Neon Cyan
                    _config.Config.BackgroundColorHex = "#B4100520"; // Dark Violet
                    _config.Config.IsTextBold = true;
                    _config.Config.IsIconBold = true;
                    break;
                case "Midnight Gold":
                    _config.Config.AccentColorHex = "#D4AF37"; // Gold
                    _config.Config.IconColorHex = "#F5F5F5";   // Off-white
                    _config.Config.BackgroundColorHex = "#B4050505"; // Matte Black
                    _config.Config.IsTextBold = true;
                    _config.Config.IsIconBold = true;
                    break;
                case "Frost":
                    _config.Config.AccentColorHex = "#E0FFFF"; // Arctic
                    _config.Config.IconColorHex = "#00BFFF";   // Sky
                    _config.Config.BackgroundColorHex = "#B41A2533"; // Ice
                    _config.Config.IsTextBold = true;
                    break;
                case "Inferno":
                    _config.Config.AccentColorHex = "#FF4500"; // OrangeRed
                    _config.Config.IconColorHex = "#990000";   // Deep Red
                    _config.Config.BackgroundColorHex = "#B4100505"; // Charcoal
                    _config.Config.IsTextBold = true;
                    break;
                case "Toxic":
                    _config.Config.AccentColorHex = "#9400D3"; // Purple
                    _config.Config.IconColorHex = "#00FF00";   // Green
                    _config.Config.BackgroundColorHex = "#B4051000"; // Sludge
                    _config.Config.IsTextBold = true;
                    break;
                case "Nordic":
                    _config.Config.AccentColorHex = "#4682B4"; // Steel Blue
                    _config.Config.IconColorHex = "#FFFAFA";   // Snow
                    _config.Config.BackgroundColorHex = "#B4101A25"; // Cold Navy
                    _config.Config.IsTextBold = false;
                    _config.Config.FontFamily = "Inter";
                    break;
                case "Default":
                    _config.Config.AccentColorHex = "#FFFFFF";
                    _config.Config.IconColorHex = "#00CCFF";
                    _config.Config.BackgroundColorHex = "#B4141414";
                    _config.Config.FontFamily = "Segoe UI";
                    _config.Config.IsTextBold = true;
                    break;
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try 
            {
                StartupService.SetStartup(_config.Config.LaunchOnStartup);
                _config.SaveConfig();
                this.Close();
            }
            catch (Exception)
            {
                this.Close();
            }
        }

        private void ResetAppearance_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _config.Config.ScaleFactor = 1.0;
                _config.Config.AccentColorHex = "#FFFFFF";
                _config.Config.IconColorHex = "#00CCFF";
                _config.Config.FontFamily = "Segoe UI";
                _config.Config.DisplayStyle = "Icon";
                _config.Config.IsTextBold = true;
                _config.Config.IsIconBold = false;
                _config.Config.Theme = "Default";
                _config.Config.ShowBackground = false;
                
                // Refresh UI if necessary (bindings should handle it)
            }
            catch { }
        }

        private void QuitButton_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Exit();
        }

        private void SettingsWindow_Closed(object sender, WindowEventArgs args)
        {
            try
            {
                // Unsubscribe events
                this.Activated -= SettingsWindow_Activated;
                this.Closed -= SettingsWindow_Closed;

                // Clear Collections to break references
                NetAdapterCombo?.Items.Clear();
                GpuAdapterCombo?.Items.Clear();
                DiskDriveCombo?.Items.Clear();
                ThemeCombo?.Items.Clear();

                // Clear DataContext and Content
                this.Content = null;
                this.DataContext = null;
                _viewModel = null!;
                _config = null!;

                // Force a small GC hint if possible, though usually not needed if refs are gone
                // GC.Collect(); 
            }
            catch { }
        }
    }
}
