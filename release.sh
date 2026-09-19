#!/bin/bash
# Builds Couch Launcher for Windows — right here on the Mac — and publishes it
# as a new GitHub release. The Beelink picks it up by itself the next time the
# launcher starts, or straight away from Settings › Check for updates.
#
#   ./release.sh           next version (1.0.4 -> 1.0.5)
#   ./release.sh 1.2.0     that exact version
#
# Needs: the .NET SDK in ~/.dotnet and the GitHub CLI (gh), signed in.
set -euo pipefail
cd "$(dirname "$0")"

export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$HOME/.local/bin:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

REPO_URL="$(gh repo view --json url -q .url)"
if [ $# -ge 1 ]; then
  VERSION="$1"
else
  LAST="$(gh release list --limit 1 --json tagName -q '.[0].tagName' 2>/dev/null || true)"
  if [ -z "$LAST" ]; then
    VERSION="1.0.0"
  else
    LAST="${LAST#v}"
    VERSION="${LAST%.*}.$(( ${LAST##*.} + 1 ))"
  fi
fi
echo "==> Couch Launcher $VERSION -> $REPO_URL"

# GitHub holds the release history; start the local copy fresh each time.
rm -rf build/publish build/releases
dotnet publish CouchLauncher.csproj -c Release -r win-x64 --self-contained true \
  -o build/publish -p:Version="$VERSION" -p:UpdateRepo="$REPO_URL"

dotnet tool restore
# The previous release, so this one can ship as a small "what changed" update.
# --channel win everywhere: on a Mac vpk otherwise assumes a Mac release.
dotnet vpk download github --repoUrl "$REPO_URL" --channel win -o build/releases || true
# "[win]" tells vpk to package for Windows from a Mac. Quoted so bash leaves it alone.
dotnet vpk "[win]" pack --packId CouchLauncher --packVersion "$VERSION" \
  --packDir build/publish --mainExe CouchLauncher.exe \
  --packTitle "Couch Launcher" --packAuthors "Couch Commander" -o build/releases
dotnet vpk upload github --repoUrl "$REPO_URL" --channel win -o build/releases --publish \
  --releaseName "Couch Launcher $VERSION" --tag "v$VERSION" --token "$(gh auth token)"

echo "==> Released $VERSION"
echo "    Installer: $REPO_URL/releases/latest/download/CouchLauncher-win-Setup.exe"
