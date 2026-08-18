//! Tray icon rendering.

/// `ic_fluent_layer_24_filled` from microsoft/fluentui-system-icons, MIT.
/// See assets/NOTICE-fluentui.txt.
pub const GLYPH_PATH: &str = "M13.3867 3.42476L19.7519 7.66821C20.2115 7.97456 20.3356 8.59543 20.0293 9.05496C19.956 9.16481 19.8618 9.25907 19.7519 9.33231L13.3867 13.5758C12.547 14.1356 11.453 14.1356 10.6132 13.5758L4.24807 9.33231C3.78854 9.02595 3.66437 8.40509 3.97072 7.94556C4.04396 7.8357 4.13822 7.74144 4.24807 7.66821L10.6132 3.42476C11.453 2.86492 12.547 2.86492 13.3867 3.42476ZM20.0256 12.1922C19.8772 12.4296 19.6806 12.6332 19.4486 12.7899L13.3987 16.8736C12.5535 17.4441 11.4465 17.4441 10.6013 16.8736L4.55142 12.7899C3.79043 12.2762 3.49533 11.3306 3.77229 10.5003L10.6132 15.0598C11.4005 15.5847 12.4112 15.6175 13.2264 15.1582L13.3867 15.0598L20.2271 10.4998C20.4088 11.0459 20.3545 11.666 20.0256 12.1922ZM20.0256 15.4422C19.8772 15.6796 19.6806 15.8832 19.4486 16.0399L13.3987 20.1236C12.5535 20.6941 11.4465 20.6941 10.6013 20.1236L4.55142 16.0399C3.79043 15.5262 3.49533 14.5806 3.77229 13.7503L10.6132 18.3098C11.4005 18.8347 12.4112 18.8675 13.2264 18.4082L13.3867 18.3098L20.2271 13.7498C20.4088 14.2959 20.3545 14.916 20.0256 15.4422Z";

/// The glyph is authored on a 24x24 grid.
pub const GLYPH_VIEWBOX: f32 = 24.0;

use crate::compose::{combine, downsample, to_premultiplied_bgra, Alpha};
use crate::geometry::Segment;
use crate::render::Renderer;
use windows::core::{Result, PCWSTR};
use windows::Win32::Graphics::Direct2D::Common::{
    D2D1_BEZIER_SEGMENT, D2D1_COLOR_F, D2D1_FIGURE_BEGIN_FILLED, D2D1_FIGURE_END_CLOSED,
    D2D1_FILL_MODE_WINDING, D2D_RECT_F,
};
use windows::Win32::Graphics::Direct2D::{ID2D1RenderTarget, D2D1_ROUNDED_RECT};
use windows_numerics::Vector2;
use windows::Win32::Graphics::DirectWrite::{
    DWRITE_FONT_STRETCH_NORMAL, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_WEIGHT_SEMI_BOLD,
    DWRITE_PARAGRAPH_ALIGNMENT_CENTER, DWRITE_TEXT_ALIGNMENT_CENTER,
};
use windows::Win32::Graphics::Gdi::{
    CreateBitmap, CreateDIBSection, DeleteObject, BITMAPINFO, BITMAPINFOHEADER, BI_RGB,
    DIB_RGB_COLORS,
};
use windows::Win32::UI::WindowsAndMessaging::{CreateIconIndirect, HICON, ICONINFO};

/// Supersampling factor. The digit needs it far more than the glyph does.
const SS: usize = 4;

/// Badge geometry as a fraction of the icon box, placed so the digit clears
/// the glyph's band spacing.
const BADGE_CENTER: f32 = 0.68;
const BADGE_RADIUS: f32 = 0.34;
const DIGIT_HEIGHT: f32 = 0.52;

const WHITE: D2D1_COLOR_F = D2D1_COLOR_F { r: 1.0, g: 1.0, b: 1.0, a: 1.0 };

/// Builds the tray icon at `size` logical pixels.
///
/// `badge` is `None` for layer 0, which renders as the bare glyph.
pub fn build(
    r: &Renderer,
    badge: Option<u8>,
    dark_taskbar: bool,
    size: usize,
) -> Result<HICON> {
    let hi = size * SS;

    let glyph = r.render_alpha(hi, |rt| draw_glyph(rt, hi as f32))?;
    let (hole, digit) = match badge {
        None => (Alpha::new(hi, hi), Alpha::new(hi, hi)),
        Some(n) => (
            r.render_alpha(hi, |rt| draw_hole(rt, hi as f32))?,
            r.render_alpha(hi, |rt| draw_digit(r, rt, hi as f32, n))?,
        ),
    };

    let small = downsample(&combine(&glyph, &hole, &digit), SS);
    // White on dark taskbars, near-black on light ones.
    let rgb = if dark_taskbar { (255, 255, 255) } else { (0x19, 0x19, 0x19) };
    bgra_to_hicon(&to_premultiplied_bgra(&small, rgb), size)
}

