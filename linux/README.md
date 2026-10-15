# ShareX on Linux

This branch (`linux-port`) builds upstream ShareX (Avalonia UI) for Linux, with native backends for the parts
that used Win32. It is developed and tested on KDE Plasma 6 (Wayland).

## Install

Requirements: .NET 10 SDK, `wl-clipboard`, ImageMagick (icons). Optional: `tesseract` (OCR).

```sh
./linux/install.sh      # publishes to ~/.local/lib/sharex, adds ~/.local/bin/sharex and a desktop entry
./linux/uninstall.sh    # removes it again (keeps ~/Documents/ShareX)
```

Re-run `install.sh` after pulling changes.

## How the Linux backends work

| Area | Linux implementation |
| --- | --- |
| Screen capture | KWin `org.kde.KWin.ScreenShot2` over D-Bus; falls back to xdg-desktop-portal, then `spectacle`/`grim` |
| Window list, active window, cursor position | Short-lived KWin scripts that report back over D-Bus (`KWinBridge`) |
| Global hotkeys | KDE KGlobalAccel (shown under System Settings > Shortcuts > ShareX) |
| Tray icon | Avalonia TrayIcon (StatusNotifierItem) |
| Clipboard | `wl-copy` / `wl-paste` on Wayland |
| Single instance | flock lock file + Unix socket in `$XDG_RUNTIME_DIR` |
| Autostart, file types, Dolphin menu, browser extension | `~/.config/autostart`, shared-mime-info, KIO service menus, native messaging manifests |
| Stored credentials | AES-GCM with a per-user key in `~/.local/share/ShareX/dpapi.key` (replaces Windows DPAPI) |
| OCR | Tesseract CLI |
| Screen recording (GIF and video) | Frames of the selected area from KWin `ScreenShot2.CaptureArea`, streamed as raw video into FFmpeg (`/usr/bin/ffmpeg`); the recording frame is cut out with the X Shape extension so the recorded area stays clickable |

KWin only allows `ScreenShot2` for executables whose `.desktop` file declares
`X-KDE-DBUS-Restricted-Interfaces=org.kde.KWin.ScreenShot2` and whose `Exec` points at that binary. `install.sh`
sets this up. A binary run from somewhere else (for example `ShareX/bin/Debug`) falls back to the portal unless it
has its own desktop entry.

## Known limitations

- Screen recording has no audio on Linux yet, and needs KDE (KWin) for frames. The Windows-only "native" recorder
  option is ignored; recordings always go through FFmpeg.
- Print Screen and Shift+Print Screen are bound to Spectacle by default in KDE, so ShareX can't register them.
  Unbind them in System Settings > Shortcuts > Spectacle, or choose different hotkeys in ShareX.
- Cursor capture uses the cursor theme's default arrow; Wayland does not expose the live cursor image.
- Wayland does not let apps move the pointer, so arrow-key nudging of the cursor in region capture does nothing.
- Only tested on KDE Plasma. Other desktops get the portal capture path and no global hotkeys.
