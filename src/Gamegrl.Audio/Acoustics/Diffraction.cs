using System.Numerics;
using Gamegrl.Audio.Spatial;

namespace Gamegrl.Audio.Acoustics;

/// <summary>
/// A path that bends around one or two edges from a hidden source to the listener: the edge points (nearest the listener
/// first), its length, the detour over the straight line, and its per-band amplitude from the barrier attenuation.
/// </summary>
public struct DiffractionPath
{
    public int Edges;
    public Vector3 First, Second;
    public float Length, Detour;
    public Bands3 Gain;
}

/// <summary>
/// Sound bending around corners, door frames and wall tops. When the straight line from the listener to a source is
/// blocked, the shortest detour around one edge whose legs are both clear is the diffraction path, or failing that
/// around two. Its attenuation is ISO 9613-2's barrier formula, per band:
/// <c>Dz = 10·lg(3 + (C₂/λ)·C₃·z)</c> with z the path difference, C₂ = 40 (ground reflections are separate image
/// sources), C₃ = 1 for one edge and <c>(1 + (5λ/e)²) / (1/3 + (5λ/e)²)</c> for two edges e apart, capped at 20 and 25 dB.
/// Long wavelengths bend easily, so a corner muffles rather than silences.
/// </summary>
public static class Diffraction
{
    public const float SingleCapDb = 20, DoubleCapDb = 25;

    /// <summary>ISO 9613-2 barrier attenuation in dB for path difference <paramref name="z"/> at a wavelength.</summary>
    public static float BarrierDb(float z, float wavelength, float c3 = 1, float capDb = SingleCapDb) =>
        MathF.Min(capDb, 10 * MathF.Log10(3 + 40 / wavelength * c3 * MathF.Max(0, z)));

    /// <summary>ISO 9613-2's C₃ for double diffraction over two edges <paramref name="separation"/> apart.</summary>
    public static float DoubleC3(float separation, float wavelength)
    {
        float r = 5 * wavelength / MathF.Max(separation, 1e-3f);
        return (1 + r * r) / (1f / 3 + r * r);
    }

    /// <summary>The diffraction path from a hidden source, if there is one (false too when the source is in plain view).</summary>
    public static bool Find(AcousticScene scene, Vector3 listener, Vector3 source, out DiffractionPath path, float speedOfSound = 343, int candidates = 24)
    {
        path = default;
        var edges = scene.Edges;
        if (edges.Length == 0 || !scene.Blocked(listener, source)) return false;
        float direct = Vector3.Distance(listener, source);

        // Every edge's shortest detour point, ranked by length (on the simulation thread, when things have moved).
        var lengths = new float[edges.Length];
        var order = new int[edges.Length];
        var ts = new float[edges.Length];
        for (int e = 0; e < edges.Length; e++)
        {
            ref readonly var edge = ref edges[e];
            float t = Minimise(edge.A, edge.B, listener, source);
            var p = Vector3.Lerp(edge.A, edge.B, t);
            lengths[e] = Vector3.Distance(listener, p) + Vector3.Distance(p, source);
            ts[e] = t;
            order[e] = e;
        }
        Array.Sort((float[])lengths.Clone(), order);
        int kept = Math.Min(candidates, edges.Length);
        Span<float> bestLength = stackalloc float[kept];
        Span<int> bestEdge = stackalloc int[kept];
        Span<float> bestT = stackalloc float[kept];
        for (int i = 0; i < kept; i++) { bestEdge[i] = order[i]; bestLength[i] = lengths[order[i]]; bestT[i] = ts[order[i]]; }

        // Paths compete on what reaches the listener: the mid band's amplitude over the path's length.
        float bestScore = 0;
        bool found = false;
        void Offer(in DiffractionPath candidate, ref DiffractionPath path)
        {
            float score = candidate.Gain.Mid / candidate.Length;
            if (score > bestScore) { bestScore = score; path = candidate; found = true; }
        }

        // One edge: the shortest detour with both legs clear.
        for (int i = 0; i < kept; i++)
        {
            ref readonly var edge = ref edges[bestEdge[i]];
            var p = Vector3.Lerp(edge.A, edge.B, bestT[i]);
            if (!Clear(scene, listener, p) || !Clear(scene, p, source)) continue;
            var single = new DiffractionPath { Edges = 1, First = p, Second = p, Length = bestLength[i], Detour = bestLength[i] - direct };
            single.Gain = Gains(single.Detour, 0, single: true, speedOfSound);
            Offer(single, ref path);
            break;
        }

        // Two edges (round a thick wall or a door frame, through a door and round a corner): the clear pair that
        // delivers most, which can beat a longer single-edge path.
        float best = float.MaxValue;
        int pairs = Math.Min(kept, 12);
        for (int i = 0; i < pairs; i++)
        for (int j = 0; j < pairs; j++)
        {
            if (i == j) continue;
            ref readonly var near = ref edges[bestEdge[i]]; // nearest the listener
            ref readonly var far = ref edges[bestEdge[j]];
            float t1 = bestT[i], t2 = bestT[j];
            // Coordinate descent: each edge point is the best for the fixed other one; a few rounds converge.
            for (int round = 0; round < 8; round++)
            {
                t1 = Minimise(near.A, near.B, listener, Vector3.Lerp(far.A, far.B, t2));
                t2 = Minimise(far.A, far.B, Vector3.Lerp(near.A, near.B, t1), source);
            }
            var p1 = Vector3.Lerp(near.A, near.B, t1); var p2 = Vector3.Lerp(far.A, far.B, t2);
            float length = Vector3.Distance(listener, p1) + Vector3.Distance(p1, p2) + Vector3.Distance(p2, source);
            if (length >= best || !Clear(scene, listener, p1) || !Clear(scene, p1, p2) || !Clear(scene, p2, source)) continue;
            best = length;
            var pair = new DiffractionPath { Edges = 2, First = p1, Second = p2, Length = length, Detour = length - direct };
            pair.Gain = Gains(pair.Detour, Vector3.Distance(p1, p2), single: false, speedOfSound);
            Offer(pair, ref path);
        }
        return found;
    }

