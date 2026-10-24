#!/usr/bin/env bash
# Builds the macOS/Linux desktop app and packs it for a release.
#   package.sh <rid> <version> <output folder>
# osx-arm64 / osx-x64 (run on macOS): ScopeDeck.app, ad-hoc signed, in ScopeDeck-<version>-macos-<arch>.zip
# linux-x64 / linux-arm64: a ScopeDeck folder in ScopeDeck-<version>-linux-<arch>.tar.gz
set -euo pipefail
rid=$1 version=$2 out=$(mkdir -p "$3" && cd "$3" && pwd)
here=$(cd "$(dirname "$0")" && pwd)
work=$(mktemp -d)
arch=${rid#*-}

case $rid in
  osx-*)
    app="$work/ScopeDeck.app"
    mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
    # Not a single file: every native library in the bundle is signed in place (Apple silicon runs no unsigned code).
    dotnet publish "$here" -c Release -r "$rid" --self-contained true -p:Version="$version" -o "$app/Contents/MacOS"
    rm -f "$app/Contents/MacOS/app.png"
    cp "$here/app.icns" "$app/Contents/Resources/ScopeDeck.icns"
    cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>ScopeDeck</string>
  <key>CFBundleDisplayName</key><string>ScopeDeck</string>
  <key>CFBundleIdentifier</key><string>io.github.scopedeck</string>
  <key>CFBundleExecutable</key><string>ScopeDeck</string>
  <key>CFBundleIconFile</key><string>ScopeDeck</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>${version%%-*}</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSLocalNetworkUsageDescription</key><string>ScopeDeck connects to your oscilloscope on the local network.</string>
</dict>
</plist>
EOF
    # Ad-hoc signature: no Apple developer certificate, so Gatekeeper asks once (see README).
    codesign --force --deep --sign - "$app"
    ditto -c -k --keepParent "$app" "$out/ScopeDeck-$version-macos-$arch.zip"
    ;;
  linux-*)
    dir="$work/ScopeDeck"
    dotnet publish "$here" -c Release -r "$rid" --self-contained true -p:Version="$version" -o "$dir" \
      -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
    tar -czf "$out/ScopeDeck-$version-linux-$arch.tar.gz" -C "$work" ScopeDeck
    ;;
  *) echo "unsupported runtime $rid" >&2; exit 1 ;;
esac
rm -rf "$work"
