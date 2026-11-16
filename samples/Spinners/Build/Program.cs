using System.Numerics;
using Gamegrl;
using Gamegrl.AssetPipeline;
using Gamegrl.Assets;
using Gamegrl.Rendering;
using Spinners;

// Development tool for the Spinners project (runs under the JIT, with the game's components loaded):
//   author          write Assets/main.scene from code (a stand-in for the editor until milestone 3)
//   pack <out>      import everything and write a player archive
//   run             run the game from the asset database; edits under Assets/ hot-reload
string project = FindProject();
string command = args.Length > 0 ? args[0] : "run";

switch (command)
{
    case "author":
    {
        using var db = new AssetDatabase(project);
        var ball = new AssetRef<TextureAsset>(Require(db, "Assets/ball.png"));
        var gem = new AssetRef<MeshAsset>(Require(db, "Assets/gem.gltf"));

        // speck.prefab, then tower.prefab (which nests a speck), then main.scene with three tower instances.
        using (var w = new World())
        {
            w.Spawn(new Name("Speck"), LocalTransform.At(new Vector3(0, 1.5f, 0), 0.5f), new LocalToWorld(),
                new MeshRenderer { Mesh = gem, Color = new Vector4(1, 1, 1, 1) });
            Write(project, "Assets/speck.prefab", Scene.SaveText(w));
        }
        db.Refresh();
        var speck = new AssetRef<PrefabAsset>(Require(db, "Assets/speck.prefab"));
        using (var w = new World())
        {
            AuthorTower(w, gem, speck, db.PrefabText);
            Write(project, "Assets/tower.prefab", Scene.SaveText(w, db.PrefabText));
        }
        db.Refresh();
        var tower = new AssetRef<PrefabAsset>(Require(db, "Assets/tower.prefab"));
        using (var w = new World())
        {
            AuthorScene(w, tower, ball, db.PrefabText);
            Write(project, SpinnersModule.MainScene, Scene.SaveText(w, db.PrefabText));
        }
        break;
    }
    case "pack":
    {
        string output = args.Length > 1 ? args[1] : Path.Combine(project, "spinners.ggpak");
        using var db = new AssetDatabase(project);
        db.Pack(output);
        Console.WriteLine($"packed {db.Records.Count} assets into {output} ({new FileInfo(output).Length / 1024} KiB)");
        break;
    }
    case "run":
    {
        var db = new AssetDatabase(project);
        db.Watch();
        using var app = SpinnersApp.Build(db, SpinnersApp.ParseRunFor(args));
        SpinnersApp.Run(app);
        break;
    }
    default:
        Console.Error.WriteLine("usage: Spinners.Build [author | pack <out> | run [--seconds N]]");
        return 1;
}
return 0;

static Guid Require(AssetDatabase db, string path)
{
    var g = db.FindByPath(path);
    if (g == Guid.Empty) throw new FileNotFoundException(path);
    return g;
}

static string FindProject()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "samples", "Spinners", "Assets"))) return Path.Combine(dir.FullName, "samples", "Spinners");
        if (Directory.Exists(Path.Combine(dir.FullName, "Assets")) && File.Exists(Path.Combine(dir.FullName, ".gitignore"))) return dir.FullName;
    }
    throw new DirectoryNotFoundException("Can't find the Spinners project (a folder with Assets/).");
}

static void Write(string project, string path, string text)
{
    File.WriteAllText(Path.Combine(project, path), text);
    Console.WriteLine($"wrote {path}");
}

// tower.prefab: a spinning gem with four orbiting moons (a hierarchy); moon 1 carries a nested speck prefab.
static void AuthorTower(World world, AssetRef<MeshAsset> gem, AssetRef<PrefabAsset> speck, Func<Guid, string?> prefabs)
{
    var tower = world.Spawn(new Name("Tower"), LocalTransform.At(Vector3.Zero, 3), new LocalToWorld(),
        new MeshRenderer { Mesh = gem, Color = new Vector4(1f, 0.55f, 0.2f, 1) },
        new Spinner { Axis = Vector3.UnitY, DegreesPerSecond = 40 });
    for (int i = 0; i < 4; i++)
    {
        float a = i * MathF.PI / 2;
        var moon = world.Spawn(new Name($"Moon {i + 1}"), LocalTransform.At(new Vector3(MathF.Cos(a) * 1.6f, 0, MathF.Sin(a) * 1.6f), 0.35f), new LocalToWorld(),
            new MeshRenderer { Mesh = BuiltinAssets.Cube, Color = new Vector4(0.3f, 0.8f, 1f, 1) },
            new Spinner { Axis = Vector3.Normalize(new Vector3(1, 1, 0)), DegreesPerSecond = 120 });
        world.SetParent(moon, tower);
        if (i == 0) world.SetParent(Scene.Instantiate(world, speck, prefabs), moon);
    }
}

// main.scene: a camera, three tower instances with different overrides, bouncing sprites.
static void AuthorScene(World world, AssetRef<PrefabAsset> towerPrefab, AssetRef<TextureAsset> ball, Func<Guid, string?> prefabs)
{
    var camera = world.Spawn(new Name("Camera"), Camera.Perspective(60), LocalTransform.Identity, new LocalToWorld());
    world.Add(camera, new Orbit { Radius = 34, Height = 15, Speed = 0.12f });

    var center = Scene.Instantiate(world, towerPrefab, prefabs);
    world.Get<LocalTransform>(center).Position = new Vector3(0, 5, 0);

    var left = Scene.Instantiate(world, towerPrefab, prefabs);                 // recolored, faster
    world.Get<LocalTransform>(left).Position = new Vector3(-11, 4, 0);
    world.Get<MeshRenderer>(left).Color = new Vector4(0.6f, 0.3f, 1f, 1);
    world.Get<Spinner>(left).DegreesPerSecond = 110;

    var right = Scene.Instantiate(world, towerPrefab, prefabs);                // a moon removed, a moon made gold
    world.Get<LocalTransform>(right).Position = new Vector3(11, 4, 0);
    foreach (var moon in world.Children(right))
    {
        string name = world.Get<Name>(moon).ToString();
        if (name == "Moon 3") world.Get<MeshRenderer>(moon).Color = new Vector4(1f, 0.85f, 0.2f, 1);
    }
    foreach (var moon in world.Children(right))
        if (world.Get<Name>(moon).ToString() == "Moon 2")
        {
            world.Destroy(moon);
            break;
        }

    var rng = new Random(7);
    for (int i = 0; i < 12; i++)
    {
        float size = 28 + rng.NextSingle() * 40;
        var tint = new Vector4(0.6f + rng.NextSingle() * 0.4f, 0.5f + rng.NextSingle() * 0.5f, 0.3f + rng.NextSingle() * 0.7f, 0.85f);
        world.Spawn(new Name($"Ball {i + 1}"),
            LocalTransform.At(new Vector3(100 + rng.NextSingle() * 1000, 100 + rng.NextSingle() * 500, 0)),
            Sprite.Of(ball, new Vector2(size)).Tinted(tint),
            new Bouncer { Velocity = new Vector2(rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f) * 500, Spin = rng.NextSingle() * 4 - 2 });
    }
}
