using FlyleafLib.Custom;

namespace FlyleafLib.MediaPlayer;

public unsafe partial class Player
{
    public bool FrameSearchOneStep(out long frameTimestamp)
    {
        frameTimestamp = 0;

        if (ReversePlayback || !CanPlay || VideoDecoder.CodecCtx == null)
            return true;
        try
        {
            decoder.GetVideoFrame();

            if (!vFrames.TryDequeue(out var vFrame))
                return true;

            vFrame.Id = showFrameCount;

            var skipFrame = VideoDemuxer.SkipFrameBySearch(VideoDemuxer.ToCustomTimestamp(vFrame.Timestamp / Ticks.InOneMillisecond));
            
            if (!skipFrame)
            {
                Renderer.RenderRequest(vFrame);
                frameTimestamp = VideoDemuxer.ToCustomTimestamp(vFrame.Timestamp / Ticks.InOneMillisecond);
            }

            UpdateCurTime(vFrame.Timestamp);
            showFrameCount++;

            // Required for buffering on paused
            if (decoder.RequiresResync && !IsPlaying && seeks.IsEmpty)
                decoder.Resync(vFrame.Timestamp);
        }
        catch (Exception e)
        {
            Log.Error(e.Message);
        }
        return frameTimestamp > 0;
    }

    public void ShowFrame()
    {
        lock(Renderer.Frames)
        {
            if (vFrame is not null)
                Renderer.RenderRequest(vFrame);
            else
                Renderer.RenderRequest();
        }
    }
}
