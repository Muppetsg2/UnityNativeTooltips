#!/usr/bin/env bash
# Builds NativeTooltipsMac.bundle (universal: Apple Silicon + Intel).
# Requires: macOS + Xcode Command Line Tools (xcode-select --install).
#
# Usage:
#   ./build_macos.sh                       -> ../NativeTooltipsMac.bundle (next to this folder)
#   ./build_macos.sh /path/to/Assets/Plugins/macOS/NativeTooltipsMac.bundle
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="${1:-$HERE/../NativeTooltipsMac.bundle}"
MIN_MACOS="${MIN_MACOS:-10.13}"

if [[ "$(uname)" != "Darwin" ]]; then
  echo "This script must be run on macOS." >&2
  exit 1
fi

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

xcrun clang++ \
  -x objective-c++ -std=c++17 -fobjc-arc -O2 -Wall \
  -arch arm64 -arch x86_64 \
  -mmacosx-version-min="$MIN_MACOS" \
  -fvisibility=hidden \
  -bundle \
  -framework AppKit -framework Foundation \
  -o "$TMP/NativeTooltipsMac" \
  "$HERE/NativeTooltipsMac.mm"

rm -rf "$OUT"
mkdir -p "$OUT/Contents/MacOS"
cp "$TMP/NativeTooltipsMac" "$OUT/Contents/MacOS/NativeTooltipsMac"

cat > "$OUT/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key><string>en</string>
  <key>CFBundleExecutable</key><string>NativeTooltipsMac</string>
  <key>CFBundleIdentifier</key><string>com.nativetooltips.mac</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleName</key><string>NativeTooltipsMac</string>
  <key>CFBundlePackageType</key><string>BNDL</string>
  <key>CFBundleShortVersionString</key><string>1.0.0</string>
  <key>CFBundleVersion</key><string>1</string>
</dict>
</plist>
PLIST

# Ad-hoc signature (for distribution you sign the whole app with your Developer ID anyway).
codesign --force --sign - "$OUT" >/dev/null

# Unity import settings (Standalone macOS only), if they don't exist yet.
if [[ ! -f "$OUT.meta" ]]; then
  GUID="$(uuidgen | tr -d '-' | tr 'A-Z' 'a-z')"
  cat > "$OUT.meta" <<META
fileFormatVersion: 2
guid: $GUID
folderAsset: yes
PluginImporter:
  externalObjects: {}
  serializedVersion: 2
  iconMap: {}
  executionOrder: {}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 0
  isExplicitlyReferenced: 0
  validateReferences: 1
  platformData:
  - first:
      Any: 
    second:
      enabled: 0
      settings: {}
  - first:
      Editor: Editor
    second:
      enabled: 0
      settings:
        DefaultValueInitialized: true
  - first:
      Standalone: OSXUniversal
    second:
      enabled: 1
      settings:
        CPU: AnyCPU
  userData: 
  assetBundleName: 
  assetBundleVariant: 
META
fi

lipo -info "$OUT/Contents/MacOS/NativeTooltipsMac"
echo "OK: $OUT"
