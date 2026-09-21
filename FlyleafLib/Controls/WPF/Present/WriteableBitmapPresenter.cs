using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FlyleafLib.Controls.WPF.Present;

/// <summary>
/// Software present path: reads frames back to the CPU and blits them into a
/// <see cref="WriteableBitmap"/> shown by an <see cref="Image"/>. Adapter-agnostic
/// and needs no D3D front buffer (works over RDP/headless/locked sessions).
/// </summary>
internal sealed class WriteableBitmapPresenter : IVideoPresenter
{
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private WriteableBitmap _bitmap;
    private int _bmpWidth;
    private int _bmpHeight;

    // Built for a new size and put on screen only once it holds a frame - see Resize.
    private WriteableBitmap _pending;
    private int _pendingWidth;
    private int _pendingHeight;

    private IVideoFrameProvider _provider;

    public FrameworkElement Host => _image;
    public bool IsHardware => false;

    public void Attach(IVideoFrameProvider provider) => _provider = provider;

    /// <summary>
    /// Prepares a bitmap for the new size, without putting it on screen yet.
    /// </summary>
    /// <remarks>
    /// A fresh WriteableBitmap is empty, and the provider drops its frame on a resize, so the earliest
    /// this one can hold anything is the next publish - which arrives a composition frame or more later,
    /// because the swap-chain re-presents on the render thread. Showing it straight away therefore left
    /// the video blank for most frames of a continuous resize, which reads as flicker. The bitmap already
    /// on screen is kept and simply stretched until the new one has content, so the picture never goes
    /// away; the stretch lasts a frame or two, so it is not visible.
    /// </remarks>
    public void Resize(int pixelWidth, int pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0)
            return;

        if (_bitmap != null && _bmpWidth == pixelWidth && _bmpHeight == pixelHeight)
        {
            // Dragged back to the size already on screen - nothing to prepare any more.
            DropPending();
            return;
        }

        if (_pending != null && _pendingWidth == pixelWidth && _pendingHeight == pixelHeight)
            return;

        _pending = new WriteableBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Bgra32, null);
        _pendingWidth = pixelWidth;
        _pendingHeight = pixelHeight;

        // Nothing on screen to keep, so there is nothing to be gained by waiting.
        if (_bitmap == null)
            Promote();
    }

    public void Clear() => _bitmap?.Clear();

    public void Pump()
    {
        var target = _pending ?? _bitmap;
        if (_provider == null || target == null || !_provider.HasPendingFrame)
            return;

        var width = _pending != null ? _pendingWidth : _bmpWidth;
        var height = _pending != null ? _pendingHeight : _bmpHeight;

        bool copied;
        target.Lock();
        try
        {
            copied = _provider.TryCopyCpuInto(target.BackBuffer, target.BackBufferStride, width, height);
            if (copied)
                target.AddDirtyRect(new Int32Rect(0, 0, width, height));
        }
        finally
        {
            target.Unlock();
        }

        // Only now does the new size have a picture in it, so only now is it worth showing.
        if (copied && _pending != null)
            Promote();
    }

    public void Dispose()
    {
        _image.Source = null;
        _bitmap = null;
        _bmpWidth = 0;
        _bmpHeight = 0;
        DropPending();
    }

    private void Promote()
    {
        _bitmap = _pending;
        _bmpWidth = _pendingWidth;
        _bmpHeight = _pendingHeight;
        _image.Source = _bitmap;

        DropPending();
    }

    private void DropPending()
    {
        _pending = null;
        _pendingWidth = 0;
        _pendingHeight = 0;
    }
}
