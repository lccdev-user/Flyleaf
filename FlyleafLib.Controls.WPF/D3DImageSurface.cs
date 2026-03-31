using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Vortice.Direct3D9;
using D9Usage          = Vortice.Direct3D9.Usage;
using D9Format         = Vortice.Direct3D9.Format;
using D9Pool           = Vortice.Direct3D9.Pool;
using D9PresentParams  = Vortice.Direct3D9.PresentParameters;
using D9SwapEffect     = Vortice.Direct3D9.SwapEffect;

using FlyleafLib.MediaPlayer;

namespace FlyleafLib.Controls.WPF;

/// <summary>
/// Owns the D3D9Ex device and D3DImage needed to integrate a D3D11 render target
/// into the WPF visual tree without a Win32 HWND.
/// </summary>
internal sealed class D3DImageSurface : IDisposable
{
    public D3DImage D3DImage { get; } = new D3DImage();

    IDirect3D9      d9Factory;
    IDirect3DDevice9 d9Device;
    IDirect3DTexture9 d9Texture;
    IDirect3DSurface9 d9Surface;

    Player  player;
    int     width, height;
    bool    d9Ready;

    public void Initialize(Player player, int width, int height, nint focusHwnd)
    {
        this.player = player;
        this.width  = width;
        this.height = height;

        Console.WriteLine($"[D9S] Initialize  {width}x{height} hwnd=0x{focusHwnd:X}");
        CreateD3D9Device(focusHwnd);
        Console.WriteLine("[D9S] D3D9 device created");
        SetupSharedTexture();
        Console.WriteLine("[D9S] SetupSharedTexture called");
    }

    void CreateD3D9Device(nint focusHwnd)
    {
        // D3D9Ex is required on WDDM 2.0+ (Windows 8+) for cross-API D3D11→D3D9 shared
        // texture interop. Non-Ex D3D9 CreateTexture(ref sharedHandle) returns
        // D3DERR_INVALIDCALL (0x8876086C) on modern Windows regardless of adapter.
        D3D9.Direct3DCreate9Ex(out var d9ExFactory);
        d9Factory = d9ExFactory; // IDirect3D9Ex inherits IDirect3D9

        uint d9Adapter = FindMatchingD3D9Adapter(d9ExFactory);
        Console.WriteLine($"[D9S] CreateD3D9Device  adapter={d9Adapter} D3D11 GPU={player.Renderer.GPUAdapter?.Description}");

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

        // D3D9Ex CreateDeviceEx requires pFullscreenDisplayMode=NULL for windowed mode.
        // Vortice binds DisplayModeEx as a C# value type, so it always passes a non-null
        // pointer — causing D3DERR_INVALIDCALL (0x8876086C). We bypass Vortice via a direct
        // COM vtable call so we can pass a genuine null pointer.
        d9Device = CreateDeviceExNullDisplayMode(d9ExFactory, d9Adapter, focusHwnd,
            CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve,
            pp);
    }

    // IDirect3D9Ex vtable layout (0-based):
    //   IUnknown:      0=QueryInterface, 1=AddRef, 2=Release
    //   IDirect3D9:    3..16  (14 methods: RegisterSoftwareDevice..GetAdapterModeCount..etc)
    //   IDirect3D9Ex: 17=GetAdapterModeCountEx, 18=EnumAdapterModesEx,
    //                 19=GetAdapterDisplayModeEx, 20=CreateDeviceEx, 21=GetAdapterLuid
    //
    // CreateDeviceEx native signature:
    //   HRESULT CreateDeviceEx(UINT Adapter, D3DDEVTYPE DeviceType, HWND hFocusWindow,
    //       DWORD BehaviorFlags, D3DPRESENT_PARAMETERS* pPP,
    //       D3DDISPLAYMODEEX* pFullscreenDisplayMode,   ← must be NULL for windowed
    //       IDirect3DDevice9Ex** ppReturnedDeviceInterface)
    static unsafe IDirect3DDevice9 CreateDeviceExNullDisplayMode(
        IDirect3D9Ex factory, uint adapter, nint hwnd, CreateFlags flags, D9PresentParams pp)
    {
        nint pFactory = factory.NativePointer;
        void** vtable = *(void***)pFactory;

        // slot 20 = CreateDeviceEx
        var pfn = (delegate* unmanaged[Stdcall]<nint, uint, int, nint, uint, D9PresentParams*, void*, nint*, int>)vtable[20];

        nint devicePtr = 0;
        int hr = pfn(pFactory, adapter, (int)DeviceType.Hardware, hwnd, (uint)flags, &pp, null, &devicePtr);
        Marshal.ThrowExceptionForHR(hr);

        Console.WriteLine($"[D9S] CreateDeviceExNullDisplayMode  hr=0x{hr:X8} devicePtr=0x{devicePtr:X}");
        return new IDirect3DDevice9(devicePtr);
    }

