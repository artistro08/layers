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

use crate::device;
use crate::render::Renderer;
use crate::theme;
use std::cell::RefCell;
use windows::core::{w, Result, PCWSTR};
use windows::Win32::Foundation::{COLORREF, HINSTANCE, HWND, LPARAM, LRESULT, SIZE, WPARAM};
use windows::Win32::Graphics::Direct2D::Common::{D2D1_COLOR_F as Color, D2D_RECT_F};
use windows::Win32::Graphics::Direct2D::{
    ID2D1Brush, ID2D1RenderTarget, D2D1_ELLIPSE, D2D1_ROUNDED_RECT,
};
use windows::Win32::Graphics::DirectWrite::{
    IDWriteTextFormat, DWRITE_FONT_STRETCH_NORMAL, DWRITE_FONT_STYLE_NORMAL,
    DWRITE_FONT_WEIGHT, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_FONT_WEIGHT_SEMI_BOLD,
    DWRITE_PARAGRAPH_ALIGNMENT_CENTER, DWRITE_TEXT_ALIGNMENT, DWRITE_TEXT_ALIGNMENT_CENTER,
    DWRITE_TEXT_ALIGNMENT_LEADING, DWRITE_TEXT_ALIGNMENT_TRAILING,
};
use windows::Win32::Graphics::Gdi::{
    CreateCompatibleDC, CreateDIBSection, DeleteDC, DeleteObject, GetMonitorInfoW,
    MonitorFromPoint, SelectObject, AC_SRC_ALPHA, AC_SRC_OVER, BITMAPINFO, BITMAPINFOHEADER,
    BI_RGB, BLENDFUNCTION, DIB_RGB_COLORS, MONITORINFO, MONITOR_DEFAULTTONEAREST,
};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::HiDpi::{GetDpiForMonitor, GetDpiForWindow, MDT_EFFECTIVE_DPI};
use windows::Win32::UI::Input::KeyboardAndMouse::VK_ESCAPE;
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, GetCursorPos, PostMessageW,
    RegisterClassW, SetForegroundWindow, ShowWindow, UpdateLayeredWindow, SW_HIDE,
    SW_SHOWNOACTIVATE, ULW_ALPHA, WM_ACTIVATEAPP, WM_KEYDOWN, WM_KILLFOCUS, WM_LBUTTONUP,
    WM_MOUSEMOVE, WNDCLASSW, WS_EX_LAYERED, WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW,
    WS_EX_TOPMOST, WS_POPUP,
};
use windows_numerics::Vector2;

const CLASS: PCWSTR = w!("LayersPopupWindow");
const FONT: PCWSTR = w!("Segoe UI Variable Text");
const LOCALE: PCWSTR = w!("en-us");

/// Row interior metrics, in logical pixels at 96 dpi.
const DOT: f32 = 8.0;
/// Left inset of every row's text, clearing the status dot.
const TEXT_LEFT: f32 = 28.0;
/// Right inset of the layer pill.
const TEXT_RIGHT: f32 = 16.0;
const PILL_W: f32 = 28.0;
const PILL_H: f32 = 22.0;

const WHITE: Color = Color { r: 1.0, g: 1.0, b: 1.0, a: 1.0 };

/// Popup state the window procedure needs. Kept out of `main.rs`'s `APP` so
/// the popup's messages, which arrive on the same UI thread, can never
/// re-enter an `APP` borrow.
struct Inner {
    owner: HWND,
    visible: bool,
    hovered: Option<Row>,
    scale: f32,
    pos: POINT,
    size: (i32, i32),
    /// One pre-rendered frame per hover state, indexed by [`frame_index`], so
    /// a hover repaint needs no renderer inside the window procedure.
    frames: [Vec<u8>; 4],
}

thread_local! {
    static POPUP: RefCell<Option<Inner>> = const { RefCell::new(None) };
}

fn frame_index(hovered: Option<Row>) -> usize {
    match hovered {
        None => 0,
        Some(Row::Status) => 1,
        Some(Row::Layer) => 2,
        Some(Row::Quit) => 3,
    }
}

