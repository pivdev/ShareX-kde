#!/usr/bin/env bash
# Builds ShareX and installs it for the current user (no root required).
#
#   ~/.local/lib/sharex/                         published application (framework-dependent, needs the .NET 10 runtime)
#   ~/.local/bin/sharex                          symlink to the ShareX binary
#   ~/.local/share/applications/sharex.desktop   desktop entry (absolute Exec, KWin ScreenShot2 permission)
#   ~/.local/share/icons/hicolor/*/apps/sharex.png
#   ~/.local/share/mime/packages/sharex.xml      .sxcu / .sxie file types
#
# Environment overrides: SHAREX_INSTALL_DIR, SHAREX_BIN_DIR, XDG_DATA_HOME, SHAREX_CONFIGURATION (default Release).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

INSTALL_DIR="${SHAREX_INSTALL_DIR:-$HOME/.local/lib/sharex}"
BIN_DIR="${SHAREX_BIN_DIR:-$HOME/.local/bin}"
DATA_DIR="${XDG_DATA_HOME:-$HOME/.local/share}"
CONFIGURATION="${SHAREX_CONFIGURATION:-Release}"

case "$(uname -m)" in
    x86_64) RID="linux-x64" ;;
    aarch64 | arm64) RID="linux-arm64" ;;
    *) echo "Unsupported architecture: $(uname -m)" >&2; exit 1 ;;
esac

info() { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33mWarning:\033[0m %s\n' "$*" >&2; }
have() { command -v "$1" >/dev/null 2>&1; }

if ! have dotnet; then
    echo "dotnet was not found. Install the .NET 10 SDK (e.g. 'sudo pacman -S dotnet-sdk')." >&2
    exit 1
fi

# Quote a path for a desktop entry Exec key, then escape it for a sed replacement.
desktop_exec_quote() {
    local value="$1"
    local reserved='[[:space:]"'"'"'\\><~|&;$*?#()`]'
    if [[ "$value" =~ $reserved ]]; then
        value="${value//\\/\\\\}"
        value="${value//\"/\\\"}"
        value="${value//\`/\\\`}"
        value="${value//\$/\\\$}"
        value="\"$value\""
    fi
    # Exec is a string value: backslashes are escaped once more and % becomes %%.
    value="${value//\\/\\\\}"
    value="${value//%/%%}"
    printf '%s' "$value"
}

sed_escape() { printf '%s' "$1" | sed -e 's/[\/&|]/\\&/g'; }

STAGING_DIR="$INSTALL_DIR.new"
rm -rf "$STAGING_DIR"

info "Publishing ShareX ($CONFIGURATION, $RID) to $INSTALL_DIR"
dotnet publish "$REPO_DIR/ShareX/ShareX.csproj" -c "$CONFIGURATION" -r "$RID" --self-contained false -o "$STAGING_DIR" -nologo
# ShareX.csproj references the native messaging host without referencing its assembly, so publish it next to ShareX.
dotnet publish "$REPO_DIR/ShareX.NativeMessagingHost/ShareX.NativeMessagingHost.csproj" -c "$CONFIGURATION" -r "$RID" --self-contained false -o "$STAGING_DIR" -nologo

if [[ ! -x "$STAGING_DIR/ShareX" ]]; then
    echo "Publish did not produce $STAGING_DIR/ShareX" >&2
    exit 1
fi

# Swap in the new build. A running ShareX keeps its already opened files.
mkdir -p "$(dirname "$INSTALL_DIR")"
if [[ -e "$INSTALL_DIR" ]]; then
    rm -rf "$INSTALL_DIR.old"
    mv "$INSTALL_DIR" "$INSTALL_DIR.old"
fi
mv "$STAGING_DIR" "$INSTALL_DIR"
rm -rf "$INSTALL_DIR.old"

EXECUTABLE="$INSTALL_DIR/ShareX"

info "Installing icons"
ICON_SOURCE="$REPO_DIR/ShareX/ShareX_Icon.ico"
FALLBACK_PNG="$REPO_DIR/ShareX.HelpersLib/Resources/ShareX_Logo.png"
MAGICK=""
if have magick; then MAGICK="magick"; elif have convert; then MAGICK="convert"; fi

if [[ -n "$MAGICK" ]]; then
    for size in 16 22 24 32 48 64 128 256; do
        dir="$DATA_DIR/icons/hicolor/${size}x${size}/apps"
        mkdir -p "$dir"
        # Frame 0 of ShareX_Icon.ico is the 256x256 PNG.
        "$MAGICK" "${ICON_SOURCE}[0]" -background none -resize "${size}x${size}" "PNG32:$dir/sharex.png"
    done
else
    warn "ImageMagick not found; installing only the 256x256 icon."
    mkdir -p "$DATA_DIR/icons/hicolor/256x256/apps"
    install -m 644 "$FALLBACK_PNG" "$DATA_DIR/icons/hicolor/256x256/apps/sharex.png"
fi

if have gtk-update-icon-cache && [[ -f "$DATA_DIR/icons/hicolor/index.theme" ]]; then
    gtk-update-icon-cache -q -t -f "$DATA_DIR/icons/hicolor" || true
fi

info "Installing desktop entry"
DESKTOP_FILE="$DATA_DIR/applications/sharex.desktop"
mkdir -p "$DATA_DIR/applications"
EXEC_VALUE="$(sed_escape "$(desktop_exec_quote "$EXECUTABLE")")"
sed -e "s|@EXEC@|$EXEC_VALUE|g" -e '/^# /d' "$SCRIPT_DIR/sharex.desktop" > "$DESKTOP_FILE.tmp"
mv "$DESKTOP_FILE.tmp" "$DESKTOP_FILE"
chmod 644 "$DESKTOP_FILE"

if have desktop-file-validate; then
    desktop-file-validate "$DESKTOP_FILE" || warn "desktop-file-validate reported problems."
fi

info "Registering file types (.sxcu, .sxie)"
mkdir -p "$DATA_DIR/mime/packages"
install -m 644 "$SCRIPT_DIR/sharex-mime.xml" "$DATA_DIR/mime/packages/sharex.xml"
if have update-mime-database; then
    update-mime-database "$DATA_DIR/mime"
fi
if have xdg-mime; then
    # xdg-mime prints harmless "qtpaths: command not found" noise on Plasma without qt6-tools.
    xdg-mime default sharex.desktop application/x-sharex-custom-uploader application/x-sharex-image-effect 2>/dev/null || warn "xdg-mime failed to set ShareX as the default application for .sxcu/.sxie files."
fi

info "Linking $BIN_DIR/sharex"
mkdir -p "$BIN_DIR"
ln -sfn "$EXECUTABLE" "$BIN_DIR/sharex"
case ":$PATH:" in
    *":$BIN_DIR:"*) ;;
    *) warn "$BIN_DIR is not in PATH." ;;
