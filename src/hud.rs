//! The Raycast-style layer HUD.
//!
//! A layered, click-through, non-activating panel painted once per layer
//! change and faded out purely by varying `BLENDFUNCTION::SourceConstantAlpha`
//! on repeated `UpdateLayeredWindow` calls against the same source bitmap, so
//! a fade costs a blit, not a repaint.

use crate::icon;
use crate::popup;
use crate::protocol;
use crate::render::Renderer;
use crate::theme;
use std::cell::RefCell;
use std::time::Instant;
use windows::core::{w, Result, PCWSTR};
use windows::Win32::Foundation::{
    COLORREF, HINSTANCE, HWND, LPARAM, LRESULT, POINT, RECT, SIZE, WPARAM,
};
use windows::Win32::Graphics::Direct2D::Common::D2D_RECT_F;
use windows::Win32::Graphics::Direct2D::D2D1_ROUNDED_RECT;
use windows::Win32::Graphics::DirectWrite::{
    DWRITE_FONT_WEIGHT_NORMAL, DWRITE_TEXT_ALIGNMENT_LEADING, DWRITE_TEXT_METRICS,
};
use windows::Win32::Graphics::Gdi::{
    CreateCompatibleDC, CreateDIBSection, DeleteDC, DeleteObject, GetMonitorInfoW,
    MonitorFromPoint, SelectObject, AC_SRC_ALPHA, AC_SRC_OVER, BITMAPINFO, BITMAPINFOHEADER,
    BI_RGB, BLENDFUNCTION, DIB_RGB_COLORS, HBITMAP, HDC, HGDIOBJ, MONITORINFO,
    MONITOR_DEFAULTTOPRIMARY,
};
use windows::Win32::UI::HiDpi::{GetDpiForMonitor, MDT_EFFECTIVE_DPI};
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, KillTimer, RegisterClassW, SetTimer, ShowWindow,
    UpdateLayeredWindow, SW_HIDE, SW_SHOWNOACTIVATE, ULW_ALPHA, WM_DESTROY, WM_TIMER, WNDCLASSW,
    WS_EX_LAYERED, WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW, WS_EX_TOPMOST, WS_EX_TRANSPARENT, WS_POPUP,
};

const CLASS: PCWSTR = w!("LayersHudWindow");

/// Logical layout in pixels at 96 dpi.
const HUD_WIDTH: f32 = 200.0;
const HUD_HEIGHT: f32 = 48.0;
const HUD_CORNER: f32 = 12.0;
const HUD_ICON: f32 = 20.0;
/// From the work area's bottom edge to the panel's bottom.
const BOTTOM_GAP: f32 = 80.0;
/// Transparent bleed around the panel that the drop shadow is drawn into.
use crate::popup::SHADOW_MARGIN;
/// Gap between the glyph and the label, matching popup.rs's icon-to-text
/// convention (`TEXT_LEFT - ICON_LEFT - ICON_SIZE`).
const ICON_TEXT_GAP: f32 = 10.0;

const HOLD_MS: u64 = 900;
const FADE_MS: u64 = 250;
const TICK_MS: u32 = 16;

const TIMER_ID: usize = 1;

/// HUD state the window procedure needs. Kept out of `main.rs`'s `APP`, same
/// reasoning as `popup.rs`'s `POPUP`: the HUD's own messages arrive on the UI
/// thread and must never re-enter an `APP` borrow.
struct Inner {
    pos: POINT,
    /// Bitmap dimensions of the last paint (shadow-inclusive), in physical
    /// pixels.
    size: (i32, i32),
    /// The painted-once frame, kept alive for the whole hold+fade so the fade
    /// only ever varies `SourceConstantAlpha`, never repaints.
    dib: Option<HBITMAP>,
    dc: Option<HDC>,
    /// The DC's original (default) bitmap, restored before the DC is deleted.
    old: Option<HGDIOBJ>,
    /// When the current frame went to full opacity. `None` while hidden.
    shown_at: Option<Instant>,
}

thread_local! {
    static HUD: RefCell<Option<Inner>> = const { RefCell::new(None) };
}

