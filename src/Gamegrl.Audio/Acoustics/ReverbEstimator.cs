using System.Numerics;
using Gamegrl.Audio.Spatial;

namespace Gamegrl.Audio.Acoustics;

/// <summary>
/// The late reverberation at a point, as the reverb renders it: decay time per band, the reverberant level relative to
/// the direct sound at 1 m, a pre-delay, and what the estimate saw (the late decay, volume, surface, how enclosed the
/// point is).
/// </summary>
public struct ReverbParams
{
    /// <summary>
    /// The decay the reverb renders, per band: the early decay time (EDT, the first 10 dB, times six), which is what
    /// listeners hear as reverberance (ISO 3382-1). In a closed room it equals <see cref="LateDecaySeconds"/>; where most
    /// energy escapes early (outside a cave, at a cliff) it's far shorter, and the faint late tail isn't rendered.
    /// </summary>
    public Bands3 DecaySeconds;
    /// <summary>The late decay time per band (T30: −5 to −35 dB, extrapolated), what Sabine and Eyring predict. Informational.</summary>
    public Bands3 LateDecaySeconds;
    /// <summary>
    /// Reverberant amplitude relative to a source's direct sound at 1 m: the square root of the diffuse and late energy
    /// that comes back to the listener (√(16π ÷ R) for room constant R in a diffuse room).
    /// </summary>
    public float Level;
    public float PreDelaySeconds;
    public float Volume, Surface, Enclosure;

    public static ReverbParams None => new() { DecaySeconds = Bands3.All(0) };
    public readonly bool IsSilent => Level <= 0 || DecaySeconds.Mid <= 0;
}

/// <summary>
/// Estimates the reverberation heard at a listener by tracing rays from it (by reciprocity, the same as from sources
/// around it). Each ray carries energy per band and bounces off the scene: a material's scattering fraction of
/// reflections go in a random cosine-weighted direction, the rest mirror; each bounce keeps (1 − absorption) of the
/// energy and air absorption takes its share along the way. Rays that escape take their energy out.
/// <list type="bullet">
/// <item>Heard: energy that comes back to the listener, against a source's direct fluence at 1 m. At each scattering
/// bounce the scattered share goes straight to the listener when it can see the spot (next-event estimation, "diffuse
/// rain": every bounce contributes a little, rather than a few lucky rays a lot); specular legs are collected by a
/// receiver sphere round the listener (radius scaled to the space, 0.5 to 4 m, and at most half the way to the nearest
/// surface), each crossing adding its energy times chord ÷ sphere volume, the fluence it carries. All-specular paths
/// of the first two orders are left out (the image sources render those as discrete reflections), and so is the first
/// order (it's local to where you stand, strong beside a wall, and arrives
/// with the early sound): this is the late sound. Its total, divided by what a first bounce keeps on average, is the
/// reverb's energy relative to the direct sound at 1 m (the level squared: 16π/R in a diffuse room of room constant R),
/// and its backward-integrated
/// decay (Schroeder's) gives the early decay time the reverb renders: the first 10 dB, times six. Out in the open
/// little comes back and it dies at once, however long faint energy wanders in some cave nearby.</item>
/// <item>In the room: the total energy still travelling, over time, is the room's energy decay; its slope between −5
/// and −35 dB is the late reverberation time T30 per band (by construction what Eyring's formula approximates).</item>
/// <item>Size: first-hit distances give the volume visible from the point, V = (4π/N)·Σ r³/3; the mean free path
/// l = 4V/S then gives the surface S. The fraction of first rays that hit something is the enclosure.</item>
/// </list>
/// Deterministic for given inputs (a fixed random seed).
/// </summary>
public static class ReverbEstimator
{
    public const float MaxSeconds = 4;
    private const float BinSeconds = 0.005f;

