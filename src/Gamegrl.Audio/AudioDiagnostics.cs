using System.Globalization;
using System.Numerics;
using System.Text;
using Gamegrl.Audio.Acoustics;

namespace Gamegrl.Audio;

/// <summary>One positional voice as the last audio update placed it.</summary>
public struct VoiceReport
{
    public VoiceHandle Voice;
    public AudioClip? Clip;
    public Vector3 Position;
    /// <summary>Its level at the listener in dB, before HDR (what the HDR window ranks).</summary>
    public float LoudnessDb;
    /// <summary>What was sent to the mixer: direction, gain after HDR, air and occlusion per band, Doppler, reverb sends.</summary>
    public SpatialParams Spatial;
    /// <summary>The direct path's mid-band transmission (1: in plain view; through walls it's what gets through).</summary>
    public float Transmission;
    public bool Rendered, Diffracted;
    public DiffractionPath Diffraction;
    /// <summary>Its reflection paths in <see cref="AudioDiagnostics.Paths"/> and the taps sent, slot by slot, in <see cref="AudioDiagnostics.Taps"/>.</summary>
    public int PathStart, PathCount, TapStart, TapCount;
}

/// <summary>Where the listener's reverb came from.</summary>
public enum ReverbSource : byte { None, Traced, Baked }

/// <summary>
/// What the audio update did last frame, for overlays and captures: every positional voice with its paths and taps, the
/// reverb estimates with a sample of their rays and decay curves. Off by default; while <see cref="Enabled"/> the
/// update fills it each frame (into reused lists) and the simulator keeps traces of its estimates.
/// </summary>
public sealed class AudioDiagnostics
{
    public bool Enabled { get; set; }
    public readonly List<VoiceReport> Voices = [];
    public readonly List<ReflectionPath> Paths = [];
    public readonly List<Reflection> Taps = [];
    /// <summary>The listener's world transform, as of the last update.</summary>
    public Matrix4x4 Listener;
    public ReverbSource ReverbSource;
    /// <summary>The traces of the latest reverb and other-room estimates (null when baked, off, or not traced yet).</summary>
    public ReverbTrace? Reverb, Remote;
    /// <summary>Where the other room's reverb is heard from (world), when it's active.</summary>
    public Vector3 RemoteOpening;
    public bool RemoteActive;

    internal void Begin(in Matrix4x4 listener)
    {
        Voices.Clear(); Paths.Clear(); Taps.Clear();
        Listener = listener;
    }

    internal void SetPaths(int voice, ReadOnlySpan<ReflectionPath> paths, ReadOnlySpan<Reflection> taps)
    {
        if (voice >= Voices.Count) return;
        var r = Voices[voice];
        r.PathStart = Paths.Count; r.PathCount = paths.Length;
        foreach (var p in paths) Paths.Add(p);
        r.TapStart = Taps.Count; r.TapCount = taps.Length;
        foreach (var t in taps) Taps.Add(t);
        Voices[voice] = r;
    }

    /// <summary>A plain-text account of everything above and the context's state, for a capture's report.</summary>
    public string Describe(AudioContext audio)
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        void Line(FormattableString s) => sb.AppendLine(s.ToString(c));
        static string V(Vector3 v) => string.Create(CultureInfo.InvariantCulture, $"({v.X:0.00}, {v.Y:0.00}, {v.Z:0.00})");
        static string Db(float a) => a > 0 ? string.Create(CultureInfo.InvariantCulture, $"{20 * MathF.Log10(a):0.0} dB") : "-inf dB";
        var engine = audio.Engine;
        var env = audio.Environment;
        Line($"engine: {engine.SampleRate} Hz, out {engine.Layout.Count} ch, bed {engine.BedLayout.Count} ch{(engine.Headphones ? ", binaural (headphones)" : "")}, device {engine.Device?.Name ?? "offline"}");
        Line($"voices: {engine.ActiveVoices} playing, {engine.RealVoices} rendered; dropped commands {engine.DroppedCommands}");
        var forward = -new Vector3(Listener.M31, Listener.M32, Listener.M33);
        Line($"listener: at {V(Listener.Translation)}, facing {V(forward)}, gain {audio.ListenerGain:0.00}, velocity {V(audio.ListenerVelocity)}");
        Line($"environment: reverb {env.Reverb}, reflections {env.Reflections}, diffraction {env.Diffraction}, remote reverb {env.RemoteReverb}, HDR {env.Hdr}; {env.TemperatureC:0} °C, {env.HumidityPercent:0}% RH");
        Line($"HDR: window top {audio.Hdr.TopDb:0.0} dB, range {env.HdrRangeDb:0} dB, release {env.HdrReleaseDbPerSecond:0} dB/s, floor {env.HdrFloorDb:0} dB, headroom {env.HdrHeadroomDb:0} dB");
        sb.AppendLine();
        Reverb(sb, "reverb", audio.Reverb, ReverbSource.ToString().ToLowerInvariant());
        if (this.Reverb is { } trace)
            Line($"    estimated at {V(trace.Listener)}, {Vector3.Distance(trace.Listener, Listener.Translation):0.00} m from the listener now (receiver {trace.Receiver:0.00} m)");
        Reverb(sb, "other room", audio.RemoteReverb, RemoteActive ? "heard from " + V(RemoteOpening) : audio.HiddenInThisRoom ? "the hidden source is in this room: not played" : "inactive");
        sb.AppendLine();
        Line($"positional voices ({Voices.Count}):");
        foreach (var r in Voices)
        {
            var s = r.Spatial;
            float distance = Vector3.Distance(r.Position, Listener.Translation);
            Line($"- {r.Clip?.Name ?? "?"} #{r.Voice.Value:x}: at {V(r.Position)}, {distance:0.0} m, {(r.Rendered ? "rendered" : "virtual")}");
            Line($"    loudness {r.LoudnessDb:0.0} dB at the listener; gain {Db(s.Gain)} (after HDR); bands {s.Low:0.000}/{s.Mid:0.000}/{s.High:0.000}; doppler {s.Doppler:0.0000}; spread {s.Spread:0.00}");
            Line($"    direction {V(s.Direction)} (listener space); transmission {r.Transmission:0.000}; reverb send {Db(s.Reverb)}, other room {Db(s.RemoteReverb)}");
            if (r.Diffracted)
            {
                var d = r.Diffraction;
                Line($"    round {d.Edges} edge(s) at {V(d.First)}{(d.Edges > 1 ? " and " + V(d.Second) : "")}: path {d.Length:0.00} m, detour {d.Detour:0.00} m, gain {d.Gain.Low:0.000}/{d.Gain.Mid:0.000}/{d.Gain.High:0.000}");
            }
            for (int k = 0; k < r.PathCount; k++)
            {
                var p = Paths[r.PathStart + k];
                Line($"    path {k}: order {p.Order}, key {p.Key:x}, {p.Length:0.00} m when found, via {V(p.Point)}{(p.Order > 1 ? " and " + V(p.First) : "")}, surfaces {p.Surfaces.Mid:0.000}");
            }
            for (int k = 0; k < r.TapCount; k++)
            {
                var t = Taps[r.TapStart + k];
                Line($"    tap {k}: delay {t.Delay * 1000:0.00} ms, gain {Db(t.Gain)} re direct, from {V(t.Direction)}{(t.Restart ? ", restarted" : "")}");
            }
        }
        return sb.ToString();

