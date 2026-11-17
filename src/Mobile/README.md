# RTX Lens mobile preview

The iOS and Android builds use the same Expo app in this directory. Each platform has a small native transport module for verifying the PC’s public key before sending pairing or stream credentials.

## Build artifacts

Push the mobile files on the feature/mobile-app branch to run the Mobile preview builds workflow. Once the workflow is on the default branch, it can also be run manually from the Actions tab. Download the platform artifact from the completed run and extract it.

- **iPhone**. Open RTX-Lens-unsigned.ipa with [AltStore Classic](https://faq.altstore.io/altstore-classic/how-to-install-altstore-windows) to sign and install it with your Apple Account. Enable Developer Mode when prompted. With free signing, refresh the installation every seven days.
- **Android**. Open RTX-Lens-preview.apk on the phone and allow installation from the app that opened the file if Android asks. This APK uses test signing.

## Test the connection

1. Run the RTX Lens service and desktop build with Mobile Access support. Enable it and create a pairing code under **Configuration → Mobile Access**.
2. On the phone, join the same Wi-Fi as the PC, scan the code, and approve the phone in the desktop app.
3. Compare the Power and GPU tabs with the desktop readings. In Power, switch between connected sources and pin current, wattage, and voltage where supported.
4. With **Automatic** connection mode selected, switch the phone to cellular and back to Wi-Fi. Check that the stream recovers and that the desktop reports a successful external connection. **Home Wi-Fi** and **Away** can force a route for troubleshooting.
5. Revoke the phone from the desktop and confirm that the stream disconnects and cannot reconnect.

The preview shows current sensor readings and active warnings. Enable **Warning notifications** in mobile Settings to get system banners while the app is open and connected.

To test a notification without creating a real fault, pair with the Debug service first. Then run a simulation in the Debug desktop app and trigger a warning in its debug console. The phone shows the notification and the Warnings entry under **Simulation**. The release service never accepts simulated warnings.

In **Warnings → History**, open a saved service warning or Safeguard+ execution to view its details and captured readings. The desktop **Mobile Access** settings control warning retention. Safeguard+ executions use Safeguard+ History retention. Desktop Debug Console messages are session-only.

## Checks on Windows

From this directory, run `npm ci`, `npm run typecheck`, and `npm test`. The Actions workflow compiles and packages the native targets.