pub struct Hud {
    hwnd: HWND,
}

impl Hud {
    /// Creates the (hidden) layered window once, at startup.
    pub fn new(instance: HINSTANCE) -> Result<Hud> {
        register_class(instance);
        let hwnd = create(instance)?;
        HUD.with(|h| {
            *h.borrow_mut() = Some(Inner {
                pos: POINT::default(),
                size: (0, 0),
                dib: None,
                dc: None,
                old: None,
                shown_at: None,
            });
        });
        Ok(Hud { hwnd })
    }

    /// Paints the panel for `layers`, positions it bottom-centred on the
    /// primary monitor, shows it at full opacity and (re)starts the
    /// hold-then-fade timer. Safe to call while already visible or mid-fade:
    /// the old frame is torn down and a fresh hold begins.
    pub fn show(&mut self, r: &Renderer, layers: protocol::Layers) -> Result<()> {
        unsafe {
            let monitor = MonitorFromPoint(POINT { x: 0, y: 0 }, MONITOR_DEFAULTTOPRIMARY);
            let mut mi = MONITORINFO {
                cbSize: std::mem::size_of::<MONITORINFO>() as u32,
                ..Default::default()
            };
            let work = if GetMonitorInfoW(monitor, &mut mi).as_bool() {
                mi.rcWork
            } else {
                RECT { left: 0, top: 0, right: HUD_WIDTH as i32, bottom: HUD_HEIGHT as i32 }
            };
            let mut dx = 96u32;
            let mut dy = 96u32;
            let scale = match GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, &mut dx, &mut dy) {
                Ok(()) => dx.max(96) as f32 / 96.0,
                Err(_) => 1.0,
            };

            let (bgra, w, h) = paint(r, &layers.label(), scale)?;
            let pos = place(work, w, h, scale);

            HUD.with(|hc| {
                let Ok(mut b) = hc.try_borrow_mut() else { return };
                let Some(i) = b.as_mut() else { return };
                release(self.hwnd, i);
                if let Ok((dib, dc, old)) = build_dib(w, h, &bgra) {
                    let _ = push_alpha(self.hwnd, dc, w, h, pos, 255);
                    i.dib = Some(dib);
                    i.dc = Some(dc);
                    i.old = Some(old);
                    i.size = (w, h);
                    i.pos = pos;
                    i.shown_at = Some(Instant::now());
                    let _ = SetTimer(Some(self.hwnd), TIMER_ID, TICK_MS, None);
                }
            });

            let _ = ShowWindow(self.hwnd, SW_SHOWNOACTIVATE);
            Ok(())
        }
    }

    /// Hides the HUD immediately, skipping any in-progress fade, and frees
    /// its frame.
    pub fn hide(&mut self) {
        HUD.with(|hc| {
            let Ok(mut b) = hc.try_borrow_mut() else { return };
            let Some(i) = b.as_mut() else { return };
            release(self.hwnd, i);
        });
        let _ = unsafe { ShowWindow(self.hwnd, SW_HIDE) };
    }
}

/// Frees the current frame's GDI resources and kills the fade timer.
/// Idempotent: safe to call when already released.
fn release(hwnd: HWND, i: &mut Inner) {
    unsafe {
        let _ = KillTimer(Some(hwnd), TIMER_ID);
        if let (Some(dc), Some(old)) = (i.dc.take(), i.old.take()) {
            let _ = SelectObject(dc, old);
            let _ = DeleteDC(dc);
        }
        if let Some(dib) = i.dib.take() {
            let _ = DeleteObject(dib.into());
        }
    }
    i.shown_at = None;
}

fn register_class(instance: HINSTANCE) {
    static ONCE: std::sync::Once = std::sync::Once::new();
    ONCE.call_once(|| unsafe {
        // Null hCursor: the HUD is click-through and never hovered, so it
        // must never fight whatever cursor shape is already showing.
        let wc = WNDCLASSW {
            lpfnWndProc: Some(wndproc),
            hInstance: instance,
            lpszClassName: CLASS,
            ..Default::default()
        };
        RegisterClassW(&wc);
    });
}

