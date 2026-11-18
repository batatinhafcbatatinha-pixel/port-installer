#!/usr/bin/env bash
# Packages the Linux / macOS launcher built by `dotnet publish` (NativeAOT) the way each system expects it.
#   tools/package_launcher.sh linux <publish dir> <out dir> [version]
#       -> Launcher-linux-<arch>.tar.gz (the program, its two Skia libraries, icon, .desktop entry, install.sh)
#          Launcher-linux-<arch>.AppImage (one file; appimagetool is downloaded when missing)
#   tools/package_launcher.sh macos <publish dir arm64> <out dir> [version] [publish dir x64]
#       -> Launcher-macos.zip holding "Port Launcher.app" (universal when both publish dirs are given)
# Nothing from any game is packaged: only the launcher this repository builds.
set -euo pipefail
kind=$1; pub=$2; out=$3; version=${4:-0.0.0}; pub2=${5:-}
here="$(cd "$(dirname "$0")/.." && pwd)"
mkdir -p "$out"
out="$(cd "$out" && pwd)"
icon="$here/launcher-avalonia/Assets/port-launcher.png"

desktop_entry() {   # $1: Exec value
  cat <<EOF
[Desktop Entry]
Type=Application
Name=Port Launcher
GenericName=Game launcher
Comment=Installs, builds and starts games recompiled for PC from your own discs
Exec=$1
Icon=port-launcher
Terminal=false
Categories=Game;
StartupWMClass=port-launcher
EOF
}

if [ "$kind" = linux ]; then
  case "$(uname -m)" in aarch64|arm64) arch=arm64; aiarch=aarch64 ;; *) arch=x64; aiarch=x86_64 ;; esac
  name="Launcher-linux-$arch"
  stage="$(mktemp -d)"
  dir="$stage/port-launcher"
  mkdir -p "$dir"
  cp "$pub/Launcher" "$pub"/*.so "$dir/"
  cp "$icon" "$dir/port-launcher.png"
  desktop_entry "Launcher" > "$dir/port-launcher.desktop"
  cat > "$dir/install.sh" <<'EOF'
#!/usr/bin/env bash
# Puts the launcher in ~/.local/share/port-launcher and adds it to the applications menu (no administrator needed).
set -e
src="$(cd "$(dirname "$0")" && pwd)"
dest="${XDG_DATA_HOME:-$HOME/.local/share}/port-launcher"
mkdir -p "$dest" "${XDG_DATA_HOME:-$HOME/.local/share}/applications" "${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/256x256/apps"
cp "$src"/Launcher "$src"/*.so "$src"/port-launcher.png "$dest/"
cp "$src/port-launcher.png" "${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/256x256/apps/port-launcher.png"
sed "s|^Exec=.*|Exec=\"$dest/Launcher\"|" "$src/port-launcher.desktop" > "${XDG_DATA_HOME:-$HOME/.local/share}/applications/port-launcher.desktop"
echo "Installed: $dest/Launcher (also in the applications menu as Port Launcher)"
EOF
  chmod +x "$dir/Launcher" "$dir/install.sh"
  echo "$version" > "$dir/VERSION"
  tar -C "$stage" -czf "$out/$name.tar.gz" port-launcher
  # AppImage: the same files in an AppDir, the program started by AppRun.
  appdir="$stage/AppDir"
  mkdir -p "$appdir/usr/bin"
  cp "$dir/Launcher" "$dir"/*.so "$dir/port-launcher.png" "$appdir/usr/bin/"
  cp "$icon" "$appdir/port-launcher.png"
  desktop_entry "Launcher" > "$appdir/port-launcher.desktop"
  printf '#!/bin/sh\nhere="$(dirname "$(readlink -f "$0")")"\nexec "$here/usr/bin/Launcher" "$@"\n' > "$appdir/AppRun"
  chmod +x "$appdir/AppRun"
  tool="${APPIMAGETOOL:-}"
  if [ -z "$tool" ]; then
    tool="$stage/appimagetool"
    curl -fsSL -o "$tool" "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-$aiarch.AppImage"
    chmod +x "$tool"
  fi
  ARCH=$aiarch APPIMAGE_EXTRACT_AND_RUN=1 "$tool" --no-appstream "$appdir" "$out/$name.AppImage"
  rm -rf "$stage"
  ls -la "$out"
elif [ "$kind" = macos ]; then
  stage="$(mktemp -d)"
  app="$stage/Port Launcher.app"
  mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
  for f in "$pub"/*; do
    b="$(basename "$f")"
    [ -d "$f" ] && continue   # debug symbols (Launcher.dsym) stay out
    case "$b" in *.dSYM|*.dsym|*.dbg) continue ;; esac
    if [ -n "$pub2" ] && [ -f "$pub2/$b" ] && ! cmp -s "$f" "$pub2/$b" && lipo -info "$f" 2>/dev/null | grep -q "Non-fat"; then
      lipo -create "$f" "$pub2/$b" -output "$app/Contents/MacOS/$b"   # one program for Apple Silicon and Intel
    else
      cp "$f" "$app/Contents/MacOS/$b"
    fi
  done
  # The icon: the PNG at every size iconutil wants.
  set_dir="$stage/port-launcher.iconset"
  mkdir -p "$set_dir"
  for s in 16 32 128 256 512; do
    sips -z $s $s "$icon" --out "$set_dir/icon_${s}x${s}.png" >/dev/null
    d=$((s * 2)); sips -z $d $d "$icon" --out "$set_dir/icon_${s}x${s}@2x.png" >/dev/null
  done
  iconutil -c icns "$set_dir" -o "$app/Contents/Resources/port-launcher.icns"
  cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Port Launcher</string>
  <key>CFBundleDisplayName</key><string>Port Launcher</string>
  <key>CFBundleIdentifier</key><string>io.github.batatinhafcbatatinha-pixel.port-launcher</string>
  <key>CFBundleExecutable</key><string>Launcher</string>
  <key>CFBundleIconFile</key><string>port-launcher</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.games</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
EOF
  chmod +x "$app/Contents/MacOS/Launcher"
  codesign --force --deep -s - "$app"   # ad-hoc signature: Apple Silicon runs only signed code
  (cd "$stage" && ditto -c -k --keepParent "Port Launcher.app" "$out/Launcher-macos.zip")
  lipo -info "$app/Contents/MacOS/Launcher" || true
  rm -rf "$stage"
  ls -la "$out"
else
  echo "usage: $0 linux|macos <publish dir> <out dir> [version] [second publish dir]" >&2
  exit 1
fi
