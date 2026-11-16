using System.Globalization;
using System.Numerics;
using System.Text;

namespace Gamegrl;

/// <summary>Marks the root of a prefab instance; the rest of the instance is expanded from the prefab file.</summary>
public struct PrefabInstance : IComponent
{
    public AssetRef<PrefabAsset> Prefab;
}

/// <summary>Marker type for prefab assets.</summary>
public sealed class PrefabAsset;

// The text format (scenes and prefabs share it):
//
//   gamegrl scene 1
//
//   entity 3f9a0c1e5b7d2a46            a plain entity: everything written
//     parent 1a2b3c4d5e6f7081
//     Gamegrl.LocalTransform
//       Position = 0 1 0
//
//   entity 7c00000000000001            the root of a prefab instance: only what differs from the prefab
//     prefab asset:9f8e7d6c5b4a39281706f5e4d3c2b1a0
//     Gamegrl.LocalTransform
//       Position = 5 0 0
//
//   entity 7c00000000000001/0000000000000b0b      an entity inside that instance, by its path
//     - Gamegrl.Rendering.Sprite                  a component removed in this instance
//
//   entity 7c00000000000001/0000000000000c0c
//     removed                                     an entity deleted from this instance
//
// Entities inside an instance get ids derived from their path, so overrides stay attached to the same entity
// however deeply prefabs nest, and nothing extra is stored. Players never see prefabs: packing flattens them.
public static unsafe partial class Scene
{
    public const string TextHeader = "gamegrl scene 1";

    // ------------------------------------------------------------------ public API

