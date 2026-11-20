using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Gamegrl.AssetPipeline.Importers;
using Gamegrl.Assets;

namespace Gamegrl.AssetPipeline;

/// <summary>What an importer gets: the source file, its settings and a way to read files next to it.</summary>
public sealed class ImportContext(string path, byte[] bytes, IReadOnlyDictionary<string, string> settings, Func<string, byte[]> loadSibling,
    Func<Guid, string?> prefabText)
{
    /// <summary>The project path, e.g. "Assets/textures/ball.png".</summary>
    public string Path { get; } = path;
    public byte[] Bytes { get; } = bytes;
    public IReadOnlyDictionary<string, string> Settings { get; } = settings;
    public byte[] LoadSibling(string relative) => loadSibling(relative);

    /// <summary>The source text of a prefab, by GUID, for expanding prefab instances.</summary>
    public string? PrefabText(Guid guid) => prefabText(guid);

    public bool Flag(string key, bool fallback) => Settings.TryGetValue(key, out var v) ? v == "true" : fallback;
}

/// <summary>Turns a source file into a runtime artifact. Bump <see cref="Version"/> when the output changes.</summary>
public interface IAssetImporter
{
    string Id { get; }
    int Version { get; }
    AssetKind Kind { get; }
    IReadOnlyList<string> Extensions { get; }
    IReadOnlyDictionary<string, string> DefaultSettings { get; }
    /// <summary>The output depends on prefab sources (so any prefab change invalidates its cache entry).</summary>
    bool UsesPrefabs => false;
    byte[] Import(ImportContext ctx);
}

public sealed class TextureImporter : IAssetImporter
{
    public string Id => "texture";
    public int Version => 1;
    public AssetKind Kind => AssetKind.Texture;
    public IReadOnlyList<string> Extensions { get; } = [".png"];
    public IReadOnlyDictionary<string, string> DefaultSettings { get; } = new Dictionary<string, string> { ["srgb"] = "true" };
    public byte[] Import(ImportContext ctx) => Png.Decode(ctx.Bytes, ctx.Flag("srgb", true)).ToArtifact();
}

public sealed class MeshImporter : IAssetImporter
{
    public string Id => "mesh";
    public int Version => 1;
    public AssetKind Kind => AssetKind.Mesh;
    public IReadOnlyList<string> Extensions { get; } = [".gltf", ".glb"];
    public IReadOnlyDictionary<string, string> DefaultSettings { get; } = new Dictionary<string, string>();
    public byte[] Import(ImportContext ctx) => Gltf.Import(ctx.Bytes, ctx.LoadSibling).ToArtifact();
}

/// <summary>TrueType fonts, stored unchanged. CFF-flavored OpenType ("OTTO") is rejected: the rasterizer reads glyf outlines.</summary>
public sealed class FontImporter : IAssetImporter
{
    public string Id => "font";
    public int Version => 1;
    public AssetKind Kind => AssetKind.Font;
    public IReadOnlyList<string> Extensions { get; } = [".ttf", ".otf"];
    public IReadOnlyDictionary<string, string> DefaultSettings { get; } = new Dictionary<string, string>();
    public byte[] Import(ImportContext ctx)
    {
        var b = ctx.Bytes;
        uint tag = b.Length >= 12 ? (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]) : 0;
        if (tag == 0x4F54544F) throw new FormatException(ctx.Path + ": CFF outlines (OTTO) are not supported; use a TrueType-outline font.");
        if (tag is not (0x00010000 or 0x74727565)) throw new FormatException(ctx.Path + ": not a TrueType font.");
        return b;
    }
}

/// <summary>
/// WAV files to the audio artifact: decoded and validated, stored as a float WAV with the source's rate and channel
/// layout, so the player loads it with no conversion. (Compressed formats and streaming come with Ogg Vorbis.)
/// </summary>
public sealed class AudioImporter : IAssetImporter
{
    public string Id => "audio";
    public int Version => 1;
    public AssetKind Kind => AssetKind.Audio;
    public IReadOnlyList<string> Extensions { get; } = [".wav"];
    public IReadOnlyDictionary<string, string> DefaultSettings { get; } = new Dictionary<string, string>();
    public byte[] Import(ImportContext ctx)
    {
        Gamegrl.Audio.AudioClip clip;
        try { clip = Gamegrl.Audio.Wav.Read(ctx.Bytes, ctx.Path); }
        catch (InvalidDataException e) { throw new FormatException(ctx.Path + ": " + e.Message, e); }
        var output = new MemoryStream(44 + 40 + clip.Samples.Length * 4);
        Gamegrl.Audio.Wav.Write(output, clip.Samples, clip.SampleRate, clip.Layout);
        return output.ToArray();
    }
}

