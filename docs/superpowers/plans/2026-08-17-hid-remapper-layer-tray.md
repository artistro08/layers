# Layers — HID Remapper Layer Tray Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Windows 11 tray application that shows the active HID Remapper layer as a digit badge on a Fluent tray icon, with a custom-drawn popup showing connection status, active layers, and quit.

**Architecture:** A single process. Pure logic (wire protocol, SVG path parsing, alpha compositing) lives in dependency-free modules that are fully unit tested. A background thread owns the `hidapi` handle and posts window messages to the UI thread; the UI thread owns every Direct2D object and does all drawing. The device's layer state is obtained by injecting a three-opcode expression into a free expression slot and reading it back over the firmware's Monitor stream.

**Tech Stack:** Rust 2021, `windows` 0.62, `hidapi` 2.6, `crc32fast` 1.5, Direct2D + DirectWrite + WIC, Inno Setup 6.

**Spec:** `docs/superpowers/specs/2026-08-17-hid-remapper-layer-tray-design.md`

## Global Constraints

- Target `x86_64-pc-windows-msvc`. Windows 10 1809 or later.
- **Never send `PERSIST_CONFIG` (7).** It writes device flash. It must not appear anywhere in the source.
- **Never send `CLEAR_EXPRESSIONS` (19).** It wipes all eight expression slots including the user's own.
- **Never send `SUSPEND` (10).** A crash while suspended leaves the user's keyboard and mouse dead.
- Config protocol constants are fixed: report ID 100 for config, report ID 101 for monitor, config version 18, 33-byte packets.
- **Check the firmware's config version before writing anything.** Every opcode number and command byte here belongs to version 18. On a mismatch, report it and write nothing.
- The sentinel usage is `0xFF000001` and must be identical in the injected expression and the monitor filter.
- CRC32 covers packet bytes `1..29` — everything after the report ID and before the four CRC bytes.
- The vendored Fluent path is MIT licensed and requires `assets/NOTICE-fluentui.txt` to ship.
- **No git commits.** The user's standing instruction is that nothing is committed unless they explicitly ask, and this directory is not a git repository. Commit steps are therefore omitted from every task; run the tests and stop.

## Deviations from the spec

Two, both decided while planning, both already approved:

1. The spec calls for `D2D1_COMPOSITE_MODE_DESTINATION_OUT` to punch the badge hole. That requires an `ID2D1DeviceContext`, which requires a D3D11 device and DXGI. This plan renders glyph, hole, and digit as three separate alpha-coverage buffers and combines them arithmetically instead. Same result, no D3D11, and the combine step becomes testable.
2. The spec calls for `DWMWA_SYSTEMBACKDROP_TYPE` acrylic on the popup. That does not compose with a Direct2D-painted client area without `WS_EX_NOREDIRECTIONBITMAP` and a DXGI composition swapchain. This plan uses the layered window the spec already specified, with per-pixel alpha and corners drawn by us. Translucent, not blurred.

## File Structure

| File | Responsibility |
|------|----------------|
| `Cargo.toml` | Crate manifest, dependency pins, release profile |
| `build.rs` | Embeds `app.ico` and the application manifest into the executable |
| `app.manifest` | Per-monitor-v2 DPI awareness and Windows 10/11 compatibility |
| `src/protocol.rs` | Device wire format only: framing, CRC, command builders, response parsing, slot selection, layer display. No I/O. Fully tested. |
| `src/geometry.rs` | SVG path data to Direct2D-shaped segment lists. No Win32 types. Fully tested. |
| `src/compose.rs` | Alpha-coverage arithmetic: combine, box downsample, tint to premultiplied BGRA. Fully tested. |
| `src/device.rs` | The only module that talks to `hidapi`. Enumeration, connect sequence, read loop, reconnection, worker thread. |
| `src/render.rs` | Shared Direct2D, DirectWrite, and WIC factories, plus a render-to-alpha helper. |
| `src/icon.rs` | Builds the tray `HICON` from glyph, badge hole, and digit. |
| `src/theme.rs` | Light/dark theme and accent color from the registry; watches for changes. |
| `src/tray.rs` | Hidden message window, `Shell_NotifyIconW` lifecycle, tooltip. |
| `src/popup.rs` | The layered Fluent popup window and all of its drawing. |
| `src/lib.rs` | Module declarations, so the binary and any probe binaries share them. |
| `src/main.rs` | Single-instance guard, wiring, message loop. |
| `installer/layers.iss` | Inno Setup script |
| `assets/app.ico` | User-supplied application icon (already present) |
| `assets/NOTICE-fluentui.txt` | MIT license text for the vendored Fluent glyph |

Dependency direction is strictly one-way: `protocol`, `geometry`, and `compose` depend on nothing in the crate. `device` depends on `protocol`. `icon` depends on `geometry`, `compose`, and `render`. `tray`, `popup`, and `main` sit on top.

---

### Task 1: Crate skeleton and packet framing

**Files:**
- Create: `Cargo.toml`
- Create: `src/lib.rs`
- Create: `src/main.rs`
- Create: `src/protocol.rs`
- Create: `assets/NOTICE-fluentui.txt`

**Interfaces:**
- Consumes: nothing.
- Produces: `protocol::build_packet(cmd: u8, payload: &[u8]) -> [u8; 33]`, `protocol::verify_crc(packet: &[u8]) -> bool`, and the constants listed in the implementation step.

- [ ] **Step 1: Create the manifest**

`Cargo.toml`:

```toml
[package]
name = "layers"
version = "0.1.0"
edition = "2021"

[lib]
name = "layers"
path = "src/lib.rs"

[[bin]]
name = "layers"
path = "src/main.rs"

[dependencies]
crc32fast = "1.5"
hidapi = "2.6"

[dependencies.windows]
version = "0.62"
features = [
    "Win32_Foundation",
    "Win32_Graphics_Direct2D",
    "Win32_Graphics_Direct2D_Common",
    "Win32_Graphics_DirectWrite",
    "Win32_Graphics_Dwm",
    "Win32_Graphics_Dxgi_Common",
    "Win32_Graphics_Gdi",
    "Win32_Graphics_Imaging",
    "Win32_System_Com",
    "Win32_System_LibraryLoader",
    "Win32_System_Registry",
    "Win32_System_Threading",
    "Win32_UI_HiDpi",
    "Win32_UI_Shell",
    "Win32_UI_WindowsAndMessaging",
]

[build-dependencies]
winresource = "0.1"

[profile.release]
opt-level = "z"
lto = true
codegen-units = 1
strip = true
```

`build.rs` itself is created in Task 8. Cargo detects a build script by the file's presence, so declaring `winresource` now is harmless — it simply goes unused until then.

- [ ] **Step 2: Record the Fluent license**

`assets/NOTICE-fluentui.txt`:

```
The tray glyph path data in src/icon.rs is derived from
ic_fluent_layer_24_filled in microsoft/fluentui-system-icons.

MIT License

Copyright (c) 2020 Microsoft Corporation

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

- [ ] **Step 3: Write the failing test**

Create `src/lib.rs`:

```rust
pub mod protocol;
```

Create `src/main.rs`:

```rust
fn main() {}
```

Create `src/protocol.rs` containing only this test module for now:

```rust
#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn packet_has_correct_header_and_length() {
        let p = build_packet(CMD_RESUME, &[]);
        assert_eq!(p.len(), PACKET_LEN);
        assert_eq!(p[0], REPORT_ID_CONFIG);
        assert_eq!(p[1], CONFIG_VERSION);
        assert_eq!(p[2], CMD_RESUME);
        assert!(p[3..29].iter().all(|&b| b == 0));
    }

    #[test]
    fn packet_payload_is_placed_after_command_byte() {
        let p = build_packet(CMD_SET_MONITOR_ENABLED, &[1]);
        assert_eq!(p[3], 1);
        assert!(p[4..29].iter().all(|&b| b == 0));
    }

    #[test]
    fn crc_covers_bytes_one_through_twenty_eight() {
        let p = build_packet(CMD_RESUME, &[]);
        let expect = crc32fast::hash(&p[1..29]);
        assert_eq!(u32::from_le_bytes(p[29..33].try_into().unwrap()), expect);
    }

    #[test]
    fn verify_crc_accepts_our_own_packets() {
        assert!(verify_crc(&build_packet(CMD_GET_EXPRESSION, &[3, 0, 0, 0])));
    }

    #[test]
    fn verify_crc_rejects_a_corrupted_payload() {
        let mut p = build_packet(CMD_GET_EXPRESSION, &[3, 0, 0, 0]);
        p[5] ^= 0xFF;
        assert!(!verify_crc(&p));
    }

    #[test]
    fn verify_crc_rejects_a_short_buffer() {
        assert!(!verify_crc(&[0u8; 10]));
    }

    #[test]
    #[should_panic]
    fn oversized_payload_panics() {
        build_packet(CMD_RESUME, &[0u8; 27]);
    }
}
```

- [ ] **Step 4: Run the tests and confirm they fail**

Run: `cargo test`
Expected: compile errors — `build_packet` and the constants are not defined.

- [ ] **Step 5: Implement framing**

Prepend to `src/protocol.rs`, above the test module:

```rust
//! HID Remapper config wire format. Pure data, no I/O.

pub const REPORT_ID_CONFIG: u8 = 100;
pub const REPORT_ID_MONITOR: u8 = 101;
pub const CONFIG_VERSION: u8 = 18;

/// Config packets are always 33 bytes: report id, version, command,
/// 26 payload bytes, then a 4-byte CRC.
pub const PACKET_LEN: usize = 33;
pub const PAYLOAD_LEN: usize = 26;
const PAYLOAD_START: usize = 3;
const CRC_START: usize = 29;

pub const CMD_RESUME: u8 = 11;
pub const CMD_APPEND_TO_EXPRESSION: u8 = 20;
pub const CMD_GET_EXPRESSION: u8 = 21;
pub const CMD_SET_MONITOR_ENABLED: u8 = 22;

pub const CONFIG_USAGE_PAGE: u16 = 0xFF00;
pub const CONFIG_USAGE: u16 = 0x0020;

/// The firmware computes the CRC over everything between the report id and
/// the CRC field itself.
fn crc_of(packet: &[u8]) -> u32 {
    crc32fast::hash(&packet[1..CRC_START])
}

/// # Panics
/// If `payload` is longer than [`PAYLOAD_LEN`]. Payload sizes are fixed by the
/// protocol and known at every call site, so an overlong payload is a bug in
/// this crate rather than bad input.
pub fn build_packet(cmd: u8, payload: &[u8]) -> [u8; PACKET_LEN] {
    assert!(payload.len() <= PAYLOAD_LEN, "payload exceeds 26 bytes");
    let mut p = [0u8; PACKET_LEN];
    p[0] = REPORT_ID_CONFIG;
    p[1] = CONFIG_VERSION;
    p[2] = cmd;
    p[PAYLOAD_START..PAYLOAD_START + payload.len()].copy_from_slice(payload);
    p[CRC_START..].copy_from_slice(&crc_of(&p).to_le_bytes());
    p
}

