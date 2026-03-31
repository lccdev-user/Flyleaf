using FlyleafLib.MediaFramework.MediaFrame;
using FlyleafLib.MediaFramework.MediaRenderer;

namespace FlyleafLib.Custom;

public interface ICustomPlayer
{
    bool CustomHandlerEnabled { get; }
    void FillCustomPlanes(Renderer sender, VideoFrame frame);
}
