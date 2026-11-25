RTX LENS - WINDOWS x64

1. Extract this complete ZIP to a local folder.
2. Run RTXLens-Setup-<version>-win-x64.exe. Administrator approval is required.
3. Setup includes the monitoring service. The desktop app and PawnIO temperature driver are selected by default on a new installation. Clear either checkbox to skip it.
4. Import rtxlens.icuewidget manually through iCUE's Import Widget command.
5. Open RTX Lens from the Start menu to configure the backend. The app can be closed afterward.

The service listens only on localhost:59273 and starts automatically with Windows.
Stop older WireView/ASUS services using that port before installing.
An existing unmanaged RTXLensService registration must be removed before the first MSI installation.

The desktop app's Start with Windows option applies to your Windows user account.
Start minimized is a separate setting. Opening an installed app repairs an enabled startup entry pointing to an old portable folder.

UPDATING AN EXISTING INSTALLATION
Exit RTX Lens from its tray menu, then run the new setup EXE.
Import the included widget into iCUE separately. Both the updated app and
widget check rtxlens.com for future releases.

MODIFY / REPAIR / UNINSTALL
Use Windows Settings > Apps > RTX Lens, or run the same setup EXE again.
Modify lets you add/remove the desktop component without removing the service.
Uninstall removes the installed service, app, shortcuts, startup entries and RTX Lens's Afterburner backups.
Setup can reuse an installation folder that contains only saved profiles or leftover RTX Lens Afterburner backups.
Uninstall-RTXLens.ps1 is an optional launcher for the registered cached uninstaller.
Exit the desktop app using its tray menu first; unsaved configuration drafts are not forcibly discarded.

Backend settings (%ProgramData%\RTXLens), saved GPU chart and device warning profiles (Desktop\config),
and desktop preferences (%LocalAppData%\RTXLens) are retained during upgrade and uninstall.
The widget is imported and removed separately in iCUE.
Shared Microsoft redistributables are retained because other applications can use them.

The offline package bundles the .NET runtime and includes a signed Microsoft Visual C++ redistributable.
An unsigned development build is a release candidate, not a publicly signed release.
See release.json for component versions, source commit, signing status and build timestamp.
SHA256SUMS.txt verifies the extracted ZIP contents.


UPDATE CHECKS AND PUBLICATION
Desktop Settings and widget iCUE settings offer automatic update checks at startup
and Daily, Weekly or Monthly intervals. Checks open a release page; they do not
install automatically. Widget import remains manual. The service reports its own
running version at GET /version and does not poll GitHub.

Publish the release using the tag in version.json. Upload version.json as a separate
release asset alongside the ZIP. Keep the tagged repository manifest and uploaded
manifest identical; clients only advertise published stable releases.
