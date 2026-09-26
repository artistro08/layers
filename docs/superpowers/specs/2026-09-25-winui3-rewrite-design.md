# Layers: WinUI 3 Rewrite Design

Rewrite Layers from Rust/Win32/Direct2D to C# on WinUI 3 and the Windows App SDK, using stock platform features wherever one exists. Behavior stays the same as Layers 1.0.3 except where this spec calls out a deliberate change.

## Goals

- Full feature parity with Layers 1.0.3 (the Rust app at `ac9897a`), including every fix recorded in the commit history.
- Stock everything: WinUI 3 controls, Windows App SDK, WinRT, and .NET BCL. Win32 (via CsWin32) only where no stock API exists.
- Two shipping formats from one codebase: a per-user MSI (unpackaged) and a signed MSIX.
- Tests for every part of the app, plus enforced coding and security standards.

## Non-Goals

These don't exist in 1.0.3 and aren't added: hotkeys, keyboard hooks, a config file, update checks, toast notifications, CLI arguments, log files, CI, MSIX auto-update (`.appinstaller`), Microsoft Store listing.

## Decisions

| Topic | Decision |
|---|---|
| Language | C# on the latest stable .NET |
| UI | WinUI 3, latest stable Windows App SDK, pinned to the exact NuGet version at implementation time |
| Compilation | Native AOT |
| Win32 interop | CsWin32 (`NativeMethods.txt`) |
| Packaging | Unpackaged self-contained build shipped as an MSI (WiX Toolset), and an MSIX, both from one project |
| Distribution | GitHub Releases, both signed with the owner's code-signing certificate |
| Build | Manual, via `build.ps1`. No CI |
| Tests | MSTest (`Layers.Tests`) and MSTest with a WinUI host thread (`Layers.UITests`) |
| Repo | Branch `winui-rewrite`. C# replaces the Rust at the repo root. Merge after parity |
| Settings storage | Registry, `HKCU\Software\Layers`, same value names as 1.0.3 |
| Icons | Segoe Fluent Icons font glyphs, except the layers glyph, which stays the vendored Fluent System Icons `ic_fluent_layer_24_filled` path (Segoe Fluent Icons has no filled layers glyph) |

## Deliberate Behavior Changes From 1.0.3

1. The custom-drawn popup and hover submenu are replaced by a stock `MenuFlyout`.
2. HUD settings (Show HUD, per-layer mutes) move out of the tray menu into a new Settings window.
3. Start at sign-in becomes the "Run on Startup" toggle in the Settings window (previously installer-only).
4. The installer changes from Inno Setup to an MSI, and an MSIX is added.
5. Device reconnect is driven by `DeviceWatcher` add/remove events instead of a 2-second retry loop.
6. Theme changes are detected via `WM_SETTINGCHANGE` ("ImmersiveColorSet") instead of a registry watcher thread.
7. The vendored Fluent SVG glyphs other than the layers glyph are removed in favor of Segoe Fluent Icons. The layers glyph, a minimal SVG path parser (`PathData`), and `NOTICE-fluentui.txt` stay, because Segoe Fluent Icons only has an outlined layers glyph (U+E81E) and the owner wants the solid one.
8. The HUD is a stock XAML `Flyout` sized, placed, and animated like the Windows 11 virtual-desktop switcher: a 127 x 46 desktop-acrylic box 12 px above the bottom of the work area that appears instantly and slides down out of view at the screen edge while fading, instead of a custom window with a 200 ms fade in place. It isn't click-through.

Everything else behaves as in 1.0.3.

## Solution Layout

```
Layers.slnx
Directory.Build.props        Shared settings, version, analyzers
Directory.Packages.props     Central package versions
.editorconfig                Code style
build.ps1                    Test, publish, sign, package
src/
    Layers.Core/             Logic, services, view models. No XAML
    Layers.UI/               WinUI 3 class library: windows and tray icon
    Layers/                  Thin WinUI 3 exe: entry point, App, manifests
    Layers.Installer/        WiX MSI project (Layers.Installer.wixproj)
tests/
    Layers.Tests/            MSTest: Core logic, device service, rendering, storage, view models, lifecycle
    Layers.UITests/          MSTest with a WinUI host thread: XAML/window tests against Layers.UI
assets/app.ico               Exe and package icon (kept)
docs/                        Kept
```

Deleted on the branch: `src/*.rs`, `Cargo.toml`, `Cargo.lock`, `build.rs`, `app.manifest` (replaced by the app project's manifest), `installer/layers.iss`. `assets/NOTICE-fluentui.txt` was deleted and then restored (Task 13b), since the layers glyph path ships again. `.gitignore` is replaced with .NET entries (`bin/`, `obj/`, `dist/`, `*.user`, `.vs/`) plus `.superpowers/`.

### Layers.Core

Class library targeting `net10.0-windows10.0.26100.0`, `IsAotCompatible=true`, no XAML. It holds everything that doesn't need a XAML window, so the test project can reference it directly (a WinUI exe project can't be referenced from a test project cleanly). Pure logic lives in `Logic/`. Windows-facing services live in `Services/`: `DeviceService`, `IHidChannel`, `WinRtHidChannel`, `SettingsStore`, `IStartupRegistration`, `RunKeyStartup`, `PackagedStartupTask`, `TrayIconRenderer`, `TrayMessageRouter`. View models live in `ViewModels/` (`TrayMenuViewModel`, `SettingsViewModel`) and use no WinUI types. CsWin32 is referenced here. The pure logic in `Logic/` contains:

