using System.Numerics;
using Gamegrl.Platform;

namespace Gamegrl.Rhi;

// The render hardware interface. Its shape follows WebGPU (the lowest common denominator of Vulkan, D3D12, Metal
// and WebGPU): bind groups instead of raw descriptors, dynamic uniform offsets instead of push constants,
// render passes that name their attachments. Backends: Vulkan first.

public enum TextureFormat
{
    Unknown,
    Rgba8Unorm,
    Rgba8Srgb,
    Bgra8Unorm,
    Bgra8Srgb,
    Rgba16Float,
    Depth32Float,
}

[Flags]
public enum BufferUsage
{
    None = 0,
    Vertex = 1,
    Index = 2,
    Uniform = 4,
    Storage = 8,
    CopySrc = 16,
    CopyDst = 32,
}

public enum MemoryKind
{
    /// <summary>GPU-local; written through uploads.</summary>
    GpuOnly,
    /// <summary>CPU-writable and persistently mapped; for per-frame data. May live in GPU memory the CPU reads slowly.</summary>
    Upload,
    /// <summary>GPU-writable, CPU-readable and persistently mapped, in cached system memory: for downloads.</summary>
    Readback,
}

[Flags]
public enum TextureUsage
{
    None = 0,
    Sampled = 1,
    RenderTarget = 2,
    DepthStencil = 4,
    CopyDst = 8,
    CopySrc = 16,
}

public enum IndexFormat { UInt16, UInt32 }
public enum PrimitiveTopology { TriangleList, TriangleStrip, LineList }
public enum CullMode { None, Front, Back }
public enum FrontFace { CounterClockwise, Clockwise }
public enum CompareOp { Never, Less, Equal, LessOrEqual, Greater, NotEqual, GreaterOrEqual, Always }
public enum BlendMode { Opaque, Alpha, Premultiplied, Additive }
public enum Filter { Nearest, Linear }
public enum AddressMode { Repeat, ClampToEdge, MirroredRepeat }
public enum LoadOp { Load, Clear, DontCare }
public enum StoreOp { Store, DontCare }

[Flags]
public enum ShaderStages { None = 0, Vertex = 1, Fragment = 2, Compute = 4, Graphics = Vertex | Fragment }

public enum VertexFormat { Float, Float2, Float3, Float4, UByte4Norm }
public enum VertexStep { Vertex, Instance }

public enum BindingType
{
    UniformBuffer,
    /// <summary>A uniform buffer whose offset is given at bind time (per-frame data in a ring).</summary>
    UniformBufferDynamic,
    StorageBufferReadOnly,
    /// <summary>A storage buffer whose offset is given at bind time.</summary>
    StorageBufferReadOnlyDynamic,
    SampledTexture,
    Sampler,
}

public readonly record struct BufferDesc(long Size, BufferUsage Usage, MemoryKind Memory = MemoryKind.GpuOnly, string? Name = null);

public readonly record struct TextureDesc(
    int Width, int Height, TextureFormat Format, TextureUsage Usage, int MipLevels = 1, string? Name = null);

public readonly record struct SamplerDesc(Filter Filter = Filter.Linear, AddressMode Address = AddressMode.Repeat);

public readonly record struct ShaderDesc(ShaderStages Stage, byte[] Code, string EntryPoint = "main", string? Name = null);

public readonly record struct BindingLayout(int Binding, BindingType Type, ShaderStages Visibility);

public readonly record struct VertexAttribute(int Location, VertexFormat Format, int Offset);

public sealed record VertexBufferLayout(int Stride, VertexStep Step, params VertexAttribute[] Attributes);

/// <summary>One resource in a bind group: set exactly one of buffer, texture or sampler.</summary>
public readonly record struct BindGroupEntry(int Binding, GpuBuffer? Buffer = null, long Offset = 0, long Size = 0,
    GpuTexture? Texture = null, GpuSampler? Sampler = null)
{
    public static BindGroupEntry Of(int binding, GpuBuffer buffer, long offset = 0, long size = 0) => new(binding, Buffer: buffer, Offset: offset, Size: size);
    public static BindGroupEntry Of(int binding, GpuTexture texture) => new(binding, Texture: texture);
    public static BindGroupEntry Of(int binding, GpuSampler sampler) => new(binding, Sampler: sampler);
}

