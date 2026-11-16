using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Gamegrl;

/// <summary>
/// Saves and loads entities as scenes: a line-oriented text format for the editor (readable, deterministic,
/// merge-friendly) and a compact binary format for players. Both are driven by the generated field visitors.
/// <code>
/// gamegrl scene 1
///
/// entity 3f9a0c1e5b7d2a46
///   parent 1a2b3c4d5e6f7081
///   Gamegrl.LocalTransform
///     Position = 0 1 0
///     Rotation = 0 0 0 1
///     Scale = 1 1 1
///   Gamegrl.Name
///     Value = "Cube"
/// </code>
/// Entities are written depth first (parents before children, siblings in order, roots by id), components by
/// name, fields in declaration order, one per line.
/// </summary>
public static unsafe partial class Scene
{
    private const uint BinaryMagic = 0x42534747; // "GGSB"
    private const uint BinaryVersion = 1;

    // ------------------------------------------------------------------ binary

    /// <summary>The player format: the same content as text, compact and fast to load.</summary>
    public static byte[] SaveBinary(World world)
    {
        var order = Order(world);
        var ids = SceneIds(world, order);
        var w = new BinaryWriteVisitor(ids);
        w.U32(BinaryMagic);
        w.U32(BinaryVersion);
        w.U32((uint)order.Count);
        foreach (var e in order)
        {
            w.U64(ids[e]);
            var parent = world.GetParent(e);
            w.U64(parent.IsNull ? 0 : ids[parent]);
            var components = SortedComponents(world, e).Where(c => c.Info.Serializer is not null).ToList();
            if (world.Has<PrefabInstance>(e)) components.Add((ComponentRegistry.Get(Component<PrefabInstance>.Id), (nint)Unsafe.AsPointer(ref world.Get<PrefabInstance>(e))));
            w.U32((uint)components.Count);
            foreach (var (info, data) in components)
            {
                w.U64(info.StableId);
                int lengthAt = w.Reserve(4);
                int start = w.Length;
                info.Serializer!.Visit((void*)data, w);
                w.PatchU32(lengthAt, (uint)(w.Length - start));
            }
        }
        return w.ToArray();
    }

    public static Entity[] LoadBinary(World world, ReadOnlySpan<byte> data, Action<string>? warn = null)
    {
        var r = new BinaryReadVisitor(data.ToArray());
        if (r.U32() != BinaryMagic) throw new FormatException("Not a gamegrl binary scene.");
        if (r.U32() != BinaryVersion) throw new FormatException("Unsupported binary scene version.");
        int count = (int)r.U32();

        // Pass 1: entities (references resolve in pass 2).
        int bodyStart = r.Position;
        var map = new Dictionary<ulong, Entity>(count);
        var created = new Entity[count];
        for (int i = 0; i < count; i++)
        {
            ulong id = r.U64();
            r.U64();
            created[i] = world.Spawn(new SceneId { Value = id });
            map[id] = created[i];
            int components = (int)r.U32();
            for (int c = 0; c < components; c++)
            {
                r.U64();
                r.Skip((int)r.U32());
            }
        }

        r.Position = bodyStart;
        r.Map = map;
        var scratch = new byte[256];
        for (int i = 0; i < count; i++)
        {
            var e = created[i];
            r.U64();
            ulong parent = r.U64();
            int components = (int)r.U32();
            for (int c = 0; c < components; c++)
            {
                ulong stable = r.U64();
                int length = (int)r.U32();
                int id = ComponentRegistry.FindByStableId(stable);
                if (id < 0 || ComponentRegistry.Get(id).Serializer is not { } serializer)
                {
                    warn?.Invoke($"Unknown component {stable:x16} skipped.");
                    r.Skip(length);
                    continue;
                }
                var info = ComponentRegistry.Get(id);
                if (scratch.Length < info.Size) scratch = new byte[info.Size];
                Array.Clear(scratch, 0, info.Size);
                fixed (byte* buffer = scratch)
                {
                    serializer.Visit(buffer, r);
                    world.AddRaw(e, id, buffer);
                }
            }
            if (parent != 0 && map.TryGetValue(parent, out var p)) world.SetParent(e, p);
        }
        return created;
    }

