using System.Numerics;
using System.Runtime.InteropServices;

using Vortice.Direct3D11;

using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace FlyleafLib.MediaFramework.MediaRenderer;
#nullable enable

/// <summary>
/// One quarter of a fisheye unwrap, in the source frame's own pixels and in radians.
/// </summary>
/// <remarks>
/// The caller owns the geometry - see <c>FisheyeQuadrantGeometry</c> in VlsPlayerLib, which derives
/// these from the frame size and is held against Fisheye.Net by its tests. The renderer only draws what
/// it is given.
/// </remarks>
public readonly record struct FisheyeSegment(
    float CenterX,
    float CenterY,
    float SourceWidth,
    float SourceHeight,
    float AngleStart,
    float AngleSpan,
    float OuterRadius,
    float InnerRadius,
    int TargetWidth,
    int TargetHeight);

/// <summary>
/// One unwrapped quadrant, as BGRA rows.
/// </summary>
/// <remarks>
/// Valid for the duration of the call it is handed to and no longer: the memory is a mapped staging
/// texture, unmapped the moment that call returns. Copy out of it, do not keep the pointer, and do not
/// block - the render loop lock is held throughout.
/// </remarks>
public readonly record struct FisheyeFrame(nint Data, int Stride, int Width, int Height);

public unsafe partial class Renderer
{
    /// <summary>
    /// The unwrap reads an ordinary RGBA texture, so it needs no video format handling of its own.
    /// Opaque on purpose: the sinks draw these through a Bgra32 bitmap, and whatever alpha the video
    /// shader happened to leave in the intermediate is none of a quadrant's business.
    /// </summary>
    const string            FISHEYE_SAMPLE  = "color = float4(Texture1.Sample(Sampler, FisheyeProject(input.Texture)).rgb, 1.0);";

    static readonly List<string>
                            fisheyeDefines  = ["dFisheye"];

    static BufferDescription fisheyeDesc    = new()
    {
        Usage           = ResourceUsage.Default,
        BindFlags       = BindFlags.ConstantBuffer,
        CPUAccessFlags  = CpuAccessFlags.None,
        ByteWidth       = (uint)sizeof(FisheyeBufferType)
    };

    Snapshot?[]             fisheyeTargets  = new Snapshot?[4];
    ID3D11PixelShader?      fisheyePS;
    ID3D11Buffer?           fisheyeBuffer;
    /// <summary>One entry, but PSSetShaderResources binds a range - kept to avoid an array per frame.</summary>
    readonly ID3D11ShaderResourceView[]
                            fisheyeSourceSrvs = new ID3D11ShaderResourceView[1];
    ID3D11Texture2D?        fisheyeSourceTxt;
    FisheyeBufferType       fisheyeData;

