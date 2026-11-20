using System.Numerics;
using Gamegrl.Audio.Spatial;

namespace Gamegrl.Audio.Acoustics;

/// <summary>
/// The late reverberation at a point, as the reverb renders it: decay time per band, the reverberant level relative to
/// the direct sound at 1 m, a pre-delay, and what the estimate saw (volume, surface, how enclosed the point is).
/// </summary>
public struct ReverbParams
{
    public Bands3 DecaySeconds;
    /// <summary>Reverberant amplitude relative to a source's direct sound at 1 m (diffuse-field theory: √(16π ÷ R) for room constant R).</summary>
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
/// <item>Decay: the total energy still travelling, over time, is the room's energy decay; its slope between −5 and
/// −35 dB gives the reverberation time per band (by construction what Eyring's formula approximates).</item>
/// <item>Size: first-hit distances give the volume visible from the point, V = (4π/N)·Σ r³/3; the mean free path
/// l = 4V/S then gives the surface S. The fraction of first rays that hit something is the enclosure.</item>
/// <item>Level: the room constant R = S·ᾱ/(1 − ᾱ), with ᾱ from the measured decay by Eyring, puts the reverberant
/// energy at 16π/R times a source's direct energy at 1 m, for a closed room. Where energy escapes early (open air, a cliff,
/// a cave mouth) the decay's tail can be long while little energy is left in it, so the energy is scaled by how long
/// it actually stays, ∫E dt, against a closed room's T/13.8.</item>
/// </list>
/// Deterministic for given inputs (a fixed random seed).
/// </summary>
public static class ReverbEstimator
{
    public const float MaxSeconds = 4;
    private const float BinSeconds = 0.005f;

    public static ReverbParams Estimate(AcousticScene scene, Vector3 listener, int rays = 2048, int bounces = 64,
        float temperatureC = 20, float humidityPercent = 50, bool airAbsorption = true, int seed = 1)
    {
        if (scene.TriangleCount == 0 || rays <= 0) return ReverbParams.None;
        float c = Propagation.SpeedOfSound(temperatureC);
        int bins = (int)(MaxSeconds / BinSeconds);
        Span<double> energy = new double[bins * 3]; // per band: energy alive during each bin, summed over rays
        var air = airAbsorption
            ? new Bands3((float)Propagation.AirAbsorptionDbPerMetre(Bands.LowCenter, temperatureC, humidityPercent),
                (float)Propagation.AirAbsorptionDbPerMetre(Bands.MidCenter, temperatureC, humidityPercent),
                (float)Propagation.AirAbsorptionDbPerMetre(Bands.HighCenter, temperatureC, humidityPercent))
            : default;
        uint state = (uint)seed * 2654435761u | 1;
        float Random() { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return (state >> 8) * (1f / 16777216f); }

        double volume = 0, pathSum = 0;
        int firstHits = 0, segments = 0;
        float golden = MathF.PI * (3 - MathF.Sqrt(5));
        float maxPath = MaxSeconds * c;
        for (int r = 0; r < rays; r++)
        {
            float y = 1 - 2 * (r + 0.5f) / rays, rad = MathF.Sqrt(1 - y * y), phi = golden * r;
            var dir = new Vector3(MathF.Cos(phi) * rad, y, MathF.Sin(phi) * rad);
            var origin = listener;
            var e = Bands3.All(1f / rays);
            float travelled = 0;
            for (int b = 0; b < bounces; b++)
            {
                // Escaped through an opening (or out of time): its energy leaves the room.
                if (!scene.Raycast(origin, dir, maxPath - travelled, out var hit)) break;
                if (b == 0) { firstHits++; volume += hit.Distance * hit.Distance * hit.Distance / 3; }
                else { pathSum += hit.Distance; segments++; }
                Deposit(energy, travelled / c, (travelled + hit.Distance) / c, e, air, c, bins);
                // Air absorption over the segment, then the surface.
                if (airAbsorption) e = e * new Bands3(Pow10(-air.Low * hit.Distance / 10), Pow10(-air.Mid * hit.Distance / 10), Pow10(-air.High * hit.Distance / 10));
                travelled += hit.Distance;
                var material = scene.MaterialOf(hit.Triangle);
                e = e * new Bands3(1 - material.Absorption.Low, 1 - material.Absorption.Mid, 1 - material.Absorption.High);
                if (e.Low + e.Mid + e.High < 1e-9f / rays) break;
                var normal = Vector3.Dot(hit.Normal, dir) > 0 ? -hit.Normal : hit.Normal; // facing the ray
                origin = origin + dir * hit.Distance + normal * 1e-4f;
                dir = Random() < material.Scattering ? CosineDirection(normal, Random(), Random()) : Vector3.Reflect(dir, normal);
            }
        }

        var result = new ReverbParams { Enclosure = (float)firstHits / rays };
        result.Volume = (float)(4 * Math.PI / rays * volume);
        float meanFreePath = segments > 0 ? (float)(pathSum / segments) : 0;
        result.Surface = meanFreePath > 0 ? 4 * result.Volume / meanFreePath : 0;
        result.DecaySeconds = new Bands3(Decay(energy, 0, bins), Decay(energy, 1, bins), Decay(energy, 2, bins));
        result.PreDelaySeconds = firstHits > 0 ? Math.Min(0.1f, meanFreePath / c) : 0;
        float t = result.DecaySeconds.Mid;
        if (t > 0 && result.Volume > 0 && result.Surface > 0)
        {
            // Eyring: T = 0.161·V / (−S·ln(1 − ᾱ)) → ᾱ; room constant R = S·ᾱ/(1 − ᾱ).
            float alpha = 1 - MathF.Exp(-0.161f * result.Volume / (result.Surface * t));
            alpha = Math.Clamp(alpha, 1e-4f, 0.999f);
            float roomConstant = result.Surface * alpha / (1 - alpha);
            // How long the energy really stays (the decay curve's integral, from 1 at the start) against a closed room's.
            double stays = 0, start = energy[bins];
            for (int b = 0; b < bins && start > 0; b++) stays += energy[bins + b] / start * BinSeconds;
            float kept = start > 0 ? (float)Math.Min(1, stays / (t / 13.8)) : 0;
            result.Level = MathF.Sqrt(16 * MathF.PI / roomConstant * kept);
        }
        return result;
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
