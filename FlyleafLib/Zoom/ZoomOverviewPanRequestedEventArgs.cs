using System.Windows;

namespace FlyleafLib.Zoom;

public delegate void ZoomOverviewPanRequestedEventHandler(object sender, ZoomOverviewPanRequestedEventArgs e);

public class ZoomOverviewPanRequestedEventArgs(RoutedEvent routedEvent, object source, double panX, double panY)
    : RoutedEventArgs(routedEvent, source)
{
    public double PanX { get; } = panX;
    public double PanY { get; } = panY;
}