    private static Bands3 Gains(float detour, float separation, bool single, float c)
    {
        float G(float frequency)
        {
            float wavelength = c / frequency;
            float db = single ? BarrierDb(detour, wavelength) : BarrierDb(detour, wavelength, DoubleC3(separation, wavelength), DoubleCapDb);
            return MathF.Pow(10, -db / 20);
        }
        return new Bands3(G(Bands.LowCenter), G(Bands.MidCenter), G(Bands.HighCenter));
    }

    /// <summary>The point on segment AB minimising |L − p| + |p − S| (convex in the parameter, so golden-section search finds it).</summary>
    private static float Minimise(Vector3 a, Vector3 b, Vector3 l, Vector3 s)
    {
        const float ratio = 0.61803399f;
        float lo = 0, hi = 1;
        float F(float t) { var p = Vector3.Lerp(a, b, t); return Vector3.Distance(l, p) + Vector3.Distance(p, s); }
        float x1 = hi - ratio * (hi - lo), x2 = lo + ratio * (hi - lo), f1 = F(x1), f2 = F(x2);
        for (int i = 0; i < 28; i++)
        {
            if (f1 < f2) { hi = x2; x2 = x1; f2 = f1; x1 = hi - ratio * (hi - lo); f1 = F(x1); }
            else { lo = x1; x1 = x2; f1 = f2; x2 = lo + ratio * (hi - lo); f2 = F(x2); }
        }
        return 0.5f * (lo + hi);
    }

    /// <summary>
    /// Nothing between two points, ignoring the surfaces they sit on (each end is pulled in by a millimetre), and not a
    /// crevice: a leg running between solids flush against each other (a wall box standing on a floor box) grazes both
    /// without hitting either, so it's rejected when its middle has solid on opposite sides. (A leg across the top of a
    /// barrier, or along a door jamb, has solid on one side only. Hollow rooms are <see cref="AcousticShape.Room"/>,
    /// whose inside isn't solid.)
    /// </summary>
    private static bool Clear(AcousticScene scene, Vector3 a, Vector3 b)
    {
        var d = b - a;
        float length = d.Length();
        if (length < 2e-3f) return true;
        float pull = 1e-3f / length;
        if (scene.Raycast(a + d * pull, d * (1 - 2 * pull), 1, out _)) return false;
        var mid = 0.5f * (a + b);
        var axis = d / length;
        var u = Vector3.Normalize(Vector3.Cross(axis, MathF.Abs(axis.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        var v = Vector3.Cross(axis, u);
        const float probe = 3e-3f;
        if (scene.InsideSolid(mid + u * probe) && scene.InsideSolid(mid - u * probe)) return false;
        return !(scene.InsideSolid(mid + v * probe) && scene.InsideSolid(mid - v * probe));
    }
}
