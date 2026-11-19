namespace Gamegrl.Rhi.Vulkan;

internal sealed unsafe class VulkanCommandList : CommandList
{
    public nint Cmd;
    public VulkanDevice Device = null!;
    private ulong _layout;
    private int _targetWidth, _targetHeight;
    private VulkanTexture? _passColor, _passDepth;

    public override void BeginRenderPass(in RenderPassDesc desc)
    {
        var color = (VulkanTexture)desc.Color.Texture;
        VulkanTexture? depth = desc.Depth is { } d ? (VulkanTexture)d.Texture : null;
        Barriers.Transition(Cmd, color, VkConst.ImageLayoutColorAttachment);
        if (depth is not null) Barriers.Transition(Cmd, depth, VkConst.ImageLayoutDepthAttachment);

        var colorInfo = new VkRenderingAttachmentInfo
        {
            SType = VkStructureType.RenderingAttachmentInfo,
            ImageView = color.View,
            ImageLayout = VkConst.ImageLayoutColorAttachment,
            LoadOp = LoadOp(desc.Color.Load),
            StoreOp = StoreOp(desc.Color.Store),
        };
        colorInfo.ClearValue.Color[0] = desc.Color.ClearColor.X;
        colorInfo.ClearValue.Color[1] = desc.Color.ClearColor.Y;
        colorInfo.ClearValue.Color[2] = desc.Color.ClearColor.Z;
        colorInfo.ClearValue.Color[3] = desc.Color.ClearColor.W;

        var depthInfo = default(VkRenderingAttachmentInfo);
        if (depth is not null)
        {
            depthInfo.SType = VkStructureType.RenderingAttachmentInfo;
            depthInfo.ImageView = depth.View;
            depthInfo.ImageLayout = VkConst.ImageLayoutDepthAttachment;
            depthInfo.LoadOp = LoadOp(desc.Depth!.Value.Load);
            depthInfo.StoreOp = StoreOp(desc.Depth!.Value.Store);
            depthInfo.ClearValue.Depth = desc.Depth!.Value.ClearDepth;
        }

        var info = new VkRenderingInfo
        {
            SType = VkStructureType.RenderingInfo,
            RenderArea = new VkRect2D { Extent = new VkExtent2D { Width = (uint)color.Width, Height = (uint)color.Height } },
            LayerCount = 1,
            ColorAttachmentCount = 1,
            ColorAttachments = &colorInfo,
            DepthAttachment = depth is null ? null : &depthInfo,
        };
        Vk.CmdBeginRendering(Cmd, &info);

        _targetWidth = color.Width;
        _targetHeight = color.Height;
        _passColor = color;
        _passDepth = depth;
        SetViewport(0, 0, color.Width, color.Height);
        SetScissor(0, 0, color.Width, color.Height);
    }

    /// <summary>
    /// Ends the pass. Attachments that can also be sampled move to the shader-read layout, so a texture rendered
    /// here can be drawn by a later pass (the editor's Scene and Game views) without explicit barriers.
    /// </summary>
    public override void EndRenderPass()
    {
        Vk.CmdEndRendering(Cmd);
        if (_passColor is { } c && (c.Usage & TextureUsage.Sampled) != 0) Barriers.Transition(Cmd, c, VkConst.ImageLayoutShaderReadOnly);
        if (_passDepth is { } d && (d.Usage & TextureUsage.Sampled) != 0) Barriers.Transition(Cmd, d, VkConst.ImageLayoutShaderReadOnly);
        _passColor = _passDepth = null;
    }

    public override void SetPipeline(GraphicsPipeline pipeline)
    {
        var p = (VulkanPipeline)pipeline;
        _layout = p.Layout;
        Vk.CmdBindPipeline(Cmd, VkConst.PipelineBindPointGraphics, p.Pipeline);
    }

    public override void SetBindGroup(int index, BindGroup group, ReadOnlySpan<uint> dynamicOffsets = default)
    {
        ulong set = ((VulkanBindGroup)group).Set;
        fixed (uint* offsets = dynamicOffsets)
            Vk.CmdBindDescriptorSets(Cmd, VkConst.PipelineBindPointGraphics, _layout, (uint)index, 1, &set, (uint)dynamicOffsets.Length, offsets);
    }

    public override void SetVertexBuffer(int slot, GpuBuffer buffer, long offset = 0)
    {
        ulong b = ((VulkanBuffer)buffer).Handle;
        ulong o = (ulong)offset;
        Vk.CmdBindVertexBuffers(Cmd, (uint)slot, 1, &b, &o);
    }

    public override void SetIndexBuffer(GpuBuffer buffer, IndexFormat format, long offset = 0) =>
        Vk.CmdBindIndexBuffer(Cmd, ((VulkanBuffer)buffer).Handle, (ulong)offset,
            format == IndexFormat.UInt16 ? VkConst.IndexTypeUint16 : VkConst.IndexTypeUint32);

