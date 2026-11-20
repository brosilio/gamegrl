using System.Numerics;
using System.Runtime.InteropServices;
using Gamegrl.Assets;
using Gamegrl.Platform;
using Gamegrl.Rhi;

namespace Gamegrl.Rendering;

/// <summary>
/// The renderer's state, as a world resource: the device and swapchain, GPU meshes and textures (resolved from
/// asset GUIDs on first use and re-uploaded when the asset source reports a change), the frame being recorded,
/// and the instance lists that extract systems fill each frame.
/// TODO(milestone 4): asynchronous streaming instead of loading on first use.
/// </summary>
public sealed class RenderContext : IDisposable
{
    public const TextureFormat DepthFormat = TextureFormat.Depth32Float;

    private readonly List<Mesh?> _meshes = new();
    private readonly List<SpriteTexture?> _textures = new();
    private readonly Dictionary<Guid, int> _meshSlots = new();
    private readonly Dictionary<Guid, int> _textureSlots = new();
    private readonly HashSet<Guid> _missing = new();
    private readonly IAssetSource? _assets;
    private GpuTexture? _depth;

    internal readonly List<MeshInstance> MeshInstances = new();
    internal readonly List<SpriteInstance> SpriteInstances = new();

    public RenderContext(GpuDevice device, IWindow window, bool vsync, IAssetSource? assets)
    {
        Device = device;
        Swapchain = device.CreateSwapchain(window, vsync);
        Ring = new UploadRing(device, 16 << 20);
        _assets = assets;
        if (assets is not null) assets.Changed += OnAssetChanged;
    }

    public GpuDevice Device { get; }
    public Swapchain Swapchain { get; }
    public UploadRing Ring { get; }

    /// <summary>Draws views of the world (set up by the rendering module).</summary>
    public SceneRenderer Scene { get; internal set; } = null!;

    /// <summary>Lines for gizmos and debugging; add during Update, drawn by views that ask for them.</summary>
    public DebugDraw Debug { get; } = new();

    /// <summary>
    /// Draw the first camera into the window each frame (the game). The editor turns this off and draws its own
    /// views and UI instead.
    /// </summary>
    public bool DrawMainCamera { get; set; } = true;

    /// <summary>The local-space bounds of a mesh (loading it if needed), for picking and framing.</summary>
    public bool TryGetBounds(AssetRef<MeshAsset> mesh, out Vector3 min, out Vector3 max)
    {
        int slot = ResolveMesh(mesh);
        if (slot < 0)
        {
            min = max = Vector3.Zero;
            return false;
        }
        var m = _meshes[slot]!.Value;
        (min, max) = (m.Min, m.Max);
        return true;
    }

    /// <summary>The frame being recorded (valid during the Render phase when <see cref="HasFrame"/>).</summary>
    public Frame Frame { get; internal set; }
    public bool HasFrame { get; internal set; }
    public GpuTexture Depth => _depth!;

    /// <summary>Reports problems (a missing or broken asset) once per asset.</summary>
    public Action<string> Warn { get; set; } = message => Log.Warn("rendering: " + message);

    internal readonly record struct Mesh(GpuBuffer Vertices, GpuBuffer Indices, int IndexCount, IndexFormat IndexFormat, Vector3 Min, Vector3 Max);
    internal readonly record struct SpriteTexture(GpuTexture Texture, BindGroup Group);

    internal Mesh GetMesh(int slot) => _meshes[slot]!.Value;
    internal SpriteTexture GetTexture(int slot) => _textures[slot]!.Value;
    internal BindGroupLayout? SpriteTextureLayout { get; set; }
    internal GpuSampler? SpriteSampler { get; set; }

    // ------------------------------------------------------------------ resolution

    /// <summary>The GPU slot for <paramref name="mesh"/>, loading it from the asset source if needed; -1 if unavailable.</summary>
    public int ResolveMesh(AssetRef<MeshAsset> mesh)
    {
        if (_meshSlots.TryGetValue(mesh.Guid, out int slot)) return slot;
        if (_assets is null || !_assets.TryRead(mesh.Guid, out var kind, out var bytes) || kind != AssetKind.Mesh)
        {
            Missing(mesh.Guid, "mesh");
            return -1;
        }
        return Register(mesh, MeshData.FromArtifact(bytes.Span));
    }

    public int ResolveTexture(AssetRef<TextureAsset> texture)
    {
        if (_textureSlots.TryGetValue(texture.Guid, out int slot)) return slot;
        if (_assets is null || !_assets.TryRead(texture.Guid, out var kind, out var bytes) || kind != AssetKind.Texture)
        {
            Missing(texture.Guid, "texture");
            return -1;
        }
        return Register(texture, TextureData.FromArtifact(bytes.Span));
    }

