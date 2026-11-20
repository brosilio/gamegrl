using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Gamegrl.Audio.Acoustics;

/// <summary>A ray's hit: distance along it, the triangle, its unit normal (as wound) and its material.</summary>
public struct AcousticHit
{
    public float Distance;
    public int Triangle, Material;
    public Vector3 Normal;
}

/// <summary>
/// The geometry sound sees: triangles with materials, grouped into instances (a box, a mesh), in a bounding-volume
/// hierarchy for ray queries (closest hit, and the surfaces a segment crosses for transmission). Solid instances
/// (boxes) count once per path however many of their faces it crosses (a wall, a pillar, a box room around the
/// listener); open ones (meshes) count at every crossing (thin walls). Immutable once built, so the acoustic simulation
/// can read it from another thread while the game builds the next one. Build one with <see cref="AcousticSceneBuilder"/>.
/// </summary>
public sealed class AcousticScene
{
    private struct Node
    {
        public Vector3 Min, Max;
        public int Left;   // inner: left child (right is Left + 1); leaf: first triangle in _order
        public int Count;  // leaf: triangle count; inner: 0
    }

    private readonly Vector3[] _a, _e1, _e2; // triangle corner and edges (Möller–Trumbore)
    private readonly Vector3[] _normals;
    private readonly int[] _materials, _instances;
    private readonly bool[] _solid;
    private readonly AcousticMaterial[] _palette;
    private readonly int[] _order;
    private Node[] _nodes = [];
    private int _nodeCount;

    public int TriangleCount => _a.Length;
    public float SurfaceArea { get; }
    public ReadOnlySpan<AcousticMaterial> Materials => _palette;
    public Vector3 BoundsMin { get; }
    public Vector3 BoundsMax { get; }
    public static readonly AcousticScene Empty = new([], [], [], [], []);

    /// <param name="vertices">Three per triangle.</param>
    /// <param name="materials">A material index per triangle, into <paramref name="palette"/>.</param>
    /// <param name="instances">An instance index per triangle, into <paramref name="solid"/>.</param>
    internal AcousticScene(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> materials, ReadOnlySpan<int> instances, AcousticMaterial[] palette, bool[] solid)
    {
        int n = vertices.Length / 3;
        _a = new Vector3[n]; _e1 = new Vector3[n]; _e2 = new Vector3[n]; _normals = new Vector3[n];
        _materials = materials[..n].ToArray();
        _instances = instances[..n].ToArray();
        _solid = solid;
        _palette = palette;
        _order = new int[n];
        float area = 0;
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        for (int t = 0; t < n; t++)
        {
            var a = vertices[3 * t]; var b = vertices[3 * t + 1]; var c = vertices[3 * t + 2];
            _a[t] = a; _e1[t] = b - a; _e2[t] = c - a;
            var cross = Vector3.Cross(_e1[t], _e2[t]);
            float len = cross.Length();
            _normals[t] = len > 0 ? cross / len : Vector3.UnitY;
            area += 0.5f * len;
            _order[t] = t;
            min = Vector3.Min(min, Vector3.Min(a, Vector3.Min(b, c)));
            max = Vector3.Max(max, Vector3.Max(a, Vector3.Max(b, c)));
        }
        SurfaceArea = area;
        BoundsMin = n > 0 ? min : Vector3.Zero;
        BoundsMax = n > 0 ? max : Vector3.Zero;
        if (n > 0) Build();
    }

    public AcousticMaterial MaterialOf(int triangle) => _palette[_materials[triangle]];

    // ------------------------------------------------------------------ building

    private void Build()
    {
        int n = _a.Length;
        var centroids = new Vector3[n];
        var boxMin = new Vector3[n]; var boxMax = new Vector3[n];
        for (int t = 0; t < n; t++)
        {
            var a = _a[t]; var b = a + _e1[t]; var c = a + _e2[t];
            boxMin[t] = Vector3.Min(a, Vector3.Min(b, c));
            boxMax[t] = Vector3.Max(a, Vector3.Max(b, c));
            centroids[t] = (a + b + c) / 3;
        }
        _nodes = new Node[Math.Max(1, 2 * n)];
        _nodeCount = 1;
        Split(0, 0, n, centroids, boxMin, boxMax);
    }

    /// <summary>Median split on the widest axis of the centroids' bounds, down to leaves of four triangles.</summary>
    private void Split(int node, int start, int count, Vector3[] centroids, Vector3[] boxMin, Vector3[] boxMax)
    {
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        var cmin = new Vector3(float.MaxValue); var cmax = new Vector3(float.MinValue);
        for (int i = start; i < start + count; i++)
        {
            int t = _order[i];
            min = Vector3.Min(min, boxMin[t]); max = Vector3.Max(max, boxMax[t]);
            cmin = Vector3.Min(cmin, centroids[t]); cmax = Vector3.Max(cmax, centroids[t]);
        }
        _nodes[node].Min = min; _nodes[node].Max = max;
        var extent = cmax - cmin;
        if (count <= 4 || extent.LengthSquared() < 1e-12f)
        {
            _nodes[node].Left = start; _nodes[node].Count = count;
            return;
        }
        int axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
        var span = _order.AsSpan(start, count);
        int half = count / 2;
        // Partial sort: nth element by the centroid on the axis.
        Select(span, half, centroids, axis);
        int left = _nodeCount;
        _nodeCount += 2;
        _nodes[node].Left = left; _nodes[node].Count = 0;
        Split(left, start, half, centroids, boxMin, boxMax);
        Split(left + 1, start + half, count - half, centroids, boxMin, boxMax);
    }

