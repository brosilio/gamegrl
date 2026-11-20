using System.Numerics;
using Gamegrl.Audio.Spatial;

namespace Gamegrl.Audio.Acoustics;

/// <summary>Settings for <see cref="AcousticMeasurement.Measure"/>.</summary>
public struct MeasureOptions
{
    /// <summary>Rays from the source for the echogram.</summary>
    public int Rays;
    /// <summary>The receiver sphere around the listener the rays are counted in.</summary>
    public float ReceiverRadius;
    public float MaxSeconds;
    public float TemperatureC, HumidityPercent;

    public static MeasureOptions Default => new() { Rays = 20000, ReceiverRadius = 0.5f, MaxSeconds = 2, TemperatureC = 20, HumidityPercent = 50 };
}

/// <summary>
/// What a measurement microphone would tell you about a source and a listener in a scene, computed by ray tracing:
/// the echogram (sound energy arriving per millisecond, per band, relative to a unit-power source), the direct sound,
/// the specular reflection paths, and the room-acoustics metrics of ISO 3382-1 derived from them —
/// <list type="bullet">
/// <item>T30: decay time from the Schroeder-integrated curve between −5 and −35 dB, extrapolated to −60;</item>
/// <item>EDT: early decay time, from the first 10 dB;</item>
/// <item>C50 and C80: clarity, early (first 50 or 80 ms after the direct sound) to late energy, in dB — speech and music;</item>
/// <item>D50: definition, the early fraction of all energy;</item>
/// <item>the direct-to-reverberant ratio and the initial time delay gap (direct sound to first reflection);</item>
/// </list>
/// plus the listener-centric reverb estimate (volume, surface, enclosure) and Sabine's and Eyring's predictions from it.
/// </summary>
public sealed class AcousticMeasurement
{
    public const float BinSeconds = 0.001f;

    /// <summary>Reflected energy per bin (intensity from a unit-power source, W/m²), per band; the direct sound is separate.</summary>
    public required float[] Low, Mid, High;
    public float DirectSeconds, Distance;
    /// <summary>The direct sound's intensity, per band, after whatever it passes through.</summary>
    public Bands3 DirectEnergy;
    public Bands3 DirectTransmission;
    public required ReflectionPath[] Paths;
    public ReverbParams Reverb;
    public Bands3 MeanAbsorption;
    public Bands3 T30, Edt, C50, C80, D50;
    public float DirectToReverberantDb, InitialGapSeconds;
    public float SabineSeconds, EyringSeconds;
    /// <summary>Where the reverberant field is as loud as the direct sound: √(R ÷ 16π) for room constant R.</summary>
    public float CriticalDistance;

    public float[] Band(int band) => band switch { 0 => Low, 1 => Mid, _ => High };

