<div align="center">

<img src="assets/icon.png" alt="Layers" width="160">

# Layers

Windows 11 tray indicator for the active [HID Remapper](https://github.com/jfedor2/hid-remapper) layer.

</div>

The tray icon shows the layer you are on. Switch layers on your peripheral and it updates instantly, with an optional heads-up display at the bottom of the screen. Click the icon for status and settings.

Native Rust on Win32 and Direct2D — one 1.1 MB executable, no runtime dependencies.

## Install

Download `layers-setup.exe` from the [latest release](../../releases/latest) and run it. Per-user, so no UAC prompt. Plug in a flashed HID Remapper and it works — nothing to configure.

Uninstall from Settings → Apps.

## Settings

Click the tray icon, hover **HUD**. Turn the heads-up display off entirely, or silence it for individual layers — a silenced layer stays silent in both directions, so a muted hold-to-activate layer will not announce the layer you land back on either.

Stored under `HKCU\Software\Layers`.

## How it reads the layer

The firmware has no command for it, so on connect the app writes the expression `layer_state 0xFF000001 monitor` into a free expression slot and reads it back over Monitor mode.

**This touches device RAM only.** `PERSIST_CONFIG` is never sent, so nothing reaches flash and unplugging reverts the device. `CLEAR_EXPRESSIONS` is never sent, so your own expressions are untouched. `SUSPEND` is never sent, so a crash cannot leave your keyboard unresponsive.

Requires firmware config version 18. If all eight expression slots are in use, the tray shows connected but cannot read the layer.

## Building

Rust MSVC toolchain, plus [Inno Setup 6.3+](https://jrsoftware.org/isdl.php) for the installer.

```
cargo build --release
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\layers.iss
```

## Credits

Glyphs from [fluentui-system-icons](https://github.com/microsoft/fluentui-system-icons) (MIT) — see [`assets/NOTICE-fluentui.txt`](assets/NOTICE-fluentui.txt). Built for [jfedor2/hid-remapper](https://github.com/jfedor2/hid-remapper).

[MIT](LICENSE).
