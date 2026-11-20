using System.Numerics;
using Gamegrl.Assets;
using Gamegrl.Audio;
using Gamegrl.Audio.Acoustics;
using Gamegrl.Audio.Devices;
using Gamegrl.Platform;
using Gamegrl.UI;

namespace Gamegrl.Editor;

/// <summary>
/// The Acoustics panel: measure how sound behaves between a source (the selected entity) and the listener (the scene's
/// <see cref="AudioListener"/>, or the Scene camera) in the scene's <see cref="AcousticSurface"/>s, as a measurement
/// microphone would — the echogram, reverberation and early decay times, clarity, definition, direct-to-reverberant ratio,
/// the room's size, Sabine's and Eyring's predictions, and the reflection paths, drawn in the Scene view — and listen
/// to the space: a clap, a gunshot or the source's own clip rendered through it (dry or wet, for speakers or
/// headphones), or export its impulse response. Measuring and rendering run as background jobs.
/// </summary>
internal sealed partial class EditorHost
{
    private AcousticMeasurement? _measurement;
    private AcousticScene? _measuredScene;
    private Vector3 _measuredSource;
    private Matrix4x4 _measuredListener = Matrix4x4.Identity;
    private string _measuredSourceName = "", _measuredListenerName = "";
    private AudioEmitter _measuredEmitter = AudioEmitter.Default;
    private Gamegrl.AssetRef<AudioAsset> _measuredClip;
    private bool _measuring, _auditionDry, _auditionPhones, _showPaths = true;
    private int _auditionSound, _pendingAudition = -1;
    private string _acousticsStatus = "";
    private AudioEngine? _auditionEngine;
    private VoiceHandle _auditionVoice;
    private readonly Dictionary<Guid, Vector3[]?> _acousticMeshes = new();
    private static readonly string[] AuditionSounds = ["clap", "gunshot", "source clip"];

    /// <summary>The last measurement (for tests and the smoke run).</summary>
    internal AcousticMeasurement? Measurement => _measurement;

    private void RegisterAcoustics()
    {
        Registry.AddPanel("acoustics", "Acoustics", DockRegion.Bottom, (_, r) => AcousticsPanel(r));
        Registry.AddCommand("acoustics.show", "Acoustics: Show", _ => { Docks.Sync(Registry.Panels); Docks.Show("acoustics"); });
        Registry.AddCommand("acoustics.measure", "Acoustics: Measure from the selected entity to the listener", _ => MeasureAcoustics());
        Registry.AddCommand("acoustics.clap", "Acoustics: Listen to a clap in the space", _ => Audition(0));
        Registry.AddCommand("acoustics.gunshot", "Acoustics: Listen to a gunshot in the space", _ => Audition(1));
        Registry.AddCommand("acoustics.export", "Acoustics: Export the impulse response (WAV)", _ => ExportImpulseResponse());
    }