    /// <summary>Convert a text scene to the binary player format, flattening prefab instances (what the asset pipeline does).</summary>
    public static byte[] TextToBinary(string text, Func<Guid, string?>? prefabs = null, Action<string>? warn = null)
    {
        using var world = new World();
        LoadText(world, text, prefabs, warn);
        return SaveBinary(world);
    }

    // ------------------------------------------------------------------ shared

    /// <summary>Depth first from the roots, roots by scene id (or spawn order when unassigned), siblings in order.</summary>
    private static List<Entity> Order(World world)
    {
        var roots = new List<Entity>();
        foreach (var a in world.Archetypes)
            foreach (var e in a.Entities)
                if (!world.HasParent(e)) roots.Add(e);
        roots.Sort((x, y) =>
        {
            ulong ix = world.Has<SceneId>(x) ? world.Get<SceneId>(x).Value : ulong.MaxValue;
            ulong iy = world.Has<SceneId>(y) ? world.Get<SceneId>(y).Value : ulong.MaxValue;
            return ix != iy ? ix.CompareTo(iy) : x.Index.CompareTo(y.Index);
        });
        var order = new List<Entity>(world.EntityCount);
        void Walk(Entity e)
        {
            order.Add(e);
            foreach (var c in world.Children(e)) Walk(c);
        }
        foreach (var r in roots) Walk(r);
        return order;
    }

    private static Dictionary<Entity, ulong> SceneIds(World world, List<Entity> order)
    {
        var ids = new Dictionary<Entity, ulong>(order.Count);
        foreach (var e in order)
        {
            if (!world.Has<SceneId>(e)) world.Add(e, SceneId.New());
            ids[e] = world.Get<SceneId>(e).Value;
        }
        return ids;
    }

    /// <summary>The entity's components (except its <see cref="SceneId"/>) by name, with pointers to their data.</summary>
    private static List<(ComponentInfo Info, nint Data)> SortedComponents(World world, Entity e)
    {
        var (a, row) = world.Locate(e);
        var list = new List<(ComponentInfo, nint)>(a.Types.Length);
        for (int c = 0; c < a.Types.Length; c++)
        {
            var info = ComponentRegistry.Get(a.Types[c]);
            if (info.Id == Component<SceneId>.Id || info.Id == Component<PrefabInstance>.Id) continue; // headers carry these
            list.Add((info, (nint)a.Ptr(c, row)));
        }
        list.Sort((x, y) => string.CompareOrdinal(x.Item1.Name, y.Item1.Name));
        return list;
    }

    // ------------------------------------------------------------------ visitors

    private sealed class BinaryWriteVisitor(Dictionary<Entity, ulong> ids) : IFieldVisitor
    {
        private byte[] _buffer = new byte[4096];

        public int Length { get; private set; }

        private Span<byte> Take(int n)
        {
            if (Length + n > _buffer.Length) Array.Resize(ref _buffer, Math.Max(Length + n, _buffer.Length * 2));
            var s = _buffer.AsSpan(Length, n);
            Length += n;
            return s;
        }

        public int Reserve(int n)
        {
            int at = Length;
            Take(n);
            return at;
        }

