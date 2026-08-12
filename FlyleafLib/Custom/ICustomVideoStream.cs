namespace FlyleafLib.Custom;
#nullable enable
public interface ICustomVideoStream
{
    event Action<long>? StartTimeChanged;
    event Action<long>? CurrentTimeChanged;
    /// <summary>
    /// The real start date of the video archive. The default is minus 30 days before the current date.
    /// </summary>
    DateTime StartRealTime { get; }
    /// <summary>
    /// Target timestamp in milliseconds for video frame timestamp search operations
    /// </summary>
    long TargetTimestamp { get; }
    /// <summary>
    /// The initial timestamp of the video archive (in milliseconds). The default is minus 30 days before the current date.
    /// </summary>
    long StartTimestamp { get; }
    /// <summary>
    /// The first timestamp of the video response (in milliseconds).
    /// </summary>
    long FirstTimestampInGoP { get; }
    /// <summary>
    ///
    /// </summary>
    long CurrentTimestamp { get; set; }
    long LastTimestamp { get; }
    /// <summary>
    /// The first timestamp (in milliseconds) worth presenting, or 0 to present everything decoded.
    /// </summary>
    /// <remarks>
    /// A stream addressed by time is served whole groups, and a group begins at its key picture - so
    /// playing on from a moment inside one delivers pictures before it that were already watched.
    /// They have to be decoded, because the pictures after them are predicted from them, but they
    /// must not be queued for display. Defaults to showing everything, which is what a stream that
    /// does not work in groups wants.
    /// </remarks>
    long DisplayFromTimestamp => 0;
    Double FrameDuration { get; }
    int FramesPerSecond { get; }
    long PictureGroupTimeStamp { get; }
    double PictureGroupFrameDuration { get; }
    int PictureGroupFrameIndex { get; set; }
    bool IsLive { get; }
    int ExpectedFrameIndex { get; }
    long FrameCount { get; set; }
    bool IsPlayStopMode { get; }
    bool SearchCompleted { get; set; }
    bool IsBufferReady { get; }
    int Mode { get; set; }
    double SpoolSpeed { get; set; }
    void Play(long timestamp, int playMode, double spoolSpeed);
    void ErrorByStreamingDetected(StreamingErrorCode errorCode);
}
#nullable disable
