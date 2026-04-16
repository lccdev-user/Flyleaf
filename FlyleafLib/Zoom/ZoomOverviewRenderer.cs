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
    /// Zoom-Overview Renderer
    /// </summary>
    public sealed class ZoomOverviewRenderer : IDisposable
    {
        public bool IsInitialized { get; private set; }
        public int  ControlWidth     { get; private set; }
        public int  ControlHeight    { get; private set; }

        // Shared source
        private ID3D11Texture2D          _sharedTex;
        private ID3D11ShaderResourceView _sharedSrv;
        private IntPtr                   _lastHandle = IntPtr.Zero;

        // D3D11 Pipeline
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

        // HLSL
        // Vertex Shader: Full-screen triangle without vertex buffer
        private const string VSSrc = @"
struct VSOut { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; };
VSOut main(uint id:SV_VertexID)
{
    float2 uv  = float2((id<<1)&2, id&2);
    float4 pos = float4(uv*float2(2,-2)+float2(-1,1), 0, 1);
    VSOut o; o.pos=pos; o.uv=uv; return o;
}";

        // Pixel Shader      
        private const string PSSrc = @"
Texture2D    src : register(t0);
SamplerState sam : register(s0);
struct PSIn { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; };
float4 main(PSIn i) : SV_TARGET
{  
    return src.Sample(sam, i.uv);
}";

        public ZoomOverviewRenderer(Player player, int miniWidth = 256, int miniHeight = 144)
        {
            _player    = player ?? throw new ArgumentNullException(nameof(player));
            ControlWidth  = miniWidth;
            ControlHeight = miniHeight;

            var uniqueId =  GetUniqueId();
            Log = new(("[#" + uniqueId + "]").PadRight(8, ' ') + " [ZOVRenderer    ] ");
        }

        /// <summary>
        /// Initializes D3D11 pipeline and DecodedFrameSource.
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

            if (_frameSource is null)
                _frameSource = new DecodedFrameSource(_player.Renderer.Device, _player.VideoDecoder);

            IsInitialized = true;
        }

        // DrawingSurface Render-Callback
        /// <summary>
        /// Renders the frame to the target provided by DrawingSurface.
        /// </summary>
        public void RenderIntoTexture(ID3D11Texture2D renderTarget, DrawEventArgs args)
        {
            if (!IsInitialized || _disposed || renderTarget == null) return;
            _device = renderTarget.Device;
            _context = _device.ImmediateContext;
            
            IntPtr handle = _frameSource.SharedTextureHandle;

            OpenSharedIfNeeded(handle, args.Device);
            if (_sharedSrv == null) return;

            // In DrawingSurface-Target rendern
            using var rtv    = _device.CreateRenderTargetView(renderTarget);
            var descTarget   = renderTarget.Description;
            var viewport     = new Viewport(0, 0, descTarget.Width, descTarget.Height, 0f, 1f);

            _context.RSSetViewports(new[] { viewport });
            _context.RSSetState(_rasterizer);
            _context.OMSetBlendState(_blend);
            _context.OMSetRenderTargets(rtv);
            _context.ClearRenderTargetView(rtv, new Color4(0f, 0f, 0f, 1f));

            _context.VSSetShader(_vs);
            _context.PSSetShader(_ps);
            _context.PSSetShaderResource(0, _sharedSrv);
            _context.PSSetSampler(0, _sampler);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.Draw(3, 0);

            _context.OMSetRenderTargets((ID3D11RenderTargetView)null);
            _context.PSSetShaderResource(0, null);
        }

        // Pipeline-Setup
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

        // Shared texture management
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
                _sharedTex = device.OpenSharedResource<ID3D11Texture2D>(handle);
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
                Log.Error($"[ZoomOverview] OpenSharedResource failed: {ex.Message}");
            }
        }


        // IDisposable
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _sharedSrv?.Dispose();
            _sharedSrv = null;
            _sharedTex?.Dispose();
            _sharedTex = null;

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
