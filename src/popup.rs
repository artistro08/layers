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
pub const WIDTH: f32 = 196.0;
pub const ROW_HEIGHT: f32 = 32.0;
pub const PADDING: f32 = 2.0;

/// How far the hover highlight is inset from the panel edges. Deliberately
/// independent of PADDING so tuning the panel's vertical breathing room does
/// not also resize the highlight.
///
/// Split per axis: Windows insets the highlight noticeably from the panel's
/// left and right edges, while keeping it tall enough that a row's contents
/// are not pressed against its top and bottom.
const HOVER_INSET_X: f32 = 5.0;
const HOVER_INSET_Y: f32 = 3.0;
pub const CORNER: f32 = 8.0;
/// Height of the rule between the Layer and Quit rows: 3px gap, 1px rule,
/// 3px gap.
pub const SEPARATOR_H: f32 = 7.0;
/// Transparent bleed around the panel that the drop shadow is drawn into.
/// The bitmap and window are the panel plus this margin on all sides.
pub const SHADOW_MARGIN: f32 = 16.0;
pub const HEIGHT: f32 = PADDING * 2.0 + ROW_HEIGHT * 3.0 + SEPARATOR_H;

#[derive(Clone, Copy, PartialEq, Debug)]
pub enum Row {
    Status,
    Layer,
    Quit,
}

/// Which row contains a y coordinate, in bitmap-space client pixels at 96
/// dpi (i.e. including the shadow margin, as the window's own client area
/// does).
pub fn row_at(y: f32) -> Option<Row> {
    let y = y - SHADOW_MARGIN;
    if y < PADDING {
        return None;
    }
    let rel = y - PADDING;
    if rel < ROW_HEIGHT {
        return Some(Row::Status);
    }
    if rel < ROW_HEIGHT * 2.0 {
        return Some(Row::Layer);
    }
    let quit_start = ROW_HEIGHT * 2.0 + SEPARATOR_H;
    if rel >= quit_start && rel < quit_start + ROW_HEIGHT {
        return Some(Row::Quit);
    }
    None
}

