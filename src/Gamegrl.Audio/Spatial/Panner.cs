using System.Numerics;

namespace Gamegrl.Audio.Spatial;

/// <summary>
/// Directions in listener space: +X right, +Y up, −Z forward (the camera's convention). Azimuth is measured from straight
/// ahead, positive to the left (front left speakers sit at +30°); elevation is positive upward.
/// </summary>
public static class Directions
{
    public static Vector3 FromAngles(float azimuthDegrees, float elevationDegrees)
    {
        float az = float.DegreesToRadians(azimuthDegrees), el = float.DegreesToRadians(elevationDegrees);
        return new Vector3(-MathF.Sin(az) * MathF.Cos(el), MathF.Sin(el), -MathF.Cos(az) * MathF.Cos(el));
    }

    public static float Azimuth(Vector3 d) => float.RadiansToDegrees(MathF.Atan2(-d.X, -d.Z));
    public static float Elevation(Vector3 d) => float.RadiansToDegrees(MathF.Asin(Math.Clamp(d.Y / MathF.Max(d.Length(), 1e-20f), -1, 1)));

    /// <summary>
    /// Where a speaker sits in a layout (ITU-R BS.775 and Dolby placements): fronts ±30°, centre 0°, 5.1 surrounds ±110°,
    /// 7.1 sides ±90° and backs ±150°, quad corners ±45° and ±135°, heights at 45° elevation above ±45° and ±135°.
    /// </summary>
    public static (float Azimuth, float Elevation) Speaker(ChannelLayout layout, Audio.Speaker s)
    {
        bool quad = layout == ChannelLayout.Quad;
        bool sides = layout.Has(Audio.Speaker.SideLeft) || layout.Has(Audio.Speaker.SideRight);
        return s switch
        {
            Audio.Speaker.FrontLeft => (quad ? 45 : 30, 0),
            Audio.Speaker.FrontRight => (quad ? -45 : -30, 0),
            Audio.Speaker.FrontCenter or Audio.Speaker.Lfe => (0, 0),
            Audio.Speaker.BackLeft => (quad ? 135 : sides ? 150 : 110, 0),
            Audio.Speaker.BackRight => (quad ? -135 : sides ? -150 : -110, 0),
            Audio.Speaker.FrontLeftCenter => (15, 0),
            Audio.Speaker.FrontRightCenter => (-15, 0),
            Audio.Speaker.BackCenter => (180, 0),
            Audio.Speaker.SideLeft => (90, 0),
            Audio.Speaker.SideRight => (-90, 0),
            Audio.Speaker.TopCenter => (0, 90),
            Audio.Speaker.TopFrontLeft => (45, 45),
            Audio.Speaker.TopFrontCenter => (0, 45),
            Audio.Speaker.TopFrontRight => (-45, 45),
            Audio.Speaker.TopBackLeft => (135, 45),
            Audio.Speaker.TopBackCenter => (180, 45),
            Audio.Speaker.TopBackRight => (-135, 45),
            _ => (0, 0),
        };
    }

    public static Vector3 SpeakerVector(ChannelLayout layout, Audio.Speaker s)
    {
        var (az, el) = Speaker(layout, s);
        return FromAngles(az, el);
    }
}

/// <summary>
/// Vector-base amplitude panning (Pulkki 1997) onto a speaker layout: a source between two speakers (a ring) or three (a
/// dome) plays from those, with gains solving <c>source = Σ gᵢ · speakerᵢ</c>, normalised to constant power. Layouts
/// with height speakers use 3D VBAP over the speakers' convex hull, with a virtual speaker straight down (its share
/// folds into its neighbours) so every direction is covered. A ring with a gap wider than 180° (stereo, front-only
/// layouts) mirrors sources from behind into the front and clamps sources past its edges to the edge speaker. LFE
/// never takes a share. <c>spread</c> blends toward equal power on every speaker (a large or very near source).
/// </summary>
public sealed class Panner
{
    private readonly ChannelLayout _layout;
    private readonly int[] _speakers;          // bed channels that take part (all but LFE)
    private readonly Vector3[] _vectors;       // their directions
    // 2D: speakers in azimuth order and the pairs between neighbours.
    private readonly bool _threeD;
    private readonly int[] _ring = [];
    private readonly float[] _ringAzimuth = [];
    private readonly int _gapPair = -1;        // index into _ring of the pair spanning a gap > 180°, or −1
    // 3D: hull triangles over _vectors plus virtual speakers (indices from _vectors.Length: the nadir, and the zenith
    // when no speaker is near it), with inverse matrices; each virtual speaker's real neighbours share its power.
    private readonly (int A, int B, int C, Matrix4x4 Inverse)[] _triangles = [];
    private readonly int[][] _virtualNeighbours = [];

