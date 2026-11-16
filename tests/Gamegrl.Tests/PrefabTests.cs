using System.Numerics;
using static Gamegrl.Tests.Harness;

namespace Gamegrl.Tests;

internal static class PrefabTests
{
    private static readonly Guid SpeckGuid = new("5bec0000000000000000000000000001");
    private static readonly Guid TowerGuid = new("70e40000000000000000000000000002");

    public static void Register()
    {
        Test("prefabs: nested expansion, per-instance overrides, remapped references", () =>
        {
            var resolver = Resolver();
            using var w = new World();
            var a = Scene.Instantiate(w, new AssetRef<PrefabAsset>(TowerGuid), resolver);
            var b = Scene.Instantiate(w, new AssetRef<PrefabAsset>(TowerGuid), resolver);
            Assert.Equal(8, w.EntityCount); // 2 × (tower, moon 1, speck, moon 2)

            var moonA = Child(w, a, "Moon 2");
            var moonB = Child(w, b, "Moon 2");
            Assert.Equal(moonA, w.Get<Everything>(a).Target); // references point inside their own instance
            Assert.Equal(moonB, w.Get<Everything>(b).Target);
            var speckA = Child(w, Child(w, a, "Moon 1"), "Speck");
            Assert.True(w.Has<PrefabInstance>(speckA), "nested instance root is marked");
            Assert.Equal(new Vector3(0, 2, 0), w.Get<LocalTransform>(speckA).Position);
        });

        Test("prefabs: save writes only differences; save → load → save is identical", () =>
        {
            var resolver = Resolver();
            using var w = new World();
            var a = Scene.Instantiate(w, new AssetRef<PrefabAsset>(TowerGuid), resolver);
            var b = Scene.Instantiate(w, new AssetRef<PrefabAsset>(TowerGuid), resolver);
            // Instance b: move the root, rename the nested speck, drop a tag, delete a child, add a component.
            w.Get<LocalTransform>(b).Position = new Vector3(5, 0, 0);
            var speckB = Child(w, Child(w, b, "Moon 1"), "Speck");
            w.Get<Name>(speckB) = new Name("Renamed Speck");
            var moon2B = Child(w, b, "Moon 2");
            w.Remove<Frozen>(moon2B);
            w.Add(moon2B, new Health { Value = 3 });
            w.Destroy(Child(w, b, "Moon 1")); // takes the nested speck with it — so undo the rename's relevance
            var extra = w.Spawn(new Name("Added Child"));
            w.SetParent(extra, b);

            string text = Scene.SaveText(w, resolver);
            if (Environment.GetEnvironmentVariable("GAMEGRL_DUMP") is { } dump) File.WriteAllText(dump, text);
            int instances = text.Split("prefab asset:").Length - 1;
            Assert.Equal(2, instances);
            Assert.True(!text.Contains("\"Moon 2\""), "unchanged prefab content isn't written");
            Assert.True(text.Contains("    Position = 5 0 0\n"), "root override written");
            Assert.True(text.Contains("  - Gamegrl.Tests.Frozen\n"), "removed component written");
            Assert.True(text.Contains("  Gamegrl.Tests.Health\n    Value = 3\n"), "added component written");
            Assert.True(text.Contains("\n  removed\n"), "deleted child written");
            Assert.True(text.Contains("\"Added Child\""), "plain child of an instance written in full");

            using var w2 = new World();
            Scene.LoadText(w2, text, resolver);
            Assert.Equal(w.EntityCount, w2.EntityCount);
            Assert.Equal(text, Scene.SaveText(w2, resolver));
            _ = a;
        });

        Test("prefabs: flattened into binary scenes for players", () =>
        {
            var resolver = Resolver();
            using var w = new World();
            Scene.Instantiate(w, new AssetRef<PrefabAsset>(TowerGuid), resolver);
            string text = Scene.SaveText(w, resolver);
            byte[] binary = Scene.TextToBinary(text, resolver);
            using var w2 = new World();
            Scene.LoadBinary(w2, binary);
            Assert.Equal(4, w2.EntityCount);
        });

        Test("prefabs: a missing prefab warns and keeps the overrides", () =>
        {
            string text = Scene.TextHeader + "\n\nentity 0000000000000001\n  prefab asset:00000000000000000000000000000bad\n  Gamegrl.Name\n    Value = \"orphan\"\n";
            var warnings = new List<string>();
            using var w = new World();
            var e = Scene.LoadText(w, text, _ => null, warnings.Add)[0];
            Assert.Equal("orphan", w.Get<Name>(e).ToString());
            Assert.Equal(1, warnings.Count);
        });
    }

    private static Entity Child(World w, Entity parent, string name)
    {
        foreach (var c in w.Children(parent))
            if (w.Get<Name>(c).ToString() == name) return c;
        throw new Exception($"no child named {name}");
    }

    /// <summary>speck.prefab (one entity) and tower.prefab (a root, two moons, a nested speck, an internal reference).</summary>
    private static Func<Guid, string?> Resolver()
    {
        string speck, tower;
        using (var w = new World())
        {
            w.Spawn(new SceneId { Value = 0x5ec }, new Name("Speck"), LocalTransform.At(new Vector3(0, 2, 0), 0.5f));
            speck = Scene.SaveText(w);
        }
        using (var w = new World())
        {
            var root = w.Spawn(new SceneId { Value = 0x100 }, new Name("Tower"), LocalTransform.Identity);
            var m1 = w.Spawn(new SceneId { Value = 0x201 }, new Name("Moon 1"), LocalTransform.At(new Vector3(1, 0, 0)));
            var m2 = w.Spawn(new SceneId { Value = 0x202 }, new Name("Moon 2"), LocalTransform.At(new Vector3(-1, 0, 0)), new Frozen());
            w.SetParent(m1, root);
            w.SetParent(m2, root);
            w.Add(root, new Everything { Target = m2 });
            string partial = Scene.SaveText(w);
            // Nest a speck instance under Moon 1 (written by hand, as the editor would).
            tower = partial + "\nentity 0000000000000300\n  parent 0000000000000201\n  prefab asset:" + SpeckGuid.ToString("N") + "\n";
        }
        return g => g == SpeckGuid ? speck : g == TowerGuid ? tower : null;
    }
}
