# RTX Lens mobile preview

The iOS and Android builds use the same Expo app in this directory. Each platform has a small native transport module for verifying the PC’s public key before sending pairing or stream credentials.

## Build artifacts

Once the workflow is on the default branch, open **Actions → Mobile App - Build → Run workflow** and select a branch containing the mobile app. Every run builds the iOS IPA and Android APK. Download the platform artifact from the completed run and extract it.

- **iPhone**. Open RTX-Lens-unsigned.ipa with [AltStore Classic](https://faq.altstore.io/altstore-classic/how-to-install-altstore-windows) to sign and install it with your Apple Account. Enable Developer Mode when prompted. With free signing, refresh the installation every seven days.
- **Android**. Open RTX-Lens-preview.apk on the phone and allow installation from the app that opened the file if Android asks. This APK uses test signing.

## Store builds

Both publishing checkboxes are off by default. You can select either or both when starting a build.

| Checkbox | Additional result |
| --- | --- |
| **Publish to App Store (TestFlight)** | Uploads a signed iOS build to App Store Connect for TestFlight |
| **Publish to Play Store (Internal testing)** | Saves a signed AAB and uploads it to the Google Play Internal testing track |

Store builds use the GitHub workflow run number for the iOS build number and Android version code. Dispatch a fresh run if a build number already exists in a store. Manage tester access, public review, and production releases in each store console.

Before public review, prepare the store listing, screenshots, privacy policy, and store-specific privacy declarations. Provide review instructions and a way to exercise the app's connected views, which normally require pairing with the RTX Lens Windows service.

Both stores use `com.rtxlens.mobile` as the app identifier. Create the named GitHub environments below and store credentials in their **Environment secrets**. Limit those environments to branches you trust for distribution.

### Google Play

1. [Register for Play Console](https://support.google.com/googleplay/android-developer/answer/6112435) and complete its verification. A new personal account also requires Android device verification. For public production access, new personal accounts must complete a closed test with at least 12 testers opted in continuously for 14 days.
2. Create **RTX Lens** as an Android app, set up [Play App Signing](https://developer.android.com/studio/publish/app-signing), and create a dedicated upload keystore. Back up the keystore and passwords securely.
3. Add the upload key settings to the `google-play` environment and run the workflow with **Publish to Play Store (Internal testing)** checked. Download the AAB artifact and upload it to **Internal testing** in Play Console. The automated upload step requires the service account in the next step.
4. [Enable the Google Play Developer API](https://developers.google.com/android-publisher/getting_started) in a Google Cloud project. Create a service account, invite it in Play Console, and give it app-level access to release to testing tracks for RTX Lens. Add its JSON key to the `google-play` environment for subsequent uploads.

| Environment secret | Value |
| --- | --- |
| `PLAY_UPLOAD_KEYSTORE_B64` | Base64 of the private upload keystore file |
| `PLAY_UPLOAD_STORE_PASSWORD` | Keystore password |
| `PLAY_UPLOAD_KEY_ALIAS` | Alias of the upload key |
| `PLAY_UPLOAD_KEY_PASSWORD` | Upload key password |
| `PLAY_SERVICE_ACCOUNT_JSON` | Raw service account JSON for automated uploads |

For push-enabled Android store builds, also add the raw Firebase `google-services.json` as the `GOOGLE_SERVICES_JSON` environment secret and the Expo project ID as the `RTXLENS_EXPO_PROJECT_ID` environment variable. Use the same Expo project and FCM credentials described below.

### App Store Connect

1. Enroll in the [Apple Developer Program](https://developer.apple.com/programs/enroll/). Register the explicit bundle ID and create the app record in App Store Connect.
2. Create an Apple Distribution certificate with its private key and a matching [App Store Connect provisioning profile](https://developer.apple.com/help/account/provisioning-profiles/create-an-app-store-provisioning-profile). Export the certificate and private key as a password-protected `.p12`.
3. Create a [team API key](https://developer.apple.com/documentation/appstoreconnectapi/creating-api-keys-for-app-store-connect-api) with upload permission. Store its credentials and the signing files in the `app-store-connect` environment. Run the workflow with **Publish to App Store (TestFlight)** checked and wait for the build to process in App Store Connect.

| Environment secret | Value |
| --- | --- |
| `IOS_DISTRIBUTION_P12_B64` | Base64 of the Apple Distribution `.p12` containing the private key |
| `IOS_DISTRIBUTION_P12_PASSWORD` | Password for the `.p12` |
| `IOS_APP_STORE_PROFILE_B64` | Base64 of the App Store Connect `.mobileprovision` |
| `IOS_ASC_KEY_ID` | App Store Connect team API key ID |
| `IOS_ASC_ISSUER_ID` | App Store Connect API issuer ID |
| `IOS_ASC_PRIVATE_KEY` | Raw App Store Connect `.p8` private key |

For a push-enabled iOS store build, set environment variables `RTXLENS_IOS_PUSH=1` and `RTXLENS_EXPO_PROJECT_ID` and use a provisioning profile with the production push entitlement. Add the APNs credentials in Expo as described below.

## Test the connection

1. Run the RTX Lens service and desktop build with Mobile Access support. Enable it and create a pairing code under **Configuration → Mobile Access**.
2. On the phone, join the same Wi-Fi as the PC, scan the code, and approve the phone in the desktop app.
3. Compare the Power and GPU tabs with the desktop readings. In Power, switch between connected sources and pin current, wattage, and voltage where supported.
4. Switch the phone to cellular and verify that it falls back to the remote address. Check that the desktop reports a successful external connection. Rejoin the PC’s Wi-Fi. If the remote stream is still connected, the app offers to switch once it can reach the PC locally. If the stream disconnected, the next connection tries the local address first.
5. Revoke the phone from the desktop and confirm that the stream disconnects and cannot reconnect.

The preview shows current sensor readings and active warnings. Enable **Warning notifications** in mobile Settings to get system banners while the app is open and connected.

To test a notification without creating a real fault, pair with the Debug service first. Then run a simulation in the Debug desktop app and trigger a warning in its debug console. The phone shows the notification and the Warnings entry under **Simulation**. The release service never accepts simulated warnings.

In **Warnings → History**, open a saved service warning or Safeguard+ execution to view its details and captured readings. The desktop **Mobile Access** settings control warning retention. Safeguard+ executions use Safeguard+ History retention. Desktop Debug Console messages are session-only.

The desktop pairing QR code supplies the initial local and remote addresses. Edit either under **Settings → PC connection** if it changes. The phone keeps verifying the paired PC’s key. Under mobile **Settings**, **Test remote access** checks the saved remote address without switching the live connection. To verify access from outside your home network, run the test using mobile data. **Temperature units** applies to live readings and captured incident readings.

Under **Settings → Appearance**, tap **Edit home page**. You can also touch and hold on Home. Open **+ Panels** to add or remove panels. Drag a panel by its handle to preview its new position, then tap **OK** to save and stay on Home or **Cancel** to discard your changes. A pin panel follows the selected device and connector. Add panels from multiple reporting plugins to view them together. Under **MSI PSU**, enable **Hide connectors with zero current** to hide those connectors in Power until they start drawing current.

**Warning filters** and **Lock-screen privacy** apply to phone notifications, while warnings and incident history remain visible in the app. Changes save automatically on the phone. Offline changes sync to the PC when it reconnects. Under **Appearance**, tap **Color scheme** to preview and select a palette. Switch between dark and light above it, or choose **Custom** to use the color picker.

## Lock-screen notifications

Create an Expo project and configure push credentials for the native application ID `com.rtxlens.mobile`. Follow the [Expo push setup](https://docs.expo.dev/push-notifications/push-notifications-setup/) to configure Apple Push Notification service (APNs) or Firebase Cloud Messaging (FCM). Build with these environment variables:

- `RTXLENS_EXPO_PROJECT_ID` is the Expo project UUID.
- `RTXLENS_IOS_PUSH=1` enables the iOS push entitlement. Sign with an Apple Developer Program provisioning profile that supports APNs. Set `RTXLENS_IOS_PUSH_ENVIRONMENT=production` when signing with a distribution profile. The free AltStore preview continues to use foreground notifications.
- `RTXLENS_GOOGLE_SERVICES_FILE` is the path to the Android app’s Firebase configuration file. Configure the matching FCM V1 credential in Expo.

In a push-enabled build, pair the phone and enable **Lock-screen notifications** in mobile Settings. Under **Configuration → Mobile Access** on the PC, select **Send test** next to the paired phone. Lock the phone and check that the alert arrives. Use a Debug simulation to verify the warning path. Tapping a real warning opens its incident report once the phone reconnects to the PC.

The Windows service sends Expo the push token, alert title, and incident ID when available. With **Hide warning title** selected, the title is generic. Sensor streams and incident reports still travel directly between the phone and the PC.

## Checks on Windows

From this directory, run `npm ci`, `npm run typecheck`, and `npm test`. The Actions workflow compiles and packages the native targets.
