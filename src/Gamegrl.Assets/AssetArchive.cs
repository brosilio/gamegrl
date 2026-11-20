using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace Gamegrl.Assets;

/// <summary>
/// Where a running game gets imported assets: a packed archive in a player, the asset database in the editor.
/// <see cref="Changed"/> fires when an asset is reimported, so systems can hot-reload it.
/// </summary>
public interface IAssetSource
{
    bool TryRead(Guid guid, out AssetKind kind, out ReadOnlyMemory<byte> artifact);

    /// <summary>Find an asset by its project path (e.g. "Assets/main.scene"), or Guid.Empty.</summary>
    Guid FindByPath(string path);

    event Action<Guid>? Changed;

    /// <summary>Deliver pending change notifications on the calling (main) thread.</summary>
    void PumpChanges();
}

/// <summary>
/// A packed archive (".ggpak"): a table of (GUID, kind, path, offset, length) and 16-byte-aligned artifact data.
/// Opened memory-mapped, so reading an asset is a pointer into the file with no copy and no load step.
/// <code>
/// "GGPK" u32 version · u32 count · count × entry · data
/// entry: guid[16] · u32 kind · u64 offset · u64 length · u32 pathLength · path (UTF-8)
/// </code>
/// </summary>
public sealed unsafe class AssetArchive : IAssetSource, IDisposable
{
    private const uint Magic = 0x4B504747; // "GGPK"
    private const uint Version = 1;

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _base;
    private readonly long _length;
    private readonly Dictionary<Guid, (AssetKind Kind, long Offset, long Length)> _entries = new();
    private readonly Dictionary<string, Guid> _paths = new(StringComparer.Ordinal);

    public AssetArchive(string path)
    {
        _file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _view = _file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _base = p + _view.PointerOffset;
        _length = new FileInfo(path).Length;

        var span = new ReadOnlySpan<byte>(_base, (int)Math.Min(_length, int.MaxValue));
        if (BinaryPrimitives.ReadUInt32LittleEndian(span) != Magic) throw new FormatException($"{path} is not a gamegrl archive.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(span[4..]) != Version) throw new FormatException($"{path}: unsupported archive version.");
        int count = BinaryPrimitives.ReadInt32LittleEndian(span[8..]);
        int at = 12;
        for (int i = 0; i < count; i++)
        {
            var guid = new Guid(span.Slice(at, 16));
            var kind = (AssetKind)BinaryPrimitives.ReadUInt32LittleEndian(span[(at + 16)..]);
            long offset = BinaryPrimitives.ReadInt64LittleEndian(span[(at + 20)..]);
            long length = BinaryPrimitives.ReadInt64LittleEndian(span[(at + 28)..]);
            int pathLength = BinaryPrimitives.ReadInt32LittleEndian(span[(at + 36)..]);
            string assetPath = Encoding.UTF8.GetString(span.Slice(at + 40, pathLength));
            at += 40 + pathLength;
            if (offset < 0 || length < 0 || offset + length > _length) throw new FormatException($"{path}: entry {guid} is out of bounds.");
            _entries[guid] = (kind, offset, length);
            _paths[assetPath] = guid;
        }
    }

    public int Count => _entries.Count;

    public event Action<Guid>? Changed { add { } remove { } }

    public void PumpChanges() { }

    public bool TryRead(Guid guid, out AssetKind kind, out ReadOnlyMemory<byte> artifact)
    {
        if (!_entries.TryGetValue(guid, out var e))
        {
            kind = AssetKind.Unknown;
            artifact = default;
            return false;
        }
        kind = e.Kind;
        artifact = new UnmanagedMemory(_base + e.Offset, (int)e.Length).Memory;
        return true;
    }

    public Guid FindByPath(string path) => _paths.TryGetValue(path, out var g) ? g : Guid.Empty;

    /// <summary>Write an archive from (guid, kind, path, artifact) entries.</summary>
    public static void Write(string path, IEnumerable<(Guid Guid, AssetKind Kind, string Path, byte[] Artifact)> entries)
    {
        var list = entries.OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
        var paths = list.Select(e => Encoding.UTF8.GetBytes(e.Path)).ToList();
        long header = 12 + list.Sum(e => 40L) + paths.Sum(p => (long)p.Length);
        long cursor = Align(header);
        var offsets = new long[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            offsets[i] = cursor;
            cursor = Align(cursor + list[i].Artifact.Length);
        }

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        Span<byte> buf = stackalloc byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[4..], Version);
        BinaryPrimitives.WriteInt32LittleEndian(buf[8..], list.Count);
        fs.Write(buf[..12]);
        for (int i = 0; i < list.Count; i++)
        {
            list[i].Guid.TryWriteBytes(buf);
            BinaryPrimitives.WriteUInt32LittleEndian(buf[16..], (uint)list[i].Kind);
            BinaryPrimitives.WriteInt64LittleEndian(buf[20..], offsets[i]);
            BinaryPrimitives.WriteInt64LittleEndian(buf[28..], list[i].Artifact.Length);
            BinaryPrimitives.WriteInt32LittleEndian(buf[36..], paths[i].Length);
            fs.Write(buf);
            fs.Write(paths[i]);
        }
        for (int i = 0; i < list.Count; i++)
        {
            while (fs.Position < offsets[i]) fs.WriteByte(0);
            fs.Write(list[i].Artifact);
        }
    }

    private static long Align(long v) => (v + 15) & ~15L;

    private bool _disposed;

    /// <summary>Unmaps the archive. Safe to call twice: the assets module disposes it on stop, and the world owns it as a resource.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }

    /// <summary>Exposes a span of the mapping as ReadOnlyMemory without copying.</summary>
    private sealed class UnmanagedMemory(byte* pointer, int length) : System.Buffers.MemoryManager<byte>
    {
        public override Span<byte> GetSpan() => new(pointer, length);
        public override System.Buffers.MemoryHandle Pin(int elementIndex = 0) => new(pointer + elementIndex);
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }
}

/// <summary>Makes an <see cref="IAssetSource"/> available as a world resource.</summary>
public sealed class AssetsModule(IAssetSource source) : IModule
{
    public void Build(EngineBuilder builder)
    {
        builder.SetResource(source);
        builder.AddSystem(new PumpAssetChangesSystem());
        if (source is IDisposable d) builder.OnStop(_ => d.Dispose());
    }

    private sealed class PumpAssetChangesSystem : ISystem
    {
        public string Name => "Assets.PumpChanges";
        public Phase Phase => Phase.First;
        public int Order => -100;
        public void Initialize(World world) { }
        public void Run(World world) => world.Resource<IAssetSource>().PumpChanges();
    }
}
