using System.Numerics;
using System.Runtime.InteropServices;
using Gamegrl.Platform;
using Gamegrl.Rhi;

namespace Gamegrl.Rendering;

/// <summary>
/// The renderer's state, as a world resource: the device and swapchain, GPU meshes and textures, the frame being
/// recorded, and the instance lists that extract systems fill each frame.
/// </summary>
public sealed class RenderContext : IDisposable
{
    public const TextureFormat DepthFormat = TextureFormat.Depth32Float;

    private readonly List<Mesh> _meshes = new();
    private readonly List<SpriteTexture> _textures = new();
    private GpuTexture? _depth;

    internal readonly List<MeshInstance> MeshInstances = new();
    internal readonly List<SpriteInstance> SpriteInstances = new();

    public RenderContext(GpuDevice device, IWindow window, bool vsync)
    {
        Device = device;
        Swapchain = device.CreateSwapchain(window, vsync);
        Ring = new UploadRing(device, 16 << 20);
    }

    public GpuDevice Device { get; }
    public Swapchain Swapchain { get; }
    public UploadRing Ring { get; }

    /// <summary>The frame being recorded (valid during the Render phase when <see cref="HasFrame"/>).</summary>
    public Frame Frame { get; internal set; }
    public bool HasFrame { get; internal set; }
    public GpuTexture Depth => _depth!;

    internal readonly record struct Mesh(GpuBuffer Vertices, GpuBuffer Indices, int IndexCount);
    internal readonly record struct SpriteTexture(GpuTexture Texture, BindGroup Group);

    internal Mesh GetMesh(MeshHandle h) => _meshes[h.Id];
    internal SpriteTexture GetTexture(TextureHandle h) => _textures[h.Id];
    internal BindGroupLayout? SpriteTextureLayout { get; set; }
    internal GpuSampler? SpriteSampler { get; set; }

    public MeshHandle CreateMesh(ReadOnlySpan<MeshVertex> vertices, ReadOnlySpan<ushort> indices)
    {
        var vb = Device.CreateBuffer(new BufferDesc(vertices.Length * Marshal.SizeOf<MeshVertex>(), BufferUsage.Vertex));
        var ib = Device.CreateBuffer(new BufferDesc(indices.Length * sizeof(ushort), BufferUsage.Index));
        Device.WriteBuffer(vb, vertices);
        Device.WriteBuffer(ib, indices);
        _meshes.Add(new Mesh(vb, ib, indices.Length));
        return new MeshHandle(_meshes.Count - 1);
    }

    /// <summary>Upload an RGBA8 (sRGB) image for sprites.</summary>
    public TextureHandle CreateTexture(int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (rgba.Length != width * height * 4) throw new ArgumentException("Expected width × height × 4 bytes.");
        var tex = Device.CreateTexture(new TextureDesc(width, height, TextureFormat.Rgba8Srgb, TextureUsage.Sampled));
        Device.WriteTexture(tex, rgba);
        var group = Device.CreateBindGroup(SpriteTextureLayout!, [BindGroupEntry.Of(0, tex), BindGroupEntry.Of(1, SpriteSampler!)]);
        _textures.Add(new SpriteTexture(tex, group));
        return new TextureHandle(_textures.Count - 1);
    }

    internal void EnsureDepth(int width, int height)
    {
        if (_depth is not null && _depth.Width == width && _depth.Height == height) return;
        _depth?.Dispose();
        _depth = Device.CreateTexture(new TextureDesc(width, height, DepthFormat, TextureUsage.DepthStencil, Name: "depth"));
    }

    public void Dispose()
    {
        Device.WaitIdle();
        foreach (var m in _meshes)
        {
            m.Vertices.Dispose();
            m.Indices.Dispose();
        }
        foreach (var t in _textures)
        {
            t.Group.Dispose();
            t.Texture.Dispose();
        }
        _depth?.Dispose();
        Ring.Dispose();
        Swapchain.Dispose();
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct MeshInstance
{
    public Matrix4x4 Model;
    public Vector4 Color;
    public int Mesh; // sort key; not uploaded
}

[StructLayout(LayoutKind.Sequential)]
internal struct MeshInstanceGpu
{
    public Matrix4x4 Model;
    public Vector4 Color;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SpriteInstance
{
    public Vector4 PosSize;
    public Vector4 Uv;
    public Vector4 Color;
    public Vector4 RotPivot;
    public int Texture; // sort key; not uploaded
}

[StructLayout(LayoutKind.Sequential)]
internal struct SpriteInstanceGpu
{
    public Vector4 PosSize;
    public Vector4 Uv;
    public Vector4 Color;
    public Vector4 RotPivot;
}
