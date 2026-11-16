using Gamegrl.Assets;
using Gamegrl.AssetPipeline;

namespace Gamegrl.Editor;

/// <summary>GPU consumers see immutable, already imported bytes; no file I/O on the render thread.</summary>
internal sealed class EditorAssets : IAssetSource
{
    internal sealed record Catalog(Dictionary<Guid, (AssetKind Kind, byte[] Bytes)> Artifacts,
        Dictionary<string, Guid> Paths, Dictionary<Guid, string> Prefabs, string[] Files);
    private Catalog _catalog = new(new(), new(StringComparer.Ordinal), new(), []);
    public event Action<Guid>? Changed;
    public void PumpChanges() { }
    public string[] Files => _catalog.Files;
    public bool TryRead(Guid guid, out AssetKind kind, out ReadOnlyMemory<byte> artifact)
    {
        if (_catalog.Artifacts.TryGetValue(guid, out var entry)) { kind = entry.Kind; artifact = entry.Bytes; return true; }
        kind = AssetKind.Unknown; artifact = default; return false;
    }
    public Guid FindByPath(string path) => _catalog.Paths.GetValueOrDefault(path.Replace('\\', '/'));
    public string? PrefabText(Guid guid) => _catalog.Prefabs.GetValueOrDefault(guid);
    public void Apply(Catalog catalog)
    {
        var old = _catalog;
        _catalog = catalog;
        foreach (var (guid, entry) in catalog.Artifacts)
            if (!old.Artifacts.TryGetValue(guid, out var previous) || !entry.Bytes.AsSpan().SequenceEqual(previous.Bytes)) Changed?.Invoke(guid);
    }
    public static Catalog Import(string root, CancellationToken stop)
    {
        using var db = new AssetDatabase(root);
        var artifacts = new Dictionary<Guid, (AssetKind, byte[])>();
        var paths = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var prefabs = new Dictionary<Guid, string>();
        foreach (var record in db.Records)
        {
            stop.ThrowIfCancellationRequested();
            paths[record.Path] = record.Guid;
            if (db.TryRead(record.Guid, out var kind, out var bytes)) artifacts[record.Guid] = (kind, bytes.ToArray());
            else Log.Error($"{record.Path}: {record.Error}");
            if (record.Importer is PrefabImporter && db.PrefabText(record.Guid) is { } text) prefabs[record.Guid] = text;
        }
        return new(artifacts, paths, prefabs, paths.Keys.Order(StringComparer.Ordinal).ToArray());
    }
}
