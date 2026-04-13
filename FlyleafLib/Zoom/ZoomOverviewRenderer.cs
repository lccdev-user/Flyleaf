using FlyleafLib.MediaPlayer;
using System;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.Wpf;
using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace FlyleafLib.Zoom
{
    /// <summary>
    /// Zoom-Overview Renderer — V4.
    ///
    /// Quelle: Direkt dekodierter Frame aus <c>VideoDecoder.Frames</c>
    ///   • HW-Frame (D3D11VA): NV12 Texture-Array → D3D11 VideoProcessor → BGRA
    ///   • SW-Frame           : BGRA Staging-Textur → direkt kopiert
    ///
    /// Vorteile gegenüber V3 (PreRenderTexture):
    ///   ✓ Kein Patch in Renderer.cs / Present() nötig
    ///   ✓ Kein CopyResource pro Frame im Render-Pfad
    ///   ✓ Kein KeyedMutex nötig
    ///   ✓ Immer ungezoomtes Originalbild (Decoder-Output)
    ///   ✓ Funktioniert unabhängig vom Zoom-Zustand
    ///
    /// Einschränkungen:
    ///   • TryPeek auf Decoder-Queue: kein Dequeuen, kein Timing-Einfluss
    ///   • NV12→BGRA per D3D11 VideoProcessor (GPU-seitig, kein CPU-Overhead)
    ///   • Bei sehr alten Treibern ohne VideoProcessor: SW-Fallback via
    ///     CopyResource wenn Format bereits BGRA ist
    /// </summary>
    public sealed class ZoomOverviewRenderer : IDisposable
    {
        // ── Public ────────────────────────────────────────────────────────────
        public bool IsInitialized { get; private set; }
        public int  ControlWidth     { get; private set; }
        public int  ControlHeight    { get; private set; }

        // ── D3D11 Pipeline ────────────────────────────────────────────────────
        private ID3D11Device          _device;
        private ID3D11DeviceContext   _context;
        private DecodedFrameSource    _frameSource;

        private ID3D11VertexShader    _vs;
        private ID3D11PixelShader     _ps;
        private ID3D11Buffer          _cbViewport;
        private ID3D11SamplerState    _sampler;
        private ID3D11RasterizerState _rasterizer;
        private ID3D11BlendState      _blend;

        private readonly Player       _player;
        private bool                  _disposed;
        internal LogHandler Log;

        // ── cbuffer (32 bytes) ────────────────────────────────────────────────
        [StructLayout(LayoutKind.Sequential, Size = 32)]
        private struct CbViewport
        {
            public float ViewX, ViewY, ViewW, ViewH;
            public float MapW, MapH;
            public float _pad0, _pad1;
        }

        // ── HLSL ─────────────────────────────────────────────────────────────
        // Vertex Shader: Full-Screen-Triangle ohne Vertex-Buffer
        private const string VSSrc = @"
struct VSOut { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; };
VSOut main(uint id:SV_VertexID)
{
    float2 uv  = float2((id<<1)&2, id&2);
    float4 pos = float4(uv*float2(2,-2)+float2(-1,1), 0, 1);
    VSOut o; o.pos=pos; o.uv=uv; return o;
}";

        // Pixel Shader:
        //   src = DecodedFrameSource.ConvertedSrv (BGRA, ungezoomt)
        //   viewRect = aktueller Zoom-Viewport im UV-Raum [0..1]
        //   Außerhalb des Viewports: 40% gedimmt
        //   Viewport-Rahmen: Amber-Highlight (2.5 px)
        private const string PSSrc = @"
Texture2D    src : register(t0);
SamplerState sam : register(s0);
cbuffer CB : register(b0)
{
    float4 viewRect;   // x y w h in UV [0..1]
    float2 mapSize;
    float2 _pad;
};
struct PSIn { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; };
float4 main(PSIn i) : SV_TARGET
{
    float4 c  = src.Sample(sam, i.uv);
    float2 uv = i.uv;

    bool inV = uv.x >= viewRect.x && uv.x <= (viewRect.x + viewRect.z)
            && uv.y >= viewRect.y && uv.y <= (viewRect.y + viewRect.w);

    float bw = 2.5 / mapSize.x;
    float bh = 2.5 / mapSize.y;
    bool border = inV && (uv.x < viewRect.x + bw
                       || uv.x > viewRect.x + viewRect.z - bw
                       || uv.y < viewRect.y + bh
                       || uv.y > viewRect.y + viewRect.w - bh);

    float3 res = border ? float3(1.0, 0.85, 0.15)   // Amber-Rahmen
               : inV   ? c.rgb                       // sichtbarer Bereich
                        : c.rgb * 0.40;              // außerhalb
    return float4(res, 1.0);
}";

        // ────────────────────────────────────────────────────────────────────
        public ZoomOverviewRenderer(Player player, int miniWidth = 256, int miniHeight = 144)
        {
            _player    = player ?? throw new ArgumentNullException(nameof(player));
            ControlWidth  = miniWidth;
            ControlHeight = miniHeight;

            var uniqueId =  GetUniqueId();
            Log = new(("[#" + uniqueId + "]").PadRight(8, ' ') + " [ZOVRenderer    ] ");
        }

        /// <summary>
        /// Initialisiert D3D11-Pipeline und DecodedFrameSource.
        /// Kein D3DImage, kein Renderer-Patch nötig.
        /// </summary>
        public void InitializeWithoutD3DImage(DrawingSurface surface)
        {
            if (IsInitialized) return;

            _device = surface.ColorTexture?.Device;
            if (_device is null)
                return;
            _context = _device.ImmediateContext;

            // DecodedFrameSource kapselt HW/SW Frame-Konvertierung
            //_frameSource = new DecodedFrameSource(_device, _player.VideoDecoder);

            CompileShaders();
            CreateConstantBuffer();
            CreateSamplerAndStates();

            IsInitialized = true;
        }

        // ── DrawingSurface Render-Callback ───────────────────────────────────
        /// <summary>
        /// Rendert die Minimap in das von DrawingSurface bereitgestellte Target.
        /// Holt den neuesten dekodierten Frame und zeichnet ihn mit Viewport-Rahmen.
        /// </summary>
        public void RenderIntoTexture(ID3D11Texture2D renderTarget, DrawEventArgs args)
        {
            if (!IsInitialized || _disposed || renderTarget == null) return;
            _device = renderTarget.Device;
            _context = _device.ImmediateContext;
            
            if (_frameSource is null)
                _frameSource = new DecodedFrameSource(_device, _player.VideoDecoder);

            ///_context = args.Device.ImmediateContext;

            // ── Neuesten dekodierten Frame holen und konvertieren ─────────────
            // TryUpdate: NV12 (HW) oder BGRA (SW) → _frameSource.ConvertedSrv
            bool hasFrame = _frameSource.TryUpdate();

            // Ersten Frame abwarten
            if (!hasFrame && !_frameSource.HasValidFrame) return;

            var srv = _frameSource.ConvertedSrv;
            if (srv == null) return;

            // ── In DrawingSurface-Target rendern ─────────────────────────────
            using var rtv    = _device.CreateRenderTargetView(renderTarget);
            var descTarget   = renderTarget.Description;
            var viewport     = new Viewport(0, 0, descTarget.Width, descTarget.Height, 0f, 1f);

            UpdateConstantBuffer();
            Log.Debug($"viewport : {viewport}");
            _context.RSSetViewports(new[] { viewport });
            _context.RSSetState(_rasterizer);
            _context.OMSetBlendState(_blend);
            _context.OMSetRenderTargets(rtv);
            _context.ClearRenderTargetView(rtv, new Color4(15f, 64f, 7f, 1f));

            _context.VSSetShader(_vs);
            _context.PSSetShader(_ps);
            _context.PSSetShaderResource(0, srv);
            _context.PSSetSampler(0, _sampler);
            _context.PSSetConstantBuffer(0, _cbViewport);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.Draw(3, 0);

            _context.OMSetRenderTargets((ID3D11RenderTargetView)null);
            _context.PSSetShaderResource(0, null);
        }

        // ── cbuffer: Viewport-Rect aus Zoom/Pan ──────────────────────────────
        private void UpdateConstantBuffer()
        {   
            var cfg    = _player.Config.Video;
            float zoom = Math.Min(0.01f, (float)cfg.Zoom);
            float panX = 0.0F; // (float)cfg.PanXOffset;
            float panY = 0.0F; // (float)cfg.PanYOffset;

            float w = 1f; // / zoom;
            float h = 1f; // / zoom;
            float x = Math.Clamp(0.5f + panX * 0.5f - w * 0.5f, 0f, 1f - w);
            float y = Math.Clamp(0.5f + panY * 0.5f - h * 0.5f, 0f, 1f - h);

            var cb = new CbViewport
            {
                ViewX = x, ViewY = y, ViewW = w, ViewH = h,
                MapW  = ControlWidth, MapH = ControlHeight
            };
            Log.Debug($"cbViewPort : {cb}, zoom {zoom}, panX {panX}, panY {panY}");
            var mapped = _context.Map(_cbViewport, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            Marshal.StructureToPtr(cb, mapped.DataPointer, false);
            _context.Unmap(_cbViewport, 0);
            
        }

        // ── Pipeline-Setup ───────────────────────────────────────────────────
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
                ByteWidth = (uint)Marshal.SizeOf<CbViewport>(),
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

        // ── IDisposable ──────────────────────────────────────────────────────
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _frameSource?.Dispose();
            _cbViewport?.Dispose();
            _sampler?.Dispose();
            _rasterizer?.Dispose();
            _blend?.Dispose();
            _vs?.Dispose();
            _ps?.Dispose();
        }

        internal void UpdateSize(int actualWidth, int actualHeight)
        {
            ControlWidth = actualWidth;
            ControlHeight = actualHeight;
        }
    }
}
