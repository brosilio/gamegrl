using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Gamegrl.AssetPipeline;
using Gamegrl.AssetPipeline.Importers;
using Gamegrl.Assets;
using static Gamegrl.Tests.Harness;

namespace Gamegrl.Tests;

internal static class AssetTests
{
    private static string Data(string name) => Path.Combine(AppContext.BaseDirectory, "Data", name);

    public static void Register()
    {
        Test("png: rgba8, interlaced, rgba16, rgb8 and palette decode to the source pattern", () =>
        {
            byte[] pattern;
            // palette.png was quantized by the encoder, so it's checked against the encoder's own decode.
            foreach (var (file, checkAlpha, expectedFile) in new[] { ("rgba8.png", true, "pattern.rgba"), ("rgba8-interlaced.png", true, "pattern.rgba"),
                ("rgba16.png", true, "pattern.rgba"), ("rgb8.png", false, "pattern.rgba"), ("palette.png", true, "palette.rgba") })
            {
                pattern = File.ReadAllBytes(Data(expectedFile));
                var t = Png.Decode(File.ReadAllBytes(Data(file)));
                Assert.Equal(13, t.Width);
                Assert.Equal(7, t.Height);
                for (int i = 0; i < pattern.Length; i++)
                {
                    if (i % 4 == 3 && !checkAlpha) continue;
                    if (t.Rgba[i] != pattern[i]) throw new Exception($"{file}: byte {i} is {t.Rgba[i]}, expected {pattern[i]}");
                }
            }
        });

        Test("png: gray 1, 8 and 16 bit", () =>
        {
            var gray = File.ReadAllBytes(Data("gray.raw"));
            var g8 = Png.Decode(File.ReadAllBytes(Data("gray8.png")));
            var g16 = Png.Decode(File.ReadAllBytes(Data("gray16.png")));
            var g1 = Png.Decode(File.ReadAllBytes(Data("gray1.png")));
            for (int i = 0; i < gray.Length; i++)
            {
                Assert.Equal(gray[i], g8.Rgba[i * 4]);
                Assert.Equal(gray[i], g16.Rgba[i * 4]);
                Assert.Equal(g8.Rgba[i * 4], g8.Rgba[i * 4 + 2]);
                Assert.True(g1.Rgba[i * 4] is 0 or 255);
            }
        });

        Test("png: corrupt data is rejected", () =>
        {
            var bytes = File.ReadAllBytes(Data("rgba8.png"));
            bytes[40] ^= 0xFF; // inside a chunk: CRC mismatch
            Assert.Throws<FormatException>(() => Png.Decode(bytes));
            Assert.Throws<FormatException>(() => Png.Decode("not a png"u8));
        });

        Test("gltf: data-uri buffers, node transforms, generated normals", () =>
        {
            var mesh = Gltf.Import(Encoding.UTF8.GetBytes(TriangleGltf(withNormals: false)), _ => throw new Exception("no external files"));
            Assert.Equal(3, mesh.Vertices.Length);
            Assert.Equal(3, mesh.Indices.Length);
            Assert.Equal(new Vector3(10, 0, 0), mesh.Vertices[0].Position); // node translation applied
            Assert.Equal(new Vector3(11, 0, 0), mesh.Vertices[1].Position);
            Assert.True(Vector3.Distance(mesh.Vertices[0].Normal, Vector3.UnitZ) < 1e-5f, "flat normal generated");
        });

        Test("asset database: metas, cache, pack, memory-mapped archive, reimport on change", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "gamegrl-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "Assets", "sub"));
                File.Copy(Data("rgba8.png"), Path.Combine(root, "Assets", "sub", "pattern.png"));
                File.WriteAllText(Path.Combine(root, "Assets", "tri.gltf"), TriangleGltf(withNormals: true));
                File.WriteAllText(Path.Combine(root, "Assets", "readme.txt"), "ignored: no importer");

                Guid texGuid, meshGuid, sceneGuid;
                using (var db = new AssetDatabase(root))
                {
                    texGuid = db.FindByPath("Assets/sub/pattern.png");
                    meshGuid = db.FindByPath("Assets/tri.gltf");
                    Assert.True(texGuid != Guid.Empty && meshGuid != Guid.Empty);
                    Assert.True(File.Exists(Path.Combine(root, "Assets", "sub", "pattern.png.meta")));
                    Assert.True(!File.Exists(Path.Combine(root, "Assets", "readme.txt.meta")));

                    // A scene that references the texture by GUID.
                    using (var w = new World())
                    {
                        w.Spawn(new Name("Thing"), new Everything { Texture = new AssetRef<TextureAsset>(texGuid) });
                        File.WriteAllText(Path.Combine(root, "Assets", "main.scene"), Scene.SaveText(w));
                    }
                    db.Refresh();
                    sceneGuid = db.FindByPath("Assets/main.scene");
                    Assert.Equal(0, db.ImportAll().Count);
                }

                // GUIDs survive a restart (they live in the .meta files); artifacts come from the cache.
                using (var db = new AssetDatabase(root))
                {
                    Assert.Equal(texGuid, db.FindByPath("Assets/sub/pattern.png"));
                    int artifacts = Directory.GetFiles(db.ArtifactsDir).Length;
                    Assert.Equal(0, db.ImportAll().Count);
                    Assert.Equal(artifacts, Directory.GetFiles(db.ArtifactsDir).Length);

                    string pak = Path.Combine(root, "game.ggpak");
                    db.Pack(pak);
                    using var archive = new AssetArchive(pak);
                    Assert.Equal(3, archive.Count);
                    Assert.True(archive.TryRead(texGuid, out var kind, out var bytes));
                    Assert.Equal(AssetKind.Texture, kind);
                    var tex = TextureData.FromArtifact(bytes.Span);
                    Assert.Equal(13, tex.Width);
                    Assert.True(archive.TryRead(meshGuid, out kind, out bytes));
                    Assert.Equal(3, MeshData.FromArtifact(bytes.Span).Vertices.Length);
                    Assert.Equal(sceneGuid, archive.FindByPath("Assets/main.scene"));
                    Assert.True(archive.TryRead(sceneGuid, out kind, out bytes));
                    using var w = new World();
                    var e = Scene.LoadBinary(w, bytes.Span)[0];
                    Assert.Equal(texGuid, w.Get<Everything>(e).Texture.Guid);

                    // Hot reload: change the source, the watcher queues it, PumpChanges reimports and announces it.
                    var changed = new List<Guid>();
                    db.Changed += changed.Add;
                    db.Watch();
                    File.Copy(Data("rgb8.png"), Path.Combine(root, "Assets", "sub", "pattern.png"), overwrite: true);
                    for (int i = 0; i < 100 && changed.Count == 0; i++)
                    {
                        Thread.Sleep(20);
                        db.PumpChanges();
                    }
                    Assert.True(changed.Contains(texGuid), "change detected and reimported");
                }
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });
    }

    /// <summary>A one-triangle glTF with an embedded buffer and a translated node.</summary>
    private static string TriangleGltf(bool withNormals)
    {
        float[] positions = [0, 0, 0, 1, 0, 0, 0, 1, 0];
        float[] normals = [0, 0, 1, 0, 0, 1, 0, 0, 1];
        ushort[] idx = [0, 1, 2, 0];
        var buffer = new List<byte>();
        buffer.AddRange(MemoryMarshal.AsBytes(positions.AsSpan()).ToArray());
        int normalsOffset = buffer.Count;
        buffer.AddRange(MemoryMarshal.AsBytes(normals.AsSpan()).ToArray());
        int indexOffset = buffer.Count;
        buffer.AddRange(MemoryMarshal.AsBytes(idx.AsSpan()).ToArray());
        string b64 = Convert.ToBase64String(buffer.ToArray());
        string normalAttr = withNormals ? ", \"NORMAL\": 1" : "";
        return $$"""
        {
          "asset": { "version": "2.0" },
          "scene": 0,
          "scenes": [ { "nodes": [0] } ],
          "nodes": [ { "mesh": 0, "translation": [10, 0, 0] } ],
          "meshes": [ { "primitives": [ { "attributes": { "POSITION": 0{{normalAttr}} }, "indices": 2 } ] } ],
          "buffers": [ { "byteLength": {{buffer.Count}}, "uri": "data:application/octet-stream;base64,{{b64}}" } ],
          "bufferViews": [
            { "buffer": 0, "byteOffset": 0, "byteLength": 36 },
            { "buffer": 0, "byteOffset": {{normalsOffset}}, "byteLength": 36 },
            { "buffer": 0, "byteOffset": {{indexOffset}}, "byteLength": 6 }
          ],
          "accessors": [
            { "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3" },
            { "bufferView": 1, "componentType": 5126, "count": 3, "type": "VEC3" },
            { "bufferView": 2, "componentType": 5123, "count": 3, "type": "SCALAR" }
          ]
        }
        """;
    }
}