pub fn verify_crc(packet: &[u8]) -> bool {
    if packet.len() < PACKET_LEN {
        return false;
    }
    let claimed = u32::from_le_bytes(packet[CRC_START..PACKET_LEN].try_into().unwrap());
    crc_of(packet) == claimed
}
```

- [ ] **Step 6: Run the tests and confirm they pass**

Run: `cargo test`
Expected: 7 passed.

---

### Task 2: Expression encoding and slot selection

**Files:**
- Modify: `src/protocol.rs`

**Interfaces:**
- Consumes: `build_packet`, `verify_crc`, and the command constants from Task 1.
- Produces:
  - `protocol::EXPR_BYTES: [u8; 7]`
  - `protocol::SENTINEL_USAGE: u32`
  - `protocol::NEXPRESSIONS: usize`
  - `protocol::get_expression(slot: u8) -> [u8; 33]`
  - `protocol::append_expression(slot: u8) -> [u8; 33]`
  - `protocol::resume() -> [u8; 33]`
  - `protocol::set_monitor_enabled(on: bool) -> [u8; 33]`
  - `protocol::SlotContents { pub nelems: u8, pub bytes: [u8; 27] }`
  - `protocol::parse_expression_response(packet: &[u8]) -> Option<SlotContents>`
  - `protocol::SlotChoice { Existing(u8), Empty(u8), NoneFree }`
  - `protocol::choose_slot(slots: &[SlotContents]) -> SlotChoice`
  - `protocol::CMD_GET_CONFIG: u8`
  - `protocol::get_config() -> [u8; 33]`
  - `protocol::parse_config_version(packet: &[u8]) -> Option<u8>`

- [ ] **Step 1: Write the failing tests**

Add to the `tests` module in `src/protocol.rs`:

```rust
    #[test]
    fn expression_bytes_are_layer_state_push_sentinel_monitor() {
        assert_eq!(EXPR_BYTES, [20u8, 1, 0x01, 0x00, 0x00, 0xFF, 44]);
    }

    #[test]
    fn sentinel_in_expression_matches_the_monitor_filter_constant() {
        let encoded = u32::from_le_bytes(EXPR_BYTES[2..6].try_into().unwrap());
        assert_eq!(encoded, SENTINEL_USAGE);
    }

    #[test]
    fn append_packet_carries_slot_then_nelems_then_element_bytes() {
        let p = append_expression(5);
        assert_eq!(p[2], CMD_APPEND_TO_EXPRESSION);
        assert_eq!(p[3], 5, "slot index");
        assert_eq!(p[4], 3, "three elements, not seven bytes");
        assert_eq!(&p[5..12], &EXPR_BYTES);
        assert!(p[12..29].iter().all(|&b| b == 0));
        assert!(verify_crc(&p));
    }

    #[test]
    fn get_expression_packet_carries_slot_and_zero_element_offset() {
        let p = get_expression(6);
        assert_eq!(p[2], CMD_GET_EXPRESSION);
        assert_eq!(u32::from_le_bytes(p[3..7].try_into().unwrap()), 6);
        assert_eq!(u32::from_le_bytes(p[7..11].try_into().unwrap()), 0);
    }

    #[test]
    fn monitor_enable_and_disable_differ_only_in_the_payload_byte() {
        assert_eq!(set_monitor_enabled(true)[3], 1);
        assert_eq!(set_monitor_enabled(false)[3], 0);
        assert_eq!(set_monitor_enabled(true)[2], CMD_SET_MONITOR_ENABLED);
    }

    #[test]
    fn resume_packet_has_an_empty_payload() {
        let p = resume();
        assert_eq!(p[2], CMD_RESUME);
        assert!(p[3..29].iter().all(|&b| b == 0));
    }

    /// Builds a well-formed GET_EXPRESSION response the way the firmware would:
    /// report id, element count, 27 element bytes, CRC.
    fn expr_response(nelems: u8, bytes: &[u8]) -> [u8; PACKET_LEN] {
        let mut p = [0u8; PACKET_LEN];
        p[0] = REPORT_ID_CONFIG;
        p[1] = nelems;
        p[2..2 + bytes.len()].copy_from_slice(bytes);
        let crc = crc32fast::hash(&p[1..29]);
        p[29..].copy_from_slice(&crc.to_le_bytes());
        p
    }

    #[test]
    fn parses_an_empty_slot_response() {
        let r = parse_expression_response(&expr_response(0, &[])).unwrap();
        assert_eq!(r.nelems, 0);
    }

    #[test]
    fn parses_our_own_expression_back_out() {
        let r = parse_expression_response(&expr_response(3, &EXPR_BYTES)).unwrap();
        assert_eq!(r.nelems, 3);
        assert_eq!(&r.bytes[..7], &EXPR_BYTES);
    }

    #[test]
    fn rejects_a_response_with_a_bad_crc() {
        let mut p = expr_response(3, &EXPR_BYTES);
        p[4] ^= 0xFF;
        assert!(parse_expression_response(&p).is_none());
    }

    fn slot(nelems: u8, bytes: &[u8]) -> SlotContents {
        let mut b = [0u8; 27];
        b[..bytes.len()].copy_from_slice(bytes);
        SlotContents { nelems, bytes: b }
    }

    #[test]
    fn reuses_our_slot_even_when_an_earlier_slot_is_empty() {
        let slots = [slot(0, &[]), slot(3, &EXPR_BYTES)];
        assert_eq!(choose_slot(&slots), SlotChoice::Existing(1));
    }

    #[test]
    fn takes_the_first_empty_slot_when_ours_is_absent() {
        let slots = [slot(5, &[20, 20, 20]), slot(0, &[]), slot(0, &[])];
        assert_eq!(choose_slot(&slots), SlotChoice::Empty(1));
    }

    #[test]
    fn reports_none_free_when_every_slot_is_occupied_by_someone_else() {
        let slots: Vec<_> = (0..8).map(|_| slot(2, &[20, 44])).collect();
        assert_eq!(choose_slot(&slots), SlotChoice::NoneFree);
    }

    #[test]
    fn does_not_mistake_a_longer_expression_that_merely_starts_like_ours() {
        let mut bytes = EXPR_BYTES.to_vec();
        bytes.push(20);
        let slots = [slot(4, &bytes), slot(0, &[])];
        assert_eq!(choose_slot(&slots), SlotChoice::Empty(1));
    }

    /// A GET_CONFIG response carries the firmware's config version in byte 1.
    fn config_response(version: u8) -> [u8; PACKET_LEN] {
        let mut p = [0u8; PACKET_LEN];
        p[0] = REPORT_ID_CONFIG;
        p[1] = version;
        let crc = crc32fast::hash(&p[1..29]);
        p[29..].copy_from_slice(&crc.to_le_bytes());
        p
    }

    #[test]
    fn reads_the_config_version_out_of_a_response() {
        assert_eq!(parse_config_version(&config_response(18)), Some(18));
    }

    #[test]
    fn reports_a_mismatched_version_rather_than_assuming_eighteen() {
        assert_eq!(parse_config_version(&config_response(19)), Some(19));
    }

    #[test]
    fn rejects_a_config_response_with_a_bad_crc() {
        let mut p = config_response(18);
        p[1] = 19;
        assert_eq!(parse_config_version(&p), None);
    }
```

- [ ] **Step 2: Run the tests and confirm they fail**

Run: `cargo test`
Expected: compile errors — `EXPR_BYTES`, `append_expression`, `choose_slot` and friends are undefined.

- [ ] **Step 3: Implement the command builders and slot logic**

Append to the non-test part of `src/protocol.rs`:

```rust
pub const NEXPRESSIONS: usize = 8;

/// Vendor-defined usage the injected expression reports the layer mask under.
/// Cannot collide with a real input usage.
pub const SENTINEL_USAGE: u32 = 0xFF00_0001;

const OP_PUSH_USAGE: u8 = 1;
const OP_LAYER_STATE: u8 = 20;
const OP_MONITOR: u8 = 44;

/// `layer_state 0xFF000001 monitor` — three elements, seven bytes.
///
/// The firmware's validator accepts it: `layer_state` and `push_usage` each
/// push one value, `monitor` requires two and consumes two, leaving the stack
/// balanced.
pub const EXPR_BYTES: [u8; 7] = [
    OP_LAYER_STATE,
    OP_PUSH_USAGE,
    SENTINEL_USAGE as u8,
    (SENTINEL_USAGE >> 8) as u8,
    (SENTINEL_USAGE >> 16) as u8,
    (SENTINEL_USAGE >> 24) as u8,
    OP_MONITOR,
];

const EXPR_NELEMS: u8 = 3;

pub fn get_expression(slot: u8) -> [u8; PACKET_LEN] {
    let mut payload = [0u8; 8];
    payload[..4].copy_from_slice(&(slot as u32).to_le_bytes());
    // Element offset. We only ever read from the start of a slot.
    payload[4..].copy_from_slice(&0u32.to_le_bytes());
    build_packet(CMD_GET_EXPRESSION, &payload)
}

pub fn append_expression(slot: u8) -> [u8; PACKET_LEN] {
    let mut payload = [0u8; 2 + EXPR_BYTES.len()];
    payload[0] = slot;
    payload[1] = EXPR_NELEMS;
    payload[2..].copy_from_slice(&EXPR_BYTES);
    build_packet(CMD_APPEND_TO_EXPRESSION, &payload)
}

/// Required after appending. `eval_expr` refuses to run an expression whose
/// `expression_valid` flag is unset, and only `RESUME` reaches the code that
/// sets it. Without this the expression is silently inert.
pub fn resume() -> [u8; PACKET_LEN] {
    build_packet(CMD_RESUME, &[])
}

pub fn set_monitor_enabled(on: bool) -> [u8; PACKET_LEN] {
    build_packet(CMD_SET_MONITOR_ENABLED, &[on as u8])
}

/// One expression slot as the firmware reports it: an element count and the
/// encoded element byte stream.
#[derive(Clone, Copy)]
pub struct SlotContents {
    pub nelems: u8,
    pub bytes: [u8; 27],
}

impl SlotContents {
    fn is_ours(&self) -> bool {
        self.nelems == EXPR_NELEMS && self.bytes[..EXPR_BYTES.len()] == EXPR_BYTES
    }
}

/// Response layout is report id, element count, 27 bytes of elements, CRC.
pub fn parse_expression_response(packet: &[u8]) -> Option<SlotContents> {
    if !verify_crc(packet) {
        return None;
    }
    let mut bytes = [0u8; 27];
    bytes.copy_from_slice(&packet[2..29]);
    Some(SlotContents { nelems: packet[1], bytes })
}

#[derive(Debug, PartialEq, Eq, Clone, Copy)]
pub enum SlotChoice {
    /// Our expression is already here from a previous run. Reuse it.
    Existing(u8),
    /// Free slot to append into.
    Empty(u8),
    /// All eight slots belong to the user. The layer cannot be read.
    NoneFree,
}

/// Reusing our own slot matters: without it every app restart would consume
/// another slot and eight restarts would exhaust the device.
pub fn choose_slot(slots: &[SlotContents]) -> SlotChoice {
    if let Some(i) = slots.iter().position(SlotContents::is_ours) {
        return SlotChoice::Existing(i as u8);
    }
    match slots.iter().position(|s| s.nelems == 0) {
        Some(i) => SlotChoice::Empty(i as u8),
        None => SlotChoice::NoneFree,
    }
}

pub const CMD_GET_CONFIG: u8 = 3;

pub fn get_config() -> [u8; PACKET_LEN] {
    build_packet(CMD_GET_CONFIG, &[])
}

/// The firmware's config version, from byte 1 of a GET_CONFIG response.
///
/// Every opcode number, report id and command byte this app relies on is tied
/// to a specific protocol version. A mismatch is surfaced rather than guessed
/// at, because guessing wrong means writing an expression the firmware would
/// interpret as something else entirely.
pub fn parse_config_version(packet: &[u8]) -> Option<u8> {
    if !verify_crc(packet) {
        return None;
    }
    Some(packet[1])
}
```

- [ ] **Step 4: Run the tests and confirm they pass**

Run: `cargo test`
Expected: 23 passed.

---

### Task 3: Monitor report parsing and layer display

**Files:**
- Modify: `src/protocol.rs`

**Interfaces:**
- Consumes: `SENTINEL_USAGE`, `REPORT_ID_MONITOR` from Tasks 1 and 2.
- Produces:
  - `protocol::MONITOR_REPORT_LEN: usize` (64)
  - `protocol::parse_monitor_report(buf: &[u8]) -> Option<Layers>`
  - `protocol::Layers(pub u8)` with `active() -> Vec<u8>`, `badge() -> Option<u8>`, `label() -> String`

- [ ] **Step 1: Write the failing tests**

Add to the `tests` module:

```rust
    /// Builds a monitor input report the way the firmware would: report id
    /// then seven packed 9-byte items of usage, value, hub port.
    fn monitor_report(items: &[(u32, i32)]) -> Vec<u8> {
        let mut b = vec![0u8; MONITOR_REPORT_LEN];
        b[0] = REPORT_ID_MONITOR;
        for (i, (usage, value)) in items.iter().enumerate() {
            let o = 1 + i * 9;
            b[o..o + 4].copy_from_slice(&usage.to_le_bytes());
            b[o + 4..o + 8].copy_from_slice(&value.to_le_bytes());
        }
        b
    }

    #[test]
    fn extracts_the_layer_mask_from_the_sentinel_item() {
        let r = monitor_report(&[(SENTINEL_USAGE, 0b0000_0100)]);
        assert_eq!(parse_monitor_report(&r).unwrap().0, 0b100);
    }

    #[test]
    fn finds_the_sentinel_in_the_last_slot() {
        let mut items: Vec<(u32, i32)> = (0..6).map(|i| (0x0009_0001 + i, 1)).collect();
        items.push((SENTINEL_USAGE, 2));
        assert_eq!(parse_monitor_report(&monitor_report(&items)).unwrap().0, 2);
    }

    #[test]
    fn ignores_reports_carrying_only_ordinary_input_usages() {
        let r = monitor_report(&[(0x0009_0001, 1), (0x0001_0030, -5)]);
        assert!(parse_monitor_report(&r).is_none());
    }

    #[test]
    fn ignores_padding_items_whose_usage_is_zero() {
        assert!(parse_monitor_report(&monitor_report(&[])).is_none());
    }

    #[test]
    fn ignores_reports_with_the_wrong_report_id() {
        let mut r = monitor_report(&[(SENTINEL_USAGE, 3)]);
        r[0] = REPORT_ID_CONFIG;
        assert!(parse_monitor_report(&r).is_none());
    }

    #[test]
    fn ignores_a_truncated_report() {
        assert!(parse_monitor_report(&[REPORT_ID_MONITOR, 0, 0]).is_none());
    }

    #[test]
    fn keeps_only_the_low_eight_bits_of_a_negative_value() {
        // layer_state_mask is a u8 on the device but travels as an i32.
        let r = monitor_report(&[(SENTINEL_USAGE, -1)]);
        assert_eq!(parse_monitor_report(&r).unwrap().0, 0xFF);
    }

    #[test]
    fn layer_zero_shows_no_badge() {
        assert_eq!(Layers(0b1).badge(), None);
        assert_eq!(Layers(0b1).active(), vec![0]);
        assert_eq!(Layers(0b1).label(), "Layer 0");
    }

    #[test]
    fn an_empty_mask_is_treated_as_layer_zero() {
        // The firmware floors an empty mask to 1, so this should not occur,
        // but a garbled report must not produce an empty display.
        assert_eq!(Layers(0).active(), vec![0]);
        assert_eq!(Layers(0).badge(), None);
    }

    #[test]
    fn a_single_active_layer_badges_that_layer() {
        assert_eq!(Layers(0b1000).badge(), Some(3));
        assert_eq!(Layers(0b1000).label(), "Layer 3");
    }

    #[test]
    fn several_active_layers_badge_the_highest_and_list_them_all() {
        let l = Layers(0b1010);
        assert_eq!(l.active(), vec![1, 3]);
        assert_eq!(l.badge(), Some(3));
        assert_eq!(l.label(), "Layers 1, 3");
    }

    #[test]
    fn layer_zero_alongside_another_layer_still_badges_the_higher_one() {
        let l = Layers(0b0101);
        assert_eq!(l.active(), vec![0, 2]);
        assert_eq!(l.badge(), Some(2));
        assert_eq!(l.label(), "Layers 0, 2");
    }

    #[test]
    fn parses_all_eight_bits_even_though_this_firmware_uses_four() {
        assert_eq!(Layers(0b1000_0000).badge(), Some(7));
    }