    private void AcousticsPanel(Rect area)
    {
        var theme = _ui.Theme;
        if (_auditionEngine is not null) _auditionEngine.Update();
        else if (!Document.Playing && _app.World.HasResource<AudioContext>()) _app.World.Resource<AudioContext>().Engine.Update();

        // Controls, wrapping onto more rows when the panel is narrow.
        float x = area.X, y = area.Y;
        Rect Place(float width)
        {
            if (x > area.X && x + width > area.Right) { x = area.X; y++; }
            var rect = new Rect(x, y, width, 1);
            x += width + 2;
            return rect;
        }
        if (_ui.Link(Place(9), "acoustics-measure", _measuring ? "measuring" : "▶ measure", theme.Accent, !_measuring)) MeasureAcoustics();
        _ui.Combo(Place(13), "acoustics-sound", ref _auditionSound, AuditionSounds);
        if (_ui.Link(Place(8), "acoustics-listen", "» listen", theme.Accent, !_measuring)) Audition(_auditionSound);
        _ui.Toggle(Place(5), "acoustics-dry", "dry", ref _auditionDry, theme.Dim);
        _ui.Toggle(Place(12), "acoustics-phones", "headphones", ref _auditionPhones, theme.AxisY);
        _ui.Toggle(Place(7), "acoustics-paths", "paths", ref _showPaths, theme.AxisZ);
        if (_ui.Link(Place(9), "acoustics-export", "export IR", theme.Dim, _measurement is not null)) ExportImpulseResponse();
        if (_acousticsStatus.Length > 0) { var status = Place(Math.Min(area.W, _acousticsStatus.Length)); _ui.TextIn(status, _acousticsStatus, theme.Dim); }
        y++;
        if (y + 1 >= area.Bottom) return;

        var m = _measurement;
        if (m is null)
        {
            _ui.TextIn(new Rect(area.X, y + 1, area.W, 1), "Select the entity a sound comes from and measure: the listener is the scene's AudioListener, or the Scene camera.", theme.Dim);
            _ui.TextIn(new Rect(area.X, y + 2, area.W, 1), "The space is the scene's AcousticSurface boxes and meshes.", theme.Dim);
            return;
        }
        Span<char> buffer = stackalloc char[256];
        var line = new Rect(area.X, y, area.W, 1);
        string occluded = m.DirectTransmission.Mid >= 0.999f ? "clear" : $"through walls {20 * MathF.Log10(MathF.Max(m.DirectTransmission.Mid, 1e-6f)):0} dB";
        _ui.TextIn(line, SpanFormat.Write(buffer, $"{_measuredSourceName} → {_measuredListenerName}  {m.Distance:0.0} m  direct {occluded}  first reflection +{m.InitialGapSeconds * 1000:0.0} ms  {m.Paths.Length} paths"), theme.Text);
        y++;
        var r = m.Reverb;
        if (r.Volume > 0)
            _ui.TextIn(line with { Y = y }, r.Enclosure < 0.99f
                ? SpanFormat.Write(buffer, $"room {r.Volume:0} m³ {r.Surface:0} m²  abs {m.MeanAbsorption.Mid:0.00}  open {100 - r.Enclosure * 100:0}%  critical {m.CriticalDistance:0.0} m  Sabine {m.SabineSeconds:0.00} s  Eyring {m.EyringSeconds:0.00} s")
                : SpanFormat.Write(buffer, $"room {r.Volume:0} m³ {r.Surface:0} m²  abs {m.MeanAbsorption.Mid:0.00}  critical {m.CriticalDistance:0.0} m  Sabine {m.SabineSeconds:0.00} s  Eyring {m.EyringSeconds:0.00} s"), theme.Dim);
        else _ui.TextIn(line with { Y = y }, "open air: no enclosing surfaces around the listener", theme.Dim);
        y++;
        if (y + 4 > area.Bottom) return;

        // Metrics by band; columns shrink to fit.
        float col = Math.Clamp((area.W - 10) / 6, 7, 12);
        _ui.TextIn(new Rect(area.X, y, 10, 1), "band", theme.Dim);
        ReadOnlySpan<string> heads = col >= 9 ? ["T30 s", "EDT s", "C50 dB", "C80 dB", "D50"] : ["T30 s", "EDT s", "C50", "C80", "D50"];
        for (int i = 0; i < heads.Length; i++) _ui.TextIn(new Rect(area.X + 10 + i * col, y, col - 2, 1), heads[i], theme.Dim, 1);
        ReadOnlySpan<string> bands = ["< 800 Hz", "mid", "> 8 kHz"];
        for (int b = 0; b < 3; b++)
        {
            float row = y + 1 + b;
            _ui.TextIn(new Rect(area.X, row, 10, 1), bands[b], theme.Text);
            Number(area.X + 10, row, col, m.T30[b], 2);
            Number(area.X + 10 + col, row, col, m.Edt[b], 2);
            Number(area.X + 10 + 2 * col, row, col, m.C50[b], 1);
            Number(area.X + 10 + 3 * col, row, col, m.C80[b], 1);
            Number(area.X + 10 + 4 * col, row, col, m.D50[b], 2);
        }
        float side = area.X + 10 + 5 * col + 1;
        _ui.TextIn(new Rect(side, y, Math.Max(0, area.Right - side), 1), "DRR dB", theme.Dim, 1);
        Number(side, y + 1, Math.Max(0, area.Right - side) + 2, m.DirectToReverberantDb, 1);
        // The echogram: energy per column, in dB below its peak, over the decay. Beside the table when there's room, else below.
        float span = Math.Clamp(m.T30.Mid * 1.1f, 0.15f, m.Mid.Length * AcousticMeasurement.BinSeconds);
        float right = side + 10;
        if (area.Right - right >= 30)
        {
            _ui.TextIn(new Rect(right, y, area.Right - right, 1), SpanFormat.Write(buffer, $"echogram · {span * 1000:0} ms · 60 dB"), theme.Dim);
            Echogram(new Rect(right, y + 1, area.Right - right, Math.Clamp(area.Bottom - y - 1, 1, 6)), m, span);
            return;
        }
        y += 4;
        if (y + 2 > area.Bottom) return;
        _ui.TextIn(new Rect(area.X, y, area.W, 1), SpanFormat.Write(buffer, $"echogram · {span * 1000:0} ms · 60 dB"), theme.Dim);
        Echogram(new Rect(area.X, y + 1, area.W, Math.Clamp(area.Bottom - y - 1, 1, 6)), m, span);
    }

