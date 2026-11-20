using System.Numerics;
using Gamegrl;
using Gamegrl.Assets;
using Gamegrl.Audio;
using Gamegrl.Audio.Acoustics;
using Gamegrl.Platform;
using Gamegrl.Rendering;
using Gamegrl.UI;

namespace CaveGunfight;

/// <summary>The listener: a first-person walker with a gun.</summary>
public struct Player : IComponent
{
    public float Yaw, Pitch;
}

/// <summary>Someone shooting back: a burst of <see cref="Burst"/> shots every few seconds.</summary>
public struct Shooter : IComponent
{
    public float Next, Gap;
    public int Burst, Left;
    public float Pitch;
}

/// <summary>The cave's sounds, registered with the audio context at start.</summary>
public struct CaveSounds
{
    public AssetRef<AudioAsset> Gun, Wind;
    public AssetRef<AudioAsset> Drip0, Drip1, Drip2;
}

/// <summary>Timers and a seeded random generator (no allocations, no clocks).</summary>
public struct CaveState
{
    public uint Seed;
    public float NextDrip;
    public bool Acoustics;
    public float Clock;
    /// <summary>Quit after this many seconds (0: never), for smoke runs.</summary>
    public double QuitAfter;
    public uint Random() { Seed ^= Seed << 13; Seed ^= Seed >> 17; Seed ^= Seed << 5; return Seed; }
    public float Range(float a, float b) => a + (b - a) * ((Random() >> 8) * (1f / 16777216f));
}

/// <summary>
/// Captures of the audio engine's state (<see cref="AudioCapture"/>) for examining afterwards: F12 takes one, and
/// <see cref="At"/> schedules them (seconds since start, or into the tour). Each goes into its own folder under
/// <see cref="Root"/>.
/// </summary>
public sealed class CaveCaptures
{
    public string Root = Path.Combine("artifacts", "captures");
    public double[] At = [];
    public int Next;
    /// <summary>The last capture's folder and when it was taken (for the HUD).</summary>
    public string? Last;
    public double LastTime = double.NegativeInfinity;
}

/// <summary>The scripted walk the --wav recording takes: where the player is and where it looks, over time.</summary>
public struct Tour
{
    public bool Active;
    public int NextShot;
}

/// <summary>
/// A cave to hear: outside on gravel at the foot of a cliff, a 6 m tunnel into it, and a 40 × 16 × 40 m rock chamber
/// with a pillar and a pool. Someone fires from deep in the chamber (behind the pillar from the tunnel, so heard round
/// it, and from outside heard as the chamber ringing out of the tunnel mouth); someone fires out in the open; water drips
/// in the chamber; wind moves across the mouth. You walk and shoot; R switches the room acoustics off and on.
/// </summary>
public sealed class CaveModule(bool tour = false, double quitAfter = 0, CaveCaptures? captures = null) : IModule
{
    public CaveModule() : this(false) { }

    public static readonly CaveSounds Clips = new()
    {
        Gun = new AssetRef<AudioAsset>(new Guid("c4e00000000000000000000000000001")),
        Wind = new AssetRef<AudioAsset>(new Guid("c4e00000000000000000000000000002")),
        Drip0 = new AssetRef<AudioAsset>(new Guid("c4e00000000000000000000000000003")),
        Drip1 = new AssetRef<AudioAsset>(new Guid("c4e00000000000000000000000000004")),
        Drip2 = new AssetRef<AudioAsset>(new Guid("c4e00000000000000000000000000005")),
    };

    public void Build(EngineBuilder builder)
    {
        if (!builder.HasModule<AudioModule>()) builder.AddModule<AudioModule>();
        if (!builder.World.HasResource<Input>()) builder.SetResource(new Input());
        builder.SetResource(Clips);
        builder.SetResource(new CaveState { Seed = 0x1234567u, NextDrip = 0.5f, Acoustics = true, QuitAfter = quitAfter });
        builder.SetResource(new Tour { Active = tour });
        builder.SetResource(captures ?? new CaveCaptures());
        Gamegrl.Generated.CaveGunfightGameSystems.AddTo(builder.Schedule);
        builder.OnStart(Start);
    }

