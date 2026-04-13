using System;
using System.Runtime.InteropServices;
using FlyleafLib.MediaFramework.MediaDecoder;
using FlyleafLib.MediaFramework.MediaFrame;
using FlyleafLib.MediaFramework.MediaRenderer;
using FlyleafLib.MediaPlayer;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;
using ID3D11VideoContext = Vortice.Direct3D11.ID3D11VideoContext;
using ID3D11VideoDevice = Vortice.Direct3D11.ID3D11VideoDevice;

namespace FlyleafLib.Zoom
{
    /// <summary>
    /// Kapselt den Zugriff auf dekodierte Frames direkt aus dem VideoDecoder —
    /// sowohl HW-beschleunigt (D3D11VA, NV12 Texture-Array) als auch
    /// SW-dekodiert (BGRA Staging-Textur).
    ///
    /// Ergebnis ist immer eine einzelne BGRA <c>ID3D11Texture2D</c> die
    /// als ShaderResourceView im MiniMap-Shader verwendet werden kann.
    ///
    /// Kein Eingriff in Renderer.cs, Present() oder den Zoom-Shader.
    ///
    /// Ablauf:
    ///   HW-Frame → NV12 Texture-Array[SubresourceIndex]
    ///              → D3D11 VideoProcessor (NV12→BGRA, in-GPU)
    ///              → _convertedTex (BGRA, Array=1)
    ///
    ///   SW-Frame → TextureSW (bereits BGRA oder YUV)
    ///              → CopySubresourceRegion in _convertedTex
    ///              → (optional SwsScale wenn Format ≠ BGRA)
    /// </summary>
    internal unsafe class DecodedFrameSource : IDisposable
    {
        // ── Public output ─────────────────────────────────────────────────────
        /// <summary>
        /// Letzte konvertierte BGRA-Textur (ArraySize=1, MipLevels=1).
        /// Gültig nach einem erfolgreichen <see cref="TryUpdate"/> Aufruf.
        /// </summary>
        public ID3D11Texture2D    ConvertedTexture  { get; private set; }
        public ID3D11ShaderResourceView ConvertedSrv { get; private set; }
        public bool               HasValidFrame      { get; private set; }

        // ── D3D11 ─────────────────────────────────────────────────────────────
        private  ID3D11Device          _device;
        private  ID3D11DeviceContext   _context;
        private readonly VideoDecoder          _decoder;

        // VideoProcessor für HW NV12→BGRA Konvertierung (kein Shader nötig)
        private ID3D11VideoDevice              _videoDevice;
        private ID3D11VideoContext             _videoContext;
        private ID3D11VideoProcessor           _videoProcessor;
        private ID3D11VideoProcessorEnumerator _vpEnum;
        private ID3D11VideoProcessorInputView  _vpInputView;
        private ID3D11VideoProcessorOutputView _vpOutputView;

        // Ziel-Textur (BGRA, ArraySize=1) — Ausgabe beider Pfade
        private ID3D11Texture2D                _convertedTex;
        private int                            _texWidth, _texHeight;

        // Tracking um unnötige Rebuilds zu vermeiden
        private int _lastSubresource = -1;
        private bool _vpBuilt        = false;

        private bool _disposed;
        
        // ── NV12 Pixel Shader Fallback (wenn VideoProcessor nicht verfügbar) ──
        // Wird nur als Fallback bei sehr alten Treibern benötigt.
        // Hier vereinfacht: VideoProcessor wird bevorzugt.

        // ─────────────────────────────────────────────────────────────────────
        public DecodedFrameSource(ID3D11Device device, VideoDecoder decoder)
        {
            _device  = device  ?? throw new ArgumentNullException(nameof(device));
            _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
            _context = device.ImmediateContext;

            // VideoDevice/Context für HW-Konvertierung
            _videoDevice  = device.QueryInterface<ID3D11VideoDevice>();
            _videoContext = _context.QueryInterface<ID3D11VideoContext>();
        }

        
        // ── Hauptmethode: Frame aktualisieren ─────────────────────────────────
        /// <summary>
        /// Versucht den neuesten dekodierten Frame abzurufen und in
        /// <see cref="ConvertedTexture"/> (BGRA) zu konvertieren.
        /// Gibt <c>true</c> zurück wenn ein neuer Frame verfügbar war.
        /// </summary>
        public bool TryUpdate()
        {
            if (_disposed) return false;

            // Neuesten Frame aus der Decoder-Queue peeken (nicht dequeuen —
            // der Renderer soll ihn weiterhin verarbeiten)
            //if (!TryPeekLatestFrame(out var frame)) return false;
            VideoFrame frame = null;
            lock (_decoder.Renderer.Frames)
            {
                frame = _decoder.Renderer.Frames.Current;
                if (frame == null ) return false;

                bool isHW = _decoder.VideoAccelerated && frame.VPIV != null;

                if (isHW)
                    return UpdateFromHWFrame(frame);
                else
                    return UpdateFromSWFrame(frame);
            }
        }