    private void Number(float x, float y, float width, float value, int decimals)
    {
        Span<char> buffer = stackalloc char[32];
        var text = decimals == 1 ? SpanFormat.Write(buffer, $"{value:0.0}") : SpanFormat.Write(buffer, $"{value:0.00}");
        _ui.TextIn(new Rect(x, y, width - 2, 1), text, _ui.Theme.Text, 1);
    }

    /// <summary>One column per time slice, rows in eighths: each column's energy (the direct sound's column accented), in dB below the loudest column.</summary>
    private void Echogram(Rect r, AcousticMeasurement m, float seconds)
    {
        if (r.H < 1 || r.W < 4) return;
        ReadOnlySpan<char> blocks = " ▁▂▃▄▅▆▇█";
        int columns = Math.Min((int)r.W, 512), levels = (int)r.H * 8;
        int bins = Math.Min(m.Mid.Length, (int)(seconds / AcousticMeasurement.BinSeconds));
        int directBin = (int)(m.DirectSeconds / AcousticMeasurement.BinSeconds);
        Span<double> energy = stackalloc double[columns];
        int directColumn = -1;
        double peak = 0;
        for (int c = 0; c < columns; c++)
        {
            int from = c * bins / columns, to = Math.Max(from + 1, (c + 1) * bins / columns);
            double e = 0;
            for (int i = from; i < to && i < bins; i++) e += m.Mid[i];
            if (directBin >= from && directBin < to) { e += m.DirectEnergy.Mid; directColumn = c; }
            energy[c] = e;
            peak = Math.Max(peak, e);
        }
        if (peak <= 0) return;
        for (int c = 0; c < columns; c++)
        {
            if (energy[c] <= 0) continue;
            double db = 10 * Math.Log10(energy[c] / peak);
            int level = Math.Clamp((int)Math.Round((db + 60) / 60 * levels), 0, levels);
            var color = c == directColumn ? _ui.Theme.Accent : _ui.Theme.Dim;
            for (int row = 0; row < r.H; row++)
            {
                int fill = Math.Clamp(level - row * 8, 0, 8);
                if (fill > 0) _ui.Draw.Glyph(r.X + c, r.Bottom - 1 - row, blocks[fill], color);
            }
        }
    }

    /// <summary>The source (selected entity, or the first positional source) and the listener (AudioListener, or the Scene camera).</summary>
    private bool AcousticEnds(out Vector3 source, out Matrix4x4 listener, out string sourceName, out string listenerName, out AudioEmitter emitter, out Gamegrl.AssetRef<AudioAsset> clip)
    {
        var world = _app.World;
        source = default; listener = Matrix4x4.Identity; sourceName = listenerName = ""; emitter = AudioEmitter.Default; clip = default;
        Span<char> label = stackalloc char[64];
        var selected = Document.Selected;
        Entity from = world.IsAlive(selected) && world.Has<LocalToWorld>(selected) && !world.Has<AudioListener>(selected) ? selected : default;
        if (from.IsNull)
            foreach (var a in world.Query(new QueryDesc().With<AudioSource>().With<LocalToWorld>()).Archetypes)
            {
                var sources = a.GetSpan<AudioSource>();
                for (int i = 0; i < sources.Length && from.IsNull; i++) if (sources[i].Positional) from = a.Entities[i];
            }
        if (from.IsNull) { _acousticsStatus = "select the entity the sound comes from"; return false; }
        source = world.Get<LocalToWorld>(from).Value.Translation;
        sourceName = new string(label[..EntityLabel(world, from, label)]);
        if (world.Has<AudioSource>(from)) { emitter = world.Get<AudioSource>(from).Emitter; clip = world.Get<AudioSource>(from).Clip; }

        bool found = false;
        foreach (var a in world.Query(new QueryDesc().With<AudioListener>().With<LocalToWorld>()).Archetypes)
        {
            if (a.Count == 0) continue;
            listener = a.GetSpan<LocalToWorld>()[0].Value;
            listenerName = new string(label[..EntityLabel(world, a.Entities[0], label)]);
            found = true;
            break;
        }
        if (!found)
        {
            Matrix4x4.Invert(Matrix4x4.CreateLookAt(_camera.Position, _camera.Target, Vector3.UnitY), out listener);
            listenerName = "scene camera";
        }
        if (Vector3.Distance(source, listener.Translation) < 0.05f) { _acousticsStatus = "the source is at the listener"; return false; }
        return true;
    }

