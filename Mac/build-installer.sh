#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPOSITORY_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT="$SCRIPT_DIR/MosaicStudioMac.xcodeproj"
SCHEME="MosaicStudioMac"
VERSION="${VERSION:-1.0.0}"
BUILD_ROOT="$REPOSITORY_ROOT/artifacts/macos"
OUTPUT_DIRECTORY="$REPOSITORY_ROOT/artifacts/installers"
ARCHIVE="$BUILD_ROOT/MosaicStudioMac.xcarchive"
APP="$ARCHIVE/Products/Applications/MosaicStudioMac.app"

if [[ ! "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "VERSION must use the numeric major.minor.patch form, such as 1.0.0." >&2
    exit 2
fi

if ! command -v xcodebuild >/dev/null 2>&1 || ! command -v pkgbuild >/dev/null 2>&1; then
    echo "Install Xcode and its command-line tools before building the macOS installers." >&2
    exit 2
fi

mkdir -p "$BUILD_ROOT" "$OUTPUT_DIRECTORY"
rm -rf "$ARCHIVE"

SIGNING_ARGUMENTS=(CODE_SIGNING_ALLOWED=NO)
if [[ -n "${MACOS_APPLICATION_SIGNING_IDENTITY:-}" ]]; then
    SIGNING_ARGUMENTS=(
        CODE_SIGNING_ALLOWED=YES
        CODE_SIGN_STYLE=Manual
        "CODE_SIGN_IDENTITY=$MACOS_APPLICATION_SIGNING_IDENTITY"
    )
    if [[ -n "${DEVELOPMENT_TEAM:-}" ]]; then
        SIGNING_ARGUMENTS+=("DEVELOPMENT_TEAM=$DEVELOPMENT_TEAM")
    fi
fi

xcodebuild \
    -project "$PROJECT" \
    -scheme "$SCHEME" \
    -configuration Release \
    -destination "generic/platform=macOS" \
    -archivePath "$ARCHIVE" \
    "MARKETING_VERSION=$VERSION" \
    "CURRENT_PROJECT_VERSION=${BUILD_NUMBER:-1}" \
    "${SIGNING_ARGUMENTS[@]}" \
    archive

if [[ ! -d "$APP" ]]; then
    echo "The Xcode archive completed but the .app bundle is missing: $APP" >&2
    exit 1
fi

PKG="$OUTPUT_DIRECTORY/MosaicStudio-macOS-$VERSION.pkg"
DMG="$OUTPUT_DIRECTORY/MosaicStudio-macOS-$VERSION.dmg"
PKG_ARGUMENTS=(
    --component "$APP"
    --install-location /Applications
    --identifier com.mosaicstudio.mac
    --version "$VERSION"
)
if [[ -n "${MACOS_INSTALLER_SIGNING_IDENTITY:-}" ]]; then
    PKG_ARGUMENTS+=(--sign "$MACOS_INSTALLER_SIGNING_IDENTITY")
fi
pkgbuild "${PKG_ARGUMENTS[@]}" "$PKG"

STAGING_DIRECTORY="$(mktemp -d "${TMPDIR:-/tmp}/mosaic-studio-dmg.XXXXXX")"
cleanup() {
    rm -rf "$STAGING_DIRECTORY"
}
trap cleanup EXIT
ditto "$APP" "$STAGING_DIRECTORY/Mosaic Studio.app"
ln -s /Applications "$STAGING_DIRECTORY/Applications"
hdiutil create \
    -volname "Mosaic Studio $VERSION" \
    -srcfolder "$STAGING_DIRECTORY" \
    -ov \
    -format UDZO \
    "$DMG"

echo "macOS installer package created: $PKG"
echo "macOS drag-to-Applications disk image created: $DMG"
