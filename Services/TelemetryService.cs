using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Net.NetworkInformation;
using System.Timers;
using System.Management;
using Kil0bitSystemMonitor.Models;

namespace Kil0bitSystemMonitor.Services
{
    public class TelemetryService : IDisposable
    {
        private readonly PerformanceCounter _cpuCounter;
        private PerformanceCounter? _diskCounter;
        private System.Collections.Generic.Dictionary<string, PerformanceCounter> _gpuCounters = new();
        private string? _selectedGpuLuid; // Lowercase luid e.g. "0x00000000_0x0000e3aa"
        private int _nvidiaGlobalIndex = -1;
        private bool _isNvidiaSelected = false;
        private Process? _smiProcess;
        private System.Timers.Timer? _timer;
        private float _nvidiaUsageValue = 0;
        
        private long _lastNetUp;
        private long _lastNetDown;
        private DateTime _lastNetTime;
        private System.Collections.Generic.List<System.IO.DriveInfo> _cachedDrives = new();
        private DateTime _lastDriveRefresh = DateTime.MinValue;
        private System.Net.NetworkInformation.NetworkInterface[]? _cachedNetworkInterfaces;
        private DateTime _lastNetworkRefresh = DateTime.MinValue;
        private DateTime _lastGpuCounterRefresh = DateTime.MinValue;

        public event Action<SystemMetrics>? MetricsUpdated;

