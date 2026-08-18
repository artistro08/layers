//! Persisted user settings: HUD master switch and per-layer suppression.
//!
//! Stored under `HKCU\Software\Layers` as `REG_DWORD` values. `theme.rs`
//! already shows the `RegGetValueW` read pattern; writing needs
//! `RegCreateKeyExW` (the key may not exist yet) then `RegSetValueExW`.
//!
//! The registry I/O is split from the pure logic below it so the logic is
//! reachable from tests without touching the registry.

use crate::protocol;

const KEY: &str = r"Software\Layers";
const HUD_ENABLED: &str = "HudEnabled";
const HUD_SUPPRESSED_LAYERS: &str = "HudSuppressedLayers";

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Settings {
    pub hud_enabled: bool,
    pub hud_suppressed: u8,
}

impl Default for Settings {
    fn default() -> Self {
        Settings { hud_enabled: true, hud_suppressed: 0 }
    }
}

impl Settings {
    /// Missing or unreadable values fall back to the defaults. A stale
    /// `SeenLayers` value left over from a previous build is simply ignored.
    pub fn load() -> Settings {
        let defaults = Settings::default();
        Settings {
            hud_enabled: reg::dword(HUD_ENABLED)
                .map(|v| v != 0)
                .unwrap_or(defaults.hud_enabled),
            hud_suppressed: reg::dword(HUD_SUPPRESSED_LAYERS)
                .map(|v| v as u8)
                .unwrap_or(defaults.hud_suppressed),
        }
    }

    /// Best effort — a failure to persist must never break the running app.
    pub fn save(&self) {
        let _ = reg::set_dword(HUD_ENABLED, self.hud_enabled as u32);
        let _ = reg::set_dword(HUD_SUPPRESSED_LAYERS, self.hud_suppressed as u32);
    }

    /// The layer a HUD would be announcing: the highest active layer, which
    /// is the same one the tray icon badges.
    pub fn displayed_layer(layers: protocol::Layers) -> u8 {
        *layers.active().last().unwrap()
    }

    /// Whether a HUD should be shown for this layer state.
    pub fn hud_allowed(&self, layers: protocol::Layers) -> bool {
        self.hud_enabled
            && (self.hud_suppressed & (1 << Settings::displayed_layer(layers))) == 0
    }
}

mod reg {
    use windows::core::PCWSTR;
    use windows::Win32::System::Registry::{
        RegCreateKeyExW, RegGetValueW, RegSetValueExW, HKEY, HKEY_CURRENT_USER, KEY_WRITE,
        REG_DWORD, REG_OPTION_NON_VOLATILE, RRF_RT_REG_DWORD,
    };

    use super::KEY;

    fn wide(s: &str) -> Vec<u16> {
        s.encode_utf16().chain(std::iter::once(0)).collect()
    }

    pub fn dword(value: &str) -> Option<u32> {
        unsafe {
            let key = wide(KEY);
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

    pub fn set_dword(name: &str, value: u32) -> windows::core::Result<()> {
        unsafe {
            let key_name = wide(KEY);
            let mut key = HKEY::default();
            RegCreateKeyExW(
                HKEY_CURRENT_USER,
                PCWSTR(key_name.as_ptr()),
                None,
                None,
                REG_OPTION_NON_VOLATILE,
                KEY_WRITE,
                None,
                &mut key,
                None,
            )
            .ok()?;
            let value_name = wide(name);
            let bytes = value.to_le_bytes();
            RegSetValueExW(key, PCWSTR(value_name.as_ptr()), None, REG_DWORD, Some(&bytes)).ok()
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn settings(hud_enabled: bool, hud_suppressed: u8) -> Settings {
        Settings { hud_enabled, hud_suppressed }
    }

    #[test]
    fn hud_disallowed_when_master_switch_is_off() {
        let s = settings(false, 0);
        assert!(!s.hud_allowed(protocol::Layers(0b1)));
    }

    #[test]
    fn hud_disallowed_when_the_displayed_layer_is_suppressed() {
        // Layer 2 active, layer 2 suppressed.
        let s = settings(true, 1 << 2);
        assert!(!s.hud_allowed(protocol::Layers(0b100)));
    }

    #[test]
    fn hud_allowed_when_a_different_layer_is_suppressed() {
        // Layer 2 active, layer 3 suppressed.
        let s = settings(true, 1 << 3);
        assert!(s.hud_allowed(protocol::Layers(0b100)));
    }

    #[test]
    fn hud_allowed_by_default_with_nothing_suppressed() {
        let s = settings(true, 0);
        assert!(s.hud_allowed(protocol::Layers(0b1)));
    }

    #[test]
    fn displayed_layer_of_a_zero_mask_is_layer_zero() {
        assert_eq!(Settings::displayed_layer(protocol::Layers(0)), 0);
    }

    #[test]
    fn displayed_layer_of_a_single_bit_is_that_bit() {
        assert_eq!(Settings::displayed_layer(protocol::Layers(0b1000)), 3);
    }

    #[test]
    fn displayed_layer_of_multiple_bits_is_the_highest() {
        assert_eq!(Settings::displayed_layer(protocol::Layers(0b1010)), 3);
    }
}
