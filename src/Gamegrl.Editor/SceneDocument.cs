namespace Gamegrl.Editor;

/// <summary>
/// The edit transaction boundary. Structural edits (<see cref="Edit"/>) store text splices of the whole scene.
/// Value edits inside <see cref="BeginEntityEdit"/> store only the bytes of the components that changed, so
/// clicks, field edits and gizmo drags never serialize the scene.
/// </summary>
/// <remarks>History is strictly LIFO and only this class mutates the scene between entries, so each entry is
/// applied to exactly the state it was recorded against. Entries still verify their target and reset history
/// (with a warning) when something outside the document changed the scene.</remarks>
public sealed unsafe class SceneDocument(World world, Func<Guid, string?>? prefabs = null)
{
    private abstract record Change(long Serial, ulong BeforeSelection, ulong AfterSelection);
    private sealed record TextChange(long Serial, int Start, string Removed, string Inserted, ulong BeforeSelection, ulong AfterSelection)
        : Change(Serial, BeforeSelection, AfterSelection);
    private sealed record ValueChange(long Serial, ulong Entity, ComponentValue[] Before, ComponentValue[] After)
        : Change(Serial, Entity, Entity);
    /// <summary>One component's raw bytes, plus the scene ids of its entity fields (handles change when the world is rebuilt).</summary>
    private readonly record struct ComponentValue(int Type, byte[] Bytes, ulong[] References);
    private sealed class EntityEdit(Entity entity, ulong sceneId, Archetype archetype, ComponentValue[] values)
    {
        public readonly Entity Entity = entity;
        public readonly ulong SceneId = sceneId;
        public readonly Archetype Archetype = archetype;
        public readonly ComponentValue[] Values = values;
    }

    private readonly Stack<Change> _undo = new(), _redo = new();
    private string? _before;
    private ulong _beforeSelection;
    private EntityEdit? _entityEdit;
    private string? _playSnapshot;
    private Time _editTime;
    private ulong _playSelection;
    private string _savedText = "";
    private long _nextSerial = 1, _baseSerial, _savedSerial;

    public World World { get; } = world;
    public Entity Selected { get; set; }
    public ulong SelectedId => SceneIdOf(Selected);
    public bool Playing => _playSnapshot is not null;
    public bool CanUndo => !Playing && Held is null && _undo.Count > 0;
    public bool CanRedo => !Playing && Held is null && _redo.Count > 0;
    /// <summary>The scene's text, kept verbatim while no loaded code can represent it. Saving writes it unchanged.</summary>
    public string? Held { get; private set; }
    /// <summary>Serializes the whole scene: use for saving and structural history, not per frame.</summary>
    public string Text => Held ?? Scene.SaveText(World, prefabs);
    /// <summary>Unsaved changes, tracked by history position rather than by comparing text.</summary>
    public bool Dirty => StateSerial != _savedSerial;
    /// <summary>Increments whenever the scene changes through this document.</summary>
    public long Revision { get; private set; }
    private long StateSerial => _undo.TryPeek(out var top) ? top.Serial : _baseSerial;

    public void SelectSceneId(ulong id) => Selected = FindSceneId(id);

    public void Open(string text)
    {
        if (Playing) throw new InvalidOperationException("Stop play mode before opening a scene.");
        Restore(text);
        ClearHistory();
        _savedText = Text;
        _savedSerial = StateSerial;
    }

    /// <summary>Record a completed write. Edits made while the write ran keep the scene dirty.</summary>
    public void MarkSaved(string text)
    {
        _savedText = text;
        _savedSerial = text == Text ? StateSerial : -1;
    }

    /// <summary>Recompare with the last saved text, after a reload that may have changed the schema.</summary>
    public void RecheckSaved() => _savedSerial = Text == _savedText ? StateSerial : -1;

    /// <summary>Forget history; whether the scene is dirty is unchanged.</summary>
    public void ClearHistory()
    {
        bool dirty = Dirty;
        _undo.Clear(); _redo.Clear(); _before = null; _entityEdit = null;
        _baseSerial = _nextSerial++;
        _savedSerial = dirty ? -1 : _baseSerial;
    }

    /// <summary>Keep the edit scene as text and empty the world, e.g. when game code fails to load.
    /// Play mode ends and its edit snapshot is what gets held. Restore or Open releases it.</summary>
    public void Hold(string liveText)
    {
        string text = _playSnapshot ?? liveText;
        if (_playSnapshot is not null && World.HasResource<Time>()) World.Resource<Time>() = _editTime;
        _playSnapshot = null;
        World.Clear(); Selected = default;
        ClearHistory();
        Held = text;
        Revision++;
    }