    private AudioEnvironment Environment() => _app.World.HasResource<AudioEnvironment>() ? _app.World.Resource<AudioEnvironment>() : AudioEnvironment.Default;

    private void MeasureAcoustics()
    {
        if (_measuring || !AcousticEnds(out var source, out var listener, out var sourceName, out var listenerName, out var emitter, out var clip)) return;
        var builder = new AcousticSceneBuilder();
        AcousticGeometry.Gather(_app.World, builder, _assets, _acousticMeshes);
        if (builder.Count == 0) { _acousticsStatus = "no acoustic surfaces in the scene"; return; }
        var env = Environment();
        var options = MeasureOptions.Default with { TemperatureC = env.TemperatureC, HumidityPercent = env.HumidityPercent };
        _measuring = true;
        _acousticsStatus = "measuring…";
        Jobs.Run("Measure acoustics", stop =>
        {
            var scene = builder.Build();
            return (Scene: scene, Measurement: AcousticMeasurement.Measure(scene, source, listener.Translation, options, stop));
        }, result =>
        {
            _measuredScene = result.Scene;
            _measurement = result.Measurement;
            _measuredSource = source; _measuredListener = listener;
            _measuredSourceName = sourceName; _measuredListenerName = listenerName;
            _measuredEmitter = emitter; _measuredClip = clip;
            _acousticsStatus = "";
            if (_pendingAudition >= 0) { int sound = _pendingAudition; _pendingAudition = -1; Audition(sound); }
        }, () => _measuring = false);
    }

    /// <summary>The engine to play auditions on: the game's when it has audio, otherwise one the editor opens on the default device.</summary>
    private AudioEngine AuditionEngine()
    {
        if (_app.World.HasResource<AudioContext>()) return _app.World.Resource<AudioContext>().Engine;
        if (_auditionEngine is null)
        {
            IAudioDevice device;
            try { device = AlsaDevice.IsAvailable ? AlsaDevice.Open(AlsaDeviceOptions.Default) : new NullAudioDevice(); }
            catch (AudioDeviceException e) { Log.Warn("acoustics: " + e.Message + "; auditions are silent"); device = new NullAudioDevice(); }
            _auditionEngine = AudioEngine.Start(device);
        }
        return _auditionEngine;
    }

    /// <summary>Renders a sound through the measured space and plays it (measuring first if there's nothing measured yet).</summary>
    private void Audition(int sound)
    {
        if (_measurement is null || _measuredScene is null)
        {
            if (!_measuring) { _pendingAudition = sound; MeasureAcoustics(); if (!_measuring) _pendingAudition = -1; }
            return;
        }
        var engine = AuditionEngine();
        AudioClip? dry = sound switch
        {
            0 => TestSounds.Clap(engine.SampleRate),
            1 => TestSounds.Gunshot(engine.SampleRate),
            _ => SourceClip(),
        };
        if (dry is null) { _acousticsStatus = "the source has no clip"; return; }
        var setup = Setup(engine) with { Dry = _auditionDry };
        _acousticsStatus = "rendering…";
        Jobs.Run("Render through the space", _ => Auralizer.Render(dry, setup), clip =>
        {
            engine.Stop(_auditionVoice);
            _auditionVoice = engine.Play(clip, PlayOptions.Default with { Priority = 1000 });
            _acousticsStatus = (_auditionDry ? "dry " : "") + dry.Name;
        });
    }

