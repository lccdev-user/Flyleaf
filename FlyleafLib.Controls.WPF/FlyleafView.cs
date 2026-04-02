using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using FlyleafLib.MediaPlayer;
using System.Windows.Controls;
using System.Windows.Input;

namespace FlyleafLib.Controls.WPF;

/// <summary>
/// A WPF FrameworkElement that renders a Flyleaf <see cref="Player"/> directly into the
/// WPF visual tree via D3DImage, avoiding the Win32 airspace limitation of
/// <see cref="FlyleafLib.Controls.WPF.FlyleafHost"/>.
/// </summary>
public class FlyleafView : Decorator, IHostPlayer, IDisposable
{
    static readonly Type _flType   = typeof(FlyleafView);
    static readonly Type _playerType = typeof(Player);

    D3DImageSurface _surface;
    bool            _isFullScreen;

    #region Dependency Properties

    public Player Player
    {
        get => (Player)GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }
    public static readonly DependencyProperty PlayerProperty =
        DependencyProperty.Register(nameof(Player), _playerType, _flType, new(null, OnPlayerChanged));

    static void OnPlayerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((FlyleafView)d).SetPlayer((Player)e.OldValue);

    public Player ReplicaPlayer
    {
        get => (Player)GetValue(ReplicaPlayerProperty);
        set => SetValue(ReplicaPlayerProperty, value);
    }
    public static readonly DependencyProperty ReplicaPlayerProperty =
    DependencyProperty.Register(nameof(ReplicaPlayer), typeof(Player), _flType, new PropertyMetadata(null, OnReplicaPlayerChanged));

    private static void OnReplicaPlayerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        FlyleafView host = d as FlyleafView;

