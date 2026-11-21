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

RTX Lens shows how your GPU, its power connector, and your PSU behave under load. The Windows service collects readings and runs your configured protection. Use the desktop app to configure hardware, compare pins, and record a session. You can also follow live readings on a Corsair Xeneon Edge or a paired Android phone or iPhone.

## Download and install

The monitoring PC requires 64-bit Windows 11 or Windows 10 build 17763 or later. Choose a file from the [latest GitHub release](https://github.com/Codeh4ck/RTXLens/releases/latest).

| What you want | Release file | How to install |
| --- | --- | --- |
| Windows desktop app and service | `RTXLens-Setup-<version>-win-x64.exe` | Run setup, approve the Windows administrator prompt, and keep the desktop app selected to configure hardware or record sessions. |
| Windows installer and Xeneon Edge widget | `RTXLens-win-x64.zip` | Extract the ZIP and run setup. In iCUE, import `rtxlens.icuewidget` and assign it to a Xeneon Edge slot. |
| Android companion | `RTX-Lens.apk` | Open the APK on your phone and allow installation from that source if Android asks. |
| iPhone companion | `RTX-Lens.ipa` | Import the unsigned IPA into AltStore Classic to sign and install it with your Apple Account. |

**Upgrading an existing installation?** Exit RTX Lens from its system tray menu and run the new installer. Setup preserves your settings. If you use the widget, import the updated `.icuewidget` file in iCUE as a separate step.

## First run

1. Open **RTX Lens** from the Windows Start menu. Under **Configuration**, check that the plugin for your hardware is enabled. Select **Apply** after making changes.
2. Open the device’s dashboard tab once it starts reporting. To capture a gaming or benchmark session, open **Performance Chart** and choose **New recording**.
3. To pair a phone, connect it to the same network as the PC. On the PC, open **Configuration → Mobile Access**, enable mobile access, select **Apply mobile access**, then **Create pairing code**. On the phone, open **Settings → Scan pairing code**. Scan the QR code and approve the device on the PC.

You can exit the desktop app and keep monitoring on the widget or phone. The Windows service continues collecting readings and running configured protection. Keep the desktop app running during a performance recording or when you want desktop notifications.

## Supported hardware

| Hardware | Where the readings come from | What you can see |
| --- | --- | --- |
| Desktop NVIDIA GeForce RTX GPUs | GPU telemetry | Clocks, core voltage, GPU and VRAM usage, temperatures, and fan speeds where reported. Individual pin currents require one of the sources below. |
| ASUS ROG Astral RTX 5080 and 5090 | GPU side of the power connector | See how current is shared across the connector pins at the graphics card. |
| Thermal Grizzly WireView Pro II | Inline power monitor at the GPU connector | Per-pin current, voltage and power, plus connector voltage, cable rating, temperatures, and device faults. |
| MSI MPG Ai1300TS and Ai1600TS | PSU side of each connector | Per-pin currents for both connectors, PSU temperature, and fan status. With an Astral or WireView Pro II, you can compare both ends of the cable. |

Sensor availability varies by device. All listed hardware has been tested on physical devices, including the MSI Ai1600TS and the WireView Pro II Noctua Edition. A community member tested WireView support.

## What you can do

- **Record and review sessions.** Pick metrics and hardware sources, zoom into a spike, export an interactive HTML chart, or reopen a saved recording.
- **Set warnings for each device.** Configure thresholds, reminders, and an optional shutdown if a warning persists.
- **Tune an MSI PSU fan curve.** Set up to six temperature and fan-duty points while viewing live temperature, RPM, and requested duty.
- **Configure MSI Afterburner Safeguard+.** Set independent alarms for each PSU connector, back up settings, and choose an emergency low-power profile or a shutdown action with optional email. Safeguard+ History shows script executions and sensor snapshots.
- **Monitor away from the desktop.** Customize live charts in the read-only Xeneon Edge widget, or check readings and warning history on your paired phone.

## Troubleshooting

| Symptom | First check |
| --- | --- |
| Desktop app cannot connect | Open Windows **Services** and check that **RTX Lens Service** is running. On a default installation the desktop app connects to `http://localhost:59273/`. |
| One device or reading is missing | Check the device’s plugin under **Configuration** and see whether another device is reporting. Use the hardware table above to check which device supplies the reading. |
| Xeneon Edge widget is blank | Confirm live readings appear in the desktop app on the same PC, then check that the widget is imported and assigned to a slot in iCUE. |

For a reproducible problem, include the RTX Lens version, Windows build, hardware model, and whether other devices still report. The read-only PowerShell diagnostic collector saves a report to your desktop. [Collect diagnostics](scripts/Collect-RTXLensDiagnostics.ps1) · [Report a bug](https://github.com/Codeh4ck/RTXLens/issues/new?template=bug_report.md) · [Setup and compatibility FAQ](https://rtxlens.com/faq)

## Support RTX Lens

Sponsorship helps fund development and testing on real GPUs, power supplies, and monitoring hardware. Starring the repo also helps people find RTX Lens.

**[Sponsor on GitHub](https://github.com/sponsors/Codeh4ck)** · **[Buy me a coffee](https://buymeacoffee.com/codehack)** · [Support page](https://rtxlens.com/support)

<details>
<summary>Build from source</summary>

Build on Windows with the .NET 10 SDK and PowerShell 7. From the repository root, run the desktop regression tests with this command.

```powershell
dotnet run --project tests/Desktop -c Release
```

To build the complete Windows release, install Node.js 22.18 or later, npm, and the `icuewidget` CLI. Browser verification uses Microsoft Edge by default.

```powershell
./scripts/Build-Release.ps1
```

The script reads `version.json`, runs the release checks, and writes the Windows installer, widget, and ZIP under `.dist/releases/<version>/`. For development, open `RTXLens.slnx` in Rider or Visual Studio. The Android and iOS packages are built separately through the [mobile build workflow](.github/workflows/mobile-app-build.yml).

</details>

## License

[LGPL-3.0](LICENSE.md) · [Installer notices](installer/Resources/THIRD-PARTY-NOTICES.txt) · [Widget notices](src/Widget/THIRD-PARTY-NOTICES.md)
