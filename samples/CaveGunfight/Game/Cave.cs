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
public sealed class CaveModule(bool tour = false, double quitAfter = 0) : IModule
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
    private static void Fire(in LocalToWorld body, in Player player, [Res] in Input input, [Res] in AudioContext audio, [Res] in CaveSounds sounds,
        [Res] ref CaveState state, [Res] ref AudioEnvironment env, [Res] in Tour tour, [Res] in AppControl control, [Res] in Time time)
    {
        if (input.WasPressed(Key.Escape) || (state.QuitAfter > 0 && time.Elapsed >= state.QuitAfter)) control.QuitRequested = true;
        if (input.WasPressed(Key.R))
        {
            state.Acoustics = !state.Acoustics;
            env.Reverb = env.Reflections = env.Diffraction = env.RemoteReverb = state.Acoustics;
        }
        if (tour.Active) return;
        if (input.WasPressed(Key.Space) || input.WasPressed(MouseButton.Left)) Shoot(audio, sounds, body.Value.Translation, ref state, 1);
    }

    /// <summary>A gunshot at a point: 140 dB at a metre, a touch of random pitch.</summary>
    public static void Shoot(AudioContext audio, in CaveSounds sounds, Vector3 at, ref CaveState state, float pitch) =>
        audio.PlayAt(sounds.Gun, at, AudioEmitter.Default with { LoudnessDb = GunLoudness, MaxDistance = 2000 },
            PlayOptions.Default with { Pitch = pitch * state.Range(0.96f, 1.04f) });

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
    private static void Walkthrough(ref LocalTransform t, ref Player player, [Res] in Time time, [Res] in AudioContext audio, [Res] in CaveSounds sounds,
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
            Shoot(audio, sounds, t.Position, ref state, 1);
        }
    }
}

/// <summary>The windowed player's heads-up text: where you are and what the audio engine is doing.</summary>
public sealed class CaveHudModule : IModule
{
    public void Build(EngineBuilder builder)
    {
        builder.AddModule(new UiModule());
        builder.AddSystem(new Hud());
    }

    private sealed class Hud : ISystem
    {
        private Query _players = null!;
        public string Name => "Cave.Hud";
        public Phase Phase => Phase.Update;
        public int Order => 100;
        public void Initialize(World world) => _players = world.Query(new QueryDesc().With<Player>().With<LocalToWorld>());

        public void Run(World world)
        {
            var ui = world.Resource<Ui>();
            var audio = world.Resource<AudioContext>();
            var state = world.Resource<CaveState>();
            var position = Vector3.Zero;
            foreach (var a in _players.Archetypes) if (a.Count > 0) position = a.GetSpan<LocalToWorld>()[0].Value.Translation;
            var ink = Color32.Hex(0xf2e6d0); var dim = Color32.Hex(0xb8ab94); var warn = Color32.Hex(0xffb070);
            Span<char> line = stackalloc char[160];
            float y = 10;
            void Text(ReadOnlySpan<char> s, Color32 c) { ui.TextAt(new Vector2(12, y), s, c); y += 18; }
            Text("CAVE GUNFIGHT", ink);
            Text("WASD walk  ·  arrows / Q E turn  ·  right-drag look  ·  Space / click fire  ·  R acoustics  ·  Esc quit", dim);
            Text(SpanFormat.Write(line, $"{Level.Region(position)}  ·  room acoustics {(state.Acoustics ? "on" : "OFF")}"), state.Acoustics ? ink : warn);
            var r = audio.Reverb;
            Text(r.IsSilent ? "reverb: open air" : SpanFormat.Write(line, $"reverb {r.DecaySeconds.Low:0.0} / {r.DecaySeconds.Mid:0.0} / {r.DecaySeconds.High:0.0} s  ·  level {20 * MathF.Log10(MathF.Max(r.Level, 1e-6f)):0} dB  ·  {r.Volume:0} m³  ·  enclosed {r.Enclosure * 100:0}%"), dim);
            var remote = audio.RemoteReverb;
            Text(remote.IsSilent ? "other room: none" : SpanFormat.Write(line, $"other room heard through an opening: {remote.DecaySeconds.Mid:0.0} s"), dim);
            Text(SpanFormat.Write(line, $"HDR top {audio.Hdr.TopDb:0} dB  ·  voices {audio.Engine.RealVoices} rendered of {audio.Engine.ActiveVoices}"), dim);
            var time = world.Resource<Time>();
            Text(SpanFormat.Write(line, $"{1 / MathF.Max(time.Delta, 1e-4f):0} fps"), dim);
        }
    }
}