    public ChannelLayout Layout => _layout;

    public Panner(ChannelLayout layout)
    {
        _layout = layout;
        var speakers = new List<int>();
        for (int c = 0; c < layout.Count; c++) if (layout.Speakers[c] != Speaker.Lfe) speakers.Add(c);
        _speakers = speakers.ToArray();
        _vectors = new Vector3[_speakers.Length];
        bool heights = false;
        for (int i = 0; i < _speakers.Length; i++)
        {
            var (_, el) = Directions.Speaker(layout, layout.Speakers[_speakers[i]]);
            heights |= el > 1;
            _vectors[i] = Directions.SpeakerVector(layout, layout.Speakers[_speakers[i]]);
        }
        _threeD = heights && _speakers.Length >= 4;
        if (_threeD) (_triangles, _virtualNeighbours) = Triangulate(_vectors);
        else if (_speakers.Length >= 2)
        {
            var order = Enumerable.Range(0, _speakers.Length).Where(i => Directions.Speaker(layout, layout.Speakers[_speakers[i]]).Elevation <= 1)
                .OrderBy(i => Normalise(Directions.Speaker(layout, layout.Speakers[_speakers[i]]).Azimuth)).ToArray();
            _ring = order;
            _ringAzimuth = order.Select(i => Normalise(Directions.Speaker(layout, layout.Speakers[_speakers[i]]).Azimuth)).ToArray();
            for (int k = 0; k < _ring.Length; k++)
            {
                float arc = Arc(k);
                if (arc > 180) _gapPair = k;
            }
        }
    }

    private static float Normalise(float az) => ((az % 360) + 360) % 360;
    private float Arc(int k) => k + 1 < _ringAzimuth.Length ? _ringAzimuth[k + 1] - _ringAzimuth[k] : _ringAzimuth[0] + 360 - _ringAzimuth[k];

    /// <summary>Gains for every bed channel (LFE gets 0) for a unit direction in listener space.</summary>
    public void Pan(Vector3 direction, float spread, Span<float> gains)
    {
        gains.Clear();
        int n = _speakers.Length;
        if (n == 0) return;
        if (n == 1) { gains[_speakers[0]] = 1; return; }
        Span<float> g = stackalloc float[n];
        g.Clear();
        float len = direction.Length();
        if (len < 1e-6f) spread = 1;
        else if (_threeD) Pan3D(direction / len, g);
        else Pan2D(direction / len, g, ref spread);

        float power = 0;
        for (int i = 0; i < n; i++) power += g[i] * g[i];
        if (power > 0) { float s = 1 / MathF.Sqrt(power); for (int i = 0; i < n; i++) g[i] *= s; }
        spread = Math.Clamp(spread, 0, 1);
        for (int i = 0; i < n; i++) gains[_speakers[i]] = MathF.Sqrt((1 - spread) * g[i] * g[i] + spread / n);
    }

    private void Pan2D(Vector3 d, Span<float> g, ref float spread)
    {
        var flat = new Vector2(d.X, d.Z);
        float horizontal = flat.Length();
        // Straight up or down on a flat layout: no horizontal direction, so spread over everything.
        if (horizontal < 1e-3f) { spread = 1; return; }
        if (_ring.Length < 2) { g[_ring.Length == 1 ? _ring[0] : 0] = 1; return; }
        float az = Normalise(Directions.Azimuth(d));
        int pair = -1;
        for (int k = 0; k < _ring.Length; k++)
        {
            float start = _ringAzimuth[k], into = Normalise(az - start);
            if (into <= Arc(k)) { pair = k; break; }
        }
        if (pair == _gapPair)
        {
            // Behind a gap (stereo's back): mirror across the front-back axis of the speakers next to it, then clamp.
            int a = _ring[(pair + 1) % _ring.Length], b = _ring[pair];
            var bisector = Vector3.Normalize(_vectors[a] + _vectors[b]);
            var mirrored = d - 2 * Vector3.Dot(d, bisector) * bisector * (Vector3.Dot(d, bisector) < 0 ? 1 : 0);
            float maz = Normalise(Directions.Azimuth(mirrored));
            // The pair across the front from the gap: (_ring[pair+1] … _ring[pair]) going the short way.
            float lo = _ringAzimuth[(pair + 1) % _ring.Length], span = Normalise(_ringAzimuth[pair] - lo);
            float into = Normalise(maz - lo);
            if (into > span) { g[into - span < 360 - into ? b : a] = 1; return; }
            // Within the front: find its neighbouring pair inside the short way round and pan between them.
            for (int k = 0; k < _ring.Length; k++)
            {
                if (k == _gapPair) continue;
                float start = _ringAzimuth[k];
                if (Normalise(maz - start) <= Arc(k)) { PanPair(_ring[k], _ring[(k + 1) % _ring.Length], mirrored, g); return; }
            }
            return;
        }
        PanPair(_ring[pair], _ring[(pair + 1) % _ring.Length], d, g);
    }