/// <summary>
/// Text scenes (and prefabs) to the binary player format, with prefab instances flattened. Needs the game's
/// components loaded in this process.
/// </summary>
public class SceneImporter : IAssetImporter
{
    public virtual string Id => "scene";
    public int Version => 2;
    public virtual AssetKind Kind => AssetKind.Scene;
    public virtual IReadOnlyList<string> Extensions { get; } = [".scene"];
    public IReadOnlyDictionary<string, string> DefaultSettings { get; } = new Dictionary<string, string>();
    public bool UsesPrefabs => true;

    public byte[] Import(ImportContext ctx)
    {
        var warnings = new List<string>();
        var binary = Scene.TextToBinary(Encoding.UTF8.GetString(ctx.Bytes), ctx.PrefabText, warnings.Add);
        if (warnings.Count > 0) throw new InvalidDataException($"{ctx.Path}: " + string.Join("; ", warnings));
        return binary;
    }
}

public sealed class PrefabImporter : SceneImporter
{
    public override string Id => "prefab";
    public override AssetKind Kind => AssetKind.Prefab;
    public override IReadOnlyList<string> Extensions { get; } = [".prefab"];
}

/// <summary>
/// The editor's view of a project's <c>Assets/</c> folder. Every asset has a <c>.meta</c> sidecar holding its
/// GUID and importer settings; imported artifacts are cached in <c>Library/artifacts</c>, keyed by a hash of the
/// source, settings and importer version, so reimports only happen when something changed.
/// <code>
/// gamegrl meta 1
/// guid = 3f2a9c1e5b7d4e8a9c217d4e5f6a8b90
/// importer = texture
/// srgb = true
/// </code>
/// </summary>
public sealed class AssetDatabase : IAssetSource, IDisposable
{
    public const string MetaHeader = "gamegrl meta 1";

    public sealed record AssetRecord(Guid Guid, string Path, IAssetImporter Importer, Dictionary<string, string> Settings)
    {
        public string? ArtifactHash { get; set; }
        public string? Error { get; set; }
    }

    private readonly string _root;
    private readonly List<IAssetImporter> _importers = [new TextureImporter(), new MeshImporter(), new FontImporter(), new AudioImporter(), new SceneImporter(), new PrefabImporter()];
    private readonly Dictionary<Guid, AssetRecord> _byGuid = new();
    private readonly Dictionary<string, AssetRecord> _byPath = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _pendingChanges = new();
    private FileSystemWatcher? _watcher;

    public AssetDatabase(string projectRoot)
    {
        _root = Path.GetFullPath(projectRoot);
        Directory.CreateDirectory(AssetsDir);
        Directory.CreateDirectory(ArtifactsDir);
        Refresh();
    }

    public string AssetsDir => Path.Combine(_root, "Assets");
    public string ArtifactsDir => Path.Combine(_root, "Library", "artifacts");
    public IReadOnlyCollection<AssetRecord> Records => _byGuid.Values;

    public event Action<Guid>? Changed;

    /// <summary>Add a custom importer (e.g. from a game module). Later registrations win on extension clashes.</summary>
    public void AddImporter(IAssetImporter importer) => _importers.Insert(0, importer);