public sealed class GraphicsPipelineDesc
{
    public required GpuShader VertexShader { get; init; }
    public required GpuShader FragmentShader { get; init; }
    public VertexBufferLayout[] VertexBuffers { get; init; } = [];
    public BindGroupLayout[] BindGroupLayouts { get; init; } = [];
    public PrimitiveTopology Topology { get; init; } = PrimitiveTopology.TriangleList;
    public CullMode CullMode { get; init; } = CullMode.Back;
    public FrontFace FrontFace { get; init; } = FrontFace.CounterClockwise;
    public bool DepthTest { get; init; } = true;
    public bool DepthWrite { get; init; } = true;
    /// <summary>Reverse-Z everywhere: near = 1, far = 0, so the default test is Greater.</summary>
    public CompareOp DepthCompare { get; init; } = CompareOp.Greater;
    public BlendMode Blend { get; init; } = BlendMode.Opaque;
    public TextureFormat[] ColorFormats { get; init; } = [];
    public TextureFormat DepthFormat { get; init; } = TextureFormat.Unknown;
    public string? Name { get; init; }
}

public struct ColorAttachment
{
    public GpuTexture Texture;
    public LoadOp Load;
    public StoreOp Store;
    public Vector4 ClearColor;
}

public struct DepthAttachment
{
    public GpuTexture Texture;
    public LoadOp Load;
    public StoreOp Store;
    /// <summary>Reverse-Z: clear to 0 (the far plane).</summary>
    public float ClearDepth;
}

public struct RenderPassDesc
{
    public ColorAttachment Color;
    public DepthAttachment? Depth;
}

public readonly record struct DeviceLimits(int MinUniformBufferOffsetAlignment, int MinStorageBufferOffsetAlignment, long MaxUniformBufferRange);

// ------------------------------------------------------------------ resources

public abstract class GpuResource : IDisposable
{
    public string? Name { get; protected init; }
    public abstract void Dispose();
}

public abstract unsafe class GpuBuffer : GpuResource
{
    public long Size { get; protected init; }
    public BufferUsage Usage { get; protected init; }
    public MemoryKind Memory { get; protected init; }

    /// <summary>The persistent CPU mapping of an <see cref="MemoryKind.Upload"/> or <see cref="MemoryKind.Readback"/> buffer; null otherwise.</summary>
    public byte* Mapped { get; protected init; }

    /// <summary>Write into an Upload buffer at <paramref name="offset"/>.</summary>
    public void Write<T>(ReadOnlySpan<T> data, long offset = 0) where T : unmanaged
    {
        if (Mapped == null) throw new InvalidOperationException("Only Upload buffers can be written directly; use GpuDevice.WriteBuffer.");
        long bytes = (long)data.Length * sizeof(T);
        if (offset < 0 || offset + bytes > Size) throw new ArgumentOutOfRangeException(nameof(offset));
        fixed (T* src = data) Buffer.MemoryCopy(src, Mapped + offset, Size - offset, bytes);
    }
}

public abstract class GpuTexture : GpuResource
{
    public int Width { get; protected set; }
    public int Height { get; protected set; }
    public TextureFormat Format { get; protected set; }
    public TextureUsage Usage { get; protected set; }
}

/// <summary>A bounded asynchronous texture download. A successful read consumes the pending copy.</summary>
public abstract class GpuReadback : GpuResource
{
    public abstract bool Pending { get; }
    public abstract bool TryRead(Span<byte> destination);
}

public abstract class GpuSampler : GpuResource;
public abstract class GpuShader : GpuResource;
public abstract class BindGroupLayout : GpuResource;
public abstract class BindGroup : GpuResource;
public abstract class GraphicsPipeline : GpuResource;

/// <summary>A window's chain of presentable images. Recreated automatically when the window resizes.</summary>
public abstract class Swapchain : GpuResource
{
    public TextureFormat Format { get; protected set; }
    public int Width { get; protected set; }
    public int Height { get; protected set; }
}

// ------------------------------------------------------------------ commands

