# kil0bit System Monitor

<div align="center">

<img src="logo.png" width="100" height="100" alt="kil0bit System Monitor" />

**The high-precision, low-latency system monitor for Windows 11.**  
Built with C#, WPF, and raw Win32 power  

[Download v3.0.0](https://github.com/kil0bit-kb/kil0bit-system-monitor/releases/latest) | [User Guide](GUIDE.md) | [Report Bug](https://github.com/kil0bit-kb/kil0bit-system-monitor/issues)

[![GitHub release](https://img.shields.io/github/v/release/kil0bit-kb/kil0bit-system-monitor?style=flat-square)](https://github.com/kil0bit-kb/kil0bit-system-monitor/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg?style=flat-square)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-blueviolet?style=flat-square)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)

</div>

---

## ✨ Why kil0bit System Monitor?

Kil0bit System Monitor is a modern successor to legacy taskbar monitors. It’s designed specifically for **Windows 11 power users** who need accurate, real-time metrics without the bloat of Electron or the overhead of high-level monitoring tools.

### 📊 Core Features
- **🚀 Ultra-Lightweight** — Dropped WinUI 3 for a lean WPF + Win32 engine. Tiny binary, zero bloat.
- **⚡ Ultra-Low Overhead** — Uses low-level Win32 APIs and GDI+ for near-zero CPU usage.
- **🖥️ Taskbar Integration** — Sits directly inside your taskbar. Always visible, never in your way.
- **📐 Flexible Layout** — Enable "Snap to Taskbar" for a native look, or disable it to **free-float** the overlay anywhere on your desktop.
- **🏠 High-Performance Dashboard** — A modern, snappy control center built for speed.
- **🎨 Pixel-Perfect Design** — Glassmorphism, Mica effects, and fully customizable themes.
- **🛡️ High-DPI Ready** — Precision rendering that looks sharp on 4K, Ultrawides, and multi-monitor setups.
- **⚙️ Power-User Settings** — Customize sensors (CPU, GPU, Network, Disk), colors, and smart startup behavior.

---

## ✨ v3.0.0 Gold Highlights
- **🚀 Multi-Disk Monitoring**: Track activity across C:, D:, and all your drives simultaneously with a dynamic 3x3 layout.
- **🎨 High-Res Branding**: Full 32-bit ARGB multi-resolution icons for a pixel-perfect Taskbar and Task Manager presence.
- **🛡️ Admin-Less Stability**: Access hardware telemetry without UAC elevation or system crashes.
- **⚡ .NET 8.0 Engine**: Re-engineered for maximum performance and ultra-low CPU overhead.
- **🏠 Refined Dashboard**: Modern, high-contrast Welcome screen with glassmorphism effects.

## 📸 Screenshots

### 🛠️ Professional Dashboard
![Settings Dashboard](Assets/preview/Screenshot%202026-04-28%20140004.png)

### 📊 Multi-Disk Monitoring & Settings
![Monitoring Config](Assets/preview/Screenshot%202026-04-28%20140019.png)

### 📈 Refined Overlay Modes

<div align="center">
  <p><b>Detailed Mode (Maximum Telemetry)</b></p>
  <img src="Assets/preview/Screenshot%202026-04-28%20140051.png" width="500" alt="Detailed Style" />
  <p><b>Compact Mode (Clean & Minimal)</b></p>
  <img src="Assets/preview/Screenshot%202026-04-28%20140119.png" width="500" alt="Compact Style" />
</div>

---

## 📥 Installation

### 1. Download the Installer
Head over to the [**Releases**](https://github.com/kil0bit-kb/kil0bit-system-monitor/releases) page and download:
- **`Kil0bitSystemMonitor-Setup.exe`**: The official high-performance installer.

### 2. Run & Enjoy
Launch the setup and follow the prompts. The app will automatically initialize the v3.0 overlay and open the high-contrast Welcome dashboard for your first configuration.

---

## 🔨 Build from Source

### Prerequisites
- **Visual Studio 2022** (17.10+) with the "Windows App Development" workload.
- **.NET 10.0 SDK**.
- **Windows 11** (recommended) or Windows 10 (Build 19041+).

### Steps
```powershell
# Clone the repository
git clone https://github.com/kil0bit-kb/kil0bit-system-monitor.git
cd kil0bit-system-monitor

# Build the project
dotnet build -c Release
```
The resulting executable will be in `kil0bit-system-monitor/bin/Release/net10.0-windows10.0.19041.0/win-x64/`.

---

## 🧰 The Tech Stack

| Layer | Technology |
|---|---|
| **Language** | C# 12 |
| **Framework** | WPF + ModernWPF |
| **Runtime** | .NET 8.0 |
| **Graphics** | Win32 GDI+ (BitBlt, AlphaBlend) |
| **Persistence** | JSON (`ConfigService`) |

---

## ❤️ Credits & Support

| Platform | Link |
|---|---|
| 📺 YouTube | [@kilObit](https://www.youtube.com/@kilObit) |
| ✍️ Blog | [kil0bit.blogspot.com](https://kil0bit.blogspot.com/) |
| ❤️ Support the Dev | [Patreon](https://www.patreon.com/cw/KB_kilObit) |

Built with ❤️ by **KB - kil0bit**.
