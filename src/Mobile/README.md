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
4. Switch the phone to cellular and verify that it falls back to the remote address. Check that the desktop reports a successful external connection. Rejoin the PC’s Wi-Fi. If the remote stream is still connected, the app offers to switch once it can reach the PC locally. If the stream disconnected, the next connection tries the local address first.
5. Revoke the phone from the desktop and confirm that the stream disconnects and cannot reconnect.

The preview shows current sensor readings and active warnings. Enable **Warning notifications** in mobile Settings to get system banners while the app is open and connected.

To test a notification without creating a real fault, pair with the Debug service first. Then run a simulation in the Debug desktop app and trigger a warning in its debug console. The phone shows the notification and the Warnings entry under **Simulation**. The release service never accepts simulated warnings.

In **Warnings → History**, open a saved service warning or Safeguard+ execution to view its details and captured readings. The desktop **Mobile Access** settings control warning retention. Safeguard+ executions use Safeguard+ History retention. Desktop Debug Console messages are session-only.

Under mobile **Settings**, **Test remote access** checks the saved remote address without switching the live connection. To verify access from outside your home network, run the test using mobile data. **Temperature units** applies to live readings and captured incident readings.

In **Home screen**, add or remove metric and pin panels, then use the up and down arrows to order them. A pin panel follows the selected device and connector. Add panels from multiple reporting plugins to view them together. Under **MSI PSU**, enable **Hide connectors with zero current** to hide those connectors in Power until they start drawing current.

**Warning filters** and **Lock-screen privacy** apply to phone notifications, while warnings and incident history remain visible in the app. To save changes while lock-screen delivery is enabled, connect to the updated Windows service so it can apply the phone’s preferences.

## Lock-screen notifications

Create an Expo project and configure push credentials for the native application ID `com.rtxlens.mobile`. Follow the [Expo push setup](https://docs.expo.dev/push-notifications/push-notifications-setup/) to configure Apple Push Notification service (APNs) or Firebase Cloud Messaging (FCM). Build with these environment variables:

- `RTXLENS_EXPO_PROJECT_ID` is the Expo project UUID.
- `RTXLENS_IOS_PUSH=1` enables the iOS push entitlement. Sign with an Apple Developer Program provisioning profile that supports APNs. Set `RTXLENS_IOS_PUSH_ENVIRONMENT=production` when signing with a distribution profile. The free AltStore preview continues to use foreground notifications.
- `RTXLENS_GOOGLE_SERVICES_FILE` is the path to the Android app’s Firebase configuration file. Configure the matching FCM V1 credential in Expo.

In a push-enabled build, pair the phone and enable **Lock-screen notifications** in mobile Settings. Under **Configuration → Mobile Access** on the PC, select **Send test** next to the paired phone. Lock the phone and check that the alert arrives. Use a Debug simulation to verify the warning path. Tapping a real warning opens its incident report once the phone reconnects to the PC.

The Windows service sends Expo the push token, alert title, and incident ID when available. With **Hide warning title** selected, the title is generic. Sensor streams and incident reports still travel directly between the phone and the PC.

## Checks on Windows

From this directory, run `npm ci`, `npm run typecheck`, and `npm test`. The Actions workflow compiles and packages the native targets.
