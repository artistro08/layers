//! The HUD settings hover submenu.
//!
//! A second layered window, opened beside the main popup's `HudMenu` row
//! after a hover dwell (see `popup.rs`'s `WM_MOUSEMOVE`/`WM_TIMER`
//! handling). Modeled closely on `popup.rs`: same window styles, the same
//! paint-every-hover-frame-up-front approach, and the same thread-local
//! `Inner` + `try_borrow`/`try_borrow_mut` discipline — a plain `borrow_mut`
//! panicking inside an `extern "system"` window procedure aborts the
//! process, and this codebase has been bitten by that before.
//!
//! Unlike the popup, this window never takes focus or capture: all of its
//! dismissal triggers (Escape, click-away, focus loss, the popup itself
//! closing) are driven by messages the popup posts, not by anything this
//! window receives directly. It only needs to paint, hit-test hover, and
//! react to clicks.

use crate::popup::{
    self, item_at, panel_height, border, draw_shadow, draw_toggle_row, hover, inset, inset_xy, monitor_work_area,
    separator, surface, text, CORNER, HOVER_INSET_X, HOVER_INSET_Y, SHADOW_MARGIN,
};
use crate::render::Renderer;
use crate::settings::Settings;
use crate::theme;
use std::cell::RefCell;
use windows::core::{w, Result, PCWSTR};
use windows::Win32::Foundation::{HINSTANCE, HWND, LPARAM, LRESULT, POINT, RECT, WPARAM};
use windows::Win32::Graphics::Direct2D::Common::D2D_RECT_F;
use windows::Win32::Graphics::Direct2D::{ID2D1Brush, D2D1_ROUNDED_RECT};
use windows::Win32::UI::Controls::WM_MOUSELEAVE;
use windows::Win32::UI::Input::KeyboardAndMouse::{TrackMouseEvent, TME_LEAVE, TRACKMOUSEEVENT};
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, LoadCursorW, PostMessageW, RegisterClassW, SetWindowPos,
    ShowWindow, HWND_TOPMOST, IDC_ARROW, SWP_NOACTIVATE, SWP_NOMOVE, SWP_NOSIZE, SW_HIDE,
    MA_NOACTIVATE, SW_SHOWNOACTIVATE, WM_APP, WM_LBUTTONUP, WM_MOUSEACTIVATE, WM_MOUSEMOVE,
    WNDCLASSW, WS_EX_LAYERED,
    WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW, WS_EX_TOPMOST, WS_POPUP,
};
use windows_numerics::Vector2;

/// Posted to the owner window when the "Show HUD" row is clicked. The
/// submenu has already stayed open; the owner flips `Settings::hud_enabled`,
/// saves, and re-shows so the checkmark updates.
pub const HUD_TOGGLE_CLICKED: u32 = WM_APP + 5;
/// Posted to the owner window when a per-layer row is clicked. `wParam` is
/// the layer number. The owner flips that bit of `Settings::hud_suppressed`,
/// saves, and re-shows.
pub const LAYER_TOGGLE_CLICKED: u32 = WM_APP + 6;

/// Logical layout in pixels at 96 dpi. Sized to fit "Show HUD" comfortably
/// rather than to match the popup's own (unrelated) width.
pub const WIDTH: f32 = 150.0;

/// Screen-pixel overlap between the submenu and the popup's edge it opens
/// beside, so the pointer can cross the boundary without dipping into a gap.
const OVERLAP: f32 = 2.0;

/// The submenu's contents, in painted/hit-tested order. Fixed: all 8 layers
/// always show, so the panel never changes size while the master switch is
/// flipped.
#[derive(Clone, Copy, PartialEq, Debug)]
pub enum SubItem {
    HudToggle,
    Separator,
    LayerToggle(u8),
}

impl crate::popup::PanelItem for SubItem {
    fn is_separator(self) -> bool {
        matches!(self, SubItem::Separator)
    }
}

