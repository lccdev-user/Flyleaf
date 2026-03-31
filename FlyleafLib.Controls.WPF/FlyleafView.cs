using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using FlyleafLib.MediaPlayer;

namespace FlyleafLib.Controls.WPF;

/// <summary>
/// A WPF FrameworkElement that renders a Flyleaf <see cref="Player"/> directly into the
/// WPF visual tree via D3DImage, avoiding the Win32 airspace limitation of
/// <see cref="FlyleafLib.Controls.WPF.FlyleafHost"/>.
/// </summary>
public class FlyleafView : FrameworkElement, IHostPlayer, IDisposable
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
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        Console.WriteLine($"[FLV] OnLoaded  Player={(Player != null ? "set" : "null")} _surface={(_surface != null ? "set" : "null")} ActualSize={ActualWidth}x{ActualHeight}");
        if (Player != null && _surface == null)
            InitSurface();
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

        var dpi  = VisualTreeHelper.GetDpi(this);
        int pixW = Math.Max(1, (int)(ActualWidth  * dpi.DpiScaleX));
        int pixH = Math.Max(1, (int)(ActualHeight * dpi.DpiScaleY));

        Console.WriteLine($"[FLV] InitSurface  pixW={pixW} pixH={pixH} hwnd=0x{hwnd:X} dpi={dpi.DpiScaleX:F2}");

        _surface = new D3DImageSurface();
        _surface.D3DImage.IsFrontBufferAvailableChanged += OnIsFrontBufferAvailableChanged;
        _surface.Initialize(Player, pixW, pixH, hwnd);

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

        if (_surface == null) return;

        var dpi  = VisualTreeHelper.GetDpi(this);
        int pixW = Math.Max(1, (int)(sizeInfo.NewSize.Width  * dpi.DpiScaleX));
        int pixH = Math.Max(1, (int)(sizeInfo.NewSize.Height * dpi.DpiScaleY));

        _surface.Resize(pixW, pixH);
        InvalidateVisual();
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
}