        public void PatchU32(int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(at), v);
        public void U32(uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Take(4), v);
        public void U64(ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(Take(8), v);
        private void F32(float v) => BinaryPrimitives.WriteSingleLittleEndian(Take(4), v);
        public byte[] ToArray() => _buffer.AsSpan(0, Length).ToArray();

        public void Begin(string name) { }
        public void End() { }
        public void Field(string name, ref bool value) => Take(1)[0] = value ? (byte)1 : (byte)0;
        public void Field(string name, ref byte value) => Take(1)[0] = value;
        public void Field(string name, ref sbyte value) => Take(1)[0] = (byte)value;
        public void Field(string name, ref short value) => BinaryPrimitives.WriteInt16LittleEndian(Take(2), value);
        public void Field(string name, ref ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Take(2), value);
        public void Field(string name, ref int value) => BinaryPrimitives.WriteInt32LittleEndian(Take(4), value);
        public void Field(string name, ref uint value) => U32(value);
        public void Field(string name, ref long value) => BinaryPrimitives.WriteInt64LittleEndian(Take(8), value);
        public void Field(string name, ref ulong value) => U64(value);
        public void Field(string name, ref float value) => F32(value);
        public void Field(string name, ref double value) => BinaryPrimitives.WriteDoubleLittleEndian(Take(8), value);
        public void Field(string name, ref Vector2 v) { F32(v.X); F32(v.Y); }
        public void Field(string name, ref Vector3 v) { F32(v.X); F32(v.Y); F32(v.Z); }
        public void Field(string name, ref Vector4 v) { F32(v.X); F32(v.Y); F32(v.Z); F32(v.W); }
        public void Field(string name, ref Quaternion v) { F32(v.X); F32(v.Y); F32(v.Z); F32(v.W); }

        public void Field(string name, ref Matrix4x4 m)
        {
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    F32(m[r, c]);
        }

        public void Field(string name, ref Guid value) => value.TryWriteBytes(Take(16));

        public void Field(string name, ref FixedString64 value)
        {
            var utf8 = value.Utf8;
            Take(1)[0] = (byte)utf8.Length;
            utf8.CopyTo(Take(utf8.Length));
        }

        public void Field(string name, ref Entity value) => U64(value.IsNull || !ids.TryGetValue(value, out var id) ? 0 : id);
        public void Asset(string name, ref Guid value) => value.TryWriteBytes(Take(16));
        public void Enum(string name, ref long value, ReadOnlySpan<string> names, ReadOnlySpan<long> values) =>
            BinaryPrimitives.WriteInt64LittleEndian(Take(8), value);
    }

    private sealed class BinaryReadVisitor(byte[] data) : IFieldVisitor
    {
        public int Position;
        public Dictionary<ulong, Entity> Map = new();

        private ReadOnlySpan<byte> Take(int n)
        {
            if (Position + n > data.Length) throw new FormatException("Truncated binary scene.");
            var s = data.AsSpan(Position, n);
            Position += n;
            return s;
        }

        public void Skip(int n) => Take(n);
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
        private float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

        public void Begin(string name) { }
        public void End() { }
        public void Field(string name, ref bool value) => value = Take(1)[0] != 0;
        public void Field(string name, ref byte value) => value = Take(1)[0];
        public void Field(string name, ref sbyte value) => value = (sbyte)Take(1)[0];
        public void Field(string name, ref short value) => value = BinaryPrimitives.ReadInt16LittleEndian(Take(2));
        public void Field(string name, ref ushort value) => value = BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public void Field(string name, ref int value) => value = BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public void Field(string name, ref uint value) => value = U32();
        public void Field(string name, ref long value) => value = BinaryPrimitives.ReadInt64LittleEndian(Take(8));
        public void Field(string name, ref ulong value) => value = U64();
        public void Field(string name, ref float value) => value = F32();
        public void Field(string name, ref double value) => value = BinaryPrimitives.ReadDoubleLittleEndian(Take(8));
        public void Field(string name, ref Vector2 v) => v = new(F32(), F32());
        public void Field(string name, ref Vector3 v) => v = new(F32(), F32(), F32());
        public void Field(string name, ref Vector4 v) => v = new(F32(), F32(), F32(), F32());
        public void Field(string name, ref Quaternion v) => v = new(F32(), F32(), F32(), F32());

        public void Field(string name, ref Matrix4x4 m)
        {
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    m[r, c] = F32();
        }

        public void Field(string name, ref Guid value) => value = new Guid(Take(16));

        public void Field(string name, ref FixedString64 value)
        {
            int n = Take(1)[0];
            value = new FixedString64(Encoding.UTF8.GetString(Take(n)));
        }

        public void Field(string name, ref Entity value)
        {
            ulong id = U64();
            value = id != 0 && Map.TryGetValue(id, out var e) ? e : default;
        }

        public void Asset(string name, ref Guid value) => value = new Guid(Take(16));
        public void Enum(string name, ref long value, ReadOnlySpan<string> names, ReadOnlySpan<long> values) =>
            value = BinaryPrimitives.ReadInt64LittleEndian(Take(8));
    }
}