    public static ReverbParams Estimate(AcousticScene scene, Vector3 listener, int rays = 2048, int bounces = 64,
        float temperatureC = 20, float humidityPercent = 50, bool airAbsorption = true, int seed = 1, ReverbTrace? trace = null)
    {
        if (scene.TriangleCount == 0 || rays <= 0) return ReverbParams.None;
        float c = Propagation.SpeedOfSound(temperatureC);
        int bins = (int)(MaxSeconds / BinSeconds);
        Span<double> energy = new double[bins * 3]; // per band: energy alive during each bin, summed over rays
        Span<double> heard = new double[bins * 3];  // per band: fluence reaching the receiver in each bin, re direct at 1 m
        var air = airAbsorption
            ? new Bands3((float)Propagation.AirAbsorptionDbPerMetre(Bands.LowCenter, temperatureC, humidityPercent),
                (float)Propagation.AirAbsorptionDbPerMetre(Bands.MidCenter, temperatureC, humidityPercent),
                (float)Propagation.AirAbsorptionDbPerMetre(Bands.HighCenter, temperatureC, humidityPercent))
            : default;
        uint state = (uint)seed * 2654435761u | 1;
        float Random() { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return (state >> 8) * (1f / 16777216f); }
        float golden = MathF.PI * (3 - MathF.Sqrt(5));
        Vector3 Direction(int r, int n)
        {
            float y = 1 - 2 * (r + 0.5f) / n, rad = MathF.Sqrt(1 - y * y), phi = golden * r;
            return new Vector3(MathF.Cos(phi) * rad, y, MathF.Sin(phi) * rad);
        }

        // The receiver: big enough to be crossed often (a few times per ray in a big room), small next to the space.
        // It stays within half the distance to the nearest surface, so it doesn't soak up one wall's return.
        double sizing = 0;
        float nearest = float.MaxValue;
        const int SizingRays = 256;
        for (int r = 0; r < SizingRays; r++)
            if (scene.Raycast(listener, Direction(r, SizingRays), 200, out var first))
            {
                sizing += first.Distance * first.Distance * first.Distance / 3;
                nearest = MathF.Min(nearest, first.Distance);
            }
        float receiver = MathF.Min(Math.Clamp(0.15f * MathF.Cbrt((float)(4 * Math.PI / SizingRays * sizing)), 0.5f, 4), MathF.Max(0.2f, 0.5f * nearest));
        float r2 = receiver * receiver, fluence = 3 / (receiver * r2); // chord ÷ (4/3·π·R³), times 4π (direct fluence at 1 m is 1/4π)

        double volume = 0, pathSum = 0, firstKept = 0;
        int firstHits = 0, segments = 0;
        float maxPath = MaxSeconds * c;
        int traceEvery = trace is null ? 0 : Math.Max(1, rays / ReverbTrace.Rays);
        for (int r = 0; r < rays; r++)
        {
            var dir = Direction(r, rays);
            var origin = listener;
            var e = Bands3.All(1f / rays);
            float travelled = 0;
            bool traced = traceEvery > 0 && r % traceEvery == 0, scattered = false, specularLeg = true;
            for (int b = 0; b < bounces; b++)
            {
                bool escaped = !scene.Raycast(origin, dir, maxPath - travelled, out var hit);
                float length = escaped ? maxPath - travelled : hit.Distance;
                if (traced && b < ReverbTrace.Bounces)
                    trace!.Segments.Add(new ReverbTrace.Segment { A = origin, B = origin + dir * (escaped ? ReverbTrace.EscapeLength : length), Bounce = b, Escaped = escaped });
                // A specular leg back past the listener: heard from the second order on (the first is local to where you
                // stand, strong beside a wall, and arrives with the early sound), except all-specular paths of the first
                // two orders (image sources' job). Scattered legs are counted at the bounce instead (below).
                if (b > 1 && specularLeg && (scattered || b > 2)) Receive(heard, origin - listener, dir, length, travelled, e, air, c, r2, fluence, bins);
                if (escaped) break; // through an opening (or out of time): its energy leaves the room
                if (b == 0) { firstHits++; volume += hit.Distance * hit.Distance * hit.Distance / 3; }
                else { pathSum += hit.Distance; segments++; }
                Deposit(energy, travelled / c, (travelled + hit.Distance) / c, e, air, c, bins);
                // Air absorption over the segment, then the surface.
                if (airAbsorption) e = e * new Bands3(Pow10(-air.Low * hit.Distance / 10), Pow10(-air.Mid * hit.Distance / 10), Pow10(-air.High * hit.Distance / 10));
                travelled += hit.Distance;
                var material = scene.MaterialOf(hit.Triangle);
                if (b == 0) firstKept += 1 - material.Absorption.Mid;
                e = e * new Bands3(1 - material.Absorption.Low, 1 - material.Absorption.Mid, 1 - material.Absorption.High);
                if (e.Low + e.Mid + e.High < 1e-9f / rays) break;
                var normal = Vector3.Dot(hit.Normal, dir) > 0 ? -hit.Normal : hit.Normal; // facing the ray
                origin = origin + dir * hit.Distance + normal * 1e-4f;
                // The scattered share straight to the listener, if it can see the spot (from the second order on).
                if (b >= 1 && material.Scattering > 0) Rain(scene, heard, origin, normal, listener, travelled, e * material.Scattering, air, c, r2, bins);
                if (Random() < material.Scattering) { dir = CosineDirection(normal, Random(), Random()); scattered = true; specularLeg = false; }
                else { dir = Vector3.Reflect(dir, normal); specularLeg = true; }
            }
        }

        var result = new ReverbParams { Enclosure = (float)firstHits / rays };
        result.Volume = (float)(4 * Math.PI / rays * volume);
        float meanFreePath = segments > 0 ? (float)(pathSum / segments) : 0;
        result.Surface = meanFreePath > 0 ? 4 * result.Volume / meanFreePath : 0;
        result.LateDecaySeconds = new Bands3(Decay(energy, 0, bins), Decay(energy, 1, bins), Decay(energy, 2, bins));
        // What's heard, backward-integrated: the energy still to arrive after each moment.
        for (int band = 0; band < 3; band++)
        {
            var h = heard.Slice(band * bins, bins);
            for (int b = bins - 2; b >= 0; b--) h[b] += h[b + 1];
        }
        result.DecaySeconds = new Bands3(EarlyDecay(heard, 0, bins), EarlyDecay(heard, 1, bins), EarlyDecay(heard, 2, bins));
        // The first bounce's share, put back at the average a first bounce keeps (rather than what the nearest wall returns).
        double reflectance = firstHits > 0 ? firstKept / firstHits : 1;
        result.Level = result.DecaySeconds.Mid > 0 ? (float)Math.Sqrt(heard[bins] / Math.Max(reflectance, 0.05)) : 0;
        result.PreDelaySeconds = firstHits > 0 ? Math.Min(0.1f, meanFreePath / c) : 0;
        if (trace is not null)
        {
            trace.Listener = listener;
            trace.Receiver = receiver;
            trace.Decay = new float[bins * 3];
            for (int band = 0; band < 3; band++)
            {
                double start = heard[band * bins];
                for (int b = 0; b < bins; b++)
                    trace.Decay[band * bins + b] = start > 0 && heard[band * bins + b] > 0 ? (float)(10 * Math.Log10(heard[band * bins + b] / start)) : float.NegativeInfinity;
            }
            trace.Result = result;
        }
        return result;
    }