    private static void Start(App app)
    {
        var world = app.World;
        var audio = world.Resource<AudioContext>();
        int rate = audio.Engine.SampleRate;
        audio.Register(Clips.Gun, TestSounds.Gunshot(rate));
        audio.Register(Clips.Wind, Sounds.Wind(rate));
        audio.Register(Clips.Drip0, Sounds.Drip(rate, 0));
        audio.Register(Clips.Drip1, Sounds.Drip(rate, 1));
        audio.Register(Clips.Drip2, Sounds.Drip(rate, 2));
        // Gunfights want the space back quickly after a shot: release the HDR window faster than the default.
        world.Resource<AudioEnvironment>().HdrReleaseDbPerSecond = 18;
        // Always ready for a capture (F12): the last 12 s of output and every voice's state.
        audio.Engine.KeepHistory(12);
        audio.Diagnostics.Enabled = true;
        Level.Build(world);
    }
}

/// <summary>The cave's geometry: rock masses as acoustic boxes, each drawn as a cube with the same transform.</summary>
public static class Level
{
    public static readonly Vector4 RockColor = new(0.36f, 0.31f, 0.27f, 1), CeilingColor = new(0.22f, 0.2f, 0.19f, 1),
        GravelColor = new(0.45f, 0.43f, 0.38f, 1), FloorColor = new(0.3f, 0.27f, 0.24f, 1), WaterColor = new(0.12f, 0.28f, 0.36f, 1);

    public static void Build(World world)
    {
        // Floors: gravel outside the cliff (z > 2), rock inside.
        Mass(world, new Vector3(0, -1, 51), new Vector3(200, 2, 98), AcousticMaterial.Gravel, GravelColor);
        Mass(world, new Vector3(0, -1, -49), new Vector3(200, 2, 102), AcousticMaterial.Rock, FloorColor);
        // The cliff face, z ∈ [0, 4], 14 m high, with the tunnel mouth (6 m wide, 6 m high) at x = 0.
        Mass(world, new Vector3(-41.5f, 7, 2), new Vector3(77, 14, 4), AcousticMaterial.Rock, RockColor);
        Mass(world, new Vector3(41.5f, 7, 2), new Vector3(77, 14, 4), AcousticMaterial.Rock, RockColor);
        Mass(world, new Vector3(0, 10, 2), new Vector3(6, 8, 4), AcousticMaterial.Rock, RockColor);
        // The hill behind it: the tunnel, z ∈ [−24, 0], and the chamber are dug into this.
        Mass(world, new Vector3(-16.5f, 10, -12), new Vector3(27, 20, 24), AcousticMaterial.Rock, RockColor);
        Mass(world, new Vector3(16.5f, 10, -12), new Vector3(27, 20, 24), AcousticMaterial.Rock, RockColor);
        Mass(world, new Vector3(0, 13, -12), new Vector3(6, 14, 24), AcousticMaterial.Rock, CeilingColor);
        // The chamber: x ∈ [−20, 20], y ∈ [0, 16], z ∈ [−64, −24].
        Mass(world, new Vector3(-30, 11, -44), new Vector3(20, 22, 40), AcousticMaterial.Rock, RockColor);
        Mass(world, new Vector3(30, 11, -44), new Vector3(20, 22, 40), AcousticMaterial.Rock, RockColor);
        Mass(world, new Vector3(0, 11, -74), new Vector3(80, 22, 20), AcousticMaterial.Rock, RockColor);
        Mass(world, new Vector3(0, 19, -44), new Vector3(40, 6, 40), AcousticMaterial.Rock, CeilingColor);
        // A pillar between the tunnel and the far shooter, and a pool under the drips.
        Mass(world, new Vector3(-4, 8, -43), new Vector3(4, 16, 6), AcousticMaterial.Rock, RockColor);
        Mass(world, new Vector3(10, 0.025f, -54), new Vector3(10, 0.05f, 12), AcousticMaterial.Water, WaterColor);
        // Outside: a few boulders.
        Mass(world, new Vector3(-12, 1.5f, 22), new Vector3(4, 3, 3), AcousticMaterial.Rock, RockColor);
        Mass(world, new Vector3(14, 2, 30), new Vector3(5, 4, 4), AcousticMaterial.Rock, RockColor);

        // The player starts outside, facing the cave.
        world.Spawn(LocalTransform.At(new Vector3(0, 1.7f, 18)), new LocalToWorld(), new Player(), AudioListener.Default,
            Camera.Perspective(70) with { ClearColor = new Vector4(0.42f, 0.55f, 0.7f, 1) }, new Name("Player"));
        // The shooters: deep in the chamber behind the pillar, and out in the field.
        world.Spawn(LocalTransform.At(new Vector3(-4, 1.5f, -54)), new LocalToWorld(), new Shooter { Next = 2.5f, Gap = 4, Burst = 3, Pitch = 1 },
            new MeshRenderer { Mesh = BuiltinAssets.Cube, Color = new Vector4(0.8f, 0.2f, 0.15f, 1) }, new Name("Chamber shooter"));
        world.Spawn(LocalTransform.At(new Vector3(26, 1.5f, 48)), new LocalToWorld(), new Shooter { Next = 5, Gap = 6, Burst = 1, Pitch = 0.92f },
            new MeshRenderer { Mesh = BuiltinAssets.Cube, Color = new Vector4(0.8f, 0.6f, 0.15f, 1) }, new Name("Field shooter"));
        // Wind across the mouth: a large, quiet source just outside it.
        world.Spawn(LocalTransform.At(new Vector3(0, 4, 6)), new LocalToWorld(), new Name("Wind"),
            AudioSource.At(CaveModule.Clips.Wind, loop: true) with { Emitter = AudioEmitter.Default with { LoudnessDb = 64, Radius = 6, MinDistance = 3 } });
    }

