using System.Numerics;
using static Gamegrl.Tests.Harness;

namespace Gamegrl.Tests;

public enum Team : byte { Red = 1, Blue = 2, Green = 4 }

public struct Stats
{
    public int Strength;
    public float Speed;
    public Inner Extra;

    public struct Inner
    {
        public Vector2 Offset;
        public bool Flag;
    }
}

public sealed class TextureAsset;

public struct Everything : IComponent
{
    public bool B;
    public byte U8;
    public short I16;
    public uint U32;
    public long I64;
    public float F;
    public double D;
    public Vector3 V3;
    public Quaternion Q;
    public Matrix4x4 M;
    public Guid G;
    public FixedString64 Text;
    public Team Team;
    public Stats Stats;
    public Entity Target;
    public AssetRef<TextureAsset> Texture;
    [NoSerialize] public int RuntimeOnly;
}

internal static class SceneTests
{
    public static void Register()
    {
        Test("hierarchy: set parent, children in order, reparent, cycles rejected", () =>
        {
            using var w = new World();
            var root = w.Spawn();
            var a = w.Spawn();
            var b = w.Spawn();
            var c = w.Spawn();
            w.SetParent(a, root);
            w.SetParent(b, root);
            w.SetParent(c, root);
            Assert.Equal("a b c", Names(w, root, a, b, c));
            w.SetParent(b, a);
            Assert.Equal("a c", Names(w, root, a, b, c));
            Assert.Equal(a, w.GetParent(b));
            Assert.Throws<InvalidOperationException>(() => w.SetParent(root, b)); // root -> a -> b -> root
            w.SetParent(b, default);
            Assert.True(!w.HasParent(b));
        });

        Test("hierarchy: destroying a parent destroys its subtree", () =>
        {
            using var w = new World();
            var root = w.Spawn();
            var child = w.Spawn();
            var grandchild = w.Spawn();
            var other = w.Spawn();
            w.SetParent(child, root);
            w.SetParent(grandchild, child);
            w.Destroy(root);
            Assert.True(!w.IsAlive(child) && !w.IsAlive(grandchild));
            Assert.True(w.IsAlive(other));
            Assert.Equal(1, w.EntityCount);
        });

        Test("transforms: children inherit the parent's matrix", () =>
        {
            var b = EngineBuilder.Create().AddModule<CoreModule>();
            var parent = b.World.Spawn(LocalTransform.At(new Vector3(10, 0, 0), 2), new LocalToWorld());
            var child = b.World.Spawn(LocalTransform.At(new Vector3(1, 0, 0)), new LocalToWorld());
            var grandchild = b.World.Spawn(LocalTransform.At(new Vector3(0, 1, 0)), new LocalToWorld());
            b.World.SetParent(child, parent);
            b.World.SetParent(grandchild, child);
            using var app = b.Build();
            app.Tick(0.016);
            Assert.Equal(new Vector3(12, 0, 0), app.World.Get<LocalToWorld>(child).Position);      // 10 + 2 * 1
            Assert.Equal(new Vector3(12, 2, 0), app.World.Get<LocalToWorld>(grandchild).Position); // scale carries down
        });

        Test("components: stable ids are name hashes, registered with generated serializers", () =>
        {
            int id = Component<LocalTransform>.Id;
            var info = ComponentRegistry.Get(id);
            Assert.Equal("Gamegrl.LocalTransform", info.Name);
            Assert.Equal(ComponentRegistry.StableId("Gamegrl.LocalTransform"), info.StableId);
            Assert.Equal(id, ComponentRegistry.FindByStableId(info.StableId));
            Assert.True(info.Serializer is not null);
            Assert.True(ComponentRegistry.FindByName("Gamegrl.Tests.Everything") >= 0); // registered at load, before first use
        });

        Test("fixed string: utf-8, truncates at a character boundary", () =>
        {
            Assert.Equal("héllo", new FixedString64("héllo").ToString());
            var longText = new string('é', 40); // 80 bytes
            var f = new FixedString64(longText);
            Assert.Equal(62, f.Length); // 31 two-byte characters
            Assert.Equal(new string('é', 31), f.ToString());
        });

        Test("scene: text round trip of every field kind, deterministic output", () =>
        {
            using var w = new World();
            var (root, child, sample) = Populate(w);
            string text = Scene.SaveText(w);
            Assert.True(text.StartsWith(Scene.TextHeader + "\n"));
            Assert.True(text.Contains("    Team = Blue\n"), "enum written by name");
            Assert.True(text.Contains("    Stats.Extra.Offset = 1.5 -2\n"), "nested struct path");
            Assert.True(text.Contains("    Text = \"quote \\\" and tab \\t\"\n"), "escaped string");
            Assert.True(!text.Contains("RuntimeOnly"), "[NoSerialize] skipped");
            Assert.True(!text.Contains("LocalToWorld\n    "), "derived matrix has no fields");

            using var w2 = new World();
            var loaded = Scene.LoadText(w2, text);
            Assert.Equal(3, loaded.Length);
            CheckLoaded(w2, w, sample);
            Assert.Equal(text, Scene.SaveText(w2)); // save → load → save is byte-identical
        });

        Test("scene: binary round trip matches text", () =>
        {
            using var w = new World();
            var (_, _, sample) = Populate(w);
            string text = Scene.SaveText(w);
            byte[] binary = Scene.TextToBinary(text);
            using var w2 = new World();
            Scene.LoadBinary(w2, binary);
            CheckLoaded(w2, w, sample);
            Assert.Equal(text, Scene.SaveText(w2));
        });

        Test("scene: unknown components and missing parents warn, load continues", () =>
        {
            string text = Scene.TextHeader + "\n\nentity 00000000000000aa\n  parent 00000000000000ff\n  Some.Removed.Component\n    X = 1\n  Gamegrl.Name\n    Value = \"kept\"\n";
            var warnings = new List<string>();
            using var w = new World();
            var e = Scene.LoadText(w, text, warn: warnings.Add)[0];
            Assert.Equal("kept", w.Get<Name>(e).ToString());
            Assert.Equal(2, warnings.Count);
        });
    }

