using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Gamegrl.Assets;

/// <summary>What an imported asset is. Artifacts are the runtime formats importers produce and players load.</summary>
public enum AssetKind : uint
{
    Unknown = 0,
    Texture = 1,
    Mesh = 2,
    Scene = 3,
    /// <summary>A prefab, flattened to the binary scene format; instantiate it with <see cref="Scene.LoadBinary"/>.</summary>
    Prefab = 4,
    /// <summary>A TrueType font file, stored as is; the UI bakes it at the size it needs.</summary>
    Font = 5,
}

// Marker types for AssetRef<T>.
public sealed class TextureAsset;
public sealed class MeshAsset;
public sealed class FontAsset;
public sealed class SceneAsset;

/// <summary>A decoded image: RGBA8, rows top to bottom.</summary>
public sealed class TextureData(int width, int height, byte[] rgba, bool srgb = true)
{
    private const uint Magic = 0x58544747; // "GGTX"

    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Rgba { get; } = rgba;
    public bool Srgb { get; } = srgb;

    public byte[] ToArtifact()
    {
        var b = new byte[20 + Rgba.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(b, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), Width);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(12), Height);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), Srgb ? 0u : 1u);
        Rgba.CopyTo(b.AsSpan(20));
        return b;
    }

    public static TextureData FromArtifact(ReadOnlySpan<byte> a)
    {
        if (a.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(a) != Magic) throw new FormatException("Not a texture artifact.");
        int w = BinaryPrimitives.ReadInt32LittleEndian(a[8..]), h = BinaryPrimitives.ReadInt32LittleEndian(a[12..]);
        bool srgb = BinaryPrimitives.ReadUInt32LittleEndian(a[16..]) == 0;
        return new TextureData(w, h, a.Slice(20, w * h * 4).ToArray(), srgb);
    }
}

/// <summary>A mesh vertex: position, normal, texture coordinate (32 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Vertex(Vector3 position, Vector3 normal, Vector2 uv)
{
    public Vector3 Position = position;
    public Vector3 Normal = normal;
    public Vector2 Uv = uv;
}

/// <summary>Decoded mesh geometry: one vertex stream, 32-bit indices, triangle list.</summary>
public sealed class MeshData(Vertex[] vertices, uint[] indices)
{
    private const uint Magic = 0x534D4747; // "GGMS"

    public Vertex[] Vertices { get; } = vertices;
    public uint[] Indices { get; } = indices;

    public byte[] ToArtifact()
    {
        var vb = MemoryMarshal.AsBytes(Vertices.AsSpan());
        var ib = MemoryMarshal.AsBytes(Indices.AsSpan());
        var b = new byte[16 + vb.Length + ib.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(b, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), Vertices.Length);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(12), Indices.Length);
        vb.CopyTo(b.AsSpan(16));
        ib.CopyTo(b.AsSpan(16 + vb.Length));
        return b;
    }

    public static MeshData FromArtifact(ReadOnlySpan<byte> a)
    {
        if (a.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(a) != Magic) throw new FormatException("Not a mesh artifact.");
        int vc = BinaryPrimitives.ReadInt32LittleEndian(a[8..]), ic = BinaryPrimitives.ReadInt32LittleEndian(a[12..]);
        int vbytes = vc * Marshal.SizeOf<Vertex>();
        var vertices = MemoryMarshal.Cast<byte, Vertex>(a.Slice(16, vbytes)).ToArray();
        var indices = MemoryMarshal.Cast<byte, uint>(a.Slice(16 + vbytes, ic * 4)).ToArray();
        return new MeshData(vertices, indices);
    }
}
