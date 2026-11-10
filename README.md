<p align="center">
  <img src="https://rtxlens.com/favicon.svg" alt="" width="72" height="72">
</p>

<h1 align="center">RTX Lens</h1>

<p align="center">
  <strong>GPU power, pin currents, and temperatures at a glance.</strong>
</p>

<p align="center">
  <a href="https://rtxlens.com/download"><img src="https://img.shields.io/badge/Download-Windows%20x64-1ad1b9?style=for-the-badge" alt="Download RTX Lens for Windows"></a>
  <a href="https://rtxlens.com/"><img src="https://img.shields.io/badge/View-Screenshots-236779?style=for-the-badge" alt="View screenshots on the RTX Lens website"></a>
  <a href="https://rtxlens.com/support"><img src="https://img.shields.io/badge/Support-RTX%20Lens-db4e79?style=for-the-badge" alt="Support RTX Lens"></a>
</p>

RTX Lens is a Windows desktop app and an optional Corsair Xeneon Edge widget for monitoring your GPU, power connector, and PSU. See how the load is shared across the connector pins, record what happens under load, and set alerts for the readings you care about.

## What you can do

- **Watch live telemetry.** See connector power, voltage, and per-pin current on supported hardware. Compare readings from the GPU or WireView with readings from the MSI PSU.
- **Record and review sessions.** Pick the metrics and hardware sources to plot, zoom into a spike, and export an interactive HTML chart. Open saved recordings in the app to review them later.
- **Set warnings for each device.** Configure thresholds and optional reminders for supported sources, with an opt-in shutdown after a warning persists.
- **Set an MSI PSU fan curve.** Draw a temperature and fan speed curve with up to six points, and see the live temperature, RPM, and duty while you edit.
- **Configure MSI Afterburner Safeguard+.** Set independent alarms for each PSU connector, back up settings, and choose an emergency low-power profile or a shutdown action with optional email. Safeguard+ History shows RTX Lens script executions and sensor snapshots.
- **Monitor on Xeneon Edge.** Arrange and customize live charts and readings in the included read-only iCUE widget.

## Supported hardware

| Hardware | Readings |
| --- | --- |
| Desktop NVIDIA GeForce RTX GPUs | Clocks, core voltage, GPU and VRAM usage, temperatures, and fans where reported |
| ASUS ROG Astral RTX 5080 and 5090 | GPU-side per-pin connector readings |
| Thermal Grizzly WireView Pro II | Per-pin connector readings, cable rating, temperatures, and faults |
| MSI MPG Ai1300TS and Ai1600TS | PSU-side per-pin current for both connectors, temperature, and fan status |

Sensor availability varies by device. All listed hardware has been tested on physical devices, including the MSI Ai1600TS and the WireView Pro II Noctua Edition. A community member tested WireView support.

## Install

1. Get the ZIP from the [RTX Lens download page](https://rtxlens.com/download), extract it, and run `RTXLens-Setup-<version>-win-x64.exe`.
2. Install the monitoring service and select the desktop app if you want to configure settings or record sessions.
3. For Xeneon Edge, import `rtxlens.icuewidget` separately in iCUE. [Widget setup guide](src/Widget/INSTALL.md)

Requires 64-bit Windows 11 or Windows 10 build 17763 or later. [Setup and compatibility FAQ](https://rtxlens.com/faq)

## Support RTX Lens

Sponsorship helps fund development and testing on real GPUs, power supplies, and monitoring hardware. Starring the repo also helps people find RTX Lens.

**[Sponsor on GitHub](https://github.com/sponsors/Codeh4ck)** · **[Buy me a coffee](https://buymeacoffee.com/codehack)** · [Support page](https://rtxlens.com/support)

<details>
<summary>Build from source</summary>

Build on Windows with the .NET 10 SDK and PowerShell 7. Widget packaging also requires Node.js 22.18 or later, npm, and the `icuewidget` CLI on your PATH.

Open `RTXLens.slnx` in Rider or Visual Studio, or build the complete release from the repository root.

```powershell
./scripts/Build-Release.ps1
```

The script reads the version from `version.json`, runs the checks, and writes the installer and widget bundle under `.dist/releases/`.

</details>

## License

[LGPL-3.0](LICENSE.md) · [Installer notices](installer/Resources/THIRD-PARTY-NOTICES.txt) · [Widget notices](src/Widget/THIRD-PARTY-NOTICES.md)
