using System.Numerics;
using System.Runtime.InteropServices;
using Gamegrl.Assets;

namespace Gamegrl.Audio.Acoustics;

public enum AcousticShape : byte
{
    /// <summary>The unit cube from −0.5 to 0.5 through the entity's transform (rooms, walls, crates; matches the built-in cube mesh).</summary>
    Box,
    /// <summary>The triangles of <see cref="AcousticSurface.Mesh"/> through the entity's transform.</summary>
    Mesh,
    /// <summary>A room: the unit cube through the transform, hollow (its inside is air, each wall a thin surface).</summary>
    Room,
}

/// <summary>
/// Geometry that sound reflects off and passes through: a box, a room or a mesh, made of <see cref="Material"/>. Sound
/// passing through is scaled by the material's transmission: a box is solid and counts once per path (a wall, a pillar,
/// a crate); a room is a hollow box whose inside is air and whose walls count once each; a mesh is thin surfaces counting
/// at every crossing.
/// </summary>
public struct AcousticSurface : IComponent
{
    public AcousticShape Shape;
    public AssetRef<MeshAsset> Mesh;
    public AcousticMaterial Material;

    public static AcousticSurface Box(AcousticMaterial material) => new() { Shape = AcousticShape.Box, Material = material };
    public static AcousticSurface Room(AcousticMaterial material) => new() { Shape = AcousticShape.Room, Material = material };
    public static AcousticSurface Of(AssetRef<MeshAsset> mesh, AcousticMaterial material) => new() { Shape = AcousticShape.Mesh, Mesh = mesh, Material = material };
}

/// <summary>
/// Keeps <see cref="AudioContext.Scene"/> in step with the <see cref="AcousticSurface"/>s: every frame it hashes their
/// data and transforms (cheap), and rebuilds the scene and its BVH only when something changed.
/// </summary>
internal sealed class AcousticGeometryUpdate : ISystem
{
    private Query _surfaces = null!;
    private int _hash;
    private bool _built;
    private readonly AcousticSceneBuilder _builder = new();
    private readonly Dictionary<Guid, Vector3[]?> _meshes = [];

    public string Name => "Audio.AcousticGeometry";
    public Phase Phase => Phase.PostUpdate;
    public int Order => 990; // after transforms, before the audio update

    public void Initialize(World world) => _surfaces = world.Query(new QueryDesc().With<AcousticSurface>().With<LocalToWorld>());

    public void Run(World world)
    {
        var hash = new HashCode();
        int count = 0;
        foreach (var a in _surfaces.Archetypes)
        {
            hash.AddBytes(MemoryMarshal.AsBytes(a.GetSpan<AcousticSurface>()));
            hash.AddBytes(MemoryMarshal.AsBytes(a.GetSpan<LocalToWorld>()));
            count += a.Count;
        }
        hash.Add(count);
        int h = hash.ToHashCode();
        if (_built && h == _hash) return;
        _hash = h;
        _built = true;
        world.Resource<AudioContext>().Scene = Build(world);
    }

    private AcousticScene Build(World world)
    {
        _builder.Clear();
        AcousticGeometry.Gather(world, _builder, world.HasResource<IAssetSource>() ? world.Resource<IAssetSource>() : null, _meshes);
        return _builder.Build();
    }
}

/// <summary>Reading acoustic geometry out of a world.</summary>
public static class AcousticGeometry
{
    /// <summary>
    /// Adds every <see cref="AcousticSurface"/> in a world (with its current <see cref="LocalToWorld"/>) to a builder:
    /// what the scene is made of right now. Reads the world, so call it on the world's thread; build on any thread.
    /// <paramref name="meshes"/> caches mesh assets' triangles between calls.
    /// </summary>
    public static void Gather(World world, AcousticSceneBuilder builder, IAssetSource? assets, Dictionary<Guid, Vector3[]?> meshes)
    {
        foreach (var a in world.Query(new QueryDesc().With<AcousticSurface>().With<LocalToWorld>()).Archetypes)
        {
            var surfaces = a.GetSpan<AcousticSurface>();
            var transforms = a.GetSpan<LocalToWorld>();
            for (int r = 0; r < surfaces.Length; r++)
            {
                if (surfaces[r].Shape == AcousticShape.Box) builder.AddBox(transforms[r].Value, surfaces[r].Material);
                else if (surfaces[r].Shape == AcousticShape.Room) builder.AddRoom(transforms[r].Value, surfaces[r].Material);
                else if (MeshTriangles(surfaces[r].Mesh, assets, meshes) is { } local) builder.AddMesh(local, transforms[r].Value, surfaces[r].Material);
            }
        }
    }

    /// <summary>A mesh asset's triangles in its own space, three vertices each (cached; null when missing).</summary>
    private static Vector3[]? MeshTriangles(AssetRef<MeshAsset> mesh, IAssetSource? assets, Dictionary<Guid, Vector3[]?> cache)
    {
        if (mesh.IsNone) return null;
        if (cache.TryGetValue(mesh.Guid, out var cached)) return cached;
        Vector3[]? tris = null;
        if (assets is not null && assets.TryRead(mesh.Guid, out var kind, out var bytes) && kind == AssetKind.Mesh)
        {
            var data = MeshData.FromArtifact(bytes.Span);
            tris = new Vector3[data.Indices.Length];
            for (int i = 0; i < tris.Length; i++) tris[i] = data.Vertices[data.Indices[i]].Position;
        }
        else Log.Warn($"audio: no mesh asset {mesh.Guid:N} for an acoustic surface");
        cache[mesh.Guid] = tris;
        return tris;
    }
}
