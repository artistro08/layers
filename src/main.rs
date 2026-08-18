// No console window.
#![windows_subsystem = "windows"]

use layers::{device, icon, popup, protocol, render, theme, tray};
use std::cell::{Cell, RefCell};
use windows::core::{w, Result, PCWSTR};
use windows::Win32::Foundation::{ERROR_ALREADY_EXISTS, GetLastError, HWND, LPARAM, LRESULT, WPARAM};
use windows::Win32::System::Com::{CoInitializeEx, COINIT_APARTMENTTHREADED};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::Threading::CreateMutexW;
use windows::Win32::UI::HiDpi::GetDpiForWindow;
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, DispatchMessageW, FindWindowW, GetMessageW, MessageBoxW,
    PostMessageW, PostQuitMessage, RegisterClassW, RegisterWindowMessageW, TranslateMessage,
    CW_USEDEFAULT, MB_ICONERROR, MSG, WINDOW_STYLE, WM_DESTROY, WM_DPICHANGED, WM_SETTINGCHANGE,
    WNDCLASSW, WS_EX_TOOLWINDOW,
};

struct App {
    renderer: render::Renderer,
    tray: tray::Tray,
    popup: popup::Popup,
    state: device::State,
    device: Option<device::Handle>,
}

thread_local! {
    static APP: RefCell<Option<App>> = const { RefCell::new(None) };
    /// The value `RegisterWindowMessageW(w!("TaskbarCreated"))` returned, so
    /// `wndproc` can recognize the broadcast when Explorer restarts. 0 means
    /// unregistered/failed.
    static TASKBAR_CREATED: Cell<u32> = const { Cell::new(0) };
}

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

fn main() -> Result<()> {
    if let Err(e) = run() {
        // The product is plug-and-play with no console; a startup failure
        // must not vanish silently.
        let text = wide(&e.to_string());
        unsafe {
            MessageBoxW(None, PCWSTR(text.as_ptr()), w!("Layers"), MB_ICONERROR);
        }
        return Err(e);
    }
    Ok(())
}

fn run() -> Result<()> {
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

        // A normal top-level window that is never shown (no WS_VISIBLE): it
        // gets DPI/monitor association and broadcast messages
        // (WM_SETTINGCHANGE, TaskbarCreated) that a HWND_MESSAGE window
        // never receives, but stays invisible and out of Alt-Tab
        // (WS_EX_TOOLWINDOW).
        let hwnd = CreateWindowExW(
            WS_EX_TOOLWINDOW,
            PCWSTR(class.as_ptr()),
            PCWSTR(class.as_ptr()),
            WINDOW_STYLE::default(),
            CW_USEDEFAULT,
            CW_USEDEFAULT,
            0,
            0,
            None,
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
                popup: popup::Popup::new(hwnd)?,
                state: device::State {
                    status: device::Status::Disconnected,
                    layers: protocol::Layers(1),
                },
                device: Some(handle),
            });
            Ok(())
        })?;

        TASKBAR_CREATED.with(|c| c.set(RegisterWindowMessageW(w!("TaskbarCreated"))));

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

/// DPI of the taskbar, which is what the tray icon is actually drawn for and
/// may differ from this (invisible, unparented) window's own DPI on a
/// mixed-DPI setup. Falls back to the window's own DPI, then to 96.
fn taskbar_dpi(hwnd: HWND) -> u32 {
    unsafe {
        let dpi = match FindWindowW(w!("Shell_TrayWnd"), None) {
            Ok(tray) => GetDpiForWindow(tray),
            Err(_) => GetDpiForWindow(hwnd),
        };
        dpi.max(96)
    }
}

