using FlyleafLib.MediaFramework.MediaContext;
using FlyleafLib.MediaPlayer;

namespace FlyleafLib.Custom;

public static class DecoderContextExtensions
{
    public static Player GetPlayer(this DecoderContext decoderContext) => decoderContext?.Config.Player.player ?? null;
}