- **Protocol**: packet framing, CRC32, command IDs, expression bytes, slot choice, config version parsing, expression read parsing, monitor report parsing. Ported from `protocol.rs`.
- **LayerMask**: the 8-bit layer set. `Active()` (an empty mask counts as `[0]`), `Badge()` (highest active layer, or null for layer 0), `Label()` ("Layer N" / "Layers a, b").
- **DeviceStatus**: `Disconnected`, `NoSlot`, `Connected`, `VersionMismatch`, plus the tooltip, status label, and detail strings for each.
- **HudRules**: `Allowed(mask)` and `AllowedTransition(from, to)`, plus the "should the HUD fire" rule.
- **HudTiming**: default hold 1.7 s (the switcher's on-screen time in the owner's video; the user can change it with HUD Show Duration), exit 150 ms.
- **HudLifecycle**: open, closing, and closed state for the HUD flyout, so a change during a close reopens it.
- **PathData**: the vendored `ic_fluent_layer_24_filled` path (24x24 view box) and a minimal absolute `M`/`L`/`C`/`V`/`H`/`Z` parser that rejects anything else. Ported from `geometry.rs`.
- **HudPlacement**: box and gap in whole pixels, the flyout's top-center anchor (so its content ends one pixel above the work-area bottom), the host position above it, and the anchor in the host's client DIPs.
- **IconMath**: tray icon size from DPI, box downsample, `CenterInk`, tint into premultiplied BGRA. Ported from `compose.rs`.

### Layers.UI