    private static float Axis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    private static void Select(Span<int> items, int k, Vector3[] centroids, int axis)
    {
        int lo = 0, hi = items.Length - 1;
        while (lo < hi)
        {
            float pivot = Axis(centroids[items[(lo + hi) / 2]], axis);
            int i = lo, j = hi;
            while (i <= j)
            {
                while (Axis(centroids[items[i]], axis) < pivot) i++;
                while (Axis(centroids[items[j]], axis) > pivot) j--;
                if (i <= j) { (items[i], items[j]) = (items[j], items[i]); i++; j--; }
            }
            if (k <= j) hi = j; else if (k >= i) lo = i; else return;
        }
    }

    // ------------------------------------------------------------------ queries

    /// <summary>The closest hit along a ray within <paramref name="maxDistance"/> (direction need not be unit; distance is in its units).</summary>
    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out AcousticHit hit)
    {
        hit = default;
        if (_a.Length == 0) return false;
        var inv = new Vector3(1 / direction.X, 1 / direction.Y, 1 / direction.Z);
        float best = maxDistance;
        int bestTri = -1;
        Span<int> stack = stackalloc int[64];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            ref var node = ref _nodes[stack[--sp]];
            if (!Slab(origin, inv, node.Min, node.Max, best)) continue;
            if (node.Count > 0)
            {
                for (int i = node.Left; i < node.Left + node.Count; i++)
                {
                    int t = _order[i];
                    float d = Intersect(t, origin, direction);
                    if (d > 1e-5f && d < best) { best = d; bestTri = t; }
                }
            }
            else if (sp + 2 <= stack.Length) { stack[sp++] = node.Left; stack[sp++] = node.Left + 1; }
        }
        if (bestTri < 0) return false;
        hit = new AcousticHit { Distance = best, Triangle = bestTri, Material = _materials[bestTri], Normal = _normals[bestTri] };
        return true;
    }

    /// <summary>
    /// The product of the transmission of the surfaces the segment from <paramref name="from"/> to <paramref name="to"/>
    /// crosses (all ones when nothing is in the way): once per solid instance, once per crossing of an open one.
    /// </summary>
    public Bands3 Transmission(Vector3 from, Vector3 to)
    {
        var result = Bands3.All(1);
        if (_a.Length == 0) return result;
        var direction = to - from;
        var inv = new Vector3(1 / direction.X, 1 / direction.Y, 1 / direction.Z);
        Span<int> stack = stackalloc int[64];
        Span<int> solids = stackalloc int[32]; // solid instances already counted on this segment
        Span<int> openInstance = stackalloc int[32];
        Span<float> openDistance = stackalloc float[32]; // open crossings counted, to merge hits on a shared edge
        int solidCount = 0, openCount = 0, sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            ref var node = ref _nodes[stack[--sp]];
            if (!Slab(from, inv, node.Min, node.Max, 1)) continue;
            if (node.Count > 0)
            {
                for (int i = node.Left; i < node.Left + node.Count; i++)
                {
                    int t = _order[i];
                    float d = Intersect(t, from, direction);
                    if (d <= 1e-5f || d >= 1 - 1e-5f) continue;
                    int instance = _instances[t];
                    if (_solid[instance])
                    {
                        if (solids[..solidCount].Contains(instance)) continue;
                        if (solidCount < solids.Length) solids[solidCount++] = instance;
                    }
                    else
                    {
                        // A segment through an edge or vertex hits every triangle sharing it: one crossing.
                        bool seen = false;
                        for (int k = 0; k < openCount && !seen; k++) seen = openInstance[k] == instance && MathF.Abs(openDistance[k] - d) < 1e-5f;
                        if (seen) continue;
                        if (openCount < openInstance.Length) { openInstance[openCount] = instance; openDistance[openCount] = d; openCount++; }
                    }
                    result = result * _palette[_materials[t]].Transmission;
                }
            }
            else if (sp + 2 <= stack.Length) { stack[sp++] = node.Left; stack[sp++] = node.Left + 1; }
        }
        return result;
    }

    /// <summary>
    /// How much of a source the listener hears directly, per band: rays to the source's centre and to points spread
    /// over its radius (a Fibonacci sphere), each carrying the transmission of the surfaces it crosses, averaged. A
    /// source half hidden behind a pillar is half occluded; one behind a wall comes through at the wall's transmission.
    /// </summary>
    public Bands3 DirectPath(Vector3 listener, Vector3 source, float radius, int samples)
    {
        if (_a.Length == 0) return Bands3.All(1);
        var sum = Transmission(listener, source);
        int n = radius > 0 ? Math.Max(0, samples) : 0;
        float golden = MathF.PI * (3 - MathF.Sqrt(5));
        for (int i = 0; i < n; i++)
        {
            float y = 1 - 2 * (i + 0.5f) / n, r = MathF.Sqrt(1 - y * y), phi = golden * i;
            var point = source + radius * new Vector3(MathF.Cos(phi) * r, y, MathF.Sin(phi) * r);
            sum = sum + Transmission(listener, point);
        }
        return sum * (1f / (n + 1));
    }

    /// <summary>Whether anything blocks the segment (an early-out visibility test).</summary>
    public bool Blocked(Vector3 from, Vector3 to) => Raycast(from, to - from, 1 - 1e-4f, out _);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Slab(Vector3 origin, Vector3 inv, Vector3 min, Vector3 max, float maxT)
    {
        var t0 = (min - origin) * inv; var t1 = (max - origin) * inv;
        var tmin = Vector3.Min(t0, t1); var tmax = Vector3.Max(t0, t1);
        float enter = MathF.Max(MathF.Max(tmin.X, tmin.Y), MathF.Max(tmin.Z, 0));
        float exit = MathF.Min(MathF.Min(tmax.X, tmax.Y), MathF.Min(tmax.Z, maxT));
        return enter <= exit;
    }

    /// <summary>Möller–Trumbore: the ray parameter of the hit, or −1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float Intersect(int t, Vector3 origin, Vector3 direction)
    {
        var e1 = _e1[t]; var e2 = _e2[t];
        var p = Vector3.Cross(direction, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-12f) return -1;
        float invDet = 1 / det;
        var s = origin - _a[t];
        float u = Vector3.Dot(s, p) * invDet;
        if (u < 0 || u > 1) return -1;
        var q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(direction, q) * invDet;
        if (v < 0 || u + v > 1) return -1;
        return Vector3.Dot(e2, q) * invDet;
    }
}