        host.SetReplicaPlayer((Player)e.OldValue);
    }

    public object HostDataContext
    {
        get => GetValue(HostDataContextProperty);
        set => SetValue(HostDataContextProperty, value);
    }
    public static readonly DependencyProperty HostDataContextProperty =
        DependencyProperty.Register(nameof(HostDataContext), typeof(object), _flType, new(null));
    #endregion

    public FlyleafView()
    {
        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        MouseWheel += OnMouseWheel;
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        Console.WriteLine($"[FLV] OnLoaded  Player={(Player != null ? "set" : "null")} _surface={(_surface != null ? "set" : "null")} ActualSize={ActualWidth}x{ActualHeight}");
        if (Player != null && _surface == null)
            InitSurface();
    }

    void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_surface == null)
            return;

        UpdateSurfaceLayout();
    }

    void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DisposeSurface();
    }

    void SetPlayer(Player oldPlayer)
    {
        if (oldPlayer != null)
        {
            oldPlayer.Renderer?.SwapChain.Dispose(rendererFrame: false);
            oldPlayer.Host = null;
            DisposeSurface();
        }

        if (Player == null) return;

        // Disconnect player from any previous host
        Player.Host?.Player_Disposed();
        if (Player == null) return; // Player_Disposed may have cleared Player

        Player.Host = this;

        if (IsLoaded && ActualWidth > 0 && ActualHeight > 0)
            InitSurface();
    }

    public void  SetReplicaPlayer(Player oldPlayer)
    {
        // temporary placeholder
    }

    void InitSurface()
    {
        if (Player?.Renderer == null)
        {
            Console.WriteLine("[FLV] InitSurface  SKIP — Player or Renderer is null");
            return;
        }

        var window = Window.GetWindow(this);
        nint hwnd  = window != null ? new WindowInteropHelper(window).EnsureHandle() : IntPtr.Zero;

        var imageSize = GetImagePixelSize();
        var controlSize = GetControlPixelSize();

        Console.WriteLine($"[FLV] InitSurface  image={imageSize.Width}x{imageSize.Height} control={controlSize.Width}x{controlSize.Height} hwnd=0x{hwnd:X}");

        _surface = new D3DImageSurface();
        _surface.D3DImage.IsFrontBufferAvailableChanged += OnIsFrontBufferAvailableChanged;
        _surface.Initialize(Player, imageSize.Width, imageSize.Height, controlSize.Width, controlSize.Height, hwnd);

        Console.WriteLine($"[FLV] InitSurface  surface ready — IsFrontBufferAvailable={_surface.D3DImage.IsFrontBufferAvailable} PixelSize={_surface.D3DImage.PixelWidth}x{_surface.D3DImage.PixelHeight}");

        // Force OnRender to be called so WPF registers as a listener for D3DImage.Changed.
        // Without this, the initial OnRender (before OnLoaded) drew a black fallback rect,
        // so WPF never knows to re-render when D3DImage fires Changed.
        InvalidateVisual();
        Console.WriteLine("[FLV] InitSurface  InvalidateVisual called");
    }

    void DisposeSurface()
    {
        if (_surface == null) return;
        Console.WriteLine("[FLV] DisposeSurface  called");
        _surface.D3DImage.IsFrontBufferAvailableChanged -= OnIsFrontBufferAvailableChanged;
        _surface.Dispose();
        _surface = null;
    }

    void OnIsFrontBufferAvailableChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        bool isAvailable = (bool)e.NewValue;
        Console.WriteLine($"[FLV] IsFrontBufferAvailableChanged  {e.OldValue} → {isAvailable}");
        if (isAvailable)
            InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var rect = new Rect(RenderSize);
        if (_surface?.D3DImage != null && _surface.D3DImage.IsFrontBufferAvailable)
        {
            Console.WriteLine($"[FLV] OnRender  DrawImage D3DImage={_surface.D3DImage.PixelWidth}x{_surface.D3DImage.PixelHeight} rect={rect.Width:F0}x{rect.Height:F0}");
            dc.DrawImage(_surface.D3DImage, rect);
        }
        else
        {
            Console.WriteLine($"[FLV] OnRender  FALLBACK black rect  _surface={(_surface != null ? "set" : "null")} IsFrontBufferAvailable={_surface?.D3DImage?.IsFrontBufferAvailable}");
            dc.DrawRectangle(Brushes.Black, null, rect);
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        if (_surface != null)
            UpdateSurfaceLayout();
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.IsKeyDown(Key.LeftCtrl))
            return;

        var relativeMousePosition = e.GetPosition(this);
        var center = new Point(relativeMousePosition.X / ActualWidth, relativeMousePosition.Y / ActualHeight);
        Player.Config.Video.ZoomCenter = center;

        var isZoomIn = e.Delta > 0;
        if (isZoomIn)
            Player.Config.Video.ZoomIn();
        else
            Player.Config.Video.ZoomOut();
        Console.WriteLine($"{Player.Config.Video.Zoom} | {Player.Config.Video.ZoomCenter}");
        Console.WriteLine($"{Player.Renderer.Viewport.Width / ActualWidth}");
    }

    #region IHostPlayer

    public bool Player_CanHideCursor() => IsMouseOver;

    public bool Player_GetFullScreen() => _isFullScreen;

    public void Player_SetFullScreen(bool value)
    {
        _isFullScreen = value;
        var window = Window.GetWindow(this);
        if (window == null) return;

        if (value)
        {
            window.WindowStyle     = WindowStyle.None;
            window.ResizeMode      = ResizeMode.NoResize;
            window.WindowState     = WindowState.Maximized;
        }
        else
        {
            window.WindowStyle     = WindowStyle.SingleBorderWindow;
            window.ResizeMode      = ResizeMode.CanResize;
            window.WindowState     = WindowState.Normal;
        }
    }

    public void Player_RatioChanged(double keepRatio)
    {
        // WPF layout handles sizing; no explicit resize needed.
    }

    public bool Player_HandlesRatioResize(int width, int height) => false;

    public void Player_Disposed()
        => Dispatcher.BeginInvoke(() => Player = null);

    #endregion

    public void Dispose()
    {
        DisposeSurface();
        if (Player != null)
        {
            var p = Player;
            Player = null; // clears via OnPlayerChanged
            p.Host = null;
        }
    }

    void UpdateSurfaceLayout()
    {
        var imageSize = GetImagePixelSize();
        var controlSize = GetControlPixelSize();

        _surface.Resize(imageSize.Width, imageSize.Height, controlSize.Width, controlSize.Height);
        InvalidateVisual();
    }

    Int32Size GetImagePixelSize()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return new(
            Math.Max(1, (int)Math.Round(ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Round(ActualHeight * dpi.DpiScaleY)));
    }

    Int32Size GetControlPixelSize()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return new(
            Math.Max(1, (int)Math.Round(RenderSize.Width * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Round(RenderSize.Height * dpi.DpiScaleY)));
    }

    readonly record struct Int32Size(int Width, int Height);
}
