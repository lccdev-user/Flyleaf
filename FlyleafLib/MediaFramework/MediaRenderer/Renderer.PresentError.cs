using System.Drawing.Imaging;
using Vortice;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using Bitmap = System.Drawing.Bitmap;
using Rectangle = System.Drawing.Rectangle;

namespace FlyleafLib.MediaFramework.MediaRenderer;
#nullable enable
public  partial class Renderer
{
    internal ID2D1Device?          deviceErrorScreen;
    internal ID2D1DeviceContext?   contextErrorScreen;

    internal ID2D1Bitmap?          bitmapErrorImage;
    internal ID2D1SolidColorBrush  brush2dFill;
    internal ID2D1SolidColorBrush  brush2dText;
    internal IDWriteTextFormat     textFormat;


    internal bool errorScreenEnabled = false;
    internal Bitmap? errorBitmap;

    public bool ErrorScreenEnabled
    {
        get => errorScreenEnabled;
        set
        {
            if (errorScreenEnabled != value)
            {
                if (value)
                    SwapChain?.SetupErrorScreenContext();
                else
                {
                    ErrorMessage = string.Empty;
                    SwapChain?.DisposeErrorScreenContext();
                }
                errorScreenEnabled = value;
            }
        }
    }
    public string ErrorMessage { get; set; } = string.Empty;

    public Bitmap? ErrorImage
    {
        get => errorBitmap;
        set
        {
            if (errorBitmap != value)
            {
                if (value == null)
                    ClearErrorImageField();
                else
                    SetErrorImageField(value);
            }
        }
    }

    public int ErrorCode { get; set; }


    private void SetErrorImageField(Bitmap bitmap)
    {
        lock (lockDevice)
        {
            errorBitmap?.Dispose();
            errorBitmap = bitmap;
            SetErrorImage(bitmap);
            RenderRequest();
        }
    }

    private void ClearErrorImageField()
    {
        lock (lockDevice)
        {
            if (contextErrorScreen != null)
                contextErrorScreen.Target = null;
            bitmapErrorImage?.Dispose();
            bitmapErrorImage = null;
            errorBitmap?.Dispose();
            errorBitmap = null;
            RenderRequest();
        }
    }

