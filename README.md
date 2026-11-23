<p align="center">
  <img src="assets/banner.png" alt="PCUptime — Live System Uptime, Hardware Profiler & Electricity Cost Estimator" width="100%" />
</p>

<h1 align="center">PCUptime</h1>

<p align="center">
  <strong>A tiny (~77 KB), zero-dependency native Windows utility that shows how long your PC has been running, auto-detects your hardware, and estimates your real electricity cost in real time.</strong>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011-0078D6?style=for-the-badge&logo=windows&logoColor=white" alt="Windows 10 / 11" />
  <img src="https://img.shields.io/badge/Size-77%20KB%20Single%20EXE-34D399?style=for-the-badge" alt="77 KB Single EXE" />
  <img src="https://img.shields.io/badge/Languages-EN%20%7C%20TR-F59E0B?style=for-the-badge" alt="English and Turkish" />
  <img src="https://img.shields.io/badge/License-MIT-38BDF8?style=for-the-badge" alt="MIT License" />
</p>

---

## Features

- **Live System Uptime Counter** — Reads Windows kernel counters (`GetTickCount64` + `QueryUnbiasedInterruptTime`) to display total hours, a live `DD : HH : MM : SS` breakdown, exact last boot timestamp, and active awake time excluding sleep/hibernation.
- **Automatic Hardware Detection** — Queries WMI asynchronously at startup (zero UI lag) to identify your **CPU** (model, core count & TDP), **GPU** (discrete vs. integrated & TDP), **Desktop vs. Laptop** form factor, **RAM**, **SSD/HDD count**, and **connected monitors**.
- **Real-Time Electricity Cost Estimation** — Converts detected hardware wattage into realistic wall power draw (factoring in PSU efficiency, platform overhead, and monitor consumption) across three 1-click usage profiles (**Light**, **Average**, **Gaming / Heavy Load**) and estimates total, hourly, and daily electricity cost.
- **Turkey (EPDK 2026) & Global Tariff Presets** — Includes built-in tax-inclusive Turkey EPDK 2026 residential tiers (**Low 3.24 ₺**, **Mixed 4.05 ₺**, **High 4.86 ₺**), global benchmarks (**US $0.17**, **EU €0.29**, **UK £0.28**), custom kWh price & currency (`TL`, `USD`, `EUR`, `GBP`), and optional real watt-meter override.
- **Instant Bilingual UI (`EN` / `TR`)** — Auto-detects Windows language on first launch and switches between English and Turkish with a single click (`TR | EN` pill or `L` key).
- **Native Windows 11 Look & Zero Bloat** — Dark immersive title bar (`DwmSetWindowAttribute`), Per-Monitor V2 DPI scaling, ~0% idle CPU usage, Always-On-Top pinning, system tray minimize with live hover tooltip, and 1-click clipboard summary export.
- **Zero Dependencies** — Single portable `.exe` (~77 KB) built directly against the inbox .NET Framework 4.x included with every Windows 10 & 11 machine.

---

## Screenshots

<table>
  <tr>
    <td align="center" width="50%">
      <img src="assets/screenshot-en.png" alt="English UI — USD tariff" width="100%" /><br />
      <sub><b>English UI</b> — US Avg ($0.17/kWh)</sub>
    </td>
    <td align="center" width="50%">
      <img src="assets/screenshot-tr.png" alt="Turkish UI — EPDK 2026 TL tariff" width="100%" /><br />
      <sub><b>Turkish UI</b> — Turkey EPDK 2026 Mixed (4.05 ₺/kWh)</sub>
    </td>
  </tr>
</table>

<p align="center">
  <img src="assets/screenshot-settings.png" alt="Electricity Tariff & Power Settings Dialog" width="420" /><br />
  <sub><b>Tariff & Power Settings</b> — Built-in EPDK 2026 & global presets, custom kWh rate, custom watt-meter override, and monitor toggle</sub>
</p>

---

## Download & Quick Start

