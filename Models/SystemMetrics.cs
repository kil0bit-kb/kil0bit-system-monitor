using System.Windows.Media;
using System.Linq;

namespace Kil0bitSystemMonitor.Models
{
    public class DiskMetric
    {
        public string Name { get; set; } = "";
        public float SpacePercent { get; set; }
        public float ActivityPercent { get; set; }
    }

    public class SystemMetrics
    {
        public float CpuUsage { get; set; }
        public float RamPercent { get; set; }
        public float GpuUsage { get; set; }
        public float GpuTemperature { get; set; }
        public float NetUpKbps { get; set; }
        public float NetDownKbps { get; set; }
        public string NetUpText { get; set; } = "0 KB/s";
        public string NetDownText { get; set; } = "0 KB/s";
        public float DiskUsage { get; set; }
        public float DiskPercent { get; set; }
        public System.Collections.Generic.List<DiskMetric> Disks { get; set; } = new();
    }

    public class AppConfig : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _showOverlay = true;
        private bool _lockPosition = false;
        private bool _launchOnStartup = false;
        private bool _showCpu = true;
        private bool _showRam = true;
        private bool _showGpu = true;
        private bool _showTemp = true;
        private bool _showDisk = true;
        private bool _showDiskSpeed = true;
        private bool _showNetUp = true;
        private bool _showNetDown = true;
        private string _networkAdapter = "Default";
        private string _gpuAdapter = "Default";
        private string _selectedDisks = "Default";
        private string _displayStyle = "Text";
        private string _fontFamily = "Segoe UI";
        private string _accentColorHex = "#FFFFFF";
        private string _labelColorHex = "#00CCFF";
        private double _x = 100;
        private double _y = 100;
        private bool _hideOnFullscreen = true;
        private bool _stickToTaskbar = true;
        private bool _showBackground = false;
        private string _backgroundColorHex = "#B4141414";
        private double _scaleFactor = 1.0;
        private bool _isTextBold = true;

        private string _theme = "Default";
        private int _updateInterval = 1000;
        private int _gpuIndex = 0;
        private bool _showPods = true;
        private string _podColorHex = "#0FFFFFFF"; 
        private bool _alwaysOnTop = true;
        public bool ShowOverlay { get => _showOverlay; set { _showOverlay = value; OnPropertyChanged(); } }
        public bool LockPosition { get => _lockPosition; set { _lockPosition = value; OnPropertyChanged(); } }
        public bool LaunchOnStartup { get => _launchOnStartup; set { _launchOnStartup = value; OnPropertyChanged(); } }

        public bool ShowCpu { get => _showCpu; set { _showCpu = value; OnPropertyChanged(); } }
        public bool ShowRam { get => _showRam; set { _showRam = value; OnPropertyChanged(); } }
        public bool ShowGpu { get => _showGpu; set { _showGpu = value; OnPropertyChanged(); } }
        public bool ShowTemp { get => _showTemp; set { _showTemp = value; OnPropertyChanged(); } }
        public bool ShowDisk { get => _showDisk; set { _showDisk = value; OnPropertyChanged(); } }
        public bool ShowDiskSpeed { get => _showDiskSpeed; set { _showDiskSpeed = value; OnPropertyChanged(); } }
        public bool ShowNetUp { get => _showNetUp; set { _showNetUp = value; OnPropertyChanged(); } }
        public bool ShowNetDown { get => _showNetDown; set { _showNetDown = value; OnPropertyChanged(); } }

        public string NetworkAdapter { get => _networkAdapter; set { _networkAdapter = value; OnPropertyChanged(); } }
        public string GpuAdapter { get => _gpuAdapter; set { _gpuAdapter = value; OnPropertyChanged(); } }
        public string SelectedDisks { get => _selectedDisks; set { _selectedDisks = value; OnPropertyChanged(); } }
        public string DisplayStyle { get => _displayStyle; set { _displayStyle = value; OnPropertyChanged(); } }
        public string FontFamily { get => _fontFamily; set { _fontFamily = value; OnPropertyChanged(); } }

        public string AccentColorHex { get => _accentColorHex; set { _accentColorHex = value; OnPropertyChanged(); OnPropertyChanged(nameof(AccentColor)); } }
        public string LabelColorHex { get => _labelColorHex; set { _labelColorHex = value; OnPropertyChanged(); OnPropertyChanged(nameof(LabelColor)); } }
        public string BackgroundColorHex { get => _backgroundColorHex; set { _backgroundColorHex = value; OnPropertyChanged(); OnPropertyChanged(nameof(BackgroundColor)); } }

        public double ScaleFactor { get => _scaleFactor; set { _scaleFactor = value; OnPropertyChanged(); } }
        public bool IsTextBold { get => _isTextBold; set { _isTextBold = value; OnPropertyChanged(); } }

        public string Theme { get => _theme; set { _theme = value; OnPropertyChanged(); } }
        public int UpdateInterval { get => _updateInterval; set { _updateInterval = value; OnPropertyChanged(); } }
        public int GpuIndex { get => _gpuIndex; set { _gpuIndex = value; OnPropertyChanged(); } }
        public bool ShowPods { get => _showPods; set { _showPods = value; OnPropertyChanged(); } }
        public string PodColorHex { get => _podColorHex; set { _podColorHex = value; OnPropertyChanged(); OnPropertyChanged(nameof(PodColor)); } }
        public bool AlwaysOnTop { get => _alwaysOnTop; set { _alwaysOnTop = value; OnPropertyChanged(); } }

        public double X { get => _x; set { _x = value; OnPropertyChanged(); } }
        public double Y { get => _y; set { _y = value; OnPropertyChanged(); } }
        public bool HideOnFullscreen { get => _hideOnFullscreen; set { _hideOnFullscreen = value; OnPropertyChanged(); } }
        public bool StickToTaskbar { get => _stickToTaskbar; set { _stickToTaskbar = value; OnPropertyChanged(); } }
        public bool ShowBackground { get => _showBackground; set { _showBackground = value; OnPropertyChanged(); } }

        [System.Text.Json.Serialization.JsonIgnore]
        public System.Windows.Media.Color AccentColor { get => HexToColor(AccentColorHex); set => AccentColorHex = ColorToHex(value); }

        [System.Text.Json.Serialization.JsonIgnore]
        public System.Windows.Media.Color LabelColor { get => HexToColor(LabelColorHex); set => LabelColorHex = ColorToHex(value); }

        [System.Text.Json.Serialization.JsonIgnore]
        public System.Windows.Media.Color BackgroundColor { get => HexToColor(BackgroundColorHex); set => BackgroundColorHex = ColorToHex(value); }

        [System.Text.Json.Serialization.JsonIgnore]
        public System.Windows.Media.Color PodColor { get => HexToColor(PodColorHex); set => PodColorHex = ColorToHex(value); }

        private System.Windows.Media.Color HexToColor(string hex)
        {
            try
            {
                hex = hex.TrimStart('#');
                if (hex.Length == 8) // ARGB
                {
                    return System.Windows.Media.Color.FromArgb(
                        byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber),
                        byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber),
                        byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber),
                        byte.Parse(hex.Substring(6, 2), System.Globalization.NumberStyles.HexNumber));
                }
                if (hex.Length == 6) // RGB
                {
                    return System.Windows.Media.Color.FromRgb(
                        byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber),
                        byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber),
                        byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber));
                }
            }
            catch { }
            return Colors.White;
        }

        private string ColorToHex(System.Windows.Media.Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        }
    }
}
