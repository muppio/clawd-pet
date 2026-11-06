#!/bin/zsh
# Makes the prebuilt app that ships inside the plugin, so people can install
# Clawd without Xcode: app/dist/ClawdPet.zip (Apple silicon + Intel) and
# app/dist/VERSION (the build id /pet compares with the installed app's).
# Run it after changing ClawdPet.swift or Info.plist, before publishing.
set -euo pipefail

here="${0:A:h}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

/bin/zsh "$here/build.sh" --universal --out "$work"
mkdir -p "$here/dist"
rm -f "$here/dist/ClawdPet.zip"
ditto -c -k --keepParent "$work/Clawd Pet.app" "$here/dist/ClawdPet.zip"
cp "$work/Clawd Pet.app/Contents/Resources/build-id" "$here/dist/VERSION"
echo "Released build $(cat "$here/dist/VERSION"): $here/dist/ClawdPet.zip ($(du -h "$here/dist/ClawdPet.zip" | cut -f1))"
lipo -archs "$work/Clawd Pet.app/Contents/MacOS/ClawdPet"
