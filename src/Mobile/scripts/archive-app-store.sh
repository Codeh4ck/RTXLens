#!/usr/bin/env bash
set -euo pipefail

: "${IOS_DISTRIBUTION_P12_B64:?Set IOS_DISTRIBUTION_P12_B64 in the app-store-connect environment}"
: "${IOS_DISTRIBUTION_P12_PASSWORD:?Set IOS_DISTRIBUTION_P12_PASSWORD in the app-store-connect environment}"
: "${IOS_APP_STORE_PROFILE_B64:?Set IOS_APP_STORE_PROFILE_B64 in the app-store-connect environment}"

umask 077
signing="$(mktemp -d "$RUNNER_TEMP/rtxlens-ios-signing.XXXXXX")"
keychain="$signing/distribution.keychain-db"
profile_path=
cleanup() {
    if [[ -n "$profile_path" ]]; then
        rm -f -- "$profile_path"
    fi
    security delete-keychain "$keychain" >/dev/null 2>&1 || true
    rm -f -- "$keychain" "$signing/distribution.p12" "$signing/store.mobileprovision" "$signing/profile.plist" "$signing/export.plist"
    rmdir -- "$signing"
}
trap cleanup EXIT

printf '%s' "$IOS_DISTRIBUTION_P12_B64" | base64 -D > "$signing/distribution.p12"
printf '%s' "$IOS_APP_STORE_PROFILE_B64" | base64 -D > "$signing/store.mobileprovision"
security cms -D -i "$signing/store.mobileprovision" -o "$signing/profile.plist"
profile_uuid="$(/usr/libexec/PlistBuddy -c 'Print :UUID' "$signing/profile.plist")"
profile_name="$(/usr/libexec/PlistBuddy -c 'Print :Name' "$signing/profile.plist")"
team="$(/usr/libexec/PlistBuddy -c 'Print :TeamIdentifier:0' "$signing/profile.plist")"
app_id="$(/usr/libexec/PlistBuddy -c 'Print :Entitlements:application-identifier' "$signing/profile.plist")"
bundle_id=com.rtxlens.mobile
if [[ ! "$profile_uuid" =~ ^[a-fA-F0-9-]{36}$ || ! "$team" =~ ^[A-Z0-9]{10}$ || "$app_id" != "$team.$bundle_id" ]]; then
    echo "Use an App Store Connect provisioning profile for $bundle_id." >&2
    exit 1
fi

profiles="$HOME/Library/Developer/Xcode/UserData/Provisioning Profiles"
mkdir -p "$profiles"
profile_path="$profiles/$profile_uuid.mobileprovision"
cp "$signing/store.mobileprovision" "$profile_path"

keychain_password="$(openssl rand -hex 24)"
security create-keychain -p "$keychain_password" "$keychain"
security set-keychain-settings -lut 21600 "$keychain"
security unlock-keychain -p "$keychain_password" "$keychain"
security import "$signing/distribution.p12" -P "$IOS_DISTRIBUTION_P12_PASSWORD" -A -t cert -f pkcs12 -k "$keychain"
security set-key-partition-list -S apple-tool:,apple: -s -k "$keychain_password" "$keychain"
security list-keychains -d user -s "$keychain"
unset IOS_DISTRIBUTION_P12_B64 IOS_DISTRIBUTION_P12_PASSWORD IOS_APP_STORE_PROFILE_B64

workspace="$(find ios -maxdepth 1 -name '*.xcworkspace' -print -quit)"
test -n "$workspace"
scheme="$(basename "$workspace" .xcworkspace)"
export IOS_XCODE_PROJECT="ios/$scheme.xcodeproj" IOS_TEAM="$team" IOS_PROFILE="$profile_name"
ruby -rxcodeproj <<'RUBY'
project = Xcodeproj::Project.open(ENV.fetch('IOS_XCODE_PROJECT'))
targets = project.targets.select { |target| target.product_type == 'com.apple.product-type.application' }
abort 'Expected one iOS application target.' unless targets.size == 1

settings = targets.fetch(0).build_configurations.find { |config| config.name == 'Release' }.build_settings
settings['CODE_SIGN_STYLE'] = 'Manual'
settings['CODE_SIGN_IDENTITY'] = 'Apple Distribution'
settings['DEVELOPMENT_TEAM'] = ENV.fetch('IOS_TEAM')
settings['PROVISIONING_PROFILE_SPECIFIER'] = ENV.fetch('IOS_PROFILE')
project.save
RUBY

archive="$RUNNER_TEMP/rtxlens-store.xcarchive"
xcodebuild -workspace "$workspace" -scheme "$scheme" -configuration Release -sdk iphoneos \
    -destination 'generic/platform=iOS' -archivePath "$archive" archive

cat > "$signing/export.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>method</key><string>app-store-connect</string>
    <key>destination</key><string>export</string>
    <key>signingStyle</key><string>manual</string>
    <key>signingCertificate</key><string>Apple Distribution</string>
    <key>teamID</key><string>$team</string>
    <key>provisioningProfiles</key>
    <dict><key>$bundle_id</key><string>$profile_uuid</string></dict>
</dict>
</plist>
PLIST

xcodebuild -exportArchive -archivePath "$archive" -exportOptionsPlist "$signing/export.plist" \
    -exportPath "$RUNNER_TEMP/rtxlens-app-store"
ipa="$(find "$RUNNER_TEMP/rtxlens-app-store" -maxdepth 1 -name '*.ipa' -print -quit)"
test -n "$ipa"
unzip -t "$ipa" >/dev/null
mv "$ipa" "$RUNNER_TEMP/RTX-Lens-App-Store.ipa"