pub struct Popup {
    hwnd: HWND,
}

impl Popup {
    /// Creates the (hidden) layered window owned by `owner`, so it is
    /// destroyed with it.
    pub fn new(owner: HWND) -> Result<Popup> {
        unsafe {
            let instance: HINSTANCE = GetModuleHandleW(None)?.into();
            register_class(instance);
            let hwnd = create(instance, owner)?;
            POPUP.with(|p| {
                *p.borrow_mut() = Some(Inner {
                    owner,
                    visible: false,
                    hovered: None,
                    scale: 1.0,
                    pos: POINT::default(),
                    size: (0, 0),
                    frames: Default::default(),
                });
            });
            Ok(Popup { hwnd })
        }
    }

    pub fn is_visible(&self) -> bool {
        POPUP.with(|p| {
            p.try_borrow()
                .ok()
                .and_then(|b| b.as_ref().map(|i| i.visible))
                .unwrap_or(false)
        })
    }

    /// Paints every hover frame for `state`, places the window on the monitor
    /// under the cursor and shows it. Called again while it is already up it
    /// repaints in place, keeping its position and hovered row, so a layer
    /// change does not yank the window over to the pointer.
    pub fn show(&mut self, r: &Renderer, state: device::State) -> Result<()> {
        unsafe {
            let open = POPUP.with(|p| {
                let b = p.try_borrow().ok()?;
                let i = b.as_ref()?;
                i.visible.then_some((i.pos, i.scale, i.hovered))
            });

            let mut cursor = POINT::default();
            let _ = GetCursorPos(&mut cursor);
            let monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
            let mut mi = MONITORINFO {
                cbSize: std::mem::size_of::<MONITORINFO>() as u32,
                ..Default::default()
            };
            let work = if GetMonitorInfoW(monitor, &mut mi).as_bool() {
                mi.rcWork
            } else {
                RECT { left: 0, top: 0, right: WIDTH as i32, bottom: HEIGHT as i32 }
            };
            let mut dx = 96u32;
            let mut dy = 96u32;
            let cursor_scale =
                match GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, &mut dx, &mut dy) {
                    Ok(()) => dx.max(96) as f32 / 96.0,
                    Err(_) => 1.0,
                };
            let (scale, hovered) = match open {
                Some((_, scale, hovered)) => (scale, hovered),
                None => (cursor_scale, None),
            };

            let frames = [
                paint(r, state, None, scale)?,
                paint(r, state, Some(Row::Status), scale)?,
                paint(r, state, Some(Row::Layer), scale)?,
                paint(r, state, Some(Row::Quit), scale)?,
            ];
            let (w, h) = (frames[0].1, frames[0].2);
            let pos = match open {
                Some((pos, _, _)) => pos,
                None => place(cursor, work, w, h),
            };

            // The borrow ends before ShowWindow/SetForegroundWindow, which
            // dispatch messages straight back into `wndproc`.
            POPUP.with(|p| {
                if let Some(i) = p.borrow_mut().as_mut() {
                    i.frames = frames.map(|f| f.0);
                    i.scale = scale;
                    i.pos = pos;
                    i.size = (w, h);
                    i.hovered = hovered;
                    i.visible = true;
                    let _ = push(self.hwnd, &i.frames[frame_index(hovered)], w, h, pos);
                }
            });

            if open.is_none() {
                let _ = ShowWindow(self.hwnd, SW_SHOWNOACTIVATE);
                // Without foreground ownership the popup never receives the
                // kill-focus that dismisses it.
                let _ = SetForegroundWindow(self.hwnd);
            }
            Ok(())
        }
    }

    pub fn hide(&mut self) {
        dismiss(self.hwnd);
    }
}

fn register_class(instance: HINSTANCE) {
    static ONCE: std::sync::Once = std::sync::Once::new();
    ONCE.call_once(|| unsafe {
        let wc = WNDCLASSW {
            lpfnWndProc: Some(wndproc),
            hInstance: instance,
            lpszClassName: CLASS,
            ..Default::default()
        };
        RegisterClassW(&wc);
    });
}