/// <summary>Records GPU work for one frame. Not thread-safe.</summary>
public abstract class CommandList
{
    public abstract void BeginRenderPass(in RenderPassDesc desc);
    public abstract void EndRenderPass();
    public abstract void SetPipeline(GraphicsPipeline pipeline);
    public abstract void SetBindGroup(int index, BindGroup group, ReadOnlySpan<uint> dynamicOffsets = default);
    public abstract void SetVertexBuffer(int slot, GpuBuffer buffer, long offset = 0);
    public abstract void SetIndexBuffer(GpuBuffer buffer, IndexFormat format, long offset = 0);
    public abstract void SetViewport(float x, float y, float width, float height);
    public abstract void SetScissor(int x, int y, int width, int height);
    public abstract void CopyToReadback(GpuTexture texture, GpuReadback readback);
    public abstract void Draw(int vertexCount, int instanceCount = 1, int firstVertex = 0, int firstInstance = 0);
    public abstract void DrawIndexed(int indexCount, int instanceCount = 1, int firstIndex = 0, int vertexOffset = 0, int firstInstance = 0);

    /// <summary>
    /// Time the GPU work recorded inside the <c>using</c> block; results reach the <see cref="Profiler"/> once the
    /// GPU finishes the frame. Free when profiling is off or the backend has no timestamps.
    /// </summary>
    public GpuProfileScope Zone(ProfileMarker marker)
    {
        BeginZone(marker);
        return new GpuProfileScope(this);
    }

    protected virtual void BeginZone(ProfileMarker marker) { }
    protected internal virtual void EndZone() { }
}

/// <summary>An open GPU zone (see <see cref="CommandList.Zone"/>).</summary>
public readonly ref struct GpuProfileScope(CommandList list)
{
    public void Dispose() => list.EndZone();
}

/// <summary>What <see cref="GpuDevice.BeginFrame"/> hands out: the commands to record and the image to draw into.</summary>
public readonly record struct Frame(CommandList Commands, GpuTexture Backbuffer, int FrameIndex, ulong Number);

// ------------------------------------------------------------------ device

public abstract class GpuDevice : IDisposable
{
    /// <summary>Frames the CPU may record ahead of the GPU; per-frame resources come in this many copies.</summary>
    public const int FramesInFlight = 2;

    public abstract string BackendName { get; }
    public abstract string AdapterName { get; }
    public abstract DeviceLimits Limits { get; }

    /// <summary>A swapchain presenting to <paramref name="window"/>.</summary>
    public abstract Swapchain CreateSwapchain(IWindow window, bool vsync = true);

    public abstract GpuBuffer CreateBuffer(in BufferDesc desc);
    public abstract GpuTexture CreateTexture(in TextureDesc desc);
    public abstract GpuReadback CreateReadback(int width, int height);
    public abstract GpuSampler CreateSampler(in SamplerDesc desc);
    public abstract GpuShader CreateShader(in ShaderDesc desc);
    public abstract BindGroupLayout CreateBindGroupLayout(ReadOnlySpan<BindingLayout> entries);
    public abstract BindGroup CreateBindGroup(BindGroupLayout layout, ReadOnlySpan<BindGroupEntry> entries);
    public abstract GraphicsPipeline CreateGraphicsPipeline(GraphicsPipelineDesc desc);

    /// <summary>Copy <paramref name="data"/> into a GPU-only buffer (blocking; for load time).</summary>
    public abstract void WriteBuffer<T>(GpuBuffer buffer, ReadOnlySpan<T> data, long offset = 0) where T : unmanaged;

    /// <summary>Upload mip 0 of <paramref name="texture"/> (blocking; for load time).</summary>
    public abstract void WriteTexture(GpuTexture texture, ReadOnlySpan<byte> pixels);

    /// <summary>
    /// Wait for this frame slot's previous use to finish, acquire the next swapchain image and start recording.
    /// Returns false when there's nothing to draw into (minimized window); skip the frame.
    /// </summary>
    public abstract bool BeginFrame(Swapchain swapchain, out Frame frame);

    /// <summary>Submit the frame's commands and present.</summary>
    public abstract void EndFrame(in Frame frame);

    public abstract void WaitIdle();
    public abstract void Dispose();
}
