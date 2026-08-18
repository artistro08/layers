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
