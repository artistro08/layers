# Layers — HID Remapper layer indicator for the Windows tray

Design spec. 2026-08-17.

## Purpose

A Windows 11 tray application that shows which HID Remapper layer is currently
active. It runs with no configuration: install it, and it finds the device,
reads the layer, and displays it.

Scope is deliberately narrow. The app reads layer state and displays it. It does
not edit remapper configuration, does not replace the official config tool, and
does not persist anything to the device's flash.

## Constraints discovered during research

The HID Remapper firmware exposes no command for reading the active layer. The
config protocol's command set ends at `GET_QUIRK` (25), and `GET_CONFIG` returns
flags and counts but never `layer_state_mask`.

Two indirect routes exist. Only one is reliable.

**Client-side inference was rejected.** The app could read every mapping whose
`target_usage` falls in `LAYERS_USAGE_PAGE` (`0xFFF10000`), enable Monitor mode,
watch the source usages, and replay the firmware's layer state machine locally.
This fails on keyboard-triggered layers. `remapper.cc` line 1606 states that for
array range inputs, key-up events with value 0 do not appear in the monitor.
Keyboard keys are array range inputs, so a layer bound to a keyboard key would
be seen pressed and never released, and the display would stick. The approach
also requires reimplementing tap-hold timing and sticky-toggle logic, which
would drift with every firmware release.

**Expression injection was chosen.** The `layer_state` expression opcode pushes
the firmware's own computed layer bitmask, and the `monitor` opcode forwards a
value to the Monitor stream under a caller-chosen usage code. Injecting a
three-token expression yields the authoritative layer mask, correct for every
trigger type: momentary, toggle, sticky, tap, hold, keyboard, mouse, gamepad,
and GPIO.

The firmware evaluates all eight expression slots on every mapping-engine
iteration (`remapper.cc` line 1172). No mapping needs to reference the slot for
it to run.

## Device protocol

### Selection

Enumerate HID devices and select on `usage_page == 0xFF00 && usage == 0x0020`.
This is the config interface, and it is the same selector the official Python
tool uses. Vendor and product IDs (`0xCAFE` / `0xBAF2`) serve only as a
secondary filter, since custom builds may change them.

If more than one remapper is present, use the first and note the ambiguity in
the popup. Multi-device support is out of scope.

### Packet framing

Every config packet is a 33-byte feature report:

| Offset | Size | Contents |
|--------|------|----------|
| 0 | 1 | Report ID, always 100 |
| 1 | 1 | Config version, always 18 |
| 2 | 1 | Command byte |
| 3 | 26 | Command payload |
| 29 | 4 | CRC32, little-endian |

The CRC covers bytes 1 through 28 inclusive — that is, everything after the
report ID and before the CRC itself. Responses are read as 33-byte feature
reports and their CRC is verified the same way.

Reads need retry. The firmware answers a request asynchronously, so a
`get_feature_report` issued too soon returns a short buffer. Retry up to ten
times with a delay starting at 2ms and doubling, matching the official tool's
behaviour.

### Connect sequence

Four steps. Neither `SUSPEND` (10) nor `PERSIST_CONFIG` (7) is ever sent.

**1. Locate or claim an expression slot.** Issue `GET_EXPRESSION` (21) for slots
0 through 7. Response format is report ID, then `nelems` as one byte, then 27
bytes of element data, then the CRC.

Check first whether our expression is already present, by matching the exact
seven element bytes below. Restarting the app must reuse its previous slot.
Without this check, every restart would consume another slot and eight restarts
would exhaust the device.

If our expression is absent, take the first slot reporting `nelems == 0`. If no
slot is free, skip to step 4 and display the layer as unknown, with the reason
shown in the popup.

**2. Write the expression.** `APPEND_TO_EXPRESSION` (20), payload `expr` =
slot index, `nelems` = 3, followed by these seven bytes:

```
14 01 01 00 00 FF 2C
```

That decodes as `LAYER_STATE` (opcode 20), then `PUSH_USAGE` (opcode 1) carrying
the little-endian sentinel `0xFF000001`, then `MONITOR` (opcode 44).

The sentinel sits in the vendor-defined usage page and cannot collide with a
real input usage.

The firmware's validator accepts this: `LAYER_STATE` and `PUSH_USAGE` each push
one value, `MONITOR` requires at least two and consumes two, leaving the stack
empty and balanced.

