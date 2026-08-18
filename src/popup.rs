//! The Fluent popup.
//!
//! A DirectComposition-backed window: content is painted onto a transparent
//! background and DWM supplies the acrylic backdrop, rounded corners and
//! shadow around it. DWMWA_SYSTEMBACKDROP_TYPE fills the entire window rect,
//! so it can't coexist with the old layered window's transparent shadow
//! margin — DWM owns the background now, and painting here is content only.

use windows::Win32::Foundation::{POINT, RECT};
use windows::Win32::UI::WindowsAndMessaging::WM_APP;

/// Posted to the owner window when the Quit row is clicked.
pub const QUIT_CLICKED: u32 = WM_APP + 4;

/// Logical layout in pixels at 96 dpi.
pub const WIDTH: f32 = 220.0;
pub const ROW_HEIGHT: f32 = 32.0;
pub const PADDING: f32 = 4.0;

/// How far the hover highlight is inset from the panel edges. Deliberately
/// independent of PADDING so tuning the panel's vertical breathing room does
/// not also resize the highlight.
const HOVER_INSET: f32 = 4.0;
/// Height of the rule between the Layer and Quit rows: 3px gap, 1px rule,
/// 3px gap.
pub const SEPARATOR_H: f32 = 7.0;
pub const HEIGHT: f32 = PADDING * 2.0 + ROW_HEIGHT * 3.0 + SEPARATOR_H;

#[derive(Clone, Copy, PartialEq, Debug)]
pub enum Row {
    Status,
    Layer,
    Quit,
}