esac

info "Updating desktop databases"
if have update-desktop-database; then
    update-desktop-database -q "$DATA_DIR/applications" || true
fi
if have kbuildsycoca6; then
    kbuildsycoca6 >/dev/null 2>&1 || true
elif have kbuildsycoca5; then
    kbuildsycoca5 >/dev/null 2>&1 || true
fi

# Integrations created from ShareX's settings (autostart, Dolphin service menus, browser manifests) embed the
# executable path. Refresh entries that were created by an earlier ShareX build in a different location.
refresh_exec() {
    local file="$1"
    [[ -f "$file" ]] || return 0
    grep -q '^Exec=' "$file" || return 0
    local quoted
    quoted="$(sed_escape "$(desktop_exec_quote "$EXECUTABLE")")"
    # Replace the program (first word, optionally quoted) of every Exec line, keep the arguments.
    sed -i -E -e "s|^Exec=\"[^\"]*\"|Exec=$quoted|" -e t -e "s|^Exec=[^ ]+|Exec=$quoted|" "$file"
}
refresh_exec "${XDG_CONFIG_HOME:-$HOME/.config}/autostart/sharex.desktop"
refresh_exec "$DATA_DIR/kio/servicemenus/sharex-upload.desktop"
refresh_exec "$DATA_DIR/kio/servicemenus/sharex-edit.desktop"

echo
info "ShareX installed: $EXECUTABLE"
echo "    Start it from the application menu or run 'sharex'."
echo "    Autostart, Dolphin context menu entries and browser integration can be enabled in"
echo "    ShareX > Application settings > Integration."
