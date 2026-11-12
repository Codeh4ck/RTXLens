# RTX Lens mobile preview

The iOS and Android builds use the same Expo app in this directory. Each platform has a small native transport module for verifying the PC’s public key before sending pairing or stream credentials.

## Build artifacts

Push the mobile files on the feature/mobile-app branch to run the Mobile preview builds workflow. Once the workflow is on the default branch, it can also be run manually from the Actions tab. Download the platform artifact from the completed run and extract it.

- **iPhone**. Open RTX-Lens-unsigned.ipa with [AltStore Classic](https://faq.altstore.io/altstore-classic/how-to-install-altstore-windows) to sign and install it with your Apple Account. Enable Developer Mode when prompted. With free signing, refresh the installation every seven days.
- **Android**. Open RTX-Lens-preview.apk on the phone and allow installation from the app that opened the file if Android asks. This APK uses test signing.

## Test the connection

1. Run the RTX Lens service and desktop build with Mobile Access support. Enable it and create a pairing code under **Configuration → Mobile Access**.
2. On the phone, join the same Wi-Fi as the PC, scan the code, and approve the phone in the desktop app.
3. Confirm the frame number advances and readings match the PC.
4. For the external connection check, switch the phone to cellular and select **Away**. Verify the desktop reports a successful external connection.
5. Revoke the phone from the desktop and confirm that the stream disconnects and cannot reconnect.

The preview shows current sensor readings and active warnings. Route selection is manual for testing Wi-Fi and cellular.

## Checks on Windows

From this directory, run `npm ci`, `npm run typecheck`, and `npm test`. The Actions workflow compiles and packages the native targets.