    /// <summary>
    /// How to render for the device: when its engine already renders for headphones, a speaker bed it will turn binaural;
    /// with "headphones" on a stereo device, binaural directly; otherwise its speakers.
    /// </summary>
    private AuralizeSetup Setup(AudioEngine engine)
    {
        var m = _measurement!;
        bool engineBinaural = engine.Headphones;
        return AuralizeSetup.For(_measuredScene!, _measuredListener, _measuredSource) with
        {
            Emitter = _measuredEmitter, Environment = Environment() with { Hdr = false }, SampleRate = engine.SampleRate,
            Output = engineBinaural ? engine.BedLayout : engine.Layout, Headphones = !engineBinaural && _auditionPhones && engine.Layout.Count == 2,
            Reverb = m.Reverb, TailSeconds = Math.Clamp(m.T30.Low * 1.2f, 0.5f, 4),
        };
    }

    private AudioClip? SourceClip()
    {
        if (_measuredClip.IsNone) return null;
        if (_app.World.HasResource<AudioContext>()) return _app.World.Resource<AudioContext>().Clip(_measuredClip);
        return _assets.TryRead(_measuredClip.Guid, out var kind, out var bytes) && kind == AssetKind.Audio ? Wav.Read(bytes.Span, "source") : null;
    }

    /// <summary>Writes the measured impulse response (binaural with "headphones", stereo otherwise) under the project's Library folder.</summary>
    private void ExportImpulseResponse()
    {
        if (_measurement is null || _measuredScene is null) { _acousticsStatus = "measure first"; return; }
        var setup = Setup(AuditionEngine()) with { Output = ChannelLayout.Stereo, Headphones = _auditionPhones, SampleRate = 48000, Dry = false };
        string path = Path.Combine(_root, "Library", "Acoustics", $"impulse-response{(_auditionPhones ? "-binaural" : "")}.wav");
        Jobs.Run("Export impulse response", _ =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Wav.Write(path, Auralizer.ImpulseResponse(setup));
            return path;
        }, written => { _acousticsStatus = "wrote " + Path.GetRelativePath(_root, written); Log.Info("acoustics: wrote " + written); });
    }

    /// <summary>
    /// The acoustic geometry and the measured paths in the Scene view (while the Acoustics panel is showing): surfaces
    /// as wireframes coloured by hardness (cool and hard, warm and soft); the direct line (red when walls are in the
    /// way); reflections by order.
    /// </summary>
    private void DrawAcousticPaths()
    {
        if (!_showPaths || (Docks.Selected(DockRegion.Bottom) != "acoustics" && Docks.Selected(DockRegion.Center) != "acoustics")) return;
        foreach (var a in _app.World.Query(new QueryDesc().With<AcousticSurface>().With<LocalToWorld>()).Archetypes)
        {
            var surfaces = a.GetSpan<AcousticSurface>();
            var transforms = a.GetSpan<LocalToWorld>();
            for (int i = 0; i < surfaces.Length; i++)
            {
                if (surfaces[i].Shape != AcousticShape.Box) continue;
                float soft = Math.Clamp(surfaces[i].Material.Absorption.Mid * 1.5f, 0, 1);
                _render.Debug.Box(transforms[i].Value, new Vector3(-0.5f), new Vector3(0.5f), new Vector4(0.45f + 0.5f * soft, 0.6f, 0.95f - 0.6f * soft, 1));
            }
        }
        var m = _measurement;
        if (m is null) return;
        var listener = _measuredListener.Translation;
        var source = _measuredSource;
        var debug = _render.Debug;
        var white = new Vector4(0.9f, 0.9f, 0.9f, 1);
        debug.Line(source, listener, m.DirectTransmission.Mid >= 0.999f ? white : new Vector4(1, 0.3f, 0.3f, 1));
        foreach (ref readonly var p in m.Paths.AsSpan())
        {
            var color = p.Order == 1 ? new Vector4(0.35f, 0.8f, 1, 1) : new Vector4(0.6f, 0.45f, 0.9f, 1);
            debug.Line(source, p.First, color);
            if (p.Order > 1) debug.Line(p.First, p.Point, color);
            debug.Line(p.Point, listener, color);
        }
        Cross(source, new Vector4(1, 0.37f, 0.64f, 1));
        Cross(listener, new Vector4(0.4f, 0.9f, 0.5f, 1));
        void Cross(Vector3 at, Vector4 color)
        {
            const float s = 0.25f;
            debug.Line(at - Vector3.UnitX * s, at + Vector3.UnitX * s, color);
            debug.Line(at - Vector3.UnitY * s, at + Vector3.UnitY * s, color);
            debug.Line(at - Vector3.UnitZ * s, at + Vector3.UnitZ * s, color);
        }
    }
}
