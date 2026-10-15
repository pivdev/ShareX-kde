#!/usr/bin/env bash
# Removes a per-user ShareX installation created by install.sh, plus the integrations ShareX itself creates
# (autostart entry, Dolphin service menus, browser native messaging manifests, file types).
# Personal data (screenshots, settings, history in ~/Documents/ShareX) is not touched.

set -uo pipefail

INSTALL_DIR="${SHAREX_INSTALL_DIR:-$HOME/.local/lib/sharex}"
BIN_DIR="${SHAREX_BIN_DIR:-$HOME/.local/bin}"
DATA_DIR="${XDG_DATA_HOME:-$HOME/.local/share}"
CONFIG_DIR="${XDG_CONFIG_HOME:-$HOME/.config}"

info() { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
have() { command -v "$1" >/dev/null 2>&1; }

remove() {
    for path in "$@"; do
        if [[ -e "$path" || -L "$path" ]]; then
            rm -rf -- "$path"
            echo "    removed $path"
        fi
    done
}

if pgrep -f "^$INSTALL_DIR/ShareX" >/dev/null 2>&1; then
    echo "ShareX appears to be running. Close it first (tray icon > Exit) and run this script again." >&2
    exit 1
fi

info "Removing application"
remove "$INSTALL_DIR" "$INSTALL_DIR.new" "$INSTALL_DIR.old"
if [[ -L "$BIN_DIR/sharex" ]]; then
    remove "$BIN_DIR/sharex"
fi

info "Removing desktop integration"
remove "$DATA_DIR/applications/sharex.desktop"
for icon in "$DATA_DIR"/icons/hicolor/*/apps/sharex.png; do
    remove "$icon"
done
remove "$CONFIG_DIR/autostart/sharex.desktop"
remove "$DATA_DIR/kio/servicemenus/sharex-upload.desktop" "$DATA_DIR/kio/servicemenus/sharex-edit.desktop"

if [[ -f "$DATA_DIR/mime/packages/sharex.xml" ]]; then
    remove "$DATA_DIR/mime/packages/sharex.xml"
    have update-mime-database && update-mime-database "$DATA_DIR/mime"
fi

info "Removing browser native messaging manifests"
for dir in "$CONFIG_DIR"/google-chrome* "$CONFIG_DIR/chromium" "$CONFIG_DIR/BraveSoftware/Brave-Browser" \
    "$CONFIG_DIR"/microsoft-edge* "$CONFIG_DIR/vivaldi" "$CONFIG_DIR/opera"; do
    remove "$dir/NativeMessagingHosts/com.getsharex.sharex.json"
done
for dir in "$HOME/.mozilla" "$CONFIG_DIR/mozilla" "$HOME/.librewolf"; do
    remove "$dir/native-messaging-hosts/ShareX.json"
done

info "Updating desktop databases"
have update-desktop-database && update-desktop-database -q "$DATA_DIR/applications"
have gtk-update-icon-cache && [[ -f "$DATA_DIR/icons/hicolor/index.theme" ]] && gtk-update-icon-cache -q -t -f "$DATA_DIR/icons/hicolor"
if have kbuildsycoca6; then
    kbuildsycoca6 >/dev/null 2>&1
elif have kbuildsycoca5; then
    kbuildsycoca5 >/dev/null 2>&1
fi

info "ShareX uninstalled. Personal data in ~/Documents/ShareX was kept."
exit 0