/// This panel's own width, so callers cannot accidentally pick up the
/// popup's.
fn row_rect(index: usize, items: &[SubItem], scale: f32) -> D2D_RECT_F {
    crate::popup::row_rect(index, items, scale, WIDTH)
}

/// The submenu's fixed contents, in order.
pub fn items() -> Vec<SubItem> {
    let mut v = vec![SubItem::HudToggle, SubItem::Separator];
    v.extend((0..8u8).map(SubItem::LayerToggle));
    v
}






/// Places the submenu beside `anchor` (the popup's `HudMenu` row, in screen
/// coordinates): its first row lines up with `anchor`'s top, and it opens
/// just outside the popup's right edge with a couple of pixels of overlap.
/// Flips to the popup's left edge if there is no room on the right, and
/// clamps vertically into `work`. `w`/`h` are the submenu bitmap's
/// (shadow-inclusive) dimensions in physical pixels.
pub(crate) fn place(anchor: RECT, work: RECT, w: i32, h: i32, scale: f32) -> POINT {
    let margin = (SHADOW_MARGIN * scale).round() as i32;
    let panel_w = w - margin * 2;
    let panel_h = h - margin * 2;
    let overlap = (OVERLAP * scale).round() as i32;

    let right_x = anchor.right - overlap;
    let fits_right = right_x + panel_w <= work.right;
    let x = if fits_right { right_x } else { anchor.left - panel_w + overlap };

    let y = anchor.top.clamp(work.top, (work.bottom - panel_h).max(work.top));
    POINT { x: x - margin, y: y - margin }
}

/// Submenu state the window procedure needs. Kept out of `main.rs`'s `APP`,
/// same reasoning as `popup.rs`'s `POPUP`.
struct Inner {
    /// This window's own handle, so free functions outside the window
    /// procedure (`click_at_screen`, `hover_at_screen`) can repaint without
    /// needing a `Submenu` handle passed in from the popup.
    hwnd: HWND,
    owner: HWND,
    visible: bool,
    hovered: Option<usize>,
    scale: f32,
    pos: POINT,
    size: (i32, i32),
    items: Vec<SubItem>,
    /// Snapshot of the master switch at the last `show()`, so a click on a
    /// `LayerToggle` row can be gated inert without touching `Settings`.
    hud_enabled: bool,
    frames: Vec<Vec<u8>>,
}

thread_local! {
    static SUBMENU: RefCell<Option<Inner>> = const { RefCell::new(None) };
}

/// Index into `Inner::frames`: 0 is unhovered, `i + 1` is `items[i]` hovered.
fn frame_index(hovered: Option<usize>) -> usize {
    match hovered {
        None => 0,
        Some(i) => i + 1,
    }
}

pub struct Submenu {
    hwnd: HWND,
}

impl Submenu {
    /// Creates the (hidden) layered window owned by `owner`.
    pub fn new(instance: HINSTANCE, owner: HWND) -> Result<Submenu> {
        register_class(instance);
        let hwnd = create(instance, owner)?;
        SUBMENU.with(|s| {
            *s.borrow_mut() = Some(Inner {
                hwnd,
                owner,
                visible: false,
                hovered: None,
                scale: 1.0,
                pos: POINT::default(),
                size: (0, 0),
                items: Vec::new(),
                hud_enabled: true,
                frames: Vec::new(),
            });
        });
        Ok(Submenu { hwnd })
    }

    pub fn is_visible(&self) -> bool {
        SUBMENU.with(|s| {
            s.try_borrow()
                .ok()
                .and_then(|b| b.as_ref().map(|i| i.visible))
                .unwrap_or(false)
        })
    }

