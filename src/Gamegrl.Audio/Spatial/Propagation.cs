namespace Gamegrl.Audio.Spatial;

/// <summary>The three bands every per-voice filter works in (as Steam Audio does): below 800 Hz, 800 Hz to 8 kHz, above 8 kHz.</summary>
public static class Bands
{
    public const float LowCrossover = 800, HighCrossover = 8000;
    /// <summary>Representative frequencies for evaluating a band's response: ~400 Hz, the geometric middle (2.5 kHz), 12 kHz.</summary>
    public const float LowCenter = 400, MidCenter = 2530, HighCenter = 12000;
}

/// <summary>Sound travelling through air: distance, absorption, Doppler and source directivity. Pure functions of their inputs.</summary>
public static class Propagation
{
    /// <summary>
    /// Atmospheric absorption in dB per metre, from ISO 9613-1:1993 (pure-tone absorption by oxygen and nitrogen
    /// relaxation and classical losses). Temperature in °C, relative humidity in percent, pressure in kPa.
    /// </summary>
    public static double AirAbsorptionDbPerMetre(double frequency, double temperatureC = 20, double humidityPercent = 50, double pressureKPa = 101.325)
    {
        const double pr = 101.325, t0 = 293.15, t01 = 273.16;
        double t = temperatureC + 273.15;
        double pa = pressureKPa / pr;
        double c = -6.8346 * Math.Pow(t01 / t, 1.261) + 4.6151;
        double h = humidityPercent * Math.Pow(10, c) / pa; // molar concentration of water vapour, %
        double fro = pa * (24 + 4.04e4 * h * (0.02 + h) / (0.391 + h));
        double frn = pa * Math.Pow(t / t0, -0.5) * (9 + 280 * h * Math.Exp(-4.170 * (Math.Pow(t / t0, -1.0 / 3) - 1)));
        double f2 = frequency * frequency;
        return 8.686 * f2 * (1.84e-11 / pa * Math.Sqrt(t / t0)
            + Math.Pow(t / t0, -2.5) * (0.01275 * Math.Exp(-2239.1 / t) / (fro + f2 / fro) + 0.1068 * Math.Exp(-3352.0 / t) / (frn + f2 / frn)));
    }

    /// <summary>The speed of sound in dry air at a temperature (°C).</summary>
    public static float SpeedOfSound(float temperatureC) => 331.3f * MathF.Sqrt(1 + temperatureC / 273.15f);

    /// <summary>
    /// Inverse-distance attenuation (−6 dB per doubling, the free-field law) beyond <paramref name="minDistance"/>, full
    /// level inside it; <paramref name="rolloff"/> scales the slope (1 is physical). Silent beyond <paramref name="maxDistance"/>.
    /// </summary>
    public static float DistanceGain(float distance, float minDistance, float maxDistance, float rolloff = 1)
    {
        if (distance >= maxDistance) return 0;
        if (distance <= minDistance || minDistance <= 0) return 1;
        return MathF.Pow(minDistance / distance, rolloff);
    }

    /// <summary>
    /// A directivity cone: full level within <paramref name="innerAngle"/> (degrees, whole angle) of the source's forward
    /// axis, <paramref name="outerGain"/> beyond <paramref name="outerAngle"/>, linear in angle between.
    /// <paramref name="cosine"/> is the cosine of the angle between the source's forward axis and the direction to the listener.
    /// </summary>
    public static float ConeGain(float cosine, float innerAngle, float outerAngle, float outerGain)
    {
        if (innerAngle >= 360) return 1;
        float angle = float.RadiansToDegrees(MathF.Acos(Math.Clamp(cosine, -1, 1))) * 2;
        if (angle <= innerAngle) return 1;
        if (angle >= outerAngle) return outerGain;
        float t = (angle - innerAngle) / MathF.Max(outerAngle - innerAngle, 1e-6f);
        return 1 + (outerGain - 1) * t;
    }

    /// <summary>
    /// The Doppler factor heard: <c>(c + v_listener) / (c + v_source)</c>, with each speed measured along the line from
    /// listener to source (the source's positive when it recedes, the listener's when it approaches the source). Clamped
    /// to two octaves either way; <paramref name="scale"/> exaggerates or tames it.
    /// </summary>
    public static float Doppler(float sourceRecedingSpeed, float listenerApproachSpeed, float speedOfSound, float scale = 1)
    {
        if (scale == 0) return 1;
        float c = speedOfSound;
        float vs = Math.Clamp(sourceRecedingSpeed * scale, -0.9f * c, 0.9f * c), vl = Math.Clamp(listenerApproachSpeed * scale, -0.9f * c, 0.9f * c);
        return Math.Clamp((c + vl) / (c + vs), 0.25f, 4);
    }

    /// <summary>Per-band gains for air absorption over a distance.</summary>
    public static (float Low, float Mid, float High) AirBands(float distance, float temperatureC, float humidityPercent)
    {
        float G(float f) => (float)Math.Pow(10, -AirAbsorptionDbPerMetre(f, temperatureC, humidityPercent) * distance / 20);
        return (G(Bands.LowCenter), G(Bands.MidCenter), G(Bands.HighCenter));
    }
}

/// <summary>
/// HDR mixing (as in DICE's Frostbite): every world sound has a loudness in dB, and a window of <see cref="RangeDb"/> dB
/// hangs from the loudest one audible. Sounds play at their level relative to the window's top, so a gunshot pushes
/// everything else down; when it ends the top falls back at <see cref="ReleaseDbPerSecond"/> and the quiet sounds come
/// up again, like eyes adjusting. Sounds below the window's bottom are culled. The top never falls below
/// <see cref="FloorDb"/>, so a quiet scene isn't amplified without limit.
/// </summary>
public sealed class HdrWindow
{
    public float RangeDb { get; set; } = 60;
    public float ReleaseDbPerSecond { get; set; } = 12;
    public float FloorDb { get; set; } = 50;
    /// <summary>The window's current top: the loudness that plays at full scale.</summary>
    public float TopDb { get; private set; } = 50;

    /// <summary>Moves the window for this frame given the loudest audible sound (or nothing).</summary>
    public void Update(float loudestDb, float deltaSeconds)
    {
        float target = MathF.Max(loudestDb, FloorDb);
        TopDb = target >= TopDb ? target : MathF.Max(target, TopDb - ReleaseDbPerSecond * deltaSeconds);
    }

    /// <summary>A sound starting now: if it's louder than the top, the window jumps up to it at once (no start above full scale).</summary>
    public void Raise(float loudnessDb) { if (loudnessDb > TopDb) TopDb = loudnessDb; }

    /// <summary>The linear gain for a sound of this loudness, or 0 when it's below the window.</summary>
    public float Gain(float loudnessDb) => loudnessDb < TopDb - RangeDb ? 0 : MathF.Pow(10, (loudnessDb - TopDb) / 20);
}
