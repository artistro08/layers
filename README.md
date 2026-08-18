<div align="center">

<img src="assets/icon.png" alt="Layers" width="160">

# Layers

A native Windows 11 tray indicator for the active [HID Remapper](https://github.com/jfedor2/hid-remapper) layer.

</div>

The tray icon shows which layer you are on. Switch layers on your peripheral and the icon changes instantly, with an optional heads-up display at the bottom of the screen. Click the icon for connection status and settings.

Written in Rust against Win32, Direct2D, DirectWrite and WIC directly — no WebView, no WinUI runtime, no Electron. The installed application is a single 1.1 MB executable with no runtime dependencies.

## Features

- **Layer digit in the tray.** Layer 0 shows the Fluent layers glyph; any other layer shows that number, drawn at full icon height so it stays legible at 100% scaling.
- **A Fluent flyout.** Click the tray icon for connection status, the active layer, HUD settings and quit. Rounded, translucent, with a drop shadow and hover highlights.
- **A heads-up display.** On a layer change, a panel appears at the bottom of the primary display, holds briefly, and fades. Sized to its content. Click-through, and never in the taskbar or Alt-Tab.
- **Per-layer HUD control.** Turn the HUD off entirely, or silence it for individual layers. Suppressing a layer silences it in both directions, so a hold-to-activate layer you have muted will not announce the layer you land back on either.
- **Follows your theme.** The tray glyph tracks the taskbar theme and the flyout tracks the app theme — Windows tracks those separately, and so does this. Both update live, without a restart.
- **DPI aware.** The tray icon is rendered at 16, 20, 24 or 32 px to match your display scaling, and re-rendered when it changes.
- **Survives an Explorer restart.** Re-adds its tray icon on the `TaskbarCreated` broadcast rather than vanishing until you relaunch it.

## Install

Download `layers-setup.exe` from the [latest release](../../releases/latest) and run it.

The installer is per-user, so it raises no UAC prompt. It installs to `%LOCALAPPDATA%\Layers`, adds a Start Menu entry, and offers a "Start with Windows" checkbox (on by default) that writes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

Uninstall from Settings → Apps, or:

```
"%LOCALAPPDATA%\Layers\unins000.exe" /SILENT
```

No configuration is required. Plug in a flashed HID Remapper and it works.

## How it works

This is the interesting part, and it is worth knowing before you run it, because the application writes to your device.

**The firmware has no command for reading the active layer.** The config protocol's command set ends at `GET_QUIRK` (25), and `GET_CONFIG` returns flags and counts but never `layer_state_mask`. Two indirect routes exist, and only one is reliable:

- *Inferring it client-side* — read the mappings whose target is in `LAYERS_USAGE_PAGE`, watch their source usages over Monitor mode, and replay the firmware's layer state machine locally. This fails on keyboard-triggered layers: [`remapper.cc`](https://github.com/jfedor2/hid-remapper/blob/master/firmware/src/remapper.cc) notes that for array range inputs, key-up events do not appear in the monitor. A layer bound to a keyboard key would be seen pressed and never released.

- *Asking the firmware directly* — the `layer_state` expression opcode pushes the firmware's own computed layer bitmask, and `monitor` forwards a value to the Monitor stream under a usage code of your choosing. That is what this application does.

On connect it:

1. Reads the config version and stops if it is not 18. Every opcode number here belongs to that version, so nothing is written until the firmware confirms it.
2. Scans the eight expression slots, reusing its own if already present, otherwise claiming the first empty one.
3. Appends the three-token expression `layer_state 0xFF000001 monitor`.
4. Sends `RESUME`. This step is required and not obvious: `eval_expr` refuses to run an expression whose `expression_valid` flag is unset, and only `RESUME` reaches the code that sets it. Without it the expression is silently inert.
5. Enables Monitor mode and reads report ID 101, filtering for the sentinel usage.

### What this means for your device

**Nothing is written to flash.** `PERSIST_CONFIG` is never sent — the constant does not appear anywhere in the source. The injected expression lives in device RAM only and disappears when you unplug the device.

**Your own expressions are safe.** `CLEAR_EXPRESSIONS` is never sent either. Only an empty slot is ever claimed.

**Your keyboard cannot be left dead.** `SUSPEND` is never sent. Suspending halts input passthrough, and a crash while suspended would leave your keyboard and mouse unresponsive.

Every device packet in the codebase is built by five named constructors in one module, and `hidapi` appears in exactly one file, so those three commands have no path to the device.

### Known limitations

- Monitor mode streams every raw input usage while the application runs, not just the sentinel. That is extra USB traffic on the config interface, and it is unavoidable — `monitor` is the only transport the firmware offers for expression values.
- The injected expression stays in device RAM until you unplug. Monitor is disabled on quit, which makes it inert, but it cannot be removed without `CLEAR_EXPRESSIONS`, which would destroy your own expressions.
- If all eight expression slots are already in use, the tray reports connected but cannot read the layer.
- Multi-device support is out of scope. With two remappers attached, the first is used.

## Settings

Stored under `HKCU\Software\Layers` as `REG_DWORD` values. The keys are only written once you change something; defaults apply when absent.

| Value | Meaning | Default |
|---|---|---|
| `HudEnabled` | HUD master switch, 0 or 1 | 1 |
| `HudSuppressedLayers` | Bitmask; bit *N* set means the HUD is suppressed for layer *N* | 0 |

Both are controlled from the flyout — hover **HUD** to open the settings submenu.

## Building

Requires the Rust MSVC toolchain. For the installer, [Inno Setup 6.3 or later](https://jrsoftware.org/isdl.php).

```
cargo build --release
```

```
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\layers.iss
```

The installer script is at `installer/layers.iss`; the output lands in `installer/Output/`.

### Layout

| Module | Responsibility |
|---|---|
| `protocol.rs` | HID Remapper wire format. Pure data, no I/O. |
| `device.rs` | The only module that talks to `hidapi`. Owns the device thread. |
| `geometry.rs` | Minimal SVG path parser for the vendored glyphs. |
| `compose.rs` | Alpha-coverage compositing for the tray icon. |
| `render.rs` | Shared Direct2D, DirectWrite and WIC factories. |
| `icon.rs` | Builds the tray `HICON`. |
| `theme.rs` | Light/dark theme, watched for changes. |
| `tray.rs` | `Shell_NotifyIconW` lifecycle. |
| `popup.rs` | The main flyout. |
| `submenu.rs` | The HUD settings flyout. |
| `hud.rs` | The bottom-of-screen heads-up display. |
| `settings.rs` | Persisted preferences. |
| `main.rs` | Wiring and the message loop. |

The pure-logic modules — `protocol`, `geometry`, `compose`, `settings`, and the panel layout maths — carry the test suite. The Direct2D and Win32 code is verified by running it.

```
cargo test
```

## Credits

Tray and menu glyphs are from [fluentui-system-icons](https://github.com/microsoft/fluentui-system-icons) (MIT): `ic_fluent_layer_24_filled`, `ic_fluent_power_20_filled`, `ic_fluent_checkmark_20_filled` and `ic_fluent_chevron_right_20_filled`. See [`assets/NOTICE-fluentui.txt`](assets/NOTICE-fluentui.txt).

Built for [jfedor2/hid-remapper](https://github.com/jfedor2/hid-remapper).
