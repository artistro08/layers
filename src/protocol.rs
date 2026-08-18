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
