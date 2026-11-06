#!/bin/zsh
# Builds Clawd Pet.app from source (needs Xcode's command line tools).
#
#   build.sh               this Mac's architecture, into ~/Applications, then launches it
#   build.sh --universal   Apple silicon + Intel in one app
#   build.sh --out <dir>   write the app into <dir> and don't launch it (release.sh uses this)
set -euo pipefail

here="${0:A:h}"
universal=0
out=""
while (( $# )); do
  case "$1" in
    --universal) universal=1 ;;
    --out) out="$2"; shift ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done

dest="${out:-$HOME/Applications}"
app="$dest/Clawd Pet.app"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

compile() { # <arch> <output>
  swiftc -O -swift-version 5 -parse-as-library -target "$1-apple-macosx14.0" -o "$2" "$here/ClawdPet.swift"
}

echo "Compiling…"
if (( universal )); then
  compile arm64 "$work/ClawdPet-arm64"
  compile x86_64 "$work/ClawdPet-x86_64"
  lipo -create -output "$work/ClawdPet" "$work/ClawdPet-arm64" "$work/ClawdPet-x86_64"
else
  compile "$(uname -m)" "$work/ClawdPet"
fi

echo "Drawing the icon…"
"$work/ClawdPet" --render-icon "$work/icon.png"
mkdir -p "$work/AppIcon.iconset"
for s in 16 32 128 256 512; do
  sips -z $s $s "$work/icon.png" --out "$work/AppIcon.iconset/icon_${s}x${s}.png" >/dev/null
  d=$((s * 2))
  sips -z $d $d "$work/icon.png" --out "$work/AppIcon.iconset/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$work/AppIcon.iconset" -o "$work/AppIcon.icns"

echo "Bundling…"
if [[ -z "$out" ]]; then
  pkill -x ClawdPet 2>/dev/null || true
  pkill -x ClaudePet 2>/dev/null || true # builds from before the rename
  rm -rf "$HOME/Applications/Claude Pet.app"
fi
mkdir -p "$dest"
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp "$work/ClawdPet" "$app/Contents/MacOS/ClawdPet"
cp "$here/Info.plist" "$app/Contents/Info.plist"
cp "$work/AppIcon.icns" "$app/Contents/Resources/AppIcon.icns"
# Which source this app was built from: /pet compares it with the prebuilt app's.
cat "$here/ClawdPet.swift" "$here/Info.plist" | shasum -a 256 | cut -c1-16 > "$app/Contents/Resources/build-id"
codesign --force --sign - "$app" >/dev/null 2>&1

if [[ -z "$out" ]]; then
  echo "Launching…"
  open "$app"
fi
echo "Done: $app"
