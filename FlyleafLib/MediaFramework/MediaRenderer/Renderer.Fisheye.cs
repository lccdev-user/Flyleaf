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
    /// The unwrap itself is applied by the shader's own header, under the same define; this only reads
    /// the RGBA intermediate, so it needs no video format handling of its own. Opaque on purpose: the
    /// sinks draw these through a Bgra32 bitmap, and whatever alpha the video shader happened to leave
    /// in the intermediate is none of a quadrant's business.
    /// </summary>
    const string            FISHEYE_SAMPLE  = "color = float4(Texture1.Sample(Sampler, input.Texture).rgb, 1.0);";

    /// <summary>The offscreen pass draws a bare quad, so there is no crop to undo and none to apply.</summary>
    static readonly Vector4 fisheyeNoCrop   = new(0, 0, 1, 1);

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
    FisheyeSegment?         fisheyeView;
    bool                    fisheyeUnwrap;

    /// <summary>
    /// Whether this stream is to be drawn unwrapped. Set before the stream is opened, and deliberately
    /// kept apart from <see cref="FisheyeView"/>.
    /// </summary>
    /// <remarks>
    /// The unwrap is a pixel shader, so it needs the Flyleaf video processor - and that choice is made
    /// once, when the stream is configured. Deciding it later, from the first frame, is too late: the
    /// frames already in hand were filled for the D3D11 processor, which gives them a
    /// VideoProcessorInputView and no shader resource view, so FLRender has nothing to sample and
    /// returns. A paused stream decodes no more of them, and the panel stays black.
    /// </remarks>
    public bool FisheyeUnwrapEnabled
    {
        get => fisheyeUnwrap;
        set
        {
            if (fisheyeUnwrap == value)
                return;

            fisheyeUnwrap = value;

            if (!value)
                fisheyeView = null;

            VPRequest(VPRequestType.ReConfigVP);
        }
    }

    /// <summary>
    /// Unwraps the video itself as it is drawn, rather than producing a second picture beside it. Null
    /// switches it off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the single view mode: the panel shows one quadrant of a fisheye camera and that quadrant
    /// is the video, so the unwrap belongs in the shader the frame is drawn with. Everything that reads
    /// the picture back - a snapshot, an export - goes through the same shader and therefore sees the
    /// same thing, which is what the decode time transform this replaces had to arrange by rewriting
    /// the decoded frame.
    /// </para>
    /// <para>
    /// Switching it on or off changes which shader variant the stream needs, so it asks for a full
    /// reconfigure; changing the geometry only refills the constant buffer. The same is true of
    /// <see cref="Pano360Config.Enabled"/>, and for the same reason.
    /// </para>
    /// </remarks>
    public FisheyeSegment? FisheyeView
    {
        get => fisheyeView;
        set
        {
            // Set once per presented frame, so the common case is no change at all and must cost
            // nothing: a reconfigure per frame would rebuild the shader variant for a living.
            if (fisheyeView.Equals(value))
                return;

            var was = fisheyeView.HasValue;
            fisheyeView = value;

            // Arriving for the first time brings the define with it, which is a different shader
            // variant; after that only the constants change.
            if (was != value.HasValue)
                VPRequest(VPRequestType.ReConfigVP);
            else
                VPRequest(VPRequestType.Fisheye);
        }
    }

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

                    SetFisheyeBuffer(segment, fisheyeNoCrop, buffer);

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

    /// <summary>
    /// Refills the unwrap's constants for the main pass, from the crop the vertex shader is using.
    /// </summary>
    void FLSetFisheye()
    {
        vpRequests &= ~VPRequestType.Fisheye;

        if (fisheyeView is not FisheyeSegment segment)
            return;

        fisheyeBuffer ??= device.CreateBuffer(fisheyeDesc);

        context.PSSetConstantBuffer(2, fisheyeBuffer);
        SetFisheyeBuffer(segment, new(vsData.Crop.X, vsData.Crop.Y, vsData.Crop.Z, vsData.Crop.W), fisheyeBuffer);
    }

    void SetFisheyeBuffer(FisheyeSegment segment, Vector4 crop, ID3D11Buffer buffer)
    {
        fisheyeData.Arc     = new(segment.AngleStart, segment.AngleSpan, segment.OuterRadius, segment.InnerRadius);
        fisheyeData.Source  = new(segment.CenterX, segment.CenterY,
                                  segment.SourceWidth  > 0 ? 1f / segment.SourceWidth  : 0,
                                  segment.SourceHeight > 0 ? 1f / segment.SourceHeight : 0);
        fisheyeData.Crop    = crop;

        context.UpdateSubresource(fisheyeData, buffer);
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
        // With the device goes the stream, and with the stream the segment it was unwrapping. Left
        // standing, the next stream's first update is a change of parameters rather than a switch from
        // off to on - constants without a reconfigure - and a paused player, which presents one frame
        // and stops, has nothing left to redraw with them. The panel then keeps showing the previous
        // camera's quadrant until something else forces a render.
        fisheyeView = null;

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
        public Vector4 Source;  // centre x, centre y, 1 / picture width, 1 / picture height
        public Vector4 Crop;    // the visible picture inside the texture: left, top, right, bottom
    }
}
#nullable disable
