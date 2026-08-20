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
/// The monitor input reports live in a second top-level collection. Windows
/// exposes each top-level collection as its own device path, so reading them
/// needs a separate handle from the one used for config feature reports.
pub const MONITOR_USAGE: u16 = 0x0021;

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
    let crc = crc_of(&p).to_le_bytes();
    p[CRC_START..].copy_from_slice(&crc);
    p
}

pub fn verify_crc(packet: &[u8]) -> bool {
    if packet.len() < PACKET_LEN {
        return false;
    }
    let claimed = u32::from_le_bytes(packet[CRC_START..PACKET_LEN].try_into().unwrap());
    crc_of(packet) == claimed
}

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
    /// Whether this slot still holds our injected expression. Checked
    /// periodically: loading a config from the web tool sends
    /// `CLEAR_EXPRESSIONS`, which wipes ours along with everything else.
    pub fn is_ours(&self) -> bool {
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
}