    /// <summary>
    /// Write every entity in <paramref name="world"/>. Prefab instances are written as their differences from the
    /// prefab when <paramref name="prefabs"/> can resolve the prefab's text; otherwise they're flattened.
    /// </summary>
    public static string SaveText(World world, Func<Guid, string?>? prefabs = null)
    {
        var order = Order(world);
        var ids = SceneIds(world, order);
        var bySceneId = new Dictionary<ulong, Entity>(order.Count);
        foreach (var e in order) bySceneId[ids[e]] = e;

        // Id strings, and the baseline (what the prefab alone would produce) of every entity inside an instance.
        var idOf = new Dictionary<Entity, string>(order.Count);
        var baselines = new Dictionary<Entity, Baseline>();
        var deleted = new List<(string Id, string? Parent)>();
        foreach (var e in order)
        {
            if (idOf.ContainsKey(e)) continue; // already placed by an enclosing instance
            idOf[e] = ids[e].ToString("x16");
            if (prefabs is null || !world.Has<PrefabInstance>(e)) continue;
            var guid = world.Get<PrefabInstance>(e).Prefab.Guid;
            var root = new EntitySpec { Id = idOf[e], Prefab = guid };
            List<EntitySpec> expanded;
            try
            {
                expanded = Expand([root], prefabs, null, 0, []);
            }
            catch (FormatException)
            {
                continue; // a broken prefab: write the instance flattened rather than lose it
            }
            var canon = Canonicalize(expanded);
            foreach (var spec in expanded)
            {
                if (bySceneId.TryGetValue(IdToU64(spec.Id), out var we))
                {
                    idOf[we] = spec.Id;
                    baselines[we] = new Baseline(spec.Id, spec.Id == root.Id ? null : spec.Parent, spec.Id == root.Id, guid, canon[spec.Id]);
                }
                else if (spec.Id != root.Id) deleted.Add((spec.Id, spec.Parent));
            }
        }

        var sb = new StringBuilder();
        sb.Append(TextHeader).Append('\n');
        foreach (var e in order)
        {
            var fields = ComponentFields(world, e, idOf);
            var parent = world.GetParent(e);
            string? parentId = parent.IsNull ? null : idOf[parent];
            if (baselines.TryGetValue(e, out var b))
            {
                var (changed, removed) = Diff(fields, b.Components);
                bool parentChanged = parentId != b.Parent;
                if (!b.IsInstanceRoot && changed.Count == 0 && removed.Count == 0 && !parentChanged) continue;
                sb.Append('\n').Append("entity ").Append(b.Path).Append('\n');
                if (parentId is not null && (b.IsInstanceRoot || parentChanged)) sb.Append("  parent ").Append(parentId).Append('\n');
                if (b.IsInstanceRoot) sb.Append("  prefab asset:").Append(b.Prefab.ToString("N")).Append('\n');
                foreach (var r in removed) sb.Append("  - ").Append(r).Append('\n');
                WriteComponents(sb, changed);
            }
            else
            {
                sb.Append('\n').Append("entity ").Append(idOf[e]).Append('\n');
                if (parentId is not null) sb.Append("  parent ").Append(parentId).Append('\n');
                WriteComponents(sb, fields);
            }
        }
        // A deleted entity takes its subtree, so only the topmost deletions are written.
        var deletedIds = deleted.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (id, parent) in deleted)
            if (parent is null || !deletedIds.Contains(parent)) sb.Append('\n').Append("entity ").Append(id).Append("\n  removed\n");
        return sb.ToString();
    }

    /// <summary>Load a text scene into <paramref name="world"/>, expanding prefab instances; returns the new entities.</summary>
    public static Entity[] LoadText(World world, string text, Func<Guid, string?>? prefabs = null, Action<string>? warn = null)
    {
        var specs = Expand(Parse(text), prefabs, warn, 0, []);
        var map = Instantiate(world, specs, warn);
        var result = new Entity[specs.Count];
        for (int i = 0; i < specs.Count; i++) result[i] = map[specs[i].Id];
        return result;
    }

    /// <summary>Instantiate a prefab (e.g. spawning at runtime in the editor); returns the instance root.</summary>
    public static Entity Instantiate(World world, AssetRef<PrefabAsset> prefab, Func<Guid, string?> prefabs, Action<string>? warn = null)
    {
        var root = new EntitySpec { Id = SceneId.New().Value.ToString("x16"), Prefab = prefab.Guid };
        var specs = Expand([root], prefabs, warn, 0, []);
        return Instantiate(world, specs, warn)[root.Id];
    }

    // ------------------------------------------------------------------ document model

    private sealed class EntitySpec
    {
        public string Id = "";
        public string? Parent;
        public Guid Prefab;
        public bool Deleted;
        public readonly Dictionary<string, List<KeyValuePair<string, string>>> Components = new(StringComparer.Ordinal);
        public readonly List<string> Removed = new();
    }

    private sealed record Baseline(string Path, string? Parent, bool IsInstanceRoot, Guid Prefab,
        List<(string Component, List<KeyValuePair<string, string>> Fields)> Components);

    private static List<EntitySpec> Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != TextHeader) throw new FormatException($"Not a gamegrl scene (expected '{TextHeader}').");
        var specs = new List<EntitySpec>();
        EntitySpec? current = null;
        List<KeyValuePair<string, string>>? component = null;
        for (int n = 1; n < lines.Length; n++)
        {
            string line = lines[n];
            if (line.Length == 0 || line.TrimStart().StartsWith('#')) continue;
            if (line.StartsWith("entity ", StringComparison.Ordinal))
            {
                current = new EntitySpec { Id = line[7..].Trim() };
                specs.Add(current);
                component = null;
                continue;
            }
            if (current is null) throw new FormatException($"Line {n + 1}: expected 'entity'.");
            if (line.StartsWith("    ", StringComparison.Ordinal))
            {
                int eq = line.IndexOf(" = ", StringComparison.Ordinal);
                if (eq < 0) throw new FormatException($"Line {n + 1}: expected 'field = value'.");
                component?.Add(new(line[4..eq], line[(eq + 3)..]));
            }
            else if (line.StartsWith("  parent ", StringComparison.Ordinal)) { current.Parent = line[9..].Trim(); component = null; }
            else if (line.StartsWith("  prefab ", StringComparison.Ordinal)) { current.Prefab = ParseAsset(line[9..].Trim()); component = null; }
            else if (line.StartsWith("  - ", StringComparison.Ordinal)) { current.Removed.Add(line[4..].Trim()); component = null; }
            else if (line.Trim() == "removed") { current.Deleted = true; component = null; }
            else if (line.StartsWith("  ", StringComparison.Ordinal))
            {
                string name = line.Trim();
                if (!current.Components.TryGetValue(name, out component)) current.Components[name] = component = new();
            }
            else throw new FormatException($"Line {n + 1}: unexpected '{line}'.");
        }
        return specs;
    }

    private static Guid ParseAsset(string s) => Guid.ParseExact(s.StartsWith("asset:", StringComparison.Ordinal) ? s[6..] : s, "N");

    /// <summary>Expand prefab instances (recursively) and apply overrides; the result is a flat list of entities.</summary>
    private static List<EntitySpec> Expand(List<EntitySpec> file, Func<Guid, string?>? prefabs, Action<string>? warn, int depth, HashSet<Guid> stack)
    {
        if (depth > 32) throw new FormatException("Prefabs nest more than 32 deep.");
        var result = new List<EntitySpec>();
        var byId = new Dictionary<string, EntitySpec>(StringComparer.Ordinal);
        var overrides = new List<EntitySpec>();

        foreach (var spec in file)
        {
            if (spec.Prefab != Guid.Empty)
            {
                string? text = prefabs?.Invoke(spec.Prefab);
                if (text is null || stack.Contains(spec.Prefab))
                {
                    warn?.Invoke(text is null ? $"Prefab {spec.Prefab:N} not found; instance {spec.Id} keeps only its overrides."
                                              : $"Prefab {spec.Prefab:N} contains itself; instance {spec.Id} not expanded.");
                    Add(result, byId, Copy(spec, spec.Id, spec.Parent, null));
                    continue;
                }
                stack.Add(spec.Prefab);
                var inner = Expand(Parse(text), prefabs, warn, depth + 1, stack);
                stack.Remove(spec.Prefab);
                var roots = inner.Where(p => p.Parent is null).ToList();
                if (roots.Count != 1) throw new FormatException($"Prefab {spec.Prefab:N} must have exactly one root entity (has {roots.Count}).");
                var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var p in inner) idMap[p.Id] = p == roots[0] ? spec.Id : spec.Id + "/" + p.Id;
                foreach (var p in inner)
                {
                    var q = Copy(p, idMap[p.Id], p.Parent is null ? spec.Parent : idMap.GetValueOrDefault(p.Parent, p.Parent), idMap);
                    if (p == roots[0]) q.Prefab = spec.Prefab;
                    Add(result, byId, q);
                }
                Merge(byId[spec.Id], spec);
            }
            else if (byId.ContainsKey(spec.Id) || spec.Id.Contains('/')) overrides.Add(spec);
            else Add(result, byId, Copy(spec, spec.Id, spec.Parent, null));
        }

        foreach (var o in overrides)
        {
            if (!byId.TryGetValue(o.Id, out var target))
            {
                warn?.Invoke($"Override for {o.Id}, which no prefab instance contains; ignored.");
                continue;
            }
            if (o.Deleted) target.Deleted = true;
            if (o.Parent is not null) target.Parent = o.Parent;
            Merge(target, o);
        }

        // Deleting an entity deletes its subtree.
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var s in result)
                if (!s.Deleted && s.Parent is not null && byId.TryGetValue(s.Parent, out var p) && p.Deleted) { s.Deleted = true; changed = true; }
        }
        result.RemoveAll(s => s.Deleted);
        return result;
    }

    private static void Add(List<EntitySpec> result, Dictionary<string, EntitySpec> byId, EntitySpec spec)
    {
        if (!byId.TryAdd(spec.Id, spec)) throw new FormatException($"Duplicate entity {spec.Id}.");
        result.Add(spec);
    }

    private static EntitySpec Copy(EntitySpec s, string id, string? parent, Dictionary<string, string>? remap)
    {
        var c = new EntitySpec { Id = id, Parent = parent, Prefab = s.Prefab };
        foreach (var (name, fields) in s.Components)
        {
            var list = new List<KeyValuePair<string, string>>(fields.Count);
            foreach (var f in fields)
            {
                string v = f.Value;
                if (remap is not null && v.StartsWith('@') && remap.TryGetValue(v[1..], out var mapped)) v = "@" + mapped;
                list.Add(new(f.Key, v));
            }
            c.Components[name] = list;
        }
        return c;
    }

    private static void Merge(EntitySpec target, EntitySpec source)
    {
        foreach (var r in source.Removed) target.Components.Remove(r);
        foreach (var (name, fields) in source.Components)
        {
            if (!target.Components.TryGetValue(name, out var existing))
            {
                target.Components[name] = new List<KeyValuePair<string, string>>(fields);
                continue;
            }
            foreach (var f in fields)
            {
                int i = existing.FindIndex(x => x.Key == f.Key);
                if (i >= 0) existing[i] = f;
                else existing.Add(f);
            }
        }
    }

    /// <summary>An entity's runtime scene id: its hex id, or for entities inside instances, a hash of the path.</summary>
    private static ulong IdToU64(string id)
    {
        if (!id.Contains('/')) return ulong.Parse(id, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        ulong h = ComponentRegistry.StableId(id);
        return h == 0 ? 1 : h;
    }

    private static Dictionary<string, Entity> Instantiate(World world, List<EntitySpec> specs, Action<string>? warn)
    {
        var map = new Dictionary<string, Entity>(specs.Count, StringComparer.Ordinal);
        foreach (var s in specs) map[s.Id] = world.Spawn(new SceneId { Value = IdToU64(s.Id) });
        var reader = new TextReadVisitor(map, warn);
        var scratch = new byte[256];
        var fieldMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in specs)
        {
            var e = map[s.Id];
            foreach (var (name, fields) in s.Components)
            {
                int id = ComponentRegistry.FindByName(name);
                if (id < 0 || ComponentRegistry.Get(id).Serializer is not { } serializer)
                {
                    warn?.Invoke($"Entity {s.Id}: unknown component '{name}' skipped.");
                    continue;
                }
                var info = ComponentRegistry.Get(id);
                if (scratch.Length < info.Size) scratch = new byte[info.Size];
                Array.Clear(scratch, 0, info.Size);
                fieldMap.Clear();
                foreach (var f in fields) fieldMap[f.Key] = f.Value;
                reader.Load(fieldMap);
                fixed (byte* buffer = scratch)
                {
                    serializer.Visit(buffer, reader);
                    world.AddRaw(e, id, buffer);
                }
            }
            if (s.Prefab != Guid.Empty) world.Add(e, new PrefabInstance { Prefab = new AssetRef<PrefabAsset>(s.Prefab) });
        }
        foreach (var s in specs)
        {
            if (s.Parent is null) continue;
            if (map.TryGetValue(s.Parent, out var p)) world.SetParent(map[s.Id], p);
            else warn?.Invoke($"Entity {s.Id}: parent {s.Parent} is not in the scene.");
        }
        return map;
    }

    /// <summary>What each entity of an expanded instance looks like when written canonically (via a scratch world).</summary>
    private static Dictionary<string, List<(string Component, List<KeyValuePair<string, string>> Fields)>> Canonicalize(List<EntitySpec> expanded)
    {
        using var scratch = new World();
        var map = Instantiate(scratch, expanded, null);
        var idOf = new Dictionary<Entity, string>(map.Count);
        foreach (var (id, e) in map) idOf[e] = id;
        var result = new Dictionary<string, List<(string, List<KeyValuePair<string, string>>)>>(StringComparer.Ordinal);
        foreach (var (id, e) in map) result[id] = ComponentFields(scratch, e, idOf);
        return result;
    }

    private static List<(string Component, List<KeyValuePair<string, string>> Fields)> ComponentFields(World world, Entity e, Dictionary<Entity, string> idOf)
    {
        var result = new List<(string, List<KeyValuePair<string, string>>)>();
        var writer = new TextWriteVisitor(idOf);
        foreach (var (info, data) in SortedComponents(world, e))
        {
            if (info.Serializer is null) continue;
            writer.Begin();
            info.Serializer.Visit((void*)data, writer);
            result.Add((info.Name, writer.Fields));
        }
        return result;
    }

    private static (List<(string Component, List<KeyValuePair<string, string>> Fields)> Changed, List<string> Removed) Diff(
        List<(string Component, List<KeyValuePair<string, string>> Fields)> current,
        List<(string Component, List<KeyValuePair<string, string>> Fields)> baseline)
    {
        var changed = new List<(string, List<KeyValuePair<string, string>>)>();
        var removed = new List<string>();
        foreach (var (name, fields) in current)
        {
            int bi = baseline.FindIndex(b => b.Component == name);
            if (bi < 0)
            {
                changed.Add((name, fields)); // added in this instance: all of it
                continue;
            }
            var diff = fields.Where(f => !baseline[bi].Fields.Contains(f)).ToList();
            if (diff.Count > 0) changed.Add((name, diff));
        }
        foreach (var (name, _) in baseline)
            if (!current.Exists(c => c.Component == name)) removed.Add(name);
        return (changed, removed);
    }

    private static void WriteComponents(StringBuilder sb, List<(string Component, List<KeyValuePair<string, string>> Fields)> components)
    {
        foreach (var (name, fields) in components)
        {
            sb.Append("  ").Append(name).Append('\n');
            foreach (var f in fields) sb.Append("    ").Append(f.Key).Append(" = ").Append(f.Value).Append('\n');
        }
    }

    // ------------------------------------------------------------------ text visitors

    private sealed class TextWriteVisitor(Dictionary<Entity, string> ids) : IFieldVisitor
    {
        private readonly List<string> _path = new();

        public List<KeyValuePair<string, string>> Fields { get; private set; } = new();

        public void Begin()
        {
            _path.Clear();
            Fields = new List<KeyValuePair<string, string>>();
        }

        private void Put(string name, string value) =>
            Fields.Add(new(_path.Count == 0 ? name : string.Join('.', _path) + "." + name, value));

        private static string F(float v) => v.ToString(CultureInfo.InvariantCulture);

        public void Begin(string name) => _path.Add(name);
        public void End() => _path.RemoveAt(_path.Count - 1);
        public void Field(string name, ref bool value) => Put(name, value ? "true" : "false");
        public void Field(string name, ref byte value) => Put(name, value.ToString(CultureInfo.InvariantCulture));
        public void Field(string name, ref sbyte value) => Put(name, value.ToString(CultureInfo.InvariantCulture));
        public void Field(string name, ref short value) => Put(name, value.ToString(CultureInfo.InvariantCulture));
        public void Field(string name, ref ushort value) => Put(name, value.ToString(CultureInfo.InvariantCulture));
        public void Field(string name, ref int value) => Put(name, value.ToString(CultureInfo.InvariantCulture));
        public void Field(string name, ref uint value) => Put(name, value.ToString(CultureInfo.InvariantCulture));
        public void Field(string name, ref long value) => Put(name, value.ToString(CultureInfo.InvariantCulture));
        public void Field(string name, ref ulong value) => Put(name, value.ToString(CultureInfo.InvariantCulture));
        public void Field(string name, ref float value) => Put(name, F(value));
        public void Field(string name, ref double value) => Put(name, value.ToString(CultureInfo.InvariantCulture));
        public void Field(string name, ref Vector2 v) => Put(name, $"{F(v.X)} {F(v.Y)}");
        public void Field(string name, ref Vector3 v) => Put(name, $"{F(v.X)} {F(v.Y)} {F(v.Z)}");
        public void Field(string name, ref Vector4 v) => Put(name, $"{F(v.X)} {F(v.Y)} {F(v.Z)} {F(v.W)}");
        public void Field(string name, ref Quaternion v) => Put(name, $"{F(v.X)} {F(v.Y)} {F(v.Z)} {F(v.W)}");

        public void Field(string name, ref Matrix4x4 m)
        {
            var parts = new string[16];
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    parts[r * 4 + c] = F(m[r, c]);
            Put(name, string.Join(' ', parts));
        }

        public void Field(string name, ref Guid value) => Put(name, value.ToString("N"));
        public void Field(string name, ref FixedString64 value) => Put(name, Quote(value.ToString()));
        public void Field(string name, ref Entity value) =>
            Put(name, value.IsNull || !ids.TryGetValue(value, out var id) ? "none" : "@" + id);
        public void Asset(string name, ref Guid value) => Put(name, value == Guid.Empty ? "none" : "asset:" + value.ToString("N"));

        public void Enum(string name, ref long value, ReadOnlySpan<string> names, ReadOnlySpan<long> values)
        {
            int i = values.IndexOf(value);
            Put(name, i >= 0 ? names[i] : value.ToString(CultureInfo.InvariantCulture));
        }

        private static string Quote(string s)
        {
            var b = new StringBuilder(s.Length + 2).Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': b.Append("\\r"); break;
                    case '\t': b.Append("\\t"); break;
                    default: b.Append(c); break;
                }
            }
            return b.Append('"').ToString();
        }
    }

    private sealed class TextReadVisitor(Dictionary<string, Entity> map, Action<string>? warn) : IFieldVisitor
    {
        private readonly List<string> _path = new();
        private Dictionary<string, string> _fields = new();

        public void Load(Dictionary<string, string> fields)
        {
            _fields = fields;
            _path.Clear();
        }

        private bool Get(string name, out string value)
        {
            string key = _path.Count == 0 ? name : string.Join('.', _path) + "." + name;
            return _fields.TryGetValue(key, out value!);
        }

        private static float P(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

        private static float[] Floats(string s, int n)
        {
            var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != n) throw new FormatException($"Expected {n} numbers, got '{s}'.");
            var f = new float[n];
            for (int i = 0; i < n; i++) f[i] = P(parts[i]);
            return f;
        }

        public void Begin(string name) => _path.Add(name);
        public void End() => _path.RemoveAt(_path.Count - 1);
        public void Field(string name, ref bool value) { if (Get(name, out var s)) value = s == "true"; }
        public void Field(string name, ref byte value) { if (Get(name, out var s)) value = byte.Parse(s, CultureInfo.InvariantCulture); }
        public void Field(string name, ref sbyte value) { if (Get(name, out var s)) value = sbyte.Parse(s, CultureInfo.InvariantCulture); }
        public void Field(string name, ref short value) { if (Get(name, out var s)) value = short.Parse(s, CultureInfo.InvariantCulture); }
        public void Field(string name, ref ushort value) { if (Get(name, out var s)) value = ushort.Parse(s, CultureInfo.InvariantCulture); }
        public void Field(string name, ref int value) { if (Get(name, out var s)) value = int.Parse(s, CultureInfo.InvariantCulture); }
        public void Field(string name, ref uint value) { if (Get(name, out var s)) value = uint.Parse(s, CultureInfo.InvariantCulture); }
        public void Field(string name, ref long value) { if (Get(name, out var s)) value = long.Parse(s, CultureInfo.InvariantCulture); }
        public void Field(string name, ref ulong value) { if (Get(name, out var s)) value = ulong.Parse(s, CultureInfo.InvariantCulture); }
        public void Field(string name, ref float value) { if (Get(name, out var s)) value = P(s); }
        public void Field(string name, ref double value) { if (Get(name, out var s)) value = double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture); }
        public void Field(string name, ref Vector2 value) { if (Get(name, out var s)) { var f = Floats(s, 2); value = new(f[0], f[1]); } }
        public void Field(string name, ref Vector3 value) { if (Get(name, out var s)) { var f = Floats(s, 3); value = new(f[0], f[1], f[2]); } }
        public void Field(string name, ref Vector4 value) { if (Get(name, out var s)) { var f = Floats(s, 4); value = new(f[0], f[1], f[2], f[3]); } }
        public void Field(string name, ref Quaternion value) { if (Get(name, out var s)) { var f = Floats(s, 4); value = new(f[0], f[1], f[2], f[3]); } }

        public void Field(string name, ref Matrix4x4 value)
        {
            if (!Get(name, out var s)) return;
            var f = Floats(s, 16);
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    value[r, c] = f[r * 4 + c];
        }

        public void Field(string name, ref Guid value) { if (Get(name, out var s)) value = Guid.ParseExact(s, "N"); }
        public void Field(string name, ref FixedString64 value) { if (Get(name, out var s)) value = new FixedString64(Unquote(s)); }

        public void Field(string name, ref Entity value)
        {
            if (!Get(name, out var s) || s == "none") return;
            if (map.TryGetValue(s[1..], out var e)) value = e;
            else warn?.Invoke($"Reference to entity {s[1..]}, which is not in the scene.");
        }

        public void Asset(string name, ref Guid value)
        {
            if (Get(name, out var s) && s != "none") value = ParseAsset(s);
        }

        public void Enum(string name, ref long value, ReadOnlySpan<string> names, ReadOnlySpan<long> values)
        {
            if (!Get(name, out var s)) return;
            int i = names.IndexOf(s);
            value = i >= 0 ? values[i] : long.Parse(s, CultureInfo.InvariantCulture);
        }

        private static string Unquote(string s)
        {
            if (s.Length < 2 || s[0] != '"' || s[^1] != '"') throw new FormatException($"Expected a quoted string, got {s}.");
            var b = new StringBuilder(s.Length);
            for (int i = 1; i < s.Length - 1; i++)
            {
                char c = s[i];
                if (c != '\\') { b.Append(c); continue; }
                c = s[++i];
                b.Append(c switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => c });
            }
            return b.ToString();
        }
    }
}
