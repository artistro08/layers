//! Shared Direct2D, DirectWrite and WIC factories.
//!
//! UI-thread only. None of these objects are shared with the device thread.

use crate::compose::Alpha;
use windows::core::Result;
use windows::Win32::Graphics::Direct2D::Common::{
    D2D1_ALPHA_MODE_PREMULTIPLIED, D2D1_PIXEL_FORMAT,
};
use windows::Win32::Graphics::Direct2D::{
    D2D1CreateFactory, ID2D1Factory, ID2D1RenderTarget, D2D1_FACTORY_TYPE_SINGLE_THREADED,
    D2D1_RENDER_TARGET_PROPERTIES, D2D1_RENDER_TARGET_TYPE_DEFAULT,
};
use windows::Win32::Graphics::DirectWrite::{
    DWriteCreateFactory, IDWriteFactory, DWRITE_FACTORY_TYPE_SHARED,
};
use windows::Win32::Graphics::Dxgi::Common::DXGI_FORMAT_B8G8R8A8_UNORM;
use windows::Win32::Graphics::Imaging::{
    CLSID_WICImagingFactory, IWICImagingFactory, GUID_WICPixelFormat32bppPBGRA,
    WICBitmapCacheOnLoad, WICBitmapLockRead,
};
use windows::Win32::System::Com::{CoCreateInstance, CLSCTX_INPROC_SERVER};

pub struct Renderer {
    d2d: ID2D1Factory,
    dwrite: IDWriteFactory,
    wic: IWICImagingFactory,
}

impl Renderer {
    pub fn new() -> Result<Self> {
        unsafe {
            let d2d: ID2D1Factory =
                D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, None)?;
            let dwrite: IDWriteFactory = DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED)?;
            let wic: IWICImagingFactory =
                CoCreateInstance(&CLSID_WICImagingFactory, None, CLSCTX_INPROC_SERVER)?;
            Ok(Renderer { d2d, dwrite, wic })
        }
    }

    pub fn d2d(&self) -> &ID2D1Factory {
        &self.d2d
    }

    pub fn dwrite(&self) -> &IDWriteFactory {
        &self.dwrite
    }

    /// Draws white-on-transparent into a square WIC bitmap and returns the
    /// alpha channel. Everything the icon draws is monochrome, so only
    /// coverage matters; the color is applied later in `compose`.
    pub fn render_alpha(
        &self,
        size: usize,
        draw: impl FnOnce(&ID2D1RenderTarget) -> Result<()>,
    ) -> Result<Alpha> {
        unsafe {
            let bitmap = self.wic.CreateBitmap(
                size as u32,
                size as u32,
                &GUID_WICPixelFormat32bppPBGRA,
                WICBitmapCacheOnLoad,
            )?;

            let props = D2D1_RENDER_TARGET_PROPERTIES {
                r#type: D2D1_RENDER_TARGET_TYPE_DEFAULT,
                pixelFormat: D2D1_PIXEL_FORMAT {
                    format: DXGI_FORMAT_B8G8R8A8_UNORM,
                    alphaMode: D2D1_ALPHA_MODE_PREMULTIPLIED,
                },
                dpiX: 96.0,
                dpiY: 96.0,
                ..Default::default()
            };
            let rt = self.d2d.CreateWicBitmapRenderTarget(&bitmap, &props)?;

            rt.BeginDraw();
            rt.Clear(None);
            draw(&rt)?;
            rt.EndDraw(None, None)?;

            let lock = bitmap.Lock(std::ptr::null(), WICBitmapLockRead.0 as u32)?;
            let stride = lock.GetStride()? as usize;
            let mut len: u32 = 0;
            let mut data_ptr: *mut u8 = std::ptr::null_mut();
            lock.GetDataPointer(&mut len, &mut data_ptr)?;
            let bytes = std::slice::from_raw_parts(data_ptr, len as usize);

            let mut px = vec![0u8; size * size];
            for y in 0..size {
                for x in 0..size {
                    // Premultiplied BGRA: alpha is the fourth byte.
                    px[y * size + x] = bytes[y * stride + x * 4 + 3];
                }
            }
            Ok(Alpha { w: size, h: size, px })
        }
    }
}