    private static void Mass(World world, Vector3 center, Vector3 size, AcousticMaterial material, Vector4 color) =>
        world.Spawn(new LocalTransform { Position = center, Rotation = Quaternion.Identity, Scale = size }, new LocalToWorld(),
            new MeshRenderer { Mesh = BuiltinAssets.Cube, Color = color }, AcousticSurface.Box(material));

    /// <summary>Where a point is, for the HUD.</summary>
    public static string Region(Vector3 p) => p.Z > 2 ? "outside" : p.Z > -24 ? "tunnel" : "chamber";
}

public static partial class Gameplay
{
    private const float GunLoudness = 140;

    /// <summary>WASD to walk, arrows or Q/E to turn, right-drag to look; walls stop you (rays against the acoustic scene).</summary>
    [System(Phase.Update)]
    private static void Walk(ref LocalTransform t, ref Player player, [Res] in Input input, [Res] in Time time, [Res] in AudioContext audio, [Res] in Tour tour)
    {
        if (tour.Active) return;
        float dt = MathF.Min(time.Delta, 0.1f);
        float turn = (input.IsDown(Key.Left) || input.IsDown(Key.Q) ? 1 : 0) - (input.IsDown(Key.Right) || input.IsDown(Key.E) ? 1 : 0);
        player.Yaw += turn * 1.8f * dt;
        if (input.IsDown(MouseButton.Right)) { player.Yaw -= input.MouseDelta.X * 0.004f; player.Pitch -= input.MouseDelta.Y * 0.004f; }
        float look = (input.IsDown(Key.Up) ? 1 : 0) - (input.IsDown(Key.Down) ? 1 : 0);
        player.Pitch = Math.Clamp(player.Pitch + look * 1.2f * dt, -1.2f, 1.2f);
        var forward = new Vector3(-MathF.Sin(player.Yaw), 0, -MathF.Cos(player.Yaw));
        var right = new Vector3(MathF.Cos(player.Yaw), 0, -MathF.Sin(player.Yaw));
        var move = forward * ((input.IsDown(Key.W) ? 1 : 0) - (input.IsDown(Key.S) ? 1 : 0)) + right * ((input.IsDown(Key.D) ? 1 : 0) - (input.IsDown(Key.A) ? 1 : 0));
        if (move.LengthSquared() > 0)
        {
            move = Vector3.Normalize(move) * (input.Shift ? 9 : 4.5f) * dt;
            // Slide along whatever is in the way, at knee and eye height.
            for (int pass = 0; pass < 2; pass++)
                foreach (float height in (ReadOnlySpan<float>)[-1.2f, 0])
                {
                    var from = t.Position + new Vector3(0, height, 0);
                    float length = move.Length();
                    if (length > 1e-6f && audio.Scene.Raycast(from, move / length, length + 0.4f, out var hit))
                    {
                        var n = new Vector3(hit.Normal.X, 0, hit.Normal.Z);
                        if (n.LengthSquared() > 1e-6f) { n = Vector3.Normalize(n); move -= n * Vector3.Dot(move, n); }
                    }
                }
            t.Position += move;
        }
        t.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, player.Yaw) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, player.Pitch);
    }

    /// <summary>Space or left click fires; R switches the room acoustics (reverb, reflections, diffraction) off and on.</summary>
    [System(Phase.Update)]
    private static void Fire(Entity self, in Player player, [Res] in Input input, [Res] in AudioContext audio, [Res] in CaveSounds sounds,
        [Res] ref CaveState state, [Res] ref AudioEnvironment env, [Res] in Tour tour, [Res] in AppControl control, [Res] in Time time)
    {
        if (input.WasPressed(Key.Escape) || (state.QuitAfter > 0 && time.Elapsed >= state.QuitAfter)) control.QuitRequested = true;
        if (input.WasPressed(Key.R))
        {
            state.Acoustics = !state.Acoustics;
            env.Reverb = env.Reflections = env.Diffraction = env.RemoteReverb = state.Acoustics;
        }
        if (tour.Active) return;
        if (input.WasPressed(Key.Space) || input.WasPressed(MouseButton.Left)) Fire(audio, sounds, self, ref state);
    }

    /// <summary>Where the player's gun is, in the player's space: in the right hand, half a metre ahead.</summary>
    public static readonly Vector3 Muzzle = new(0.2f, -0.3f, -0.6f);

    /// <summary>The player's own shot: it moves with the player (and so with the listener) while it rings out.</summary>
    public static void Fire(AudioContext audio, in CaveSounds sounds, Entity player, ref CaveState state) =>
        audio.PlayOn(player, sounds.Gun, Muzzle, Gun, PlayOptions.Default with { Pitch = state.Range(0.96f, 1.04f) });

    private static AudioEmitter Gun => AudioEmitter.Default with { LoudnessDb = GunLoudness, MaxDistance = 2000 };

    /// <summary>A gunshot at a point: 140 dB at a metre, a touch of random pitch.</summary>
    public static void Shoot(AudioContext audio, in CaveSounds sounds, Vector3 at, ref CaveState state, float pitch) =>
        audio.PlayAt(sounds.Gun, at, Gun, PlayOptions.Default with { Pitch = pitch * state.Range(0.96f, 1.04f) });

    /// <summary>F12, or a scheduled time, writes a capture: the last seconds of output and what every voice is doing.</summary>
    [System(Phase.Update)]
    private static void Captures(in LocalToWorld body, in Player player, [Res] in Input input, [Res] in Time time, [Res] in AudioContext audio,
        [Res] in CaveCaptures captures, [Res] in CaveState state, [Res] in Tour tour)
    {
        double clock = tour.Active ? state.Clock : time.Elapsed;
        bool scheduled = captures.Next < captures.At.Length && clock >= captures.At[captures.Next];
        if (!scheduled && !input.WasPressed(Key.F12)) return;
        if (scheduled) captures.Next++;
        var at = body.Value.Translation;
        string folder = Path.GetFullPath(Path.Combine(captures.Root, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture)));
        string note = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Cave Gunfight capture ({(scheduled ? "scheduled" : "F12")}) at {clock:0.00} s{(tour.Active ? " into the tour" : "")}: {Level.Region(at)}, room acoustics {(state.Acoustics ? "on" : "off")}");
        AudioCapture.Write(folder, audio, note);
        captures.Last = folder;
        captures.LastTime = time.Elapsed;
        Console.WriteLine($"Captured to {folder}");
    }

    /// <summary>The shooters fire their bursts, a tenth of a second apart, every few seconds.</summary>
    [System(Phase.Update)]
    private static void Shooters(in LocalToWorld body, ref Shooter shooter, [Res] in Time time, [Res] in AudioContext audio, [Res] in CaveSounds sounds, [Res] ref CaveState state)
    {
        shooter.Next -= time.Delta;
        if (shooter.Next > 0) return;
        if (shooter.Left == 0) shooter.Left = shooter.Burst;
        Shoot(audio, sounds, body.Value.Translation, ref state, shooter.Pitch);
        shooter.Left--;
        shooter.Next = shooter.Left > 0 ? 0.11f : shooter.Gap * state.Range(0.8f, 1.3f);
    }

    /// <summary>Water drips into the pool: a quiet plink every so often, somewhere over it.</summary>
    [System(Phase.Update)]
    private static void Drips([Res] in Time time, [Res] in AudioContext audio, [Res] in CaveSounds sounds, [Res] ref CaveState state)
    {
        state.NextDrip -= time.Delta;
        if (state.NextDrip > 0) return;
        state.NextDrip = state.Range(0.25f, 1.1f);
        var at = new Vector3(state.Range(6, 14), 0.2f, state.Range(-59, -49));
        uint pick = state.Random() % 3;
        var clip = pick == 0 ? sounds.Drip0 : pick == 1 ? sounds.Drip1 : sounds.Drip2;
        audio.PlayAt(clip, at, AudioEmitter.Default with { LoudnessDb = 58, MinDistance = 0.5f }, PlayOptions.Default with { Pitch = state.Range(0.9f, 1.15f) });
    }

    /// <summary>
    /// The --wav recording's walk: from the field, through the tunnel and into the chamber, shooting on the way; then
    /// back to the tunnel mouth to hear the chamber from outside.
    /// </summary>
    [System(Phase.Update)]
    private static void Walkthrough(Entity self, ref LocalTransform t, ref Player player, [Res] in Time time, [Res] in AudioContext audio, [Res] in CaveSounds sounds,
        [Res] ref CaveState state, [Res] ref Tour tour)
    {
        if (!tour.Active) return;
        state.Clock += time.Delta;
        float s = state.Clock;
        ReadOnlySpan<Vector4> path = // x, z, yaw, time
        [
            new(0, 18, 0, 0), new(0, 10, 0, 6), new(0, -2, 0, 12), new(0, -20, 0, 18), new(2, -34, 0.3f, 24), new(6, -44, 0.6f, 30),
            new(4, -40, 3.1f, 33), new(0, -18, 3.14f, 39), new(0, 8, 3.14f, 46), new(0, 12, 3.14f, 52),
        ];
        int k = 0;
        while (k < path.Length - 2 && s > path[k + 1].W) k++;
        var a = path[k]; var b = path[k + 1];
        float u = Math.Clamp((s - a.W) / (b.W - a.W), 0, 1);
        u = u * u * (3 - 2 * u);
        t.Position = new Vector3(a.X + (b.X - a.X) * u, 1.7f, a.Y + (b.Y - a.Y) * u);
        player.Yaw = a.Z + (b.Z - a.Z) * u;
        t.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, player.Yaw);
        ReadOnlySpan<float> shots = [3, 9, 15, 21.5f, 22, 27, 31, 35];
        if (tour.NextShot < shots.Length && s >= shots[tour.NextShot])
        {
            tour.NextShot++;
            Fire(audio, sounds, self, ref state);
        }
    }
}

