using FlyleafLib.MediaFramework.MediaDemuxer;
using FlyleafLib.MediaFramework.MediaRemuxer;

namespace FlyleafLib.Custom;
#nullable enable
public unsafe static class DemuxerExtensions
{
    public static bool IsCustomStream(this Demuxer demuxer) => demuxer.CustomIOContext.stream is ICustomVideoStream stream;
    public static bool IsCustomStreamLive(this Demuxer demuxer) => demuxer.IsCustomStream() ? demuxer.CustomIOContext.stream.IsCustomStreamLive() : false;
    public static long FirstCustomTimestampInGoP(this Demuxer demuxer, VideoTimeUnit unit) => demuxer.IsCustomStream() ? demuxer.CustomIOContext.stream.FirstTimestampInGoP(unit) : 0;
    public static long StartCustomTimestamp(this Demuxer demuxer, VideoTimeUnit unit) => demuxer.IsCustomStream() ? demuxer.CustomIOContext.stream.StartTimestamp (unit) : 0;
    public static long LastCustomTimestamp(this Demuxer demuxer, VideoTimeUnit unit) => demuxer.IsCustomStream() ? demuxer.CustomIOContext.stream.LastTimestamp(unit) : 0;
    public static long CurCustomTime(this Demuxer demuxer, VideoTimeUnit unit) => demuxer.IsCustomStream() ? demuxer.CustomIOContext.stream.CurTime(unit) : 0;
    public static long PictureGroupTime(this Demuxer demuxer, VideoTimeUnit unit) => demuxer.IsCustomStream() ? demuxer.CustomIOContext.stream.PictureGroupTime(unit) : 0;
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
    /// <summary>
    /// How far before the moment asked for a picture may sit and still count as reaching it.
    /// </summary>
    /// <remarks>
    /// The picture covering a moment is the one at or after it, so a search would naturally accept
    /// nothing earlier. That breaks at the newest moment a camera has recorded: the archive keeps
    /// growing, so the extent a client was told about can be a picture ahead of the newest one any
    /// group actually holds, and demanding a picture at or after it matches nothing at all. Since an
    /// empty read ends the demuxer, "nothing at all" is permanent - the screen stays blank until
    /// something else moves the player.
    /// <para>
    /// A tolerance of well under one frame interval costs nothing anywhere else and makes the two
    /// checks agree. <see cref="SkipFrameBySearch"/> has always allowed this much;
    /// A picture could pass one and fail the other.
    /// </para>
    /// </remarks>
    public const long SearchToleranceMs = 50;

    public static bool IsSearchCompleted(this Demuxer demuxer, long timestamp, LogHandler? Log = null)
    {
        if (demuxer.CustomIOContext.stream is not ICustomVideoStream stream)
            return false;

        long frameTime = timestamp + stream.StartTimestamp;
        Log?.Trace($"IsSearchCompleted: timestamp {timestamp} ms, frame time {frameTime}, expected {stream.TargetTimestamp}");
        return stream.IsPlayStopMode && frameTime >= stream.TargetTimestamp - SearchToleranceMs;
    }

    /// <summary>
    /// Settles a custom stream's reported current time on the picture the search actually reached,
    /// once <see cref="IsSearchCompleted(Demuxer, long, LogHandler)"/> says it's done. No-op for
    /// non-custom streams.
    /// </summary>
    public static void CompleteCustomSearch(this Demuxer demuxer)
    {
        if (demuxer.CustomIOContext.stream is ICustomVideoStream stream)
            stream.SearchCompleted = true;
    }

    /// <summary>
    /// Whether a custom stream has a search in flight that hasn't settled yet. While true, nothing
    /// external to the search (a UI refresh tick, an unrelated property read) should be told "here is
    /// the current position", because isn't one yet, only the demuxer's read-ahead point.
    /// </summary>
    public static bool HasUnsettledCustomSearch(this Demuxer demuxer) =>
        demuxer.CustomIOContext.stream is ICustomVideoStream stream && !stream.SearchCompleted;

