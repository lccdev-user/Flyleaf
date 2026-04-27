using FlyleafLib.MediaFramework.MediaDemuxer;

namespace FlyleafLib.Custom;
#nullable enable
public unsafe static class DemuxerExtensions
{
    public static bool IsCustomStream(this Demuxer demuxer) => demuxer.CustomIOContext.stream is ICustomVideoStream stream;
    public static bool IsCustomStreamLive(this Demuxer demuxer) => demuxer.IsCustomStream() ? demuxer.CustomIOContext.stream.IsCustomStreamLive() : false;
    public static long FirstCustomTimestamp(this Demuxer demuxer, VideoTimeUnit unit) => demuxer.IsCustomStream() ? demuxer.CustomIOContext.stream.FirstTimestamp(unit) : 0;
    public static long StartCustomTimestamp(this Demuxer demuxer, VideoTimeUnit unit) => demuxer.IsCustomStream() ? demuxer.CustomIOContext.stream.StartTimestamp (unit) : 0;
    public static long LastCustomTimestamp(this Demuxer demuxer, VideoTimeUnit unit) => demuxer.IsCustomStream() ? demuxer.CustomIOContext.stream.LastTimestamp(unit) : 0;
    public static long CurCustomTime(this Demuxer demuxer, VideoTimeUnit unit) => demuxer.IsCustomStream() ? demuxer.CustomIOContext.stream.CurTime(unit) : 0;
    public static long ExpectedCustomTimestamp(this Demuxer demuxer, VideoTimeUnit unit) => demuxer.IsCustomStream() ? demuxer.CustomIOContext.stream.ExpectedTimestamp (unit) : 0;
    public static int ExpectedCustomFrameIndex(this Demuxer demuxer) => demuxer.IsCustomStream() ? demuxer.CustomIOContext.stream.ExpectedFrameIndex() : 0;
    public static long CustomDuration(this Demuxer demuxer) => demuxer.IsCustomStream() ? demuxer.CustomIOContext.stream.GetDuration() : 40;
    public static int CustomFramePerSecond(this Demuxer demuxer) => demuxer.IsCustomStream() ? demuxer.CustomIOContext.stream.GetFramesPerSecond() : 25;
    public static void UpdateCustomDuration(this Demuxer demuxer)
    {
        if (demuxer.CustomIOContext.stream is ICustomVideoStream custom)
            demuxer.Duration = Convert.ToInt64(custom.FrameDuration);
    }
    public static long CustomFrameCount(this Demuxer demuxer) => demuxer.IsCustomStream() ? demuxer.CustomIOContext.stream.FrameCount() : 0;
    public static void AddCustomFrameCount(this Demuxer demuxer)
    {
        if (demuxer.CustomIOContext.stream is ICustomVideoStream custom)
            custom.FrameCount++;
    }
    public static void ResetCustomFrameCount(this Demuxer demuxer)
    {
        if (demuxer.CustomIOContext.stream is ICustomVideoStream custom)
            custom.FrameCount = 0;
    }
    public static bool IsCustomPlayStopMode(this Demuxer demuxer) => demuxer.IsCustomStream() ? demuxer.CustomIOContext.stream.IsCustomPlayStopMode() : false;
    public static bool IsSearchCompleted(this Demuxer demuxer, long timestamp, LogHandler? Log = null)
    {
        if (demuxer.CustomIOContext.stream is not ICustomVideoStream stream)
            return false;

        long frameTime = timestamp + stream.StartTimestamp;
        Log?.Trace($"IsSearchCompleted: timestamp {timestamp} ms, frame time {frameTime}, expected {stream.TargetTimestamp}");
        return stream.IsPlayStopMode && frameTime >= stream.TargetTimestamp;
    }
    public static bool IsSearchCompleted(this Demuxer demuxer, AVFrame* frame, double timeBase, LogHandler? Log = null)
    {
        if (demuxer.CustomIOContext.stream is not ICustomVideoStream stream || !stream.IsPlayStopMode)
            return false;
        var frameTime = (long)(frame->pts * timeBase) / 1_000;
        frameTime += demuxer.StartCustomTimestamp(VideoTimeUnit.Milliseconds);
        var expectedTime = demuxer.ExpectedCustomTimestamp(VideoTimeUnit.Milliseconds);
        Log?.Trace($"IsSearchCompleted: pts {frame->pts}, timeBase {timeBase}, frameTime {frameTime}, expected {expectedTime}");
        return (frameTime >= expectedTime) || (expectedTime == 0);
    }
    public static bool SkipFrameBySearch(this Demuxer demuxer, long timestamp, LogHandler? Log = null)
    {
        if (demuxer.CustomIOContext.stream is not ICustomVideoStream stream || !stream.IsPlayStopMode)
            return false;

        var distance = timestamp - stream.TargetTimestamp;
        Log?.Trace($"SkipFrameBySearch: timestamp {timestamp}, expected {stream.TargetTimestamp}, distance {distance}, result {distance < - 50}");
        return distance < - 50;        
    }
    public static void SetPacketPts(this Demuxer demuxer, AVPacket* packet, out double timeBase, LogHandler? Log = null)
    {
        timeBase = 0.0F;
        if (demuxer.CustomIOContext.stream is not ICustomVideoStream stream)
            return;

        var videoStream = demuxer.AVStreamToStream[packet->stream_index];
        timeBase = videoStream.Timebase;
        long frameDuration = 1_000_000 / demuxer.CustomFramePerSecond();
        long frameTime = demuxer.CurCustomTime(VideoTimeUnit.Ticks);
        if (timeBase > 0)
        {
            Log?.Trace($"SetPacketPts: frame ts {frameTime}, pts {(long)(frameTime / timeBase)},timeBase {timeBase}, timestamp {(frameTime / 10_000) + stream.StartTimestamp}");
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
    public static bool IsVideoBufferReady(this Demuxer demuxer) => demuxer.IsCustomStream() ? demuxer.CustomIOContext.stream.IsBufferReady() : false;

    public static void SetPlayMode(this Demuxer demuxer, int playMode)
    {
        if (demuxer.CustomIOContext.stream is ICustomVideoStream custom)
            custom.Mode = playMode;
    }
}
#nullable disable