    public static AcousticMeasurement Measure(AcousticScene scene, Vector3 source, Vector3 listener, MeasureOptions options, CancellationToken stop = default)
    {
        float c = Propagation.SpeedOfSound(options.TemperatureC);
        int bins = (int)(options.MaxSeconds / BinSeconds);
        var energy = new double[3, bins];
        float distance = Vector3.Distance(source, listener);
        var transmission = scene.DirectPath(listener, source, 0, 0);
        var air = new Bands3((float)Propagation.AirAbsorptionDbPerMetre(Bands.LowCenter, options.TemperatureC, options.HumidityPercent),
            (float)Propagation.AirAbsorptionDbPerMetre(Bands.MidCenter, options.TemperatureC, options.HumidityPercent),
            (float)Propagation.AirAbsorptionDbPerMetre(Bands.HighCenter, options.TemperatureC, options.HumidityPercent));
        float Air(int band, float metres) => MathF.Pow(10, -air[band] * metres / 10);
        // The direct sound: a unit-power point source gives 1/(4πr²); walls pass the square of their pressure transmission.
        float spherical = 1 / (4 * MathF.PI * MathF.Max(distance, 0.01f) * MathF.Max(distance, 0.01f));
        var direct = new Bands3(spherical * transmission.Low * transmission.Low * Air(0, distance), spherical * transmission.Mid * transmission.Mid * Air(1, distance),
            spherical * transmission.High * transmission.High * Air(2, distance));

        // Reflected energy: rays from the source, counted while they cross the receiver sphere after their first bounce.
        // A ray of energy E crossing a chord ℓ of a sphere of volume V adds E·ℓ/V of intensity (which makes the direct
        // sound come out at exactly 1/(4πr²)).
        int rays = Math.Max(1, options.Rays);
        float radius = options.ReceiverRadius, sphereVolume = 4f / 3 * MathF.PI * radius * radius * radius;
        float maxPath = options.MaxSeconds * c;
        float golden = MathF.PI * (3 - MathF.Sqrt(5));
        uint state = 0x9E3779B9u;
        float Random() { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return (state >> 8) * (1f / 16777216f); }
        for (int r = 0; r < rays; r++)
        {
            if ((r & 1023) == 0) stop.ThrowIfCancellationRequested();
            float y = 1 - 2 * (r + 0.5f) / rays, rad = MathF.Sqrt(1 - y * y), phi = golden * r;
            var dir = new Vector3(MathF.Cos(phi) * rad, y, MathF.Sin(phi) * rad);
            var origin = source;
            var e = Bands3.All(1f / rays);
            float travelled = 0;
            for (int bounce = 0; bounce < 256 && travelled < maxPath; bounce++)
            {
                bool hit = scene.Raycast(origin, dir, maxPath - travelled, out var h);
                float length = hit ? h.Distance : maxPath - travelled;
                if (bounce > 0)
                {
                    // Chord of the receiver sphere within this segment.
                    var oc = origin - listener;
                    float b = Vector3.Dot(oc, dir), cc = Vector3.Dot(oc, oc) - radius * radius, disc = b * b - cc;
                    if (disc > 0)
                    {
                        float sq = MathF.Sqrt(disc), t0 = MathF.Max(0, -b - sq), t1 = MathF.Min(length, -b + sq);
                        if (t1 > t0)
                        {
                            float along = travelled + 0.5f * (t0 + t1);
                            int bin = (int)(along / c / BinSeconds);
                            if (bin < bins)
                            {
                                float chord = (t1 - t0) / sphereVolume;
                                float airPart = 0.5f * (t0 + t1);
                                for (int band = 0; band < 3; band++) energy[band, bin] += e[band] * chord * Air(band, airPart);
                            }
                        }
                    }
                }
                if (!hit) break;
                for (int band = 0; band < 3; band++) e = Set(e, band, e[band] * Air(band, length));
                travelled += length;
                var m = scene.MaterialOf(h.Triangle);
                e = e * new Bands3(1 - m.Absorption.Low, 1 - m.Absorption.Mid, 1 - m.Absorption.High);
                if (e.Mid < 1e-7f / rays && e.Low < 1e-7f / rays) break;
                var normal = Vector3.Dot(h.Normal, dir) > 0 ? -h.Normal : h.Normal;
                origin = origin + dir * length + normal * 1e-4f;
                dir = Random() < m.Scattering ? Cosine(normal, Random(), Random()) : Vector3.Reflect(dir, normal);
            }
        }

        var result = new AcousticMeasurement
        {
            Low = new float[bins], Mid = new float[bins], High = new float[bins], Distance = distance, DirectSeconds = distance / c,
            DirectEnergy = direct, DirectTransmission = transmission, Paths = new ReflectionPath[ReflectionTaps.Count * 2],
        };
        for (int b = 0; b < bins; b++) { result.Low[b] = (float)energy[0, b]; result.Mid[b] = (float)energy[1, b]; result.High[b] = (float)energy[2, b]; }
        stop.ThrowIfCancellationRequested();
        int paths = ImageSources.Find(scene, listener, source, result.Paths);
        result.Paths = result.Paths[..paths];
        result.Reverb = ReverbEstimator.Estimate(scene, listener, 4096, 128, options.TemperatureC, options.HumidityPercent);
        result.MeanAbsorption = scene.MeanAbsorption();
        result.Metrics(c);
        return result;
    }

    private static Bands3 Set(Bands3 b, int band, float v) => band switch { 0 => b with { Low = v }, 1 => b with { Mid = v }, _ => b with { High = v } };

