using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Vortice.Direct3D11;
using Vortice.Direct3D9;
using Vortice.DXGI;

using D9Format         = Vortice.Direct3D9.Format;
using D9Pool           = Vortice.Direct3D9.Pool;
using D9PresentParams  = Vortice.Direct3D9.PresentParameters;
using D9SwapEffect     = Vortice.Direct3D9.SwapEffect;
using D9Usage          = Vortice.Direct3D9.Usage;
using ID3D11Texture2D  = Vortice.Direct3D11.ID3D11Texture2D;
using IDirect3DDevice9 = Vortice.Direct3D9.IDirect3DDevice9;
using IDirect3DTexture9= Vortice.Direct3D9.IDirect3DTexture9;

using FlyleafLib.MediaPlayer;
using Format = Vortice.DXGI.Format;

namespace FlyleafLib.Controls.WPF;

/// <summary>
/// Bridges the renderer's D3D11 swap-chain backbuffer into WPF's D3DImage.
/// WPF still requires an IDirect3DSurface9 backbuffer, so D3D9 remains only as
/// the final interop layer while swap-chain setup and rendering stay on D3D11.
/// </summary>
internal sealed class D3DImageSurface : IDisposable
{
    readonly struct SharedDeviceKey : IEquatable<SharedDeviceKey>
    {
        public SharedDeviceKey(long adapterLuid, nint focusHwnd)
        {
            AdapterLuid = adapterLuid;
            FocusHwnd = focusHwnd;
        }

        public long AdapterLuid { get; }
        public nint FocusHwnd { get; }

        public bool Equals(SharedDeviceKey other) => AdapterLuid == other.AdapterLuid && FocusHwnd == other.FocusHwnd;
        public override bool Equals(object obj) => obj is SharedDeviceKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(AdapterLuid, FocusHwnd);
    }

    sealed class SharedD3D9Context : IDisposable
    {
        public SharedD3D9Context(IDirect3D9 factory, IDirect3DDevice9 device, uint adapter)
        {
            Factory = factory;
            Device = device;
            Adapter = adapter;
        }

        public IDirect3D9 Factory { get; }
        public IDirect3DDevice9 Device { get; }
        public uint Adapter { get; }
        public int RefCount { get; set; }

        public void Dispose()
        {
            Device.Dispose();
            Factory.Dispose();
        }
    }

    static readonly object lockSharedContexts = new();
    static readonly Dictionary<SharedDeviceKey, SharedD3D9Context> sharedContexts = new();

    readonly object sync = new();

    public D3DImage D3DImage { get; } = new D3DImage();

    SharedD3D9Context sharedContext;
    ID3D11Texture2D d3d11Texture;
    IDirect3DTexture9 d9Texture;
    IDirect3DSurface9 d9Surface;
    ID3D11Texture2D pendingD3D11Texture;
    IDirect3DTexture9 pendingD9Texture;
    IDirect3DSurface9 pendingD9Surface;

    Player player;
    int imageWidth, imageHeight;
    int controlWidth, controlHeight;
    bool bridgeReady;
    int callbackGeneration;
    int pendingPresentGeneration = -1;
    bool isDisposed;

    public void Initialize(Player player, int imageWidth, int imageHeight, int controlWidth, int controlHeight, nint focusHwnd)
    {
        this.player = player;
        this.imageWidth = imageWidth;
        this.imageHeight = imageHeight;
        this.controlWidth = controlWidth;
        this.controlHeight = controlHeight;

        Console.WriteLine($"[D3DI] Initialize image={imageWidth}x{imageHeight} control={controlWidth}x{controlHeight} hwnd=0x{focusHwnd:X}");
        CreateD3D9Device(focusHwnd);
        player.Renderer.SwapChain.RegisterBeforePresentCallback(OnBeforePresent);
        player.Renderer.SwapChain.SetupWinUI(OnSwapChainUpdated);
    }