    /// Paints every hover frame for `settings`, places the window beside
    /// `anchor_row` (the popup's `HudMenu` row, in screen coordinates) and
    /// shows it without taking focus. Safe to call while already open — a
    /// toggle click re-shows to refresh the checkmarks.
    pub fn show(&mut self, r: &Renderer, settings: &Settings, anchor_row: RECT) -> Result<()> {
        unsafe {
            let list = items();
            let (work, scale) =
                monitor_work_area(POINT { x: anchor_row.left, y: anchor_row.top });

            let base = paint(r, settings, &list, None, scale)?;
            let mut frames: Vec<Vec<u8>> = Vec::with_capacity(list.len() + 1);
            frames.push(base.0.clone());
            for (idx, item) in list.iter().enumerate() {
                if matches!(item, SubItem::Separator) {
                    frames.push(base.0.clone());
                } else {
                    frames.push(paint(r, settings, &list, Some(idx), scale)?.0);
                }
            }
            let (w, h) = (base.1, base.2);
            let pos = place(anchor_row, work, w, h, scale);

            SUBMENU.with(|s| {
                if let Some(i) = s.borrow_mut().as_mut() {
                    i.items = list;
                    i.frames = frames;
                    i.scale = scale;
                    i.pos = pos;
                    i.size = (w, h);
                    i.hud_enabled = settings.hud_enabled;
                    i.hovered = None;
                    i.visible = true;
                    let _ = popup::push(self.hwnd, &i.frames[frame_index(None)], w, h, pos);
                }
            });

            let _ = ShowWindow(self.hwnd, SW_SHOWNOACTIVATE);
            // Both this window and the popup sit in the WS_EX_TOPMOST band,
            // which does not by itself order them relative to each other.
            // Lift the submenu to the front of that band on every show (not
            // just the first) since the popup repaints and re-shows on its
            // own hover changes and can otherwise end up above it again.
            let _ = SetWindowPos(
                self.hwnd,
                Some(HWND_TOPMOST),
                0,
                0,
                0,
                0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE,
            );
            Ok(())
        }
    }

    /// Hides the window. Idempotent — safe to call whether or not it is
    /// currently open, which is how the popup's dismissal path treats it.
    pub fn hide(&mut self) {
        SUBMENU.with(|s| {
            if let Ok(mut b) = s.try_borrow_mut() {
                if let Some(i) = b.as_mut() {
                    i.visible = false;
                    i.hovered = None;
                }
            }
        });
        let _ = unsafe { ShowWindow(self.hwnd, SW_HIDE) };
    }
}

/// The submenu's window rect in screen coordinates, if it is currently
/// visible. The popup uses this (via `popup.rs`'s `captured_screen_point`)
/// to route a captured mouse message to the submenu instead of treating it
/// as a click-away. A free function, not a `Submenu` method, since the
/// popup's window procedure has no `Submenu` handle to call through — only
/// the thread-local state, same as everything else here.
pub fn visible_rect() -> Option<RECT> {
    SUBMENU.with(|s| {
        // `try_borrow`, not `borrow`: called from the popup's window
        // procedure, which must never panic into an aborted process.
        let b = s.try_borrow().ok()?;
        let i = b.as_ref()?;
        if !i.visible {
            return None;
        }
        let (w, h) = i.size;
        Some(RECT { left: i.pos.x, top: i.pos.y, right: i.pos.x + w, bottom: i.pos.y + h })
    })
}

/// Resolves a click on `idx` (if any) against the current master-switch
/// state and posts the matching message to the owner. Shared by the window
/// procedure's `WM_LBUTTONUP` arm (idx from hover, the normal path when the
/// submenu receives its own messages) and `click_at_screen` (idx from a
/// screen point, the path used when the popup had capture and forwards a
/// click here instead) — one implementation of "a click landed on item N".
fn click_item(idx: Option<usize>) {
    let clicked = SUBMENU.with(|s| {
        let Ok(b) = s.try_borrow() else { return None };
        let i = b.as_ref()?;
        let item = i.items.get(idx?).copied()?;
        if matches!(item, SubItem::LayerToggle(_)) && !i.hud_enabled {
            // Inert while the master switch is off.
            return None;
        }
        Some((item, i.owner))
    });
    if let Some((item, owner)) = clicked {
        match item {
            SubItem::HudToggle => {
                let _ = unsafe {
                    PostMessageW(Some(owner), HUD_TOGGLE_CLICKED, WPARAM(0), LPARAM(0))
                };
            }
            SubItem::LayerToggle(n) => {
                let _ = unsafe {
                    PostMessageW(Some(owner), LAYER_TOGGLE_CLICKED, WPARAM(n as usize), LPARAM(0))
                };
            }
            SubItem::Separator => {}
        }
    }
}