    /// <summary>
    /// Unwraps the frame currently on screen, one quadrant per segment, and hands each of them to
    /// <paramref name="consume"/> as mapped pixels. A null segment is skipped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two passes. The first puts the frame into an RGBA texture, which is what
    /// <see cref="TakeSnapshot"/> already does and what keeps this working whichever video processor is
    /// active - the D3D11 one hands out a VideoProcessorInputView a pixel shader cannot read. The second
    /// samples that texture through the unwrap, once per segment.
    /// </para>
    /// <para>
    /// All of it under the render loop lock: the immediate context is not shared between threads, and
    /// this is called from a worker. Every draw is issued before the first map, so the stall is one wait
    /// for the GPU rather than one per segment.
    /// </para>
    /// </remarks>
    public bool RenderFisheyeSegments(FisheyeSegment?[] segments, Action<int, FisheyeFrame> consume)
    {
        if (segments == null || consume == null)
            return false;

        lock (lockRenderLoops)
        {
            try
            {
                if (VisibleWidth == 0 || VisibleHeight == 0)
                    return false;

                var source = GetSnapshot(VisibleWidth, VisibleHeight);

                if (!RenderCurrentFrameInto(source) || !EnsureFisheyeResources(source)
                    || fisheyeBuffer is not ID3D11Buffer buffer)
                    return false;

                // The rotation, flip and crop of the stream are already baked into the intermediate by
                // the pass above, which went through the main vertex shader. Going through it a second
                // time would apply them twice, so the unwrap uses the pass-through one - the same thing
                // the subtitle pass does.
                context.VSSetShader         (vsSimple);
                context.PSSetShader         (fisheyePS);
                context.PSSetConstantBuffer (2, buffer);
                context.PSSetShaderResources(0, fisheyeSourceSrvs);

                var drawn = false;

                for (int i = 0; i < segments.Length; i++)
                {
                    if (segments[i] is not FisheyeSegment segment || segment.TargetWidth <= 0 || segment.TargetHeight <= 0)
                        continue;

                    var target = GetFisheyeTarget(i, (uint)segment.TargetWidth, (uint)segment.TargetHeight);

                    fisheyeData.Arc     = new(segment.AngleStart, segment.AngleSpan, segment.OuterRadius, segment.InnerRadius);
                    fisheyeData.Source  = new(segment.CenterX, segment.CenterY,
                                              segment.SourceWidth  > 0 ? 1f / segment.SourceWidth  : 0,
                                              segment.SourceHeight > 0 ? 1f / segment.SourceHeight : 0);
                    context.UpdateSubresource(fisheyeData, buffer);

                    context.OMSetRenderTargets(target.rtv);
                    context.RSSetViewport(target.view);
                    context.Draw(6, 0);
                    context.CopyResource(target.txtStage, target.txt);

                    drawn = true;
                }

                // Every draw and copy is issued above before the first map below, so the wait for the
                // GPU is one, not one per quadrant.
                if (drawn)
                    for (int i = 0; i < segments.Length; i++)
                    {
                        // The same test the draw loop made: a segment it skipped has no fresh pixels,
                        // and its target may still be holding the one before.
                        if (segments[i] is not FisheyeSegment segment
                            || segment.TargetWidth <= 0
                            || segment.TargetHeight <= 0
                            || fisheyeTargets[i] is not Snapshot target)
                            continue;

                        var mapped = context.Map(target.txtStage, 0);

                        try
                        {
                            consume(i, new FisheyeFrame(mapped.DataPointer, (int)mapped.RowPitch, (int)target.Width, (int)target.Height));
                        }
                        finally
                        {
                            context.Unmap(target.txtStage, 0);
                        }
                    }

                return drawn;
            }
            catch (Exception e)
            {
                Log.Error($"[RenderFisheyeSegments] Failed ({e.Message})");

                return false;
            }
            finally
            {
                RestoreMainPipeline();
            }
        }
    }

    /// <summary>
    /// Puts the pipeline back the way the presenting path expects to find it.
    /// </summary>
    /// <remarks>
    /// Render target and shader resources are set on every present, so those look after themselves. The
    /// viewport and the two shaders are not: the pixel shader is only set again when the stream's own id
    /// changes, which is why it has to be put back by hand here.
    /// </remarks>
    void RestoreMainPipeline()
    {
        context.RSSetViewport(Viewport);
        context.VSSetShader(vsMain);

        if (psIdPrev != null && psShader.TryGetValue(psIdPrev, out var current))
            context.PSSetShader(current);
    }

    bool EnsureFisheyeResources(Snapshot source)
    {
        fisheyePS       ??= ShaderCompiler.CompilePS(device, "fisheye", FISHEYE_SAMPLE, fisheyeDefines);
        fisheyeBuffer   ??= device.CreateBuffer(fisheyeDesc);

        // The intermediate is rebuilt whenever the video changes size, and the view over it with it.
        if (!ReferenceEquals(fisheyeSourceTxt, source.txt))
        {
            fisheyeSourceSrvs[0]?.Dispose();
            fisheyeSourceSrvs[0] = device.CreateShaderResourceView(source.txt);
            fisheyeSourceTxt = source.txt;
        }

        return fisheyePS != null && fisheyeSourceSrvs[0] != null;
    }

    Snapshot GetFisheyeTarget(int index, uint width, uint height)
    {
        var target = fisheyeTargets[index];

        if (target != null)
        {
            if (target.Width == width && target.Height == height)
                return target;

            target.Dispose();
        }

        // No video device: the unwrap is a plain pixel shader pass and never needs an output view.
        return fisheyeTargets[index] = new Snapshot(device, null, null, width, height);
    }

    void FisheyeDispose()
    {
        for (int i = 0; i < fisheyeTargets.Length; i++)
        {
            fisheyeTargets[i]?.Dispose();
            fisheyeTargets[i] = null;
        }

        fisheyeSourceSrvs[0]?.Dispose();
        fisheyeSourceSrvs[0] = null!;
        fisheyeSourceTxt = null;

        fisheyeBuffer?.Dispose();
        fisheyeBuffer = null;

        fisheyePS?.Dispose();
        fisheyePS = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FisheyeBufferType
    {
        public Vector4 Arc;     // angle at the left edge, angle across, outer radius, inner radius
        public Vector4 Source;  // centre x, centre y, 1 / source width, 1 / source height
    }
}
#nullable disable