    uint FindMatchingD3D9Adapter(IDirect3D9Ex d9ExFactory)
    {
        // Match by LUID — the most reliable cross-API adapter identity on WDDM.
        long targetLuid = player.Renderer.GPUAdapter?.Luid ?? 0;
        if (targetLuid == 0)
        {
            Console.WriteLine("[D9S] FindMatchingD3D9Adapter  no LUID on renderer GPUAdapter, defaulting to 0");
            return 0;
        }

        try
        {
            uint count = d9ExFactory.AdapterCount;
            Console.WriteLine($"[D9S] FindMatchingD3D9Adapter  target LUID=0x{targetLuid:X} D3D9Ex adapters={count}");
            for (uint i = 0; i < count; i++)
            {
                var luid = d9ExFactory.GetAdapterLuid(i);
                // Vortice Luid struct: combine LowPart/HighPart into long for comparison
                long luidLong = ((long)luid.HighPart << 32) | (uint)luid.LowPart;
                Console.WriteLine($"[D9S] FindMatchingD3D9Adapter  adapter[{i}] LUID=0x{luidLong:X}");
                if (luidLong == targetLuid)
                    return i;
            }
            Console.WriteLine("[D9S] FindMatchingD3D9Adapter  no LUID match, defaulting to 0");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[D9S] FindMatchingD3D9Adapter  error: {ex.Message}, defaulting to 0");
        }

        return 0;
    }

    void SetupSharedTexture()
    {
        player.Renderer.SwapChain.SetupD3DImage(width, height, OnHandleUpdated, OnPresented);
    }

    void OnHandleUpdated(nint sharedHandle)
    {
        Console.WriteLine($"[D9S] OnHandleUpdated  handle=0x{sharedHandle:X} size={width}x{height}");

        // Keep old resources alive until AFTER D3DImage is repointed — disposing them
        // before SetBackBuffer would leave D3DImage pointing at freed GPU memory.
        var oldSurface = d9Surface;
        var oldTexture = d9Texture;
        bool wasReady  = d9Ready;
        d9Ready   = false;
        d9Surface = null;
        d9Texture = null;
        Console.WriteLine($"[D9S] OnHandleUpdated  wasReady={wasReady} → d9Ready=false (will reattach)");

        if (sharedHandle == 0)
        {
            Console.WriteLine("[D9S] OnHandleUpdated  handle=0 → detaching");
            DispatchDetach();
            oldSurface?.Dispose();
            oldTexture?.Dispose();
            return;
        }

        // Open the D3D11 shared texture in D3D9 by passing its DXGI legacy handle.
        // CreateTexture takes HANDLE* (ref nint) as the last param; passing the existing
        // handle value causes D3D9 to open that shared resource instead of creating a new one.
        nint h = sharedHandle;
        try
        {
            d9Texture = d9Device.CreateTexture(
                (uint)width, (uint)height, 1,
                D9Usage.RenderTarget, D9Format.A8R8G8B8, D9Pool.Default,
                ref h);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[D9S] OnHandleUpdated  CreateTexture FAILED: {ex.GetType().Name} HR=0x{ex.HResult:X8} msg={ex.Message}");
            oldSurface?.Dispose();
            oldTexture?.Dispose();
            throw; // propagate so SwapChain.SetupLocalD3DImage's catch can clean up
        }

        d9Surface = d9Texture.GetSurfaceLevel(0);
        d9Ready   = true;

        nint surfacePtr = d9Surface.NativePointer;
        Console.WriteLine($"[D9S] OnHandleUpdated  d9Surface=0x{surfacePtr:X} d9Ready={d9Ready}");

        // Synchronously switch D3DImage to the new surface, then dispose old resources.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            Console.WriteLine("[D9S] OnHandleUpdated  WARN dispatcher is null, skipping attach");
            oldSurface?.Dispose();
            oldTexture?.Dispose();
            return;
        }