    private void PanPair(int i, int j, Vector3 d, Span<float> g)
    {
        // Solve d = gi·vi + gj·vj in the horizontal plane.
        Vector2 vi = new(_vectors[i].X, _vectors[i].Z), vj = new(_vectors[j].X, _vectors[j].Z), p = Vector2.Normalize(new Vector2(d.X, d.Z));
        float det = vi.X * vj.Y - vj.X * vi.Y;
        if (MathF.Abs(det) < 1e-6f) { g[i] = 1; return; }
        float gi = (p.X * vj.Y - vj.X * p.Y) / det, gj = (vi.X * p.Y - p.X * vi.Y) / det;
        g[i] = MathF.Max(0, gi); g[j] = MathF.Max(0, gj);
    }

    private void Pan3D(Vector3 d, Span<float> g)
    {
        int real = _vectors.Length;
        Span<float> power = stackalloc float[real];
        foreach (var (a, b, c, inv) in _triangles)
        {
            var w = Vector3.Transform(d, inv);
            if (w.X < -1e-5f || w.Y < -1e-5f || w.Z < -1e-5f) continue;
            power.Clear();
            void Give(int speaker, float gain, Span<float> power)
            {
                float p = MathF.Max(0, gain);
                p *= p;
                if (speaker < real) { power[speaker] += p; return; }
                var neighbours = _virtualNeighbours[speaker - real];
                foreach (int n in neighbours) power[n] += p / neighbours.Length;
            }
            Give(a, w.X, power); Give(b, w.Y, power); Give(c, w.Z, power);
            for (int i = 0; i < real; i++) g[i] = MathF.Sqrt(power[i]);
            return;
        }
    }

    /// <summary>
    /// The convex hull's faces over the speakers plus virtual speakers at the nadir (always) and the zenith (when no
    /// speaker is within 30° of it), each face with the inverse of its speaker matrix; and each virtual speaker's real
    /// neighbours. Brute force: there are only a handful of speakers.
    /// </summary>
    private static ((int, int, int, Matrix4x4)[], int[][]) Triangulate(Vector3[] speakers)
    {
        var points = new List<Vector3>(speakers) { new(0, -1, 0) };
        bool zenithCovered = speakers.Any(v => v.Y > MathF.Cos(MathF.PI / 6));
        if (!zenithCovered) points.Add(new Vector3(0, 1, 0));
        int n = points.Count, real = speakers.Length;
        var faces = new List<(int, int, int, Matrix4x4)>();
        var neighbours = new HashSet<int>[n - real];
        for (int v = 0; v < neighbours.Length; v++) neighbours[v] = [];
        for (int i = 0; i < n; i++)
        for (int j = i + 1; j < n; j++)
        for (int k = j + 1; k < n; k++)
        {
            var normal = Vector3.Cross(points[j] - points[i], points[k] - points[i]);
            if (normal.LengthSquared() < 1e-8f) continue;
            // A face: every other point on one side (none strictly on the other).
            bool front = false, back = false;
            for (int m = 0; m < n && !(front && back); m++)
            {
                if (m == i || m == j || m == k) continue;
                float side = Vector3.Dot(normal, points[m] - points[i]);
                if (side > 1e-5f) front = true; else if (side < -1e-5f) back = true;
            }
            if (front && back) continue;
            // Rows of the matrix are the speakers: w = d · M⁻¹ gives the three gains.
            var m3 = new Matrix4x4(points[i].X, points[i].Y, points[i].Z, 0, points[j].X, points[j].Y, points[j].Z, 0, points[k].X, points[k].Y, points[k].Z, 0, 0, 0, 0, 1);
            if (!Matrix4x4.Invert(m3, out var inverse)) continue;
            faces.Add((i, j, k, inverse));
            foreach (int v in (ReadOnlySpan<int>)[i, j, k])
            {
                if (v < real) continue;
                foreach (int other in (ReadOnlySpan<int>)[i, j, k]) if (other < real) neighbours[v - real].Add(other);
            }
        }
        return (faces.ToArray(), neighbours.Select(h => h.Order().ToArray()).ToArray());
    }
}