fn create(instance: HINSTANCE, owner: HWND) -> Result<HWND> {
    unsafe {
        let scale = GetDpiForWindow(owner).max(96) as f32 / 96.0;
        CreateWindowExW(
            WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE,
            CLASS,
            CLASS,
            WS_POPUP,
            0,
            0,
            (WIDTH * scale) as i32,
            (HEIGHT * scale) as i32,
            Some(owner),
            None,
            Some(instance),
            None,
        )
    }
}

/// Hides the window first, then clears the state: `ShowWindow` can dispatch
/// activation messages back into `wndproc`, which takes the same borrow.
fn dismiss(hwnd: HWND) {
    unsafe {
        let _ = ShowWindow(hwnd, SW_HIDE);
    }
    POPUP.with(|p| {
        if let Ok(mut b) = p.try_borrow_mut() {
            if let Some(i) = b.as_mut() {
                i.visible = false;
                i.hovered = None;
            }
        }
    });
}

fn row_rect(row: Row, scale: f32) -> D2D_RECT_F {
    let index = frame_index(Some(row)) as f32 - 1.0;
    let top = (PADDING + index * ROW_HEIGHT) * scale;
    D2D_RECT_F { left: 0.0, top, right: WIDTH * scale, bottom: top + ROW_HEIGHT * scale }
}

fn inset(r: D2D_RECT_F, by: f32) -> D2D_RECT_F {
    D2D_RECT_F {
        left: r.left + by,
        top: r.top + by,
        right: r.right - by,
        bottom: r.bottom - by,
    }
}

fn format(
    r: &Renderer,
    size: f32,
    weight: DWRITE_FONT_WEIGHT,
    align: DWRITE_TEXT_ALIGNMENT,
) -> Result<IDWriteTextFormat> {
    unsafe {
        let f = r.dwrite().CreateTextFormat(
            FONT,
            None,
            weight,
            DWRITE_FONT_STYLE_NORMAL,
            DWRITE_FONT_STRETCH_NORMAL,
            size,
            LOCALE,
        )?;
        f.SetTextAlignment(align)?;
        f.SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER)?;
        Ok(f)
    }
}

fn draw_text(
    rt: &ID2D1RenderTarget,
    s: &str,
    f: &IDWriteTextFormat,
    rect: D2D_RECT_F,
    brush: &ID2D1Brush,
) {
    let text: Vec<u16> = s.encode_utf16().collect();
    unsafe {
        rt.DrawText(&text, f, &rect, brush, Default::default(), Default::default());
    }
}