        // ── HW-Frame: NV12 Texture-Array → BGRA via VideoProcessor ───────────
        private bool UpdateFromHWFrame(VideoFrame frame)
        {
            // frame.textures[0]      = ID3D11Texture2D (NV12 Array, ArraySize=17+)
            // frame.subresourceIndex = Index in dieses Array
            var srcTex         = new ID3D11Texture2D( frame.AVFrame->data[0]);
            int subresource    = (int)frame.AVFrame->data[1];

            if (srcTex == null) return false;

            var srcDesc = srcTex.Description;
            EnsureConvertedTexture((int)srcDesc.Width, (int)srcDesc.Height);
            EnsureVideoProcessor(srcTex, (int)srcDesc.Width,(int) srcDesc.Height);

            if (_videoProcessor == null) return false;

            // InputView auf den korrekten Array-Slice
            if (_lastSubresource != subresource || _vpInputView == null)
            {
                _vpInputView?.Dispose();
                var ivDesc = new VideoProcessorInputViewDescription
                {
                    FourCC    = 0,
                    ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorInputView
                    {
                        MipSlice        = 0,
                        ArraySlice = (uint)subresource,
                    }
                };
                _videoDevice.CreateVideoProcessorInputView(srcTex, _vpEnum, ivDesc,
                    out _vpInputView);
                _lastSubresource = subresource;
            }

            if (_vpInputView == null || _vpOutputView == null) return false;

            // VideoProcessor-Blit: NV12[subresource] → BGRA _convertedTex
            var stream = new VideoProcessorStream
            {
                Enable      = true,
                InputSurface = _vpInputView
            };

            _videoContext.VideoProcessorBlt(_videoProcessor, _vpOutputView,
                0, 1, new[] { stream });

            InvalidateSrv();
            HasValidFrame = true;
            return true;
        }

        // ── SW-Frame: TextureSW (Staging) → _convertedTex ────────────────────
        private bool UpdateFromSWFrame(VideoFrame frame)
        {
            // frame.textures[0] = ID3D11Texture2D (Staging, BGRA oder NV12)
            var srcTex = frame.Texture != null && frame.Texture.Length > 0
                ? frame.Texture[0]
                : null;

            if (srcTex == null) return false;

            var srcDesc = srcTex.Description;
            EnsureConvertedTexture((int)srcDesc.Width, (int)srcDesc.Height);

            if (_convertedTex == null) return false;

            // Einfaches CopyResource — funktioniert wenn Formate übereinstimmen
            // (FlyleafLib konvertiert SW-Frames zu BGRA vor dem Staging)
            if (srcDesc.Format == _convertedTex.Description.Format)
            {
                _context.CopyResource(_convertedTex, srcTex);
            }
            else
            {
                // Format-Mismatch (z.B. NV12 SW-Fallback):
                // VideoProcessor als Fallback verwenden
                EnsureVideoProcessor(srcTex, (int)srcDesc.Width, (int)srcDesc.Height);
                if (_videoProcessor != null)
                    return UpdateViaVideoProcessor(srcTex, 0);
                return false;
            }

            InvalidateSrv();
            HasValidFrame = true;
            return true;
        }

        private bool UpdateViaVideoProcessor(ID3D11Texture2D srcTex, int arraySlice)
        {
            if (_vpInputView == null || _vpOutputView == null) return false;
            var stream = new VideoProcessorStream
            {
                Enable       = true,
                InputSurface = _vpInputView
            };
            _videoContext.VideoProcessorBlt(_videoProcessor, _vpOutputView,
                0, 1, new[] { stream });
            InvalidateSrv();
            HasValidFrame = true;
            return true;
        }