    private static string Names(World w, Entity parent, Entity a, Entity b, Entity c)
    {
        var names = new List<string>();
        foreach (var e in w.Children(parent)) names.Add(e == a ? "a" : e == b ? "b" : e == c ? "c" : "?");
        return string.Join(' ', names);
    }

    private static (Entity Root, Entity Child, Everything Sample) Populate(World w)
    {
        var root = w.Spawn(new Name("Root"), LocalTransform.At(new Vector3(1, 2, 3)), new LocalToWorld());
        var child = w.Spawn(new Name("Child"), LocalTransform.Identity);
        w.SetParent(child, root);
        var sample = new Everything
        {
            B = true, U8 = 200, I16 = -1234, U32 = 4_000_000_000, I64 = long.MinValue, F = 0.1f, D = Math.PI,
            V3 = new Vector3(1, -2, 3.25f), Q = Quaternion.CreateFromYawPitchRoll(0.3f, 0.2f, 0.1f),
            M = Matrix4x4.CreateRotationY(1) * Matrix4x4.CreateTranslation(4, 5, 6), G = Guid.NewGuid(),
            Text = "quote \" and tab \t", Team = Team.Blue,
            Stats = new Stats { Strength = 7, Speed = 2.5f, Extra = new Stats.Inner { Offset = new Vector2(1.5f, -2), Flag = true } },
            Target = root, Texture = new AssetRef<TextureAsset>(Guid.NewGuid()), RuntimeOnly = 99,
        };
        var e = w.Spawn(sample, new Name("Everything"));
        w.SetParent(e, child);
        return (root, child, sample);
    }

    private static void CheckLoaded(World loaded, World original, Everything expected)
    {
        Entity everything = default, root = default;
        foreach (var a in loaded.Archetypes)
            foreach (var e in a.Entities)
            {
                if (loaded.Get<Name>(e).ToString() == "Everything") everything = e;
                if (loaded.Get<Name>(e).ToString() == "Root") root = e;
            }
        var got = loaded.Get<Everything>(everything);
        Assert.Equal(expected.B, got.B);
        Assert.Equal(expected.U8, got.U8);
        Assert.Equal(expected.I16, got.I16);
        Assert.Equal(expected.U32, got.U32);
        Assert.Equal(expected.I64, got.I64);
        Assert.Equal(expected.F, got.F);
        Assert.Equal(expected.D, got.D);
        Assert.Equal(expected.V3, got.V3);
        Assert.Equal(expected.Q, got.Q);
        Assert.Equal(expected.M, got.M);
        Assert.Equal(expected.G, got.G);
        Assert.Equal(expected.Text, got.Text);
        Assert.Equal(expected.Team, got.Team);
        Assert.Equal(expected.Stats.Strength, got.Stats.Strength);
        Assert.Equal(expected.Stats.Extra.Offset, got.Stats.Extra.Offset);
        Assert.Equal(expected.Stats.Extra.Flag, got.Stats.Extra.Flag);
        Assert.Equal(expected.Texture, got.Texture);
        Assert.Equal(0, got.RuntimeOnly);
        Assert.Equal(root, got.Target);                              // entity reference remapped
        Assert.Equal("Child", loaded.Get<Name>(loaded.GetParent(everything)).ToString());
        Assert.Equal(root, loaded.GetParent(loaded.GetParent(everything)));
        Assert.Equal(new Vector3(1, 2, 3), loaded.Get<LocalTransform>(root).Position);
        Assert.True(loaded.Has<LocalToWorld>(root));
        _ = original;
    }
}