/// Renders one frame of the popup. Returns premultiplied BGRA plus its pixel
/// dimensions.
fn paint(
    r: &Renderer,
    state: device::State,
    hovered: Option<Row>,
    scale: f32,
) -> Result<(Vec<u8>, i32, i32)> {
    let w = (WIDTH * scale).round() as i32;
    let h = (HEIGHT * scale).round() as i32;
    let dark = theme::dark_apps();
    let s = scale;

    let bgra = r.render_bgra(w as u32, h as u32, |rt| unsafe {
        // Surface and border. Half a stroke of inset keeps the border inside
        // the bitmap instead of half-clipped by its edge.
        let panel = D2D1_ROUNDED_RECT {
            rect: inset(
                D2D_RECT_F { left: 0.0, top: 0.0, right: w as f32, bottom: h as f32 },
                s / 2.0,
            ),
            radiusX: CORNER * s,
            radiusY: CORNER * s,
        };
        let fill = rt.CreateSolidColorBrush(&surface(dark), None)?;
        rt.FillRoundedRectangle(&panel, &fill);
        let edge = rt.CreateSolidColorBrush(&border(dark), None)?;
        rt.DrawRoundedRectangle(&panel, &edge, s, None);

        if let Some(row) = hovered {
            let rr = D2D1_ROUNDED_RECT {
                rect: inset(row_rect(row, s), PADDING / 2.0 * s),
                radiusX: 4.0 * s,
                radiusY: 4.0 * s,
            };
            let brush = rt.CreateSolidColorBrush(&hover(dark), None)?;
            rt.FillRoundedRectangle(&rr, &brush);
        }

        let ink = rt.CreateSolidColorBrush(&text(dark), None)?;

        // Status row.
        let row = row_rect(Row::Status, s);
        let middle = (row.top + row.bottom) / 2.0;
        let dot = rt.CreateSolidColorBrush(&status_dot(state.status), None)?;
        rt.FillEllipse(
            &D2D1_ELLIPSE {
                point: Vector2 { X: PADDING * 2.0 * s, Y: middle },
                radiusX: DOT / 2.0 * s,
                radiusY: DOT / 2.0 * s,
            },
            &dot,
        );
        let body =
            D2D_RECT_F { left: TEXT_LEFT * s, right: (WIDTH - TEXT_RIGHT) * s, ..row };
        match status_detail(state.status) {
            None => {
                let f =
                    format(r, 14.0 * s, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_TEXT_ALIGNMENT_LEADING)?;
                draw_text(rt, status_label(state.status), &f, body, &ink);
            }
            Some(detail) => {
                let f =
                    format(r, 13.0 * s, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_TEXT_ALIGNMENT_LEADING)?;
                draw_text(
                    rt,
                    status_label(state.status),
                    &f,
                    D2D_RECT_F { bottom: middle, ..body },
                    &ink,
                );
                let mut faint = text(dark);
                faint.a *= 0.6;
                let brush = rt.CreateSolidColorBrush(&faint, None)?;
                let f =
                    format(r, 11.0 * s, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_TEXT_ALIGNMENT_LEADING)?;
                draw_text(rt, detail, &f, D2D_RECT_F { top: middle, ..body }, &brush);
            }
        }

        // Layer row.
        let row = row_rect(Row::Layer, s);
        let middle = (row.top + row.bottom) / 2.0;
        let f = format(r, 14.0 * s, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_TEXT_ALIGNMENT_LEADING)?;
        draw_text(
            rt,
            &state.layers.label(),
            &f,
            D2D_RECT_F {
                left: TEXT_LEFT * s,
                right: (WIDTH - TEXT_RIGHT - PILL_W - PADDING) * s,
                ..row
            },
            &ink,
        );
        let badge = match state.status {
            device::Status::Connected => state.layers.badge(),
            device::Status::Disconnected
            | device::Status::NoSlot
            | device::Status::VersionMismatch => None,
        };
        match badge {
            Some(n) => {
                let pill = D2D_RECT_F {
                    left: (WIDTH - TEXT_RIGHT - PILL_W) * s,
                    right: (WIDTH - TEXT_RIGHT) * s,
                    top: middle - PILL_H / 2.0 * s,
                    bottom: middle + PILL_H / 2.0 * s,
                };
                let (ar, ag, ab) = theme::accent();
                let accent = Color {
                    r: ar as f32 / 255.0,
                    g: ag as f32 / 255.0,
                    b: ab as f32 / 255.0,
                    a: 1.0,
                };
                let brush = rt.CreateSolidColorBrush(&accent, None)?;
                rt.FillRoundedRectangle(
                    &D2D1_ROUNDED_RECT {
                        rect: pill,
                        radiusX: PILL_H / 2.0 * s,
                        radiusY: PILL_H / 2.0 * s,
                    },
                    &brush,
                );
                let white = rt.CreateSolidColorBrush(&WHITE, None)?;
                let f = format(
                    r,
                    12.0 * s,
                    DWRITE_FONT_WEIGHT_SEMI_BOLD,
                    DWRITE_TEXT_ALIGNMENT_CENTER,
                )?;
                draw_text(rt, &n.to_string(), &f, pill, &white);
            }
            None => {
                let mut faint = text(dark);
                faint.a *= 0.4;
                let brush = rt.CreateSolidColorBrush(&faint, None)?;
                let f = format(
                    r,
                    14.0 * s,
                    DWRITE_FONT_WEIGHT_NORMAL,
                    DWRITE_TEXT_ALIGNMENT_TRAILING,
                )?;
                draw_text(
                    rt,
                    "\u{2014}",
                    &f,
                    D2D_RECT_F {
                        left: TEXT_LEFT * s,
                        right: (WIDTH - TEXT_RIGHT) * s,
                        ..row
                    },
                    &brush,
                );
            }
        }

        // Quit row.
        let row = row_rect(Row::Quit, s);
        let f = format(r, 14.0 * s, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_TEXT_ALIGNMENT_LEADING)?;
        draw_text(
            rt,
            "Quit",
            &f,
            D2D_RECT_F { left: TEXT_LEFT * s, right: (WIDTH - TEXT_RIGHT) * s, ..row },
            &ink,
        );
        Ok(())
    })?;

    Ok((bgra, w, h))
}

