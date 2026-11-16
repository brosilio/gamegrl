namespace Gamegrl.Rhi.Vulkan;

internal sealed unsafe class VulkanBuffer : GpuBuffer
{
    private readonly VulkanDevice _device;
    public ulong Handle;
    public ulong MemoryHandle;

    public VulkanBuffer(VulkanDevice device, in BufferDesc desc, ulong handle, ulong memory, byte* mapped)
    {
        _device = device;
        Handle = handle;
        MemoryHandle = memory;
        Size = desc.Size;
        Usage = desc.Usage;
        Memory = desc.Memory;
        Mapped = mapped;
        Name = desc.Name;
    }

    public override void Dispose()
    {
        if (Handle == 0) return;
        _device.DeferDestroy(DeferredKind.Buffer, Handle, MemoryHandle);
        Handle = MemoryHandle = 0;
    }
}

internal sealed class VulkanTexture : GpuTexture
{
    private readonly VulkanDevice? _device; // null: owned by a swapchain
    public ulong Image;
    public ulong MemoryHandle;
    public ulong View;
    public uint VkFormat;
    public uint Aspect;
    /// <summary>The layout the image is in at the current point of command recording.</summary>
    public uint Layout;

    public VulkanTexture(VulkanDevice? device, ulong image, ulong memory, ulong view, int width, int height,
        TextureFormat format, uint vkFormat, TextureUsage usage, string? name)
    {
        _device = device;
        Image = image;
        MemoryHandle = memory;
        View = view;
        Width = width;
        Height = height;
        Format = format;
        VkFormat = vkFormat;
        Usage = usage;
        Aspect = format == TextureFormat.Depth32Float ? VkConst.AspectDepth : VkConst.AspectColor;
        Name = name;
    }

    public override void Dispose()
    {
        if (_device is null || Image == 0) return;
        _device.DeferDestroy(DeferredKind.ImageView, View, 0);
        _device.DeferDestroy(DeferredKind.Image, Image, MemoryHandle);
        Image = View = MemoryHandle = 0;
    }
}

internal sealed class VulkanSampler(VulkanDevice device, ulong handle) : GpuSampler
{
    public ulong Handle = handle;

    public override void Dispose()
    {
        if (Handle == 0) return;
        device.DeferDestroy(DeferredKind.Sampler, Handle, 0);
        Handle = 0;
    }
}

internal sealed class VulkanShader(VulkanDevice device, ulong module, ShaderStages stage, string entry) : GpuShader
{
    public ulong Module = module;
    public readonly ShaderStages Stage = stage;
    public readonly byte[] EntryPoint = Vk.Utf8(entry);

    public override void Dispose()
    {
        if (Module == 0) return;
        device.DeferDestroy(DeferredKind.ShaderModule, Module, 0);
        Module = 0;
    }
}

internal sealed class VulkanBindGroupLayout(VulkanDevice device, ulong handle, BindingLayout[] entries) : BindGroupLayout
{
    public ulong Handle = handle;
    public readonly BindingLayout[] Entries = entries;

    public BindingType TypeOf(int binding)
    {
        foreach (var e in Entries)
            if (e.Binding == binding) return e.Type;
        throw new ArgumentException($"Binding {binding} is not in the layout.");
    }

    public override void Dispose()
    {
        if (Handle == 0) return;
        device.DeferDestroy(DeferredKind.DescriptorSetLayout, Handle, 0);
        Handle = 0;
    }
}

internal sealed class VulkanBindGroup(VulkanDevice device, ulong set) : BindGroup
{
    public ulong Set = set;

    public override void Dispose()
    {
        if (Set == 0) return;
        device.DeferDestroy(DeferredKind.DescriptorSet, Set, 0);
        Set = 0;
    }
}

internal sealed class VulkanPipeline(VulkanDevice device, ulong pipeline, ulong layout) : GraphicsPipeline
{
    public ulong Pipeline = pipeline;
    public ulong Layout = layout;

    public override void Dispose()
    {
        if (Pipeline == 0) return;
        device.DeferDestroy(DeferredKind.Pipeline, Pipeline, 0);
        device.DeferDestroy(DeferredKind.PipelineLayout, Layout, 0);
        Pipeline = Layout = 0;
    }
}

internal enum DeferredKind : byte
{
    Buffer, Image, ImageView, Sampler, ShaderModule, DescriptorSetLayout, DescriptorSet, Pipeline, PipelineLayout,
}

internal readonly record struct Deferred(DeferredKind Kind, ulong Handle, ulong Memory);

internal sealed class VulkanOffscreenSwapchain : Swapchain
{
    public GpuTexture Texture { get; }
    public VulkanOffscreenSwapchain(VulkanDevice device, int width, int height)
    {
        Width = width; Height = height; Format = TextureFormat.Bgra8Srgb;
        Texture = device.CreateTexture(new TextureDesc(width, height, Format, TextureUsage.RenderTarget));
    }
    public override void Dispose() => Texture.Dispose();
}

internal sealed unsafe class VulkanReadback : GpuReadback
{
    private readonly VulkanDevice _device;
    internal readonly VulkanBuffer Buffer;
    internal readonly int Width, Height;
    internal ulong? Frame;
    public VulkanReadback(VulkanDevice device, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width); ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        _device = device; Width = width; Height = height;
        Buffer = (VulkanBuffer)device.CreateBuffer(new BufferDesc(checked(width * height * 4), BufferUsage.CopyDst, MemoryKind.Upload));
    }
    public override bool Pending => Frame is not null;
    public override bool TryRead(Span<byte> destination)
    {
        if (destination.Length < Width * Height * 4) throw new ArgumentException("Readback destination is too small.");
        if (Frame is not { } frame || !_device.IsComplete(frame)) return false;
        new ReadOnlySpan<byte>(Buffer.Mapped, Width * Height * 4).CopyTo(destination); Frame = null; return true;
    }
    public override void Dispose() { Buffer.Dispose(); Frame = null; }
}
