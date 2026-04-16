using FlyleafLib.MediaPlayer;
using FlyleafLib.Zoom;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Vortice.Wpf;

namespace FlyleafLib.Controls.WPF;

/// <summary>
/// WPF-Overlay-Control für die Zoom-Minimap.
///
/// Basiert auf Vortice.Wpf.DrawingSurface — kein manueller D3D9-Interop,
/// kein D3DImage, kein P/Invoke. DrawingSurface verwaltet den
/// D3D9-Shared-Surface intern und liefert per Draw-Callback
/// eine ID3D11Texture2D als fertiges Render-Target.
///
///
/// XAML-Verwendung:
///   <zoom:ZoomOverlayControl x:Name="Minimap"
///       HorizontalAlignment="Right" VerticalAlignment="Bottom"
///       Margin="0,0,16,16" Panel.ZIndex="10" />
///
///   // Code-behind nach Player-Open:
///   Minimap.BindPlayer(player);
/// </summary>
public sealed class ZoomOverlayControl : FrameworkElement, IDisposable
{
	// Dependency Properties
	public static readonly DependencyProperty ShowWhenZoom1Property =
			DependencyProperty.Register(nameof(ShowWhenZoom1), typeof(bool),
				typeof(ZoomOverlayControl), new PropertyMetadata(false));

    public static readonly DependencyProperty ShowZoomBoxProperty =
            DependencyProperty.Register(nameof(ShowZoomBox), typeof(bool),
                typeof(ZoomOverlayControl), new PropertyMetadata(true,
                    OnShowZoomBoxChanged));

	
	public bool ShowWhenZoom1 { get => (bool)GetValue(ShowWhenZoom1Property); set => SetValue(ShowWhenZoom1Property, value); }
    public bool ShowZoomBox { get => (bool)GetValue(ShowZoomBoxProperty); set => SetValue(ShowZoomBoxProperty, value);  }


	// ── Internal state ───────────────────────────────────────────────────
	private DrawingSurface        _surface;           // Vortice.Wpf control
	private ZoomOverviewRenderer  _renderer;
	private Player                _player;
	private bool                  _initialized;
	private bool                  _disposed;
	private bool                  _needSurfaceCleaning;

	// Click-to-pan drag state
	private bool  _isDragging;

	//  Constructor
	public ZoomOverlayControl()
	{
		// Create the DrawingSurface child — it owns ALL D3D9/D3DImage work
		_surface = new DrawingSurface();
		_surface.Draw += OnDrawingSurfaceRender;

		AddVisualChild(_surface);

		// Mouse interaction
		_surface.MouseLeftButtonDown += OnMouseDown;
		_surface.MouseLeftButtonUp += OnMouseUp;
		_surface.MouseMove += OnMouseMove;
        SizeChanged += OnSizeChanged;
		// Clip to bounds (rounded corners done via a clip geometry)
		ClipToBounds = true;
	}

	// Public API

	/// <summary>
	/// Verbindet das Control mit einem FlyleafLib Player.
	/// Muss auf dem UI-Thread aufgerufen werden.
	/// </summary>
	public void BindPlayer(Player player)
	{
		if (_initialized || _disposed)
			return;
		_player = player ?? throw new ArgumentNullException(nameof(player));

		// ZoomOverviewRenderer bekommt jetzt KEIN D3DImage mehr —
		// das Render-Target kommt direkt von DrawingSurface.OnRender.
		_renderer = new ZoomOverviewRenderer(player, (int)ActualWidth, (int)ActualHeight);
		_renderer.InitializeWithoutD3DImage(_surface);   // neue, vereinfachte Init-Variante
        _renderer.ShowZoomBox = ShowZoomBox;
		// Update-Trigger

		// 1) Jeder WPF-Compositing-Frame
		CompositionTarget.Rendering += OnCompositionRendering;

		// 2) Direkter Trigger bei Zoom/Pan-Änderungen
		player.Config.Video.PropertyChanged += ZoomOverviewPropertyChanged;

		UpdateVisibility();
		_initialized = true;
	}

	/// <summary>
	/// Deaktiviert die Verbindung zwischen dem FlyleafLib-Player und dem Control.
	/// Muss auf dem UI-Thread aufgerufen werden.
	/// </summary>
	public void UnbindPlayer()
	{
		if (!_initialized || _disposed)
			return;

		_initialized = false;
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

	//  DrawingSurface.Draw Callback
	/// <summary>
	/// Wird von DrawingSurface aufgerufen, wenn ein neuer Frame benötigt wird.
	/// <paramref name="args"/>.RenderTarget ist die von Vortice.Wpf verwaltete
	/// ID3D11Texture2D — wir rendern direkt hinein.
	/// </summary>
	private void OnDrawingSurfaceRender(object sender, DrawEventArgs args)
	{
		if (_needSurfaceCleaning && !_disposed)
			ClearSurface(args);
		if (!_initialized || _disposed || _renderer == null)
			return;

		// DrawEventArgs.Surface.ColorTexture        → ID3D11Texture2D des Vortice.Wpf-Targets
		// DrawEventArgs.Device        → ID3D11Device (kann von Renderer abweichen
		//                               wenn multi-adapter, daher prüfen)
		_renderer.RenderIntoTexture(args.Surface.ColorTexture, args);

		// Signal: wir haben in diesen Frame gerendert, WPF soll ihn präsentieren
		args.InvalidateSurface();
	}

	private void ClearSurface(DrawEventArgs args)
	{
		args.Context.OMSetRenderTargets(args.Surface.ColorTextureView);
		args.Context.ClearRenderTargetView(args.Surface.ColorTextureView, new Vortice.Mathematics.Color4(0f, 0f, 0f, 1f));
		args.Context.Draw(3, 0);
		_needSurfaceCleaning = false;
	}

	// Trigger-Helfer
	private void OnCompositionRendering(object sender, EventArgs e)
	{
		if (!_initialized || _disposed)
			return;
		RequestRender();
	}

	/// <summary>Fordert DrawingSurface auf, OnRender erneut auszuführen.</summary>
	private void RequestRender()
	{
		// DrawingSurface.Invalidate() ist thread-safe und löst einen
		// weiteren OnRender-Aufruf beim nächsten Compositing-Tick aus.
		_surface.Invalidate();
	}

	//  Visibility
	private void UpdateVisibility()
	{
		if (_player == null)
			return;
		bool zoomed = _player.Config.Video.Zoom > 100;
		Visibility = (zoomed || ShowWhenZoom1) ? Visibility.Visible : Visibility.Collapsed;
	}

	//  DependencyProperty Callback
	private static void OnShowZoomBoxChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (ZoomOverlayControl)d;
        if (ctrl._renderer is not null)
            ctrl._renderer.ShowZoomBox = (bool)e.NewValue;
    }

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
		if (_disposed)
			return;
		_disposed = true;

		CompositionTarget.Rendering -= OnCompositionRendering;

		_surface.Draw -= OnDrawingSurfaceRender;
		// DrawingSurface implementiert IDisposable ab Vortice.Wpf 3.8+
		(_surface as IDisposable)?.Dispose();

		_renderer?.Dispose();
	}
}
