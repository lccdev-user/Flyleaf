using FlyleafLib.MediaFramework.MediaDemuxer;

namespace FlyleafLib.Custom;

public unsafe static class DemuxerExtensions
{
    public static bool IsCustomStream(this Demuxer demuxer) => demuxer.CustomIOContext.stream is ICustomVideoStream stream;
    public static bool IsCustomStreamLive(this Demuxer demuxer) => demuxer.CustomIOContext.stream.IsCustomStreamLive();
    public static long FirstCustomTimestamp(this Demuxer demuxer, VideoTimeUnit unit) => demuxer.CustomIOContext.stream.FirstTimestamp(unit);
    public static long StartCustomTimestamp(this Demuxer demuxer, VideoTimeUnit unit) => demuxer.CustomIOContext.stream.StartTimestamp (unit);
    public static long LastCustomTimestamp(this Demuxer demuxer, VideoTimeUnit unit) => demuxer.CustomIOContext.stream.LastTimestamp(unit);
    public static long CurCustomTime(this Demuxer demuxer, VideoTimeUnit unit) => demuxer.CustomIOContext.stream.CurTime(unit);
    public static long ExpectedCustomTimestamp(this Demuxer demuxer, VideoTimeUnit unit) => demuxer.CustomIOContext.stream.ExpectedTimestamp (unit);
    public static int ExpectedCustomFrameIndex(this Demuxer demuxer) => demuxer.CustomIOContext.stream.ExpectedFrameIndex();
    public static long CustomDuration(this Demuxer demuxer) => demuxer.CustomIOContext.stream.GetDuration();
    public static int CustomFramePerSecond(this Demuxer demuxer) => demuxer.CustomIOContext.stream.GetFramesPerSecond();
    public static void UpdateCustomDuration(this Demuxer demuxer) => demuxer.CustomIOContext.stream.UpdateDuration(demuxer);
    public static long CustomFrameCount(this Demuxer demuxer) => demuxer.CustomIOContext.stream.FrameCount();
    public static void AddCustomFrameCount(this Demuxer demuxer) => demuxer.CustomIOContext.stream.AddFrameCount();
    public static void ResetCustomFrameCount(this Demuxer demuxer) => demuxer.CustomIOContext.stream.ResetFrameCount();
    public static bool IsCustomPlayStopMode(this Demuxer demuxer) => demuxer.CustomIOContext.stream.IsCustomPlayStopMode();
    public static bool IsSearchCompleted(this Demuxer demuxer, long timestamp) => demuxer.CustomIOContext.stream is ICustomVideoStream stream && stream.IsPlayStopMode && timestamp >= stream.TargetTimestamp;
    public static bool IsSearchCompleted(this Demuxer demuxer, AVFrame* frame, double timeBase)
    {
        if (demuxer.CustomIOContext.stream is not ICustomVideoStream stream || !stream.IsPlayStopMode)
            return false;
        var frameTime = (long)(frame->pts * timeBase) / 1000;
        frameTime += demuxer.StartCustomTimestamp(VideoTimeUnit.Milliseconds);
        var expectedTime = demuxer.ExpectedCustomTimestamp(VideoTimeUnit.Milliseconds);

        return (frameTime >= expectedTime) || (expectedTime == 0);
    }
    public static bool SkipFrameBySearch(this Demuxer demuxer, long timestamp)
    {
        if (demuxer.CustomIOContext.stream is not ICustomVideoStream stream || !stream.IsPlayStopMode)
            return false;
        var offset = timestamp - stream.TargetTimestamp;
        return offset < - 50;        
    }
    public static void SetPacketPts(this Demuxer demuxer, AVPacket* packet, out double timeBase, LogHandler? Log = null)
    {
        timeBase = 0.0F;
        if (demuxer.CustomIOContext.stream is not ICustomVideoStream stream)
            return;

        var videoStream = demuxer.AVStreamToStream[packet->stream_index];
        timeBase = videoStream.Timebase;
        long frameDuration = 1_000_000 / demuxer.CustomFramePerSecond();
        long frameTime = demuxer.CurCustomTime(VideoTimeUnit.Microseconds);
        Log?.Debug($"SetPacketPts: pts {(long)(frameTime / timeBase)}, timestamp {(frameTime / 1000) + stream.StartTimestamp}");
        if (timeBase > 0)
        {
            packet->pts = (long)(frameTime / timeBase);
            packet->duration = frameDuration;
            packet->dts = AV_NOPTS_VALUE;
        }
    }
    public static long ToCustomTimestamp(this Demuxer demuxer, long timestamp)
    {
        if (demuxer.CustomIOContext.stream is not ICustomVideoStream stream)
            return 0;
        return timestamp + stream.StartTimestamp;
    }
    public static bool IsVideoBufferReady(this Demuxer demuxer) => demuxer.CustomIOContext.stream.IsBufferReady();

    public static void SetPlayMode(this Demuxer demuxer, int playMode) => demuxer.CustomIOContext.stream.SetPlayMode(playMode);
}
