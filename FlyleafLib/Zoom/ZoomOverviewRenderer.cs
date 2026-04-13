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

        // Shared source: main renderer's back buffer
        private ID3D11Texture2D          _sharedTex;
        private ID3D11ShaderResourceView _sharedSrv;
        private IntPtr                   _lastHandle = IntPtr.Zero;

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
        private const string PSSrc = @"
Texture2D    src : register(t0);
SamplerState sam : register(s0);
struct PSIn { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; };
float4 main(PSIn i) : SV_TARGET
{  
    return src.Sample(sam, i.uv);
}";

        // ────────────────────────────────────────────────────────────────────
        public ZoomOverviewRenderer(Player player, int miniWidth = 256, int miniHeight = 144)
        {
            _player    = player ?? throw new ArgumentNullException(nameof(player));
            ControlWidth  = miniWidth;
            ControlHeight = miniHeight;

            if (_frameSource is null)
                _frameSource = new DecodedFrameSource(_player.Renderer.Device, _player.VideoDecoder);


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

            CompileShaders();
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
            
            
            ///_context = args.Device.ImmediateContext;

            // ── Neuesten dekodierten Frame holen und konvertieren ─────────────
            // TryUpdate: NV12 (HW) oder BGRA (SW) → _frameSource.ConvertedSrv
            // bool hasFrame = _frameSource.TryUpdate();

            // Ersten Frame abwarten
            // if (!hasFrame && !_frameSource.HasValidFrame) return;

            /*
            var srv = _frameSource.ConvertedSrv;
            if (srv == null) return;
            */
            IntPtr handle = _frameSource.SharedTextureHandle;

            OpenSharedIfNeeded(handle, args.Device);
            if (_sharedSrv == null)
                return;

            // ── In DrawingSurface-Target rendern ─────────────────────────────
            using var rtv    = _device.CreateRenderTargetView(renderTarget);
            var descTarget   = renderTarget.Description;
            var viewport     = new Viewport(0, 0, descTarget.Width, descTarget.Height, 0f, 1f);

            Log.Debug($"viewport : {viewport}");
            _context.RSSetViewports(new[] { viewport });
            _context.RSSetState(_rasterizer);
            _context.OMSetBlendState(_blend);
            _context.OMSetRenderTargets(rtv);
            _context.ClearRenderTargetView(rtv, new Color4(15f, 64f, 7f, 1f));

            _context.VSSetShader(_vs);
            _context.PSSetShader(_ps);
            _context.PSSetShaderResource(0, _sharedSrv);
            _context.PSSetSampler(0, _sampler);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.Draw(3, 0);

            _context.OMSetRenderTargets((ID3D11RenderTargetView)null);
            _context.PSSetShaderResource(0, null);
        }

        // ── Pipeline-Setup ───────────────────────────────────────────────────
        private void CompileShaders()
        {
            var vsBlob = Compiler.Compile(VSSrc, "main", "vs", "vs_5_0");
            _vs = _device.CreateVertexShader(vsBlob.Span);

            var psBlob = Compiler.Compile(PSSrc, "main", "ps", "ps_5_0");
            _ps = _device.CreatePixelShader(psBlob.Span);
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

        // ── Shared texture management ────────────────────────────────────────
        private void OpenSharedIfNeeded(IntPtr handle, ID3D11Device device)
        {
            if (handle == _lastHandle)
                return;

            _sharedSrv?.Dispose();
            _sharedSrv = null;
            _sharedTex?.Dispose();
            _sharedTex = null;

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
                        Format = _sharedTex.Description.Format,
                        ViewDimension = ShaderResourceViewDimension.Texture2D,
                        Texture2D = new Texture2DShaderResourceView { MipLevels = 1 }
                    });

                _lastHandle = handle;
            }
            catch (Exception ex)
            {
                Log.Error($"[ZoomOverview] OpenSharedResource1 failed: {ex.Message}");
            }
        }


        // ── IDisposable ──────────────────────────────────────────────────────
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _frameSource?.Dispose();
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