    /// <summary>Begin a structural edit (spawn, destroy, reparent, add or remove components). Serializes the scene.</summary>
    public void BeginEdit()
    {
        if (Playing || Held is not null || _before is not null) return;
        CommitEntityEdit();
        _before = Text;
        _beforeSelection = SelectedId;
    }

    /// <summary>Begin editing component values of one entity. Only its component bytes are captured.
    /// Structural changes must go through <see cref="Edit"/>, which commits this edit first.</summary>
    public void BeginEntityEdit(Entity entity)
    {
        if (Playing || Held is not null || _before is not null || _entityEdit is not null) return;
        ulong id = SceneIdOf(entity);
        if (id == 0) { BeginEdit(); return; } // unaddressable by id: fall back to a whole-scene edit
        var (archetype, row) = World.Locate(entity);
        var values = new ComponentValue[archetype.Types.Length];
        for (int c = 0; c < values.Length; c++)
            values[c] = new ComponentValue(archetype.Types[c], new ReadOnlySpan<byte>(archetype.Ptr(c, row), archetype.Sizes[c]).ToArray(), []);
        _entityEdit = new EntityEdit(entity, id, archetype, values);
    }

    public void CommitEdit()
    {
        CommitEntityEdit();
        if (_before is not { } before) return;
        _before = null;
        string after = Text;
        if (before == after) return;
        int start = 0, end = 0;
        while (start < Math.Min(before.Length, after.Length) && before[start] == after[start]) start++;
        while (end < Math.Min(before.Length, after.Length) - start && before[^(end + 1)] == after[^(end + 1)]) end++;
        Push(new TextChange(_nextSerial++, start, before[start..(before.Length - end)], after[start..(after.Length - end)], _beforeSelection, SelectedId));
    }

    private void CommitEntityEdit()
    {
        if (_entityEdit is not { } edit) return;
        _entityEdit = null;
        if (!World.IsAlive(edit.Entity) || World.Locate(edit.Entity).Archetype != edit.Archetype)
        {
            ClearHistory(); _savedSerial = -1; Revision++;
            Log.Warn("An entity edit changed structure outside SceneDocument.Edit; undo history was reset.");
            return;
        }
        int row = World.Locate(edit.Entity).Row;
        List<ComponentValue>? before = null, after = null;
        foreach (var value in edit.Values)
        {
            int c = edit.Archetype.ColumnOf(value.Type);
            var live = new ReadOnlySpan<byte>(edit.Archetype.Ptr(c, row), value.Bytes.Length);
            if (live.SequenceEqual(value.Bytes)) continue;
            byte[] now = live.ToArray();
            (before ??= []).Add(value with { References = References(value.Type, value.Bytes) });
            (after ??= []).Add(new ComponentValue(value.Type, now, References(value.Type, now)));
        }
        if (before is null) return;
        Push(new ValueChange(_nextSerial++, edit.SceneId, before.ToArray(), after!.ToArray()));
    }

    private void Push(Change change) { _undo.Push(change); _redo.Clear(); Revision++; }

    public void Edit(Action action)
    {
        if (Playing || Held is not null) return;
        BeginEdit();
        try { action(); }
        finally { CommitEdit(); }
    }

    public void Undo()
    {
        CommitEdit();
        if (!CanUndo || !Apply(_undo.Peek(), undo: true)) return;
        _redo.Push(_undo.Pop()); Revision++;
    }

    public void Redo()
    {
        if (!CanRedo || !Apply(_redo.Peek(), undo: false)) return;
        _undo.Push(_redo.Pop()); Revision++;
    }

    private bool Apply(Change change, bool undo)
    {
        bool applied = change switch
        {
            TextChange text => ApplyText(text, undo),
            ValueChange values => ApplyValues(values, undo),
            _ => false,
        };
        if (!applied) { ClearHistory(); Log.Warn("Undo history reset after an external scene or schema change."); return false; }
        SelectSceneId(undo ? change.BeforeSelection : change.AfterSelection);
        return true;
    }

    private bool ApplyText(TextChange change, bool undo)
    {
        string text = Text;
        string expected = undo ? change.Inserted : change.Removed;
        if (change.Start + expected.Length > text.Length || string.CompareOrdinal(text, change.Start, expected, 0, expected.Length) != 0) return false;
        Restore(string.Concat(text.AsSpan(0, change.Start), undo ? change.Removed : change.Inserted, text.AsSpan(change.Start + expected.Length)));
        return true;
    }

    private bool ApplyValues(ValueChange change, bool undo)
    {
        var entity = FindSceneId(change.Entity);
        if (entity.IsNull) return false;
        var (archetype, row) = World.Locate(entity);
        var values = undo ? change.Before : change.After;
        foreach (var value in values)
        {
            int c = archetype.ColumnOf(value.Type);
            if (c < 0 || archetype.Sizes[c] != value.Bytes.Length) return false;
        }
        foreach (var value in values)
        {
            byte* target = archetype.Ptr(archetype.ColumnOf(value.Type), row);
            value.Bytes.CopyTo(new Span<byte>(target, value.Bytes.Length));
            if (value.References.Length > 0) EntityReferences.Resolve(this, value.Type, target, value.References);
        }
        return true;
    }