    /// <summary>
    /// A ray segment's crossing of the receiver sphere (centred at the origin of <paramref name="from"/>'s frame): its
    /// energy times the chord, as fluence, into the bin of the crossing's middle (air absorption up to there).
    /// </summary>
    private static void Receive(Span<double> heard, Vector3 from, Vector3 dir, float length, float travelled, Bands3 e, Bands3 airDbPerMetre, float c,
        float r2, float fluence, int bins)
    {
        float along = -Vector3.Dot(from, dir);          // closest approach, along the segment
        float miss = from.LengthSquared() - along * along;
        if (miss >= r2) return;
        float half = MathF.Sqrt(r2 - miss);
        float enter = MathF.Max(0, along - half), leave = MathF.Min(length, along + half);
        if (leave <= enter) return;
        float middle = 0.5f * (enter + leave);
        int bin = (int)((travelled + middle) / c / BinSeconds);
        if (bin >= bins) return;
        float weight = (leave - enter) * fluence;
        for (int band = 0; band < 3; band++)
            heard[band * bins + bin] += e[band] * weight * MathF.Pow(10, -airDbPerMetre[band] * middle / 10);
    }

    /// <summary>
    /// Next-event estimation of a scattering bounce: the scattered energy <paramref name="e"/> leaves Lambertian, so the
    /// listener at distance d and angle θ off the normal receives the fluence e·cos θ/(π d²), which is 4·e·cos θ/d²
    /// against a source's direct fluence at 1 m (1/4π). Within the receiver's radius d is held at the radius.
    /// </summary>
    private static void Rain(AcousticScene scene, Span<double> heard, Vector3 at, Vector3 normal, Vector3 listener, float travelled, Bands3 e,
        Bands3 airDbPerMetre, float c, float r2, int bins)
    {
        var toListener = listener - at;
        float d2 = toListener.LengthSquared(), d = MathF.Sqrt(d2);
        float cos = Vector3.Dot(normal, toListener) / MathF.Max(d, 1e-6f);
        if (cos <= 0) return;
        int bin = (int)((travelled + d) / c / BinSeconds);
        if (bin >= bins || scene.Raycast(at, toListener, 1, out _)) return;
        float weight = 4 * cos / MathF.Max(d2, r2);
        for (int band = 0; band < 3; band++)
            heard[band * bins + bin] += e[band] * weight * MathF.Pow(10, -airDbPerMetre[band] * d / 10);
    }