**3. Send `RESUME` (11).** This step is required and its necessity is not
obvious.

`eval_expr` returns 0 immediately unless `expression_valid[expr]` is set
(`remapper.cc` line 755). That flag is written only by `validate_expressions()`,
which is called only from `set_mapping_from_config()`, which the main loop runs
only when the `config_updated` flag is set (`main.cc` line 299). `RESUME` is the
only command that sets it. Omitting this step leaves the expression permanently
inert with no error reported.

`RESUME` also calls `reset_state()`, causing a one-time input-state reset at
connect. This is acceptable: it happens once, and held inputs are restored on
the next report.

`SUSPEND` is deliberately not paired with it. Suspending halts input
passthrough, so a crash between suspend and resume would leave the user's
keyboard and mouse dead. Appending to an empty slot needs no suspension.

**4. Send `SET_MONITOR_ENABLED` (22) with payload byte 1.**

### Read loop

Read input reports on report ID 101. Each report carries seven packed 9-byte
items:

| Offset | Size | Field |
|--------|------|-------|
| 0 | 4 | Usage, u32 little-endian |
| 4 | 4 | Value, i32 little-endian |
| 8 | 1 | Hub port, u8 |

Discard items with usage 0 (padding) and every usage other than `0xFF000001`.
For the sentinel, `value` is the raw layer bitmask. No scaling is applied:
`layer_state` is documented as bypassing the ×1000 fixed-point convention that
other opcodes use.

The firmware emits a monitor item only when the value changes, so each report
for the sentinel corresponds to an actual layer switch.

### Disconnect and reconnect

A read error marks the device disconnected. The displayed layer resets to 0,
matching the firmware's floor of `new_layer_state_mask = 1` when nothing is
active.

Re-enumerate every 2 seconds. On rediscovery, replay the connect sequence,
including the slot-reuse check.

### Shutdown

On quit, send `SET_MONITOR_ENABLED` (22) with payload byte 0.

The injected expression cannot be removed. The only removal command is
`CLEAR_EXPRESSIONS` (19), which wipes all eight slots and would destroy the
user's expressions, so it is never sent. The expression persists in device RAM
until the device is unplugged or reset. It is inert once Monitor is disabled.

### Layer count

This rp2040 firmware masks layers to 4 (`remapper.cc` line 31) while the config
tools declare 8. The app parses all 8 bits and displays whatever the device
reports, so it stays correct if the firmware limit rises.

When several bits are set, the badge shows the highest active layer and the
popup lists all of them.

## Process architecture

A single process, no elevation, no background service.

The HID thread owns the `hidapi` device handle and does all blocking reads. It
never touches UI state. On a layer change, connect, or disconnect, it posts
`WM_APP + 1` to the hidden message window with the new state packed into
`wParam`. All UI work happens on the UI thread, which is also the only thread
that talks to Direct2D.

A named mutex enforces a single instance, so a second launch exits rather than
adding a duplicate tray icon.

## Modules

`device.rs` owns enumeration, the packet codec, the connect sequence, the read
loop, and reconnection. This is the only module that knows the wire format.

`tray.rs` owns the `Shell_NotifyIconW` lifecycle, the hidden message window, and
the tooltip. The `tray-icon` crate is not used: the popup is custom-drawn and the
icon is swapped dynamically, so the crate's menu model would only be in the way.

`icon.rs` renders the tray icon. It holds the vendored Fluent path, draws it
through Direct2D, punches the badge, downsamples, and produces an `HICON`.

`popup.rs` owns the Fluent popup window and all its drawing.

`theme.rs` reads theme and accent color and watches for changes.

`main.rs` wires the modules together and runs the message loop.

## Icon rendering

The tray glyph is `ic_fluent_layer_24_filled` from
microsoft/fluentui-system-icons, MIT licensed. Its path data is vendored
directly as a Direct2D geometry rather than loaded as an SVG or PNG. That keeps
it resolution-independent, trivially recolorable, and free of any image-decoding
dependency.

Rendering happens at 4× the target size into a WIC BGRA premultiplied bitmap,
then downsamples to the DPI-correct tray size (16px at 100%, 20 at 125%, 24 at
150%, 32 at 200%), then converts to an `HICON`.