/// <summary>Collects boxes and meshes with their materials into an <see cref="AcousticScene"/>.</summary>
public sealed class AcousticSceneBuilder
{
    private readonly List<Vector3> _vertices = [];
    private readonly List<int> _materials = [], _instances = [];
    private readonly List<bool> _solid = [];
    private readonly List<AcousticMaterial> _palette = [];

    public void Clear() { _vertices.Clear(); _materials.Clear(); _instances.Clear(); _solid.Clear(); _palette.Clear(); }

    /// <summary>A solid box: the unit cube from −0.5 to 0.5 through <paramref name="transform"/>, twelve triangles with outward normals.</summary>
    public void AddBox(Matrix4x4 transform, in AcousticMaterial material)
    {
        int m = Material(material), instance = _solid.Count;
        _solid.Add(true);
        Span<Vector3> c = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++) c[i] = Vector3.Transform(new Vector3((i & 1) - 0.5f, ((i >> 1) & 1) - 0.5f, ((i >> 2) & 1) - 0.5f), transform);
        // Faces as corner indices, counter-clockwise seen from outside.
        ReadOnlySpan<int> faces = [0, 2, 3, 1, 4, 5, 7, 6, 0, 1, 5, 4, 2, 6, 7, 3, 0, 4, 6, 2, 1, 3, 7, 5];
        for (int f = 0; f < 6; f++)
        {
            int a = faces[4 * f], b = faces[4 * f + 1], cc = faces[4 * f + 2], d = faces[4 * f + 3];
            _vertices.Add(c[a]); _vertices.Add(c[b]); _vertices.Add(c[cc]);
            _vertices.Add(c[a]); _vertices.Add(c[cc]); _vertices.Add(c[d]);
            _materials.Add(m); _materials.Add(m); _instances.Add(instance); _instances.Add(instance);
        }
    }

    /// <summary>An open mesh: triangles (three vertices each, in its own space) through <paramref name="transform"/>.</summary>
    public void AddMesh(ReadOnlySpan<Vector3> triangles, Matrix4x4 transform, in AcousticMaterial material)
    {
        int m = Material(material), instance = _solid.Count;
        _solid.Add(false);
        foreach (var v in triangles) _vertices.Add(Vector3.Transform(v, transform));
        for (int t = 0; t < triangles.Length / 3; t++) { _materials.Add(m); _instances.Add(instance); }
    }

    public AcousticScene Build() => new(CollectionsMarshal.AsSpan(_vertices), CollectionsMarshal.AsSpan(_materials), CollectionsMarshal.AsSpan(_instances), _palette.ToArray(), _solid.ToArray());

    private int Material(in AcousticMaterial material)
    {
        for (int i = 0; i < _palette.Count; i++) if (_palette[i].Same(material)) return i;
        _palette.Add(material);
        return _palette.Count - 1;
    }
}
