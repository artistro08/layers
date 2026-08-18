//! DirectComposition surface for one window.
//!
//! D3D11 device -> DXGI composition swapchain -> DirectComposition visual
//! tree -> a D2D device context bound to the swapchain's backbuffer. This is
//! what lets DWM draw a real acrylic backdrop, rounded corners and a shadow
//! around content we paint on a transparent background, instead of us
//! faking all three into a layered-window bitmap.

use crate::render::Renderer;
use windows::core::{Interface, Result};
use windows::Win32::Foundation::HWND;
use windows::Win32::Graphics::Direct2D::Common::{
    D2D1_ALPHA_MODE_PREMULTIPLIED, D2D1_COLOR_F, D2D1_PIXEL_FORMAT,
};
use windows::Win32::Graphics::Direct2D::{
    ID2D1Device, ID2D1DeviceContext, D2D1_BITMAP_OPTIONS_CANNOT_DRAW, D2D1_BITMAP_OPTIONS_TARGET,
    D2D1_BITMAP_PROPERTIES1, D2D1_DEVICE_CONTEXT_OPTIONS_NONE,
};
use windows::Win32::Graphics::Direct3D::{D3D_DRIVER_TYPE, D3D_DRIVER_TYPE_HARDWARE, D3D_DRIVER_TYPE_WARP};
use windows::Win32::Graphics::Direct3D11::{
    D3D11CreateDevice, ID3D11Device, D3D11_CREATE_DEVICE_BGRA_SUPPORT, D3D11_SDK_VERSION,
};
use windows::Win32::Graphics::DirectComposition::{
    DCompositionCreateDevice, IDCompositionDevice, IDCompositionTarget, IDCompositionVisual,
};
use windows::Win32::Graphics::Dxgi::Common::{
    DXGI_ALPHA_MODE_PREMULTIPLIED, DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_SAMPLE_DESC,
};
use windows::Win32::Graphics::Dxgi::{
    IDXGIDevice, IDXGIFactory2, IDXGISurface, IDXGISwapChain1, DXGI_SWAP_CHAIN_DESC1,
    DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL, DXGI_USAGE_RENDER_TARGET_OUTPUT,
};

fn create_d3d_device(driver: D3D_DRIVER_TYPE) -> Result<ID3D11Device> {
    let mut device: Option<ID3D11Device> = None;
    unsafe {
        D3D11CreateDevice(
            None,
            driver,
            Default::default(),
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            None,
            D3D11_SDK_VERSION,
            Some(&mut device),
            None,
            None,
        )?;
    }
    device.ok_or_else(|| windows::core::Error::from_hresult(windows::Win32::Foundation::E_FAIL))
}

pub struct Composition {
    dc: ID2D1DeviceContext,
    swapchain: IDXGISwapChain1,
    dcomp: IDCompositionDevice,
    // Kept alive for the lifetime of the composition tree; never read again
    // after `new`.
    _target: IDCompositionTarget,
    _visual: IDCompositionVisual,
}

impl Composition {
    /// Builds the pipeline for `hwnd` at the given pixel size.
    pub fn new(r: &Renderer, hwnd: HWND, w: u32, h: u32) -> Result<Self> {
        let d3d_device = create_d3d_device(D3D_DRIVER_TYPE_HARDWARE)
            .or_else(|_| create_d3d_device(D3D_DRIVER_TYPE_WARP))?;
        let dxgi_device: IDXGIDevice = d3d_device.cast()?;
        let adapter = unsafe { dxgi_device.GetAdapter()? };
        let factory: IDXGIFactory2 = unsafe { adapter.GetParent()? };

        let desc = DXGI_SWAP_CHAIN_DESC1 {
            Width: w.max(1),
            Height: h.max(1),
            Format: DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc: DXGI_SAMPLE_DESC { Count: 1, Quality: 0 },
            BufferUsage: DXGI_USAGE_RENDER_TARGET_OUTPUT,
            BufferCount: 2,
            SwapEffect: DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL,
            AlphaMode: DXGI_ALPHA_MODE_PREMULTIPLIED,
            ..Default::default()
        };
        let swapchain =
            unsafe { factory.CreateSwapChainForComposition(&d3d_device, &desc, None)? };

        let dcomp: IDCompositionDevice = unsafe { DCompositionCreateDevice(&dxgi_device)? };
        let target = unsafe { dcomp.CreateTargetForHwnd(hwnd, true)? };
        let visual = unsafe { dcomp.CreateVisual()? };
        unsafe {
            visual.SetContent(&swapchain)?;
            target.SetRoot(&visual)?;
            dcomp.Commit()?;
        }

        let d2d_device: ID2D1Device = unsafe { r.d2d_factory1().CreateDevice(&dxgi_device)? };
        let dc = unsafe { d2d_device.CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE)? };

        bind_backbuffer(&dc, &swapchain)?;

        Ok(Composition { dc, swapchain, dcomp, _target: target, _visual: visual })
    }

    /// Resizes the swapchain. Must release the D2D target bitmap first.
    pub fn resize(&mut self, w: u32, h: u32) -> Result<()> {
        unsafe {
            self.dc.SetTarget(None);
        }
        unsafe {
            self.swapchain.ResizeBuffers(
                2,
                w.max(1),
                h.max(1),
                DXGI_FORMAT_B8G8R8A8_UNORM,
                Default::default(),
            )?;
        }
        bind_backbuffer(&self.dc, &self.swapchain)
    }

    /// Runs `draw` against a device context already bound to the backbuffer,
    /// cleared to transparent, then presents and commits.
    pub fn draw(&mut self, f: impl FnOnce(&ID2D1DeviceContext) -> Result<()>) -> Result<()> {
        const TRANSPARENT: D2D1_COLOR_F = D2D1_COLOR_F { r: 0.0, g: 0.0, b: 0.0, a: 0.0 };
        unsafe {
            self.dc.BeginDraw();
            self.dc.Clear(Some(&TRANSPARENT));
        }
        f(&self.dc)?;
        unsafe {
            self.dc.EndDraw(None, None)?;
            self.swapchain.Present(1, Default::default()).ok()?;
            self.dcomp.Commit()?;
        }
        Ok(())
    }
}

fn bind_backbuffer(dc: &ID2D1DeviceContext, swapchain: &IDXGISwapChain1) -> Result<()> {
    let surface: IDXGISurface = unsafe { swapchain.GetBuffer(0)? };
    let props = D2D1_BITMAP_PROPERTIES1 {
        pixelFormat: D2D1_PIXEL_FORMAT {
            format: DXGI_FORMAT_B8G8R8A8_UNORM,
            alphaMode: D2D1_ALPHA_MODE_PREMULTIPLIED,
        },
        dpiX: 96.0,
        dpiY: 96.0,
        bitmapOptions: D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
        ..Default::default()
    };
    let bitmap = unsafe { dc.CreateBitmapFromDxgiSurface(&surface, Some(&props))? };
    unsafe { dc.SetTarget(&bitmap) };
    Ok(())
}