The glyph is filled white on dark taskbars and near-black (`#191919`) on light
ones.

The badge is drawn by punching a rounded-rectangle hole in the alpha channel
using `D2D1_COMPOSITE_MODE_DESTINATION_OUT`, then drawing the digit into the
hole. The hole keeps the badge legible over the glyph while remaining
transparent over any taskbar tint — an opaque disc would show a visible square
of the wrong color.

Layer 0 renders as the bare glyph with no badge.

The icon is regenerated only on layer change, theme change, or DPI change.

## Popup

A layered Win32 window drawn with Direct2D and DirectWrite. Not WinUI 3: there
is no Rust projection for `Microsoft.UI.Xaml`, and pulling in the Windows App
SDK would add a versioned ~50MB runtime dependency to a tray utility.

Appearance uses `DWMWA_WINDOW_CORNER_PREFERENCE` set to round and
`DWMWA_SYSTEMBACKDROP_TYPE` set to acrylic, with Segoe UI Variable throughout.

Three rows:

- **Status** — a colored dot and either "Connected" or "Disconnected". When
  connected but no expression slot was free, this reads "Connected, layer
  unavailable" with the reason on a second line.
- **Layer** — an accent-colored pill showing the layer number, or a dimmed dash
  when unknown. Multiple active layers are listed.
- **Quit** — exits, after disabling Monitor.

Rows highlight on hover. The window closes on loss of focus or Escape.

It opens on both left and right click. Requiring a right-click for the only
interaction would be a pointless distinction.

Positioning respects the taskbar edge and keeps the window inside the work area
of the monitor under the cursor.

## Theme

The tray tint follows `SystemUsesLightTheme` and the popup follows
`AppsUseLightTheme`, both under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize`. Windows
treats these independently and so does the app.

Accent color comes from `DwmGetColorizationColor`.

A `RegNotifyChangeKeyValue` watch on the Personalize key drives live updates, so
switching theme re-tints the icon and redraws the popup without a restart.

## Assets

`assets/app.ico` is the user-supplied icon, already produced: 9 sizes including
a 256×256 PNG-compressed entry. Used for the executable, Start Menu, Add/Remove
Programs, and the popup header.

`assets/NOTICE-fluentui.txt` carries the MIT license text for the vendored
Fluent path.

No tray PNG is needed. The glyph is vector path data compiled into the binary.

## Installer

Inno Setup 6. Per-user install to `{localappdata}\Layers`, which avoids a UAC
prompt. Start Menu shortcut. A "Start with Windows" checkbox, default on,
writing `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. Uninstall removes
the Run entry and the install directory.

The build is a single self-contained executable with no runtime redistributable.

## Testing

One `cargo test` file, no test framework beyond the standard harness. It covers
the pure logic, which is the part that can silently be wrong:

- Packet framing round-trip, including CRC placement and the byte range it
  covers.
- The injected expression encodes to exactly `14 01 01 00 00 FF 2C` with
  `nelems == 3`.
- Monitor report parsing: the 9-byte stride across all seven slots, sentinel
  filtering, correct rejection of usage 0, and little-endian signed values.
- Slot selection: reuses a slot already holding our expression, otherwise takes
  the first empty slot, otherwise reports none available.
- Bitmask to display: 0 means layer 0 with no badge, a single bit means that
  layer, multiple bits means highest for the badge and a full list for the
  popup.

Win32 and Direct2D drawing is not unit tested and will not be mocked. It is
verified by running the app.

## Known limitations

Monitor mode streams every raw input usage while the app runs, not just the
sentinel. This is extra USB traffic on the config interface. It is unavoidable,
because `monitor` is the only transport the firmware offers for expression
values.

The injected expression stays in device RAM until unplug. Monitor is disabled on
quit, which makes it inert, but it cannot be deleted without destroying the
user's other expressions.

If all eight expression slots are occupied, the layer cannot be read. The app
reports connected status and explains why the layer is unavailable.

The app depends on firmware internals — opcode numbers, report IDs, the
`RESUME`-triggers-validation path — that are not a published stable API. A
firmware release could change them. Config version 18 is checked on connect and
a mismatch is surfaced in the popup rather than failing silently.

## Naming

"Layers", taken from the working directory. Changeable before implementation
begins.
