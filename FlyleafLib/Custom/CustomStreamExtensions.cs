using FlyleafLib.MediaFramework.MediaDemuxer;

namespace FlyleafLib.Custom;

public static class CustomStreamExtensions
{
    public static long StartTimestamp(this Stream stream, VideoTimeUnit timeUnit = VideoTimeUnit.Milliseconds) => stream is not ICustomVideoStream custom ? 0 : timeUnit switch
    {
        VideoTimeUnit.Microseconds => custom.StartTimestamp * 1000,
        VideoTimeUnit.Ticks => custom.StartTimestamp * 10_000,
        _ => custom.StartTimestamp,
    };
    
    public static long LastTimestamp(this Stream stream,  VideoTimeUnit timeUnit = VideoTimeUnit.Milliseconds) => stream is not ICustomVideoStream custom ? 0 : timeUnit switch
    {
        VideoTimeUnit.Microseconds => custom.LastTimestamp * 1000,
        VideoTimeUnit.Ticks => custom.LastTimestamp * 10_000,
        _ => custom.LastTimestamp,
    };

    public static long FirstTimestamp(this Stream stream, VideoTimeUnit timeUnit = VideoTimeUnit.Milliseconds) => stream is not ICustomVideoStream custom ? 0 : timeUnit switch
    {
        VideoTimeUnit.Microseconds => custom.FirstTimestamp * 1000,
        VideoTimeUnit.Ticks => custom.FirstTimestamp * 10_000,
        _ => custom.FirstTimestamp,
    };

    public static long CurTime(this Stream stream, VideoTimeUnit timeUnit = VideoTimeUnit.Milliseconds)
    {
        long offset = 0;
        if (stream is not ICustomVideoStream custom)
            return offset;        
        try
        {
            var startTime = custom?.StartTimestamp ?? 0;
            var lastTime = custom?.LastTimestamp ?? 0;
            offset = lastTime - startTime;
        }
        catch (Exception ex)
        {   
            Console.WriteLine(ex.Message);
        }

        return timeUnit switch
        {
            VideoTimeUnit.Microseconds => offset * 1000,
            VideoTimeUnit.Ticks => offset * 10000,
            _ => offset,
        };
    }
    public static int ExpectedFrameIndex(this Stream stream) => stream is not ICustomVideoStream custom ? 0 : custom.ExpectedFrameIndex;    
    public static long ExpectedTimestamp(this Stream stream, VideoTimeUnit timeUnit = VideoTimeUnit.Milliseconds) => stream is not ICustomVideoStream custom ? 0 : timeUnit switch
    {
        VideoTimeUnit.Microseconds => custom.TargetTimestamp * 1000,
        VideoTimeUnit.Ticks => custom.TargetTimestamp * 10_000,
        _ => custom.TargetTimestamp,
    };   
    public static long CurrentTimestamp(this Stream stream, VideoTimeUnit timeUnit = VideoTimeUnit.Milliseconds) => stream is not ICustomVideoStream custom ? 0 : timeUnit switch
    {
        VideoTimeUnit.Microseconds => custom.CurrentTimestamp * 1000,
        VideoTimeUnit.Ticks => custom.CurrentTimestamp * 10_000,
        _ => custom.CurrentTimestamp,
    };   
    public static long GetDuration(this Stream stream) => stream is not ICustomVideoStream custom? 40 : Convert.ToInt64((custom.FrameDuration > 0 ? custom.FrameDuration : 40));
    public static int GetFramesPerSecond(this Stream stream) => stream is not ICustomVideoStream custom ? 25 : (custom.FramesPerSecond > 0 ? custom.FramesPerSecond : 25) ;
    public static void UpdateDuration(this Stream stream, Demuxer demuxer)
    {
        if (stream is not ICustomVideoStream custom)
            return;
        demuxer.Duration = Convert.ToInt64(custom.FrameDuration);
    }
    public static bool IsCustomStreamLive(this Stream stream) => stream is not ICustomVideoStream custom ? false : custom.IsLive;
    public static bool IsCustomStream(this Stream stream) => stream is ICustomVideoStream;

    public static void AddFrameCount(this Stream stream)
    {
        if (stream is ICustomVideoStream custom)
            custom.FrameCount++;
    }
    public static void ResetFrameCount(this Stream stream) {  if (stream is ICustomVideoStream custom) custom.FrameCount = 0; }
    public static long FrameCount(this Stream stream) => stream is ICustomVideoStream custom ? custom.FrameCount : 0;
    public static bool IsCustomPlayStopMode(this Stream stream) => stream is ICustomVideoStream custom ? custom.IsPlayStopMode : false;
    public static bool IsBufferReady(this Stream stream) => stream is ICustomVideoStream custom ? custom.IsBufferReady : true;
    public static void SetPlayMode(this Stream stream, int PlayMode) { if (stream is ICustomVideoStream custom) custom.Mode = PlayMode; }
}