        bool onUiThread = dispatcher.CheckAccess();
        Console.WriteLine($"[D9S] OnHandleUpdated  dispatching AttachD3DImage onUiThread={onUiThread}");
        if (onUiThread)
        {
            AttachD3DImage(surfacePtr);
            oldSurface?.Dispose();
            oldTexture?.Dispose();
        }
        else
        {
            // BeginInvoke (async) instead of Invoke to avoid deadlock:
            // SetSize() holds lockRenderLoops while calling this callback, and
            // VPRequest() → RenderRequest() also tries to acquire lockRenderLoops
            // from the UI thread during resize — synchronous Invoke would deadlock.
            // Old resources stay alive until the lambda runs (after SetBackBuffer).
            dispatcher.BeginInvoke(() =>
            {
                AttachD3DImage(surfacePtr);
                oldSurface?.Dispose();
                oldTexture?.Dispose();
            });
        }
        Console.WriteLine("[D9S] OnHandleUpdated  done");
    }

    void DispatchDetach()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;

        if (dispatcher.CheckAccess())
            DetachD3DImage();
        else
            dispatcher.Invoke(DetachD3DImage);
    }

    void AttachD3DImage(nint surfacePtr)
    {
        Console.WriteLine($"[D9S] AttachD3DImage  surfacePtr=0x{surfacePtr:X}");
        D3DImage.Lock();
        D3DImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surfacePtr);
        Console.WriteLine($"[D9S] AttachD3DImage  after SetBackBuffer PixelSize={D3DImage.PixelWidth}x{D3DImage.PixelHeight} IsFrontBufferAvailable={D3DImage.IsFrontBufferAvailable}");
        // AddDirtyRect is required so WPF composites the frame immediately after attach.
        D3DImage.AddDirtyRect(new Int32Rect(0, 0, D3DImage.PixelWidth, D3DImage.PixelHeight));
        D3DImage.Unlock();
        Console.WriteLine("[D9S] AttachD3DImage  done");
    }

    void DetachD3DImage()
    {
        D3DImage.Lock();
        D3DImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
        D3DImage.Unlock();
    }

    int _presentCount;
    void OnPresented()
    {
        int n = System.Threading.Interlocked.Increment(ref _presentCount);
        if (n <= 5 || n % 60 == 0)
            Console.WriteLine($"[D9S] OnPresented #{n}  d9Ready={d9Ready}");

        if (!d9Ready) return;

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (!d9Ready || !D3DImage.IsFrontBufferAvailable) return;

            try
            {
                D3DImage.Lock();
                D3DImage.AddDirtyRect(new Int32Rect(0, 0, D3DImage.PixelWidth, D3DImage.PixelHeight));
                D3DImage.Unlock();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[D9S] OnPresented  AddDirtyRect FAILED: {ex.GetType().Name} msg={ex.Message} PixelSize={D3DImage.PixelWidth}x{D3DImage.PixelHeight} d9Ready={d9Ready} IsFrontBufferAvailable={D3DImage.IsFrontBufferAvailable}");
            }
        });
    }

    public void Resize(int newWidth, int newHeight)
    {
        if (newWidth == width && newHeight == height) return;
        if (newWidth <= 0 || newHeight <= 0) return;

        width   = newWidth;
        height  = newHeight;
        d9Ready = false;

        player?.Renderer?.SwapChain.ResizeD3DImage(newWidth, newHeight);
    }

    public void Dispose()
    {
        d9Ready = false;

        // SwapChain.Dispose triggers DisposeLocalD3DImage → OnHandleUpdated(0) → DetachD3DImage
        // so D3DImage is detached before we release the D3D9 resources.
        player?.Renderer?.SwapChain.Dispose(rendererFrame: false);

        d9Surface?.Dispose(); d9Surface = null;
        d9Texture?.Dispose(); d9Texture = null;
        d9Device ?.Dispose(); d9Device  = null;
        d9Factory?.Dispose(); d9Factory = null;
    }
}