    void CreateD3D9Device(nint focusHwnd)
    {
        long adapterLuid = player.Renderer.GPUAdapter?.Luid ?? 0;
        var key = new SharedDeviceKey(adapterLuid, focusHwnd);

        lock (lockSharedContexts)
        {
            if (sharedContexts.TryGetValue(key, out sharedContext))
            {
                sharedContext.RefCount++;
                Console.WriteLine($"[D3DI] Reusing D3D9 bridge adapter={sharedContext.Adapter} refs={sharedContext.RefCount}");
                return;
            }

            D3D9.Direct3DCreate9Ex(out var d9ExFactory);

            uint d9Adapter = FindMatchingD3D9Adapter(d9ExFactory, adapterLuid);
            var pp = new D9PresentParams
            {
                Windowed             = true,
                SwapEffect           = D9SwapEffect.Discard,
                PresentationInterval = PresentInterval.Default,
                BackBufferFormat     = D9Format.Unknown,
                BackBufferWidth      = 1,
                BackBufferHeight     = 1,
                BackBufferCount      = 1
            };

            var d9Device = CreateDeviceExNullDisplayMode(d9ExFactory, d9Adapter, focusHwnd,
                CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve,
                pp);

            sharedContext = new SharedD3D9Context(d9ExFactory, d9Device, d9Adapter) { RefCount = 1 };
            sharedContexts.Add(key, sharedContext);
        }
    }

    static unsafe IDirect3DDevice9 CreateDeviceExNullDisplayMode(
        IDirect3D9Ex factory, uint adapter, nint hwnd, CreateFlags flags, D9PresentParams pp)
    {
        nint pFactory = factory.NativePointer;
        void** vtable = *(void***)pFactory;
        var pfn = (delegate* unmanaged[Stdcall]<nint, uint, int, nint, uint, D9PresentParams*, void*, nint*, int>)vtable[20];

        nint devicePtr = 0;
        int hr = pfn(pFactory, adapter, (int)DeviceType.Hardware, hwnd, (uint)flags, &pp, null, &devicePtr);
        Marshal.ThrowExceptionForHR(hr);
        return new IDirect3DDevice9(devicePtr);
    }

    static uint FindMatchingD3D9Adapter(IDirect3D9Ex d9ExFactory, long targetLuid)
    {
        if (targetLuid == 0)
            return 0;

        try
        {
            for (uint i = 0; i < d9ExFactory.AdapterCount; i++)
            {
                var luid = d9ExFactory.GetAdapterLuid(i);
                long luidLong = ((long)luid.HighPart << 32) | (uint)luid.LowPart;
                if (luidLong == targetLuid)
                    return i;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[D3DI] FindMatchingD3D9Adapter failed: {ex.Message}");
        }

        return 0;
    }

    void OnSwapChainUpdated(IDXGISwapChain2 swapChain)
    {
        try
        {
            int generation = System.Threading.Interlocked.Increment(ref callbackGeneration);
            Console.WriteLine($"[D3DI] OnSwapChainUpdated swapChain={(swapChain != null ? "set" : "null")} gen={generation}");

            if (swapChain == null)
            {
                ReleaseBridge(generation);
                return;
            }

            QueueBridgeRecreation(generation);
            player?.Renderer?.SwapChain?.Resize(controlWidth, controlHeight);
        }
        finally
        {
            swapChain?.Dispose();
        }
    }

    void QueueBridgeRecreation(int generation)
    {
        int width = Math.Max(1, controlWidth);
        int height = Math.Max(1, controlHeight);

        var newD3D11Texture = CreateSharedTexture(width, height);
        nint sharedHandle;
        using (var dxgiResource = newD3D11Texture.QueryInterface<IDXGIResource>())
            sharedHandle = dxgiResource.SharedHandle;

        nint handle = sharedHandle;
        var newD9Texture = sharedContext.Device.CreateTexture(
            (uint)width, (uint)height, 1,
            D9Usage.RenderTarget, D9Format.A8R8G8B8, D9Pool.Default,
            ref handle);
        var newD9Surface = newD9Texture.GetSurfaceLevel(0);

        ID3D11Texture2D oldPendingD3D11Texture;
        IDirect3DTexture9 oldPendingD9Texture;
        IDirect3DSurface9 oldPendingD9Surface;

        lock (sync)
        {
            oldPendingD3D11Texture = pendingD3D11Texture;
            oldPendingD9Texture = pendingD9Texture;
            oldPendingD9Surface = pendingD9Surface;

            pendingD3D11Texture = newD3D11Texture;
            pendingD9Texture = newD9Texture;
            pendingD9Surface = newD9Surface;
        }

        if (d9Surface == null)
            PromotePendingBridge(generation);

        DisposeResources(oldPendingD3D11Texture, oldPendingD9Texture, oldPendingD9Surface);
    }

    ID3D11Texture2D CreateSharedTexture(int width, int height)
    {
        return player.Renderer.Device.CreateTexture2D(new Texture2DDescription
        {
            Width             = (uint)width,
            Height            = (uint)height,
            MipLevels         = 1,
            ArraySize         = 1,
            Format            = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage             = ResourceUsage.Default,
            BindFlags         = BindFlags.RenderTarget | BindFlags.ShaderResource,
            MiscFlags         = ResourceOptionFlags.Shared
        });
    }

    void DispatchAttach(int generation, nint surfacePtr, ID3D11Texture2D oldD3D11Texture, IDirect3DTexture9 oldD9Texture, IDirect3DSurface9 oldD9Surface)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            DisposeResources(oldD3D11Texture, oldD9Texture, oldD9Surface);
            return;
        }

