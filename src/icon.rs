//! Tray icon rendering.

/// `ic_fluent_layer_24_filled` from microsoft/fluentui-system-icons, MIT.
/// See assets/NOTICE-fluentui.txt.
pub const GLYPH_PATH: &str = "M13.3867 3.42476L19.7519 7.66821C20.2115 7.97456 20.3356 8.59543 20.0293 9.05496C19.956 9.16481 19.8618 9.25907 19.7519 9.33231L13.3867 13.5758C12.547 14.1356 11.453 14.1356 10.6132 13.5758L4.24807 9.33231C3.78854 9.02595 3.66437 8.40509 3.97072 7.94556C4.04396 7.8357 4.13822 7.74144 4.24807 7.66821L10.6132 3.42476C11.453 2.86492 12.547 2.86492 13.3867 3.42476ZM20.0256 12.1922C19.8772 12.4296 19.6806 12.6332 19.4486 12.7899L13.3987 16.8736C12.5535 17.4441 11.4465 17.4441 10.6013 16.8736L4.55142 12.7899C3.79043 12.2762 3.49533 11.3306 3.77229 10.5003L10.6132 15.0598C11.4005 15.5847 12.4112 15.6175 13.2264 15.1582L13.3867 15.0598L20.2271 10.4998C20.4088 11.0459 20.3545 11.666 20.0256 12.1922ZM20.0256 15.4422C19.8772 15.6796 19.6806 15.8832 19.4486 16.0399L13.3987 20.1236C12.5535 20.6941 11.4465 20.6941 10.6013 20.1236L4.55142 16.0399C3.79043 15.5262 3.49533 14.5806 3.77229 13.7503L10.6132 18.3098C11.4005 18.8347 12.4112 18.8675 13.2264 18.4082L13.3867 18.3098L20.2271 13.7498C20.4088 14.2959 20.3545 14.916 20.0256 15.4422Z";

/// The glyph is authored on a 24x24 grid.
pub const GLYPH_VIEWBOX: f32 = 24.0;

/// `ic_fluent_power_20_filled` from microsoft/fluentui-system-icons, MIT.
/// See assets/NOTICE-fluentui.txt.
pub const POWER_PATH: &str = "M10.75 2.5C10.75 2.08579 10.4142 1.75 10 1.75C9.58579 1.75 9.25 2.08579 9.25 2.5V8.5C9.25 8.91421 9.58579 9.25 10 9.25C10.4142 9.25 10.75 8.91421 10.75 8.5V2.5ZM13.7432 4.00091C13.3843 3.79418 12.9257 3.91757 12.719 4.2765C12.5122 4.63544 12.6356 5.094 12.9946 5.30073C14.1393 5.96007 15.0345 6.9788 15.5412 8.19885C16.0478 9.4189 16.1377 10.7721 15.7968 12.0484C15.4559 13.3247 14.7032 14.4528 13.6557 15.2578C12.6081 16.0627 11.3242 16.4993 10.0031 16.5C8.68207 16.5007 7.3977 16.0654 6.3493 15.2616C5.30091 14.4578 4.54711 13.3304 4.20485 12.0545C3.8626 10.7785 3.95103 9.42523 4.45643 8.20465C4.96182 6.98407 5.85592 5.96441 7 5.30387C7.35872 5.09676 7.48163 4.63807 7.27452 4.27935C7.06742 3.92063 6.60872 3.79773 6.25 4.00483C4.8199 4.8305 3.70227 6.10508 3.07053 7.6308C2.43879 9.15653 2.32825 10.8481 2.75607 12.4431C3.18388 14.038 4.12613 15.4472 5.43663 16.452C6.74712 17.4567 8.35259 18.0009 10.0039 18C11.6553 17.9992 13.2602 17.4533 14.5696 16.4472C15.879 15.4411 16.8198 14.0309 17.246 12.4355C17.6721 10.8401 17.5598 9.14861 16.9265 7.62355C16.2931 6.09849 15.1742 4.82508 13.7432 4.00091Z";

/// The power glyph is authored on a 20x20 grid.
pub const POWER_VIEWBOX: f32 = 20.0;

use crate::compose::{center_ink, downsample, to_premultiplied_bgra};
use crate::geometry::Segment;
use crate::render::Renderer;
use windows::core::{Result, PCWSTR};
use windows::Win32::Graphics::Direct2D::Common::{
    D2D1_BEZIER_SEGMENT, D2D1_COLOR_F, D2D1_FIGURE_BEGIN_FILLED, D2D1_FIGURE_END_CLOSED,
    D2D1_FILL_MODE_WINDING, D2D_RECT_F,
};
use windows::Win32::Graphics::Direct2D::ID2D1RenderTarget;
use windows_numerics::Vector2;
use windows::Win32::Graphics::DirectWrite::{
    DWRITE_FONT_STRETCH_NORMAL, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_WEIGHT_BOLD,
    DWRITE_PARAGRAPH_ALIGNMENT_CENTER, DWRITE_TEXT_ALIGNMENT_CENTER,
};
use windows::Win32::Graphics::Gdi::{
    CreateBitmap, CreateDIBSection, DeleteObject, BITMAPINFO, BITMAPINFOHEADER, BI_RGB,
    DIB_RGB_COLORS,
};
use windows::Win32::UI::WindowsAndMessaging::{CreateIconIndirect, HICON, ICONINFO};

/// Supersampling factor. The digit needs it far more than the glyph does.
const SS: usize = 4;

/// Digit em size as a fraction of the icon box.
///
/// The digit gets the whole icon rather than a badge corner. A badge that
/// shared 16 physical pixels with the glyph left roughly five pixels of ink
/// for the number, which was not readable at 100% scaling.
const DIGIT_HEIGHT: f32 = 1.0;

const WHITE: D2D1_COLOR_F = D2D1_COLOR_F { r: 1.0, g: 1.0, b: 1.0, a: 1.0 };

/// Builds the tray icon at `size` logical pixels.
///
/// Layer 0 renders as the bare Fluent glyph, so the app still has an identity
/// at rest. Any other layer renders as that digit alone, filling the icon —
/// when a layer is active the number is the thing worth reading, and at 16px
/// there is not room for both.
pub fn build(
    r: &Renderer,
    badge: Option<u8>,
    dark_taskbar: bool,
    size: usize,
) -> Result<HICON> {
    let hi = size * SS;

    let coverage = match badge {
        None => r.render_alpha(hi, |rt| draw_glyph(rt, hi as f32))?,
        Some(n) => center_ink(&r.render_alpha(hi, |rt| draw_digit(r, rt, hi as f32, n))?),
    };

    let small = downsample(&coverage, SS);
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

/// The digit is drawn into the whole icon box, not a badge corner.
fn digit_rect(size: f32) -> D2D_RECT_F {
    D2D_RECT_F { left: 0.0, top: 0.0, right: size, bottom: size }
}

fn draw_digit(r: &Renderer, rt: &ID2D1RenderTarget, size: f32, n: u8) -> Result<()> {
    unsafe {
        let family: Vec<u16> = "Segoe UI Variable Display\0".encode_utf16().collect();
        let locale: Vec<u16> = "en-us\0".encode_utf16().collect();
        let format = r.dwrite().CreateTextFormat(
            PCWSTR(family.as_ptr()),
            None,
            // Bold rather than semibold: at 16 physical pixels the extra
            // stroke weight is the difference between legible and grey.
            DWRITE_FONT_WEIGHT_BOLD,
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
            &digit_rect(size),
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