/// <summary>
/// The windowed player's heads-up display: where you are and what the audio engine is doing. F1 adds the acoustic
/// overlay: in the scene, the reverb estimate's sample rays (amber where they hit, blue where they escape to open sky),
/// each voice's direct line (green clear, red blocked), the path round an edge (magenta) and its reflections (yellow);
/// on screen, the decay curves and the voice list.
/// </summary>
public sealed class CaveHudModule(bool overlay = false) : IModule
{
    public CaveHudModule() : this(false) { }

    public void Build(EngineBuilder builder)
    {
        builder.AddModule(new UiModule());
        builder.AddSystem(new Hud { Overlay = overlay });
    }

    private sealed class Hud : ISystem
    {
        private Query _players = null!;
        public bool Overlay;
        public string Name => "Cave.Hud";
        public Phase Phase => Phase.Update;
        public int Order => 100;
        public void Initialize(World world) => _players = world.Query(new QueryDesc().With<Player>().With<LocalToWorld>());

        private static readonly Color32 Ink = Color32.Hex(0xf2e6d0), Dim = Color32.Hex(0xb8ab94), Warn = Color32.Hex(0xffb070);

        public void Run(World world)
        {
            var ui = world.Resource<Ui>();
            var audio = world.Resource<AudioContext>();
            var state = world.Resource<CaveState>();
            var input = world.Resource<Input>();
            var captures = world.Resource<CaveCaptures>();
            var time = world.Resource<Time>();
            if (input.WasPressed(Key.F1)) Overlay = !Overlay;
            var position = Vector3.Zero;
            foreach (var a in _players.Archetypes) if (a.Count > 0) position = a.GetSpan<LocalToWorld>()[0].Value.Translation;
            Span<char> line = stackalloc char[200];
            float y = 10;
            void Text(ReadOnlySpan<char> s, Color32 c) { ui.TextAt(new Vector2(12, y), s, c); y += 18; }
            Text("CAVE GUNFIGHT", Ink);
            Text("WASD walk  ·  arrows / Q E turn  ·  right-drag look  ·  Space / click fire  ·  R acoustics  ·  F1 overlay  ·  F12 capture  ·  Esc quit", Dim);
            Text(SpanFormat.Write(line, $"{Level.Region(position)}  ·  room acoustics {(state.Acoustics ? "on" : "OFF")}"), state.Acoustics ? Ink : Warn);
            var r = audio.Reverb;
            Text(r.IsSilent ? "reverb: none" : SpanFormat.Write(line, $"reverb EDT {r.DecaySeconds.Low:0.00} / {r.DecaySeconds.Mid:0.00} / {r.DecaySeconds.High:0.00} s (late T30 {r.LateDecaySeconds.Mid:0.0} s)  ·  level {Db(r.Level):0} dB  ·  {r.Volume:0} m³  ·  enclosed {r.Enclosure * 100:0}%"), Dim);
            var remote = audio.RemoteReverb;
            Text(audio.HiddenInThisRoom ? "hidden source in this room: it fills this room's reverb"
                : remote.IsSilent ? "other room: none" : SpanFormat.Write(line, $"other room heard through an opening: EDT {remote.DecaySeconds.Mid:0.0} s"), Dim);
            Text(SpanFormat.Write(line, $"HDR top {audio.Hdr.TopDb:0} dB  ·  voices {audio.Engine.RealVoices} rendered of {audio.Engine.ActiveVoices}  ·  {1 / MathF.Max(time.Delta, 1e-4f):0} fps"), Dim);
            if (captures.Last is not null && time.Elapsed - captures.LastTime < 6) Text(SpanFormat.Write(line, $"captured to {captures.Last}"), Warn);
            var ctx = world.HasResource<RenderContext>() ? world.Resource<RenderContext>() : null;
            if (ctx is not null) ctx.MainCameraDebugLines = Overlay;
            if (!Overlay) return;
            var diagnostics = audio.Diagnostics;
            if (ctx is not null) Scene(ctx.Debug, diagnostics);
            Voices(ui, diagnostics, y + 8);
            Curves(ui, diagnostics);
        }