        void AttachAction()
        {
            if (generation == callbackGeneration && !isDisposed)
                AttachD3DImage(surfacePtr);

            DisposeResources(oldD3D11Texture, oldD9Texture, oldD9Surface);
        }

        if (dispatcher.CheckAccess())
            AttachAction();
        else
            dispatcher.BeginInvoke((Action)AttachAction);
    }

    void ReleaseBridge(int generation)
    {
        ID3D11Texture2D oldD3D11Texture;
        IDirect3DTexture9 oldD9Texture;
        IDirect3DSurface9 oldD9Surface;
        ID3D11Texture2D oldPendingD3D11Texture;
        IDirect3DTexture9 oldPendingD9Texture;
        IDirect3DSurface9 oldPendingD9Surface;

        lock (sync)
        {
            bridgeReady = false;
            oldD3D11Texture = d3d11Texture;
            oldD9Texture = d9Texture;
            oldD9Surface = d9Surface;
            oldPendingD3D11Texture = pendingD3D11Texture;
            oldPendingD9Texture = pendingD9Texture;
            oldPendingD9Surface = pendingD9Surface;
            d3d11Texture = null;
            d9Texture = null;
            d9Surface = null;
            pendingD3D11Texture = null;
            pendingD9Texture = null;
            pendingD9Surface = null;
        }

        DispatchDetach();
        DisposeResources(oldD3D11Texture, oldD9Texture, oldD9Surface);
        DisposeResources(oldPendingD3D11Texture, oldPendingD9Texture, oldPendingD9Surface);
    }

    void PromotePendingBridge(int generation)
    {
        nint surfacePtr;
        ID3D11Texture2D oldD3D11Texture;
        IDirect3DTexture9 oldD9Texture;
        IDirect3DSurface9 oldD9Surface;
        ID3D11Texture2D nextD3D11Texture;
        IDirect3DTexture9 nextD9Texture;
        IDirect3DSurface9 nextD9Surface;

        lock (sync)
        {
            if (isDisposed || generation != callbackGeneration || pendingD9Surface == null)
                return;

            oldD3D11Texture = d3d11Texture;
            oldD9Texture = d9Texture;
            oldD9Surface = d9Surface;

            nextD3D11Texture = pendingD3D11Texture;
            nextD9Texture = pendingD9Texture;
            nextD9Surface = pendingD9Surface;

            pendingD3D11Texture = null;
            pendingD9Texture = null;
            pendingD9Surface = null;

            d3d11Texture = nextD3D11Texture;
            d9Texture = nextD9Texture;
            d9Surface = nextD9Surface;
            bridgeReady = true;
            surfacePtr = nextD9Surface.NativePointer;
        }

        DispatchAttach(generation, surfacePtr, oldD3D11Texture, oldD9Texture, oldD9Surface);
    }

    static void DisposeResources(ID3D11Texture2D d3d11Texture, IDirect3DTexture9 d9Texture, IDirect3DSurface9 d9Surface)
    {
        d9Surface?.Dispose();
        d9Texture?.Dispose();
        d3d11Texture?.Dispose();
    }

    void DispatchDetach()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
            return;

        if (dispatcher.CheckAccess())
            DetachD3DImage();
        else
            dispatcher.BeginInvoke(DetachD3DImage);
    }

    void AttachD3DImage(nint surfacePtr)
    {
        D3DImage.Lock();
        D3DImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surfacePtr);
        if (D3DImage.PixelWidth > 0 && D3DImage.PixelHeight > 0)
            D3DImage.AddDirtyRect(new Int32Rect(0, 0, D3DImage.PixelWidth, D3DImage.PixelHeight));
        D3DImage.Unlock();
    }

    void DetachD3DImage()
    {
        D3DImage.Lock();
        D3DImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
        D3DImage.Unlock();
    }

    int presentCount;
    void OnBeforePresent()
    {
        int n = System.Threading.Interlocked.Increment(ref presentCount);
        if (n <= 5 || n % 60 == 0)
            Console.WriteLine($"[D3DI] OnBeforePresent #{n} ready={bridgeReady}");

        bool requestPresentation = false;
        int generation;

        lock (sync)
        {
            if (isDisposed || !bridgeReady || d3d11Texture == null)
            {
                if (pendingD3D11Texture == null)
                    return;
            }

            var targetTexture = pendingD3D11Texture ?? d3d11Texture;
            if (!player.Renderer.SwapChain.CopyBackBufferTo(targetTexture))
                return;

            if (pendingD3D11Texture != null)
            {
                pendingPresentGeneration = -1;
            }
            else
            {
                generation = callbackGeneration;
                System.Threading.Volatile.Write(ref pendingPresentGeneration, generation);
                requestPresentation = true;
            }

            generation = callbackGeneration;
        }

        PromotePendingBridge(generation);

        if (requestPresentation)
            D3DImagePresentationPump.Request(this);
    }

    public void ProcessPendingPresentation()
    {
        bool ready;
        lock (sync)
            ready = bridgeReady;

        if (System.Threading.Volatile.Read(ref pendingPresentGeneration) != callbackGeneration || isDisposed || !ready || !D3DImage.IsFrontBufferAvailable)
            return;

        try
        {
            D3DImage.Lock();
            if (D3DImage.PixelWidth > 0 && D3DImage.PixelHeight > 0)
                D3DImage.AddDirtyRect(new Int32Rect(0, 0, D3DImage.PixelWidth, D3DImage.PixelHeight));
            D3DImage.Unlock();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[D3DI] ProcessPendingPresentation failed: {ex.GetType().Name} {ex.Message}");
        }
    }

    public void Resize(int newImageWidth, int newImageHeight, int newControlWidth, int newControlHeight)
    {
        bool imageChanged = newImageWidth != imageWidth || newImageHeight != imageHeight;
        bool controlChanged = newControlWidth != controlWidth || newControlHeight != controlHeight;
        if (!imageChanged && !controlChanged)
            return;
        if (newImageWidth <= 0 || newImageHeight <= 0 || newControlWidth <= 0 || newControlHeight <= 0)
            return;

        imageWidth = newImageWidth;
        imageHeight = newImageHeight;
        controlWidth = newControlWidth;
        controlHeight = newControlHeight;

        D3DImagePresentationPump.Remove(this);
        System.Threading.Interlocked.Increment(ref callbackGeneration);

        if (!isDisposed && player?.Renderer?.SwapChain != null && !player.Renderer.SwapChain.Disposed)
            QueueBridgeRecreation(callbackGeneration);

        player?.Renderer?.SwapChain?.Resize(newControlWidth, newControlHeight);
    }

    public void Dispose()
    {
        isDisposed = true;
        System.Threading.Interlocked.Increment(ref callbackGeneration);
        D3DImagePresentationPump.Remove(this);

        if (player?.Renderer?.SwapChain != null)
        {
            player.Renderer.SwapChain.UnregisterBeforePresentCallback(OnBeforePresent);
            player.Renderer.SwapChain.Dispose(rendererFrame: false);
        }

        ReleaseBridge(callbackGeneration);
        ReleaseSharedContext();
    }

    void ReleaseSharedContext()
    {
        if (sharedContext == null)
            return;

        lock (lockSharedContexts)
        {
            sharedContext.RefCount--;
            if (sharedContext.RefCount == 0)
            {
                var entry = sharedContexts.FirstOrDefault(pair => ReferenceEquals(pair.Value, sharedContext));
                if (!entry.Equals(default(KeyValuePair<SharedDeviceKey, SharedD3D9Context>)))
                    sharedContexts.Remove(entry.Key);

                sharedContext.Dispose();
            }
        }

        sharedContext = null;
    }
}
