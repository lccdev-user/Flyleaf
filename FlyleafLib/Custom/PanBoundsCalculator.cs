namespace FlyleafLib.Custom;

public static class PanBoundsCalculator
{
    public static (double Min, double Max) PanRange(double zoom, double center, double unzoomedSize, double baselineOffset)
    {
        if (zoom <= 1)
            return (0, 0);

        var controlSize = unzoomedSize + 2 * baselineOffset;
        if (controlSize <= 0)
            return (0, 0);

        var coverRightEdge = (unzoomedSize * (zoom - 1) * center - baselineOffset) / controlSize;
        var coverLeftEdge = (unzoomedSize * (zoom - 1) * (center - 1) + baselineOffset) / controlSize;

        return (Math.Min(coverLeftEdge, coverRightEdge), Math.Max(coverLeftEdge, coverRightEdge));
    }

    /// <summary>Clamps a pan offset into <see cref="PanRange"/> for the same axis.</summary>
    public static double ClampPan(double pan, double zoom, double center, double unzoomedSize, double baselineOffset)
    {
        var (min, max) = PanRange(zoom, center, unzoomedSize, baselineOffset);
        return Math.Clamp(pan, min, max);
    }

    /// <summary>
    /// Where a point on one axis of the zoom overview sits in the picture, 0..1.
    /// </summary>    
    public static double OverviewFraction(double position, double pictureStart, double pictureExtent, double controlExtent)
    {
        if (pictureExtent <= 0)
        {
            pictureStart = 0;
            pictureExtent = controlExtent;
        }

        return pictureExtent > 0 ? Math.Clamp((position - pictureStart) / pictureExtent, 0, 1) : 0;
    }

}
