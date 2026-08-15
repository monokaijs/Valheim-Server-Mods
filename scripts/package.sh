#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project_root="$(cd "$script_dir/.." && pwd)"
package_root="$project_root/package"
artifact_root="$project_root/artifacts"
version="$(node -p "require('$project_root/package/manifest.json').version_number")"

test -f "$package_root/BepInEx/config/XomNghienBootstrap/trusted-public-key.xml"
dotnet build "$project_root/src/XomNghien.Bootstrap/XomNghien.Bootstrap.csproj" -c Release -f net472
dotnet build "$project_root/src/XomNghien.RuntimeUpdater/XomNghien.RuntimeUpdater.csproj" -c Release
mkdir -p "$package_root/BepInEx/patchers" "$package_root/BepInEx/plugins/XomNghienBootstrap" "$artifact_root"
cp "$project_root/src/XomNghien.Bootstrap/bin/Release/net472/XomNghienBootstrap.dll" "$package_root/BepInEx/patchers/XomNghienBootstrap.dll"
cp "$project_root/src/XomNghien.RuntimeUpdater/bin/Release/net472/XomNghienRuntimeUpdater.dll" "$package_root/BepInEx/plugins/XomNghienBootstrap/XomNghienRuntimeUpdater.dll"
sips -z 256 256 "$project_root/../xom-nghien-web/apps/web/public/favicon.png" --out "$package_root/icon.png" >/dev/null

artifact="$artifact_root/XomNghien-XomNghienBootstrap-$version.zip"
rm -f "$artifact"
(cd "$package_root" && zip -qr "$artifact" manifest.json README.md CHANGELOG.md icon.png BepInEx)
shasum -a 256 "$artifact" > "$artifact.sha256"
echo "$artifact"
