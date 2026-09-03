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
    /// Recovers a live viewport's unzoomed geometry (<paramref name="unzoomedSize"/>,
    /// <paramref name="baselineOffset"/>) from its current, already zoomed/panned reading
    /// </summary>
    public static bool TryInvertBaseline(double viewportPos, double viewportSize, double zoom, double center, double pan, out double unzoomedSize, out double baselineOffset)
    {
        unzoomedSize = zoom > 0 ? viewportSize / zoom : 0;

        var denominator = 2 * pan + 1;
        if (Math.Abs(denominator) < 1e-6)
        {
            baselineOffset = 0;
            return false;
        }

        baselineOffset = (viewportPos - unzoomedSize * pan + unzoomedSize * (zoom - 1) * center) / denominator;
        return true;
    }
}