```

- [ ] **Step 2: Run the tests and confirm they fail**

Run: `cargo test`
Expected: compile errors — `MONITOR_REPORT_LEN`, `parse_monitor_report`, `Layers` are undefined.

- [ ] **Step 3: Implement parsing and display**

Append to the non-test part of `src/protocol.rs`:

```rust
const MONITOR_ITEMS: usize = 7;
const MONITOR_ITEM_LEN: usize = 9;
/// Report id plus seven packed 9-byte items.
pub const MONITOR_REPORT_LEN: usize = 1 + MONITOR_ITEMS * MONITOR_ITEM_LEN;

/// Returns the layer mask if this report carries our sentinel.
///
/// The firmware emits a monitor item only when the value changes, so every
/// sentinel item corresponds to an actual layer switch.
pub fn parse_monitor_report(buf: &[u8]) -> Option<Layers> {
    if buf.len() < MONITOR_REPORT_LEN || buf[0] != REPORT_ID_MONITOR {
        return None;
    }
    for i in 0..MONITOR_ITEMS {
        let o = 1 + i * MONITOR_ITEM_LEN;
        let usage = u32::from_le_bytes(buf[o..o + 4].try_into().unwrap());
        if usage != SENTINEL_USAGE {
            continue;
        }
        let value = i32::from_le_bytes(buf[o + 4..o + 8].try_into().unwrap());
        // layer_state bypasses the x1000 fixed-point convention, so the value
        // is the raw mask. It is a u8 on the device but travels as an i32.
        return Some(Layers((value as u32 & 0xFF) as u8));
    }
    None
}

/// Bit mask of currently active layers.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct Layers(pub u8);

impl Layers {
    pub fn active(&self) -> Vec<u8> {
        if self.0 == 0 {
            return vec![0];
        }
        (0..8).filter(|i| self.0 & (1 << i) != 0).collect()
    }

    /// The digit drawn on the tray icon, or `None` for layer 0, which renders
    /// as the bare glyph.
    pub fn badge(&self) -> Option<u8> {
        match *self.active().last().unwrap() {
            0 => None,
            n => Some(n),
        }
    }

    pub fn label(&self) -> String {
        let active = self.active();
        let list: Vec<String> = active.iter().map(u8::to_string).collect();
        if active.len() == 1 {
            format!("Layer {}", list[0])
        } else {
            format!("Layers {}", list.join(", "))
        }
    }
}
```

- [ ] **Step 4: Run the tests and confirm they pass**

Run: `cargo test`
Expected: 36 passed.

---

### Task 4: SVG path parsing

**Files:**
- Create: `src/geometry.rs`
- Create: `src/icon.rs` (constants only; Task 7 builds the rest)
- Modify: `src/lib.rs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `geometry::Point { pub x: f32, pub y: f32 }`
  - `geometry::Segment { Line(Point), Cubic(Point, Point, Point) }`
  - `geometry::Figure { pub start: Point, pub segments: Vec<Segment> }`
  - `geometry::parse_path(d: &str) -> Result<Vec<Figure>, String>`
  - `icon::GLYPH_PATH: &str`, `icon::GLYPH_VIEWBOX: f32`

The Fluent glyph uses only absolute `M`, `L`, `C`, and `Z`. Anything else is an error rather than a silent misdraw, because a silently wrong icon is harder to notice than a loud failure.

- [ ] **Step 1: Add the glyph constants**

Create `src/icon.rs` with exactly this, and nothing else for now:

```rust
//! Tray icon rendering.

/// `ic_fluent_layer_24_filled` from microsoft/fluentui-system-icons, MIT.
/// See assets/NOTICE-fluentui.txt.
pub const GLYPH_PATH: &str = "M13.3867 3.42476L19.7519 7.66821C20.2115 7.97456 20.3356 8.59543 20.0293 9.05496C19.956 9.16481 19.8618 9.25907 19.7519 9.33231L13.3867 13.5758C12.547 14.1356 11.453 14.1356 10.6132 13.5758L4.24807 9.33231C3.78854 9.02595 3.66437 8.40509 3.97072 7.94556C4.04396 7.8357 4.13822 7.74144 4.24807 7.66821L10.6132 3.42476C11.453 2.86492 12.547 2.86492 13.3867 3.42476ZM20.0256 12.1922C19.8772 12.4296 19.6806 12.6332 19.4486 12.7899L13.3987 16.8736C12.5535 17.4441 11.4465 17.4441 10.6013 16.8736L4.55142 12.7899C3.79043 12.2762 3.49533 11.3306 3.77229 10.5003L10.6132 15.0598C11.4005 15.5847 12.4112 15.6175 13.2264 15.1582L13.3867 15.0598L20.2271 10.4998C20.4088 11.0459 20.3545 11.666 20.0256 12.1922ZM20.0256 15.4422C19.8772 15.6796 19.6806 15.8832 19.4486 16.0399L13.3987 20.1236C12.5535 20.6941 11.4465 20.6941 10.6013 20.1236L4.55142 16.0399C3.79043 15.5262 3.49533 14.5806 3.77229 13.7503L10.6132 18.3098C11.4005 18.8347 12.4112 18.8675 13.2264 18.4082L13.3867 18.3098L20.2271 13.7498C20.4088 14.2959 20.3545 14.916 20.0256 15.4422Z";

/// The glyph is authored on a 24x24 grid.
pub const GLYPH_VIEWBOX: f32 = 24.0;
```

Update `src/lib.rs`:

```rust
pub mod geometry;
pub mod icon;
pub mod protocol;
```

- [ ] **Step 2: Write the failing tests**

Create `src/geometry.rs` with only this test module:

```rust
#[cfg(test)]
mod tests {
    use super::*;

    fn p(x: f32, y: f32) -> Point {
        Point { x, y }
    }

    #[test]
    fn parses_a_single_line_figure() {
        let f = parse_path("M1 2L3 4Z").unwrap();
        assert_eq!(f.len(), 1);
        assert_eq!(f[0].start, p(1.0, 2.0));
        assert_eq!(f[0].segments, vec![Segment::Line(p(3.0, 4.0))]);
    }

    #[test]
    fn parses_a_cubic_segment_as_three_points() {
        let f = parse_path("M0 0C1 2 3 4 5 6Z").unwrap();
        assert_eq!(
            f[0].segments,
            vec![Segment::Cubic(p(1.0, 2.0), p(3.0, 4.0), p(5.0, 6.0))]
        );
    }

    #[test]
    fn splits_multiple_subpaths_on_each_moveto() {
        let f = parse_path("M0 0L1 1ZM5 5L6 6Z").unwrap();
        assert_eq!(f.len(), 2);
        assert_eq!(f[1].start, p(5.0, 5.0));
    }

    #[test]
    fn accepts_negative_and_fractional_coordinates() {
        let f = parse_path("M-1.5 2.25L0.5 -3Z").unwrap();
        assert_eq!(f[0].start, p(-1.5, 2.25));
        assert_eq!(f[0].segments, vec![Segment::Line(p(0.5, -3.0))]);
    }

    #[test]
    fn treats_commas_and_extra_whitespace_as_separators() {
        let a = parse_path("M0,0 L1,1 Z").unwrap();
        let b = parse_path("M0 0L1 1Z").unwrap();
        assert_eq!(a[0].segments, b[0].segments);
    }

    #[test]
    fn repeats_the_previous_command_for_bare_coordinate_runs() {
        // "L1 1 2 2" means two lines, per the SVG grammar.
        let f = parse_path("M0 0L1 1 2 2Z").unwrap();
        assert_eq!(f[0].segments.len(), 2);
        assert_eq!(f[0].segments[1], Segment::Line(p(2.0, 2.0)));
    }

    #[test]
    fn rejects_relative_commands_rather_than_misdrawing_them() {
        assert!(parse_path("M0 0l1 1Z").is_err());
    }

    #[test]
    fn rejects_commands_outside_the_supported_subset() {
        assert!(parse_path("M0 0A1 1 0 0 1 2 2Z").is_err());
    }

    #[test]
    fn rejects_a_truncated_coordinate_run() {
        assert!(parse_path("M0 0L1Z").is_err());
    }

    #[test]
    fn rejects_a_path_that_does_not_begin_with_a_command() {
        assert!(parse_path("1 1Z").is_err());
    }

    #[test]
    fn the_vendored_fluent_glyph_parses_into_three_figures() {
        let figures = parse_path(crate::icon::GLYPH_PATH).unwrap();
        assert_eq!(figures.len(), 3);
        assert!(figures.iter().all(|f| !f.segments.is_empty()));
    }

    #[test]
    fn the_vendored_fluent_glyph_stays_inside_its_view_box() {
        for f in parse_path(crate::icon::GLYPH_PATH).unwrap() {
            let mut pts = vec![f.start];
            for s in &f.segments {
                match s {
                    Segment::Line(a) => pts.push(*a),
                    Segment::Cubic(a, b, c) => pts.extend([*a, *b, *c]),
                }
            }
            for pt in pts {
                assert!(
                    (0.0..=crate::icon::GLYPH_VIEWBOX).contains(&pt.x)
                        && (0.0..=crate::icon::GLYPH_VIEWBOX).contains(&pt.y),
                    "point {pt:?} escapes the view box"
                );
            }
        }
    }
}
```

Add `pub mod geometry;` to `src/lib.rs` if not already present.

- [ ] **Step 3: Run the tests and confirm they fail**

Run: `cargo test`
Expected: compile errors — `Point`, `Segment`, `Figure`, `parse_path` are undefined.

- [ ] **Step 4: Implement the parser**

Prepend to `src/geometry.rs`:

