#!/bin/zsh
set -euo pipefail

SCRIPT_DIR=${0:A:h}
PROJECT_ROOT=${SCRIPT_DIR:h}
FINAL_APP_DIR="$PROJECT_ROOT/PairPair ConfigTool.app"
BUILD_ROOT=$(mktemp -d "${TMPDIR:-/tmp}/pairpair-configtool.XXXXXX")
APP_DIR="$BUILD_ROOT/PairPair ConfigTool.app"
CONTENTS_DIR="$APP_DIR/Contents"
MACOS_DIR="$CONTENTS_DIR/MacOS"
RESOURCES_DIR="$CONTENTS_DIR/Resources/Web"
trap 'rm -rf "$BUILD_ROOT"' EXIT

mkdir -p "$MACOS_DIR" "$RESOURCES_DIR"

swiftc \
  -O \
  -framework AppKit \
  -framework Foundation \
  -framework WebKit \
  -framework UniformTypeIdentifiers \
  "$SCRIPT_DIR/Source/ConfigDataSupport.swift" \
  "$SCRIPT_DIR/Source/GitRepositorySupport.swift" \
  "$SCRIPT_DIR/Source/main.swift" \
  -o "$MACOS_DIR/ConfigTool"

cp "$SCRIPT_DIR/Info.plist" "$CONTENTS_DIR/Info.plist"
cp "$PROJECT_ROOT/Resources/index.html" "$RESOURCES_DIR/index.html"
cp "$PROJECT_ROOT/Resources/styles.css" "$RESOURCES_DIR/styles.css"
cp "$PROJECT_ROOT/Resources/relationships.js" "$RESOURCES_DIR/relationships.js"
cp "$PROJECT_ROOT/Resources/app.js" "$RESOURCES_DIR/app.js"

xattr -cr "$APP_DIR"
xattr -r -d com.apple.FinderInfo "$APP_DIR" 2>/dev/null || true
xattr -r -d 'com.apple.fileprovider.fpfs#P' "$APP_DIR" 2>/dev/null || true
xattr -r -d com.apple.macl "$APP_DIR" 2>/dev/null || true
xattr -r -d com.apple.provenance "$APP_DIR" 2>/dev/null || true
codesign --force --deep --sign - "$APP_DIR"

if [[ -d "$FINAL_APP_DIR" ]]; then
  rm -rf "$FINAL_APP_DIR"
fi
mv "$APP_DIR" "$FINAL_APP_DIR"
# The Desktop file provider can race with signing by reattaching Finder metadata.
# Retry the complete cleanup/sign/verify sequence until an attempt completes cleanly.
signed=false
for attempt in {1..5}; do
  xattr -cr "$FINAL_APP_DIR"
  xattr -r -d com.apple.FinderInfo "$FINAL_APP_DIR" 2>/dev/null || true
  xattr -r -d 'com.apple.fileprovider.fpfs#P' "$FINAL_APP_DIR" 2>/dev/null || true
  xattr -r -d com.apple.macl "$FINAL_APP_DIR" 2>/dev/null || true
  xattr -r -d com.apple.provenance "$FINAL_APP_DIR" 2>/dev/null || true
  if codesign --force --deep --sign - "$FINAL_APP_DIR" && codesign --verify --deep --strict --verbose=1 "$FINAL_APP_DIR"; then
    signed=true
    break
  fi
  sleep 1
done

if [[ "$signed" != true ]]; then
  echo "无法在 Desktop 文件提供器写入元数据的间隙完成 App 签名。" >&2
  exit 1
fi

echo "$FINAL_APP_DIR"
