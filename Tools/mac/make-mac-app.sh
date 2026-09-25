#!/usr/bin/env bash
#
# Builds the Mac download: "The Timeline Is.app" inside a .zip, one per
# architecture. Runs anywhere .NET runs — Linux, Windows or a Mac — because
# nothing here is an Apple tool. The .icns is committed, the .app is a plain
# folder, and the .zip is a plain zip.
#
#   Tools/mac/make-mac-app.sh              both architectures
#   Tools/mac/make-mac-app.sh arm64        just Apple Silicon
#
# Out: dist/TheTimelineIs-mac-<arch>.zip
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DIST="$ROOT/dist"
VERSION="$(sed -n 's/^version *= *//p' "$ROOT/Content/Text/Strings.txt" | head -1)"
VERSION="${VERSION:-0.0}"

ARCHES=("${@:-arm64 x64}")
read -r -a ARCHES <<< "${ARCHES[*]}"

echo "The Timeline Is — $VERSION"

for ARCH in "${ARCHES[@]}"; do
  RID="osx-$ARCH"
  STAGE="$DIST/$RID"
  APP="$STAGE/The Timeline Is.app"

  echo
  echo "=== $RID ==="
  rm -rf "$STAGE"
  mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

  # Self-contained: the player needs no .NET installed, and no internet.
  dotnet publish "$ROOT/Desktop" \
    -c Release -r "$RID" --self-contained true \
    -p:DebugType=none -p:DebugSymbols=false \
    -o "$APP/Contents/MacOS" >/dev/null

  sed "s/__VERSION__/$VERSION/g" "$ROOT/Tools/mac/Info.plist" > "$APP/Contents/Info.plist"
  printf 'APPL????' > "$APP/Contents/PkgInfo"
  cp "$ROOT/Desktop/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"
  cp "$ROOT/Tools/mac/READ ME FIRST.txt" "$STAGE/READ ME FIRST.txt"

  # The one thing a zip can lose. Finder will not launch a bundle whose
  # executable is not executable, and the error it gives says nothing useful.
  chmod +x "$APP/Contents/MacOS/TheTimelineIs"
  find "$APP/Contents/MacOS" -name '*.dylib' -exec chmod +x {} +

  ZIP="$DIST/TheTimelineIs-mac-$ARCH.zip"
  rm -f "$ZIP"
  ( cd "$STAGE" && zip -qry "$ZIP" "The Timeline Is.app" "READ ME FIRST.txt" )
  echo "wrote $ZIP  ($(du -h "$ZIP" | cut -f1))"
done

echo
echo "Unsigned, so the first launch needs one trip through"
echo "System Settings > Privacy & Security > Open Anyway. READ ME FIRST.txt says how."
