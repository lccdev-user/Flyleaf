using FlyleafLib.MediaFramework.MediaFrame;
using FlyleafLib.MediaFramework.MediaRenderer;
using FlyleafLib.Zoom;

namespace FlyleafLib.Custom;

public interface ICustomPlayer
{
    ZoomOverviewRenderer OverviewRenderer { get; set; }
    bool CustomHandlerEnabled { get; }
    void FillCustomPlanes(Renderer sender, VideoFrame frame);

    void InitStreamContext(Stream stream);
}
