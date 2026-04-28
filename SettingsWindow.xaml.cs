using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Linq;
using System.IO;
using Kil0bitSystemMonitor.ViewModels;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Helpers;
using ModernWpf.Controls;

namespace Kil0bitSystemMonitor
{
    public class DiskSelectionItem : System.ComponentModel.INotifyPropertyChanged
    {
        private string _name = "";
        private bool _isSelected;

        public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }
        public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(); } }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        }
    }

    public partial class SettingsWindow : Window
    {
        private MainViewModel _viewModel = null!;
        private ConfigService _config = null!;

        public System.Collections.ObjectModel.ObservableCollection<DiskSelectionItem> DiskItems { get; } = new();
        private System.Collections.Generic.List<string> _diskSelectionOrder = new();

        public SettingsWindow(MainViewModel viewModel, ConfigService config)
        {
            _viewModel = viewModel;
            _config = config;
            try 
            {
                this.InitializeComponent();
                
                // Set custom icon
                string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
                if (!System.IO.File.Exists(iconPath)) iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.png");
                Kil0bitSystemMonitor.Helpers.Win32Helper.SetAppIcon(new System.Windows.Interop.WindowInteropHelper(this).Handle, iconPath);

                PopulateGpuList();
                PopulateDiskList();
                PopulateNetworkList();
                EnsureValidSelections();
                
                this.DataContext = _viewModel;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SettingsWindow Init Error: {ex.Message}");
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

        private void PopulateDiskList()
        {
            try
            {
                DiskItems.Clear();
                var disks = TelemetryService.GetAvailableDisks();
                var selectedArray = _config.Config.SelectedDisks?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? new string[] { "All" };
                
                _diskSelectionOrder.Clear();
                if (!selectedArray.Contains("All") && !selectedArray.Contains("None"))
                    _diskSelectionOrder.AddRange(selectedArray);

                foreach (var disk in disks)
                {
                    if (disk == "_Total") continue;

                    var item = new DiskSelectionItem { 
                        Name = disk, 
                        IsSelected = selectedArray.Contains(disk) || selectedArray.Contains("All")
                    };
                    item.PropertyChanged += (s, e) => {
                        if (e.PropertyName == nameof(DiskSelectionItem.IsSelected)) UpdateSelectedDisks(item);
                    };
                    DiskItems.Add(item);
                }
            }
            catch { }
        }

        private void UpdateSelectedDisks(DiskSelectionItem item)
        {
            if (item.IsSelected)
            {
                if (!_diskSelectionOrder.Contains(item.Name)) _diskSelectionOrder.Add(item.Name);
            }
            else
            {
                _diskSelectionOrder.Remove(item.Name);
            }

            if (_diskSelectionOrder.Count == 0) _config.Config.SelectedDisks = "None";
            else _config.Config.SelectedDisks = string.Join(";", _diskSelectionOrder);
        }

        private void EnsureValidSelections()
        {
            try
            {
                if (_config.Config.NetworkAdapter == "All") _config.Config.NetworkAdapter = "Default";
                if (_config.Config.GpuAdapter == "All") _config.Config.GpuAdapter = "Default";
                if (_config.Config.SelectedDisks == "Default") _config.Config.SelectedDisks = "All";

                if (NetAdapterCombo.SelectedIndex == -1 && NetAdapterCombo.Items.Count > 0) NetAdapterCombo.SelectedIndex = 0;
                if (GpuAdapterCombo.SelectedIndex == -1 && GpuAdapterCombo.Items.Count > 0) GpuAdapterCombo.SelectedIndex = 0;

            }
            catch { }
        }

        private void SettingsRoot_Loaded(object sender, RoutedEventArgs e)
        {
            try 
            {
                var hWnd = new WindowInteropHelper(this).Handle;
                
                // Set taskbar icon
                string iconPng = Path.Combine(AppContext.BaseDirectory, "icon.png");
                Win32Helper.SetAppIcon(hWnd, iconPng);

                // Force Immersive Dark Mode for the title bar system buttons
                int darkMode = 1;
                Win32Helper.DwmSetWindowAttribute(hWnd, Win32Helper.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
            }
            catch { }
        }



        private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0) return;
            var item = e.AddedItems[0] as ComboBoxItem;
            if (item == null) return;

            string themeName = item.Content?.ToString() ?? "Default";
            
            switch (themeName)
            {
                case "Cyberpunk":
                    _config.Config.AccentColorHex = "#FF00FF";
                    _config.Config.LabelColorHex = "#FFFF00";
                    _config.Config.BackgroundColorHex = "#B4200020";
                    _config.Config.IsTextBold = true;
                    break;
                case "Matrix":
                    _config.Config.AccentColorHex = "#32CD32";
                    _config.Config.LabelColorHex = "#00FF00";
                    _config.Config.BackgroundColorHex = "#B4001000";
                    _config.Config.FontFamily = "Consolas";
                    _config.Config.IsTextBold = true;
                    break;
                case "Stealth":
                    _config.Config.AccentColorHex = "#AAAAAA";
                    _config.Config.LabelColorHex = "#444444";
                    _config.Config.BackgroundColorHex = "#64101010";
                    _config.Config.IsTextBold = false;
                    break;
                case "Synthwave":
                    _config.Config.AccentColorHex = "#BD00FF";
                    _config.Config.LabelColorHex = "#00E0FF";
                    _config.Config.BackgroundColorHex = "#B4100520";
                    _config.Config.IsTextBold = true;
                    break;
                case "Midnight Gold":
                    _config.Config.AccentColorHex = "#D4AF37";
                    _config.Config.LabelColorHex = "#F5F5F5";
                    _config.Config.BackgroundColorHex = "#B4050505";
                    _config.Config.IsTextBold = true;
                    break;
                case "Frost":
                    _config.Config.AccentColorHex = "#E0FFFF";
                    _config.Config.LabelColorHex = "#00BFFF";
                    _config.Config.BackgroundColorHex = "#B41A2533";
                    _config.Config.IsTextBold = true;
                    break;
                case "Inferno":
                    _config.Config.AccentColorHex = "#FF4500";
                    _config.Config.LabelColorHex = "#990000";
                    _config.Config.BackgroundColorHex = "#B4100505";
                    _config.Config.IsTextBold = true;
                    break;
                case "Toxic":
                    _config.Config.AccentColorHex = "#9400D3";
                    _config.Config.LabelColorHex = "#00FF00";
                    _config.Config.BackgroundColorHex = "#B4051000";
                    _config.Config.IsTextBold = true;
                    break;
                case "Nordic":
                    _config.Config.AccentColorHex = "#4682B4";
                    _config.Config.LabelColorHex = "#FFFAFA";
                    _config.Config.BackgroundColorHex = "#B4101A25";
                    _config.Config.IsTextBold = false;
                    _config.Config.FontFamily = "Inter";
                    break;
                case "Default":
                    _config.Config.AccentColorHex = "#FFFFFF";
                    _config.Config.LabelColorHex = "#00CCFF";
                    _config.Config.BackgroundColorHex = "#B4141414";
                    _config.Config.FontFamily = "Segoe UI";
                    _config.Config.IsTextBold = true;
                    break;
            }
        }



        private void ResetToDefaults_Click(object sender, RoutedEventArgs e)
        {
            var c = _config.Config;
            c.DisplayStyle = "Text";
            c.FontFamily = "Segoe UI";
            c.AccentColorHex = "#FFFFFF";
            c.LabelColorHex = "#00CCFF";
            c.BackgroundColorHex = "#B4141414";
            c.PodColorHex = "#0FFFFFFF";
            c.ScaleFactor = 1.0;
            c.IsTextBold = true;
            c.ShowPods = true;
            c.ShowBackground = false;
            _config.SaveConfig();
        }

        private async void ResetApp_Click(object sender, RoutedEventArgs e)
        {
            ContentDialog resetDialog = new ContentDialog
            {
                Title = "Factory Reset",
                Content = "Are you sure you want to reset all settings to factory defaults?\n\nThis will revert all monitoring, general, and appearance preferences to their original states. This action cannot be undone.",
                PrimaryButtonText = "Reset All",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };

            // Set the dialog theme to dark to match the app
            ModernWpf.ThemeManager.SetRequestedTheme(resetDialog, ModernWpf.ElementTheme.Dark);

            ContentDialogResult result = await resetDialog.ShowAsync();

            if (result != ContentDialogResult.Primary) return;

            var c = _config.Config;
            c.ShowOverlay = true;
            c.LockPosition = false;
            c.LaunchOnStartup = false;
            c.HideOnFullscreen = true;
            c.StickToTaskbar = true;
            c.AlwaysOnTop = true;
            
            c.ShowCpu = true;
            c.ShowRam = true;
            c.ShowGpu = true;
            c.ShowTemp = true;
            c.ShowDisk = true;
            c.ShowDiskSpeed = true;
            c.ShowNetUp = true;
            c.ShowNetDown = true;
            
            c.NetworkAdapter = "Default";
            c.GpuAdapter = "Default";
            c.SelectedDisks = "All";
            
            c.DisplayStyle = "Text";
            c.FontFamily = "Segoe UI";
            c.AccentColorHex = "#FFFFFF";
            c.LabelColorHex = "#00CCFF";
            c.BackgroundColorHex = "#B4141414";
            c.PodColorHex = "#0FFFFFFF";
            c.ScaleFactor = 1.0;
            c.IsTextBold = true;
            c.ShowPods = true;
            c.ShowBackground = false;
            
            StartupService.SetStartup(false);
            _config.SaveConfig();
        }

        private void ColorButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string tag)
            {
                using (var dialog = new System.Windows.Forms.ColorDialog())
                {
                    dialog.FullOpen = true;
                    
                    string currentHex = tag switch {
                        "Accent" => _config.Config.AccentColorHex,
                        "Label" => _config.Config.LabelColorHex,
                        "Background" => _config.Config.BackgroundColorHex,
                        "Pod" => _config.Config.PodColorHex,
                        _ => "#FFFFFF"
                    };
                    
                    try 
                    {
                        var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(currentHex);
                        dialog.Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B);
                    } 
                    catch { }

                    if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    {
                        // Preserve alpha if it was already there (for background mostly)
                        string alpha = "FF";
                        if (currentHex.Length == 9) alpha = currentHex.Substring(1, 2);
                        else if (tag == "Background") alpha = "B4"; // Default opacity for backplate

                        string hex = $"#{alpha}{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
                        
                        switch (tag)
                        {
                            case "Accent": _config.Config.AccentColorHex = hex; break;
                            case "Label": _config.Config.LabelColorHex = hex; break;
                            case "Background": _config.Config.BackgroundColorHex = hex; break;
                            case "Pod": _config.Config.PodColorHex = hex; break;
                        }
                    }
                }
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            StartupService.SetStartup(_config.Config.LaunchOnStartup);
            _config.SaveConfig();
            this.Close();
        }

        private void HomeCard_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string tag)
            {
                SelectSection(tag);
            }
        }

        private void SettingsNav_SelectionChanged(object sender, ModernWpf.Controls.NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is ModernWpf.Controls.NavigationViewItem item)
            {
                SelectSection(item.Tag?.ToString() ?? string.Empty);
            }
        }

        public void SelectSection(string sectionName)
        {
            if (string.IsNullOrEmpty(sectionName)) return;

            HomeSection.Visibility = Visibility.Collapsed;
            GeneralSection.Visibility = Visibility.Collapsed;
            MonitoringSection.Visibility = Visibility.Collapsed;
            AppearanceSection.Visibility = Visibility.Collapsed;
            AboutSection.Visibility = Visibility.Collapsed;

            switch (sectionName)
            {
                case "Home": HomeSection.Visibility = Visibility.Visible; break;
                case "General": GeneralSection.Visibility = Visibility.Visible; break;
                case "Monitoring": MonitoringSection.Visibility = Visibility.Visible; break;
                case "Appearance": AppearanceSection.Visibility = Visibility.Visible; break;
                case "About": AboutSection.Visibility = Visibility.Visible; break;
            }

            // Sync Nav selection
            foreach (var item in SettingsNav.MenuItems.OfType<ModernWpf.Controls.NavigationViewItem>())
            {
                if (item.Tag?.ToString() == sectionName)
                {
                    SettingsNav.SelectedItem = item;
                    break;
                }
            }
        }

        private void QuitButton_Click(object sender, RoutedEventArgs e)
        {
            App.Quit();
        }
    }
}
