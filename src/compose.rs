//! Alpha-coverage compositing for the tray icon.
//!
//! The icon is monochrome, so everything is done as single-channel coverage
//! and tinted at the very end. This replaces the Direct2D composite modes the
//! design doc originally called for, which would have required a full
//! ID2D1DeviceContext and therefore a D3D11 device.

/// A single-channel coverage buffer, one byte per pixel, row major.
#[derive(Debug, Clone, PartialEq)]
pub struct Alpha {
    pub w: usize,
    pub h: usize,
    pub px: Vec<u8>,
}

impl Alpha {
    pub fn new(w: usize, h: usize) -> Self {
        Alpha { w, h, px: vec![0; w * h] }
    }
}

/// Box-filter downsample by an integer factor. Rendering at 4x and reducing
/// gives the digit clean edges at 16 pixels.
///
/// # Panics
/// If `factor` is zero or does not divide both dimensions.
pub fn downsample(src: &Alpha, factor: usize) -> Alpha {
    assert!(factor > 0, "factor must be positive");
    assert!(
        src.w % factor == 0 && src.h % factor == 0,
        "factor must divide both dimensions"
    );
    let (w, h) = (src.w / factor, src.h / factor);
    let mut px = vec![0u8; w * h];
    let n = (factor * factor) as u32;
    for y in 0..h {
        for x in 0..w {
            let mut sum = 0u32;
            for dy in 0..factor {
                for dx in 0..factor {
                    sum += src.px[(y * factor + dy) * src.w + x * factor + dx] as u32;
                }
            }
            px[y * w + x] = (sum / n) as u8;
        }
    }
    Alpha { w, h, px }
}

/// Expands coverage into the premultiplied BGRA that CreateDIBSection and
/// CreateIconIndirect expect.
pub fn to_premultiplied_bgra(a: &Alpha, rgb: (u8, u8, u8)) -> Vec<u8> {
    let (r, g, b) = rgb;
    let mut out = Vec::with_capacity(a.px.len() * 4);
    for &cov in &a.px {
        let m = |c: u8| (c as u32 * cov as u32 / 255) as u8;
        out.extend_from_slice(&[m(b), m(g), m(r), cov]);
    }
    out
}