fn draw_glyph(rt: &ID2D1RenderTarget, size: f32) -> Result<()> {
    unsafe {
        let factory = rt.GetFactory()?;
        let geo = factory.CreatePathGeometry()?;
        let sink = geo.Open()?;
        sink.SetFillMode(D2D1_FILL_MODE_WINDING);

        let scale = size / GLYPH_VIEWBOX;
        let pt = |p: crate::geometry::Point| Vector2 { X: p.x * scale, Y: p.y * scale };

        // The path is a compile-time constant that Task 4's tests already
        // parse, so a failure here is a build-time mistake, not runtime input.
        let figures = crate::geometry::parse_path(GLYPH_PATH).expect("vendored glyph is valid");
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

        let brush = rt.CreateSolidColorBrush(&WHITE, None)?;
        rt.FillGeometry(&geo, &brush, None);
        Ok(())
    }
}

fn badge_rect(size: f32) -> D2D_RECT_F {
    let c = size * BADGE_CENTER;
    let rad = size * BADGE_RADIUS;
    D2D_RECT_F { left: c - rad, top: c - rad, right: c + rad, bottom: c + rad }
}

fn draw_hole(rt: &ID2D1RenderTarget, size: f32) -> Result<()> {
    unsafe {
        let rad = size * BADGE_RADIUS;
        let rr = D2D1_ROUNDED_RECT { rect: badge_rect(size), radiusX: rad, radiusY: rad };
        let brush = rt.CreateSolidColorBrush(&WHITE, None)?;
        rt.FillRoundedRectangle(&rr, &brush);
        Ok(())
    }
}

fn draw_digit(r: &Renderer, rt: &ID2D1RenderTarget, size: f32, n: u8) -> Result<()> {
    unsafe {
        let family: Vec<u16> = "Segoe UI Variable Display\0".encode_utf16().collect();
        let locale: Vec<u16> = "en-us\0".encode_utf16().collect();
        let format = r.dwrite().CreateTextFormat(
            PCWSTR(family.as_ptr()),
            None,
            DWRITE_FONT_WEIGHT_SEMI_BOLD,
            DWRITE_FONT_STYLE_NORMAL,
            DWRITE_FONT_STRETCH_NORMAL,
            size * DIGIT_HEIGHT,
            PCWSTR(locale.as_ptr()),
        )?;
        format.SetTextAlignment(DWRITE_TEXT_ALIGNMENT_CENTER)?;
        format.SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER)?;

        let text: Vec<u16> = n.to_string().encode_utf16().collect();
        let brush = rt.CreateSolidColorBrush(&WHITE, None)?;
        rt.DrawText(
            &text,
            &format,
            &badge_rect(size),
            &brush,
            Default::default(),
            Default::default(),
        );
        Ok(())
    }
}

/// Wraps a premultiplied BGRA buffer in an HICON.
fn bgra_to_hicon(bgra: &[u8], size: usize) -> Result<HICON> {
    unsafe {
        let mut bits: *mut std::ffi::c_void = std::ptr::null_mut();
        let info = BITMAPINFO {
            bmiHeader: BITMAPINFOHEADER {
                biSize: std::mem::size_of::<BITMAPINFOHEADER>() as u32,
                biWidth: size as i32,
                // Negative height makes the DIB top-down, matching our buffer.
                biHeight: -(size as i32),
                biPlanes: 1,
                biBitCount: 32,
                biCompression: BI_RGB.0,
                ..Default::default()
            },
            ..Default::default()
        };
        let color = CreateDIBSection(None, &info, DIB_RGB_COLORS, &mut bits, None, 0)?;
        std::ptr::copy_nonoverlapping(bgra.as_ptr(), bits as *mut u8, bgra.len());

        // A 32bpp icon still needs a mask bitmap even though alpha does the work.
        let mask = CreateBitmap(size as i32, size as i32, 1, 1, None);

        let ii = ICONINFO {
            fIcon: true.into(),
            hbmColor: color,
            hbmMask: mask,
            ..Default::default()
        };
        // CreateIconIndirect copies its bitmaps, so both are deleted
        // unconditionally afterward, on the error path too, to avoid
        // leaking a color DIB and mask bitmap per failed call.
        let icon = CreateIconIndirect(&ii);
        let _ = DeleteObject(color.into());
        let _ = DeleteObject(mask.into());
        icon
    }
}
