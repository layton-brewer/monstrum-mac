#!/bin/zsh
# Builds Monstrum-Mac.dll from src/ and zips the Thunderstore package. Run on a Mac with Valheim and
# BepInExPack_Valheim installed:
#   zsh make-release.sh
# Set VALHEIM_DIR if Valheim is not in the default Steam folder.
set -euo pipefail
HERE="${0:A:h}"
V="${VALHEIM_DIR:-$HOME/Library/Application Support/Steam/steamapps/common/Valheim}"
M="$V/Valheim.app/Contents/Game/valheim.app/Contents/Resources/Data/Managed"
DOTNET="$(command -v dotnet || echo "$HOME/.dotnet/dotnet")"
B="$HERE/build"
PKG="$HERE/Monstrum_Mac"

mkdir -p "$B"
cp "$HERE/Monstrum-Mac.csproj" "$HERE/src/"*.cs "$B/"
(cd "$B" && "$DOTNET" build -c Release -p:GameDir="$V" -p:ManagedDir="$M")

mkdir -p "$PKG/plugins"
cp "$B/bin/Release/net472/Monstrum-Mac.dll" "$PKG/plugins/"
[ -f "$PKG/icon.png" ] || { echo "Add a 256x256 icon.png to $PKG first (Thunderstore requires one)" >&2; exit 1; }
VER=$(/usr/bin/python3 -c "import json,sys; print(json.load(open(sys.argv[1]))['version_number'])" "$PKG/manifest.json")
ZIP="$HERE/Monstrum_Mac-$VER.zip"
rm -f "$ZIP"
(cd "$PKG" && zip -qr "$ZIP" manifest.json icon.png README.md CHANGELOG.md plugins config)
echo "Package: $ZIP"