/// Shifts the buffer so its non-zero coverage sits centred in the bounds.
///
/// DirectWrite's paragraph centring centres the *line box* — ascent plus
/// descent — not the ink. Digits have no descender, so a "centred" digit
/// renders visibly low. Measuring what actually got rasterised and shifting
/// it is font-independent and exact, where a hardcoded metrics fudge is
/// neither.
///
/// A buffer with no coverage at all is returned unchanged.
pub fn center_ink(a: &Alpha) -> Alpha {
    let (mut min_x, mut min_y) = (usize::MAX, usize::MAX);
    let (mut max_x, mut max_y) = (0usize, 0usize);
    for y in 0..a.h {
        for x in 0..a.w {
            if a.px[y * a.w + x] != 0 {
                min_x = min_x.min(x);
                max_x = max_x.max(x);
                min_y = min_y.min(y);
                max_y = max_y.max(y);
            }
        }
    }
    if min_x == usize::MAX {
        return a.clone();
    }

    let dx = (a.w as isize - 1 - max_x as isize - min_x as isize) / 2;
    let dy = (a.h as isize - 1 - max_y as isize - min_y as isize) / 2;
    if dx == 0 && dy == 0 {
        return a.clone();
    }

    let mut out = Alpha::new(a.w, a.h);
    for y in 0..a.h {
        let sy = y as isize - dy;
        if sy < 0 || sy >= a.h as isize {
            continue;
        }
        for x in 0..a.w {
            let sx = x as isize - dx;
            if sx < 0 || sx >= a.w as isize {
                continue;
            }
            out.px[y * a.w + x] = a.px[sy as usize * a.w + sx as usize];
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    fn filled(w: usize, h: usize, v: u8) -> Alpha {
        Alpha { w, h, px: vec![v; w * h] }
    }

    #[test]
    fn downsampling_averages_each_source_block() {
        let src = Alpha { w: 2, h: 2, px: vec![0, 100, 200, 255] };
        let out = downsample(&src, 2);
        assert_eq!(out.w, 1);
        assert_eq!(out.h, 1);
        assert_eq!(out.px[0], ((0 + 100 + 200 + 255) / 4) as u8);
    }

    #[test]
    fn downsampling_preserves_a_uniform_field_exactly() {
        let out = downsample(&filled(8, 8, 173), 4);
        assert_eq!(out.w, 2);
        assert_eq!(out.px, vec![173; 4]);
    }

    #[test]
    fn a_factor_of_one_is_a_passthrough() {
        let src = filled(3, 3, 42);
        assert_eq!(downsample(&src, 1).px, src.px);
    }

    #[test]
    fn bgra_output_is_premultiplied_and_channel_ordered() {
        let out = to_premultiplied_bgra(&filled(1, 1, 255), (0x12, 0x34, 0x56));
        assert_eq!(out, vec![0x56, 0x34, 0x12, 0xFF]);
    }

    #[test]
    fn half_alpha_halves_every_color_channel() {
        let out = to_premultiplied_bgra(&filled(1, 1, 128), (255, 255, 255));
        assert_eq!(out[3], 128);
        assert_eq!(out[0], 128);
        assert_eq!(out[1], 128);
        assert_eq!(out[2], 128);
    }

    #[test]
    fn fully_transparent_pixels_carry_no_color() {
        let out = to_premultiplied_bgra(&Alpha::new(1, 1), (255, 255, 255));
        assert_eq!(out, vec![0, 0, 0, 0]);
    }

    #[test]
    fn output_length_is_four_bytes_per_pixel() {
        assert_eq!(to_premultiplied_bgra(&Alpha::new(4, 3), (1, 2, 3)).len(), 48);
    }

    /// Builds a buffer with a single lit pixel at (x, y).
    fn dot(w: usize, h: usize, x: usize, y: usize) -> Alpha {
        let mut a = Alpha::new(w, h);
        a.px[y * w + x] = 255;
        a
    }

    #[test]
    fn ink_sitting_low_is_lifted_to_the_middle() {
        // The DirectWrite failure mode: ink one row below centre.
        let out = center_ink(&dot(5, 5, 2, 3));
        assert_eq!(out.px[2 * 5 + 2], 255, "should land on the centre row");
        assert!(out.px.iter().filter(|&&v| v != 0).count() == 1);
    }

    #[test]
    fn ink_sitting_high_is_pushed_down() {
        let out = center_ink(&dot(5, 5, 2, 0));
        assert_eq!(out.px[2 * 5 + 2], 255);
    }

    #[test]
    fn already_centred_ink_is_left_alone() {
        let src = dot(5, 5, 2, 2);
        assert_eq!(center_ink(&src), src);
    }

    #[test]
    fn horizontal_offset_is_corrected_too() {
        let out = center_ink(&dot(5, 5, 0, 2));
        assert_eq!(out.px[2 * 5 + 2], 255);
    }

    #[test]
    fn a_wide_ink_block_is_centred_by_its_bounds_not_its_mass() {
        // Lit at columns 0..=2 of a 5-wide row: bounds centre is 1, so the
        // block shifts right by one regardless of where the weight sits.
        let mut a = Alpha::new(5, 1);
        a.px[0] = 255;
        a.px[1] = 255;
        a.px[2] = 255;
        let out = center_ink(&a);
        assert_eq!(out.px, vec![0, 255, 255, 255, 0]);
    }

    #[test]
    fn an_empty_buffer_is_returned_unchanged() {
        let src = Alpha::new(4, 4);
        assert_eq!(center_ink(&src), src);
    }
}