/// Which row contains a y coordinate, in client pixels at 96 dpi. The window
/// rect is exactly the panel rect now, so this is just the panel's own
/// coordinate space.
pub fn row_at(y: f32) -> Option<Row> {
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
/// `w`/`h` are the panel's dimensions in physical pixels, already scaled by
/// the caller. Returns the panel's (and so the window's) top-left directly.
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

    // Panel dimensions at scale 1.0.
    fn panel_wh() -> (i32, i32) {
        (WIDTH as i32, HEIGHT as i32)
    }

    #[test]
    fn hit_testing_maps_each_row_band() {
        assert_eq!(row_at(PADDING + 1.0), Some(Row::Status));
        assert_eq!(row_at(PADDING + ROW_HEIGHT + 1.0), Some(Row::Layer));
        assert_eq!(
            row_at(PADDING + ROW_HEIGHT * 2.0 + SEPARATOR_H + 1.0),
            Some(Row::Quit)
        );
    }

    #[test]
    fn hit_testing_rejects_the_padding_above_the_first_row() {
        assert_eq!(row_at(PADDING - 1.0), None);
    }

    #[test]
    fn hit_testing_rejects_the_separator_gap_between_layer_and_quit() {
        let gap_mid = PADDING + ROW_HEIGHT * 2.0 + SEPARATOR_H / 2.0;
        assert_eq!(row_at(gap_mid), None);
    }

    #[test]
    fn hit_testing_rejects_the_padding_below_the_last_row() {
        assert_eq!(
            row_at(PADDING + ROW_HEIGHT * 3.0 + SEPARATOR_H + 1.0),
            None
        );
    }

    #[test]
    fn the_popup_opens_above_the_cursor_when_there_is_room() {
        let (w, h) = panel_wh();
        let p = place(POINT { x: 960, y: 1000 }, work(), w, h);
        // The panel's bottom edge must clear the cursor by the 12px gap.
        assert!(p.y + h <= 1000 - 12);
    }

    #[test]
    fn the_popup_drops_below_the_cursor_when_there_is_no_room_above() {
        let (w, h) = panel_wh();
        let p = place(POINT { x: 960, y: 5 }, work(), w, h);
        // The panel's top edge must sit below the cursor.
        assert!(p.y > 5);
    }

    #[test]
    fn the_popup_never_hangs_off_the_right_edge() {
        let (w, h) = panel_wh();
        let p = place(POINT { x: 1918, y: 1000 }, work(), w, h);
        assert_eq!(p.x + w, 1920);
    }

    #[test]
    fn the_popup_never_hangs_off_the_left_edge() {
        let (w, h) = panel_wh();
        let p = place(POINT { x: 2, y: 1000 }, work(), w, h);
        assert_eq!(p.x, 0);
    }

    #[test]
    fn the_popup_stays_inside_a_work_area_that_does_not_start_at_the_origin() {
        let (w, h) = panel_wh();
        let work = RECT { left: 1920, top: 0, right: 3840, bottom: 1040 };
        let p = place(POINT { x: 1921, y: 1000 }, work, w, h);
        assert_eq!(p.x, 1920);
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

use crate::comp::Composition;
use crate::device;
use crate::icon;
use crate::render::Renderer;
use crate::theme;
use std::cell::RefCell;
use windows::core::{w, Result, BOOL, PCWSTR};
use windows::Win32::Foundation::{HINSTANCE, HWND, LPARAM, LRESULT, WPARAM};
use crate::geometry::Segment;
use windows::Win32::Graphics::Direct2D::Common::{
    D2D1_BEZIER_SEGMENT, D2D1_COLOR_F as Color, D2D1_FIGURE_BEGIN_FILLED,
    D2D1_FIGURE_END_CLOSED, D2D1_FILL_MODE_WINDING, D2D_RECT_F,
};
use windows::Win32::Graphics::Direct2D::{
    ID2D1Brush, ID2D1RenderTarget, D2D1_ELLIPSE, D2D1_ROUNDED_RECT,
};
use windows::Win32::Graphics::DirectWrite::{
    IDWriteFactory, IDWriteTextFormat, DWRITE_FONT_STRETCH_NORMAL, DWRITE_FONT_STYLE_NORMAL,
    DWRITE_FONT_WEIGHT, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_FONT_WEIGHT_SEMI_BOLD,
    DWRITE_PARAGRAPH_ALIGNMENT_CENTER, DWRITE_TEXT_ALIGNMENT, DWRITE_TEXT_ALIGNMENT_CENTER,
    DWRITE_TEXT_ALIGNMENT_LEADING,
};
use windows::Win32::Graphics::Dwm::{
    DwmSetWindowAttribute, DWMSBT_TRANSIENTWINDOW, DWMWA_SYSTEMBACKDROP_TYPE,
    DWMWA_USE_IMMERSIVE_DARK_MODE, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND,
};
use windows::Win32::Graphics::Gdi::{
    GetMonitorInfoW, MonitorFromPoint, MONITORINFO, MONITOR_DEFAULTTONEAREST,
};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::Controls::WM_MOUSELEAVE;
use windows::Win32::UI::HiDpi::{GetDpiForMonitor, GetDpiForWindow, MDT_EFFECTIVE_DPI};
use windows::Win32::UI::Input::KeyboardAndMouse::{
    ReleaseCapture, SetCapture, TrackMouseEvent, TME_LEAVE, TRACKMOUSEEVENT, VK_ESCAPE,
};
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, GetCursorPos, LoadCursorW, PostMessageW, RegisterClassW,
    SetForegroundWindow, SetWindowPos, ShowWindow, IDC_ARROW, SW_HIDE, SW_SHOWNOACTIVATE,
    SWP_NOACTIVATE, SWP_NOZORDER, WM_ACTIVATEAPP, WM_KEYDOWN, WM_KILLFOCUS, WM_LBUTTONDOWN,
    WM_LBUTTONUP, WM_MOUSEMOVE, WNDCLASSW, WS_EX_NOACTIVATE, WS_EX_NOREDIRECTIONBITMAP,
    WS_EX_TOOLWINDOW, WS_EX_TOPMOST, WS_POPUP,
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
const TEXT_LEFT: f32 = 44.0;
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
    /// The DirectComposition pipeline for this window, built once at
    /// creation and resized in place rather than recreated. A swapchain
    /// shows exactly one frame per `Present`, so unlike the old layered
    /// window's pre-rendered hover bitmaps, content is redrawn on demand.
    comp: Composition,
    /// Cloned once from the app's `Renderer` at creation, so a hover-only
    /// repaint inside `wndproc` can build text formats without the renderer
    /// being passed back in from `main.rs`.
    dwrite: IDWriteFactory,
    /// The state last painted, so a hover-only repaint can redraw the full
    /// frame without the caller resupplying it.
    state: device::State,
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
    /// Creates the (hidden) composition-backed window owned by `owner`, so
    /// it is destroyed with it.
    pub fn new(r: &Renderer, owner: HWND) -> Result<Popup> {
        unsafe {
            let instance: HINSTANCE = GetModuleHandleW(None)?.into();
            register_class(instance);
            let (hwnd, w, h, scale) = create(instance, owner)?;

            // DWM owns the background now (acrylic, rounded corners,
            // shadow) — a transparent shadow margin can't coexist with
            // DWMWA_SYSTEMBACKDROP_TYPE, which fills the whole window rect.
            // Report each attribute's HRESULT instead of discarding it, so
            // a rejection (e.g. an OS build without acrylic support) is
            // visible instead of silently flattening the popup.
            let backdrop = DWMSBT_TRANSIENTWINDOW;
            if let Err(e) = DwmSetWindowAttribute(
                hwnd,
                DWMWA_SYSTEMBACKDROP_TYPE,
                &backdrop as *const _ as *const _,
                std::mem::size_of_val(&backdrop) as u32,
            ) {
                eprintln!("DWMWA_SYSTEMBACKDROP_TYPE failed: {e}");
            }

            let corner = DWMWCP_ROUND;
            if let Err(e) = DwmSetWindowAttribute(
                hwnd,
                DWMWA_WINDOW_CORNER_PREFERENCE,
                &corner as *const _ as *const _,
                std::mem::size_of_val(&corner) as u32,
            ) {
                eprintln!("DWMWA_WINDOW_CORNER_PREFERENCE failed: {e}");
            }

            if let Err(e) = set_dark_mode(hwnd, theme::dark_apps()) {
                eprintln!("DWMWA_USE_IMMERSIVE_DARK_MODE failed: {e}");
            }

            let comp = Composition::new(r, hwnd, w as u32, h as u32)?;

            POPUP.with(|p| {
                *p.borrow_mut() = Some(Inner {
                    owner,
                    visible: false,
                    hovered: None,
                    scale,
                    pos: POINT::default(),
                    size: (w, h),
                    comp,
                    dwrite: r.dwrite().clone(),
                    state: device::State {
                        status: device::Status::Disconnected,
                        layers: Default::default(),
                    },
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

    /// Paints the popup for `state`, places the window on the monitor under
    /// the cursor and shows it. Called again while it is already up it
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

            let w = (WIDTH * scale).round() as i32;
            let h = (HEIGHT * scale).round() as i32;
            let pos = match open {
                Some((pos, _, _)) => pos,
                None => place(cursor, work, w, h),
            };

            if open.is_none() {
                // Reposition/resize for whatever monitor's DPI is under the
                // cursor this time; a no-op the first time, since `create`
                // already sized the window to match.
                let _ = SetWindowPos(
                    self.hwnd,
                    None,
                    pos.x,
                    pos.y,
                    w,
                    h,
                    SWP_NOZORDER | SWP_NOACTIVATE,
                );
            }

            // Re-tint the acrylic for the current theme. DWM does not track
            // this on its own for a window it isn't otherwise chroming, so
            // it must be re-applied on every show, including a reshow
            // triggered by a theme change.
            if let Err(e) = set_dark_mode(self.hwnd, theme::dark_apps()) {
                eprintln!("DWMWA_USE_IMMERSIVE_DARK_MODE failed: {e}");
            }

            // The borrow ends before ShowWindow/SetForegroundWindow, which
            // dispatch messages straight back into `wndproc`.
            POPUP.with(|p| {
                if let Some(i) = p.borrow_mut().as_mut() {
                    if open.is_none() {
                        let _ = i.comp.resize(w as u32, h as u32);
                    }
                    let _ = i.comp.draw(|dc| paint(dc, r.dwrite(), state, hovered, scale));
                    i.scale = scale;
                    i.pos = pos;
                    i.size = (w, h);
                    i.hovered = hovered;
                    i.state = state;
                    i.visible = true;
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

/// Creates the window and returns its handle plus the physical pixel size
/// (and the dpi scale that produced it) it was created at.
fn create(instance: HINSTANCE, owner: HWND) -> Result<(HWND, i32, i32, f32)> {
    unsafe {
        let scale = GetDpiForWindow(owner).max(96) as f32 / 96.0;
        let w = (WIDTH * scale) as i32;
        let h = (HEIGHT * scale) as i32;
        let hwnd = CreateWindowExW(
            WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE,
            CLASS,
            CLASS,
            WS_POPUP,
            0,
            0,
            w,
            h,
            Some(owner),
            None,
            Some(instance),
            None,
        )?;
        Ok((hwnd, w, h, scale))
    }
}

/// Tints the acrylic backdrop for the current app theme.
fn set_dark_mode(hwnd: HWND, dark: bool) -> Result<()> {
    let value = BOOL::from(dark);
    unsafe {
        DwmSetWindowAttribute(
            hwnd,
            DWMWA_USE_IMMERSIVE_DARK_MODE,
            &value as *const _ as *const _,
            std::mem::size_of::<BOOL>() as u32,
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
    let top = (PADDING + index * ROW_HEIGHT + extra) * scale;
    D2D_RECT_F {
        left: 0.0,
        top,
        right: WIDTH * scale,
        bottom: top + ROW_HEIGHT * scale,
    }
}

fn inset(r: D2D_RECT_F, by: f32) -> D2D_RECT_F {
    D2D_RECT_F {
        left: r.left + by,
        top: r.top + by,
        right: r.right - by,
        bottom: r.bottom - by,
    }
}

/// The icon column's square within a row, vertically centred.
fn icon_rect(row: D2D_RECT_F, scale: f32) -> D2D_RECT_F {
    let middle = (row.top + row.bottom) / 2.0;
    let left = row.left + ICON_LEFT * scale;
    D2D_RECT_F {
        left,
        top: middle - ICON_SIZE / 2.0 * scale,
        right: left + ICON_SIZE * scale,
        bottom: middle + ICON_SIZE / 2.0 * scale,
    }
}

/// Builds and fills a path geometry for a vendored glyph, scaled from its
/// authored view box to `dest`. Mirrors `icon.rs::draw_glyph`, generalized
/// to an arbitrary destination rect instead of a fixed origin box.
fn draw_icon(
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

fn format(
    dwrite: &IDWriteFactory,
    size: f32,
    weight: DWRITE_FONT_WEIGHT,
    align: DWRITE_TEXT_ALIGNMENT,
) -> Result<IDWriteTextFormat> {
    unsafe {
        let f = dwrite.CreateTextFormat(
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

/// Draws the popup's content — hover highlight, separator, rows and accent
/// pill — onto the transparent background `Composition::draw` provides. DWM
/// supplies the acrylic surface, rounded corners and shadow, so there is no
/// panel fill, border or shadow to paint here.
fn paint(
    rt: &ID2D1RenderTarget,
    dwrite: &IDWriteFactory,
    state: device::State,
    hovered: Option<Row>,
    scale: f32,
) -> Result<()> {
    let dark = theme::dark_apps();
    let s = scale;

    unsafe {
        if let Some(row) = hovered {
            let rr = D2D1_ROUNDED_RECT {
                rect: inset(row_rect(row, s), HOVER_INSET * s),
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
                    format(dwrite, 14.0 * s, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_TEXT_ALIGNMENT_LEADING)?;
                draw_text(rt, status_label(state.status), &f, body, &ink);
            }
            Some(detail) => {
                let f =
                    format(dwrite, 13.0 * s, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_TEXT_ALIGNMENT_LEADING)?;
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
                    format(dwrite, 11.0 * s, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_TEXT_ALIGNMENT_LEADING)?;
                draw_text(rt, detail, &f, D2D_RECT_F { top: middle, ..body }, &brush);
            }
        }

        // Layer row.
        let row = row_rect(Row::Layer, s);
        let middle = (row.top + row.bottom) / 2.0;
        draw_icon(
            rt,
            icon::GLYPH_PATH,
            icon::GLYPH_VIEWBOX,
            icon_rect(row, s),
            &ink,
        )?;
        let f = format(dwrite, 14.0 * s, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_TEXT_ALIGNMENT_LEADING)?;
        draw_text(
            rt,
            &state.layers.label(),
            &f,
            D2D_RECT_F {
                left: row.left + TEXT_LEFT * s,
                right: row.right - (TEXT_RIGHT + PILL_W + PADDING) * s,
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
        // A layer above 0 gets the accent pill with its digit. Anything
        // else — disconnected, no slot, or plain layer 0 — draws nothing on
        // the right, rather than a placeholder that looks like a missing
        // keyboard accelerator.
        if let Some(n) = badge {
            let pill = D2D_RECT_F {
                left: row.right - (TEXT_RIGHT + PILL_W) * s,
                right: row.right - TEXT_RIGHT * s,
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
                dwrite,
                12.0 * s,
                DWRITE_FONT_WEIGHT_SEMI_BOLD,
                DWRITE_TEXT_ALIGNMENT_CENTER,
            )?;
            draw_text(rt, &n.to_string(), &f, pill, &white);
        }

        // Separator between the Layer and Quit rows.
        let sep_y = row.bottom + SEPARATOR_H / 2.0 * s;
        let sep_brush = rt.CreateSolidColorBrush(&separator(dark), None)?;
        rt.DrawLine(
            Vector2 { X: row.left + TEXT_LEFT * s, Y: sep_y },
            Vector2 { X: row.right - TEXT_RIGHT * s, Y: sep_y },
            &sep_brush,
            s,
            None,
        );

        // Quit row.
        let row = row_rect(Row::Quit, s);
        draw_icon(rt, icon::POWER_PATH, icon::POWER_VIEWBOX, icon_rect(row, s), &ink)?;
        let f = format(dwrite, 14.0 * s, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_TEXT_ALIGNMENT_LEADING)?;
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
                    let dwrite = i.dwrite.clone();
                    let (state, scale) = (i.state, i.scale);
                    let _ = i.comp.draw(|dc| paint(dc, &dwrite, state, row, scale));
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
                    let dwrite = i.dwrite.clone();
                    let (state, scale) = (i.state, i.scale);
                    let _ = i.comp.draw(|dc| paint(dc, &dwrite, state, None, scale));
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