        private static float Db(float amplitude) => 20 * MathF.Log10(MathF.Max(amplitude, 1e-9f));

        /// <summary>Rays and paths in the world.</summary>
        private static void Scene(DebugDraw draw, AudioDiagnostics d)
        {
            if (d.Reverb is { } trace)
                foreach (var segment in trace.Segments)
                {
                    float fade = 1f / (1 + segment.Bounce);
                    var color = segment.Escaped ? new Vector4(0.35f, 0.7f, 1f, 0.9f) : new Vector4(1f, 0.62f, 0.2f, 0.25f + 0.6f * fade);
                    draw.Line(segment.A, segment.B, color);
                }
            // Lines from the listener start a little below the eye, so they're seen from it.
            var ear = d.Listener.Translation - new Vector3(0, 0.25f, 0);
            foreach (var v in d.Voices)
            {
                Cross(draw, v.Position, 0.3f, new Vector4(1, 1, 1, 1));
                draw.Line(ear, v.Position, v.Transmission >= 0.999f ? new Vector4(0.3f, 1f, 0.4f, 1) : new Vector4(0.8f, 0.15f, 0.1f, 0.8f));
                if (v.Diffracted)
                {
                    var p = v.Diffraction;
                    var magenta = new Vector4(1, 0.3f, 1, 1);
                    draw.Line(ear, p.First, magenta);
                    if (p.Edges > 1) draw.Line(p.First, p.Second, magenta);
                    draw.Line(p.Edges > 1 ? p.Second : p.First, v.Position, magenta);
                }
                for (int k = 0; k < v.PathCount; k++)
                {
                    var path = d.Paths[v.PathStart + k];
                    var yellow = new Vector4(1, 0.95f, 0.3f, 0.35f + 0.6f * path.Surfaces.Mid / path.Order);
                    draw.Line(ear, path.Point, yellow);
                    if (path.Order > 1) draw.Line(path.Point, path.First, yellow);
                    draw.Line(path.First, v.Position, yellow);
                }
            }
            if (d.RemoteActive) Cross(draw, d.RemoteOpening, 1f, new Vector4(0.3f, 1f, 1f, 1));
            // Where the reverb now playing was estimated: it trails the listener by the estimate's latency.
            if (d.Reverb is { } at) Cross(draw, at.Listener - new Vector3(0, 0.25f, 0), 0.4f, new Vector4(1f, 0.62f, 0.2f, 1));
        }