fn create(instance: HINSTANCE) -> Result<HWND> {
    unsafe {
        CreateWindowExW(
            WS_EX_LAYERED
                | WS_EX_TRANSPARENT
                | WS_EX_TOOLWINDOW
                | WS_EX_TOPMOST
                | WS_EX_NOACTIVATE,
            CLASS,
            CLASS,
            WS_POPUP,
            0,
            0,
            0,
            0,
            None,
            None,
            Some(instance),
            None,
        )
    }
}

/// Bottom-centred placement on `work`, mirroring `popup::place`'s treatment
/// of the shadow bleed: `w`/`h` are the bitmap's (shadow-inclusive)
/// dimensions in physical pixels, `scale` converts the logical margins into
/// that same space, and the returned point is the bitmap's top-left so the
/// visible panel — not the transparent bleed around it — lands where the
/// math says.
fn place(work: RECT, w: i32, h: i32, scale: f32) -> POINT {
    let margin = (SHADOW_MARGIN * scale).round() as i32;
    let panel_w = w - margin * 2;
    let panel_h = h - margin * 2;
    let gap = (BOTTOM_GAP * scale).round() as i32;
    let x = work.left + ((work.right - work.left) - panel_w) / 2;
    let panel_bottom = work.bottom - gap;
    let y = panel_bottom - panel_h;
    POINT { x: x - margin, y: y - margin }
}

/// The fade's alpha-vs-elapsed-time curve, in `SourceConstantAlpha` units.
/// Full opacity through the hold, then a linear ramp to zero over the fade.
fn alpha_at(elapsed_ms: u64, hold_ms: u64, fade_ms: u64) -> u8 {
    if elapsed_ms < hold_ms {
        return 255;
    }
    let fade_elapsed = elapsed_ms - hold_ms;
    if fade_elapsed >= fade_ms {
        return 0;
    }
    let remaining = fade_ms - fade_elapsed;
    ((remaining as f64 / fade_ms as f64) * 255.0).round() as u8
}

/// Measures a run of text set in `format`, in the same (already-scaled)
/// units as `format`'s font size.
fn measure_width(r: &Renderer, s: &str, format: &windows::Win32::Graphics::DirectWrite::IDWriteTextFormat) -> Result<f32> {
    unsafe {
        let text: Vec<u16> = s.encode_utf16().collect();
        let layout = r.dwrite().CreateTextLayout(&text, format, 10_000.0, 10_000.0)?;
        let mut metrics = DWRITE_TEXT_METRICS::default();
        layout.GetMetrics(&mut metrics)?;
        Ok(metrics.width)
    }
}

/// Renders the HUD panel: rounded surface, border, drop shadow, and the
/// layers glyph plus label centred as a group. Returns premultiplied BGRA
/// plus its pixel dimensions.
fn paint(r: &Renderer, label: &str, scale: f32) -> Result<(Vec<u8>, i32, i32)> {
    let w = ((HUD_WIDTH + SHADOW_MARGIN * 2.0) * scale).round() as i32;
    let h = ((HUD_HEIGHT + SHADOW_MARGIN * 2.0) * scale).round() as i32;
    let dark = theme::dark_apps();
    let s = scale;

    let bgra = r.render_bgra(w as u32, h as u32, |rt| unsafe {
        crate::popup::draw_shadow(rt, w as f32, h as f32, s, HUD_CORNER)?;

        let panel_rect = popup::inset(
            D2D_RECT_F {
                left: SHADOW_MARGIN * s,
                top: SHADOW_MARGIN * s,
                right: w as f32 - SHADOW_MARGIN * s,
                bottom: h as f32 - SHADOW_MARGIN * s,
            },
            s / 2.0,
        );
        let panel = D2D1_ROUNDED_RECT { rect: panel_rect, radiusX: HUD_CORNER * s, radiusY: HUD_CORNER * s };
        let fill = rt.CreateSolidColorBrush(&popup::surface(dark), None)?;
        rt.FillRoundedRectangle(&panel, &fill);
        let edge = rt.CreateSolidColorBrush(&popup::border(dark), None)?;
        rt.DrawRoundedRectangle(&panel, &edge, s, None);

        let ink = rt.CreateSolidColorBrush(&popup::text(dark), None)?;
        let f = popup::format(
            r,
            16.0 * s,
            DWRITE_FONT_WEIGHT_NORMAL,
            DWRITE_TEXT_ALIGNMENT_LEADING,
        )?;
        let text_w = measure_width(r, label, &f)?;

        let icon_size = HUD_ICON * s;
        let gap = ICON_TEXT_GAP * s;
        let total_w = icon_size + gap + text_w;
        let center_x = (panel_rect.left + panel_rect.right) / 2.0;
        let center_y = (panel_rect.top + panel_rect.bottom) / 2.0;
        let start_x = center_x - total_w / 2.0;

        let icon_rect = D2D_RECT_F {
            left: start_x,
            top: center_y - icon_size / 2.0,
            right: start_x + icon_size,
            bottom: center_y + icon_size / 2.0,
        };
        popup::draw_icon(rt, icon::GLYPH_PATH, icon::GLYPH_VIEWBOX, icon_rect, &ink)?;

        let text_rect = D2D_RECT_F {
            left: start_x + icon_size + gap,
            top: panel_rect.top,
            right: panel_rect.right,
            bottom: panel_rect.bottom,
        };
        popup::draw_text(rt, label, &f, text_rect, &ink);

        Ok(())
    })?;

    Ok((bgra, w, h))
}