/// Handles a click the popup forwarded because it landed on the submenu
/// while the popup held mouse capture (see `popup.rs`'s
/// `captured_screen_point` and its `WM_LBUTTONUP` arm). Converts to the
/// submenu's own client coordinates and resolves the item the same way the
/// window procedure would if it had received the click directly.
pub fn click_at_screen(pt: POINT) {
    let idx = SUBMENU.with(|s| {
        let Ok(b) = s.try_borrow() else { return None };
        let i = b.as_ref()?;
        if !i.visible {
            return None;
        }
        let y = (pt.y - i.pos.y) as f32;
        item_at(y / i.scale, &i.items).map(|(idx, _)| idx)
    });
    click_item(idx);
}

/// Handles a hover move the popup forwarded for the same reason as
/// `click_at_screen`. Updates the submenu's own hover highlight, which
/// otherwise never sees `WM_MOUSEMOVE` while the popup holds capture.
pub fn hover_at_screen(pt: POINT) {
    SUBMENU.with(|s| {
        let Ok(mut b) = s.try_borrow_mut() else { return };
        let Some(i) = b.as_mut() else { return };
        if !i.visible {
            return;
        }
        let y = (pt.y - i.pos.y) as f32;
        let hit = item_at(y / i.scale, &i.items).map(|(idx, _)| idx);
        if hit != i.hovered {
            i.hovered = hit;
            let (w, h) = i.size;
            let _ = popup::push(i.hwnd, &i.frames[frame_index(hit)], w, h, i.pos);
        }
    });
}