    private void Metrics(float c)
    {
        int directBin = (int)(DirectSeconds / BinSeconds);
        float[] T = new float[3], E = new float[3], c50 = new float[3], c80 = new float[3], d50 = new float[3];
        double reflectedTotal = 0;
        for (int band = 0; band < 3; band++)
        {
            var e = Band(band);
            int n = e.Length;
            // The impulse response's energy: the direct sound in its bin plus the reflections.
            var total = new double[n];
            for (int i = 0; i < n; i++) total[i] = e[i];
            if (directBin < n) total[directBin] += DirectEnergy[band];
            double early50 = 0, early80 = 0, all = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (i - directBin) * BinSeconds;
                if (t < 0.05) early50 += total[i];
                if (t < 0.08) early80 += total[i];
                all += total[i];
            }
            c50[band] = Db(early50, all - early50);
            c80[band] = Db(early80, all - early80);
            d50[band] = all > 0 ? (float)(early50 / all) : 0;
            // Schroeder backward integration from the direct sound on.
            var edc = new double[n];
            double acc = 0;
            for (int i = n - 1; i >= 0; i--) { acc += total[i]; edc[i] = acc; }
            T[band] = Fit(edc, directBin, -5, -35);
            E[band] = Fit(edc, directBin, 0, -10);
            if (band == 1) for (int i = 0; i < n; i++) reflectedTotal += e[i];
        }
        T30 = new Bands3(T[0], T[1], T[2]); Edt = new Bands3(E[0], E[1], E[2]);
        C50 = new Bands3(c50[0], c50[1], c50[2]); C80 = new Bands3(c80[0], c80[1], c80[2]); D50 = new Bands3(d50[0], d50[1], d50[2]);
        DirectToReverberantDb = Db(DirectEnergy.Mid, reflectedTotal);
        InitialGapSeconds = Paths.Length > 0 ? Paths.Min(p => p.Length) / c - DirectSeconds : 0;
        float alpha = Math.Clamp(MeanAbsorption.Mid, 1e-4f, 0.999f);
        float v = Reverb.Volume, s = Reverb.Surface;
        if (v > 0 && s > 0)
        {
            SabineSeconds = 0.161f * v / (s * alpha);
            EyringSeconds = 0.161f * v / (-s * MathF.Log(1 - alpha));
            CriticalDistance = MathF.Sqrt(s * alpha / (1 - alpha) / (16 * MathF.PI));
        }
    }

    private static float Db(double a, double b) => a > 0 && b > 0 ? (float)(10 * Math.Log10(a / b)) : a > 0 ? 99 : -99;

    /// <summary>Decay time from a line fitted to the curve between two levels below its value at <paramref name="start"/>, extrapolated to −60 dB.</summary>
    private static float Fit(double[] edc, int start, double fromDb, double toDb)
    {
        if (start >= edc.Length || edc[start] <= 0) return 0;
        double reference = edc[start];
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        int n = 0;
        for (int i = start; i < edc.Length; i++)
        {
            if (edc[i] <= 0) break;
            double db = 10 * Math.Log10(edc[i] / reference);
            if (db > fromDb) continue;
            if (db < toDb) break;
            double t = i * BinSeconds;
            sx += t; sy += db; sxx += t * t; sxy += t * db; n++;
        }
        if (n < 3) return 0;
        double slope = (n * sxy - sx * sy) / (n * sxx - sx * sx);
        return slope < 0 ? (float)(-60 / slope) : 0;
    }

    private static Vector3 Cosine(Vector3 normal, float u1, float u2)
    {
        float r = MathF.Sqrt(u1), theta = 2 * MathF.PI * u2;
        float x = r * MathF.Cos(theta), y = r * MathF.Sin(theta), z = MathF.Sqrt(MathF.Max(0, 1 - u1));
        var tangent = MathF.Abs(normal.X) < 0.9f ? Vector3.Normalize(Vector3.Cross(normal, Vector3.UnitX)) : Vector3.Normalize(Vector3.Cross(normal, Vector3.UnitY));
        return Vector3.Normalize(tangent * x + Vector3.Cross(normal, tangent) * y + normal * z);
    }
}