1. Go to the **[Releases](https://github.com/rodvay/PCUptime/releases/latest)** page and download **`PCUptime.exe`**.
2. Double-click **`PCUptime.exe`** — no installer, no admin rights, and no extra files required.

> **Note about Windows SmartScreen:** Because `PCUptime.exe` is an independent open-source binary without a paid code-signing certificate, Windows Defender SmartScreen may show a warning on first run. Click **More info → Run anyway**, or inspect the single-file source code (`Program.cs`) and build it yourself in 2 seconds using `build.bat`.

---

## How Power & Cost Estimation Works

PCUptime estimates realistic **wall-outlet power consumption** rather than simply summing peak TDPs:

1. **Component Profiling:** Matches your detected CPU and discrete/integrated GPU against an internal TDP table, adds platform overhead (motherboard, fans, RAM sticks, SSD/HDD drives), accounts for typical PSU efficiency (84%–89%), and optionally adds connected display power (~24–30 W per desktop monitor).
2. **Usage Profiles:**
   | Profile | Typical Workload | Estimated Load Scaling |
   | :--- | :--- | :--- |
   | **Light** | Idle, web browsing, office apps, video playback | Low CPU/GPU utilization |
   | **Average** *(default)* | Daily mixed desktop / multitasking / occasional load | Balanced real-world draw |
   | **Gaming / Heavy** | 3D gaming, rendering, sustained high load | High CPU/GPU sustained load |
3. **Sleep-Aware Energy Math:**
   ```text
   Total kWh = (Awake Hours × Active Watts + Sleep Hours × 4 W) / 1000
   Total Cost = Total kWh × Price per kWh
   ```
   Using `QueryUnbiasedInterruptTime`, sleep/hibernation hours are calculated at standby (~4 W) instead of full active wattage.
4. **Custom Calibration:** Own a smart plug or Kill-A-Watt meter? Open the tariff pill and enter your exact measured wattage to override the automatic hardware model.

### Built-in Electricity Rate Presets

| Region / Tier | Rate | Currency | Notes |
| :--- | :---: | :---: | :--- |
| **Turkey — Low Tier** (`Düşük`) | `3.24` | `TL (₺)` | EPDK 2026 residential ≤ 240 kWh/month (tax incl.) |
| **Turkey — Mixed** (`Karma`) | `4.05` | `TL (₺)` | Midpoint residential household average (tax incl.) |
| **Turkey — High Tier** (`Yüksek`) | `4.86` | `TL (₺)` | EPDK 2026 residential > 240 kWh/month (tax incl.) |
| **US Average** | `$0.17` | `USD ($)` | National residential benchmark |
| **EU Average** | `€0.29` | `EUR (€)` | European residential benchmark |
| **UK Average** | `£0.28` | `GBP (£)` | UK residential price-cap benchmark |

All settings are persisted cleanly to `%APPDATA%\PCUptime\settings.ini`.

---

## Keyboard Shortcuts

| Shortcut | Action |
| :---: | :--- |
| `Ctrl + C` | Copy formatted uptime, electricity cost & hardware summary to clipboard |
| `T` | Toggle **Always On Top** |
| `H` | Minimize to **System Tray** (left-click tray icon to restore) |
| `L` | Switch language (**EN** ↔ **TR**) |

---

## Building from Source

No Visual Studio or external SDK is required — Windows 10 and 11 already include the C# compiler (`csc.exe`) in `.NET Framework 4.x`.

```bat
git clone https://github.com/rodvay/PCUptime.git
cd PCUptime
build.bat
```

Or run directly via PowerShell:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

This generates the embedded multi-resolution `app.ico` and compiles the standalone `PCUptime.exe` in ~1 second.

---

## Project Structure

```text
PCUptime/
├── Program.cs        # Complete single-file C# WinForms + GDI+ + WMI source
├── app.manifest      # Windows 10/11 compatibility & PerMonitorV2 DPI manifest
├── build.ps1         # Programmatic icon generator & csc.exe build script
├── build.bat         # One-click Windows build wrapper
├── assets/           # README banner & live UI screenshots
├── LICENSE           # MIT License
└── README.md
```

---

## Privacy & Security

- **100% Offline:** Makes zero network requests and sends zero telemetry.
- **No Elevation Needed:** Runs strictly as standard user (`asInvoker`).
- **Transparent Single-File Source:** All logic lives in [Program.cs](Program.cs) for easy auditing.

---

## License

This project is licensed under the **MIT License** — see the [LICENSE](LICENSE) file for details.
