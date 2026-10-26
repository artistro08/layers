# Layers: Shortcut Sheet Flyout Design

Add a flyout that lists every mapping on the connected HID Remapper, grouped by layer, in the style of the PowerToys Shortcut Guide. It opens from the tray icon, from a tray menu item, and from a global hotkey the user sets in Settings. Resolves [issue #2](https://github.com/artistro08/layers/issues/2).

## Intent

The web config tool is the only place to see what a HID Remapper does. Switching to a browser to remember what F13 does on layer 2 breaks flow. The flyout answers "what does this key do right now" from the tray, and gets the user into the web tool when they want to change it.

## Goals

- Every mapping on the device is listed, with the same key names the web config tool uses.
- Mappings are grouped by layer, the active layer is marked, and the mark follows layer changes while the flyout is open.
- Macros show their key sequence in full.
- Looks native to Windows 11: acrylic panel above the tray, key chips like PowerToys and the Windows Settings keyboard pages.
- Opens from a tray left-click, a "Shortcuts" tray menu item, and a user-set global hotkey.
- Footer shows connection status and gives one-click access to the web tool and Settings.

## Non-Goals

- Editing mappings, macros, or expressions from Layers. Editing stays in the web tool.
- Listing expressions. Layers' own expression is the only one with a meaning it could explain, and the others are opaque bytecode.
- Showing scaling, hub ports, unmapped passthrough layers, or other config fields that are not key mappings.
- Per-device or per-layer custom names. The firmware stores none.
- Multiple hotkeys. One hotkey opens the flyout.

## Anti-Goals

- Writing to the device. The flyout reads only. The existing expression install is the only write Layers ever does.
- Truncating macros. A long macro wraps onto more lines. Nothing is cut or hidden behind a tooltip.
- A flyout that goes solid or dim when it loses focus. It closes instead.
- Blocking the tray or the layer HUD while mappings are read.

## Decisions

| Topic | Decision |
|---|---|
| Source of truth | The device. `GET_CONFIG` gives the mapping count and descriptor number. `GET_MAPPING n` for each mapping. `GET_MACRO m, i` for each of the 32 macro slots until an empty packet. Nothing is stored locally. |
| When to read | Once after each connect that reaches `Connected` or `NoSlot`, and again every time the flyout opens. Never in `VersionMismatch`: the reply layouts are only verified for config version 18. A read that fails mid-way keeps the previous list and shows a quiet "Couldn't refresh" line in the body header. |
| Key names | A C# table generated once from the web tool's `config-tool-web/usages.js` (MIT, jfedor2/hid-remapper) and committed as `src/Layers.Core/Logic/UsageNames.cs`. Lookup order for an input: `source_0` (the web tool's default mouse labels, so `0x00090001` reads "Left button" as it does there), `source`, `source_extra`, then the Button and MIDI page rules from `readable_usage_name`. The tool's Mouse/Gamepad label switch is a browser-side preference, not a device value, so Layers follows its default and offers no switch. (Builder's call.) Lookup for an output: the table for the device's `our_descriptor_number`, falling back to `source` names, then hex. |
| Grouping | One section per layer bit set in a mapping's layer mask. A mapping on layers 0 and 2 appears in both sections. Sections are ordered 0 to 7. Layers with no mappings are not shown. |
| Row | Input chips, a right-arrow glyph, output chips. Flags appear as small subtle tags after the output: Sticky, Tap, Hold. |
| Macro row | Output is a caption "Macro 3" above the sequence. Each step is its chips joined by "+". Steps are separated by a small chevron glyph. The sequence wraps. An empty macro slot that a mapping points to shows "Macro 3" with "Empty". |
| Hotkey | `RegisterHotKey` on the tray icon's hidden window, following MagicBuds. Default Win+Shift+L. Stored as `REG_SZ` `FlyoutShortcut` under `HKCU\Software\Layers`. Empty string means disabled. Missing value means default. |
| Hotkey picker | The PowerToys `ShortcutControl`, `ShortcutDialogContentControl`, `KeyVisual`, `KeyCharPresenter`, `KeyboardHook`, and `HotkeySettings` ported from MagicBuds, with MagicBuds strings removed. MIT notice added at `assets/NOTICE-powertoys.txt`, credited in the README. |
| Tray clicks | Left-click toggles the flyout. Right-click opens the menu. The menu gains a "Shortcuts" item above "Settings…". |
| Placement | Above the tray icon on the icon's monitor, flush to the taskbar edge, 360 DIP wide, right-aligned to the work area with a 12 DIP margin, following MagicBuds' `FlyoutPlacement`. When the Windhawk `taskbar-multi-tray` mod is on, the cursor's monitor is used instead. |
| Height | The panel grows to fit its content up to the work area height minus margins. Past that the list scrolls. |
| Chrome | Borderless popup with per-panel desktop acrylic that stays active while unfocused, rounded 8 DIP corners, 1 DIP stroke, theme shadow. Ported from MagicBuds `FlyoutWindow`, `PopupWindowStyle`, `TransparentBackdrop`. Layers' existing `ActiveAcrylicBackdrop` is reused. |
| Motion | Slide out from behind the taskbar on open (250 ms) and back on close (167 ms), as in MagicBuds. |
| Dismiss | Focus loss, Esc, the hotkey again, a tray left-click, or Alt+F4. Closing hides. Only Quit destroys the window. |
| Footer | Left: status dot and label from `StatusText.Label`. Right: edit button (pencil, opens `https://www.jfedor.org/hid-remapper/`), then settings button (gear). Both icon-only with tooltips and automation names. |
| Settings page | A new "Shortcuts" page in the Settings window with one card: "Open the shortcut sheet" and the picker. A taken combination shows the PowerToys warning state and an InfoBar. |
| Version | 2.1.0 at release time. Not bumped in this work. |

## Section 1: Reading The Mapping Table

### Protocol additions (`Protocol.cs`)

- `CmdGetMapping = 6`, `CmdGetMacro = 17`.
- `GetMapping(uint index)` builds the request. Reply payload, little-endian: `uint target_usage`, `uint source_usage`, `int scaling`, `byte layer_mask`, `byte flags`, `byte hub_ports`. Flags: bit 0 sticky, bit 1 tap, bit 2 hold.
- `GetMacro(uint macro, uint offset)` builds the request. Reply payload: `byte count`, then six `uint` usages. A `count` below 6 ends the macro. A usage of 0 separates steps. Reading starts at offset 0 and advances by 6.
- `GetConfig` reply parsing gains `MappingCount` (`ushort` at payload offset 7) and `OurDescriptorNumber` (`byte` at payload offset 23). Offsets follow the web tool's field list: `uint8 version, uint8 flags, uint8 unmapped_passthrough_mask, uint32 partial_scroll_timeout, uint16 mapping_count, uint32 our_usage_count, uint32 their_usage_count, uint8 interval_override, uint32 tap_hold_threshold, uint8 gpio_debounce_time_ms, uint8 our_descriptor_number, ...`.
- `Mapping` record: `uint Source, uint Target, byte LayerMask, bool Sticky, bool Tap, bool Hold`.
- `MacroStep` is `IReadOnlyList<uint>`; a `Macro` is `IReadOnlyList<MacroStep>`.
- `DeviceConfig` record: `byte DescriptorNumber, IReadOnlyList<Mapping> Mappings, IReadOnlyList<Macro> Macros`. `Macros` always has 32 entries; unused slots are empty.

### Device service (`DeviceService.cs`)

- New event `ConfigChanged` carrying a `DeviceConfig`. Raised after a full successful read. Never raised with a partial result.
- New method `RequestConfigRefresh()`. Queues one read on the session's own loop so it never overlaps a verify tick or the install. Calls while a read is pending coalesce into one.
- The read happens inside the existing session right after `InstallAsync` returns `Connected` or `NoSlot`, before the first verify tick. In `NoSlot` the mappings are still readable, only the active layer is not. `RequestConfigRefresh` is honored in both statuses, so it runs outside the verify loop's `Connected` guard. Reads use the existing `ReadAsync` with its CRC and retry rules. (Builder's call.)
- A short or bad-CRC reply after retries, or a channel exception, aborts the whole read; the service logs it and raises nothing. The flyout shows "Couldn't refresh" when a requested refresh produces no `ConfigChanged` within 5 seconds.
- Replies carry no command tag, so a reply meant for the web config tool parses as a valid mapping with wrong contents. Layers accepts that: the tool only talks during its own load, save, or Monitor tab, and the flyout re-reads on every open, so a stray row heals on the next open. After the mapping loop, `GET_CONFIG` is read once more and the result is discarded if `mapping_count` or `our_descriptor_number` moved, which catches the common case of the tool saving mid-read. (Builder's call; `ponytail:` double-reading every mapping like the install does is the upgrade if stray rows are ever reported.)
- Macro slots are read only for the macro numbers that some mapping targets (usage page `0xFFF2`). Other slots are left empty, so a device with few macros reads fast.
- Disconnect clears nothing. The flyout keeps the last list but shows the disconnected status and a body placeholder.

### Usage names (`UsageNames.cs`)

- Generated from `usages.js` by a one-off script that is not committed. The file header records the upstream commit hash and the date.
- Shape: `static class UsageNames` with `FrozenDictionary<uint, string>` fields `Source`, `SourceExtra`, and `Descriptor0`..`DescriptorN` for every numbered table in the source, plus `string Input(uint usage)` and `string Output(uint usage, byte descriptorNumber)`.
- Unknown usages render as `0x0007001E` style hex, matching the web tool.
- Layer targets (`0xFFF1xxxx`) render as "Layer N". Macro targets (`0xFFF2xxxx`) render as "Macro N". Both come from the table; the code does not special-case them except to recognize macro targets for the macro row and to read the right slots.

## Section 2: Sheet Model (`Layers.Core`)

- `ShortcutSheet.Build(DeviceConfig config, LayerMask active)` returns `IReadOnlyList<LayerSection>`. Pure, unit-tested.
- `LayerSection(int Layer, bool IsActive, IReadOnlyList<ShortcutRow> Rows)`.
- `ShortcutRow(IReadOnlyList<string> Input, ShortcutOutput Output, bool Sticky, bool Tap, bool Hold)`.
- `ShortcutOutput` is one of: `Keys(IReadOnlyList<string>)`, or `MacroOutput(int Number, IReadOnlyList<IReadOnlyList<string>> Steps)` with `Steps` empty for an empty slot.
- Row order within a section follows the device's mapping order. The web tool keeps the user's order, so this matches what they see there.
- Input is one chip per mapping. HID Remapper mappings are single-usage, so `Input` has one element; the list shape is kept so chips render through one code path for inputs, outputs, and macro steps.
- `ShortcutSheetViewModel : ObservableObject` holds `Sections`, `Status` (from `DeviceState`), `Tone`, `IsConnected`, `HasRows`, `RefreshFailed`, and `EmptyMessage`. `State` and `Config` setters rebuild `Sections`. `EmptyMessage` is "Plug in your HID Remapper" when disconnected, `StatusText.Detail` ("This app supports config version 18") in `VersionMismatch`, "No shortcuts set up" when connected with zero mappings, and null otherwise. In `NoSlot` the list shows but no section carries the Active pill, since the layer is unknown. (Builder's call.)

## Section 3: Flyout Window (`Layers.UI`)

- `ShortcutFlyoutWindow : Window` ported from MagicBuds `FlyoutWindow`. Public API: ctor taking `ShortcutSheetViewModel`; `Open(RectInt32? iconRect)`; `Toggle(RectInt32? iconRect)`; `HideFlyout()`; `Shutdown()`; events `CloseRequested`, `SettingsRequested`, `EditRequested`, `Opened`.
- `App` wires `Opened` to `DeviceService.RequestConfigRefresh()`.
- `FlyoutPlacement` (pure, in `Layers.Core.Logic`) ported as is: `PixelRect`, `TaskbarEdge`, `DetectEdge`, `Calculate`. Constants: width 360 DIP, margin 12 DIP. Height is the work area height; the panel decides its own height within that.
- `ScreenGeometry` lives in `Layers.UI` beside `WindowStyles`, reusing `MonitorFromPoint`, `GetMonitorInfo`, `GetDpiForMonitor`, `GetCursorPos` already in `NativeMethods.txt`. Adds `MonitorFromRect`, `Shell_NotifyIconGetRect`, `SetWindowRgn`, `CreateRectRgn`, `SetWindowSubclass`, `DefSubclassProc`, `RemoveWindowSubclass`, `DwmSetWindowAttribute`, `DwmExtendFrameIntoClientArea`, `DwmEnableBlurBehindWindow`, `SetWindowCompositionAttribute` as the ported code needs them.
- `Windhawk.HasTrayOnEveryTaskbar()` ported into `Layers.Core.Services`.
- The `TrayIcon` gains `GetIconRect()` using `Shell_NotifyIconGetRect`.
- `ShortcutSheetView : UserControl`. Layout from top:
  1. Header row: "Shortcuts" title in `SubtitleTextBlockStyle`, and when `RefreshFailed`, a caption "Couldn't refresh" in `SystemFillColorCautionBrush`.
  2. `ScrollViewer` over an `ItemsRepeater` of sections. A section is a header row (layer name, and an "Active" pill in the accent color when `IsActive`) followed by its rows. Rows are a `Grid` with the input chips, a `FontIcon` arrow (`&#xE72A;`), and a `WrapPanel`-style `ItemsRepeater` with a `FlowLayout` for the output chips and flag tags. Macro outputs put the "Macro N" caption above the wrapped steps, separated by chevrons (`&#xE76C;`).
  3. Placeholder in place of the list when `EmptyMessage` is set.
  4. Footer: status dot (`PathIcon` circle, `ToneBrush`) and label on the left; on the right, edit (`&#xE70F;`) then settings (`&#xE713;`) as icon-only `Button`s in a subtle style, with `ToolTipService.ToolTip` and `AutomationProperties.Name`.
- Key chips use the ported `KeyVisual` with `KeyChipStyle`, `MinWidth 36`, `Padding 8`, `FontSize 14`. Chips show text names from `UsageNames`; the PowerToys glyph mapping (Windows logo, Shift, arrows, Enter, Backspace) applies when a name matches those keys.
- Flag tags: a `Border` with `ControlFillColorSecondaryBrush`, radius 4, caption text.
- Escape via `KeyboardAccelerator` raises `CloseRequested`. The first row's first chip takes focus on open so screen readers land in the list.
- Theme follows the app. The window re-renders on `WM_SETTINGCHANGE` through the existing mechanism the HUD uses.

## Section 4: Global Hotkey

- `Hotkey` (`readonly record struct Hotkey(HotkeyModifiers Modifiers, int Key)`) and `HotkeySettings` ported from MagicBuds into `Layers.Core.Logic`. Allowed keys: A to Z, 0 to 9, F1 to F24, Space, Delete. Win+L, Alt+F4, Tab, Shift+Tab are refused. A combination needs Ctrl, Alt, or Win.
- `GlobalHotkey` (`Layers.UI`) owns one registration. `Apply(Hotkey?)` unregisters then registers with `MOD_NOREPEAT` on the tray icon's window. `IsFree(Hotkey)` probes with a throwaway ID. `IsTaken` reports a failed registration. No logging of key names; the Win32 error code only.
- `TrayMessageRouter.Route` gains `WM_HOTKEY` to `TrayAction.Hotkey`. `TrayIcon` raises `HotkeyPressed`. `TrayMessageRouter` also splits `WM_LBUTTONUP` to `TrayAction.OpenFlyout` and `WM_RBUTTONUP` to `TrayAction.OpenMenu`.
- `SettingsStore` gains `LoadShortcut()` returning `Hotkey?` with the default when the value is missing and null when empty, and `SaveShortcut(Hotkey?)`.
- `SettingsViewModel` gains `Shortcut`, `ShortcutTaken`, `SetShortcut(Hotkey?)`. `App` subscribes and calls `GlobalHotkey.Apply`.
- If the default is taken at startup, the flyout still opens from the tray and menu. The Settings page shows the warning.

## Section 5: Settings Page

- New `ShortcutsPage` in the Settings `NavigationView`, between General and About, icon `&#xE765;` (keyboard).
- One `SettingsCard`-style row: title "Open the shortcut sheet", description "Show every shortcut on the connected HID Remapper", and the ported `ShortcutControl` on the right.
- The picker's conflict check calls `GlobalHotkey.IsFree`. A taken combination disables Save with the PowerToys conflict InfoBar.
- `HotkeySettingsChanged` saves, applies, and re-checks. If the new hotkey turned out taken, the previous one is restored with an InfoBar note.

## Section 6: Tray Menu

- `TrayMenuHost` gains a "Shortcuts" item above "Settings…", icon `&#xE765;`, raising `ShortcutsRequested`. `App` wires it to `Toggle` with the icon rect.
- The menu also closes before the flyout opens, so both are never up together.

## Section 7: Packaging and Docs

- `assets/NOTICE-powertoys.txt` with the PowerToys MIT text and the list of ported files, each ported file keeping its Microsoft header. It ships the same way `NOTICE-fluentui.txt` does: a WiX component in `Package.wxs`, an MSIX `Content` include in `Layers.csproj`, and the installer test extended to assert it. (Builder's call: follows the existing pattern.)
- README: Credits mention PowerToys and the web tool's `usages.js`. A new "Shortcut sheet" section under Settings: how to open it, the default hotkey, what it lists, and that editing happens in the web tool.
- `RegisterHotKey` and `WH_KEYBOARD_LL` work in packaged apps without a capability.
- `AllowUnsafeBlocks` is enabled in `Layers.UI` for the hook and `SendInput`.

## Section 8: Testing

- `Layers.Tests`: `Protocol` request and reply parsing for `GET_MAPPING` and `GET_MACRO`; `UsageNames` lookups for keyboard, consumer, mouse button page, MIDI, layer, macro, and unknown; `ShortcutSheet.Build` grouping across layers, flag mapping, macro steps, empty slot, empty config; `Hotkey` parse and format (ported `ShortcutTests`); `FlyoutPlacement` for all four taskbar edges and the auto-hide case; `TrayMessageRouter` left, right, hotkey; `SettingsStore` shortcut round trip, empty, missing; `DeviceService` reads mappings after install and raises `ConfigChanged`, coalesces refreshes, never raises on a bad read, and does not disturb the verify tick ordering (`FakeConfigChannel` learns `GET_MAPPING` and `GET_MACRO`).
- `Layers.UITests`: `ShortcutSheetView` renders a section per layer, the active pill, a macro row with the right chip count, the placeholders, footer buttons' automation names, and the Settings Shortcuts page shows the picker.
- `HardwareTests` (category Hardware): a connected device yields at least the mapping count `GET_CONFIG` reports and the names resolve without hex for keyboard usages.

## Left To The Builder

- Exact chip, tag, and spacing values beyond those named, as long as they match PowerToys and the Windows Settings keyboard pages.
- Whether `ScreenGeometry` returns a record or tuple.
- How the one-off generator for `UsageNames.cs` is written. It is not shipped.
- Internal names and file split inside `Layers.UI` for the ported PowerToys files, keeping their headers.
- Test fixture shapes.