    internal void SetErrorImage(Bitmap bitmap)
    {
        lock (lockDevice)
        {
            bitmapErrorImage?.Dispose();
            bitmapErrorImage = null;

            if (contextErrorScreen == null)
                return;

            BitmapProperties bp = new BitmapProperties(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied));
            Rectangle rect = new(0, 0, bitmap.Width, bitmap.Height);
            BitmapData bitmapData = bitmap.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                SizeI size = new(bitmap.Width,bitmap.Height);
                bitmapErrorImage = contextErrorScreen.CreateBitmap(size, bp);
                bitmapErrorImage?.CopyFromMemory(bitmapData.Scan0, (uint)bitmapData.Stride);
            }
            catch (Exception e)
            {
                Log.Error(e.Message);
            }
            finally
            {
                bitmap.UnlockBits(bitmapData);
            }
        }
    }
    internal void ShowErrorScreen(bool force = false)
    {
        if (force)
        {
            lock (lockDevice)
            {
                if (SwapChain.Disposed)
                    return;

                SubsDispose();
                context.OMSetRenderTargets(SwapChain.BackBufferRtv);
                context.ClearRenderTargetView(SwapChain.BackBufferRtv, ucfg.flBackColor);

                try
                {
                    if (ErrorScreenEnabled)
                    {
                        if (errorBitmap != null && bitmapErrorImage == null)
                            SetErrorImage(errorBitmap);
                        DrawErrorScreen();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex.Message);
                }
                SwapChain.Present(1, PresentFlags.None);
            }
        }
        else
        {
            try
            {
                if (ErrorScreenEnabled)
                {
                    if (errorBitmap != null && bitmapErrorImage == null)
                        SetErrorImage(errorBitmap);
                    DrawErrorScreen();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex.Message);
            }
        }
    }

    private void PresentFisheyeErrorScreen()
    {
        // todo: implement this functionality later
        // if (errorBitmap is not Bitmap srcBitmap || !FisheyeViewEnabled)
        //    return;
        Bitmap? dstBitmap = null;
        try
        {/*
            int width = FisheyeRenderer.Max(r => r.ControlWidth);
            int height = FisheyeRenderer.Max(r => r.ControlHeight);

            float scale = Math.Min((float)width / srcBitmap.Width, (float)height / srcBitmap.Height);
            var scaleWidth  = (int)(srcBitmap.Width  * scale);
            var scaleHeight = (int)(srcBitmap.Height * scale);
            var sideX = (width - scaleWidth) / 2;
            var sideY = (height - scaleHeight) / 2;

            var scaledRect = new Rectangle(sideX, sideY, scaleWidth, scaleHeight);
            var dstRect = new Rectangle(0,0,width,height);
            var brush = new SolidBrush(SystemColor.Green);
            dstBitmap = new Bitmap(width, height);

            var graph = System.Drawing.Graphics.FromImage(dstBitmap);
            graph.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.High;
            graph.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            graph.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graph.FillRectangle(brush, new RectangleF(0, 0, width, height));
            graph.DrawImage(srcBitmap, scaledRect);


            for (int i = 0; i < FisheyeRenderer.Length; i++)
            {
                if (FisheyeRenderer[i] == null)
                    continue;
                PresentFisheyeBitmap(i, dstBitmap, dstRect);
            }
            */
        }
        catch (Exception ex)
        {
            Log.Error(ex.Message);
        }
        finally
        {
            dstBitmap?.Dispose();
            dstBitmap = null;
        }
    }
    private Viewport GetErrorScreenViewport(int Width, int Height)
    {
        int sideX, sideY;

        Viewport viewport = Viewport;
        if (Viewport.Width == 0 || viewport.Height == 0 )
        {
            viewport.X = 0;
            viewport.Y = 0;
            viewport.Width = ControlWidth;
            viewport.Height = ControlHeight;
        }

        if (Width != 0 && Height != 0)
        {
            float ratio = (float)Width / Height;

            if (Height > Width)
            {
                viewport.Width = viewport.Height * ratio;
                sideX = (int)(ControlWidth - (ControlHeight * ratio));
                viewport.Y = 0;
                viewport.X = sideX / 2;
            }
            else
            {
                viewport.Height = viewport.Width / ratio;
                sideY = (int)(ControlHeight - (ControlWidth / ratio));
                viewport.X = 0;
                viewport.Y = sideY / 2;
            }
        }
        return viewport;
    }

    private void DrawErrorScreen()
    {
        if (!ErrorScreenEnabled)
            return;

        int Width = 0;
        int Height = 0;

        if (bitmapErrorImage != null)
        {
            Width = bitmapErrorImage.PixelSize.Width;
            Height = bitmapErrorImage.PixelSize.Height;
        }
        Viewport vp = GetErrorScreenViewport(Width, Height);

        RawRectF rectf =  new(vp.X, vp.Y, vp.X + vp.Width, vp.Y + vp.Height);
        var dx = vp.Width / 4;
        var dy = vp.Height / 3;
        Rect layoutRect = new(vp.X + dx, vp.Y + dy, vp.Width - 2 * dx, vp.Height - 2 * dy);

        contextErrorScreen?.BeginDraw();
        try
        {
            if (bitmapErrorImage != null)
            {
                Rect dstRect = new Rect(0.0F, 0.0F, vp.Width, vp.Height);
                var size = bitmapErrorImage.Size;
                Rect srcRect = new Rect(0.0F, 0.0F, size.Width , size.Height);
                contextErrorScreen?.DrawBitmap(bitmapErrorImage, dstRect, 1.0f, BitmapInterpolationMode.Linear, srcRect);
            }
            else if (ErrorMessage.Length > 0)
            {
                contextErrorScreen?.FillRectangle(rectf, brush2dFill);
                contextErrorScreen?.DrawText(ErrorMessage, textFormat, layoutRect, brush2dText);
            }
        }
        catch (Exception e)
        {
            Log.Error(e.Message);
        }
        finally
        {
            contextErrorScreen?.EndDraw();
        }
    }

    private void DisplayErrorImageProcessRequest()
    {
        vpRequests = vpRequestsIn;
        vpRequestsIn = VPRequestType.Empty;

        if (VideoProcessor == VideoProcessors.D3D11)
        {
            vpRequests = vpRequestsIn;

            if (vpRequests.HasFlag(VPRequestType.Resize))
                D3SetSize();

            if (vpRequests.HasFlag(VPRequestType.AspectRatio))
                SetAspectRatio();

            if (vpRequests.HasFlag(VPRequestType.Viewport))
                D3SetViewport(ControlWidth, ControlHeight);
        }
        else
        {
            if (vpRequests.HasFlag(VPRequestType.Resize))
                SetSize();

            if (vpRequests.HasFlag(VPRequestType.AspectRatio))
                SetAspectRatio();

            if (vpRequests.HasFlag(VPRequestType.Viewport))
                FLSetViewport();
        }

        context.OMSetRenderTargets(SwapChain.BackBufferRtv);
        context.ClearRenderTargetView(SwapChain.BackBufferRtv, ucfg.flBackColor);
        if (ErrorScreenEnabled)
            ShowErrorScreen();
        SwapChain.Present(1, PresentFlags.None);
    }
}
#nullable disable
