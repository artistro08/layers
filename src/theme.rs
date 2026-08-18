//! Theme, read from the registry and watched for changes.
//!
//! Windows tracks the taskbar theme and the app theme separately, so this
//! module does too.

use windows::core::PCWSTR;
use windows::Win32::Foundation::HWND;
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