    private static float Pow10(float x) => MathF.Pow(10, x);

    /// <summary>Adds a ray's energy to the bins it's alive in, decaying through air within the segment.</summary>
    private static void Deposit(Span<double> energy, float t0, float t1, Bands3 e, Bands3 airDbPerMetre, float c, int bins)
    {
        int b0 = (int)(t0 / BinSeconds), b1 = Math.Min(bins - 1, (int)(t1 / BinSeconds));
        for (int b = b0; b <= b1; b++)
        {
            float mid = MathF.Max(0, (b + 0.5f) * BinSeconds - t0) * c;
            for (int band = 0; band < 3; band++)
                energy[band * bins + b] += e[band] * MathF.Pow(10, -airDbPerMetre[band] * mid / 10);
        }
    }

    /// <summary>T60 from an energy decay: a line fitted to its level between −5 and −35 dB below the start, extrapolated to −60.</summary>
    private static float Decay(Span<double> energy, int band, int bins)
    {
        var e = energy.Slice(band * bins, bins);
        double start = e[0];
        if (start <= 0) return 0;
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        int n = 0;
        for (int b = 0; b < bins; b++)
        {
            if (e[b] <= 0) break;
            double db = 10 * Math.Log10(e[b] / start);
            if (db > -5) continue;
            if (db < -35) break;
            double t = (b + 0.5) * BinSeconds;
            sx += t; sy += db; sxx += t * t; sxy += t * db; n++;
        }
        if (n < 3) return 0;
        double slope = (n * sxy - sx * sy) / (n * sxx - sx * sx);
        return slope < 0 ? (float)(-60 / slope) : 0;
    }

    /// <summary>
    /// The early decay time of a backward-integrated curve: from when it starts falling (the first sound arriving) to
    /// when it's 10 dB below its start (interpolated between bins), times six. (A crossing rather than a fit, so a decay
    /// that falls 10 dB within a few bins, out in the open, still gets a time.)
    /// </summary>
    private static float EarlyDecay(Span<double> energy, int band, int bins)
    {
        var e = energy.Slice(band * bins, bins);
        double start = e[0];
        if (start <= 0) return 0;
        int onset = 0;
        while (onset + 1 < bins && e[onset + 1] >= start) onset++; // nothing has arrived yet
        double previous = 0;
        for (int b = onset + 1; b < bins; b++)
        {
            double db = e[b] > 0 ? 10 * Math.Log10(e[b] / start) : -100;
            if (db <= -10)
            {
                double frac = (previous - -10) / Math.Max(previous - db, 1e-9);
                return (float)(6 * (b - 1 + frac - onset) * BinSeconds);
            }
            previous = db;
        }
        return 0; // never falls 10 dB in the window: as good as endless, which a closed lossless box would be
    }

    /// <summary>A cosine-weighted direction in the hemisphere around a normal (Lambertian scattering).</summary>
    private static Vector3 CosineDirection(Vector3 normal, float u1, float u2)
    {
        float r = MathF.Sqrt(u1), theta = 2 * MathF.PI * u2;
        float x = r * MathF.Cos(theta), y = r * MathF.Sin(theta), z = MathF.Sqrt(MathF.Max(0, 1 - u1));
        var tangent = MathF.Abs(normal.X) < 0.9f ? Vector3.Normalize(Vector3.Cross(normal, Vector3.UnitX)) : Vector3.Normalize(Vector3.Cross(normal, Vector3.UnitY));
        var bitangent = Vector3.Cross(normal, tangent);
        return Vector3.Normalize(tangent * x + bitangent * y + normal * z);
    }
}

/// <summary>
/// What a reverb estimate saw, for overlays and captures: a sample of its rays' first bounces (and which escaped), the
/// receiver's radius, and the decay curve of what was heard (backward-integrated) per band, in dB below its start, one
/// value per 5 ms.
/// </summary>
public sealed class ReverbTrace
{
    /// <summary>How many rays are sampled, how many of their bounces are kept, and how far an escaping ray is drawn.</summary>
    public const int Rays = 96, Bounces = 4;
    public const float EscapeLength = 30, BinSeconds = 0.005f;

    public struct Segment
    {
        public Vector3 A, B;
        public int Bounce;
        public bool Escaped;
    }

    public Vector3 Listener;
    public float Receiver;
    public readonly List<Segment> Segments = [];
    /// <summary>Low, mid and high bands' curves one after another, <see cref="Bins"/> each.</summary>
    public float[] Decay = [];
    public ReverbParams Result;
    public int Bins => Decay.Length / 3;
}