```rust
//! Minimal SVG path parser.
//!
//! Handles exactly the subset the vendored Fluent glyph uses: absolute
//! `M`, `L`, `C`, `Z`. Anything else is rejected. A loud error beats a
//! silently misdrawn icon.

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Point {
    pub x: f32,
    pub y: f32,
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub enum Segment {
    Line(Point),
    /// Two control points then the end point.
    Cubic(Point, Point, Point),
}

#[derive(Debug, Clone, PartialEq)]
pub struct Figure {
    pub start: Point,
    pub segments: Vec<Segment>,
}

/// Splits path data into (command letter, number run) pairs. Numbers may butt
/// directly against letters and against each other via a leading sign.
fn tokenize(d: &str) -> Result<Vec<(Option<char>, Vec<f32>)>, String> {
    let mut tokens: Vec<(Option<char>, Vec<f32>)> = Vec::new();
    let mut pending_cmd: Option<char> = None;
    let mut pending: Vec<f32> = Vec::new();
    let mut chars = d.chars().peekable();

    while let Some(&c) = chars.peek() {
        if c.is_ascii_alphabetic() {
            if pending_cmd.is_some() || !pending.is_empty() {
                tokens.push((pending_cmd, std::mem::take(&mut pending)));
            }
            pending_cmd = Some(c);
            chars.next();
        } else if c.is_whitespace() || c == ',' {
            chars.next();
        } else {
            let mut s = String::new();
            if c == '-' || c == '+' {
                s.push(c);
                chars.next();
            }
            while let Some(&c2) = chars.peek() {
                if c2.is_ascii_digit() || c2 == '.' {
                    s.push(c2);
                    chars.next();
                } else {
                    break;
                }
            }
            if s.is_empty() || s == "-" || s == "+" || s == "." {
                return Err(format!("unexpected character '{c}'"));
            }
            pending.push(s.parse::<f32>().map_err(|e| e.to_string())?);
        }
    }
    if pending_cmd.is_some() || !pending.is_empty() {
        tokens.push((pending_cmd, pending));
    }
    Ok(tokens)
}

pub fn parse_path(d: &str) -> Result<Vec<Figure>, String> {
    let mut figures: Vec<Figure> = Vec::new();
    let mut current: Option<Figure> = None;
    let mut command: Option<char> = None;

    for (cmd, nums) in tokenize(d)? {
        if let Some(c) = cmd {
            command = Some(c);
        }
        let c = command.ok_or_else(|| "path does not begin with a command".to_string())?;
        match c {
            'M' => {
                if nums.len() < 2 || nums.len() % 2 != 0 {
                    return Err("M needs pairs of coordinates".into());
                }
                if let Some(f) = current.take() {
                    figures.push(f);
                }
                let mut fig = Figure {
                    start: Point { x: nums[0], y: nums[1] },
                    segments: Vec::new(),
                };
                // Extra pairs after a moveto are implicit linetos.
                for pair in nums[2..].chunks(2) {
                    fig.segments
                        .push(Segment::Line(Point { x: pair[0], y: pair[1] }));
                }
                current = Some(fig);
                // A bare coordinate run after M continues as L.
                command = Some('L');
            }
            'L' => {
                let f = current.as_mut().ok_or("L before M")?;
                if nums.is_empty() || nums.len() % 2 != 0 {
                    return Err("L needs pairs of coordinates".into());
                }
                for pair in nums.chunks(2) {
                    f.segments.push(Segment::Line(Point { x: pair[0], y: pair[1] }));
                }
            }
            'C' => {
                let f = current.as_mut().ok_or("C before M")?;
                if nums.is_empty() || nums.len() % 6 != 0 {
                    return Err("C needs groups of six coordinates".into());
                }
                for g in nums.chunks(6) {
                    f.segments.push(Segment::Cubic(
                        Point { x: g[0], y: g[1] },
                        Point { x: g[2], y: g[3] },
                        Point { x: g[4], y: g[5] },
                    ));
                }
            }
            'Z' => {
                if !nums.is_empty() {
                    return Err("Z takes no coordinates".into());
                }
                if let Some(f) = current.take() {
                    figures.push(f);
                }
            }
            other => return Err(format!("unsupported path command '{other}'")),
        }
    }

    if let Some(f) = current {
        figures.push(f);
    }
    if figures.is_empty() {
        return Err("path produced no figures".into());
    }
    Ok(figures)
}
```

Lowercase `z` is deliberately not accepted. The vendored glyph uses uppercase, and accepting one relative command while rejecting the rest would be inconsistent.

- [ ] **Step 5: Run the tests and confirm they pass**

Run: `cargo test`
Expected: 48 passed.

---

### Task 5: Alpha compositing

**Files:**
- Create: `src/compose.rs`
- Modify: `src/lib.rs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `compose::Alpha { pub w: usize, pub h: usize, pub px: Vec<u8> }` with `Alpha::new(w, h)`
  - `compose::combine(glyph: &Alpha, hole: &Alpha, digit: &Alpha) -> Alpha`
  - `compose::downsample(src: &Alpha, factor: usize) -> Alpha`
  - `compose::to_premultiplied_bgra(a: &Alpha, rgb: (u8, u8, u8)) -> Vec<u8>`

- [ ] **Step 1: Write the failing tests**

Create `src/compose.rs` with only this test module, and add `pub mod compose;` to `src/lib.rs`:

```rust
#[cfg(test)]
mod tests {
    use super::*;

    fn filled(w: usize, h: usize, v: u8) -> Alpha {
        Alpha { w, h, px: vec![v; w * h] }
    }

    #[test]
    fn with_no_hole_and_no_digit_the_glyph_passes_through() {
        let out = combine(&filled(2, 2, 200), &Alpha::new(2, 2), &Alpha::new(2, 2));
        assert_eq!(out.px, vec![200; 4]);
    }

    #[test]
    fn a_fully_opaque_hole_erases_the_glyph_beneath_it() {
        let out = combine(&filled(2, 2, 255), &filled(2, 2, 255), &Alpha::new(2, 2));
        assert_eq!(out.px, vec![0; 4]);
    }

    #[test]
    fn a_partial_hole_attenuates_the_glyph_proportionally() {
        let out = combine(&filled(1, 1, 200), &filled(1, 1, 128), &Alpha::new(1, 1));
        // 200 * (1 - 128/255) = 99.6
        assert!((out.px[0] as i32 - 100).abs() <= 1, "got {}", out.px[0]);
    }

    #[test]
    fn the_digit_is_added_inside_the_hole_it_sits_in() {
        let out = combine(&filled(1, 1, 255), &filled(1, 1, 255), &filled(1, 1, 255));
        assert_eq!(out.px[0], 255);
    }

    #[test]
    fn the_sum_saturates_rather_than_wrapping() {
        let out = combine(&filled(1, 1, 255), &Alpha::new(1, 1), &filled(1, 1, 255));
        assert_eq!(out.px[0], 255);
    }

    #[test]
    fn downsampling_averages_each_source_block() {
        let src = Alpha { w: 2, h: 2, px: vec![0, 100, 200, 255] };
        let out = downsample(&src, 2);
        assert_eq!(out.w, 1);
        assert_eq!(out.h, 1);
        assert_eq!(out.px[0], ((0 + 100 + 200 + 255) / 4) as u8);
    }

    #[test]
    fn downsampling_preserves_a_uniform_field_exactly() {
        let out = downsample(&filled(8, 8, 173), 4);
        assert_eq!(out.w, 2);
        assert_eq!(out.px, vec![173; 4]);
    }

    #[test]
    fn a_factor_of_one_is_a_passthrough() {
        let src = filled(3, 3, 42);
        assert_eq!(downsample(&src, 1).px, src.px);
    }

    #[test]
    fn bgra_output_is_premultiplied_and_channel_ordered() {
        let out = to_premultiplied_bgra(&filled(1, 1, 255), (0x12, 0x34, 0x56));
        assert_eq!(out, vec![0x56, 0x34, 0x12, 0xFF]);
    }

    #[test]
    fn half_alpha_halves_every_color_channel() {
        let out = to_premultiplied_bgra(&filled(1, 1, 128), (255, 255, 255));
        assert_eq!(out[3], 128);
        assert_eq!(out[0], 128);
        assert_eq!(out[1], 128);
        assert_eq!(out[2], 128);
    }

    #[test]
    fn fully_transparent_pixels_carry_no_color() {
        let out = to_premultiplied_bgra(&Alpha::new(1, 1), (255, 255, 255));
        assert_eq!(out, vec![0, 0, 0, 0]);
    }

    #[test]
    fn output_length_is_four_bytes_per_pixel() {
        assert_eq!(to_premultiplied_bgra(&Alpha::new(4, 3), (1, 2, 3)).len(), 48);
    }
}
```

- [ ] **Step 2: Run the tests and confirm they fail**

Run: `cargo test`
Expected: compile errors — `Alpha`, `combine`, `downsample`, `to_premultiplied_bgra` are undefined.

- [ ] **Step 3: Implement compositing**

Prepend to `src/compose.rs`:

```rust
//! Alpha-coverage compositing for the tray icon.
//!
//! The icon is monochrome, so everything is done as single-channel coverage
//! and tinted at the very end. This replaces the Direct2D composite modes the
//! design doc originally called for, which would have required a full
//! ID2D1DeviceContext and therefore a D3D11 device.

/// A single-channel coverage buffer, one byte per pixel, row major.
#[derive(Debug, Clone, PartialEq)]
pub struct Alpha {
    pub w: usize,
    pub h: usize,
    pub px: Vec<u8>,
}

impl Alpha {
    pub fn new(w: usize, h: usize) -> Self {
        Alpha { w, h, px: vec![0; w * h] }
    }
}

/// `out = glyph * (1 - hole) + digit`, saturating.
///
/// The hole knocks a transparent well into the glyph so the digit stays
/// readable over it, and stays transparent over any taskbar color.
///
/// # Panics
/// If the three buffers differ in size. They are always produced by the same
/// render pass, so a mismatch is a bug rather than bad input.
pub fn combine(glyph: &Alpha, hole: &Alpha, digit: &Alpha) -> Alpha {
    assert!(
        glyph.w == hole.w && glyph.w == digit.w && glyph.h == hole.h && glyph.h == digit.h,
        "layers must be the same size"
    );
    let px = (0..glyph.px.len())
        .map(|i| {
            let kept = glyph.px[i] as u32 * (255 - hole.px[i] as u32) / 255;
            (kept + digit.px[i] as u32).min(255) as u8
        })
        .collect();
    Alpha { w: glyph.w, h: glyph.h, px }
}

/// Box-filter downsample by an integer factor. Rendering at 4x and reducing
/// gives the digit clean edges at 16 pixels.
///
/// # Panics
/// If `factor` is zero or does not divide both dimensions.
pub fn downsample(src: &Alpha, factor: usize) -> Alpha {
    assert!(factor > 0, "factor must be positive");
    assert!(
        src.w % factor == 0 && src.h % factor == 0,
        "factor must divide both dimensions"
    );
    let (w, h) = (src.w / factor, src.h / factor);
    let mut px = vec![0u8; w * h];
    let n = (factor * factor) as u32;
    for y in 0..h {
        for x in 0..w {
            let mut sum = 0u32;
            for dy in 0..factor {
                for dx in 0..factor {
                    sum += src.px[(y * factor + dy) * src.w + x * factor + dx] as u32;
                }
            }
            px[y * w + x] = (sum / n) as u8;
        }
    }
    Alpha { w, h, px }
}

