using FlyleafLib.MediaFramework.MediaRenderer;
using FlyleafLib.MediaPlayer;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;
using Vortice.Wpf;
using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace FlyleafLib.Zoom
{
    /// <summary>
    /// Zoom-Overview Child-Renderer mit Vortice.Wpf DrawingSurface.
    ///
    ///   InitializeWithoutD3DImage() erstellt die D3D11-Pipeline.
    ///   RenderIntoTexture(target) rendert in das von DrawingSurface
    ///   bereitgestellte Render-Target (ID3D11Texture2D).
    ///
    /// DrawingSurface (Vortice.Wpf) übernimmt:
    ///   * D3D9Ex-Device-Erstellung
    ///   * Shared Surface zwischen D3D11 und D3D9
    ///   * D3DImage Lock/Unlock/AddDirtyRect
    ///   * Resize-Handling
    /// </summary>
    public sealed class ZoomOverviewRenderer : IDisposable, IVP
    {
        public VPConfig VideoConfig => ucfg;
        VPConfig ucfg;
        public bool IsInitialized { get; private set; }
        public int  MiniWidth     { get; }
        public int  MiniHeight    { get; }

        public Viewport Viewport { get; private set; }
        public int ControlWidth { get; private set; }
        public int ControlHeight { get; private set; }
        public int SideXPixels => sideXPixels;
        public int SideYPixels => sideYPixels;

        int  sideXPixels, sideYPixels;


        void IVP.MonitorChanged(GPUOutput monitor) { }

        private void SetViewport(int width, int height)
        {
            int x, y, newWidth, newHeight, xZoomPixels, yZoomPixels;

            var curRatio = (double) width / height;
            var fillRatio = (double) ControlWidth/ControlHeight;

            if (curRatio < fillRatio)
            {
                newHeight = (int)(height * ucfg.zoom);
                newWidth = (int)(newHeight * curRatio);

                sideXPixels = ((int)(width - (height * curRatio))) & ~1;
                sideYPixels = 0;

                y = (int)(height * ucfg.panYOffset);
                x = (int)(width * ucfg.panXOffset) + (sideXPixels / 2);

                yZoomPixels = newHeight - height;
                xZoomPixels = newWidth - (width - sideXPixels);
            }
            else
            {
                newWidth = (int)(width * ucfg.zoom);
                newHeight = (int)(newWidth / curRatio);

                sideYPixels = ((int)(height - (width / curRatio))) & ~1;
                sideXPixels = 0;

                x = (int)(width * ucfg.panXOffset);
                y = (int)(height * ucfg.panYOffset) + (sideYPixels / 2);

                xZoomPixels = newWidth - width;
                yZoomPixels = newHeight - (height - sideYPixels);
            }

            Viewport = new((int)(x - xZoomPixels * (float)ucfg.zoomCenter.X), (int)(y - yZoomPixels * (float)ucfg.zoomCenter.Y), newWidth, newHeight);
        }
        public void UpdateSize(int width, int height)
        {   // TBR
            ControlWidth = width;
            ControlHeight = height;
        }

        internal LogHandler Log;
        // D3D11 pipeline
        private ID3D11Device             _device;
        private ID3D11DeviceContext      _context;

        // Shared source: main renderer's back buffer
        private ID3D11Texture2D          _sharedTex;
        private ID3D11ShaderResourceView _sharedSrv;
        private IntPtr                   _lastHandle = IntPtr.Zero;

        // Pipeline objects
        private ID3D11VertexShader       _vs;
        private ID3D11PixelShader        _ps;
        private ID3D11Buffer             _cbViewport;
        private ID3D11SamplerState       _sampler;
        private ID3D11RasterizerState    _rasterizer;
        private ID3D11BlendState         _blend;

        private readonly Player          _player;
        private bool                     _disposed;

        //  cbuffer (32 bytes, 16-byte-aligned)
        [StructLayout(LayoutKind.Sequential, Size = 32)]
        private struct CbViewport
        {
            public float ViewX, ViewY, ViewW, ViewH;
            public float MapW, MapH;
            public float _pad0, _pad1;
        }

        // ********** HLSL *************************
        private const string VSSrc = @"
struct VSOut { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; };
VSOut main(uint id:SV_VertexID)
{
    float2 uv  = float2((id<<1)&2, id&2);
    float4 pos = float4(uv*float2(2,-2)+float2(-1,1), 0, 1);
    VSOut o; o.pos=pos; o.uv=uv; return o;
}";

        private const string PSSrc = @"
Texture2D    src : register(t0);
SamplerState sam : register(s0);
cbuffer CB      : register(b0)
{
    float4 viewRect;   // x y w h  in UV [0..1]
    float2 mapSize;
    float2 _pad;
};
struct PSIn { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; };
float4 main(PSIn i) : SV_TARGET
{
    float4 c   = src.Sample(sam, i.uv);
    float2 uv  = i.uv;
    bool inV   = uv.x>=viewRect.x && uv.x<=(viewRect.x+viewRect.z)
              && uv.y>=viewRect.y && uv.y<=(viewRect.y+viewRect.w);
    float bw   = 2.5/mapSize.x, bh=2.5/mapSize.y;
    bool border= inV && (uv.x<viewRect.x+bw || uv.x>viewRect.x+viewRect.z-bw
                       ||uv.y<viewRect.y+bh || uv.y>viewRect.y+viewRect.w-bh);
    float3 res = border ? float3(1,.85,.15)
               : inV   ? c.rgb
                        : c.rgb*0.40;
    return float4(res,1);
}";

        // HLSL end *******************************
        public ZoomOverviewRenderer(Player player, int miniWidth = 256, int miniHeight = 144)
        {
            _player    = player ?? throw new ArgumentNullException(nameof(player));
            MiniWidth  = miniWidth;
            MiniHeight = miniHeight;
            var uniqueId =  GetUniqueId();
            Log = new(("[#" + uniqueId + "]").PadRight(8, ' ') + " [ZOVRenderer    ] ");
        }

        /// <summary>
        /// Init ohne D3DImage — wird von ZoomOverlayControl aufgerufen.
        /// DrawingSurface liefert den Render-Target direkt per Callback.
        /// </summary>
        public void InitializeWithoutD3DImage(DrawingSurface surface)
        {
            if (IsInitialized) return;

            // Reuse main renderer device
            _device  = surface.ColorTexture?.Device;
            if (_device is null)
                return;
            _context = _device.ImmediateContext;

            CompileShaders();
            CreateConstantBuffer();
            CreateSamplerAndStates();

            IsInitialized = true;
        }

        // explicitly not implemented
        SwapChain IVP.SwapChain => throw new NotImplementedException();

        // *** Compile shaders ***
        private void CompileShaders()
        {
            var vsBlob = Compiler.Compile(VSSrc, "main", "vs", "vs_5_0");
            _vs = _device.CreateVertexShader(vsBlob.Span);

            var psBlob = Compiler.Compile(PSSrc, "main", "ps", "ps_5_0");
            _ps = _device.CreatePixelShader(psBlob.Span);
        }

        private void CreateConstantBuffer()
        {
            _cbViewport = _device.CreateBuffer(new BufferDescription
            {
                ByteWidth      = (uint)Marshal.SizeOf<CbViewport>(),
                Usage          = ResourceUsage.Dynamic,
                BindFlags      = BindFlags.ConstantBuffer,
                CPUAccessFlags = CpuAccessFlags.Write
            });
        }

        private void CreateSamplerAndStates()
        {
            _sampler = _device.CreateSamplerState(new SamplerDescription
            {
                Filter   = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp
            });
            _rasterizer = _device.CreateRasterizerState(RasterizerDescription.CullNone);
            _blend      = _device.CreateBlendState(BlendDescription.Opaque);
        }

        // *** Render into DrawingSurface target ***
        /// <summary>
        /// Rendert die Minimap in das von DrawingSurface.OnRender gelieferte
        /// <paramref name="renderTarget"/>. Das Texture-Format ist durch
        /// Vortice.Wpf vorgegeben (typisch B8G8R8A8_UNorm).
        ///
        /// Dieser Aufruf erfolgt auf dem UI-Thread, da DrawingSurface.OnRender
        /// auf dem WPF-Dispatcher-Thread läuft.
        /// </summary>
        public void RenderIntoTexture(ID3D11Texture2D renderTarget, DrawEventArgs args)
        {
            if (!IsInitialized || _disposed || renderTarget == null) return;

            var mainRenderer = _player?.VideoDecoder?.Renderer;
            if (mainRenderer is null)  return;

            IntPtr handle = mainRenderer.SharedTextureHandle;

            OpenSharedIfNeeded(handle, args.Device);
            if (_sharedSrv == null) return;

            var rtv = args.Surface.ColorTextureView;

            // Viewport matching the target texture
            var desc     = renderTarget.Description;
            var viewport = new Viewport(0, 0, desc.Width, desc.Height, 0f, 1f);
            Log.Debug($"RenderIntoTexture: viewport {viewport}, contrSize {ControlWidth}x{ControlHeight}, surfaceSize {args.Surface.ActualWidth}x{args.Surface.ActualHeight}");

            var context = args.Context;
            UpdateConstantBuffer(context);

            context.RSSetViewports(new[] { viewport });
            context.RSSetState(_rasterizer);
            context.OMSetBlendState(_blend);
            context.OMSetRenderTargets(rtv);
            context.ClearRenderTargetView(rtv, new Color4(57f, 100f, 0f, 1f));

            context.VSSetShader(_vs);
            context.PSSetShader(_ps);
            context.PSSetShaderResource(0, _sharedSrv);
            context.PSSetSampler(0, _sampler);
            context.PSSetConstantBuffer(0, _cbViewport);
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            context.Draw(3, 0);
        }

        // ── cbuffer update ───────────────────────────────────────────────────
        private void UpdateConstantBuffer(ID3D11DeviceContext1 context)
        {
            if (context  is null) return;

            var cfg   = _player.Config.Video;
            // float zoom = Math.Min(0.01f, (float)cfg.Zoom);
            float panX = 0.0F; // (float)cfg.PanXOffset;
            float panY = 0.0F; // (float)cfg.PanYOffset;

            float w = 1f; // / zoom;
            float h = 1f; // / zoom;
            float x = Math.Clamp(0.5f + panX * 0.5f - w * 0.5f, 0f, 1f - w);
            float y = Math.Clamp(0.5f + panY * 0.5f - h * 0.5f, 0f, 1f - h);

            var cb = new CbViewport
            {
                ViewX = x, ViewY = y, ViewW = w, ViewH = h,
                MapW  = MiniWidth, MapH = MiniHeight
            };

            var mapped = context.Map(_cbViewport, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            Marshal.StructureToPtr(cb, mapped.DataPointer, false);
            context.Unmap(_cbViewport, 0);

        }

        // ── Shared texture management ────────────────────────────────────────
        private void OpenSharedIfNeeded(IntPtr handle, ID3D11Device device)
        {
            if (handle == _lastHandle) return;

            _sharedSrv?.Dispose(); _sharedSrv = null;
            _sharedTex?.Dispose(); _sharedTex = null;

            try
            {
                // Nur für NT_SHARED_HANDLE
               // using var dev1 = _device.QueryInterface<ID3D11Device1>();
               // _sharedTex = dev1.OpenSharedResource1<ID3D11Texture2D>(handle);
               _sharedTex = device.OpenSharedResource<ID3D11Texture2D>(handle);
                Log.Debug($"_sharedTex {_sharedTex.Description.Width}x{_sharedTex.Description.Height}");
                _sharedSrv = device.CreateShaderResourceView(_sharedTex,
                    new ShaderResourceViewDescription
                    {
                        Format        = _sharedTex.Description.Format,
                        ViewDimension = ShaderResourceViewDimension.Texture2D,
                        Texture2D     = new Texture2DShaderResourceView { MipLevels = 1 }
                    });

                _lastHandle = handle;
            }
            catch (Exception ex)
            {
                Log.Error($"[ZoomOverview] OpenSharedResource1 failed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _sharedSrv?.Dispose();
            _sharedTex?.Dispose();
            _cbViewport?.Dispose();
            _sampler?.Dispose();
            _rasterizer?.Dispose();
            _blend?.Dispose();
            _vs?.Dispose();
            _ps?.Dispose();
        }

        void IVP.VPRequest(VPRequestType request) => throw new NotImplementedException();
    }
}