WinUI 3 class library holding the XAML and window code: `TrayIcon` (hidden window plus `Shell_NotifyIconW`), `TrayMenuHost`, `HudHost` (with `ActiveAcrylicBackdrop`), `HudFace` (the HUD box's border, glyph, and label, shared by `HudHost` and the Settings preview), `SettingsWindow` with its `GeneralPage` and `AboutPage`, and `HudPreview`. It's a library so `Layers.UITests` can load these windows (XAML compiled into an exe can't be loaded from a test host).

### Layers (App)

Thin WinUI 3 exe: `Program.Main` (single instance, DLL search hardening), `App` (composition and wiring), `app.manifest`, `Package.appxmanifest`. `WindowsPackageType` defaults to `None` (unpackaged, `WindowsAppSDKSelfContained=true`). Passing `-p:WindowsPackageType=MSIX` builds the MSIX.

## Section 1: Device Link

### Protocol (unchanged from 1.0.3)

- Config collection: usage page `0xFF00`, usage `0x0020`, used for feature reports.
- Monitor collection: usage page `0xFF00`, usage `0x0021`, used for input reports. It is a separate device interface on the same physical device. Monitor reports must be read from this collection (reading the config collection fails, see `ddb990e`).
- Packets are 33 bytes: `[100, 18, cmd, 26 payload bytes, CRC32 LE]`. The CRC covers bytes 1 through 29.
- Commands: `GET_CONFIG` 3, `RESUME` 11, `APPEND_TO_EXPRESSION` 20, `GET_EXPRESSION` 21, `SET_MONITOR_ENABLED` 22.
- The app never sends `PERSIST_CONFIG`, `CLEAR_EXPRESSIONS`, or `SUSPEND`. It touches device RAM only.
- The expression is `layer_state 0xFF000001 monitor`. It's appended as `[slot, nelems=3, 14 01 01 00 00 FF 2C]`.
- The monitor report is 64 bytes: report ID 101, then 7 items of 9 bytes (usage u32 LE, value i32 LE, hub port). The item with usage `0xFF000001` carries the layer mask in its low 8 bits.
- All 8 layer bits are parsed, although the rp2040 firmware uses 4.

### IHidChannel

```csharp
interface IHidChannel : IDisposable
{
    Task SendFeatureAsync(byte[] report, CancellationToken cancellationToken);
    Task<byte[]> GetFeatureAsync(byte reportId, CancellationToken cancellationToken);
    event EventHandler<byte[]>? InputReport;
}
```

- `WinRtHidChannel` wraps `Windows.Devices.HumanInterfaceDevice.HidDevice`.
- `FakeHidChannel` lives in the test project. It replays scripted replies and records every sent report.

### DeviceService

1. **Discovery.** A `DeviceWatcher` runs on `HidDevice.GetDeviceSelector(0xFF00, 0x0020)`. On `Added`, find the `0x0021` interface with the same `System.Devices.ContainerId`. Open both with `HidDevice.FromIdAsync(id, FileAccessMode.ReadWrite)`. On `Removed` of the open device, dispose the session and report `Disconnected`.
2. **Response reads.** `GetFeatureAsync(100)` is retried up to 10 times with a delay starting at 2 ms and doubling.
3. **Connect sequence.**
   1. `GET_CONFIG`. If the config version isn't 18, report `VersionMismatch`, stay connected, and write nothing.
   2. `GET_EXPRESSION` for all 8 slots (payload `slot u32, offset u32`).
   3. Choose a slot: the one already holding our expression, otherwise the first empty slot, otherwise report `NoSlot` and append nothing (Monitor is still enabled, as in 1.0.3).
   4. For an empty slot: `APPEND_TO_EXPRESSION`, then `RESUME`. RESUME is required, or the expression never evaluates.
   5. `SET_MONITOR_ENABLED(1)`.
   6. Report `Connected` (or `NoSlot`) with an initial mask of `Layers(1)` (layer 0). Re-verify runs only while `Connected`.
4. **Live layer.** `InputReport` from the monitor channel. Reports that are too short, have the wrong report ID, or lack usage `0xFF000001` are skipped. A change is posted only when the mask differs from the last one.
5. **Re-verify** (keeps `ac9897a`). A `PeriodicTimer` on `TimeProvider` fires every 2 s and reads our slot. A miss increments a counter and a hit resets it. After 2 consecutive misses, re-run the install (append plus RESUME) and reset the mask to `Layers(1)`, because RESUME resets the firmware's layer state. A failed reinstall is non-fatal and is retried on the next miss pair. Waiting for 2 misses avoids writing our expression into the user's flash while the web tool's save (CLEAR, then the user's expressions, then PERSIST) is still running.
6. **Serialization.** All feature-report traffic for a session runs on one session task. Status and mask changes are marshaled to the UI thread with `DispatcherQueue.TryEnqueue`.
7. **Errors.** Any exception in a session disposes it and is traced, then the device's loop waits `ReconnectDelay` (2 s, on `TimeProvider`) and retries the session, until the device is removed or the service is disposed. An unplug ends the loop and reports `Disconnected` (the device source's `Departed`, from the watcher's `Removed`), and a replug starts a new one (`Arrived`, from `Added`).
8. **Shutdown.** Cancel the session, send `SET_MONITOR_ENABLED(0)` (best effort, with a short timeout), dispose both channels, and stop the watcher.
9. **MSIX manifest.**
   ```xml
   <DeviceCapability Name="humaninterfacedevice">
       <Device Id="any">
           <Function Type="usage:FF00 0020"/>
           <Function Type="usage:FF00 0021"/>
       </Device>
   </DeviceCapability>
   ```

### Input Validation

All device data is untrusted. Every config reply is checked for length and CRC (the firmware's replies don't echo the command byte, so the CRC is the integrity check, as in 1.0.3), and every monitor report for length and report ID, before it's parsed. Slot indexes are range-checked (0–7). The parser never indexes past the buffer. Bad data is skipped or treated as a miss, never thrown to the UI.

### Risk and Probe

It's unproven that WinRT HID can open this vendor-defined collection in an unpackaged app. Plan task 1 is a throwaway probe: open both collections, send `GET_CONFIG`, and receive one monitor report. If it fails, `IHidChannel` gets a `Win32HidChannel` using `CreateFile` plus `HidD_SetFeature`/`HidD_GetFeature`/`ReadFile` via CsWin32. Nothing else changes.

## Section 2: Tray Icon and Menu

### TrayIcon

- A hidden top-level Win32 window (class `LayersMessageWindow`, `WS_EX_TOOLWINDOW`, never shown). It is not `HWND_MESSAGE`, so it receives DPI and broadcast messages (`b9addfc`). Its window procedure runs on the UI thread, which the WinUI dispatcher already pumps.
- `Shell_NotifyIconW` with `uID` 1, `NIF_MESSAGE | NIF_ICON | NIF_TIP`, callback `WM_APP + 1`.
- Left or right button-up opens the menu. Other callback messages are ignored.
- `RegisterWindowMessageW("TaskbarCreated")`: on that message, re-add the icon with `NIM_ADD` and refresh it.
- Refresh triggers: device status or mask change, `WM_SETTINGCHANGE` with "ImmersiveColorSet" (taskbar theme), `WM_DPICHANGED`, and `WM_SETTINGCHANGE` in general.
- Reentrancy: `Shell_NotifyIconW` can re-enter the window procedure through a cross-process SendMessage (`f553b57`). The refresh path is guarded by a busy flag and skips nested calls, and nothing in the window procedure throws.
- Tooltips: "HID Remapper disconnected", "Connected, layer unavailable", "Unsupported firmware version", or the layer label.

### TrayIconRenderer

- Uses plain GDI through CsWin32 (no COM, AOT-safe): the layers glyph filled white (`BeginPath`/`PolyBezierTo`/`FillPath`, nonzero winding), or the digit drawn white with `ANTIALIASED_QUALITY`, onto a black 32bpp DIB, and the red channel read back as coverage.
- Size: `RoundUpToMultipleOf4(16 * dpi / 96)`. DPI comes from `Shell_TrayWnd`, falling back to the hidden window, then 96.
- Content: layer 0, or any status other than Connected, shows the solid layers glyph (`PathData.LayersGlyph`, Fluent System Icons `ic_fluent_layer_24_filled`, as in 1.0.3). Segoe Fluent Icons was checked (every code point rendered) and has only the outlined `MapLayers` U+E81E. In the tray, the path's own bounds are fitted to the icon with a 1/16 margin on the longer side (not its 24 px view box, whose padding left the icon about 3/4 full), so the ink matches the old U+E81E glyph's extent (14 of 16 px). Otherwise it shows `LayerMask.Badge()` as a digit in "Segoe UI Variable Display" Bold, filling the whole icon.
- Pipeline: draw alpha at 4× with antialiasing, box downsample, `CenterInk` (re-center on the actual pixel bounds, because text layout centers the line box, not the ink), then tint to premultiplied BGRA and build an HICON with `CreateIconIndirect`.
- Tint follows the taskbar theme (`SystemUsesLightTheme`): white on a dark taskbar, `#191919` on a light one. A missing value means a dark taskbar.
- Every GDI object is released on success and error paths, and the previous HICON is destroyed after it's replaced.

### TrayMenu

- Host: a tiny borderless, transparent WinUI window that never shows in the taskbar. On open: move it to the cursor, bring it to the foreground, and call `MenuFlyout.ShowAt(root, point)`. On `Closed`: hide the host.
- Every item sets `Padding="{ThemeResource MenuFlyoutItemThemePaddingNarrow}"`. The stock menu otherwise uses its touch-sized padding when the host hasn't seen mouse or keyboard input yet, which made the first open after launch oversized.
- Every item icon is exactly 16x16 (explicit `Width`/`Height` on `PathIcon`), because the item's icon box is a `Viewbox` that otherwise scales a `PathIcon` by its geometry extent and pushes it off center.
- Items, in order:
  1. Status: disabled `MenuFlyoutItem`, with an 8 px `PathIcon` dot centered in the 16x16 icon box, in `SystemFillColorSuccessBrush` (Connected), `SystemFillColorCautionBrush` (NoSlot, VersionMismatch), or `SystemFillColorCriticalBrush` (Disconnected). Text: "Connected", "Connected, layer unavailable", "Unsupported firmware", "Disconnected".
  2. Status detail (only for NoSlot/VersionMismatch): disabled item with "All 8 expression slots are in use" or "This app supports config version 18".
  3. Layer: disabled item, solid layers glyph (`PathIcon`, 16 px), `LayerMask.Label()`.
  4. Separator.
  5. "Settings…" (Settings glyph): opens or activates the Settings window.
  6. Separator.
  7. "Quit" (PowerButton glyph): runs shutdown.
- Items bind to a `TrayMenuViewModel`, so an open menu updates live when the status or mask changes.
- Theme, DPI, light dismiss, Esc, keyboard, and screen reader support come from the stock control.

## Section 3: HUD

- A stock XAML `Flyout`, opened from `HudHost`: an invisible host window like `TrayMenuHost` (1x1 borderless `OverlappedPresenter`, `IsAlwaysOnTop`, not in switchers, `WS_EX_LAYERED` at alpha 0 because Windows clamps the size up to its minimum). The flyout draws in its own popup window.
- Box, matching the switcher in the owner's recording (159 x 58 physical px at 125%): the `FlyoutPresenterStyle` makes the stock presenter transparent, with zero padding, zero minimum size, no border, and no default shadow. The flyout content is the box plus a transparent gap under it (bottom padding of `HudPlacement.GapDips`). The box is 46 DIPs tall (rounded to whole pixels, `HudPlacement.BoxDips`), at least 127 DIPs wide, with 16 DIP horizontal padding around the content (it grows to fit long labels like "Layers 1, 3, 5, 7" and never shrinks below 127), and 6 DIP corners with the stock `FlyoutBorderThemeBrush` and `FlyoutBorderThemeThickness`.
- Acrylic, like the switcher: a stock `SystemBackdropElement` (`CornerRadius` 6) fills the box behind the content, so the translucent blurred desktop acrylic moves, fades, and clips with the box. Its `SystemBackdrop` is `ActiveAcrylicBackdrop`, the stock `DesktopAcrylicController` with a `SystemBackdropConfiguration` that always reports input as active (the stock `DesktopAcrylicBackdrop` shows its flat fallback color on a window that isn't active, and the HUD never is), themed from the XAML root. `FlyoutBase.SystemBackdrop` was tried first: it fills the whole flyout (gap included) and stays put while the content slides. `ContentExternalBackdropLink` isn't in this Windows App SDK's projection. On disconnect the backdrop removes and disposes its controller, then closes the disconnected target itself (as `IDisposable`, on the UI thread): XAML drops each target without closing it, so the last release otherwise comes from the .NET finalizer thread, and closing the target's UI-thread content site there fails fast (`RPC_E_WRONG_THREAD` in Microsoft.UI.Input.dll), which crashed the app after a few dozen HUD shows. Where `DesktopAcrylicController.IsSupported()` is false, no controller is created and the box gets a plain `AcrylicBackgroundFillColorDefaultBrush` background (6 DIP corners) instead.
- Content (the `HudFace` user control, also used by the Settings preview so the two can't drift: the stock flyout border, 6 DIP corners, at least 127 DIPs wide), centered as a group: solid layers glyph (`PathIcon`) at 16, then `LayerMask.Label()` at 15, regular weight, with a 10 px gap. The 15 DIP label gives the switcher's 13 physical px cap height at 125%. The 16 DIP icon box draws a glyph about 15 px tall, just over the cap height and sitting on the baseline, so it reads as the same size as the text.
- Placement: the flyout's content (box plus gap) ends one physical pixel above the bottom of `DisplayArea.Primary.WorkArea`, horizontally centered, so the box rests 12 px (scaled and rounded, 15 physical px at 125%, measured from the owner's screenshot of the switcher) above it like the Windows 11 virtual-desktop switcher. With a bottom taskbar, it sits just above the taskbar. It doesn't follow the cursor's monitor.
  - `HudPlacement.Anchor(work, scale)` is the flyout's top center: the work-area center, `BoxPixels + GapPixels` above the bottom. The flyout opens with `Placement = Bottom` below it. A point on the edge row itself (Placement Top at the bottom) belongs to a monitor directly below, and the flyout was then placed on that monitor, 19 px too high. The flyout's own gap (`HudPlacement.GapDips`) is one pixel short of `GapPixels`: a flyout ending exactly on the edge fails the stock fit check (`point + height > monitor bottom`) by a rounding error and flips to open upward from the anchor, which put the box a whole flyout height (73 px at 125%) too high.
  - `HudPlacement.HostPosition` puts the host's bottom-left on the anchor, so its clamped minimum size stays on the primary monitor (a host hanging below lands on a monitor below it, and the flyout then renders at that monitor's scale).
  - `HudPlacement.FlyoutPosition` converts the anchor to the host's client coordinates in DIPs (the borderless host's client area is inset by an invisible frame, read with `ClientToScreen`), with `ShouldConstrainToRootBounds = false`.