/// Clamps the popup into the work area so it never hangs off screen or under
/// the taskbar. Prefers opening above the cursor, as a taskbar flyout does.
///
/// `w`/`h` are the bitmap's (shadow-inclusive) dimensions in physical
/// pixels; `scale` converts `SHADOW_MARGIN` into that same space. The
/// clamping itself works in the visible panel's dimensions, then the
/// returned point is shifted back to the bitmap's top-left so the panel —
/// not the transparent bleed around it — lands where the math says.
pub fn place(cursor: POINT, work: RECT, w: i32, h: i32, scale: f32) -> POINT {
    let margin = (SHADOW_MARGIN * scale).round() as i32;
    let panel_w = w - margin * 2;
    let panel_h = h - margin * 2;
    let x = (cursor.x - panel_w / 2).clamp(work.left, (work.right - panel_w).max(work.left));
    let y = if cursor.y - panel_h - 12 >= work.top {
        cursor.y - panel_h - 12
    } else {
        (cursor.y + 12).min((work.bottom - panel_h).max(work.top))
    };
    POINT { x: x - margin, y: y - margin }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn work() -> RECT {
        RECT { left: 0, top: 0, right: 1920, bottom: 1040 }
    }

    // Bitmap dimensions at scale 1.0: panel plus shadow margin on all sides.
    fn bitmap_wh() -> (i32, i32) {
        (
            (WIDTH + SHADOW_MARGIN * 2.0) as i32,
            (HEIGHT + SHADOW_MARGIN * 2.0) as i32,
        )
    }

    #[test]
    fn hit_testing_maps_each_row_band() {
        assert_eq!(row_at(SHADOW_MARGIN + PADDING + 1.0), Some(Row::Status));
        assert_eq!(
            row_at(SHADOW_MARGIN + PADDING + ROW_HEIGHT + 1.0),
            Some(Row::Layer)
        );
        assert_eq!(
            row_at(SHADOW_MARGIN + PADDING + ROW_HEIGHT * 2.0 + SEPARATOR_H + 1.0),
            Some(Row::Quit)
        );
    }

    #[test]
    fn hit_testing_rejects_the_shadow_margin_above_the_panel() {
        assert_eq!(row_at(SHADOW_MARGIN - 1.0), None);
    }

    #[test]
    fn hit_testing_rejects_the_padding_above_the_first_row() {
        assert_eq!(row_at(SHADOW_MARGIN + PADDING - 1.0), None);
    }

    #[test]
    fn hit_testing_rejects_the_separator_gap_between_layer_and_quit() {
        let gap_mid = SHADOW_MARGIN + PADDING + ROW_HEIGHT * 2.0 + SEPARATOR_H / 2.0;
        assert_eq!(row_at(gap_mid), None);
    }

    #[test]
    fn hit_testing_rejects_the_padding_below_the_last_row() {
        assert_eq!(
            row_at(SHADOW_MARGIN + PADDING + ROW_HEIGHT * 3.0 + SEPARATOR_H + 1.0),
            None
        );
    }

    #[test]
    fn the_popup_opens_above_the_cursor_when_there_is_room() {
        let (w, h) = bitmap_wh();
        let p = place(POINT { x: 960, y: 1000 }, work(), w, h, 1.0);
        // The panel's bottom edge (bitmap y + margin + panel height) must
        // clear the cursor by the 12px gap.
        assert!(p.y + SHADOW_MARGIN as i32 + (HEIGHT as i32) <= 1000 - 12);
    }

    #[test]
    fn the_popup_drops_below_the_cursor_when_there_is_no_room_above() {
        let (w, h) = bitmap_wh();
        let p = place(POINT { x: 960, y: 5 }, work(), w, h, 1.0);
        // The panel's top edge (bitmap y + margin) must sit below the cursor.
        assert!(p.y + SHADOW_MARGIN as i32 > 5);
    }

    #[test]
    fn the_popup_never_hangs_off_the_right_edge() {
        let (w, h) = bitmap_wh();
        let p = place(POINT { x: 1918, y: 1000 }, work(), w, h, 1.0);
        // Panel right edge (bitmap x + margin + panel width) sits exactly at
        // the work area's right edge.
        assert_eq!(p.x + SHADOW_MARGIN as i32 + WIDTH as i32, 1920);
    }

    #[test]
    fn the_popup_never_hangs_off_the_left_edge() {
        let (w, h) = bitmap_wh();
        let p = place(POINT { x: 2, y: 1000 }, work(), w, h, 1.0);
        assert_eq!(p.x + SHADOW_MARGIN as i32, 0);
    }

    #[test]
    fn the_popup_stays_inside_a_work_area_that_does_not_start_at_the_origin() {
        let (w, h) = bitmap_wh();
        let work = RECT { left: 1920, top: 0, right: 3840, bottom: 1040 };
        let p = place(POINT { x: 1921, y: 1000 }, work, w, h, 1.0);
        assert_eq!(p.x + SHADOW_MARGIN as i32, 1920);
    }

    #[test]
    fn the_three_rows_plus_padding_and_separator_account_for_the_full_height() {
        assert_eq!(HEIGHT, PADDING * 2.0 + ROW_HEIGHT * 3.0 + SEPARATOR_H);
    }

    /// `row_rect` (painted geometry) and `row_at` (hit testing) each encode
    /// the row order independently. This round trip is the guard against
    /// them silently disagreeing if `Row` or `frame_index` is reordered.
    #[test]
    fn row_at_of_row_rect_midpoint_returns_the_same_row() {
        for row in [Row::Status, Row::Layer, Row::Quit] {
            let rect = row_rect(row, 1.0);
            let mid_y = (rect.top + rect.bottom) / 2.0;
            assert_eq!(row_at(mid_y), Some(row));
        }
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

/// The rule between grouped rows.
pub fn separator(dark: bool) -> D2D1_COLOR_F {
    let v = if dark { 1.0 } else { 0.0 };
    D2D1_COLOR_F { r: v, g: v, b: v, a: 0.10 }
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
use crate::icon;
use crate::render::Renderer;
use crate::theme;
use std::cell::RefCell;
use windows::core::{w, Result, PCWSTR};
use windows::Win32::Foundation::{COLORREF, HINSTANCE, HWND, LPARAM, LRESULT, SIZE, WPARAM};
use crate::geometry::Segment;
use windows::Win32::Graphics::Direct2D::Common::{
    D2D1_BEZIER_SEGMENT, D2D1_FIGURE_BEGIN_FILLED,
    D2D1_FIGURE_END_CLOSED, D2D1_FILL_MODE_WINDING, D2D_RECT_F,
};
use windows::Win32::Graphics::Direct2D::{
    ID2D1Brush, ID2D1RenderTarget, D2D1_ELLIPSE, D2D1_ROUNDED_RECT,
};
use windows::Win32::Graphics::DirectWrite::{
    IDWriteTextFormat, DWRITE_FONT_STRETCH_NORMAL, DWRITE_FONT_STYLE_NORMAL,
    DWRITE_FONT_WEIGHT, DWRITE_FONT_WEIGHT_NORMAL,
    DWRITE_PARAGRAPH_ALIGNMENT_CENTER, DWRITE_TEXT_ALIGNMENT,
    DWRITE_TEXT_ALIGNMENT_LEADING,
};
use windows::Win32::Graphics::Gdi::{
    CreateCompatibleDC, CreateDIBSection, DeleteDC, DeleteObject, GetMonitorInfoW,
    MonitorFromPoint, SelectObject, AC_SRC_ALPHA, AC_SRC_OVER, BITMAPINFO, BITMAPINFOHEADER,
    BI_RGB, BLENDFUNCTION, DIB_RGB_COLORS, MONITORINFO, MONITOR_DEFAULTTONEAREST,
};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::Controls::WM_MOUSELEAVE;
use windows::Win32::UI::HiDpi::{GetDpiForMonitor, GetDpiForWindow, MDT_EFFECTIVE_DPI};
use windows::Win32::UI::Input::KeyboardAndMouse::{
    ReleaseCapture, SetCapture, TrackMouseEvent, TME_LEAVE, TRACKMOUSEEVENT, VK_ESCAPE,
};
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, GetCursorPos, LoadCursorW, PostMessageW, RegisterClassW,
    SetForegroundWindow, ShowWindow, UpdateLayeredWindow, IDC_ARROW, SW_HIDE, SW_SHOWNOACTIVATE,
    ULW_ALPHA, WM_ACTIVATEAPP, WM_KEYDOWN, WM_KILLFOCUS, WM_LBUTTONDOWN, WM_LBUTTONUP,
    WM_MOUSEMOVE, WNDCLASSW, WS_EX_LAYERED, WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW, WS_EX_TOPMOST,
    WS_POPUP,
};
use windows_numerics::Vector2;

const CLASS: PCWSTR = w!("LayersPopupWindow");
const FONT: PCWSTR = w!("Segoe UI Variable Text");
const LOCALE: PCWSTR = w!("en-us");

/// Row interior metrics, in logical pixels at 96 dpi.
const DOT: f32 = 8.0;
/// Left inset of the icon column.
const ICON_LEFT: f32 = 12.0;
/// Side length of the icon column's square.
const ICON_SIZE: f32 = 16.0;
/// Left inset of every row's text, clearing the icon column.
const TEXT_LEFT: f32 = 38.0;
/// The layers glyph reads smaller than the dot and the power symbol at the
/// same box size, so it gets a slightly larger one. Icons are centred on the
/// column rather than left-aligned, so differing sizes stay optically aligned.
const LAYER_ICON_SIZE: f32 = 19.0;
/// Right inset of the layer pill.
const TEXT_RIGHT: f32 = 16.0;


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
    /// Set when `show()` fell back to `SetCapture` because
    /// `SetForegroundWindow` was denied by the foreground lock, so
    /// `WM_LBUTTONDOWN` knows to dismiss on an outside click.
    captured: bool,
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
                    captured: false,
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
                None => place(cursor, work, w, h, scale),
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
                // kill-focus that dismisses it. WS_EX_NOACTIVATE does not
                // block this call — it is MSDN's own remedy for it — but
                // WM_TRAY arrives as a *posted* shell message, so the
                // foreground lock can still deny us activation. When it
                // does, SetForegroundWindow returns FALSE and
                // WM_ACTIVATEAPP/WM_KILLFOCUS/WM_KEYDOWN never fire, so we
                // fall back to SetCapture: any click outside the popup then
                // dismisses it via WM_LBUTTONDOWN below.
                //
                // This call happens while the caller's `APP.borrow_mut()`
                // (in main.rs) is still live. That is safe only because
                // main's wndproc handles no activation message — adding a
                // WM_ACTIVATE/WM_ACTIVATEAPP arm there that calls refresh()
                // would re-enter APP and panic.
                let became_foreground = SetForegroundWindow(self.hwnd).as_bool();
                if !became_foreground {
                    let _ = SetCapture(self.hwnd);
                    POPUP.with(|p| {
                        if let Some(i) = p.borrow_mut().as_mut() {
                            i.captured = true;
                        }
                    });
                }
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
        // Without an hCursor, DefWindowProcW's WM_SETCURSOR never sets an
        // arrow, so the pointer keeps whatever shape the previously-hovered
        // window left it in.
        let cursor = LoadCursorW(None, IDC_ARROW).unwrap_or_default();
        let wc = WNDCLASSW {
            lpfnWndProc: Some(wndproc),
            hInstance: instance,
            lpszClassName: CLASS,
            hCursor: cursor,
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
            ((WIDTH + SHADOW_MARGIN * 2.0) * scale) as i32,
            ((HEIGHT + SHADOW_MARGIN * 2.0) * scale) as i32,
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
        // Must run on every dismissal path (including Quit), or a
        // foreground-lock fallback capture from `show()` leaves the mouse
        // captured process-wide.
        let _ = ReleaseCapture();
    }
    POPUP.with(|p| {
        if let Ok(mut b) = p.try_borrow_mut() {
            if let Some(i) = b.as_mut() {
                i.visible = false;
                i.hovered = None;
                i.captured = false;
            }
        }
    });
}

fn row_rect(row: Row, scale: f32) -> D2D_RECT_F {
    let index = frame_index(Some(row)) as f32 - 1.0;
    // Quit sits below the separator between it and Layer.
    let extra = if row == Row::Quit { SEPARATOR_H } else { 0.0 };
    let top = (SHADOW_MARGIN + PADDING + index * ROW_HEIGHT + extra) * scale;
    D2D_RECT_F {
        left: SHADOW_MARGIN * scale,
        top,
        right: (SHADOW_MARGIN + WIDTH) * scale,
        bottom: top + ROW_HEIGHT * scale,
    }
}

pub(crate) fn inset(r: D2D_RECT_F, by: f32) -> D2D_RECT_F {
    inset_xy(r, by, by)
}

fn inset_xy(r: D2D_RECT_F, x: f32, y: f32) -> D2D_RECT_F {
    D2D_RECT_F {
        left: r.left + x,
        top: r.top + y,
        right: r.right - x,
        bottom: r.bottom - y,
    }
}

/// The icon column's square within a row, vertically centred.
fn icon_rect(row: D2D_RECT_F, scale: f32) -> D2D_RECT_F {
    icon_rect_sized(row, scale, ICON_SIZE)
}

/// Centred on the icon column's midpoint, so an icon drawn at a different
/// size still lines up with its neighbours.
fn icon_rect_sized(row: D2D_RECT_F, scale: f32, size: f32) -> D2D_RECT_F {
    let middle = (row.top + row.bottom) / 2.0;
    let center_x = row.left + (ICON_LEFT + ICON_SIZE / 2.0) * scale;
    let half = size / 2.0 * scale;
    D2D_RECT_F {
        left: center_x - half,
        top: middle - half,
        right: center_x + half,
        bottom: middle + half,
    }
}

/// Builds and fills a path geometry for a vendored glyph, scaled from its
/// authored view box to `dest`. Mirrors `icon.rs::draw_glyph`, generalized
/// to an arbitrary destination rect instead of a fixed origin box.
pub(crate) fn draw_icon(
    rt: &ID2D1RenderTarget,
    path: &str,
    viewbox: f32,
    dest: D2D_RECT_F,
    brush: &ID2D1Brush,
) -> Result<()> {
    unsafe {
        let factory = rt.GetFactory()?;
        let geo = factory.CreatePathGeometry()?;
        let sink = geo.Open()?;
        sink.SetFillMode(D2D1_FILL_MODE_WINDING);

        let scale = (dest.right - dest.left) / viewbox;
        let pt = |p: crate::geometry::Point| Vector2 {
            X: dest.left + p.x * scale,
            Y: dest.top + p.y * scale,
        };

        // The path is a compile-time constant already covered by
        // geometry.rs's own parse tests, so a failure here is a build-time
        // mistake, not runtime input.
        let figures = crate::geometry::parse_path(path).expect("vendored glyph is valid");
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

        rt.FillGeometry(&geo, brush, None);
        Ok(())
    }
}

/// Approximates a soft drop shadow with concentric rounded rects of ramping
/// alpha, since this app has no D3D11 device to run a real D2D blur effect
/// through. Offset down slightly for a light-from-above look. Drawn before
/// the panel so the panel's own fill covers the inner steps.
/// Approximates a blur with concentric rounded rects, since a real D2D
/// blur effect would need an ID2D1DeviceContext and therefore a D3D11
/// device. `corner` is the panel's own radius; the shadow rings grow
/// outward from it.
pub(crate) fn draw_shadow(
    rt: &ID2D1RenderTarget,
    w: f32,
    h: f32,
    s: f32,
    corner: f32,
) -> Result<()> {
    const STEPS: i32 = 12;
    let offset_y = 2.0 * s;
    for i in 0..STEPS {
        let inset_amt = i as f32 * SHADOW_MARGIN * s / STEPS as f32;
        let remaining = SHADOW_MARGIN * s - inset_amt;
        let rect = D2D_RECT_F {
            left: inset_amt,
            top: inset_amt + offset_y,
            right: w - inset_amt,
            bottom: h - inset_amt + offset_y,
        };
        let t = i as f32 / (STEPS - 1) as f32;
        let color = D2D1_COLOR_F { r: 0.0, g: 0.0, b: 0.0, a: 0.06 * t * t };
        unsafe {
            let brush = rt.CreateSolidColorBrush(&color, None)?;
            rt.FillRoundedRectangle(
                &D2D1_ROUNDED_RECT {
                    rect,
                    radiusX: corner * s + remaining,
                    radiusY: corner * s + remaining,
                },
                &brush,
            );
        }
    }
    Ok(())
}

pub(crate) fn format(
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

pub(crate) fn draw_text(
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
    let w = ((WIDTH + SHADOW_MARGIN * 2.0) * scale).round() as i32;
    let h = ((HEIGHT + SHADOW_MARGIN * 2.0) * scale).round() as i32;
    let dark = theme::dark_apps();
    let s = scale;

    let bgra = r.render_bgra(w as u32, h as u32, |rt| unsafe {
        draw_shadow(rt, w as f32, h as f32, s, CORNER)?;

        // Surface and border. Half a stroke of inset keeps the border inside
        // the panel's own edge instead of half-clipped by it. The panel
        // itself is inset from the bitmap edge by the shadow margin.
        let panel = D2D1_ROUNDED_RECT {
            rect: inset(
                D2D_RECT_F {
                    left: SHADOW_MARGIN * s,
                    top: SHADOW_MARGIN * s,
                    right: w as f32 - SHADOW_MARGIN * s,
                    bottom: h as f32 - SHADOW_MARGIN * s,
                },
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
                rect: inset_xy(row_rect(row, s), HOVER_INSET_X * s, HOVER_INSET_Y * s),
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
                point: Vector2 { X: row.left + (ICON_LEFT + ICON_SIZE / 2.0) * s, Y: middle },
                radiusX: DOT / 2.0 * s,
                radiusY: DOT / 2.0 * s,
            },
            &dot,
        );
        let body = D2D_RECT_F { left: row.left + TEXT_LEFT * s, right: row.right - TEXT_RIGHT * s, ..row };
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
        draw_icon(
            rt,
            icon::GLYPH_PATH,
            icon::GLYPH_VIEWBOX,
            icon_rect_sized(row, s, LAYER_ICON_SIZE),
            &ink,
        )?;
        let f = format(r, 14.0 * s, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_TEXT_ALIGNMENT_LEADING)?;
        draw_text(
            rt,
            &state.layers.label(),
            &f,
            D2D_RECT_F {
                left: row.left + TEXT_LEFT * s,
                right: row.right - TEXT_RIGHT * s,
                ..row
            },
            &ink,
        );
        // Separator between the Layer and Quit rows.
        let sep_y = row.bottom + SEPARATOR_H / 2.0 * s;
        let sep_brush = rt.CreateSolidColorBrush(&separator(dark), None)?;
        // Inset to the same edges as the hover highlight, so the rule lines
        // up with the fill rather than floating wider or narrower than it.
        rt.DrawLine(
            Vector2 { X: row.left + HOVER_INSET_X * s, Y: sep_y },
            Vector2 { X: row.right - HOVER_INSET_X * s, Y: sep_y },
            &sep_brush,
            s,
            None,
        );

        // Quit row.
        let row = row_rect(Row::Quit, s);
        draw_icon(rt, icon::POWER_PATH, icon::POWER_VIEWBOX, icon_rect(row, s), &ink)?;
        let f = format(r, 14.0 * s, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_TEXT_ALIGNMENT_LEADING)?;
        draw_text(
            rt,
            "Quit",
            &f,
            D2D_RECT_F {
                left: row.left + TEXT_LEFT * s,
                right: row.right - TEXT_RIGHT * s,
                ..row
            },
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
            // TME_LEAVE is one-shot: re-arm it on every move so a genuine
            // leave still posts WM_MOUSELEAVE later.
            let mut tme = TRACKMOUSEEVENT {
                cbSize: std::mem::size_of::<TRACKMOUSEEVENT>() as u32,
                dwFlags: TME_LEAVE,
                hwndTrack: hwnd,
                dwHoverTime: 0,
            };
            let _ = unsafe { TrackMouseEvent(&mut tme) };

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
        WM_MOUSELEAVE => {
            POPUP.with(|p| {
                let Ok(mut b) = p.try_borrow_mut() else { return };
                let Some(i) = b.as_mut() else { return };
                if i.hovered.is_some() {
                    i.hovered = None;
                    let (w, h) = i.size;
                    let _ = push(hwnd, &i.frames[frame_index(None)], w, h, i.pos);
                }
            });
            LRESULT(0)
        }
        WM_LBUTTONDOWN => {
            // Fallback dismissal for when `show()` could not get
            // foreground rights (see the comment in `show`): a click
            // outside the popup's own client area dismisses it.
            let x = (lp.0 & 0xFFFF) as u16 as i16 as f32;
            let y = ((lp.0 >> 16) & 0xFFFF) as u16 as i16 as f32;
            let outside = POPUP.with(|p| {
                let Ok(b) = p.try_borrow() else { return false };
                let Some(i) = b.as_ref() else { return false };
                i.captured
                    && (x < 0.0 || y < 0.0 || x >= i.size.0 as f32 || y >= i.size.1 as f32)
            });
            if outside {
                dismiss(hwnd);
                LRESULT(0)
            } else {
                unsafe { DefWindowProcW(hwnd, msg, wp, lp) }
            }
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