        public static System.Collections.Generic.List<string> GetAvailableDisks()
        {
            var disks = new System.Collections.Generic.List<string>();
            try
            {
                var category = new PerformanceCounterCategory("PhysicalDisk");
                var instances = category.GetInstanceNames();
                foreach (var inst in instances)
                {
                    if (inst != "_Total") disks.Add(inst);
                }
            }
            catch { }
            return disks.OrderBy(d => d).ToList();
        }
        public static System.Collections.Generic.List<string> GetAvailableGpus()
        {
            var gpus = new System.Collections.Generic.List<string>();
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject obj in results)
                    {
                        try 
                        {
                            string name = obj["Name"]?.ToString() ?? "Unknown GPU";
                            if (!gpus.Contains(name)) gpus.Add(name);
                        }
                        finally { obj.Dispose(); }
                    }
                }
            }
            catch { }
            return gpus;
        }
        public static System.Collections.Generic.List<string> GetAvailableNetworkAdapters()
        {
            var adapters = new System.Collections.Generic.List<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (IsSelectableAdapter(ni))
                    {
                        adapters.Add(ni.Name);
                    }
                }
            }
            catch { }
            return adapters.OrderBy(a => a).ToList();
        }

        private static bool IsSelectableAdapter(NetworkInterface ni)
        {
            if (ni.OperationalStatus != OperationalStatus.Up) return false;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) return false;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) return false;

            string name = ni.Name.ToLowerInvariant();
            string desc = ni.Description.ToLowerInvariant();

            // Exclude ONLY redundant system filters and pseudo-layers.
            string[] systemExcludes = { 
                "filter", "packet scheduler", "wfp", "qos", "native mac layer", "microsoft loopback" 
            };

            if (systemExcludes.Any(e => name.Contains(e) || desc.Contains(e))) return false;

            // Microsoft Wi-Fi Direct and other transient internal virtual connections 
            // almost always have an asterisk in the name. We hide these to reduce clutter.
            if (name.Contains("*")) return false;

            // We keep user-visible virtual adapters like VMware, VirtualBox, and VPNs.
            return ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet || 
                   ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                   ni.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
            public MEMORYSTATUSEX()
            {
                this.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        private readonly ConfigService _config;

        public TelemetryService(ConfigService config)
        {
            _config = config;
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            
            _config.Config.PropertyChanged += Config_PropertyChanged;
            InitializeGpu();
            InitializeDisk();
            InitializeNetwork();
            _lastNetTime = DateTime.Now;

            _timer = new System.Timers.Timer(1000);
            _timer.AutoReset = false; // Prevents overlapping callbacks if UpdateMetrics takes >1s
            _timer.Elapsed += (s, e) =>
            {
                try   { UpdateMetrics(); }
                finally { _timer?.Start(); } // Re-arm only after completion
            };
            _timer.Start();

            // Perform first update immediately
            UpdateMetrics();
        }

        private void InitializeGpu()
        {
            try 
            {
                string selectedName = _config.Config.GpuAdapter;
                _selectedGpuLuid = null;
                _nvidiaGlobalIndex = -1;
                _isNvidiaSelected = false;

                // Dispose existing counters
                foreach (var c in _gpuCounters.Values) c.Dispose();
                _gpuCounters.Clear();

                // 1. SMART DEFAULT DISCOVERY (and 'All' mode fallback for Temp)
                if (selectedName == "Default" || selectedName == "All")
                {
                    // For 'All', we still want a valid temp source
                    var nvidiaGpus = GetNvidiaGpus();
                    if (nvidiaGpus.Count > 0)
                    {
                        if (selectedName == "Default") selectedName = nvidiaGpus[0];
                        _isNvidiaSelected = true;
                        _nvidiaGlobalIndex = 0;
                    }
                    else 
                    {
                        var allGpus = GetAvailableGpus();
                        var discrete = allGpus.FirstOrDefault(n => n.Contains("AMD", StringComparison.OrdinalIgnoreCase) || 
                                                                  n.Contains("Arc", StringComparison.OrdinalIgnoreCase) || 
                                                                  n.Contains("Radeon", StringComparison.OrdinalIgnoreCase));
                        if (discrete != null && selectedName == "Default")
                        {
                            selectedName = discrete;
                        }
                    }
                }

                // 2. SPECIFIC GPU LUID MAPPING (Matches real names OR discovered name)
                if (selectedName != "All" && selectedName != "Default")
                {
                    _isNvidiaSelected = selectedName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);

                    // Map selected name to LUID using memory heuristics (Dedicated vs Shared)
                    try
                    {
                        string? dedicatedLuidCandidate = null;
                        string? sharedLuidCandidate = null;
                        long maxDedicated = -1;
                        long maxShared = -1;

                        using (var searcher = new ManagementObjectSearcher(@"root\CIMV2", "SELECT Name, DedicatedUsage, SharedUsage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory"))
                        using (var results = searcher.Get())
                        {
                            foreach (ManagementObject obj in results)
                            {
                                try 
                                {
                                    string name = (obj["Name"]?.ToString() ?? "").ToLowerInvariant();
                                    long dedicated = Convert.ToInt64(obj["DedicatedUsage"] ?? 0);
                                    long shared = Convert.ToInt64(obj["SharedUsage"] ?? 0);

                                    if (name.Contains("luid_"))
                                    {
                                        var parts = name.Split('_');
                                        int lIdx = Array.IndexOf(parts, "luid");
                                        if (lIdx >= 0 && lIdx + 2 < parts.Length)
                                        {
                                            string luid = parts[lIdx + 1] + "_" + parts[lIdx + 2];
                                            
                                            // Dedicated GPUs (NVIDIA/AMD) have discrete memory
                                            if (dedicated > 0)
                                            {
                                                if (dedicated > maxDedicated) { maxDedicated = dedicated; dedicatedLuidCandidate = luid; }
                                            }
                                            else // Integrated GPUs (Intel) usually rely on Shared memory
                                            {
                                                if (shared > maxShared) { maxShared = shared; sharedLuidCandidate = luid; }
                                            }
                                        }
                                    }
                                }
                                finally { obj.Dispose(); }
                            }
                        }

                        bool isDedicatedSelection = _isNvidiaSelected || 
                                                    selectedName.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                                                    selectedName.Contains("Arc", StringComparison.OrdinalIgnoreCase) ||
                                                    selectedName.Contains("Radeon", StringComparison.OrdinalIgnoreCase);

                        // Only set _selectedGpuLuid if NOT in "All" mode (where we want aggregate usage)
                        if (_config.Config.GpuAdapter != "All")
                        {
                            _selectedGpuLuid = isDedicatedSelection ? dedicatedLuidCandidate : (sharedLuidCandidate ?? dedicatedLuidCandidate);
                        }
                        else
                        {
                            // In "All" mode, we still need a target LUID for temperature methods that require it
                            _selectedGpuLuid = null; // Aggregated Usage
                        }
                        
                        // We'll use a local search for a temp-only LUID for the hardware methods
                        if (_config.Config.GpuAdapter == "All" || _selectedGpuLuid == null)
                        {
                             // Temp methods will try their best with null or generic probes unless we find a hint
                             // We'll trust the fallback logic in GetGpuTemperature
                        }

                        if (_selectedGpuLuid != null) { /* Selection validated */ }
                    }
                    catch { }
                }

                // Initial update of counters
                UpdateGpuCounters();
                
                StartSmiReader();
            }
            catch (Exception)
            {
                // Silently fail
            }
        }

        private void InitializeDisk()
        {
            try
            {
                _diskCounter?.Dispose();
                string disk = _config.Config.DiskDrive ?? "All";
                if (disk == "All" || disk == "Default") disk = "_Total";
                _diskCounter = new PerformanceCounter("PhysicalDisk", "% Disk Time", disk);
            }
            catch { }
        }

        private void UpdateGpuCounters()
        {
            try
            {
                // Throttle enumeration to once every 30 seconds to save CPU
                if ((DateTime.Now - _lastGpuCounterRefresh).TotalSeconds < 30 && _gpuCounters.Count > 0) return;
                _lastGpuCounterRefresh = DateTime.Now;

                var category = new PerformanceCounterCategory("GPU Engine");
                var instances = category.GetInstanceNames();
                var targetInstances = instances.Where(name => 
                {
                    if (!name.Contains("engtype_3D")) return false;
                    if (_selectedGpuLuid != null) return name.ToLowerInvariant().Contains(_selectedGpuLuid);
                    return true;
                }).ToList();

                // Add new instances
                foreach (var inst in targetInstances)
                {
                    if (!_gpuCounters.ContainsKey(inst))
                    {
                        try { _gpuCounters[inst] = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst); }
                        catch { }
                    }
                }

                // Always clean up stale instances — not just when >100 — to prevent resource leak
                var toRemove = _gpuCounters.Keys.Where(k => !targetInstances.Contains(k)).ToList();
                foreach (var r in toRemove) { _gpuCounters[r].Dispose(); _gpuCounters.Remove(r); }
            }
            catch { }
        }

        private System.Collections.Generic.List<string> GetNvidiaGpus()
        {
            var list = new System.Collections.Generic.List<string>();
            try
            {
                using (var process = new Process())
                {
                    process.StartInfo.FileName = "nvidia-smi";
                    process.StartInfo.Arguments = "--query-gpu=name --format=csv,noheader";
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.CreateNoWindow = true;
                    process.Start();
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(1000);
                    foreach (var line in output.Split('\n')) 
                        if (!string.IsNullOrWhiteSpace(line)) list.Add(line.Trim());
                }
            }
            catch { }
            return list;
        }

        private void InitializeNetwork()
        {
            var stats = GetNetworkStats();
            _lastNetUp = stats.up;
            _lastNetDown = stats.down;
        }

        private (long up, long down) GetNetworkStats()
        {
            long up = 0, down = 0;
            string filter = _config?.Config?.NetworkAdapter ?? "Default";

            // Cache network interfaces for 30 seconds to avoid high CPU usage from enumeration
            if (_cachedNetworkInterfaces == null || (DateTime.Now - _lastNetworkRefresh).TotalSeconds > 30)
            {
                try 
                { 
                    _cachedNetworkInterfaces = NetworkInterface.GetAllNetworkInterfaces(); 
                    _lastNetworkRefresh = DateTime.Now;
                }
                catch { return (0, 0); }
            }

            foreach (var ni in _cachedNetworkInterfaces)
            {
                if (IsSelectableAdapter(ni))
                {
                    bool match = filter == "All" || filter == "Default" || filter == ni.Name;
                    if (!match && filter == "Wi-Fi" && ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) match = true;
                    if (!match && filter == "Ethernet" && (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet || ni.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet)) match = true;

                    if (match)
                    {
                        var iStats = ni.GetIPStatistics();
                        up += iStats.BytesSent;
                        down += iStats.BytesReceived;
                    }
                }
            }
            return (up, down);
        }

        private void UpdateMetrics()
        {
            var metrics = new SystemMetrics();

            // CPU
            metrics.CpuUsage = _cpuCounter.NextValue();

            // RAM
            var memStatus = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(memStatus))
            {
                float totalGb = memStatus.ullTotalPhys / 1024f / 1024f / 1024f;
                float availGb = memStatus.ullAvailPhys / 1024f / 1024f / 1024f;
                float usedGb = totalGb - availGb;
                metrics.RamPercent = (usedGb / totalGb) * 100;
                metrics.RamUsageText = $"{usedGb:F1}/{totalGb:F1} GB";
            }

            // GPU
            float gpuUsage = 0;
            if (_isNvidiaSelected)
            {
                // NVIDIA usage through smi is MUCH more reliable
                gpuUsage = GetNvidiaUsage();
            }
            
            if (gpuUsage == 0) // Fallback or non-nvidia
            {
                UpdateGpuCounters();
                foreach (var counter in _gpuCounters.Values)
                {
                    try { gpuUsage += counter.NextValue(); } catch { }
                }
            }
            metrics.GpuUsage = gpuUsage;

            // Network
            var now = DateTime.Now;
            var netStats = GetNetworkStats();
            double seconds = (now - _lastNetTime).TotalSeconds;
            if (seconds > 0)
            {
                // Clamp to 0 — counter resets (adapter reconnect, 30s cache refresh) produce
                // negative deltas that must not be displayed as negative speeds.
                metrics.NetUpKbps   = Math.Max(0f, (float)((netStats.up   - _lastNetUp)   / 1024.0 / seconds));
                metrics.NetDownKbps = Math.Max(0f, (float)((netStats.down - _lastNetDown) / 1024.0 / seconds));
                
                metrics.NetUpText = FormatNet(metrics.NetUpKbps);
                metrics.NetDownText = FormatNet(metrics.NetDownKbps);
            }

            // Disk
            if (_diskCounter != null)
            {
                // NOTE: "% Disk Time" is queue-depth weighted and CAN legally exceed 100%
                // (Windows Task Manager applies the same clamp before displaying).
                try { metrics.DiskUsage = Math.Min(100f, _diskCounter.NextValue()); } catch { }
                
                // Get Space for the selected drive
                try
                {
                    string selectedDisk = _config.Config.DiskDrive ?? "All";
                    char? driveLetter = null;
                    if (selectedDisk != "All")
                    {
                        // Instance usually looks like "0 C: D:"
                        int colonIdx = selectedDisk.IndexOf(':');
                        if (colonIdx > 0) driveLetter = selectedDisk[colonIdx - 1];
                    }
                    
                    if (driveLetter.HasValue)
                    {
                        var drive = new System.IO.DriveInfo(driveLetter.Value.ToString());
                        if (drive.IsReady)
                        {
                            double usedPercent = (1.0 - (double)drive.TotalFreeSpace / drive.TotalSize) * 100.0;
                            metrics.DiskSpaceText = $"{usedPercent:F0}%";
                        }
                    }
                    else
                    {
                        // Aggregate space for all ready drives
                        if ((DateTime.Now - _lastDriveRefresh).TotalSeconds > 10 || _cachedDrives.Count == 0)
                        {
                            _cachedDrives = System.IO.DriveInfo.GetDrives().Where(d => d.IsReady).ToList();
                            _lastDriveRefresh = DateTime.Now;
                        }
                        
                        long total = 0, free = 0;
                        foreach (var d in _cachedDrives)
                        {
                            total += d.TotalSize; free += d.TotalFreeSpace;
                        }
                        double usedPercent = (1.0 - (double)free / total) * 100.0;
                        metrics.DiskSpaceText = $"{usedPercent:F0}%";
                    }
                }
                catch { }
            }

            // Temperature
            metrics.GpuTemperature = GetGpuTemperature();

            _lastNetUp = netStats.up;
            _lastNetDown = netStats.down;
            _lastNetTime = now;

            MetricsUpdated?.Invoke(metrics);
        }

        private void Config_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(_config.Config.GpuAdapter))
            {
                InitializeGpu();
            }
            if (e.PropertyName == nameof(_config.Config.DiskDrive))
            {
                InitializeDisk();
            }
        }

        private void StartSmiReader()
        {
            StopSmiReader();
            if (!_isNvidiaSelected) return;

            try
            {
                string smiPath = "nvidia-smi";
                if (!System.IO.File.Exists(smiPath))
                {
                    var commonPaths = new[] { 
                        @"C:\Windows\System32\nvidia-smi.exe",
                        @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe"
                    };
                    foreach (var p in commonPaths) if (System.IO.File.Exists(p)) { smiPath = p; break; }
                }

                _smiProcess = new Process();
                _smiProcess.StartInfo.FileName = smiPath;
                string idArg = _nvidiaGlobalIndex >= 0 ? $" --id={_nvidiaGlobalIndex}" : "";
                
                // Use -l 1 to loop every second and keep the process alive
                _smiProcess.StartInfo.Arguments = $"--query-gpu=utilization.gpu --format=csv,noheader,nounits{idArg} -l 1";
                _smiProcess.StartInfo.UseShellExecute = false;
                _smiProcess.StartInfo.RedirectStandardOutput = true;
                _smiProcess.StartInfo.CreateNoWindow = true;

                _smiProcess.OutputDataReceived += (sender, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.Data))
                    {
                        if (float.TryParse(args.Data.Trim(), out float val))
                        {
                            _nvidiaUsageValue = val;
                        }
                    }
                };

                _smiProcess.Start();
                _smiProcess.BeginOutputReadLine();
            }
            catch { }
        }

        private void StopSmiReader()
        {
            try
            {
                if (_smiProcess != null && !_smiProcess.HasExited)
                {
                    _smiProcess.Kill();
                }
                _smiProcess?.Dispose();
                _smiProcess = null;
            }
            catch { }
        }

        private float GetNvidiaUsage()
        {
            return _nvidiaUsageValue;
        }

        private string FormatNet(float kbps)
        {
            if (kbps >= 1024f)           // ≥ 1 MB/s → show MB/s (2 decimals)
                return $"{kbps / 1024f:F2} MB/s";
            return $"{kbps:F1} KB/s";   // < 1 MB/s → show KB/s (1 decimal)
        }

        private float _lastGpuTemp = -1;
        private DateTime _lastGpuTempTime = DateTime.MinValue;

        private float GetGpuTemperature()
        {
            try
            {
                // Cache the result for 2 seconds
                if ((DateTime.Now - _lastGpuTempTime).TotalSeconds < 2) return _lastGpuTemp;
                
                float temp = -1;

                // Method 0.5: D3DKMT (Universal, Non-Admin, Works for AMD/NVIDIA/Intel)
                if (_selectedGpuLuid != null && _selectedGpuLuid.Contains("_"))
                {
                    try
                    {
                        var parts = _selectedGpuLuid.Split('_');
                        string lowStr = parts[0].Replace("0x", "");
                        string highStr = parts[1].Replace("0x", "");
                        
                        var openLuid = new D3DKMT_OPENADAPTERFROMLUID();
                        openLuid.AdapterLuid.LowPart = uint.Parse(lowStr, System.Globalization.NumberStyles.HexNumber);
                        openLuid.AdapterLuid.HighPart = int.Parse(highStr, System.Globalization.NumberStyles.HexNumber);

                        if (D3DKMTOpenAdapterFromLuid(ref openLuid) == 0)
                        {
                            var perfData = new D3DKMT_ADAPTER_PERFDATA();
                            int structSize = Marshal.SizeOf(perfData);
                            IntPtr pPerfData = Marshal.AllocHGlobal(structSize);
                            Marshal.StructureToPtr(perfData, pPerfData, false);

                            var queryInfo = new D3DKMT_QUERYADAPTERINFO();
                            queryInfo.hAdapter = openLuid.hAdapter;
                            queryInfo.Type = KMTQUERYADAPTERINFOTYPE.KMTQAITYPE_ADAPTERPERFDATA;
                            queryInfo.pPrivateDriverData = pPerfData;
                            queryInfo.PrivateDriverDataSize = (uint)structSize;

                            if (D3DKMTQueryAdapterInfo(ref queryInfo) == 0)
                            {
                                var resultData = Marshal.PtrToStructure<D3DKMT_ADAPTER_PERFDATA>(pPerfData);
                                if (resultData.Temperature > 0)
                                    temp = resultData.Temperature / 10f; // It's in deci-Celsius
                            }

                            Marshal.FreeHGlobal(pPerfData);
                            var closeAdapter = new D3DKMT_CLOSEADAPTER { hAdapter = openLuid.hAdapter };
                            D3DKMTCloseAdapter(ref closeAdapter);
                        }
                    }
                    catch { }
                }

                if (temp > 0)
                {
                    _lastGpuTemp = temp;
                    _lastGpuTempTime = DateTime.Now;
                    return temp;
                }

                // Method 1: nvidia-smi (Reliable fallback for NVIDIA)
                if (_isNvidiaSelected)
                {
                    try
                    {
                        string smiPath = "nvidia-smi";
                        // Try common locations if not in PATH
                        if (!System.IO.File.Exists(smiPath))
                        {
                            var commonPaths = new[] { 
                                @"C:\Windows\System32\nvidia-smi.exe",
                                @"Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe"
                            };
                            foreach (var p in commonPaths)
                            {
                                string fullPath = System.IO.Path.IsPathRooted(p) ? p : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), p);
                                if (System.IO.File.Exists(fullPath)) { smiPath = fullPath; break; }
                            }
                        }

                        using (var process = new Process())
                        {
                            process.StartInfo.FileName = smiPath;
                            string idArg = _nvidiaGlobalIndex >= 0 ? $" --id={_nvidiaGlobalIndex}" : "";
                            process.StartInfo.Arguments = $"--query-gpu=temperature.gpu --format=csv,noheader,nounits{idArg}";
                            process.StartInfo.UseShellExecute = false;
                            process.StartInfo.RedirectStandardOutput = true;
                            process.StartInfo.CreateNoWindow = true;
                            process.Start();
                            string output = process.StandardOutput.ReadToEnd();
                            process.WaitForExit(1000);
                            if (float.TryParse(output.Trim(), out float val))
                            {
                                temp = val;
                            }
                        }
                    }
                    catch { }
                }

                if (temp > 0) 
                {
                    _lastGpuTemp = temp;
                    _lastGpuTempTime = DateTime.Now;
                    return temp;
                }

                // Method 2: Performance Counters (Available on some modern Windows versions/drivers)
                try
                {
                    using (var searcher = new ManagementObjectSearcher(@"root\CIMV2", "SELECT * FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory"))
                    using (var results = searcher.Get())
                    {
                        foreach (ManagementObject obj in results)
                        {
                            try 
                            {
                                // Match by LUID if we have it
                                if (_selectedGpuLuid != null)
                                {
                                    string name = obj["Name"]?.ToString() ?? "";
                                    if (!name.Contains(_selectedGpuLuid)) continue;
                                }

                                if (obj["Temperature"] != null) temp = Convert.ToSingle(obj["Temperature"]);
                            }
                            finally { obj.Dispose(); }
                        }
                    }
                }
                catch { }

                if (temp > 0) 
                {
                    _lastGpuTemp = temp;
                    _lastGpuTempTime = DateTime.Now;
                    return temp;
                }

                // Method 3: NVIDIA WMI
                if (_isNvidiaSelected)
                {
                    try
                    {
                        using (var searcher = new ManagementObjectSearcher(@"root\cimv2\NV", "SELECT * FROM NV_GPU"))
                        using (var results = searcher.Get())
                        {
                            foreach (ManagementObject obj in results)
                            {
                                try 
                                {
                                    if (obj["GpuCoreTemp"] != null) temp = Convert.ToSingle(obj["GpuCoreTemp"]);
                                }
                                finally { obj.Dispose(); }
                            }
                        }
                    }
                    catch { }
                }

                if (temp > -1) 
                {
                    _lastGpuTemp = temp;
                    _lastGpuTempTime = DateTime.Now;
                    return temp;
                }

                _lastGpuTemp = -1;
                _lastGpuTempTime = DateTime.Now;

                return -1;
            }
            catch (Exception)
            {
                // Silently fail
            }
            return 0;
        }


        // --- D3DKMT P/Invokes for Temperature ---
        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3DKMT_OPENADAPTERFROMLUID
        {
            public LUID AdapterLuid;
            public uint hAdapter;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3DKMT_CLOSEADAPTER
        {
            public uint hAdapter;
        }

        private enum KMTQUERYADAPTERINFOTYPE
        {
            KMTQAITYPE_ADAPTERPERFDATA = 35
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3DKMT_QUERYADAPTERINFO
        {
            public uint hAdapter;
            public KMTQUERYADAPTERINFOTYPE Type;
            public IntPtr pPrivateDriverData;
            public uint PrivateDriverDataSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3DKMT_ADAPTER_PERFDATA
        {
            public uint ThermalThrottling;
            public ulong CurrentFrequency;
            public ulong MaxFrequency;
            public ulong MaxFrequencyOC;
            public ulong MemoryFrequency;
            public ulong MemoryFrequencyOC;
            public uint FanSpeed;
            public uint Temperature; // Deci-Celsius
            public uint Voltage;
            public uint MemoryUsage;
            public uint MaxMemoryUsage;
            public ulong CoreClock;
            public ulong MemoryClock;
        }

        [DllImport("gdi32.dll")]
        private static extern int D3DKMTOpenAdapterFromLuid(ref D3DKMT_OPENADAPTERFROMLUID pData);

        [DllImport("gdi32.dll")]
        private static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO pData);

        [DllImport("gdi32.dll")]
        private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER pData);

        public void Dispose()
        {
            try
            {
                // Unsubscribe first — prevents InitializeGpu/Disk being called on a disposed service
                _config.Config.PropertyChanged -= Config_PropertyChanged;
                _timer?.Stop();
                _timer?.Dispose();
                StopSmiReader();
                _cpuCounter.Dispose();
                _diskCounter?.Dispose();
                foreach (var c in _gpuCounters.Values) c.Dispose();
            }
            catch { }
        }
    }
}
