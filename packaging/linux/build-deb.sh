#!/bin/bash
# Build a .deb package for DMEdit.
# Usage: ./build-deb.sh [version]
#   version  — e.g. "0.1.0" (defaults to "0.0.0-dev")
#
# Produces:
#   dist/dmedit-linux-x64.deb

set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
VERSION="${1:-0.0.0-dev}"

DIST="$REPO_ROOT/dist"
STAGING="$DIST/deb-staging"
rm -rf "$DIST"
mkdir -p "$DIST"

echo "=== Publishing linux-x64 ==="
dotnet publish "$REPO_ROOT/src/DMEdit.App/DMEdit.App.csproj" \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -o "$DIST/publish-linux"

echo "=== Assembling .deb ==="

# App files
mkdir -p "$STAGING/opt/dmedit"
cp -R "$DIST/publish-linux/"* "$STAGING/opt/dmedit/"
chmod +x "$STAGING/opt/dmedit/dmedit"

# Icons
if [ -f "$REPO_ROOT/resources/text_editor.svg" ]; then
    cp "$REPO_ROOT/resources/text_editor.svg" "$STAGING/opt/dmedit/"
fi

# Symlink in PATH
mkdir -p "$STAGING/usr/local/bin"
# dpkg needs a relative symlink target
ln -s /opt/dmedit/dmedit "$STAGING/usr/local/bin/dmedit"

# Desktop entry
mkdir -p "$STAGING/usr/share/applications"
cat > "$STAGING/usr/share/applications/dmedit.desktop" << 'DESKTOP'
[Desktop Entry]
Name=DMEdit
Exec=/opt/dmedit/dmedit %F
Icon=/opt/dmedit/text_editor.svg
Type=Application
Categories=TextEditor;Development;
MimeType=text/plain;text/markdown;
Terminal=false
DESKTOP

# DEBIAN control file
mkdir -p "$STAGING/DEBIAN"
sed "s/__VERSION__/$VERSION/g" "$SCRIPT_DIR/control.template" > "$STAGING/DEBIAN/control"

# Build the .deb
DEB_PATH="$DIST/dmedit-linux-x64.deb"
dpkg-deb --build "$STAGING" "$DEB_PATH"

# Clean up staging
rm -rf "$STAGING" "$DIST/publish-linux"

echo ""
echo "Done: $DEB_PATH"
ls -lh "$DEB_PATH"