        // ── SRV ungültig machen (wird beim nächsten Zugriff neu erstellt) ─────
        private void InvalidateSrv()
        {
            ConvertedSrv?.Dispose();
            ConvertedSrv = null;

            if (_convertedTex != null)
            {
                ConvertedSrv = _device.CreateShaderResourceView(_convertedTex,
                    new ShaderResourceViewDescription
                    {
                        Format        = Format.B8G8R8A8_UNorm,
                        ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.Texture2D,
                        Texture2D     = new Texture2DShaderResourceView { MipLevels = 1 }
                    });
            }
            ConvertedTexture = _convertedTex;
        }

        // ── Ziel-Textur sicherstellen ─────────────────────────────────────────
        private void EnsureConvertedTexture(int width, int height)
        {
            if (_convertedTex != null
                && _texWidth  == width
                && _texHeight == height)
                return;

            ConvertedSrv?.Dispose();  ConvertedSrv  = null;
            _convertedTex?.Dispose(); _convertedTex = null;
            _vpOutputView?.Dispose(); _vpOutputView = null;

            _convertedTex = _device.CreateTexture2D(new Texture2DDescription
            {
                Width             = (uint)width,
                Height            = (uint)height,
                MipLevels         = 1,
                ArraySize         = 1,
                Format            = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage             = ResourceUsage.Default,
                BindFlags         = BindFlags.RenderTarget | BindFlags.ShaderResource,
                MiscFlags         = ResourceOptionFlags.None
            });

            _texWidth  = width;
            _texHeight = height;
            _vpBuilt   = false;   // VideoProcessor neu bauen bei nächstem HW-Frame
        }

        // ── D3D11 VideoProcessor für NV12→BGRA ───────────────────────────────
        private void EnsureVideoProcessor(ID3D11Texture2D srcTex, int width, int height)
        {
            if (_vpBuilt) return;

            _vpInputView?.Dispose();  _vpInputView  = null;
            _vpOutputView?.Dispose(); _vpOutputView = null;
            _videoProcessor?.Dispose(); _videoProcessor = null;
            _vpEnum?.Dispose();         _vpEnum         = null;

            var content = new VideoProcessorContentDescription
            {
                Usage            = VideoUsage.PlaybackNormal,
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputWidth       = (uint)width,
                InputHeight      = (uint)height,
                OutputWidth      = (uint)width,
                OutputHeight     = (uint)height,
                InputFrameRate   = new Rational(60, 1),
                OutputFrameRate  = new Rational(60, 1)
            };

            _videoDevice.CreateVideoProcessorEnumerator(content, out _vpEnum);
            if (_vpEnum == null) return;

            _videoDevice.CreateVideoProcessor(_vpEnum, 0, out _videoProcessor);
            if (_videoProcessor == null) return;

            // Output View auf _convertedTex
            if (_convertedTex != null)
            {
                var ovDesc = new VideoProcessorOutputViewDescription
                {
                    ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                    Texture2D     = new Texture2DVideoProcessorOutputView { MipSlice = 0 }
                };
                _videoDevice.CreateVideoProcessorOutputView(_convertedTex, _vpEnum,
                    ovDesc, out _vpOutputView);
            }

            _vpBuilt = true;
        }

        // ── Frame aus Decoder-Queue peeken ────────────────────────────────────
        /*
        private bool TryPeekLatestFrame(out VideoFrame frame)
        {
            frame = null;
            // VideoDecoder.Frames ist eine ConcurrentQueue<VideoFrame>
            // Wir wollen den neuesten verfügbaren Frame sehen OHNE ihn zu dequeuen,
            // damit der Renderer ihn weiterhin normal verarbeiten kann.
            // TryPeek gibt das älteste Element — wir drainieren temporär
            // um das neueste zu finden, und legen alle zurück.
            // ACHTUNG: Nur safe wenn der Renderer ebenfalls TryDequeue verwendet
            //          und wir schnell genug sind. In der Praxis ist das OK da
            //          wir nur für die Minimap lesen und kein Timing kritisch ist.

            var queue = _decoder.Renderer?.Frames;
            if (queue is not VideoCache frames) return false;

            frames.RendererFrame
            // Einfachster sicherer Ansatz: TryPeek des ersten Elements
            // (ältester Frame, aber bereits dekodiert und ungezoomt — ausreichend)
            return queue.TryPeek(out frame) && frame != null;
        }
        */
        // ── IDisposable ───────────────────────────────────────────────────────
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _vpInputView?.Dispose();
            _vpOutputView?.Dispose();
            _videoProcessor?.Dispose();
            _vpEnum?.Dispose();
            _videoContext?.Dispose();
            _videoDevice?.Dispose();
            ConvertedSrv?.Dispose();
            _convertedTex?.Dispose();
        }
    }
}