    /// <summary>
    /// Vulkan's clip space is y-down; WebGPU, D3D and Metal are y-up. A negative-height viewport (core since 1.1)
    /// flips Vulkan to y-up, so the same projection matrices work on every backend.
    /// </summary>
    public override void SetViewport(float x, float y, float width, float height)
    {
        var v = new VkViewport { X = x, Y = y + height, Width = width, Height = -height, MinDepth = 0, MaxDepth = 1 };
        Vk.CmdSetViewport(Cmd, 0, 1, &v);
    }

    public override void SetScissor(int x, int y, int width, int height)
    {
        var r = new VkRect2D
        {
            Offset = new VkOffset2D { X = Math.Max(0, x), Y = Math.Max(0, y) },
            Extent = new VkExtent2D { Width = (uint)Math.Max(0, width), Height = (uint)Math.Max(0, height) },
        };
        Vk.CmdSetScissor(Cmd, 0, 1, &r);
    }

    public override void CopyToReadback(GpuTexture texture, GpuReadback readback)
    {
        var source = (VulkanTexture)texture; var target = (VulkanReadback)readback;
        if (target.Pending) throw new InvalidOperationException("Readback is still pending.");
        if (source.Width != target.Width || source.Height != target.Height || source.Format is not (TextureFormat.Rgba8Unorm or TextureFormat.Rgba8Srgb or TextureFormat.Bgra8Unorm or TextureFormat.Bgra8Srgb))
            throw new ArgumentException("Readback requires a matching four-byte color texture.");
        if ((source.Usage & TextureUsage.CopySrc) == 0) throw new ArgumentException("Texture does not have CopySrc usage.");
        uint old = source.Layout;
        Barriers.Transition(Cmd, source, VkConst.ImageLayoutTransferSrc);
        var copy = new VkBufferImageCopy
        {
            ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkConst.AspectColor, LayerCount = 1 },
            ImageExtent = new VkExtent3D { Width = (uint)source.Width, Height = (uint)source.Height, Depth = 1 },
        };
        Vk.CmdCopyImageToBuffer(Cmd, source.Image, VkConst.ImageLayoutTransferSrc, target.Buffer.Handle, 1, &copy);
        Barriers.Transition(Cmd, source, old); target.Frame = Device.RecordingFrame;
    }

    public override void Draw(int vertexCount, int instanceCount = 1, int firstVertex = 0, int firstInstance = 0) =>
        Vk.CmdDraw(Cmd, (uint)vertexCount, (uint)instanceCount, (uint)firstVertex, (uint)firstInstance);

    public override void DrawIndexed(int indexCount, int instanceCount = 1, int firstIndex = 0, int vertexOffset = 0, int firstInstance = 0) =>
        Vk.CmdDrawIndexed(Cmd, (uint)indexCount, (uint)instanceCount, (uint)firstIndex, vertexOffset, (uint)firstInstance);

    private static uint LoadOp(LoadOp op) => op switch
    {
        Rhi.LoadOp.Load => VkConst.AttachmentLoadLoad,
        Rhi.LoadOp.Clear => VkConst.AttachmentLoadClear,
        _ => VkConst.AttachmentLoadDontCare,
    };

    private static uint StoreOp(StoreOp op) => op == Rhi.StoreOp.Store ? VkConst.AttachmentStoreStore : VkConst.AttachmentStoreDontCare;

    // ------------------------------------------------------------------ GPU timing
    // Queries 0 and 1 bracket the frame; each zone takes the next two. Results are read when the slot comes round again.

    public const int MaxQueries = 256;
    private const ulong AllCommands = 0x00010000; // VK_PIPELINE_STAGE_2_ALL_COMMANDS_BIT
    private static readonly ProfileMarker FrameMarker = ProfileMarker.Get("GPU.Frame", ProfileCategory.Gpu);
    internal ulong QueryPool;
    internal bool Timing;
    internal int QueryCount;
    internal ulong TimedFrame;
    internal readonly (int Marker, ushort Depth, int Query)[] Zones = new (int, ushort, int)[(MaxQueries - 2) / 2];
    internal int ZoneCount;
    private readonly int[] _open = new int[64];
    private int _openCount;

    internal void StartTiming(ulong frame)
    {
        Vk.CmdResetQueryPool(Cmd, QueryPool, 0, MaxQueries);
        Vk.CmdWriteTimestamp2(Cmd, AllCommands, QueryPool, 0);
        Timing = true; QueryCount = 2; ZoneCount = 0; _openCount = 0; TimedFrame = frame;
        Zones[ZoneCount++] = (FrameMarker.Id, 0, 0);
    }

    internal void FinishTiming()
    {
        if (!Timing) return;
        while (_openCount > 0) EndZone();
        Vk.CmdWriteTimestamp2(Cmd, AllCommands, QueryPool, 1);
        Timing = false;
    }

    protected override void BeginZone(ProfileMarker marker)
    {
        if (_openCount == _open.Length) return;
        if (!Timing || ZoneCount == Zones.Length || QueryCount + 2 > MaxQueries) { _open[_openCount++] = -1; return; }
        int q = QueryCount; QueryCount += 2;
        Vk.CmdWriteTimestamp2(Cmd, AllCommands, QueryPool, (uint)q);
        Zones[ZoneCount] = (marker.Id, (ushort)(_openCount + 1), q);
        _open[_openCount++] = ZoneCount++;
    }

    protected override void EndZone()
    {
        if (_openCount == 0) return;
        int zone = _open[--_openCount];
        if (zone >= 0 && Timing) Vk.CmdWriteTimestamp2(Cmd, AllCommands, QueryPool, (uint)Zones[zone].Query + 1);
    }

    /// <summary>Read a finished frame's timestamps and hand them to the profiler.</summary>
    internal void Collect(nint device, float period, ulong validMask)
    {
        if (QueryCount == 0) return;
        int count = QueryCount;
        QueryCount = 0;
        var data = stackalloc ulong[count];
        if (Vk.GetQueryPoolResults(device, QueryPool, 0, (uint)count, (nuint)(count * 8), data, 8, 1 /* 64-bit */) != VkResult.Success) return;
        ulong t0 = data[0] & validMask;
        long Ns(int q) => (long)(((data[q] & validMask) - t0) * (double)period);
        var zones = stackalloc GpuZone[ZoneCount];
        zones[0] = new GpuZone { Marker = Zones[0].Marker, StartNs = 0, EndNs = Ns(1) };
        for (int i = 1; i < ZoneCount; i++)
            zones[i] = new GpuZone { Marker = Zones[i].Marker, Depth = Zones[i].Depth, StartNs = Ns(Zones[i].Query), EndNs = Ns(Zones[i].Query + 1) };
        Profiler.SubmitGpu(TimedFrame, new ReadOnlySpan<GpuZone>(zones, ZoneCount), zones[0].EndNs);
    }

}