        private static void Cross(DebugDraw draw, Vector3 p, float size, Vector4 color)
        {
            draw.Line(p - new Vector3(size, 0, 0), p + new Vector3(size, 0, 0), color);
            draw.Line(p - new Vector3(0, size, 0), p + new Vector3(0, size, 0), color);
            draw.Line(p - new Vector3(0, 0, size), p + new Vector3(0, 0, size), color);
        }

        /// <summary>Each positional voice: what it is, how far, how loud, how it's heard.</summary>
        private static void Voices(Ui ui, AudioDiagnostics d, float y)
        {
            Span<char> line = stackalloc char[200];
            ui.TextAt(new Vector2(12, y), "voice           dist    loud    gain   blocked  taps  round", Ink); y += 18;
            int shown = 0;
            foreach (var v in d.Voices)
            {
                if (shown++ == 14) break;
                float distance = Vector3.Distance(v.Position, d.Listener.Translation);
                string name = v.Clip?.Name ?? "?";
                line.TryWrite(System.Globalization.CultureInfo.InvariantCulture,
                    $"{name,-14} {distance,5:0.0} m {v.LoudnessDb,5:0} dB {Db(v.Spatial.Gain),5:0} dB  {1 - v.Transmission,5:0.00}  {v.TapCount,4}  {(v.Diffracted ? (v.Diffraction.Edges == 1 ? "1 edge" : "2 edges") : "")}{(v.Rendered ? "" : "  (virtual)")}",
                    out int written);
                ui.TextAt(new Vector2(12, y), line[..written], v.Rendered ? Dim : Warn);
                y += 18;
            }
        }