/// Builds a DIB section holding `bgra` and a memory DC with it selected in,
/// so the caller can keep both alive across a whole hold+fade and blit the
/// same source repeatedly.
fn build_dib(w: i32, h: i32, bgra: &[u8]) -> Result<(HBITMAP, HDC, HGDIOBJ)> {
    unsafe {
        let mut bits: *mut std::ffi::c_void = std::ptr::null_mut();
        let info = BITMAPINFO {
            bmiHeader: BITMAPINFOHEADER {
                biSize: std::mem::size_of::<BITMAPINFOHEADER>() as u32,
                biWidth: w,
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
        Ok((dib, dc, old))
    }
}

/// Blits the already-painted source onto the layered window, varying only
/// the constant alpha.
fn push_alpha(hwnd: HWND, dc: HDC, w: i32, h: i32, pos: POINT, alpha: u8) -> Result<()> {
    unsafe {
        let blend = BLENDFUNCTION {
            BlendOp: AC_SRC_OVER as u8,
            BlendFlags: 0,
            SourceConstantAlpha: alpha,
            AlphaFormat: AC_SRC_ALPHA as u8,
        };
        let size = SIZE { cx: w, cy: h };
        let src = POINT { x: 0, y: 0 };
        UpdateLayeredWindow(
            hwnd,
            None,
            Some(&pos),
            Some(&size),
            Some(dc),
            Some(&src),
            COLORREF(0),
            Some(&blend),
            ULW_ALPHA,
        )
    }
}

/// Advances the fade by one tick: repaints alpha only, or hides and releases
/// the frame once the fade completes.
fn tick(hwnd: HWND) {
    enum Next {
        Hidden,
        Frame(HDC, i32, i32, POINT, u8),
    }

    let next = HUD.with(|hc| {
        let Ok(mut b) = hc.try_borrow_mut() else { return None };
        let i = b.as_mut()?;
        let shown_at = i.shown_at?;
        let elapsed = shown_at.elapsed().as_millis() as u64;
        let alpha = alpha_at(elapsed, HOLD_MS, FADE_MS);
        if alpha == 0 {
            release(hwnd, i);
            Some(Next::Hidden)
        } else {
            let dc = i.dc?;
            let (w, h) = i.size;
            Some(Next::Frame(dc, w, h, i.pos, alpha))
        }
    });

    match next {
        Some(Next::Hidden) => {
            let _ = unsafe { ShowWindow(hwnd, SW_HIDE) };
        }
        Some(Next::Frame(dc, w, h, pos, alpha)) => {
            let _ = push_alpha(hwnd, dc, w, h, pos, alpha);
        }
        None => {}
    }
}

unsafe extern "system" fn wndproc(hwnd: HWND, msg: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    match msg {
        WM_TIMER if wp.0 == TIMER_ID => {
            tick(hwnd);
            LRESULT(0)
        }
        WM_DESTROY => {
            HUD.with(|hc| {
                let Ok(mut b) = hc.try_borrow_mut() else { return };
                if let Some(i) = b.as_mut() {
                    release(hwnd, i);
                }
            });
            LRESULT(0)
        }
        _ => unsafe { DefWindowProcW(hwnd, msg, wp, lp) },
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn work() -> RECT {
        RECT { left: 0, top: 0, right: 1920, bottom: 1040 }
    }

    fn bitmap_wh(scale: f32) -> (i32, i32) {
        (
            ((HUD_WIDTH + SHADOW_MARGIN * 2.0) * scale).round() as i32,
            ((HUD_HEIGHT + SHADOW_MARGIN * 2.0) * scale).round() as i32,
        )
    }

    #[test]
    fn the_panel_is_horizontally_centred_on_the_work_area() {
        let (w, h) = bitmap_wh(1.0);
        let p = place(work(), w, h, 1.0);
        let margin = SHADOW_MARGIN as i32;
        let panel_left = p.x + margin;
        let panel_right = panel_left + HUD_WIDTH as i32;
        let center = (panel_left + panel_right) / 2;
        assert_eq!(center, (work().right - work().left) / 2);
    }

    #[test]
    fn the_panel_sits_bottom_gap_above_the_work_area_bottom() {
        let (w, h) = bitmap_wh(1.0);
        let p = place(work(), w, h, 1.0);
        let panel_bottom = p.y + SHADOW_MARGIN as i32 + HUD_HEIGHT as i32;
        assert_eq!(panel_bottom, work().bottom - BOTTOM_GAP as i32);
    }

    #[test]
    fn placement_scales_the_bottom_gap_and_margin_with_dpi() {
        let scale = 1.5;
        let (w, h) = bitmap_wh(scale);
        let p = place(work(), w, h, scale);
        let margin = (SHADOW_MARGIN * scale).round() as i32;
        let gap = (BOTTOM_GAP * scale).round() as i32;
        let panel_h = h - margin * 2;
        assert_eq!(p.y + margin + panel_h, work().bottom - gap);
    }

    #[test]
    fn placement_respects_a_work_area_not_starting_at_the_origin() {
        let work = RECT { left: 1920, top: 0, right: 3840, bottom: 1040 };
        let (w, h) = bitmap_wh(1.0);
        let p = place(work, w, h, 1.0);
        let margin = SHADOW_MARGIN as i32;
        let panel_left = p.x + margin;
        let panel_right = panel_left + HUD_WIDTH as i32;
        let center = (panel_left + panel_right) / 2;
        assert_eq!(center, (work.left + work.right) / 2);
    }

    #[test]
    fn alpha_is_full_for_the_entire_hold() {
        assert_eq!(alpha_at(0, 900, 250), 255);
        assert_eq!(alpha_at(899, 900, 250), 255);
    }

    #[test]
    fn alpha_starts_the_fade_at_full_opacity() {
        assert_eq!(alpha_at(900, 900, 250), 255);
    }

    #[test]
    fn alpha_reaches_zero_once_the_fade_completes() {
        assert_eq!(alpha_at(900 + 250, 900, 250), 0);
        assert_eq!(alpha_at(900 + 999, 900, 250), 0);
    }

    #[test]
    fn alpha_decreases_monotonically_through_the_fade() {
        let mut last = 256u16;
        let mut t = 900u64;
        while t <= 900 + 250 {
            let a = alpha_at(t, 900, 250) as u16;
            assert!(a <= last, "alpha rose from {last} to {a} at t={t}");
            last = a;
            t += 16;
        }
    }

    #[test]
    fn alpha_at_the_fade_midpoint_is_roughly_half() {
        let a = alpha_at(900 + 125, 900, 250);
        assert!((100..=155).contains(&a), "alpha {a} not near half");
    }
}
