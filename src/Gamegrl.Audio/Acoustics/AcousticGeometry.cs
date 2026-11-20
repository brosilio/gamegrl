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
}

/// <summary>
/// Geometry that sound reflects off and passes through: a box or a mesh, made of <see cref="Material"/>. Sound passing
/// through is scaled by the material's transmission: a box is solid and counts once per path (a wall, a pillar, a crate,
/// or a box room around the listener); a mesh is thin surfaces and counts at every crossing (model rooms as meshes or
/// as separate wall boxes).
/// </summary>
public struct AcousticSurface : IComponent
{
    public AcousticShape Shape;
    public AssetRef<MeshAsset> Mesh;
    public AcousticMaterial Material;

    public static AcousticSurface Box(AcousticMaterial material) => new() { Shape = AcousticShape.Box, Material = material };
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
        var assets = world.HasResource<IAssetSource>() ? world.Resource<IAssetSource>() : null;
        foreach (var a in _surfaces.Archetypes)
        {
            var surfaces = a.GetSpan<AcousticSurface>();
            var transforms = a.GetSpan<LocalToWorld>();
            for (int r = 0; r < surfaces.Length; r++)
            {
                if (surfaces[r].Shape == AcousticShape.Box) _builder.AddBox(transforms[r].Value, surfaces[r].Material);
                else if (MeshTriangles(surfaces[r].Mesh, assets) is { } local) _builder.AddMesh(local, transforms[r].Value, surfaces[r].Material);
            }
        }
        return _builder.Build();
    }

    /// <summary>A mesh asset's triangles in its own space, three vertices each (cached; null when missing).</summary>
    private Vector3[]? MeshTriangles(AssetRef<MeshAsset> mesh, IAssetSource? assets)
    {
        if (mesh.IsNone) return null;
        if (_meshes.TryGetValue(mesh.Guid, out var cached)) return cached;
        Vector3[]? tris = null;
        if (assets is not null && assets.TryRead(mesh.Guid, out var kind, out var bytes) && kind == AssetKind.Mesh)
        {
            var data = MeshData.FromArtifact(bytes.Span);
            tris = new Vector3[data.Indices.Length];
            for (int i = 0; i < tris.Length; i++) tris[i] = data.Vertices[data.Indices[i]].Position;
        }
        else Log.Warn($"audio: no mesh asset {mesh.Guid:N} for an acoustic surface");
        _meshes[mesh.Guid] = tris;
        return tris;
    }
}