/// <summary>Image layout transitions with stage and access masks derived from the layouts.</summary>
internal static unsafe class Barriers
{
    public static void Transition(nint cmd, VulkanTexture texture, uint newLayout)
    {
        uint old = texture.Layout;
        if (old == newLayout) return;
        var (srcStage, srcAccess) = Usage(old, source: true);
        var (dstStage, dstAccess) = Usage(newLayout, source: false);
        var barrier = new VkImageMemoryBarrier2
        {
            SType = VkStructureType.ImageMemoryBarrier2,
            SrcStageMask = srcStage,
            SrcAccessMask = srcAccess,
            DstStageMask = dstStage,
            DstAccessMask = dstAccess,
            OldLayout = old,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = VkConst.QueueFamilyIgnored,
            DstQueueFamilyIndex = VkConst.QueueFamilyIgnored,
            Image = texture.Image,
            SubresourceRange = new VkImageSubresourceRange { AspectMask = texture.Aspect, LevelCount = uint.MaxValue, LayerCount = uint.MaxValue },
        };
        var dep = new VkDependencyInfo { SType = VkStructureType.DependencyInfo, ImageMemoryBarrierCount = 1, ImageMemoryBarriers = &barrier };
        Vk.CmdPipelineBarrier2(cmd, &dep);
        texture.Layout = newLayout;
    }

    private static (ulong Stage, ulong Access) Usage(uint layout, bool source) => layout switch
    {
        VkConst.ImageLayoutUndefined => (source ? VkConst.StageTopOfPipe : VkConst.StageAllCommands, VkConst.AccessNone),
        VkConst.ImageLayoutColorAttachment => (VkConst.StageColorAttachmentOutput,
            VkConst.AccessColorAttachmentWrite | VkConst.AccessColorAttachmentRead),
        VkConst.ImageLayoutDepthAttachment => (VkConst.StageEarlyFragmentTests | VkConst.StageLateFragmentTests,
            VkConst.AccessDepthAttachmentWrite | VkConst.AccessDepthAttachmentRead),
        VkConst.ImageLayoutShaderReadOnly => (VkConst.StageFragmentShader | VkConst.StageVertexShader, VkConst.AccessShaderRead),
        VkConst.ImageLayoutTransferSrc => (VkConst.StageTransfer, VkConst.AccessTransferRead),
        VkConst.ImageLayoutTransferDst => (VkConst.StageTransfer, VkConst.AccessTransferWrite),
        VkConst.ImageLayoutPresentSrc => (source ? VkConst.StageColorAttachmentOutput : VkConst.StageBottomOfPipe, VkConst.AccessNone),
        _ => (VkConst.StageAllCommands, VkConst.AccessMemoryRead),
    };
}