/// Blits one frame onto the layered window, moving and sizing it to match.
fn push(hwnd: HWND, bgra: &[u8], w: i32, h: i32, pos: POINT) -> Result<()> {
    unsafe {
        let mut bits: *mut std::ffi::c_void = std::ptr::null_mut();
        let info = BITMAPINFO {
            bmiHeader: BITMAPINFOHEADER {
                biSize: std::mem::size_of::<BITMAPINFOHEADER>() as u32,
                biWidth: w,
                // Negative height makes the DIB top-down, matching our buffer.
                biHeight: -h,
                biPlanes: 1,
                biBitCount: 32,
                biCompression: BI_RGB.0,
                ..Default::default()
            },
            ..Default::default()
        };
        let dib = CreateDIBSection(None, &info, DIB_RGB_COLORS, &mut bits, None, 0)?;
        std::ptr::copy_nonoverlapping(bgra.as_ptr(), bits as *mut u8, bgra.len());

        let dc = CreateCompatibleDC(None);
        let old = SelectObject(dc, dib.into());
        let blend = BLENDFUNCTION {
            BlendOp: AC_SRC_OVER as u8,
            BlendFlags: 0,
            SourceConstantAlpha: 255,
            AlphaFormat: AC_SRC_ALPHA as u8,
        };
        let size = SIZE { cx: w, cy: h };
        let src = POINT { x: 0, y: 0 };
        let result = UpdateLayeredWindow(
            hwnd,
            None,
            Some(&pos),
            Some(&size),
            Some(dc),
            Some(&src),
            COLORREF(0),
            Some(&blend),
            ULW_ALPHA,
        );

        SelectObject(dc, old);
        let _ = DeleteDC(dc);
        let _ = DeleteObject(dib.into());
        result
    }
}

unsafe extern "system" fn wndproc(hwnd: HWND, msg: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    match msg {
        WM_MOUSEMOVE => {
            let y = ((lp.0 >> 16) & 0xFFFF) as u16 as i16 as f32;
            POPUP.with(|p| {
                let Ok(mut b) = p.try_borrow_mut() else { return };
                let Some(i) = b.as_mut() else { return };
                let row = row_at(y / i.scale);
                if row != i.hovered {
                    i.hovered = row;
                    let (w, h) = i.size;
                    let _ = push(hwnd, &i.frames[frame_index(row)], w, h, i.pos);
                }
            });
            LRESULT(0)
        }
        WM_LBUTTONUP => {
            // The borrow closes before anything that can re-enter here.
            let quit = POPUP.with(|p| {
                let Ok(b) = p.try_borrow() else { return None };
                let i = b.as_ref()?;
                (i.hovered == Some(Row::Quit)).then_some(i.owner)
            });
            if let Some(owner) = quit {
                let _ = unsafe { PostMessageW(Some(owner), QUIT_CLICKED, WPARAM(0), LPARAM(0)) };
                dismiss(hwnd);
            }
            LRESULT(0)
        }
        WM_KILLFOCUS => {
            dismiss(hwnd);
            LRESULT(0)
        }
        WM_ACTIVATEAPP if wp.0 == 0 => {
            dismiss(hwnd);
            LRESULT(0)
        }
        WM_KEYDOWN if wp.0 == VK_ESCAPE.0 as usize => {
            dismiss(hwnd);
            LRESULT(0)
        }
        _ => unsafe { DefWindowProcW(hwnd, msg, wp, lp) },
    }
}
