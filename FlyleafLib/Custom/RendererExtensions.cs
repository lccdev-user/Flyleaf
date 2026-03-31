using FlyleafLib.MediaFramework.MediaRenderer;

namespace FlyleafLib.Custom;

internal static class RendererExtensions
{
    internal static double ValidateZoom(this IVP vp, double zoom) => vp is ICustomRenderer renderer ? renderer.ValidateZoom(zoom) : zoom;
}
