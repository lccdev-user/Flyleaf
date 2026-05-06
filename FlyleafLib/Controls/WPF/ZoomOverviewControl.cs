using FlyleafLib.MediaPlayer;
using FlyleafLib.Zoom;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml;
using Vortice.Wpf;

namespace FlyleafLib.Controls.WPF;

/// <summary>
///ZoomOverviewControl - WPF control for the zoom minimap..
///
/// XAML-Verwendung:
///   <zoom:ZoomOverviewControl x:Name="Minimap"
///       HorizontalAlignment="Right" VerticalAlignment="Bottom"
///       Margin="0,0,16,16" Panel.ZIndex="10" />
///
///   // Code-behind nach Player-Open:
///   Minimap.BindPlayer(player);
/// </summary>
public sealed class ZoomOverviewControl : FrameworkElement, IDisposable
{
    private static readonly Type playerType = typeof(Player);
    private static readonly Type flType = typeof(ZoomOverviewControl);

    // Dependency Properties
    public static readonly DependencyProperty ShowWhenZoomOutProperty =
			DependencyProperty.Register(nameof(ShowWhenZoomOut), typeof(bool),
				typeof(ZoomOverviewControl), new PropertyMetadata(false));

    public static readonly DependencyProperty ShowZoomBoxProperty =
            DependencyProperty.Register(nameof(ShowZoomBox), typeof(bool),
                typeof(ZoomOverviewControl), new PropertyMetadata(true,
                    OnShowZoomBoxChanged));

    public static readonly DependencyProperty PlayerProperty =
        DependencyProperty.Register(nameof(Player), playerType, flType, new(null, OnPlayerChanged));

    public Player Player { get => (Player)GetValue(PlayerProperty); set => SetValue(PlayerProperty, value); }
    public bool ShowWhenZoomOut { get => (bool)GetValue(ShowWhenZoomOutProperty); set => SetValue(ShowWhenZoomOutProperty, value); }
    public bool ShowZoomBox { get => (bool)GetValue(ShowZoomBoxProperty); set => SetValue(ShowZoomBoxProperty, value);  }

    internal LogHandler Log;
    private DrawingSurface        _surface;           
	private ZoomOverviewRenderer  _renderer;
	private Player                _player;
	private bool                  _initialized;
	private bool                  _disposed;
	private bool                  _needSurfaceCleaning;

	// Click-to-pan drag state
	private bool  _isDragging;

	public ZoomOverviewControl()
	{
        InitSurface();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;

        // Clip to bounds (rounded corners done via a clip geometry)
        ClipToBounds = true;

        var uniqueId =  GetUniqueId();
        Log = new(("[#" + uniqueId + "]").PadRight(8, ' ') + " [ZOVC    ] ");
        Log.Debug($"new zoom overview control #{uniqueId} created");
    }

    /// <summary>
    /// Connects the control to a FlyleafLib player.
    /// Must be called on the UI thread.
    /// </summary>
    public void BindPlayer(Player player)
	{
		if (_initialized || _disposed)
			return;
		_player = player ?? throw new ArgumentNullException(nameof(player));

        Log.Debug($"Bind player #{_player.PlayerId} with zoom overview control");
        _renderer = new ZoomOverviewRenderer(player, (int)ActualWidth, (int)ActualHeight);
		_renderer.InitializeD3Resource(_surface);   // neue, vereinfachte Init-Variante
        _renderer.ShowZoomBox = ShowZoomBox;

        // Update-Trigger
        CompositionTarget.Rendering += OnCompositionRendering;
        player.Config.Video.PropertyChanged += ZoomOverviewPropertyChanged;

		UpdateVisibility();
		_initialized = true;
	}

    /// <summary>
    /// Disconnects the control from the FlyleafLib player.
    /// Must be called on the UI thread.
    /// </summary>
    public void UnbindPlayer()
	{
		if (!_initialized || _disposed)
			return;

        Log.Debug($"Unbind player from zoom overview control");
        _initialized = false;

        CompositionTarget.Rendering -= OnCompositionRendering;

        if (_player is not null)
			_player.Config.Video.PropertyChanged -= ZoomOverviewPropertyChanged;

        _player = null;

        _renderer?.Dispose();
		_renderer = null;
		_needSurfaceCleaning = true;
	}

	private void ZoomOverviewPropertyChanged(object sender, PropertyChangedEventArgs e)
	{
		if (_player is null || !_initialized || _disposed)
			return;

		if (e.PropertyName is nameof(_player.Config.Video.Zoom)
							   or nameof(_player.Config.Video.PanXOffset)
							   or nameof(_player.Config.Video.PanYOffset))
		{
			UpdateVisibility();
			RequestRender();
		}
	}

    private void SetPlayer(Player oldPlayer)
    {
        Log.Debug("SetPlayer()");
        if (oldPlayer != null)
            UnbindPlayer();

        if (Player == null)
            return;

        InitSurface();
        BindPlayer(Player);
    }