        /// <summary>The decay curves (mid band solid, low and high faint): 0 to −60 dB over two seconds.</summary>
        private static void Curves(Ui ui, AudioDiagnostics d)
        {
            const float W = 420, H = 170, Seconds = 2, Range = 60;
            var screen = ui.Screen;
            var origin = new Vector2(screen.X + screen.W - W - 16, screen.Y + screen.H - H - 28);
            var draw = ui.Target;
            draw.Rect(new Rect(origin.X - 8, origin.Y - 22, W + 16, H + 44), new Color32(0, 0, 0, 150));
            ui.TextAt(new Vector2(origin.X, origin.Y - 20), "energy decay  (amber: here  ·  cyan: other room)  0 to -60 dB, 2 s", Ink);
            for (int k = 0; k <= 6; k++)
            {
                float gy = origin.Y + H * k / 6f;
                draw.Line(new Vector2(origin.X, gy), new Vector2(origin.X + W, gy), new Color32(255, 255, 255, (byte)(k == 1 ? 90 : 30)));
            }
            for (int k = 0; k <= 4; k++)
            {
                float gx = origin.X + W * k / 4f;
                draw.Line(new Vector2(gx, origin.Y), new Vector2(gx, origin.Y + H), new Color32(255, 255, 255, 30));
            }
            Span<char> label = stackalloc char[8];
            ui.TextAt(new Vector2(origin.X, origin.Y + H + 4), "0", Dim);
            ui.TextAt(new Vector2(origin.X + W - 20, origin.Y + H + 4), SpanFormat.Write(label, $"{Seconds:0} s"), Dim);
            Plot(d.Reverb, new Color32(255, 160, 50, 255));
            Plot(d.Remote, new Color32(80, 230, 255, 255));

            void Plot(ReverbTrace? trace, Color32 color)
            {
                if (trace is null) return;
                int bins = Math.Min(trace.Bins, (int)(Seconds / ReverbTrace.BinSeconds));
                for (int band = 0; band < 3; band++)
                {
                    var c = band == 1 ? color : new Color32(color.R, color.G, color.B, 70);
                    Vector2? previous = null;
                    for (int b = 0; b < bins; b++)
                    {
                        float db = trace.Decay[band * trace.Bins + b];
                        if (!float.IsFinite(db) || db < -Range) break;
                        var p = new Vector2(origin.X + W * b / (Seconds / ReverbTrace.BinSeconds), origin.Y - H * db / Range);
                        if (previous is { } q) draw.Line(q, p, c, band == 1 ? 2 : 1);
                        previous = p;
                    }
                }
            }
        }
    }
}
