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

    /// Re-adds the icon after Explorer restarts and broadcasts
    /// `TaskbarCreated`. Reuses the existing `NOTIFYICONDATAW` rather than
    /// constructing a second `Tray`.
    pub fn readd(&mut self) {
        unsafe {
            let _ = Shell_NotifyIconW(NIM_ADD, &mut self.data);
        }
        self.added = true;
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
