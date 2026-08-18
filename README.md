# Layers

A Windows 11 tray indicator for the active [HID Remapper](https://github.com/jfedor2/hid-remapper) layer.

The tray icon shows the current layer as a digit. Click it for connection
status, the full list of active layers, and quit.

## How it works

The remapper firmware has no command for reading the active layer, so on
connect this app writes a three-opcode expression, `layer_state 0xFF000001
monitor`, into the first free expression slot and enables Monitor mode. The
firmware then reports the layer bitmask whenever it changes.

The write goes to device RAM only. `PERSIST_CONFIG` is never sent, so nothing
touches the device's flash and unplugging reverts it completely. The app also
never sends `CLEAR_EXPRESSIONS`, so your own expressions are safe.

If all eight expression slots are already in use, the app reports connected
status but cannot read the layer.

## Building

Requires the Rust MSVC toolchain, and Inno Setup 6 for the installer.

    cargo build --release
    & "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\layers.iss

## Credits

The tray glyph is `ic_fluent_layer_24_filled` from
[fluentui-system-icons](https://github.com/microsoft/fluentui-system-icons),
MIT licensed. See `assets/NOTICE-fluentui.txt`.