/// Expands coverage into the premultiplied BGRA that CreateDIBSection and
/// CreateIconIndirect expect.
pub fn to_premultiplied_bgra(a: &Alpha, rgb: (u8, u8, u8)) -> Vec<u8> {
    let (r, g, b) = rgb;
    let mut out = Vec::with_capacity(a.px.len() * 4);
    for &cov in &a.px {
        let m = |c: u8| (c as u32 * cov as u32 / 255) as u8;
        out.extend_from_slice(&[m(b), m(g), m(r), cov]);
    }
    out
}
```

- [ ] **Step 4: Run the tests and confirm they pass**

Run: `cargo test`
Expected: 60 passed.

---

### Task 6: Device thread

**Files:**
- Create: `src/device.rs`
- Create: `src/bin/probe.rs` (temporary, deleted in Step 5)
- Modify: `src/lib.rs`

**Interfaces:**
- Consumes: everything public from `protocol`.
- Produces:
  - `device::Status { Disconnected, NoSlot, Connected }`
  - `device::State { pub status: Status, pub layers: protocol::Layers }`
  - `device::spawn(on_change: impl Fn(State) + Send + 'static) -> device::Handle`
  - `device::Handle::shutdown(self)`

The callback runs on the device thread. Task 8 makes it post a window message so the UI thread does the actual work.

- [ ] **Step 1: Write the module**

Create `src/device.rs`, and add `pub mod device;` to `src/lib.rs`:

```rust
//! The only module that talks to the device.
//!
//! Owns a background thread that holds the hidapi handle, runs the connect
//! sequence, and blocks on reads. Never touches UI state directly.

use crate::protocol::{self, Layers};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::thread::{self, JoinHandle};
use std::time::Duration;

/// Discriminants are packed into a window message wParam in main.rs. Do not
/// reorder without updating the unpacking there.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Status {
    Disconnected = 0,
    /// Connected, but all eight expression slots belong to the user, so the
    /// layer cannot be read.
    NoSlot = 1,
    Connected = 2,
    /// Connected, but the firmware reports a config version this app was not
    /// built against. Every opcode and command byte is version-specific, so
    /// nothing is written to the device in this state.
    VersionMismatch = 3,
}

#[derive(Debug, Clone, Copy)]
pub struct State {
    pub status: Status,
    pub layers: Layers,
}

pub struct Handle {
    stop: Arc<AtomicBool>,
    thread: Option<JoinHandle<()>>,
}

impl Handle {
    /// Signals the thread to disable Monitor and exit, then waits for it.
    pub fn shutdown(mut self) {
        self.stop.store(true, Ordering::Relaxed);
        if let Some(t) = self.thread.take() {
            let _ = t.join();
        }
    }
}

const RECONNECT_DELAY: Duration = Duration::from_secs(2);
/// Long enough that an idle device does not spin the CPU, short enough that
/// quit stays responsive.
const READ_TIMEOUT_MS: i32 = 500;

pub fn spawn(on_change: impl Fn(State) + Send + 'static) -> Handle {
    let stop = Arc::new(AtomicBool::new(false));
    let thread_stop = stop.clone();
    let thread = thread::spawn(move || run(thread_stop, on_change));
    Handle { stop, thread: Some(thread) }
}

fn run(stop: Arc<AtomicBool>, on_change: impl Fn(State)) {
    let mut last = State { status: Status::Disconnected, layers: Layers(1) };
    on_change(last);

    while !stop.load(Ordering::Relaxed) {
        if session(&stop, &mut last, &on_change).is_err() && last.status != Status::Disconnected {
            last = State { status: Status::Disconnected, layers: Layers(1) };
            on_change(last);
        }
        if !stop.load(Ordering::Relaxed) {
            thread::sleep(RECONNECT_DELAY);
        }
    }
}

/// One connect-and-read cycle. Returns Err on any device failure so the caller
/// can back off and retry. A fresh HidApi per attempt keeps enumeration
/// results current and avoids shared mutable state.
fn session(
    stop: &AtomicBool,
    last: &mut State,
    on_change: &impl Fn(State),
) -> Result<(), ()> {
    let api = hidapi::HidApi::new().map_err(|_| ())?;

    let info = api
        .device_list()
        .find(|d| {
            d.usage_page() == protocol::CONFIG_USAGE_PAGE && d.usage() == protocol::CONFIG_USAGE
        })
        .ok_or(())?;
    let dev = info.open_device(&api).map_err(|_| ())?;

    // Version gate. Nothing is written to the device until the firmware
    // confirms it speaks the protocol version these opcodes belong to.
    dev.send_feature_report(&protocol::get_config())
        .map_err(|_| ())?;
    let version = read_response(&dev, protocol::parse_config_version)?;
    if version != protocol::CONFIG_VERSION {
        *last = State { status: Status::VersionMismatch, layers: Layers(1) };
        on_change(*last);
        // Hold the handle open so the loop does not spin re-enumerating.
        while !stop.load(Ordering::Relaxed) {
            thread::sleep(RECONNECT_DELAY);
        }
        return Ok(());
    }

    let status = match claim_slot(&dev)? {
        protocol::SlotChoice::NoneFree => Status::NoSlot,
        protocol::SlotChoice::Existing(_) => Status::Connected,
        protocol::SlotChoice::Empty(slot) => {
            dev.send_feature_report(&protocol::append_expression(slot))
                .map_err(|_| ())?;
            // Required. Without RESUME the firmware never marks the expression
            // valid and eval_expr silently returns zero forever.
            dev.send_feature_report(&protocol::resume()).map_err(|_| ())?;
            Status::Connected
        }
    };

    dev.send_feature_report(&protocol::set_monitor_enabled(true))
        .map_err(|_| ())?;

    *last = State { status, layers: Layers(1) };
    on_change(*last);

    let mut buf = [0u8; protocol::MONITOR_REPORT_LEN];
    while !stop.load(Ordering::Relaxed) {
        match dev.read_timeout(&mut buf, READ_TIMEOUT_MS) {
            Ok(0) => continue, // timeout, the device is simply idle
            Ok(_) => {
                if let Some(layers) = protocol::parse_monitor_report(&buf) {
                    if layers != last.layers {
                        last.layers = layers;
                        on_change(*last);
                    }
                }
            }
            Err(_) => return Err(()),
        }
    }

    // Clean shutdown. The expression itself cannot be removed without
    // CLEAR_EXPRESSIONS, which would destroy the user's own expressions, so it
    // is left in RAM where it is inert once Monitor is off.
    let _ = dev.send_feature_report(&protocol::set_monitor_enabled(false));
    Ok(())
}

/// Reads all eight expression slots and decides which to use.
fn claim_slot(dev: &hidapi::HidDevice) -> Result<protocol::SlotChoice, ()> {
    let mut slots = Vec::with_capacity(protocol::NEXPRESSIONS);
    for slot in 0..protocol::NEXPRESSIONS as u8 {
        dev.send_feature_report(&protocol::get_expression(slot))
            .map_err(|_| ())?;
        slots.push(read_response(dev, protocol::parse_expression_response)?);
    }
    Ok(protocol::choose_slot(&slots))
}

/// Reads a config response and runs `parse` over it.
///
/// The firmware answers asynchronously, so a read issued too soon comes back
/// short. Retry with a doubling delay, as the official config tool does.
fn read_response<T>(
    dev: &hidapi::HidDevice,
    parse: impl Fn(&[u8]) -> Option<T>,
) -> Result<T, ()> {
    let mut delay = Duration::from_millis(2);
    for _ in 0..10 {
        let mut buf = [0u8; protocol::PACKET_LEN];
        buf[0] = protocol::REPORT_ID_CONFIG;
        if let Ok(n) = dev.get_feature_report(&mut buf) {
            if n >= protocol::PACKET_LEN {
                if let Some(v) = parse(&buf) {
                    return Ok(v);
                }
            }
        }
        thread::sleep(delay);
        delay *= 2;
    }
    Err(())
}
```

- [ ] **Step 2: Check that it compiles**

Run: `cargo build`
Expected: success.

If `hidapi::DeviceInfo::usage_page` and `usage` are unavailable on this platform build, they are behind the default `windows-native` backend — confirm the crate is not being built with `default-features = false`.

- [ ] **Step 3: Verify against the real device**

The user has a flashed HID Remapper connected. Create `src/bin/probe.rs`:

```rust
fn main() {
    let (tx, rx) = std::sync::mpsc::channel();
    let handle = layers::device::spawn(move |s| {
        let _ = tx.send(s);
    });
    for s in rx.iter().take(20) {
        println!("{:?} {:?} -> {}", s.status, s.layers, s.layers.label());
    }
    handle.shutdown();
}
```

Run: `cargo run --bin probe`

Expected: `Connected Layers(1) -> Layer 0` on start. Hold or toggle a layer key on the peripheral and confirm a new line appears with the correct layer, and another on release. Unplug the device and confirm `Disconnected` appears within about 2 seconds; replug and confirm it reconnects.

If nothing prints on a layer switch, the most likely cause is a missing or failed `RESUME`. Cross-check by opening the web config tool's Monitor tab, which should also show usage `0xff000001` changing.

If the first line is `VersionMismatch`, the connected firmware reports a config version other than 18. Confirm with `python config-tool/get_config.py`, which prints the version. Do not work around it by removing the check — every opcode number in `protocol.rs` is tied to that version, and writing them to a firmware that numbers opcodes differently would inject a different expression entirely.

- [ ] **Step 4: Confirm the device was not modified persistently**

Unplug the device, replug it, and read its config with the web config tool at https://www.jfedor.org/hid-remapper/ or with `python config-tool/get_config.py` from a hid-remapper checkout. Confirm the expression slot the app used is empty again. Nothing may have been written to flash.

- [ ] **Step 5: Remove the probe**

Delete `src/bin/probe.rs`. Cargo auto-discovers `src/bin`, so deleting the file is sufficient.

Run: `cargo test`
Expected: 60 passed.

---

### Task 7: Icon rendering

**Files:**
- Create: `src/render.rs`
- Modify: `src/icon.rs`
- Modify: `src/lib.rs`

**Interfaces:**
- Consumes: `geometry::parse_path`, `compose::{Alpha, combine, downsample, to_premultiplied_bgra}`, `icon::{GLYPH_PATH, GLYPH_VIEWBOX}`.
- Produces:
  - `render::Renderer::new() -> windows::core::Result<Renderer>`
  - `render::Renderer::render_alpha(&self, size, draw) -> Result<Alpha>`
  - `render::Renderer::d2d(&self) -> &ID2D1Factory`, `render::Renderer::dwrite(&self) -> &IDWriteFactory`
  - `icon::build(r: &Renderer, badge: Option<u8>, dark_taskbar: bool, size: usize) -> Result<HICON>`

**Windows API note:** exact binding signatures shift between `windows` crate releases. The call sequences below target 0.62. If `rustc` reports a signature mismatch, adapt the call to what it reports — do not change which API is used or the order of operations. In 0.62 out-parameters come back inside a `Result` and COM interfaces are passed as `&T` or `Option<&T>`.

- [ ] **Step 1: Write the renderer**

Create `src/render.rs`, and add `pub mod render;` to `src/lib.rs`:

```rust
//! Shared Direct2D, DirectWrite and WIC factories.
//!
//! UI-thread only. None of these objects are shared with the device thread.

use crate::compose::Alpha;
use windows::core::Result;
use windows::Win32::Graphics::Direct2D::Common::{
    D2D1_ALPHA_MODE_PREMULTIPLIED, D2D1_PIXEL_FORMAT,
};
use windows::Win32::Graphics::Direct2D::{
    D2D1CreateFactory, ID2D1Factory, ID2D1RenderTarget, D2D1_FACTORY_TYPE_SINGLE_THREADED,
    D2D1_RENDER_TARGET_PROPERTIES, D2D1_RENDER_TARGET_TYPE_DEFAULT,
};
use windows::Win32::Graphics::DirectWrite::{
    DWriteCreateFactory, IDWriteFactory, DWRITE_FACTORY_TYPE_SHARED,
};
use windows::Win32::Graphics::Dxgi::Common::DXGI_FORMAT_B8G8R8A8_UNORM;
use windows::Win32::Graphics::Imaging::{
    CLSID_WICImagingFactory, IWICImagingFactory, GUID_WICPixelFormat32bppPBGRA,
    WICBitmapCacheOnLoad, WICBitmapLockRead,
};
use windows::Win32::System::Com::{CoCreateInstance, CLSCTX_INPROC_SERVER};

pub struct Renderer {
    d2d: ID2D1Factory,
    dwrite: IDWriteFactory,
    wic: IWICImagingFactory,
}

impl Renderer {
    pub fn new() -> Result<Self> {
        unsafe {
            let d2d: ID2D1Factory =
                D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, None)?;
            let dwrite: IDWriteFactory = DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED)?;
            let wic: IWICImagingFactory =
                CoCreateInstance(&CLSID_WICImagingFactory, None, CLSCTX_INPROC_SERVER)?;
            Ok(Renderer { d2d, dwrite, wic })
        }
    }

    pub fn d2d(&self) -> &ID2D1Factory {
        &self.d2d
    }

    pub fn dwrite(&self) -> &IDWriteFactory {
        &self.dwrite
    }

    /// Draws white-on-transparent into a square WIC bitmap and returns the
    /// alpha channel. Everything the icon draws is monochrome, so only
    /// coverage matters; the color is applied later in `compose`.
    pub fn render_alpha(
        &self,
        size: usize,
        draw: impl FnOnce(&ID2D1RenderTarget) -> Result<()>,
    ) -> Result<Alpha> {
        unsafe {
            let bitmap = self.wic.CreateBitmap(
                size as u32,
                size as u32,
                &GUID_WICPixelFormat32bppPBGRA,
                WICBitmapCacheOnLoad,
            )?;

            let props = D2D1_RENDER_TARGET_PROPERTIES {
                r#type: D2D1_RENDER_TARGET_TYPE_DEFAULT,
                pixelFormat: D2D1_PIXEL_FORMAT {
                    format: DXGI_FORMAT_B8G8R8A8_UNORM,
                    alphaMode: D2D1_ALPHA_MODE_PREMULTIPLIED,
                },
                dpiX: 96.0,
                dpiY: 96.0,
                ..Default::default()
            };
            let rt = self.d2d.CreateWicBitmapRenderTarget(&bitmap, &props)?;

            rt.BeginDraw();
            rt.Clear(None);
            draw(&rt)?;
            rt.EndDraw(None, None)?;

            let lock = bitmap.Lock(std::ptr::null(), WICBitmapLockRead.0 as u32)?;
            let stride = lock.GetStride()? as usize;
            let data = lock.GetDataPointer()?;
            let bytes = std::slice::from_raw_parts(data.0, data.1 as usize);

            let mut px = vec![0u8; size * size];
            for y in 0..size {
                for x in 0..size {
                    // Premultiplied BGRA: alpha is the fourth byte.
                    px[y * size + x] = bytes[y * stride + x * 4 + 3];
                }
            }
            Ok(Alpha { w: size, h: size, px })
        }
    }
}
```

If `IWICBitmapLock::GetDataPointer` in 0.62 returns something other than a pointer-and-length pair, use whatever it returns to build the slice. The rest of the function is unaffected.

- [ ] **Step 2: Build the icon**

Append to `src/icon.rs`, leaving the two constants from Task 4 unchanged:

```rust
use crate::compose::{combine, downsample, to_premultiplied_bgra, Alpha};
use crate::geometry::Segment;
use crate::render::Renderer;
use windows::core::{Result, PCWSTR};
use windows::Win32::Graphics::Direct2D::Common::{
    D2D1_BEZIER_SEGMENT, D2D1_COLOR_F, D2D1_FIGURE_BEGIN_FILLED, D2D1_FIGURE_END_CLOSED,
    D2D1_POINT_2F, D2D_RECT_F,
};
use windows::Win32::Graphics::Direct2D::{
    ID2D1RenderTarget, D2D1_FILL_MODE_WINDING, D2D1_ROUNDED_RECT,
};
use windows::Win32::Graphics::DirectWrite::{
    DWRITE_FONT_STRETCH_NORMAL, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_WEIGHT_SEMI_BOLD,
    DWRITE_PARAGRAPH_ALIGNMENT_CENTER, DWRITE_TEXT_ALIGNMENT_CENTER,
};
use windows::Win32::Graphics::Gdi::{
    CreateBitmap, CreateDIBSection, DeleteObject, BITMAPINFO, BITMAPINFOHEADER, BI_RGB,
    DIB_RGB_COLORS,
};
use windows::Win32::UI::WindowsAndMessaging::{CreateIconIndirect, HICON, ICONINFO};

/// Supersampling factor. The digit needs it far more than the glyph does.
const SS: usize = 4;

/// Badge geometry as a fraction of the icon box, placed so the digit clears
/// the glyph's band spacing.
const BADGE_CENTER: f32 = 0.68;
const BADGE_RADIUS: f32 = 0.34;
const DIGIT_HEIGHT: f32 = 0.52;

const WHITE: D2D1_COLOR_F = D2D1_COLOR_F { r: 1.0, g: 1.0, b: 1.0, a: 1.0 };

/// Builds the tray icon at `size` logical pixels.
///
/// `badge` is `None` for layer 0, which renders as the bare glyph.
pub fn build(
    r: &Renderer,
    badge: Option<u8>,
    dark_taskbar: bool,
    size: usize,
) -> Result<HICON> {
    let hi = size * SS;

    let glyph = r.render_alpha(hi, |rt| draw_glyph(rt, hi as f32))?;
    let (hole, digit) = match badge {
        None => (Alpha::new(hi, hi), Alpha::new(hi, hi)),
        Some(n) => (
            r.render_alpha(hi, |rt| draw_hole(rt, hi as f32))?,
            r.render_alpha(hi, |rt| draw_digit(r, rt, hi as f32, n))?,
        ),
    };

    let small = downsample(&combine(&glyph, &hole, &digit), SS);
    // White on dark taskbars, near-black on light ones.
    let rgb = if dark_taskbar { (255, 255, 255) } else { (0x19, 0x19, 0x19) };
    bgra_to_hicon(&to_premultiplied_bgra(&small, rgb), size)
}

fn draw_glyph(rt: &ID2D1RenderTarget, size: f32) -> Result<()> {
    unsafe {
        let factory = rt.GetFactory()?;
        let geo = factory.CreatePathGeometry()?;
        let sink = geo.Open()?;
        sink.SetFillMode(D2D1_FILL_MODE_WINDING);

        let scale = size / GLYPH_VIEWBOX;
        let pt = |p: crate::geometry::Point| D2D1_POINT_2F { x: p.x * scale, y: p.y * scale };

        // The path is a compile-time constant that Task 4's tests already
        // parse, so a failure here is a build-time mistake, not runtime input.
        let figures = crate::geometry::parse_path(GLYPH_PATH).expect("vendored glyph is valid");
        for f in figures {
            sink.BeginFigure(pt(f.start), D2D1_FIGURE_BEGIN_FILLED);
            for s in f.segments {
                match s {
                    Segment::Line(a) => sink.AddLine(pt(a)),
                    Segment::Cubic(a, b, c) => sink.AddBezier(&D2D1_BEZIER_SEGMENT {
                        point1: pt(a),
                        point2: pt(b),
                        point3: pt(c),
                    }),
                }
            }
            sink.EndFigure(D2D1_FIGURE_END_CLOSED);
        }
        sink.Close()?;

        let brush = rt.CreateSolidColorBrush(&WHITE, None)?;
        rt.FillGeometry(&geo, &brush, None);
        Ok(())
    }
}

fn badge_rect(size: f32) -> D2D_RECT_F {
    let c = size * BADGE_CENTER;
    let rad = size * BADGE_RADIUS;
    D2D_RECT_F { left: c - rad, top: c - rad, right: c + rad, bottom: c + rad }
}

fn draw_hole(rt: &ID2D1RenderTarget, size: f32) -> Result<()> {
    unsafe {
        let rad = size * BADGE_RADIUS;
        let rr = D2D1_ROUNDED_RECT { rect: badge_rect(size), radiusX: rad, radiusY: rad };
        let brush = rt.CreateSolidColorBrush(&WHITE, None)?;
        rt.FillRoundedRectangle(&rr, &brush);
        Ok(())
    }
}

fn draw_digit(r: &Renderer, rt: &ID2D1RenderTarget, size: f32, n: u8) -> Result<()> {
    unsafe {
        let family: Vec<u16> = "Segoe UI Variable Display\0".encode_utf16().collect();
        let locale: Vec<u16> = "en-us\0".encode_utf16().collect();
        let format = r.dwrite().CreateTextFormat(
            PCWSTR(family.as_ptr()),
            None,
            DWRITE_FONT_WEIGHT_SEMI_BOLD,
            DWRITE_FONT_STYLE_NORMAL,
            DWRITE_FONT_STRETCH_NORMAL,
            size * DIGIT_HEIGHT,
            PCWSTR(locale.as_ptr()),
        )?;
        format.SetTextAlignment(DWRITE_TEXT_ALIGNMENT_CENTER)?;
        format.SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER)?;

        let text: Vec<u16> = n.to_string().encode_utf16().collect();
        let brush = rt.CreateSolidColorBrush(&WHITE, None)?;
        rt.DrawText(
            &text,
            &format,
            &badge_rect(size),
            &brush,
            Default::default(),
            Default::default(),
        );
        Ok(())
    }
}

/// Wraps a premultiplied BGRA buffer in an HICON.
fn bgra_to_hicon(bgra: &[u8], size: usize) -> Result<HICON> {
    unsafe {
        let mut bits: *mut std::ffi::c_void = std::ptr::null_mut();
        let info = BITMAPINFO {
            bmiHeader: BITMAPINFOHEADER {
                biSize: std::mem::size_of::<BITMAPINFOHEADER>() as u32,
                biWidth: size as i32,
                // Negative height makes the DIB top-down, matching our buffer.
                biHeight: -(size as i32),
                biPlanes: 1,
                biBitCount: 32,
                biCompression: BI_RGB.0,
                ..Default::default()
            },
            ..Default::default()
        };
        let color = CreateDIBSection(None, &info, DIB_RGB_COLORS, &mut bits, None, 0)?;
        std::ptr::copy_nonoverlapping(bgra.as_ptr(), bits as *mut u8, bgra.len());

        // A 32bpp icon still needs a mask bitmap even though alpha does the work.
        let mask = CreateBitmap(size as i32, size as i32, 1, 1, None);

        let ii = ICONINFO {
            fIcon: true.into(),
            hbmColor: color,
            hbmMask: mask,
            ..Default::default()
        };
        let icon = CreateIconIndirect(&ii)?;
        let _ = DeleteObject(color.into());
        let _ = DeleteObject(mask.into());
        Ok(icon)
    }
}
```

- [ ] **Step 3: Build and resolve signature mismatches**

Run: `cargo build`

Expected: it may not compile on the first attempt. Work through each `rustc` error by adjusting the call to the signature it reports. Likely spots: `CreateSolidColorBrush` may want `Some(&D2D1_BRUSH_PROPERTIES::default())`; `DrawText`'s last two arguments are `D2D1_DRAW_TEXT_OPTIONS` and `DWRITE_MEASURING_MODE`; `CreateDIBSection` may return `HBITMAP` directly rather than in a `Result`; `D2D1_ROUNDED_RECT` may live in `Direct2D::Common` rather than `Direct2D`.

- [ ] **Step 4: Verify the icon builds at every size and badge**

Create `src/bin/iconprobe.rs`:

```rust
fn main() -> windows::core::Result<()> {
    unsafe {
        windows::Win32::System::Com::CoInitializeEx(
            None,
            windows::Win32::System::Com::COINIT_APARTMENTTHREADED,
        )
        .ok()?;
    }
    let r = layers::render::Renderer::new()?;
    for size in [16usize, 20, 24, 32] {
        for badge in [None, Some(2u8), Some(7u8)] {
            for dark in [true, false] {
                let icon = layers::icon::build(&r, badge, dark, size)?;
                assert!(!icon.is_invalid(), "null icon at {size} {badge:?} {dark}");
                unsafe {
                    let _ = windows::Win32::UI::WindowsAndMessaging::DestroyIcon(icon);
                }
            }
        }
    }
    println!("all 24 icon variants built");
    Ok(())
}
```

Run: `cargo run --bin iconprobe`
Expected: `all 24 icon variants built`, no panic.

Delete `src/bin/iconprobe.rs` afterwards. Visual confirmation happens in Task 8 when the icon reaches the tray.

- [ ] **Step 5: Run the tests**

Run: `cargo test`
Expected: 60 passed. The Task 4 tests referencing `icon::GLYPH_PATH` must still pass.

---

### Task 8: Theme, tray, and the running application

**Files:**
- Create: `src/theme.rs`
- Create: `src/tray.rs`
- Create: `app.manifest`
- Create: `build.rs`
- Modify: `src/main.rs`
- Modify: `src/lib.rs`

This is the milestone task. At the end of it the application runs, shows a tray icon, and the badge changes when you switch layers.

**Interfaces:**
- Consumes: `device::{spawn, State, Status}`, `icon::build`, `render::Renderer`, `protocol::Layers`.
- Produces:
  - `theme::dark_taskbar() -> bool`, `theme::dark_apps() -> bool`, `theme::accent() -> (u8, u8, u8)`
  - `theme::watch(hwnd: HWND, msg: u32)`
  - `tray::Tray::new(hwnd) -> Result<Tray>`, `Tray::set(&mut self, icon: HICON, tip: &str)`, `Tray::remove(&mut self)`
  - `tray::{WM_TRAY, WM_DEVICE, WM_THEME}`

- [ ] **Step 1: Write the theme module**

Create `src/theme.rs`, and add `pub mod theme;` to `src/lib.rs`:

```rust
//! Theme and accent color, read from the registry and watched for changes.
//!
//! Windows tracks the taskbar theme and the app theme separately, so this
//! module does too.

use windows::core::PCWSTR;
use windows::Win32::Foundation::{BOOL, HWND};
use windows::Win32::Graphics::Dwm::DwmGetColorizationColor;
use windows::Win32::System::Registry::{
    RegGetValueW, RegNotifyChangeKeyValue, RegOpenKeyExW, HKEY, HKEY_CURRENT_USER, KEY_NOTIFY,
    KEY_READ, REG_NOTIFY_CHANGE_LAST_SET, RRF_RT_REG_DWORD,
};
use windows::Win32::UI::WindowsAndMessaging::PostMessageW;

const PERSONALIZE: &str = r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

fn dword(value: &str) -> Option<u32> {
    unsafe {
        let key = wide(PERSONALIZE);
        let name = wide(value);
        let mut data = 0u32;
        let mut size = std::mem::size_of::<u32>() as u32;
        RegGetValueW(
            HKEY_CURRENT_USER,
            PCWSTR(key.as_ptr()),
            PCWSTR(name.as_ptr()),
            RRF_RT_REG_DWORD,
            None,
            Some(&mut data as *mut u32 as *mut _),
            Some(&mut size),
        )
        .ok()
        .ok()?;
        Some(data)
    }
}

/// A missing value means the Windows default, which is a dark taskbar.
pub fn dark_taskbar() -> bool {
    dword("SystemUsesLightTheme").unwrap_or(0) == 0
}

/// A missing value means the Windows default, which is light apps.
pub fn dark_apps() -> bool {
    dword("AppsUseLightTheme").unwrap_or(1) == 0
}

pub fn accent() -> (u8, u8, u8) {
    unsafe {
        let mut color = 0u32;
        let mut opaque = BOOL(0);
        if DwmGetColorizationColor(&mut color, &mut opaque).is_err() {
            // Windows default blue.
            return (0x00, 0x78, 0xD4);
        }
        (
            ((color >> 16) & 0xFF) as u8,
            ((color >> 8) & 0xFF) as u8,
            (color & 0xFF) as u8,
        )
    }
}

/// Spawns a thread that posts `msg` to `hwnd` whenever the Personalize key
/// changes, so a theme switch re-tints the icon without a restart.
pub fn watch(hwnd: HWND, msg: u32) {
    let hwnd = hwnd.0 as isize;
    std::thread::spawn(move || unsafe {
        let key_name = wide(PERSONALIZE);
        let mut key = HKEY::default();
        if RegOpenKeyExW(
            HKEY_CURRENT_USER,
            PCWSTR(key_name.as_ptr()),
            None,
            KEY_READ | KEY_NOTIFY,
            &mut key,
        )
        .is_err()
        {
            return;
        }
        loop {
            if RegNotifyChangeKeyValue(key, false, REG_NOTIFY_CHANGE_LAST_SET, None, false)
                .is_err()
            {
                return;
            }
            let _ = PostMessageW(
                Some(HWND(hwnd as *mut _)),
                msg,
                Default::default(),
                Default::default(),
            );
        }
    });
}
```

- [ ] **Step 2: Write the tray module**

Create `src/tray.rs`, and add `pub mod tray;` to `src/lib.rs`:

```rust
//! Tray icon lifecycle.
//!
//! The tray-icon crate is deliberately not used: the popup is custom drawn
//! and the icon is swapped on every layer change, so the crate's menu model
//! would only be in the way.

use windows::core::Result;
use windows::Win32::Foundation::HWND;
use windows::Win32::UI::Shell::{
    Shell_NotifyIconW, NIF_ICON, NIF_MESSAGE, NIF_TIP, NIM_ADD, NIM_DELETE, NIM_MODIFY,
    NOTIFYICONDATAW,
};
use windows::Win32::UI::WindowsAndMessaging::{DestroyIcon, HICON, WM_APP};

/// Mouse activity on the tray icon.
pub const WM_TRAY: u32 = WM_APP + 1;
/// The device thread reports a new state.
pub const WM_DEVICE: u32 = WM_APP + 2;
/// The theme watcher reports a change.
pub const WM_THEME: u32 = WM_APP + 3;

pub struct Tray {
    data: NOTIFYICONDATAW,
    added: bool,
    icon: Option<HICON>,
}

impl Tray {
    pub fn new(hwnd: HWND) -> Result<Self> {
        let mut data = NOTIFYICONDATAW {
            cbSize: std::mem::size_of::<NOTIFYICONDATAW>() as u32,
            hWnd: hwnd,
            uID: 1,
            uFlags: NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage: WM_TRAY,
            ..Default::default()
        };
        unsafe {
            Shell_NotifyIconW(NIM_ADD, &mut data).ok()?;
        }
        Ok(Tray { data, added: true, icon: None })
    }

    /// Replaces the icon and tooltip. Takes ownership of `icon` and destroys
    /// the one it replaces.
    pub fn set(&mut self, icon: HICON, tip: &str) {
        self.data.hIcon = icon;
        let mut buf = [0u16; 128];
        for (i, c) in tip.encode_utf16().take(127).enumerate() {
            buf[i] = c;
        }
        self.data.szTip = buf;
        unsafe {
            let _ = Shell_NotifyIconW(NIM_MODIFY, &mut self.data);
            if let Some(old) = self.icon.replace(icon) {
                let _ = DestroyIcon(old);
            }
        }
    }

    pub fn remove(&mut self) {
        if self.added {
            unsafe {
                let _ = Shell_NotifyIconW(NIM_DELETE, &mut self.data);
                if let Some(icon) = self.icon.take() {
                    let _ = DestroyIcon(icon);
                }
            }
            self.added = false;
        }
    }
}

impl Drop for Tray {
    fn drop(&mut self) {
        self.remove();
    }
}
```

- [ ] **Step 3: Add the manifest and build script**

`app.manifest`:

```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
  <assemblyIdentity type="win32" name="Layers" version="1.0.0.0"/>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
    </windowsSettings>
  </application>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/>
    </application>
  </compatibility>
</assembly>
```

`build.rs`:

```rust
fn main() {
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() != Ok("windows") {
        return;
    }
    let mut res = winresource::WindowsResource::new();
    res.set_icon("assets/app.ico");
    res.set_manifest_file("app.manifest");
    res.compile().expect("failed to embed resources");
    println!("cargo:rerun-if-changed=assets/app.ico");
    println!("cargo:rerun-if-changed=app.manifest");
}
```

Copy the user's icon into place:

```bash
cp "/c/Users/artistro08/Documents/Layers App Icon.ico" assets/app.ico
```

- [ ] **Step 4: Wire it together**

Replace `src/main.rs`:

```rust
// No console window.
#![windows_subsystem = "windows"]

use layers::{device, icon, protocol, render, theme, tray};
use std::cell::RefCell;
use windows::core::{Result, PCWSTR};
use windows::Win32::Foundation::{ERROR_ALREADY_EXISTS, GetLastError, HWND, LPARAM, LRESULT, WPARAM};
use windows::Win32::System::Com::{CoInitializeEx, COINIT_APARTMENTTHREADED};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::Threading::CreateMutexW;
use windows::Win32::UI::HiDpi::GetDpiForWindow;
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, DispatchMessageW, GetMessageW, PostMessageW,
    PostQuitMessage, RegisterClassW, TranslateMessage, CW_USEDEFAULT, HWND_MESSAGE, MSG,
    WINDOW_EX_STYLE, WINDOW_STYLE, WM_DESTROY, WM_DPICHANGED, WNDCLASSW,
};

struct App {
    renderer: render::Renderer,
    tray: tray::Tray,
    state: device::State,
    device: Option<device::Handle>,
}

thread_local! {
    static APP: RefCell<Option<App>> = const { RefCell::new(None) };
}

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

fn main() -> Result<()> {
    unsafe {
        CoInitializeEx(None, COINIT_APARTMENTTHREADED).ok()?;

        // Single instance. A second launch must not stack a duplicate icon.
        let name = wide("Local\\LayersTrayApp");
        let _mutex = CreateMutexW(None, true, PCWSTR(name.as_ptr()))?;
        if GetLastError() == ERROR_ALREADY_EXISTS {
            return Ok(());
        }

        let instance = GetModuleHandleW(None)?;
        let class = wide("LayersMessageWindow");
        let wc = WNDCLASSW {
            lpfnWndProc: Some(wndproc),
            hInstance: instance.into(),
            lpszClassName: PCWSTR(class.as_ptr()),
            ..Default::default()
        };
        RegisterClassW(&wc);

        let hwnd = CreateWindowExW(
            WINDOW_EX_STYLE::default(),
            PCWSTR(class.as_ptr()),
            PCWSTR(class.as_ptr()),
            WINDOW_STYLE::default(),
            CW_USEDEFAULT,
            CW_USEDEFAULT,
            0,
            0,
            Some(HWND_MESSAGE),
            None,
            Some(instance.into()),
            None,
        )?;

        let post_hwnd = hwnd.0 as isize;
        let handle = device::spawn(move |state| {
            // Packs status and mask into wParam so the UI thread owns all state.
            let packed = ((state.status as usize) << 8) | state.layers.0 as usize;
            let _ = PostMessageW(
                Some(HWND(post_hwnd as *mut _)),
                tray::WM_DEVICE,
                WPARAM(packed),
                LPARAM(0),
            );
        });

        APP.with(|a| -> Result<()> {
            *a.borrow_mut() = Some(App {
                renderer: render::Renderer::new()?,
                tray: tray::Tray::new(hwnd)?,
                state: device::State {
                    status: device::Status::Disconnected,
                    layers: protocol::Layers(1),
                },
                device: Some(handle),
            });
            Ok(())
        })?;

        theme::watch(hwnd, tray::WM_THEME);
        refresh(hwnd);

        let mut msg = MSG::default();
        while GetMessageW(&mut msg, None, 0, 0).as_bool() {
            let _ = TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        Ok(())
    }
}

/// Rebuilds the tray icon and tooltip from current state and theme.
fn refresh(hwnd: HWND) {
    APP.with(|a| {
        let mut borrow = a.borrow_mut();
        let Some(app) = borrow.as_mut() else { return };

        let dpi = unsafe { GetDpiForWindow(hwnd) }.max(96);
        // 16 at 100%, 20 at 125%, 24 at 150%, 32 at 200%. Rounded up to a
        // multiple of 4 so the supersampled buffer divides cleanly.
        let size = (16 * dpi as usize / 96).next_multiple_of(4);

        let connected = app.state.status == device::Status::Connected;
        let badge = if connected { app.state.layers.badge() } else { None };
        let tip = match app.state.status {
            device::Status::Disconnected => "HID Remapper disconnected".to_string(),
            device::Status::NoSlot => "Connected, layer unavailable".to_string(),
            device::Status::VersionMismatch => "Unsupported firmware version".to_string(),
            device::Status::Connected => app.state.layers.label(),
        };

        if let Ok(icon) = icon::build(&app.renderer, badge, theme::dark_taskbar(), size) {
            app.tray.set(icon, &tip);
        }
    });
}

unsafe extern "system" fn wndproc(hwnd: HWND, msg: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    match msg {
        tray::WM_DEVICE => {
            APP.with(|a| {
                if let Some(app) = a.borrow_mut().as_mut() {
                    let packed = wp.0;
                    app.state.layers = protocol::Layers((packed & 0xFF) as u8);
                    app.state.status = match packed >> 8 {
                        0 => device::Status::Disconnected,
                        1 => device::Status::NoSlot,
                        2 => device::Status::Connected,
                        _ => device::Status::VersionMismatch,
                    };
                }
            });
            refresh(hwnd);
            LRESULT(0)
        }
        tray::WM_THEME | WM_DPICHANGED => {
            refresh(hwnd);
            LRESULT(0)
        }
        WM_DESTROY => {
            APP.with(|a| {
                if let Some(mut app) = a.borrow_mut().take() {
                    app.tray.remove();
                    if let Some(h) = app.device.take() {
                        h.shutdown();
                    }
                }
            });
            unsafe { PostQuitMessage(0) };
            LRESULT(0)
        }
        _ => unsafe { DefWindowProcW(hwnd, msg, wp, lp) },
    }
}
```

The `device::Status` discriminants are relied on by the packing above: `Disconnected` is 0, `NoSlot` is 1, `Connected` is 2. Do not reorder the enum without updating both sides.

- [ ] **Step 5: Run it**

Run: `cargo run`

Expected: a tray icon appears. With the remapper connected and on layer 0 it is the bare Fluent layer glyph. Switch to layer 1 on the peripheral and the icon gains a "1" badge within a fraction of a second. Hover shows "Layer 1". Return to layer 0 and the badge disappears.

Unplug the device: the tooltip becomes "HID Remapper disconnected". Replug: it recovers within about 2 seconds.

Switch Windows between light and dark in Settings and confirm the glyph flips between white and near-black without a restart.

If the badge digit is illegible at 100% scaling, adjust `BADGE_CENTER`, `BADGE_RADIUS`, and `DIGIT_HEIGHT` in `src/icon.rs` — those three constants exist to be tuned against the real taskbar, which is the only place the result can honestly be judged.

- [ ] **Step 6: Run the tests**

Run: `cargo test`
Expected: 60 passed.

---

### Task 9: The popup

**Files:**
- Create: `src/popup.rs`
- Modify: `src/main.rs`
- Modify: `src/lib.rs`

**Interfaces:**
- Consumes: `render::Renderer`, `theme::{dark_apps, accent}`, `device::{State, Status}`.
- Produces:
  - `popup::Popup::new(hwnd_owner: HWND) -> Result<Popup>`
  - `popup::Popup::show(&mut self, r: &Renderer, state: device::State) -> Result<()>`
  - `popup::Popup::hide(&mut self)`
  - `popup::Popup::is_visible(&self) -> bool`
  - `popup::QUIT_CLICKED: u32`
  - `popup::{WIDTH, HEIGHT, ROW_HEIGHT, PADDING, CORNER, Row, row_at, place}`

- [ ] **Step 1: Write the layout primitives and their failing tests**

The drawing cannot be unit tested, but placement and hit testing can, and that is where off-by-one bugs live.

Create `src/popup.rs`, and add `pub mod popup;` to `src/lib.rs`:

```rust
//! The Fluent popup.
//!
//! A layered window painted through Direct2D. Layered rather than a DWM
//! backdrop because DWMWA_SYSTEMBACKDROP_TYPE does not compose with a
//! Direct2D-painted client area without a DXGI composition swapchain.

use windows::Win32::Foundation::{POINT, RECT};
use windows::Win32::UI::WindowsAndMessaging::WM_APP;

/// Posted to the owner window when the Quit row is clicked.
pub const QUIT_CLICKED: u32 = WM_APP + 4;

/// Logical layout in pixels at 96 dpi.
pub const WIDTH: f32 = 248.0;
pub const ROW_HEIGHT: f32 = 40.0;
pub const PADDING: f32 = 8.0;
pub const CORNER: f32 = 8.0;
pub const HEIGHT: f32 = PADDING * 2.0 + ROW_HEIGHT * 3.0;

#[derive(Clone, Copy, PartialEq, Debug)]
pub enum Row {
    Status,
    Layer,
    Quit,
}

/// Which row contains a y coordinate, in client pixels at 96 dpi.
pub fn row_at(y: f32) -> Option<Row> {
    if y < PADDING {
        return None;
    }
    match ((y - PADDING) / ROW_HEIGHT).floor() as i32 {
        0 => Some(Row::Status),
        1 => Some(Row::Layer),
        2 => Some(Row::Quit),
        _ => None,
    }
}

/// Clamps the popup into the work area so it never hangs off screen or under
/// the taskbar. Prefers opening above the cursor, as a taskbar flyout does.
pub fn place(cursor: POINT, work: RECT, w: i32, h: i32) -> POINT {
    let x = (cursor.x - w / 2).clamp(work.left, (work.right - w).max(work.left));
    let y = if cursor.y - h - 12 >= work.top {
        cursor.y - h - 12
    } else {
        (cursor.y + 12).min((work.bottom - h).max(work.top))
    };
    POINT { x, y }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn work() -> RECT {
        RECT { left: 0, top: 0, right: 1920, bottom: 1040 }
    }

    #[test]
    fn hit_testing_maps_each_row_band() {
        assert_eq!(row_at(PADDING + 1.0), Some(Row::Status));
        assert_eq!(row_at(PADDING + ROW_HEIGHT + 1.0), Some(Row::Layer));
        assert_eq!(row_at(PADDING + ROW_HEIGHT * 2.0 + 1.0), Some(Row::Quit));
    }

    #[test]
    fn hit_testing_rejects_the_padding_above_the_first_row() {
        assert_eq!(row_at(PADDING - 1.0), None);
    }

    #[test]
    fn hit_testing_rejects_the_padding_below_the_last_row() {
        assert_eq!(row_at(PADDING + ROW_HEIGHT * 3.0 + 1.0), None);
    }

    #[test]
    fn the_popup_opens_above_the_cursor_when_there_is_room() {
        let p = place(POINT { x: 960, y: 1000 }, work(), 248, 136);
        assert!(p.y < 1000 - 136);
    }

    #[test]
    fn the_popup_drops_below_the_cursor_when_there_is_no_room_above() {
        let p = place(POINT { x: 960, y: 5 }, work(), 248, 136);
        assert!(p.y > 5);
    }

    #[test]
    fn the_popup_never_hangs_off_the_right_edge() {
        let p = place(POINT { x: 1918, y: 1000 }, work(), 248, 136);
        assert_eq!(p.x, 1920 - 248);
    }

    #[test]
    fn the_popup_never_hangs_off_the_left_edge() {
        let p = place(POINT { x: 2, y: 1000 }, work(), 248, 136);
        assert_eq!(p.x, 0);
    }

    #[test]
    fn the_popup_stays_inside_a_work_area_that_does_not_start_at_the_origin() {
        let w = RECT { left: 1920, top: 0, right: 3840, bottom: 1040 };
        let p = place(POINT { x: 1921, y: 1000 }, w, 248, 136);
        assert_eq!(p.x, 1920);
    }

    #[test]
    fn the_three_rows_plus_padding_account_for_the_full_height() {
        assert_eq!(HEIGHT, PADDING * 2.0 + ROW_HEIGHT * 3.0);
    }
}
```

- [ ] **Step 2: Run the tests and confirm they pass**

Run: `cargo test`
Expected: 69 passed. These particular tests pass immediately because the functions are written alongside them — they are regression guards for the drawing work that follows, not a red-green cycle.

- [ ] **Step 3: Add the palette**

Append to `src/popup.rs`:

```rust
use windows::Win32::Graphics::Direct2D::Common::D2D1_COLOR_F;

/// Fluent surface colors. Alpha below 1.0 gives translucency without blur.
pub fn surface(dark: bool) -> D2D1_COLOR_F {
    if dark {
        D2D1_COLOR_F { r: 0.17, g: 0.17, b: 0.17, a: 0.97 }
    } else {
        D2D1_COLOR_F { r: 0.98, g: 0.98, b: 0.98, a: 0.97 }
    }
}

pub fn text(dark: bool) -> D2D1_COLOR_F {
    if dark {
        D2D1_COLOR_F { r: 1.0, g: 1.0, b: 1.0, a: 0.90 }
    } else {
        D2D1_COLOR_F { r: 0.10, g: 0.10, b: 0.10, a: 0.95 }
    }
}

/// Faint fill behind the row under the pointer.
pub fn hover(dark: bool) -> D2D1_COLOR_F {
    let v = if dark { 1.0 } else { 0.0 };
    D2D1_COLOR_F { r: v, g: v, b: v, a: 0.06 }
}

pub fn border(dark: bool) -> D2D1_COLOR_F {
    let v = if dark { 1.0 } else { 0.0 };
    D2D1_COLOR_F { r: v, g: v, b: v, a: 0.12 }
}

/// Status dot color: green connected, amber no slot, red disconnected.
pub fn status_dot(status: crate::device::Status) -> D2D1_COLOR_F {
    match status {
        crate::device::Status::Connected => {
            D2D1_COLOR_F { r: 0.42, g: 0.80, b: 0.37, a: 1.0 }
        }
        crate::device::Status::NoSlot | crate::device::Status::VersionMismatch => {
            D2D1_COLOR_F { r: 0.97, g: 0.69, b: 0.11, a: 1.0 }
        }
        crate::device::Status::Disconnected => {
            D2D1_COLOR_F { r: 0.91, g: 0.07, b: 0.14, a: 1.0 }
        }
    }
}

/// The status row's first line.
pub fn status_label(status: crate::device::Status) -> &'static str {
    match status {
        crate::device::Status::Connected => "Connected",
        crate::device::Status::NoSlot => "Connected, layer unavailable",
        crate::device::Status::VersionMismatch => "Unsupported firmware",
        crate::device::Status::Disconnected => "Disconnected",
    }
}

/// The status row's optional second line, explaining a degraded state.
pub fn status_detail(status: crate::device::Status) -> Option<&'static str> {
    match status {
        crate::device::Status::NoSlot => Some("All 8 expression slots are in use"),
        crate::device::Status::VersionMismatch => {
            Some("This app supports config version 18")
        }
        _ => None,
    }
}
```

- [ ] **Step 4: Implement the window and drawing**

Append to `src/popup.rs`. Build it as these five private pieces so the window procedure stays readable:

1. `register_class(instance)` — a `WNDCLASSW` named `LayersPopupWindow` with `wndproc` below.
2. `create(instance, owner) -> HWND` — `CreateWindowExW` with extended style `WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE` and style `WS_POPUP`, sized `WIDTH` by `HEIGHT` scaled by the monitor DPI. Store the owner `HWND` in `GWLP_USERDATA` so the Quit click can be posted back.
3. `paint(r: &Renderer, state: State, hovered: Option<Row>, scale: f32) -> Result<(Vec<u8>, i32, i32)>` — creates a WIC bitmap render target of `WIDTH * scale` by `HEIGHT * scale`, then in order:
   - `FillRoundedRectangle` over the whole surface with radius `CORNER * scale` in `surface(dark)`.
   - `DrawRoundedRectangle` with the same geometry, `border(dark)`, stroke width `scale`.
   - For the hovered row, `FillRoundedRectangle` inset by `PADDING/2 * scale` with radius `4.0 * scale` in `hover(dark)`.
   - Status row: `FillEllipse` an 8-pixel dot at `PADDING * 2` from the left, vertically centered, in `status_dot(state.status)`; then `DrawText` `status_label(state.status)` in `text(dark)` at 14 * scale. When `status_detail(state.status)` is `Some`, draw the label at 13 * scale on the upper half of the row and the detail beneath it at 11 * scale in `text(dark)` at 60% alpha.
   - Layer row: `DrawText` `state.layers.label()` on the left. On the right, when `state.status == Status::Connected` and `state.layers.badge()` is `Some(n)`, `FillRoundedRectangle` a pill 28 by 22 scaled, filled with `accent()` converted to a `D2D1_COLOR_F`, and `DrawText` the digit inside it in white. Otherwise `DrawText` an em dash in `text(dark)` at 40% alpha.
   - Quit row: `DrawText` "Quit" in `text(dark)`.
   Returns premultiplied BGRA plus pixel dimensions. Reuse `Renderer::render_alpha`'s WIC setup pattern, but read all four channels rather than only alpha, since the popup is not monochrome.
4. `push(hwnd, bgra, w, h, pos)` — builds a top-down 32bpp DIB with `CreateDIBSection`, copies the buffer in, selects it into a `CreateCompatibleDC`, and calls `UpdateLayeredWindow` with `BLENDFUNCTION { BlendOp: AC_SRC_OVER as u8, BlendFlags: 0, SourceConstantAlpha: 255, AlphaFormat: AC_SRC_ALPHA as u8 }` and `ULW_ALPHA`. Releases the DC and deletes the bitmap afterwards.
5. `wndproc` handling:
   - `WM_MOUSEMOVE`: recompute the hovered row from `row_at(y / scale)`; if it changed, repaint and push.
   - `WM_LBUTTONUP`: if the hovered row is `Row::Quit`, `PostMessageW(owner, QUIT_CLICKED, ...)` and hide.
   - `WM_KILLFOCUS` and `WM_ACTIVATEAPP` with `wParam == 0`: hide.
   - `WM_KEYDOWN` with `wParam == VK_ESCAPE.0 as usize`: hide.

`Popup::show` reads the cursor with `GetCursorPos`, finds the monitor with `MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST)` and its work area with `GetMonitorInfoW`, calls `place`, paints, pushes, then `ShowWindow(SW_SHOWNOACTIVATE)` followed by `SetForegroundWindow` so kill-focus dismissal works.

`Popup::hide` calls `ShowWindow(SW_HIDE)` and clears the hovered row.

- [ ] **Step 5: Hook the popup into the tray**

In `src/main.rs`, add `popup: popup::Popup` to `App`, construct it after the tray, and extend `wndproc`:

```rust
        tray::WM_TRAY => {
            // The shell packs the mouse message into the low word of lParam.
            let event = (lp.0 as u32) & 0xFFFF;
            if event == windows::Win32::UI::WindowsAndMessaging::WM_LBUTTONUP
                || event == windows::Win32::UI::WindowsAndMessaging::WM_RBUTTONUP
            {
                APP.with(|a| {
                    if let Some(app) = a.borrow_mut().as_mut() {
                        let state = app.state;
                        let _ = app.popup.show(&app.renderer, state);
                    }
                });
            }
            LRESULT(0)
        }
        popup::QUIT_CLICKED => {
            let _ = unsafe { windows::Win32::UI::WindowsAndMessaging::DestroyWindow(hwnd) };
            LRESULT(0)
        }
```

Also repaint the popup from the `WM_DEVICE` arm when it is visible, so an open popup does not go stale on a layer switch:

```rust
            APP.with(|a| {
                if let Some(app) = a.borrow_mut().as_mut() {
                    if app.popup.is_visible() {
                        let state = app.state;
                        let _ = app.popup.show(&app.renderer, state);
                    }
                }
            });
```

Opening on both mouse buttons is intentional. The popup is the only interaction, so making left click do nothing would be a pointless distinction.

- [ ] **Step 6: Run it**

Run: `cargo run`

Expected: left or right clicking the tray icon opens a rounded translucent panel near the cursor showing a green dot with "Connected", the current layer with an accent-colored pill, and "Quit". Rows highlight under the pointer. Clicking elsewhere or pressing Escape dismisses it. Clicking Quit removes the tray icon and exits the process.

Switch layers while the popup is open and confirm it updates rather than going stale.

Check it near all four screen edges, and on a second monitor if one is available, confirming it never opens partly off screen or under the taskbar.

- [ ] **Step 7: Run the tests**

Run: `cargo test`
Expected: 69 passed.

---

### Task 10: Installer and documentation

**Files:**
- Create: `installer/layers.iss`
- Create: `README.md`

**Interfaces:**
- Consumes: `target/release/layers.exe` from `cargo build --release`.
- Produces: `installer/Output/layers-setup.exe`.

- [ ] **Step 1: Write the Inno Setup script**

`installer/layers.iss`:

```ini
[Setup]
AppId={{8B4D6C21-5E3A-4C7E-9F2B-7A1D0E5C3B94}
AppName=Layers
AppVersion=0.1.0
DefaultDirName={localappdata}\Layers
DefaultGroupName=Layers
DisableProgramGroupPage=yes
DisableDirPage=yes
UninstallDisplayIcon={app}\layers.exe
OutputDir=Output
OutputBaseFilename=layers-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; Per-user, so no UAC prompt.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Files]
Source: "..\target\release\layers.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\assets\NOTICE-fluentui.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Layers"; Filename: "{app}\layers.exe"

[Tasks]
Name: "startup"; Description: "Start Layers when I sign in"; GroupDescription: "Additional options:"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Layers"; ValueData: """{app}\layers.exe"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\layers.exe"; Description: "Start Layers now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Close the running instance so the exe is not locked during uninstall.
Filename: "taskkill.exe"; Parameters: "/F /IM layers.exe"; Flags: runhidden skipifdoesntexist
```

The `Tasks: startup` on the registry entry is load-bearing. Without it the Run key is written regardless of the checkbox.

- [ ] **Step 2: Build the release binary**

Run: `cargo build --release`
Expected: `target/release/layers.exe` exists and is under about 2MB.

- [ ] **Step 3: Build the installer**

Run: `& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\layers.iss`

Expected: `installer/Output/layers-setup.exe` is produced. If Inno Setup 6 is not installed, get it from https://jrsoftware.org/isdl.php first.

- [ ] **Step 4: Test install and uninstall**

Run the setup, leave "Start Layers when I sign in" checked, finish. Confirm:
- No UAC prompt appeared at any point.
- The tray icon appears.
- "Layers" is in the Start Menu.
- "Layers" appears in Settings, Apps, Installed apps, with the correct icon.
- `reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v Layers` returns the install path.

Then uninstall and confirm the tray icon disappears, the install directory is gone, and the same `reg query` now fails.

Reinstall with the startup checkbox cleared and confirm the Run key is absent.

- [ ] **Step 5: Write the README**

`README.md`:

```markdown
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
```

- [ ] **Step 6: Final safety check**

Run: `cargo test`
Expected: 69 passed.

Run: `grep -rn "PERSIST_CONFIG\|CLEAR_EXPRESSIONS\|CMD_SUSPEND\|build_packet(7\|build_packet(19\|build_packet(10" src/`
Expected: no matches, or matches only inside comments. This is the guard for the three commands that must never reach the device.