    /// <summary>Upload <paramref name="data"/> as the mesh for <paramref name="mesh"/> (replacing it if present).</summary>
    public int Register(AssetRef<MeshAsset> mesh, MeshData data)
    {
        var vb = Device.CreateBuffer(new BufferDesc(data.Vertices.Length * Marshal.SizeOf<Vertex>(), BufferUsage.Vertex));
        Device.WriteBuffer<Vertex>(vb, data.Vertices);
        GpuBuffer ib;
        IndexFormat format;
        if (data.Vertices.Length <= ushort.MaxValue)
        {
            var shorts = new ushort[data.Indices.Length];
            for (int i = 0; i < shorts.Length; i++) shorts[i] = (ushort)data.Indices[i];
            ib = Device.CreateBuffer(new BufferDesc(shorts.Length * 2, BufferUsage.Index));
            Device.WriteBuffer<ushort>(ib, shorts);
            format = IndexFormat.UInt16;
        }
        else
        {
            ib = Device.CreateBuffer(new BufferDesc(data.Indices.Length * 4, BufferUsage.Index));
            Device.WriteBuffer<uint>(ib, data.Indices);
            format = IndexFormat.UInt32;
        }
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var v in data.Vertices)
        {
            min = Vector3.Min(min, v.Position);
            max = Vector3.Max(max, v.Position);
        }
        if (data.Vertices.Length == 0) min = max = Vector3.Zero;
        var entry = new Mesh(vb, ib, data.Indices.Length, format, min, max);
        if (_meshSlots.TryGetValue(mesh.Guid, out int slot))
        {
            var old = _meshes[slot]!.Value;
            old.Vertices.Dispose(); // deferred until the GPU is done with it
            old.Indices.Dispose();
            _meshes[slot] = entry;
            return slot;
        }
        _meshes.Add(entry);
        _meshSlots[mesh.Guid] = _meshes.Count - 1;
        _missing.Remove(mesh.Guid);
        return _meshes.Count - 1;
    }

    public int Register(AssetRef<TextureAsset> texture, TextureData data)
    {
        var tex = Device.CreateTexture(new TextureDesc(data.Width, data.Height, data.Srgb ? TextureFormat.Rgba8Srgb : TextureFormat.Rgba8Unorm, TextureUsage.Sampled));
        Device.WriteTexture(tex, data.Rgba);
        var group = Device.CreateBindGroup(SpriteTextureLayout!, [BindGroupEntry.Of(0, tex), BindGroupEntry.Of(1, SpriteSampler!)]);
        var entry = new SpriteTexture(tex, group);
        if (_textureSlots.TryGetValue(texture.Guid, out int slot))
        {
            var old = _textures[slot]!.Value;
            old.Group.Dispose();
            old.Texture.Dispose();
            _textures[slot] = entry;
            return slot;
        }
        _textures.Add(entry);
        _textureSlots[texture.Guid] = _textures.Count - 1;
        _missing.Remove(texture.Guid);
        return _textures.Count - 1;
    }

    private void OnAssetChanged(Guid guid)
    {
        if (_assets is null || !_assets.TryRead(guid, out var kind, out var bytes)) return;
        try
        {
            if (kind == AssetKind.Mesh && _meshSlots.ContainsKey(guid)) Register(new AssetRef<MeshAsset>(guid), MeshData.FromArtifact(bytes.Span));
            else if (kind == AssetKind.Texture && _textureSlots.ContainsKey(guid)) Register(new AssetRef<TextureAsset>(guid), TextureData.FromArtifact(bytes.Span));
        }
        catch (FormatException e)
        {
            Warn($"Reloading {guid:N} failed: {e.Message}");
        }
    }

    private void Missing(Guid guid, string what)
    {
        if (_missing.Add(guid)) Warn($"No {what} asset {guid:N}; entities using it are skipped.");
    }

    internal void EnsureDepth(int width, int height)
    {
        if (_depth is not null && _depth.Width == width && _depth.Height == height) return;
        _depth?.Dispose();
        _depth = Device.CreateTexture(new TextureDesc(width, height, DepthFormat, TextureUsage.DepthStencil, Name: "depth"));
    }

    private bool _disposed;

    public void Dispose()
    {
        // The rendering module releases the context before the device. The world also owns this resource.
        if (_disposed) return;
        _disposed = true;
        if (_assets is not null) _assets.Changed -= OnAssetChanged;
        Device.WaitIdle();
        foreach (var m in _meshes)
        {
            m?.Vertices.Dispose();
            m?.Indices.Dispose();
        }
        foreach (var t in _textures)
        {
            t?.Group.Dispose();
            t?.Texture.Dispose();
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
    public int Mesh; // slot; sort key, not uploaded
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
    public int Texture; // slot; sort key, not uploaded
}

[StructLayout(LayoutKind.Sequential)]
internal struct SpriteInstanceGpu
{
    public Vector4 PosSize;
    public Vector4 Uv;
    public Vector4 Color;
    public Vector4 RotPivot;
}