        static void Reverb(StringBuilder sb, string name, in ReverbParams r, string source)
        {
            var c = CultureInfo.InvariantCulture;
            if (r.IsSilent) { sb.AppendLine(string.Create(c, $"{name}: none ({source})")); return; }
            sb.AppendLine(string.Create(c, $"{name} ({source}): renders EDT {r.DecaySeconds.Low:0.00}/{r.DecaySeconds.Mid:0.00}/{r.DecaySeconds.High:0.00} s; late T30 {r.LateDecaySeconds.Low:0.00}/{r.LateDecaySeconds.Mid:0.00}/{r.LateDecaySeconds.High:0.00} s"));
            sb.AppendLine(string.Create(c, $"    level {20 * MathF.Log10(MathF.Max(r.Level, 1e-9f)):0.0} dB re direct at 1 m; pre-delay {r.PreDelaySeconds * 1000:0.0} ms; volume {r.Volume:0} m³, surface {r.Surface:0} m², enclosed {r.Enclosure * 100:0}%"));
        }
    }

    /// <summary>The decay curves as CSV: time, then the reverb's and the other room's low, mid and high bands in dB.</summary>
    public string DecayCsv()
    {
        var sb = new StringBuilder("seconds,reverb_low,reverb_mid,reverb_high,remote_low,remote_mid,remote_high\n");
        int bins = Math.Max(Reverb?.Bins ?? 0, Remote?.Bins ?? 0);
        static string Cell(ReverbTrace? t, int band, int b) =>
            t is null || b >= t.Bins || !float.IsFinite(t.Decay[band * t.Bins + b]) ? "" : t.Decay[band * t.Bins + b].ToString("0.00", CultureInfo.InvariantCulture);
        for (int b = 0; b < bins; b++)
            sb.Append(((b + 0.5f) * ReverbTrace.BinSeconds).ToString("0.0000", CultureInfo.InvariantCulture))
              .Append(',').Append(Cell(Reverb, 0, b)).Append(',').Append(Cell(Reverb, 1, b)).Append(',').Append(Cell(Reverb, 2, b))
              .Append(',').Append(Cell(Remote, 0, b)).Append(',').Append(Cell(Remote, 1, b)).Append(',').Append(Cell(Remote, 2, b)).Append('\n');
        return sb.ToString();
    }
}

/// <summary>Writes a capture of what the audio engine is doing, for someone else (or a model) to examine.</summary>
public static class AudioCapture
{
    /// <summary>
    /// Writes into a new directory: <c>output.wav</c> (the output history, when kept: see <see cref="AudioEngine.KeepHistory"/>),
    /// <c>report.txt</c> (<see cref="AudioDiagnostics.Describe"/>, after <paramref name="note"/>) and <c>decay.csv</c>.
    /// Diagnostics must have been enabled for at least one update for the report to have voices and traces.
    /// </summary>
    public static void Write(string directory, AudioContext audio, string? note = null)
    {
        Directory.CreateDirectory(directory);
        var history = audio.Engine.History();
        if (history.Frames > 0) Wav.Write(Path.Combine(directory, "output.wav"), history);
        var report = new StringBuilder();
        if (!string.IsNullOrEmpty(note)) report.AppendLine(note).AppendLine();
        report.AppendLine(string.Create(CultureInfo.InvariantCulture, $"output.wav: the last {history.Duration:0.0} s of output (ends at the capture)"));
        report.Append(audio.Diagnostics.Describe(audio));
        File.WriteAllText(Path.Combine(directory, "report.txt"), report.ToString());
        File.WriteAllText(Path.Combine(directory, "decay.csv"), audio.Diagnostics.DecayCsv());
    }
}
