using FlyleafLib.MediaPlayer;

namespace FlyleafLib.Custom;

public static class PlayerExtensions
{
    public static void SetStatus(this Player player, Status status) => player.status = status;
    public static void SetCurTime(this Player player, long value)
    {
        player.curTime = value;
        // resolves to Player's internal no-arg method
        player.SetCurTime();
    }
}
