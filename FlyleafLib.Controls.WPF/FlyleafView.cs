using FlyleafLib.MediaPlayer;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace FlyleafLib.Controls.WPF;

/// <summary>
/// A WPF FrameworkElement that renders a Flyleaf <see cref="Player"/> directly into the
/// WPF visual tree via D3DImage, avoiding the Win32 airspace limitation of
/// <see cref="FlyleafLib.Controls.WPF.FlyleafHost"/>.
/// </summary>
public class FlyleafView : Decorator, IHostPlayer, IDisposable
{
    private static readonly Type flType = typeof(FlyleafView);
    private static readonly Type playerType = typeof(Player);

    public static readonly DependencyProperty PlayerProperty =
        DependencyProperty.Register(nameof(Player), playerType, flType, new(null, OnPlayerChanged));

    public static readonly DependencyProperty ReplicaPlayerProperty =
        DependencyProperty.Register(nameof(ReplicaPlayer), typeof(Player), flType, new PropertyMetadata(null, OnReplicaPlayerChanged));

    public static readonly DependencyProperty HostDataContextProperty =
        DependencyProperty.Register(nameof(HostDataContext), typeof(object), flType, new(null));

    private D3DImageSurface surface;
    private bool isFullScreen;

    public Player Player
    {
        get => (Player)GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }

    public Player ReplicaPlayer
    {
        get => (Player)GetValue(ReplicaPlayerProperty);
        set => SetValue(ReplicaPlayerProperty, value);
    }

    public object HostDataContext
    {
        get => GetValue(HostDataContextProperty);
        set => SetValue(HostDataContextProperty, value);
    }

    public double DpiX { get; private set; } = 1;
    public double DpiY { get; private set; } = 1;