/// Rebuilds the tray icon and tooltip from current state and theme.
fn refresh(hwnd: HWND) {
    APP.with(|a| {
        // refresh() can re-enter while this borrow is still live: Tray::set
        // below blocks in a cross-process SendMessage (NIM_MODIFY), and the
        // shell's WM_SETTINGCHANGE broadcast is dispatched to this thread's
        // wndproc while it's blocked, routing straight back into refresh().
        // A plain borrow_mut() would panic (abort, since this is an
        // extern "system" fn) on that reentrant call. Match popup.rs's
        // try_borrow style: bail, the outer call already has current state.
        let Ok(mut borrow) = a.try_borrow_mut() else { return };
        let Some(app) = borrow.as_mut() else { return };

        let dpi = taskbar_dpi(hwnd);
        // 16 at 100%, 20 at 125%, 24 at 150%, 32 at 200%. Rounded up to a
        // multiple of 4 to keep the tray icon size on a clean pixel grid.
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
    let taskbar_created = TASKBAR_CREATED.with(|c| c.get());
    if taskbar_created != 0 && msg == taskbar_created {
        // Explorer restarted: the icon is gone from a fresh taskbar and
        // needs to be re-added, not recreated (the mutex is still held).
        APP.with(|a| {
            // Same reentrancy hazard as refresh(): app.tray.readd() below
            // blocks in NIM_ADD's cross-process SendMessage, during which
            // a WM_SETTINGCHANGE broadcast can re-enter this wndproc and
            // try to borrow APP again.
            let Ok(mut borrow) = a.try_borrow_mut() else { return };
            if let Some(app) = borrow.as_mut() {
                app.tray.readd();
            }
        });
        refresh(hwnd);
        return LRESULT(0);
    }

    match msg {
        tray::WM_DEVICE => {
            APP.with(|a| {
                if let Some(app) = a.borrow_mut().as_mut() {
                    let packed = wp.0;
                    app.state.layers = protocol::Layers((packed & 0xFF) as u8);
                    app.state.status =
                        device::Status::try_from(((packed >> 8) & 0xFF) as u8)
                            .unwrap_or(device::Status::Disconnected);
                }
            });
            refresh(hwnd);
            // An open popup would otherwise show the previous layer.
            APP.with(|a| {
                if let Some(app) = a.borrow_mut().as_mut() {
                    if app.popup.is_visible() {
                        let state = app.state;
                        let _ = app.popup.show(&app.renderer, state);
                    }
                }
            });
            LRESULT(0)
        }
        tray::WM_TRAY => {
            // The shell packs the mouse message into the low word of lParam.
            let event = (lp.0 as u32) & 0xFFFF;
            if event == windows::Win32::UI::WindowsAndMessaging::WM_LBUTTONUP
                || event == windows::Win32::UI::WindowsAndMessaging::WM_RBUTTONUP
            {
                APP.with(|a| {
                    if let Some(app) = a.borrow_mut().as_mut() {
                        let state = app.state;
                        // popup::show() calls SetForegroundWindow while this
                        // APP.borrow_mut() is still live. Safe only because
                        // this wndproc handles no activation message; a
                        // WM_ACTIVATE/WM_ACTIVATEAPP arm here that called
                        // refresh() would re-enter APP and panic.
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
        tray::WM_THEME | WM_DPICHANGED | WM_SETTINGCHANGE => {
            refresh(hwnd);
            // An open popup would otherwise show stale colors/DPI through a
            // theme, accent, or DPI change.
            APP.with(|a| {
                // Same reentrancy hazard as refresh(): a nested
                // WM_SETTINGCHANGE can arrive while an outer call still
                // holds this borrow. Bail; the popup re-reads theme and
                // accent on its next paint.
                let Ok(mut borrow) = a.try_borrow_mut() else { return };
                if let Some(app) = borrow.as_mut() {
                    if app.popup.is_visible() {
                        let state = app.state;
                        let _ = app.popup.show(&app.renderer, state);
                    }
                }
            });
            LRESULT(0)
        }
        WM_DESTROY => {
            APP.with(|a| {
                // Same reentrancy hazard as refresh(): app.tray.remove()
                // below blocks in NIM_DELETE's cross-process SendMessage,
                // during which a WM_SETTINGCHANGE broadcast can re-enter
                // this wndproc and try to borrow APP again.
                let Ok(mut borrow) = a.try_borrow_mut() else { return };
                if let Some(mut app) = borrow.take() {
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