fn register_class(instance: HINSTANCE) {
    static ONCE: std::sync::Once = std::sync::Once::new();
    ONCE.call_once(|| unsafe {
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

const CLASS: PCWSTR = w!("LayersSubmenuWindow");

fn create(instance: HINSTANCE, owner: HWND) -> Result<HWND> {
    unsafe {
        CreateWindowExW(
            WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE,
            CLASS,
            CLASS,
            WS_POPUP,
            0,
            0,
            0,
            0,
            Some(owner),
            None,
            Some(instance),
            None,
        )
    }
}

/// Renders one frame of the submenu. Returns premultiplied BGRA plus its
/// pixel dimensions. Mirrors `popup::paint`'s panel/shadow/hover structure.
fn paint(
    r: &Renderer,
    settings: &Settings,
    items: &[SubItem],
    hovered: Option<usize>,
    scale: f32,
) -> Result<(Vec<u8>, i32, i32)> {
    let w = ((WIDTH + SHADOW_MARGIN * 2.0) * scale).round() as i32;
    let h = ((panel_height(items) + SHADOW_MARGIN * 2.0) * scale).round() as i32;
    let dark = theme::dark_apps();
    let s = scale;

    let bgra = r.render_bgra(w as u32, h as u32, |rt| unsafe {
        draw_shadow(rt, w as f32, h as f32, s, CORNER)?;

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

        // A hovered LayerToggle row while the master switch is off is
        // inert, so it gets no hover fill either — it reads the same way
        // Windows' own disabled menu items do.
        if let Some(idx) = hovered {
            let inert = matches!(items[idx], SubItem::LayerToggle(_)) && !settings.hud_enabled;
            if !inert {
                let rr = D2D1_ROUNDED_RECT {
                    rect: inset_xy(row_rect(idx, items, s), HOVER_INSET_X * s, HOVER_INSET_Y * s),
                    radiusX: 4.0 * s,
                    radiusY: 4.0 * s,
                };
                let brush = rt.CreateSolidColorBrush(&hover(dark), None)?;
                rt.FillRoundedRectangle(&rr, &brush);
            }
        }

        let ink = rt.CreateSolidColorBrush(&text(dark), None)?;
        // While the master switch is off, the per-layer rows still render
        // (so the submenu never changes size) but greyed and inert; the
        // master already overrides them.
        let mut dim = text(dark);
        dim.a *= 0.4;
        let dim_ink = rt.CreateSolidColorBrush(&dim, None)?;

        for (i, item) in items.iter().enumerate() {
            let row = row_rect(i, items, s);
            match *item {
                SubItem::HudToggle => {
                    draw_toggle_row(rt, r, row, settings.hud_enabled, "Show HUD", &ink, s)?;
                }
                SubItem::LayerToggle(n) => {
                    let checked = settings.hud_suppressed & (1 << n) == 0;
                    let brush: &ID2D1Brush = if settings.hud_enabled { &ink } else { &dim_ink };
                    draw_toggle_row(rt, r, row, checked, &format!("Layer {n}"), brush, s)?;
                }
                SubItem::Separator => {
                    let sep_y = (row.top + row.bottom) / 2.0;
                    let sep_brush = rt.CreateSolidColorBrush(&separator(dark), None)?;
                    rt.DrawLine(
                        Vector2 { X: row.left + HOVER_INSET_X * s, Y: sep_y },
                        Vector2 { X: row.right - HOVER_INSET_X * s, Y: sep_y },
                        &sep_brush,
                        s,
                        None,
                    );
                }
            }
        }
        Ok(())
    })?;

    Ok((bgra, w, h))
}

unsafe extern "system" fn wndproc(hwnd: HWND, msg: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    match msg {
        WM_MOUSEMOVE => {
            let mut tme = TRACKMOUSEEVENT {
                cbSize: std::mem::size_of::<TRACKMOUSEEVENT>() as u32,
                dwFlags: TME_LEAVE,
                hwndTrack: hwnd,
                dwHoverTime: 0,
            };
            let _ = unsafe { TrackMouseEvent(&mut tme) };

            let y = ((lp.0 >> 16) & 0xFFFF) as u16 as i16 as f32;
            SUBMENU.with(|s| {
                let Ok(mut b) = s.try_borrow_mut() else { return };
                let Some(i) = b.as_mut() else { return };
                let hit = item_at(y / i.scale, &i.items).map(|(idx, _)| idx);
                if hit != i.hovered {
                    i.hovered = hit;
                    let (w, h) = i.size;
                    let _ = popup::push(hwnd, &i.frames[frame_index(hit)], w, h, i.pos);
                }
            });
            LRESULT(0)
        }
        WM_MOUSELEAVE => {
            SUBMENU.with(|s| {
                let Ok(mut b) = s.try_borrow_mut() else { return };
                let Some(i) = b.as_mut() else { return };
                if i.hovered.is_some() {
                    i.hovered = None;
                    let (w, h) = i.size;
                    let _ = popup::push(hwnd, &i.frames[frame_index(None)], w, h, i.pos);
                }
            });
            LRESULT(0)
        }
        // Without this the mouse-down triggers an activation attempt. The
        // popup then takes WM_KILLFOCUS, runs its dismissal path, and hides
        // this window — so WM_LBUTTONUP is delivered to a dead window and no
        // click is ever seen. MA_NOACTIVATE keeps the click from disturbing
        // focus at all.
        WM_MOUSEACTIVATE => LRESULT(MA_NOACTIVATE as isize),
        WM_LBUTTONUP => {
            let idx = SUBMENU.with(|s| {
                let Ok(b) = s.try_borrow() else { return None };
                b.as_ref()?.hovered
            });
            click_item(idx);
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

    fn bitmap_wh(h: f32) -> (i32, i32) {
        ((WIDTH + SHADOW_MARGIN * 2.0) as i32, (h + SHADOW_MARGIN * 2.0) as i32)
    }

    #[test]
    fn items_are_the_master_switch_a_separator_then_all_eight_layers_ascending() {
        let list = items();
        assert_eq!(list.len(), 10);
        assert_eq!(list[0], SubItem::HudToggle);
        assert_eq!(list[1], SubItem::Separator);
        for n in 0..8u8 {
            assert_eq!(list[2 + n as usize], SubItem::LayerToggle(n));
        }
    }

    #[test]
    fn item_at_of_row_rect_midpoint_returns_the_same_item() {
        let list = items();
        for (i, item) in list.iter().enumerate() {
            let rect = row_rect(i, &list, 1.0);
            let mid_y = (rect.top + rect.bottom) / 2.0;
            if matches!(item, SubItem::Separator) {
                assert_eq!(item_at(mid_y, &list), None);
            } else {
                assert_eq!(item_at(mid_y, &list), Some((i, *item)));
            }
        }
    }

    #[test]
    fn item_at_of_row_rect_midpoint_round_trips_at_a_scaled_dpi_too() {
        let list = items();
        for (i, item) in list.iter().enumerate() {
            let rect = row_rect(i, &list, 1.5);
            let mid_y = (rect.top + rect.bottom) / 2.0;
            if matches!(item, SubItem::Separator) {
                assert_eq!(item_at(mid_y / 1.5, &list), None);
            } else {
                assert_eq!(item_at(mid_y / 1.5, &list), Some((i, *item)));
            }
        }
    }

    #[test]
    fn opens_to_the_right_of_the_anchor_when_there_is_room() {
        let list = items();
        let h = panel_height(&list);
        let (w, bh) = bitmap_wh(h);
        // Anchor near the work area's left edge: plenty of room to the right.
        let anchor = RECT { left: 100, top: 200, right: 296, bottom: 232 };
        let p = place(anchor, work(), w, bh, 1.0);
        let panel_left = p.x + SHADOW_MARGIN as i32;
        assert_eq!(panel_left, anchor.right - OVERLAP as i32);
    }

    #[test]
    fn flips_to_the_left_of_the_anchor_when_there_is_no_room_on_the_right() {
        let list = items();
        let h = panel_height(&list);
        let (w, bh) = bitmap_wh(h);
        // Anchor hugging the work area's right edge: no room for a
        // 150-wide submenu to its right.
        let anchor = RECT { left: 1724, top: 200, right: 1920, bottom: 232 };
        let p = place(anchor, work(), w, bh, 1.0);
        let panel_left = p.x + SHADOW_MARGIN as i32;
        let panel_right = panel_left + WIDTH as i32;
        assert_eq!(panel_right, anchor.left + OVERLAP as i32);
        assert!(panel_left < anchor.left, "submenu should sit left of the anchor");
    }

    #[test]
    fn clamps_vertically_at_the_work_areas_bottom() {
        let list = items();
        let h = panel_height(&list);
        let (w, bh) = bitmap_wh(h);
        // Anchor near the bottom: aligning the panel's top with the
        // anchor's top would run it off the work area.
        let anchor = RECT { left: 100, top: 1030, right: 296, bottom: 1040 };
        let p = place(anchor, work(), w, bh, 1.0);
        let panel_bottom = p.y + SHADOW_MARGIN as i32 + h as i32;
        assert!(panel_bottom <= work().bottom, "panel bottom {panel_bottom} overflows work area");
    }

    #[test]
    fn clamps_vertically_at_the_work_areas_top() {
        let list = items();
        let h = panel_height(&list);
        let (w, bh) = bitmap_wh(h);
        let anchor = RECT { left: 100, top: -20, right: 296, bottom: 12 };
        let p = place(anchor, work(), w, bh, 1.0);
        assert!(p.y + SHADOW_MARGIN as i32 >= work().top);
    }
}