    public FlyleafView()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        MouseWheel += OnMouseWheel;
    }

    public bool Player_CanHideCursor() => IsMouseOver;

    public bool Player_GetFullScreen() => isFullScreen;

    public void Player_SetFullScreen(bool value)
    {
        isFullScreen = value;

        var window = Window.GetWindow(this);
        if (window == null)
            return;

        if (value)
        {
            window.WindowStyle = WindowStyle.None;
            window.ResizeMode = ResizeMode.NoResize;
            window.WindowState = WindowState.Maximized;
            return;
        }

        window.WindowStyle = WindowStyle.SingleBorderWindow;
        window.ResizeMode = ResizeMode.CanResize;
        window.WindowState = WindowState.Normal;
    }

    public void Player_RatioChanged(double keepRatio)
    {
        // WPF layout handles sizing; no explicit resize needed.
    }

    public bool Player_HandlesRatioResize(int width, int height) => false;

    public void Player_Disposed()
        => Dispatcher.BeginInvoke(() => Player = null);

    public void SetReplicaPlayer(Player oldPlayer)
    {
        // temporary placeholder
    }

    public void Dispose()
    {
        DisposeSurface();

        if (Player == null)
            return;

        var currentPlayer = Player;
        Player = null;
        currentPlayer.Host = null;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var rect = new Rect(RenderSize);
        if (CanDrawSurface())
        {
            Console.WriteLine($"[FLV] OnRender  DrawImage D3DImage={surface.D3DImage.PixelWidth}x{surface.D3DImage.PixelHeight} rect={rect.Width:F0}x{rect.Height:F0}");
            dc.DrawImage(surface.D3DImage, rect);
            return;
        }

        Console.WriteLine($"[FLV] OnRender  FALLBACK black rect  surface={(surface != null ? "set" : "null")} IsFrontBufferAvailable={surface?.D3DImage?.IsFrontBufferAvailable}");
        dc.DrawRectangle(Brushes.Black, null, rect);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        if (surface != null)
            UpdateSurfaceLayout();
    }

    private static void OnPlayerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((FlyleafView)d).SetPlayer((Player)e.OldValue);

    private static void OnReplicaPlayerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((FlyleafView)d).SetReplicaPlayer((Player)e.OldValue);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Console.WriteLine($"[FLV] OnLoaded  Player={(Player != null ? "set" : "null")} surface={(surface != null ? "set" : "null")} ActualSize={ActualWidth}x{ActualHeight}");

        if (Player != null && surface == null)
            InitSurface();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
        => DisposeSurface();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (surface != null)
            UpdateSurfaceLayout();
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.IsKeyDown(Key.LeftCtrl) || Player == null)
            return;

        var relativeMousePosition = e.GetPosition(this);
        Point currentDpiPoint = new(relativeMousePosition.X * DpiX, relativeMousePosition.Y * DpiY);

        if (e.Delta > 0)
            Player.Config.Video.ZoomIn(currentDpiPoint);
        else
            Player.Config.Video.ZoomOut(currentDpiPoint);
    }

    private void OnIsFrontBufferAvailableChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        bool isAvailable = (bool)e.NewValue;
        Console.WriteLine($"[FLV] IsFrontBufferAvailableChanged  {e.OldValue} → {isAvailable}");

        if (isAvailable)
            InvalidateVisual();
    }

    private void SetPlayer(Player oldPlayer)
    {
        if (oldPlayer != null)
        {
            oldPlayer.Renderer?.SwapChain.Dispose(rendererFrame: false);
            oldPlayer.Host = null;
            DisposeSurface();
        }

        if (Player == null)
            return;

        Player.Host?.Player_Disposed();
        if (Player == null)
            return;

        Player.Host = this;

        if (IsLoaded && HasVisibleSize())
            InitSurface();
    }

    private void InitSurface()
    {
        if (Player?.Renderer == null)
        {
            Console.WriteLine("[FLV] InitSurface  SKIP — Player or Renderer is null");
            return;
        }

        UpdateDpi();

        var imageSize = GetImagePixelSize();
        var controlSize = GetControlPixelSize();
        nint hwnd = GetWindowHandle();

        Console.WriteLine($"[FLV] InitSurface  image={imageSize.Width}x{imageSize.Height} control={controlSize.Width}x{controlSize.Height} hwnd=0x{hwnd:X}");

        surface = new D3DImageSurface();
        surface.D3DImage.IsFrontBufferAvailableChanged += OnIsFrontBufferAvailableChanged;
        surface.Initialize(Player, imageSize.Width, imageSize.Height, controlSize.Width, controlSize.Height, hwnd);

        Console.WriteLine($"[FLV] InitSurface  surface ready — IsFrontBufferAvailable={surface.D3DImage.IsFrontBufferAvailable} PixelSize={surface.D3DImage.PixelWidth}x{surface.D3DImage.PixelHeight}");

        // Force OnRender to be called so WPF registers as a listener for D3DImage.Changed.
        // Without this, the initial OnRender (before OnLoaded) drew a black fallback rect,
        // so WPF never knows to re-render when D3DImage fires Changed.
        InvalidateVisual();
        Console.WriteLine("[FLV] InitSurface  InvalidateVisual called");
    }

    private void DisposeSurface()
    {
        if (surface == null)
            return;

        Console.WriteLine("[FLV] DisposeSurface  called");
        surface.D3DImage.IsFrontBufferAvailableChanged -= OnIsFrontBufferAvailableChanged;
        surface.Dispose();
        surface = null;
    }

    private void UpdateSurfaceLayout()
    {
        var imageSize = GetImagePixelSize();
        var controlSize = GetControlPixelSize();

        surface.Resize(imageSize.Width, imageSize.Height, controlSize.Width, controlSize.Height);
    }

    private void UpdateDpi()
    {
        var window = Window.GetWindow(this);
        var source = PresentationSource.FromVisual(window);
        if (source == null)
            return;

        DpiX = source.CompositionTarget?.TransformToDevice.M11 ?? 1;
        DpiY = source.CompositionTarget?.TransformToDevice.M22 ?? 1;
    }

    private bool HasVisibleSize() => ActualWidth > 0 && ActualHeight > 0;

    private bool CanDrawSurface() => surface?.D3DImage != null && surface.D3DImage.IsFrontBufferAvailable;

    private nint GetWindowHandle()
    {
        var window = Window.GetWindow(this);
        return window != null ? new WindowInteropHelper(window).EnsureHandle() : IntPtr.Zero;
    }

    private Int32Size GetImagePixelSize()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return new(
            Math.Max(1, (int)Math.Round(ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Round(ActualHeight * dpi.DpiScaleY)));
    }

    private Int32Size GetControlPixelSize()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return new(
            Math.Max(1, (int)Math.Round(RenderSize.Width * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Round(RenderSize.Height * dpi.DpiScaleY)));
    }

    private readonly record struct Int32Size(int Width, int Height);
}
