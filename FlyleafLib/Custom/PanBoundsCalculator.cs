namespace FlyleafLib.Custom;

/// <summary>
/// Pure pan/zoom math for constraining a panned viewport to the image it is showing. Kept in sync
/// with FlyleafScrolling's copy of the same name - see that copy's remarks for the derivation.
/// </summary>
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
    /// <paramref name="baselineOffset"/>) from its CURRENT, already zoomed/panned reading, so
    /// <see cref="PanRange"/> can be used without ever needing to catch the player at zoom == 1,
    /// pan == 0 first - a UI that only exists once already zoomed (a minimap shown only when
    /// zoomed in, say) may never pass through that neutral state at all.
    /// </summary>
    /// <remarks>
    /// viewportSize = unzoomedSize * zoom always (pan/center only move the image, they don't resize
    /// it), so unzoomedSize falls out directly. Substituting into
    /// viewportPos = controlSize * pan + baselineOffset - unzoomedSize * (zoom - 1) * center, with
    /// controlSize = unzoomedSize + 2 * baselineOffset, and solving for baselineOffset gives the
    /// rest. Undefined at pan == -0.5 (the denominator is zero there); returns false in that case
    /// rather than dividing by zero - callers should just skip the update for that one call and let
    /// the next one (from a different pan) succeed.
    /// </remarks>
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
