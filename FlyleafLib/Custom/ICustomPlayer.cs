using FlyleafLib.Zoom;

namespace FlyleafLib.Custom;

public interface ICustomPlayer
{
    ZoomOverviewRenderer OverviewRenderer { get; set; }

    void InitStreamContext(Stream stream);
}
