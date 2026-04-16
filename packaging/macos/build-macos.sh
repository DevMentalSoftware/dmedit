#!/bin/bash
# Build DMEdit .app bundles and .dmg disk images for macOS.
# Usage: ./build-macos.sh [version]
#   version  — e.g. "0.1.0" (defaults to "0.0.0-dev")
#
# Produces:
#   dist/dmedit-macos-x64.dmg
#   dist/dmedit-macos-arm64.dmg

set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
VERSION="${1:-0.0.0-dev}"
VERSION_SHORT="${VERSION%%-*}"  # strip prerelease suffix for CFBundleShortVersionString

DIST="$REPO_ROOT/dist"
rm -rf "$DIST"
mkdir -p "$DIST"

build_arch() {
    local rid="$1"         # osx-x64 or osx-arm64
    local arch_label="$2"  # x64 or arm64

    echo "=== Building $rid ==="

    local publish_dir="$DIST/publish-$arch_label"
    local app_dir="$DIST/DMEdit-$arch_label.app"
    local dmg_path="$DIST/dmedit-macos-$arch_label.dmg"

    # Publish self-contained
    dotnet publish "$REPO_ROOT/src/DMEdit.App/DMEdit.App.csproj" \
        -c Release \
        -r "$rid" \
        --self-contained true \
        -p:PublishReadyToRun=true \
        -o "$publish_dir"

    # Assemble .app bundle
    mkdir -p "$app_dir/Contents/MacOS"
    mkdir -p "$app_dir/Contents/Resources"

    # Copy published files into MacOS/
    cp -R "$publish_dir/"* "$app_dir/Contents/MacOS/"

    # Info.plist with version substituted
    sed -e "s/__VERSION__/$VERSION/g" \
        -e "s/__VERSION_SHORT__/$VERSION_SHORT/g" \
        "$SCRIPT_DIR/Info.plist" > "$app_dir/Contents/Info.plist"

    # Icon — use .icns if available, otherwise skip (app runs fine without)
    if [ -f "$SCRIPT_DIR/dmedit.icns" ]; then
        cp "$SCRIPT_DIR/dmedit.icns" "$app_dir/Contents/Resources/"
    elif command -v sips &> /dev/null && command -v iconutil &> /dev/null; then
        echo "  Generating .icns from SVG..."
        generate_icns "$app_dir/Contents/Resources/dmedit.icns"
    else
        echo "  Warning: No .icns icon available. App will use default icon."
    fi

    # Make the binary executable
    chmod +x "$app_dir/Contents/MacOS/dmedit"

    # Create .dmg
    echo "  Creating $dmg_path..."
    hdiutil create -volname "DMEdit" \
        -srcfolder "$app_dir" \
        -ov -format UDZO \
        "$dmg_path"

    # Clean up intermediate files
    rm -rf "$publish_dir" "$app_dir"

    echo "  Done: $dmg_path"
}

generate_icns() {
    local output="$1"
    local iconset_dir="$DIST/dmedit.iconset"
    local svg="$REPO_ROOT/resources/text_editor.svg"

    if [ ! -f "$svg" ]; then
        echo "  Warning: $svg not found, skipping icon generation."
        return
    fi

    mkdir -p "$iconset_dir"

    # sips can convert common formats; for SVG we need a PNG first.
    # If rsvg-convert or magick is available, use it; otherwise skip.
    local png="$DIST/icon_1024.png"
    if command -v rsvg-convert &> /dev/null; then
        rsvg-convert -w 1024 -h 1024 "$svg" -o "$png"
    elif command -v magick &> /dev/null; then
        magick "$svg" -resize 1024x1024 "$png"
    else
        echo "  Warning: No SVG converter available (need rsvg-convert or imagemagick). Skipping .icns."
        rm -rf "$iconset_dir"
        return
    fi

    # Generate required sizes
    for size in 16 32 128 256 512; do
        sips -z $size $size "$png" --out "$iconset_dir/icon_${size}x${size}.png" > /dev/null
        local double=$((size * 2))
        sips -z $double $double "$png" --out "$iconset_dir/icon_${size}x${size}@2x.png" > /dev/null
    done

    iconutil -c icns "$iconset_dir" -o "$output"
    rm -rf "$iconset_dir" "$png"
}

build_arch "osx-x64" "x64"
build_arch "osx-arm64" "arm64"

echo ""
echo "Build complete. Artifacts:"
ls -lh "$DIST"/*.dmg
