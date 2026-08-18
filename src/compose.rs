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
}