    public static bool IsSearchCompleted(this Demuxer demuxer, AVFrame* frame, double timeBase, LogHandler? Log = null)
    {
        if (demuxer.CustomIOContext.stream is not ICustomVideoStream stream || !stream.IsPlayStopMode)
            return false;
        var frameTime = (long)(frame->pts * timeBase) / Ticks.InOneMillisecond;
        frameTime += demuxer.StartCustomTimestamp(VideoTimeUnit.Milliseconds);
        var expectedTime = demuxer.ExpectedCustomTimestamp(VideoTimeUnit.Milliseconds);
        Log?.Trace($"IsSearchCompleted: pts {frame->pts}, timeBase {timeBase}, frameTime {frameTime}, expected {expectedTime}");
        return (frameTime >= expectedTime - SearchToleranceMs) || (expectedTime == 0);
    }
    /// <summary>
    /// Whether a decoded frame sits before the moment playback was asked to start from, and so should
    /// be thrown away rather than queued for display.
    /// </summary>
    /// <remarks>
    /// Dropped here, after decoding, because the frames that follow are predicted from these. Dropped
    /// here rather than at presentation time so that the first frame the player sees is the one asked
    /// for: the screamer paces everything from that frame's timestamp, and handing it a frame it will
    /// only refuse to show would have it sit on a blank screen for a whole group.
    /// </remarks>
    public static bool SkipFrameBeforeDisplayStart(this Demuxer demuxer, AVFrame* frame, double timeBase, LogHandler? Log = null)
    {
        if (demuxer.CustomIOContext.stream is not ICustomVideoStream stream)
            return false;

        var displayFrom = stream.DisplayFromTimestamp;
        if (displayFrom <= 0)
            return false;

        var frameTime = (long)(frame->pts * timeBase) / Ticks.InOneMillisecond + stream.StartTimestamp;
        var skip = frameTime < displayFrom;

        if (skip)
            Log?.Trace($"SkipFrameBeforeDisplayStart: frameTime {frameTime}, displayFrom {displayFrom}");

        return skip;
    }
    public static bool SkipFrameBySearch(this Demuxer demuxer, long timestamp, LogHandler? Log = null)
    {
        if (demuxer.CustomIOContext.stream is not ICustomVideoStream stream || !stream.IsPlayStopMode)
            return false;

        var distance = timestamp - stream.TargetTimestamp;
        Log?.Trace($"SkipFrameBySearch: timestamp {timestamp}, expected {stream.TargetTimestamp}, distance {distance}, result {distance < -SearchToleranceMs}");
        return distance < -SearchToleranceMs;
    }
    public static void SetPacketPts(this Demuxer demuxer, AVPacket* packet, out double timeBase,  ref int gopFrameIndex, LogHandler? Log = null)
    {
        timeBase = 0.0F;

        if (demuxer.CustomIOContext.stream is not ICustomVideoStream stream)
            return;

        long frameTime = 0;
        if ((packet->flags & PktFlags.Key) != 0)
           demuxer.PictureGroupTime(VideoTimeUnit.Ticks); // synchronizes the current time with the time of the new GOP

        frameTime = demuxer.CurCustomTime(VideoTimeUnit.Ticks);

        var videoStream = demuxer.AVStreamToStream[packet->stream_index];
        timeBase = videoStream.Timebase;
        long frameDuration = 1_000_000 / demuxer.CustomFramePerSecond();

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
    public static void SetPictureGroupFrameIndex(this Demuxer demuxer, int frameIndex)
    {
        if (demuxer.CustomIOContext.stream is ICustomVideoStream custom)
            custom.PictureGroupFrameIndex = frameIndex;
    }
    public static void UpdateCustomRenderedTimestamp(this Demuxer demuxer, long frameTicks)
    {
        if (demuxer.CustomIOContext.stream is not ICustomVideoStream stream)
            return;
        stream.CurrentTimestamp = demuxer.ToCustomTimestamp(frameTicks / Ticks.InOneMillisecond);
    }
}
#nullable disable