- No focus: the host is shown with `AppWindow.Show(false)` and the flyout opens with `ShowMode = Transient` in its `FlyoutShowOptions` (the options' default, `Auto`, overrides the flyout's own `ShowMode` and takes focus). Topmost is re-asserted with `SetWindowPos(HWND_TOPMOST, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE)` on every show (`4ac5a4d`).
- Appear: `AreOpenCloseAnimationsEnabled = false`, so it appears instantly with no slide or fade.
- Exit: after the hold, an `Exit` `Storyboard` on the box moves its `TranslateTransform.Y` from 0 to its height plus the gap, and its opacity from 1 to 0, over `HudTiming.Exit` (150 ms, set on both animations in code) with a cubic ease-in, then calls `Hide()`. The flyout's content bounds end on the edge and clip the sliding box, so it disappears exactly at the bottom edge, never onto a monitor below.
- Timing: it holds for the user's HUD Show Duration (`HudSettings.HudHoldMs`, 1.7 s by default, passed to `HudHost.Show` on every show, so a change applies on the next one) after the last layer change, then runs the exit. Opening the flyout also restarts the hold, so a reopened HUD always hides after its duration. A new change while it's open or exiting stops the exit (which snaps the box back to 0 and full opacity), updates the label, and restarts the hold without reopening. The host hides when the flyout closes. `HudLifecycle` tracks open, closing, and closed: a change that lands between `Hide()` and `Closed` is remembered, and the flyout reopens (queued on the dispatcher) once it has closed, so every change ends with a visible HUD. A reopen can fail silently (the flyout never opens, so `Closed` never fires), and the flyout can also close on its own, so a show or hold end that finds the lifecycle open but `IsOpen` false (and not closing) resets it (`HudLifecycle.Lost`): a show reopens at once, a hold end hides the host instead of animating unloaded content. An `Exit` completion that was already queued when a show stopped the exit is ignored (the storyboard reads `Stopped`), so it can't hide the HUD that show just brought back. Once the host window has closed (quit), `Closed` does nothing.
- Trade-off: a native flyout isn't click-through, so for the moment it's up it blocks clicks on the small area it covers.
- It fires only when all of these hold: the mask changed, the status was Connected before and is Connected now, and `HudRules.AllowedTransition(previous, current)` is true.
  - `Allowed(mask) = HudEnabled && (mask & HudSuppressedLayers) == 0`. Every active layer must be allowed (`0817e8d`).
  - `AllowedTransition(from, to) = Allowed(from) && Allowed(to)`. Silent in both directions (`6ef0056`).