    /// <summary>Scan <c>Assets/</c>: create missing .meta files, pick up new and removed assets.</summary>
    public void Refresh()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(AssetsDir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (file.EndsWith(".meta", StringComparison.Ordinal)) continue;
            var importer = ImporterFor(file);
            if (importer is null) continue;
            string path = ProjectPath(file);
            seen.Add(path);
            if (_byPath.ContainsKey(path)) continue;
            var record = LoadOrCreateMeta(file, path, importer);
            if (_byGuid.TryGetValue(record.Guid, out var clash))
                throw new InvalidDataException($"{path} and {clash.Path} have the same GUID (a copied .meta?). Delete one .meta.");
            _byGuid[record.Guid] = record;
            _byPath[path] = record;
        }
        foreach (var gone in _byPath.Keys.Where(p => !seen.Contains(p)).ToList())
        {
            _byGuid.Remove(_byPath[gone].Guid);
            _byPath.Remove(gone);
        }
    }

    /// <summary>Import (or fetch from the cache) every asset; returns the failures.</summary>
    public IReadOnlyList<AssetRecord> ImportAll()
    {
        var failed = new List<AssetRecord>();
        foreach (var r in _byGuid.Values.OrderBy(r => r.Path, StringComparer.Ordinal))
            if (Import(r) is null) failed.Add(r);
        return failed;
    }

    public bool TryRead(Guid guid, out AssetKind kind, out ReadOnlyMemory<byte> artifact)
    {
        kind = AssetKind.Unknown;
        artifact = default;
        if (!_byGuid.TryGetValue(guid, out var r)) return false;
        var bytes = Import(r);
        if (bytes is null) return false;
        kind = r.Importer.Kind;
        artifact = bytes;
        return true;
    }

    public Guid FindByPath(string path) => _byPath.TryGetValue(path.Replace('\\', '/'), out var r) ? r.Guid : Guid.Empty;

    public AssetRecord? Get(Guid guid) => _byGuid.GetValueOrDefault(guid);

    /// <summary>Import everything and write a player archive. Throws if any asset fails to import.</summary>
    public void Pack(string archivePath)
    {
        var failed = ImportAll();
        if (failed.Count > 0) throw new InvalidDataException("Import failed: " + string.Join("; ", failed.Select(f => $"{f.Path}: {f.Error}")));
        AssetArchive.Write(archivePath, _byGuid.Values.Select(r => (r.Guid, r.Importer.Kind, r.Path, Import(r)!)));
    }

    /// <summary>Watch <c>Assets/</c>; changes are reimported and announced by <see cref="PumpChanges"/>.</summary>
    public void Watch()
    {
        if (_watcher is not null) return;
        _watcher = new FileSystemWatcher(AssetsDir) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
        _watcher.Changed += (_, e) => _pendingChanges.Enqueue(e.FullPath);
        _watcher.Created += (_, e) => _pendingChanges.Enqueue(e.FullPath);
        _watcher.Renamed += (_, e) => _pendingChanges.Enqueue(e.FullPath);
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Reimport changed assets and raise <see cref="Changed"/>, on the calling thread.</summary>
    public void PumpChanges()
    {
        if (_pendingChanges.IsEmpty) return;
        var changed = new HashSet<string>(StringComparer.Ordinal);
        while (_pendingChanges.TryDequeue(out var full))
        {
            string file = full.EndsWith(".meta", StringComparison.Ordinal) ? full[..^5] : full;
            changed.Add(file);
        }
        Refresh();
        foreach (var file in changed)
        {
            if (!_byPath.TryGetValue(ProjectPath(file), out var r)) continue;
            r.Settings.Clear();
            foreach (var kv in ReadMeta(file + ".meta").Settings) r.Settings[kv.Key] = kv.Value;
            r.ArtifactHash = null;
            if (Import(r) is not null) Changed?.Invoke(r.Guid);
        }
    }

    private byte[]? Import(AssetRecord r)
    {
        string source = Path.Combine(_root, r.Path);
        byte[] bytes;
        try
        {
            bytes = ReadShared(source);
        }
        catch (IOException e)
        {
            r.Error = e.Message;
            return null;
        }
        string hash = Hash(r, bytes);
        string artifactPath = Path.Combine(ArtifactsDir, hash);
        if (File.Exists(artifactPath))
        {
            r.ArtifactHash = hash;
            r.Error = null;
            return File.ReadAllBytes(artifactPath);
        }
        try
        {
            string dir = Path.GetDirectoryName(source)!;
            var ctx = new ImportContext(r.Path, bytes, r.Settings, rel => File.ReadAllBytes(Path.Combine(dir, rel)), PrefabText);
            var artifact = r.Importer.Import(ctx);
            string tmp = artifactPath + ".tmp";
            File.WriteAllBytes(tmp, artifact);
            File.Move(tmp, artifactPath, overwrite: true);
            r.ArtifactHash = hash;
            r.Error = null;
            return artifact;
        }
        catch (Exception e) when (e is FormatException or InvalidDataException or IOException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            r.Error = e.Message;
            return null;
        }
    }

    /// <summary>The source text of a prefab asset, or null.</summary>
    public string? PrefabText(Guid guid) =>
        _byGuid.TryGetValue(guid, out var r) && r.Importer is PrefabImporter ? Encoding.UTF8.GetString(ReadShared(Path.Combine(_root, r.Path))) : null;

    private string Hash(AssetRecord r, byte[] source)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(Encoding.UTF8.GetBytes($"{r.Importer.Id}:{r.Importer.Version}\n"));
        if (r.Importer.UsesPrefabs)
        {
            // Coarse but always correct: any prefab change invalidates every scene and prefab.
            foreach (var p in _byGuid.Values.Where(x => x.Importer is PrefabImporter).OrderBy(x => x.Path, StringComparer.Ordinal))
            {
                h.AppendData(Encoding.UTF8.GetBytes(p.Path + "\n"));
                h.AppendData(ReadShared(Path.Combine(_root, p.Path)));
            }
        }
        foreach (var kv in r.Settings.OrderBy(k => k.Key, StringComparer.Ordinal)) h.AppendData(Encoding.UTF8.GetBytes($"{kv.Key}={kv.Value}\n"));
        h.AppendData(source);
        return Convert.ToHexStringLower(h.GetHashAndReset());
    }

    private IAssetImporter? ImporterFor(string file)
    {
        string ext = Path.GetExtension(file).ToLowerInvariant();
        return _importers.FirstOrDefault(i => i.Extensions.Contains(ext));
    }

    private string ProjectPath(string file) => Path.GetRelativePath(_root, file).Replace('\\', '/');

    private AssetRecord LoadOrCreateMeta(string file, string path, IAssetImporter importer)
    {
        string metaPath = file + ".meta";
        if (File.Exists(metaPath))
        {
            var meta = ReadMeta(metaPath);
            var imp = _importers.FirstOrDefault(i => i.Id == meta.Importer) ?? importer;
            return new AssetRecord(meta.Guid, path, imp, meta.Settings);
        }
        var settings = new Dictionary<string, string>(importer.DefaultSettings);
        var record = new AssetRecord(Guid.NewGuid(), path, importer, settings);
        WriteMeta(metaPath, record);
        return record;
    }

    public static void WriteMeta(string metaPath, AssetRecord r)
    {
        var sb = new StringBuilder();
        sb.Append(MetaHeader).Append('\n');
        sb.Append("guid = ").Append(r.Guid.ToString("N")).Append('\n');
        sb.Append("importer = ").Append(r.Importer.Id).Append('\n');
        foreach (var kv in r.Settings.OrderBy(k => k.Key, StringComparer.Ordinal)) sb.Append(kv.Key).Append(" = ").Append(kv.Value).Append('\n');
        File.WriteAllText(metaPath, sb.ToString());
    }

    private static (Guid Guid, string Importer, Dictionary<string, string> Settings) ReadMeta(string metaPath)
    {
        var lines = Encoding.UTF8.GetString(ReadShared(metaPath)).Replace("\r\n", "\n").Split('\n');
        if (lines[0].Trim() != MetaHeader) throw new InvalidDataException($"{metaPath}: not a gamegrl .meta file.");
        Guid guid = Guid.Empty;
        string importer = "";
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1))
        {
            int eq = line.IndexOf(" = ", StringComparison.Ordinal);
            if (eq < 0) continue;
            string key = line[..eq], value = line[(eq + 3)..];
            if (key == "guid") guid = Guid.ParseExact(value, "N");
            else if (key == "importer") importer = value;
            else settings[key] = value;
        }
        if (guid == Guid.Empty) throw new InvalidDataException($"{metaPath}: missing guid.");
        return (guid, importer, settings);
    }

    /// <summary>Read a file that an editor or exporter may still be writing (retries briefly).</summary>
    private static byte[] ReadShared(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var bytes = new byte[fs.Length];
                fs.ReadExactly(bytes);
                return bytes;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(20);
            }
        }
    }

    public void Dispose() => _watcher?.Dispose();
}