    private void InitSurface()
    {
        _surface = new DrawingSurface();
        _surface.Draw += OnDrawingSurfaceRender;

        AddVisualChild(_surface);

        // Mouse interaction
        _surface.MouseLeftButtonDown += OnMouseDown;
        _surface.MouseLeftButtonUp += OnMouseUp;
        _surface.MouseMove += OnMouseMove;
    }

    private void DisposeSurface()
    {
        Log.Debug($"Dispose: disposed {_disposed}");
        if (_surface != null)
        {
            RemoveVisualChild(_surface);

            _surface.Draw -= OnDrawingSurfaceRender;
            _surface.MouseLeftButtonDown -= OnMouseDown;
            _surface.MouseLeftButtonUp -= OnMouseUp;
            _surface.MouseMove -= OnMouseMove;
        }

        (_surface as IDisposable)?.Dispose();
        _surface = null;
    }

    //  DrawingSurface.Draw Callback
    /// <summary>
    /// Called by DrawingSurface when a new frame is needed.    
    /// </summary>
    private void OnDrawingSurfaceRender(object sender, DrawEventArgs args)
	{
		if (_needSurfaceCleaning && !_disposed)
			ClearSurface(args);
		if (!_initialized || _disposed || _renderer == null)
			return;

		_renderer.RenderIntoTexture(args.Surface.ColorTexture, args);

		args.InvalidateSurface();
	}

	private void ClearSurface(DrawEventArgs args)
	{
		args.Context.OMSetRenderTargets(args.Surface.ColorTextureView);
		args.Context.ClearRenderTargetView(args.Surface.ColorTextureView, new Vortice.Mathematics.Color4(0f, 0f, 0f, 1f));
		args.Context.Draw(3, 0);
		_needSurfaceCleaning = false;
	}

    // Trigger Helper
    private void OnCompositionRendering(object sender, EventArgs e)
	{
		if (!_initialized || _disposed)
			return;
        if ((bool)!_renderer?.IsInitialized)
            _renderer?.InitializeD3Resource(_surface);
		RequestRender();
	}

    /// <summary>Instructs DrawingSurface to re-execute OnRender.</summary>
    private void RequestRender()
	{
        // DrawingSurface.Invalidate() is thread-safe and triggers
        // another OnRender call on the next compositing tick.
        _surface.Invalidate();
	}

	//  Visibility
	private void UpdateVisibility()
	{
		if (_player == null)
			return;
		bool zoomed = _player.Config.Video.Zoom > 100;
		Visibility = (zoomed || ShowWhenZoomOut) ? Visibility.Visible : Visibility.Collapsed;
	}

	//  DependencyProperty Callback
	private static void OnShowZoomBoxChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (ZoomOverviewControl)d;
        if (ctrl._renderer is not null && ctrl._initialized)
            ctrl._renderer.ShowZoomBox = (bool)e.NewValue;
    }

    private static void OnPlayerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ZoomOverviewControl)d).SetPlayer((Player)e.OldValue);


    //  Click-to-pan
    private void OnMouseDown(object sender, MouseButtonEventArgs e)
	{
		if (_player == null)
			return;
		_isDragging = true;
		_surface.CaptureMouse();
		PanToPosition(e.GetPosition(_surface));
		e.Handled = true;
	}

	private void OnMouseMove(object sender, MouseEventArgs e)
	{
		if (!_isDragging || _player == null)
			return;
		PanToPosition(e.GetPosition(_surface));
	}

	private void OnMouseUp(object sender, MouseButtonEventArgs e)
	{
		_isDragging = false;
		_surface.ReleaseMouseCapture();
	}

	private void PanToPosition(Point pos)
	{
		double u    = Math.Clamp(pos.X / ActualWidth,  0, 1);
		double v    = Math.Clamp(pos.Y / ActualHeight, 0, 1);
		double panX = (u - 0.5) * 2.0;
		double panY = (v - 0.5) * 2.0;

		_player.Config.Video.PanXOffset = -panX;
		_player.Config.Video.PanYOffset = -panY;
	}

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Log.Debug($"ZoomOverviewControl: Loaded, player {(Player is Player ? "set" : "null")}");
        if (Player != null)
            BindPlayer(Player);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Log.Debug($"ZoomOverviewControl: OnUnloaded, {(Player is Player ? "set" : "null")}");
        if (Player != null)
            UnbindPlayer();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _surface?.InvalidateMeasure();
        _renderer?.UpdateSize((int)e.NewSize.Width, (int)e.NewSize.Height);
    }

    //  Visual tree
    protected override int VisualChildrenCount => 1;
	protected override Visual GetVisualChild(int index) => _surface;

	protected override Size MeasureOverride(Size availableSize)
	{
		var size = new Size(ActualWidth, ActualHeight);
		_surface.Measure(size);
		return size;
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		_surface.Arrange(new Rect(finalSize));
		return finalSize;
	}

	//  IDisposable
	public void Dispose()
	{
        Log.Debug($"Dispose: disposed {_disposed}");
		if (_disposed)
			return;

        UnbindPlayer();
        DisposeSurface();
        _disposed = true;

		_renderer?.Dispose();
        _renderer = null;
	}
}
