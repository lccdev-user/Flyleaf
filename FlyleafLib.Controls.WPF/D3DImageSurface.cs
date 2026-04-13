using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Vortice.Direct3D11;
using Vortice.Direct3D9;
using Vortice.DXGI;

using D9Format = Vortice.Direct3D9.Format;
using D9Pool = Vortice.Direct3D9.Pool;
using D9PresentParams = Vortice.Direct3D9.PresentParameters;
using D9SwapEffect = Vortice.Direct3D9.SwapEffect;
using D9Usage = Vortice.Direct3D9.Usage;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;
using IDirect3DDevice9 = Vortice.Direct3D9.IDirect3DDevice9;
using IDirect3DTexture9 = Vortice.Direct3D9.IDirect3DTexture9;

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
    private static readonly object lockSharedContexts = new();
    private static readonly Dictionary<SharedDeviceKey, SharedD3D9Context> sharedContexts = new();

    private readonly object sync = new();

    private SharedD3D9Context sharedContext;
    private BridgeResources activeBridge;
    private BridgeResources pendingBridge;
    private Player player;
    private int imageWidth;
    private int imageHeight;
    private int controlWidth;
    private int controlHeight;
    private bool bridgeReady;
    private int callbackGeneration;
    private int pendingPresentGeneration = -1;
    private int presentCount;
    private bool isDisposed;

    public D3DImage D3DImage { get; } = new D3DImage();

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

    public void ProcessPendingPresentation()
    {
        if (!CanPresentPendingFrame())
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
        int generation = System.Threading.Interlocked.Increment(ref callbackGeneration);

        if (!isDisposed && player?.Renderer?.SwapChain != null && !player.Renderer.SwapChain.Disposed)
            QueueBridgeRecreation(generation);

        player?.Renderer?.SwapChain?.Resize(newControlWidth, newControlHeight);
    }

    public void Dispose()
    {
        isDisposed = true;
        int generation = System.Threading.Interlocked.Increment(ref callbackGeneration);
        D3DImagePresentationPump.Remove(this);

        if (player?.Renderer?.SwapChain != null)
        {
            player.Renderer.SwapChain.UnregisterBeforePresentCallback(OnBeforePresent);
            player.Renderer.SwapChain.Dispose(rendererFrame: false);
        }

        ReleaseBridge(generation);
        ReleaseSharedContext();
    }

    private void CreateD3D9Device(nint focusHwnd)
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
                Windowed = true,
                SwapEffect = D9SwapEffect.Discard,
                PresentationInterval = PresentInterval.Default,
                BackBufferFormat = D9Format.Unknown,
                BackBufferWidth = 1,
                BackBufferHeight = 1,
                BackBufferCount = 1
            };

            var d9Device = CreateDeviceExNullDisplayMode(
                d9ExFactory,
                d9Adapter,
                focusHwnd,
                CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve,
                pp);

            sharedContext = new SharedD3D9Context(d9ExFactory, d9Device, d9Adapter) { RefCount = 1 };
            sharedContexts.Add(key, sharedContext);
        }
    }

    private void OnSwapChainUpdated(IDXGISwapChain2 swapChain)
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

    private void QueueBridgeRecreation(int generation)
    {
        var newBridge = CreateBridgeResources(Math.Max(1, controlWidth), Math.Max(1, controlHeight));
        var oldPendingBridge = ReplacePendingBridge(newBridge);

        if (!HasActiveBridge())
            PromotePendingBridge(generation);

        DisposeResources(oldPendingBridge);
    }

    private BridgeResources CreateBridgeResources(int width, int height)
    {
        var d3d11Texture = CreateSharedTexture(width, height);
        nint sharedHandle;

        using (var dxgiResource = d3d11Texture.QueryInterface<IDXGIResource>())
            sharedHandle = dxgiResource.SharedHandle;

        nint handle = sharedHandle;
        var d9Texture = sharedContext.Device.CreateTexture(
            (uint)width,
            (uint)height,
            1,
            D9Usage.RenderTarget,
            D9Format.A8R8G8B8,
            D9Pool.Default,
            ref handle);

        var d9Surface = d9Texture.GetSurfaceLevel(0);
        return new BridgeResources(d3d11Texture, d9Texture, d9Surface);
    }

    private BridgeResources ReplacePendingBridge(BridgeResources newBridge)
    {
        lock (sync)
        {
            var oldPendingBridge = pendingBridge;
            pendingBridge = newBridge;
            return oldPendingBridge;
        }
    }

    private bool HasActiveBridge()
    {
        lock (sync)
            return !activeBridge.IsEmpty;
    }

    private void PromotePendingBridge(int generation)
    {
        BridgePromotion promotion;

        lock (sync)
        {
            if (isDisposed || generation != callbackGeneration || pendingBridge.IsEmpty)
                return;

            promotion = new BridgePromotion(activeBridge, pendingBridge, generation);
            activeBridge = pendingBridge;
            pendingBridge = default;
            bridgeReady = true;
        }

        DispatchAttach(promotion.Generation, promotion.Next.D9Surface.NativePointer, promotion.Previous);
    }

    private void ReleaseBridge(int generation)
    {
        BridgeResources active;
        BridgeResources pending;

        lock (sync)
        {
            bridgeReady = false;
            active = activeBridge;
            pending = pendingBridge;
            activeBridge = default;
            pendingBridge = default;
        }

        DispatchDetach();
        DisposeResources(active);
        DisposeResources(pending);
    }

    private void DispatchAttach(int generation, nint surfacePtr, BridgeResources previousBridge)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            DisposeResources(previousBridge);
            return;
        }

        void AttachAction()
        {
            if (generation == callbackGeneration && !isDisposed)
                AttachD3DImage(surfacePtr);

            DisposeResources(previousBridge);
        }

        if (dispatcher.CheckAccess())
            AttachAction();
        else
            dispatcher.BeginInvoke((Action)AttachAction);
    }

    private void DispatchDetach()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
            return;

        if (dispatcher.CheckAccess())
            DetachD3DImage();
        else
            dispatcher.BeginInvoke(DetachD3DImage);
    }

    private void AttachD3DImage(nint surfacePtr)
    {
        D3DImage.Lock();
        D3DImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surfacePtr);
        if (D3DImage.PixelWidth > 0 && D3DImage.PixelHeight > 0)
            D3DImage.AddDirtyRect(new Int32Rect(0, 0, D3DImage.PixelWidth, D3DImage.PixelHeight));
        D3DImage.Unlock();
    }

    private void DetachD3DImage()
    {
        D3DImage.Lock();
        D3DImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
        D3DImage.Unlock();
    }

    private void OnBeforePresent()
    {
        int count = System.Threading.Interlocked.Increment(ref presentCount);
        if (count <= 5 || count % 60 == 0)
            Console.WriteLine($"[D3DI] OnBeforePresent #{count} ready={bridgeReady}");

        if (!TryCopyBackBuffer(out int generation, out bool requestPresentation))
            return;

        PromotePendingBridge(generation);

        if (requestPresentation)
            D3DImagePresentationPump.Request(this);
    }

    private bool TryCopyBackBuffer(out int generation, out bool requestPresentation)
    {
        lock (sync)
        {
            generation = callbackGeneration;
            requestPresentation = false;

            if (!HasBridgeTargetForPresentation())
                return false;

            var targetTexture = pendingBridge.D3D11Texture ?? activeBridge.D3D11Texture;
            if (!player.Renderer.SwapChain.CopyBackBufferTo(targetTexture))
                return false;

            if (!pendingBridge.IsEmpty)
            {
                pendingPresentGeneration = -1;
                return true;
            }

            System.Threading.Volatile.Write(ref pendingPresentGeneration, generation);
            requestPresentation = true;
            return true;
        }
    }

    private bool HasBridgeTargetForPresentation()
        => !isDisposed && ((!activeBridge.IsEmpty && bridgeReady) || !pendingBridge.IsEmpty);

    private bool CanPresentPendingFrame()
    {
        lock (sync)
        {
            return System.Threading.Volatile.Read(ref pendingPresentGeneration) == callbackGeneration &&
                   !isDisposed &&
                   bridgeReady &&
                   !activeBridge.IsEmpty &&
                   D3DImage.IsFrontBufferAvailable;
        }
    }

    private ID3D11Texture2D CreateSharedTexture(int width, int height)
    {
        return player.Renderer.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            MiscFlags = ResourceOptionFlags.Shared
        });
    }

    private void ReleaseSharedContext()
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

    private static void DisposeResources(BridgeResources resources)
    {
        resources.D9Surface?.Dispose();
        resources.D9Texture?.Dispose();
        resources.D3D11Texture?.Dispose();
    }

    private static unsafe IDirect3DDevice9 CreateDeviceExNullDisplayMode(
        IDirect3D9Ex factory,
        uint adapter,
        nint hwnd,
        CreateFlags flags,
        D9PresentParams pp)
    {
        nint pFactory = factory.NativePointer;
        void** vtable = *(void***)pFactory;
        var pfn = (delegate* unmanaged[Stdcall]<nint, uint, int, nint, uint, D9PresentParams*, void*, nint*, int>)vtable[20];

        nint devicePtr = 0;
        int hr = pfn(pFactory, adapter, (int)DeviceType.Hardware, hwnd, (uint)flags, &pp, null, &devicePtr);
        Marshal.ThrowExceptionForHR(hr);
        return new IDirect3DDevice9(devicePtr);
    }

    private static uint FindMatchingD3D9Adapter(IDirect3D9Ex d9ExFactory, long targetLuid)
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

    private readonly record struct BridgeResources(
        ID3D11Texture2D D3D11Texture,
        IDirect3DTexture9 D9Texture,
        IDirect3DSurface9 D9Surface)
    {
        public bool IsEmpty => D3D11Texture == null || D9Texture == null || D9Surface == null;
    }

    private readonly record struct BridgePromotion(BridgeResources Previous, BridgeResources Next, int Generation);

    private readonly struct SharedDeviceKey : IEquatable<SharedDeviceKey>
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

    private sealed class SharedD3D9Context : IDisposable
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
}