- `taskkill` (and any WM_CLOSE sender that only targets the front window) reaches the HUD host once it has shown, so closing the HUD host quits the app, like closing the menu host.

## Section 4: Settings Window, Storage, Startup, Lifecycle

### SettingsWindow

- A stock WinUI `Window` with `MicaBackdrop`, `ExtendsContentIntoTitleBar = true`, fixed size 760×600 logical (the 160 DIP pane plus a 600 DIP content column, tall enough that the whole General page fits without scrolling; the pages keep their `ScrollViewer` for smaller screens), not resizable or maximizable, centered on the monitor under the cursor.
- Title bar: the stock `TitleBar` control, title "Layers Settings", the app icon (`ImageIconSource` from the shipped `app.ico`), no back button, and the pane toggle (`IsPaneToggleButtonVisible`, which toggles `NavigationView.IsPaneOpen`). `AppWindow.TitleBar.PreferredHeightOption = Tall` makes the caption buttons 48 DIPs, and the `TitleBar`'s own resources are overridden to match: `TitleBarCompactHeight` 48 and `TitleBarPaneToggleButtonWidth` 44, so the toggle is a 44×44 square inside its stock 2 DIP margin.
- Navigation: a stock `NavigationView` with `PaneDisplayMode="Left"`, `OpenPaneLength` 160, no settings item, no back button, and its own pane toggle hidden. Menu item "General" (`FontIcon` U+E713), and footer item "About" (`FontIcon` U+E946) at the bottom of the pane. "General" is selected on open. There are no page titles (`AlwaysShowHeader="False"`, no header). The content has 24 DIP padding on every side, so it starts 24 DIPs below the title bar. The content is a stock `Frame` (`IsNavigationStackEnabled="False"`) holding two stock `Page`s, `GeneralPage` and `AboutPage` (`NavigationCacheMode="Required"`, so each is built once per window). Selecting an item navigates the frame with the stock `DrillInNavigationTransitionInfo`: the leaving page zooms in and fades out, and the arriving page zooms down to size and fades in. The first navigation, on open, uses `SuppressNavigationTransitionInfo`. The window's one `SettingsViewModel` is the navigation parameter, and each page keeps it as its `Model` for `x:Bind` (set in `OnNavigatedTo`, before the page loads and its bindings initialize). The pane items are set in code through `MenuItemsSource`/`FooterMenuItemsSource` with concrete lists, because with CsWinRT's AOT mode the XAML parser can't add items to the native `MenuItems` vector.
- Single window: "Settings…" activates the existing window if it's open. Closing destroys it, and the app keeps running.
- Content, stock controls only. Sections have `BodyStrongTextBlockStyle` headings, and a stock divider line (a 1 DIP `Rectangle` filled with `DividerStrokeColorDefaultBrush`) separates sections:
  - **General page**:
    - Section "General": `ToggleSwitch` "Run on Startup". MSIX only: if `StartupTask.State` is `DisabledByUser`, the switch is disabled and shows "Turned off in Settings › Apps › Startup."
    - Section "HUD", one group with no dividers inside, in this order: the heading, the explanation, the Show HUD row, the demo with its Play Animation button, then the HUD Show Duration row. The tab order follows it (Show HUD, "Choose layers...", Play Animation, the slider).
      - Explanation (secondary text), right under the heading, with 16 DIPs below it (an 8 DIP bottom margin plus the section's 8 DIP spacing) so it doesn't crowd the Show HUD row: "The HUD (Heads Up Display) is a small pop-up that appears at the bottom of your screen when you switch layers on your HID Remapper."
      - A row with the stock `ToggleSwitch` on the left and, right-aligned at the row's edge and vertically centered on it, a stock `ComboBox` with the placeholder "Choose layers..." (automation name "Show for these layers") that never keeps a selection. The switch has no `Header`: `OnContent` and `OffContent` are both "Show HUD", so the label sits beside the knob on the same line in regular weight. Its automation name is "Show HUD" and its access key Alt+H. The ComboBox's items are 8 `CheckBox` controls "Layer 0" through "Layer 7" (checked means not suppressed), each filling its row so a click anywhere on the row toggles it.
      - A live HUD demo (`HudPreview`): a rounded strip (8 DIP corners, stock card stroke) as wide as the content and 150 DIPs tall. That leaves about 92 DIPs of wallpaper above the HUD's box plus its gap (58 DIPs), roughly 1.6 times the HUD, so it reads as the bottom strip of the desktop, and the Play Animation button (8 DIPs in, about 30 tall) sits well clear of the HUD; the whole General page still fits the 600 DIP window without scrolling. It shows the user's wallpaper (`SystemParametersInfo(SPI_GETDESKWALLPAPER)` through CsWin32) as an `ImageBrush` with `Stretch="UniformToFill"` and `AlignmentY="Bottom"`, decoded at 1200 px wide. With no wallpaper (solid color), a missing file, or a file that fails to load (`ImageFailed`), the strip shows the stock `SolidBackgroundFillColorSecondaryBrush`. A `HudFace` at the real HUD's size (`HudPlacement.BoxHeight`, "Layer 1" from `LayerMask`) sits at bottom center, `HudPlacement.BottomGap` above the strip's edge, over in-app acrylic (`AcrylicBackgroundFillColorDefaultBrush`). At rest it's hidden (opacity 0).
      - Whenever the HUD Show Duration slider changes (not while the page loads), and when its Play button is pressed, the demo replays the real sequence: the HUD appears instantly, holds for the current duration, then slides down by its height plus the gap while fading over `HudTiming.Exit` (cubic ease-in), clipped by the strip's rounded bottom edge. A change or press mid-sequence restarts it. The sequence is one storyboard of two `DoubleAnimationUsingKeyFrames` (translate Y and opacity), each with a discrete "shown" key at 0, a discrete "shown" key at the hold, and an eased "gone" key at hold plus exit, so the storyboard owns the whole timeline: plain exit animations with a `BeginTime` left the previous run's held end value on screen during the hold, so a replay after an exit showed nothing until the exit. After the exit the HUD stays hidden (the storyboard's default `HoldEnd`), like the real HUD, so every replay visibly brings it up. The demo stops, leaving the HUD hidden, when the preview unloads (navigating away or closing the window).
      - Play button: a stock `Button` with the stock `AccentButtonStyle` (the system accent color, stock corners), a `FontIcon` Play glyph (U+E768, 14 DIP) and the text "Play Animation" (automation name the same) in a horizontal `StackPanel`, both `VerticalAlignment="Center"` so the icon sits centered on the text's line. It's top-right over the strip (8 DIP in), clear of the HUD. It replays the demo with the slider's current duration.
      - "HUD Show Duration (In Seconds)" as a header line, then a stock `FontIcon` speedometer (Segoe Fluent Icons SpeedHigh, U+EC4A) left of a `Slider` from 0.5 to 5.0 seconds in 0.1 s steps (stock value tooltip), vertically centered on its track, with ticks under it (`TickPlacement="BottomRight"`, `TickFrequency="0.5"`). The slider's automation name is "HUD Show Duration (In Seconds)".
      - The drop-down (and so the checkboxes), the slider, the demo, and the Play Animation button are disabled while Show HUD is off (`IsEnabled` bound to `HudEnabled`). The disabled demo dims to 0.4 opacity like the stock disabled controls, stops any running sequence (the HUD goes back to hidden), and ignores replays, so a slider change doesn't play it. Turning Show HUD back on restores full opacity and Play.
  - **About page**: everything centered both ways (scrolling only when the window is too short): the app icon (`Assets/Square150x150Logo.png` at 80 DIPs) over the name "Layers", then a row "Made by" with a "artistro08" link to `https://github.com/artistro08`, then a row "Version" with the version (2.0.0) and a "GitHub" link to the repo, a divider, then "Licenses" with links "License (MIT)" (`https://github.com/artistro08/layers/blob/main/LICENSE`) and "Third-party notices" (`https://github.com/artistro08/layers/blob/main/assets/NOTICE-fluentui.txt`). The links are stock `Hyperlink` elements in `TextBlock`s, so they sit flush with the text above them.
- Every change saves immediately, and the HUD rules use it on the next layer change.
- Accessibility: logical tab order, `AutomationProperties.Name` on every control, link, and navigation item, and access keys: Alt+S "Run on Startup", Alt+H "Show HUD", Alt+D "HUD Show Duration (In Seconds)", Alt+A "artistro08", Alt+G "GitHub", Alt+L "License (MIT)", Alt+T "Third-party notices".
- Binding: `SettingsViewModel` implements `INotifyPropertyChanged`, bound with `x:Bind`. No MVVM library. The slider binds two-way to `HudHoldSeconds`, which snaps to tenths, clamps to 0.5..5.0, and ignores a value equal to the stored one, so binding echoes can't loop. The layer checkboxes' `ItemsSource` is set in code (in `GeneralPage.OnNavigatedTo`) to a concrete `List<LayerOptionViewModel>`, because CsWinRT's AOT mode can't marshal the array behind `IReadOnlyList` (setting it throws `E_INVALIDARG`).

### SettingsStore

- Key `HKCU\Software\Layers`. `HudEnabled` is a `REG_DWORD` defaulting to 1. `HudSuppressedLayers` is a `REG_DWORD` bitmask defaulting to 0. `HudHoldMs` (new in 2.0) is a `REG_DWORD` HUD Show Duration in milliseconds defaulting to 1700. The stale `SeenLayers` value is ignored.
- Load validates: a missing value or the wrong type uses the default, the mask is limited with `& 0xFF`, and `HudHoldMs` is clamped to 500..5000.
- Save is best effort: failures are traced and never crash the app.
- The root key path is a constructor parameter, so tests can point it at a throwaway key.
- MSIX virtualizes HKCU writes per package, so the MSI and MSIX builds keep separate settings.

### Start at Sign-In

`IStartupRegistration` with `IsEnabled`, `IsControllable` (false when disabled by the user or by policy), `SetEnabledAsync(bool)`.

- `RunKeyStartup` (unpackaged): `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `Layers` = the quoted full exe path. The MSI writes it at install (on by default) and removes it on uninstall.
- `PackagedStartupTask` (MSIX): `Windows.ApplicationModel.StartupTask` with task ID `LayersStartup`, enabled by default in the manifest. A failed WinRT call is traced and reads as not enabled, not controllable, with no note, so it can't crash the app.
- Picked at launch based on whether the app is packaged.

### Lifecycle

- Single instance: `AppInstance.FindOrRegisterForKey("LayersTrayApp")`. If another instance owns the key, the new process exits silently (same as 1.0.3).
- `DispatcherShutdownMode.OnExplicitShutdown`, so closing a window never exits the app.
- Startup: `SetDllDirectory("")` (keeps the current directory out of DLL search; `SetDefaultDllDirectories` breaks the self-contained unpackaged WinUI build), load settings, create the hidden window and tray icon, start `DeviceService`.
- Startup failure: `MessageBoxW` titled "Layers" with `MB_ICONERROR` and the error message, then exit.
- Quit: stop `DeviceService` (monitor off, channels disposed), `NIM_DELETE` the tray icon, close the HUD and Settings windows, `Application.Current.Exit()`.
- Logging: `System.Diagnostics.Trace` only. No log files.

## Section 5: Packaging and Build

### MSI (Layers.Installer)

- WiX Toolset via `WixToolset.Sdk`, built with `dotnet build`.
- Per-user (`Scope="perUser"`), installs to `%LocalAppData%\Programs\Layers`, no UAC prompt, x64.
- Payload: the Native AOT publish output of the unpackaged app.
- Start Menu shortcut "Layers".
- Run key `Layers` written on install as a default-on feature, and removed on uninstall.
- `MajorUpgrade`, so a newer MSI replaces an older one. A fixed `UpgradeCode` GUID is generated once and committed.
- On uninstall, the running app is closed before files are removed.
- Signed.
- Note: WiX v6 and later have an Open Source Maintenance Fee for commercial users. The owner confirms whether it applies before release.

### MSIX

- Built from the same app project with `-p:WindowsPackageType=MSIX`.
- Capabilities: `runFullTrust` and the HID `DeviceCapability` only. No `internetClient`.
- `StartupTask` extension `LayersStartup`, enabled.
- Publisher matches the signing certificate subject.
- Signed.

### Version

A single `<Version>` in `Directory.Build.props` flows into the assembly and the MSI `ProductVersion`. The MSIX `Identity Version` (4-part) is hard-coded in `Package.appxmanifest`, and `VersionSyncTests` fails the build when it drifts from `<Version>`.

### build.ps1

Header comment block describing its outputs, what it's for, and its dependencies (.NET SDK, Windows SDK `signtool`).

1. `dotnet test`. Stop on failure. Hardware tests are excluded.
2. Publish the unpackaged Native AOT build, sign `Layers.exe`, build the MSI, sign it. Output: `dist/Layers-<version>.msi`.
3. Build the MSIX, sign it. Output: `dist/Layers-<version>.msix`.

Signing uses `signtool sign /fd sha256 /tr <timestamp url> /td sha256` with the certificate from the `LAYERS_SIGN_CERT` (path or thumbprint) and `LAYERS_SIGN_PASSWORD` environment variables. If `LAYERS_SIGN_CERT` is unset, signing is skipped with a warning. MSIX installs then need a trusted test certificate.

## Section 6: Standards

### Coding

`Directory.Build.props` for every project:

```xml
<Nullable>enable</Nullable>
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<AnalysisLevel>latest-recommended</AnalysisLevel>
<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
<IsAotCompatible>true</IsAotCompatible>
```

- 4-space indents in C# and XAML. 2 spaces only in files a formatter owns.
- A Title Case comment above each logical block. Section banners in long classes.
- Aligned `=` in related assignment groups.
- Guard clauses first.
- XML doc comments on every public type and member: a summary, a paragraph explaining the behavior and why, then `<param>`, `<returns>`, and `<exception>` as needed.
- XAML: one attribute per line on complex elements, and a comment label on each block.
- Native handles are wrapped in `SafeHandle` or disposed in `using`/`finally`.
- No reflection-based serialization. JSON isn't used.

### Security

- `asInvoker` manifest. Per-user install for both formats.
- Device and registry input validated as described in Sections 1 and 4.
- No network access. No internet capability in the MSIX.
- No secrets in the repo. Signing inputs come from environment variables.
- All shipped binaries and packages are signed with an RFC 3161 timestamp.
- `SetDllDirectory("")` at startup, so the current directory is out of DLL search.
- Registry values only toggle HUD behavior. Nothing read from the registry is executed or used as a path. The Run key value is written by the app, never read and executed by it.

## Section 7: Testing

`dotnet test` runs everything without a device. `[TestCategory("Hardware")]` tests need a real HID Remapper and are excluded by default.

### Layers.Tests (MSTest)

**Protocol and LayerMask** (ports the 36 `protocol.rs` tests): framing, CRC, expression bytes, slot choice (reuse, first empty, none), config version parsing, monitor parsing (found, missing usage, short buffer, wrong report ID), `Active`, `Badge`, `Label`. Malformed input never throws or reads out of range.

**IconMath** (ports the 13 `compose.rs` tests): downsample, premultiply, `CenterInk`, size for 96/120/144/192 DPI.

**HudRules** (ports the 11 `settings.rs` tests plus connect cases): a table test covering disabled, single mute, stacked mute, both directions, connect and reconnect never firing, and unchanged mask not firing.

**HudTiming, HudPlacement, and HudLifecycle**: the 1.7 s default hold and 150 ms exit; the box and gap round to whole pixels and the anchor sits box plus gap above the work-area bottom at 100/125/150/200% and on a negative-origin work area; the host sits above the anchor; the flyout position converts from the client origin at 100/125/150% and on a negative-origin monitor; a show while closing reopens on `Closed`, a hide when not open does nothing, a lost flyout resets so the next show opens, and a lost check is ignored while closing or closed.

**DeviceService** with `FakeHidChannel` and `FakeTimeProvider`:
1. The connect sequence is in order, with RESUME after APPEND.
2. The wrong version gives VersionMismatch with no writes.
3. All slots full gives NoSlot with no APPEND or RESUME.
4. An existing expression is reused with no append.
5. Live reports: change posted, same mask ignored, malformed ignored.
6. Re-verify: 1 miss means no reinstall, 2 misses mean one reinstall and a mask reset to layer 0, and a failed reinstall doesn't crash.
7. Feature read retry: 10 attempts with doubling delay, then failure.
8. Device removal gives Disconnected, and re-add reconnects.
9. Shutdown sends monitor off and disposes both channels.
10. Replies with a bad CRC or short length are rejected and retried.

**PathData** (ports the `geometry.rs` tests): lines, cubics, multiple figures, signs and decimals, separators, repeated commands, `V`/`H`, rejection of relative commands, arcs, truncated runs, and data without a leading command, plus the vendored glyph parsing into 3 figures inside its view box.

**TrayIconRenderer** (real GDI): correct size, layer 0 draws the glyph, other layers draw the digit, ink is centered within 1 px, tint is right for each taskbar theme, and 1,000 renders leave the process GDI object count unchanged.

**Tooltip and menu view model**: text for all 4 statuses and for single and stacked layers, dot brush per status, detail row visibility, and live update on change.

**Tray message routing**: button-up (left and right) opens the menu, other messages are ignored, and `TaskbarCreated` re-adds the icon.

**SettingsStore** (throwaway `HKCU\Software\LayersTests\<guid>`, deleted after each test): defaults when missing, round trip (including `HudHoldMs`), mask limited to 8 bits, hold clamped to 500..5000 and defaulted when missing, wrong value type falls back to the default.

**RunKeyStartup** (throwaway key): enable, disable, quoted path, state read back.

**SettingsViewModel**: toggles save, checkboxes map inverted to the mask, checkboxes disabled when the HUD is off, startup switch disabled when not controllable, HUD Show Duration loads, saves, notifies, snaps to tenths, clamps, and ignores echoes.

**Lifecycle** (integration, launches the real exe): a second instance exits and the first keeps running, and Quit removes the tray icon and ends the process.

### Layers.UITests (MSTest, WinUI `Application` started on a test host thread, `[UITestMethod]`)

- The Settings window loads stored values, there's a single window, and every control (the drop-down's checkboxes included) has an automation name. The "Choose layers..." drop-down holds the 8 layer checkboxes, unchecking one saves, and it's disabled while the HUD is off. The slider has its "HUD Show Duration (In Seconds)" header, ticks, and speedometer icon, and moving it (through its automation peer) saves and shows the demo, which is hidden at rest and ends hidden after the hold and exit. Turning Show HUD off stops a running demo, dims it, disables Play, and a slider change doesn't replay it; turning it back on makes Play work again. The "Play Animation" button uses the stock `AccentButtonStyle`, centers its icon on its text's line, and shows the demo again after it has ended. The HUD section runs heading, explanation (verbatim, 12 DIPs or more below it), Show HUD row (label as the switch's on/off content, no header), demo (150 DIPs tall, HUD hidden at rest), then the slider row. The preview shows the HUD face at the real size and gap and falls back to the fill with no wallpaper, a missing file, or a broken image. Navigating to About (and back) works through the frame, and navigating away stops the demo.
- The HUD host is topmost and not in switchers, and showing the HUD doesn't change the foreground window.
- The tray menu contains the expected items for each status.

### Hardware (opt-in)

With a real device: connect reaches Connected, a layer change arrives, and a CLEAR_EXPRESSIONS from the web tool is followed by a reinstall within about 4 s.

## Parity Checklist (from 1.0.3)

- [ ] Tray icon: glyph at layer 0 or when not connected, digit for the highest layer, taskbar-theme tint, DPI from `Shell_TrayWnd`, multiple-of-4 size, ink centering
- [ ] Tray tooltips for all 4 statuses
- [ ] Left and right click open the menu
- [ ] Icon re-added on `TaskbarCreated`
- [ ] Refresh on device change, theme change, DPI change, setting change
- [ ] Reentrancy-safe tray refresh
- [ ] Menu: status with colored dot and degraded detail, layer label, Settings, Quit
- [ ] Open menu updates live
- [ ] HUD: switcher size (159 x 58 px at 125%), primary-monitor bottom-center placement, live acrylic that moves with the box, instant appear, 1.7 s hold then 150 ms slide-down fade clipped at the screen edge, restart while open, exiting, or closing, no focus, topmost re-assert
- [ ] HUD rules: mask test over all active layers, silent both directions, never on connect or reconnect
- [ ] Settings in `HKCU\Software\Layers` with the same value names and defaults
- [ ] Device: config and monitor collections, version 18 gate, slot reuse or first empty, APPEND then RESUME, monitor on, mask changes only on difference
- [ ] Re-verify every 2 s, reinstall after 2 misses, reset mask after reinstall
- [ ] Never sends PERSIST_CONFIG, CLEAR_EXPRESSIONS, SUSPEND
- [ ] Reconnect after unplug
- [ ] Monitor off on quit
- [ ] Single instance, silent second launch
- [ ] Startup error message box
- [ ] Per-user install, Start Menu shortcut, start at sign-in on by default, app closed before uninstall
- [ ] PerMonitorV2 DPI awareness (WinUI default)