    public void Play()
    {
        if (Playing) return;
        CommitEdit();
        _playSnapshot = Text;
        _playSelection = SelectedId;
        if (World.HasResource<Time>())
        {
            _editTime = World.Resource<Time>();
            World.Resource<Time>() = new Time { FixedDelta = _editTime.FixedDelta };
        }
    }

    public void Stop()
    {
        if (_playSnapshot is not { } text) return;
        Restore(text);
        SelectSceneId(_playSelection);
        _playSnapshot = null;
        if (World.HasResource<Time>()) World.Resource<Time>() = _editTime;
    }

    public void Restore(string text)
    {
        // Parse before replacing the live scene so malformed files leave the current scene intact.
        using (var check = new World()) Scene.LoadText(check, text, prefabs, Log.Warn);
        ulong selection = SelectedId;
        World.Clear();
        var loaded = Scene.LoadText(World, text, prefabs, Log.Warn);
        Selected = default;
        foreach (var e in loaded)
            if (SceneIdOf(e) == selection) Selected = e;
        Held = null;
        Revision++;
    }

    internal ulong SceneIdOf(Entity e) => World.IsAlive(e) && World.Has<SceneId>(e) ? World.Get<SceneId>(e).Value : 0;

    internal Entity FindSceneId(ulong id)
    {
        if (id == 0) return default;
        int sceneId = Component<SceneId>.Id;
        foreach (var archetype in World.Archetypes)
        {
            int c = archetype.ColumnOf(sceneId);
            if (c < 0) continue;
            var ids = archetype.GetSpan<SceneId>();
            for (int i = 0; i < ids.Length; i++) if (ids[i].Value == id) return archetype.Entities[i];
        }
        return default;
    }

    private ulong[] References(int type, byte[] bytes)
    {
        fixed (byte* p = bytes) return EntityReferences.Capture(this, type, p);
    }

    /// <summary>Converts a component's entity fields to scene ids and back, through its generated visitor.</summary>
    private sealed class EntityReferences : IFieldVisitor
    {
        [ThreadStatic] private static EntityReferences? _shared;
        private readonly List<ulong> _ids = new();
        private SceneDocument _document = null!;
        private ulong[]? _resolve;
        private int _next;

        public static ulong[] Capture(SceneDocument document, int type, void* component)
        {
            if (ComponentRegistry.Get(type).Serializer is not { } serializer) return [];
            var visitor = _shared ??= new EntityReferences();
            visitor._document = document; visitor._resolve = null; visitor._ids.Clear();
            serializer.Visit(component, visitor);
            return visitor._ids.Count == 0 ? [] : visitor._ids.ToArray();
        }

        public static void Resolve(SceneDocument document, int type, void* component, ulong[] ids)
        {
            if (ComponentRegistry.Get(type).Serializer is not { } serializer) return;
            var visitor = _shared ??= new EntityReferences();
            visitor._document = document; visitor._resolve = ids; visitor._next = 0;
            serializer.Visit(component, visitor);
            visitor._resolve = null;
        }

        public void Field(string name, ref Entity value)
        {
            if (_resolve is null) _ids.Add(_document.SceneIdOf(value));
            else if (_next < _resolve.Length) value = _document.FindSceneId(_resolve[_next++]);
        }
        public void Begin(string name) { }
        public void End() { }
        public void Field(string name, ref bool value) { }
        public void Field(string name, ref byte value) { }
        public void Field(string name, ref sbyte value) { }
        public void Field(string name, ref short value) { }
        public void Field(string name, ref ushort value) { }
        public void Field(string name, ref int value) { }
        public void Field(string name, ref uint value) { }
        public void Field(string name, ref long value) { }
        public void Field(string name, ref ulong value) { }
        public void Field(string name, ref float value) { }
        public void Field(string name, ref double value) { }
        public void Field(string name, ref System.Numerics.Vector2 value) { }
        public void Field(string name, ref System.Numerics.Vector3 value) { }
        public void Field(string name, ref System.Numerics.Vector4 value) { }
        public void Field(string name, ref System.Numerics.Quaternion value) { }
        public void Field(string name, ref System.Numerics.Matrix4x4 value) { }
        public void Field(string name, ref Guid value) { }
        public void Field(string name, ref FixedString64 value) { }
        public void Asset(string name, ref Guid value) { }
        public void Enum(string name, ref long value, ReadOnlySpan<string> names, ReadOnlySpan<long> values) { }
    }
}
