namespace Gamegrl.Editor;

/// <summary>The edit transaction boundary. History stores text splices, using the generated serializers.</summary>
public sealed class SceneDocument(World world, Func<Guid, string?>? prefabs = null)
{
    private sealed record TextEdit(int Start, string Removed, string Inserted, ulong BeforeSelection, ulong AfterSelection);
    private readonly Stack<TextEdit> _undo = new(), _redo = new();
    private string? _before;
    private string? _playSnapshot;
    private string _saved = "";
    private string? _historyState;
    private Time _editTime;
    private ulong _playSelection;
    private ulong _beforeSelection;
    private bool _dirty;
    public World World { get; } = world;
    public Entity Selected { get; set; }
    public ulong SelectedId => World.IsAlive(Selected) && World.Has<SceneId>(Selected) ? World.Get<SceneId>(Selected).Value : 0;
    public void SelectSceneId(ulong id)
    {
        Selected = default;
        foreach (var a in World.Archetypes)
            foreach (var e in a.Entities)
                if (World.Has<SceneId>(e) && World.Get<SceneId>(e).Value == id) { Selected = e; return; }
    }
    public void ClearHistory() { _undo.Clear(); _redo.Clear(); _before = null; _historyState = null; }
    public bool Playing => _playSnapshot is not null;
    public bool CanUndo => !Playing && _undo.Count > 0;
    public bool CanRedo => !Playing && _redo.Count > 0;
    public string Text => Scene.SaveText(World, prefabs);
    public bool Dirty => _dirty;

    public void Open(string text)
    {
        if (Playing) throw new InvalidOperationException("Stop play mode before opening a scene.");
        Restore(text);
        ClearHistory();
        _saved = Text;
        _dirty = false;
    }

    public void MarkSaved(string text) { _saved = text; _dirty = Text != _saved; }
    public void BeginEdit()
    {
        if (Playing || _before is not null) return;
        _before = Text;
        _beforeSelection = SelectedId;
    }
    public void CommitEdit()
    {
        if (_before is not { } before) return;
        _before = null;
        string after = Text;
        _dirty = after != _saved;
        if (before == after) return;
        int start = 0, end = 0;
        while (start < Math.Min(before.Length, after.Length) && before[start] == after[start]) start++;
        while (end < Math.Min(before.Length, after.Length) - start && before[^(end + 1)] == after[^(end + 1)]) end++;
        _undo.Push(new TextEdit(start, before[start..(before.Length - end)], after[start..(after.Length - end)], _beforeSelection, SelectedId));
        _redo.Clear();
        _historyState = after;
    }
    public void Edit(Action action)
    {
        if (Playing) return;
        BeginEdit();
        try { action(); }
        finally { CommitEdit(); }
    }
    public void Undo()
    {
        CommitEdit();
        if (!CanUndo) return;
        var edit = _undo.Peek();
        string text = Text;
        if (text != _historyState) { ClearHistory(); Log.Warn("Undo history reset after an external scene or schema change."); return; }
        Restore(text[..edit.Start] + edit.Removed + text[(edit.Start + edit.Inserted.Length)..]);
        SelectSceneId(edit.BeforeSelection);
        _redo.Push(_undo.Pop());
        _historyState = Text;
    }
    public void Redo()
    {
        if (!CanRedo) return;
        var edit = _redo.Peek();
        string text = Text;
        if (text != _historyState) { ClearHistory(); Log.Warn("Undo history reset after an external scene or schema change."); return; }
        Restore(text[..edit.Start] + edit.Inserted + text[(edit.Start + edit.Removed.Length)..]);
        SelectSceneId(edit.AfterSelection);
        _undo.Push(_redo.Pop());
        _historyState = Text;
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
        ulong selection = World.IsAlive(Selected) && World.Has<SceneId>(Selected) ? World.Get<SceneId>(Selected).Value : 0;
        World.Clear();
        var loaded = Scene.LoadText(World, text, prefabs, Log.Warn);
        Selected = default;
        foreach (var e in loaded)
            if (World.Has<SceneId>(e) && World.Get<SceneId>(e).Value == selection) Selected = e;
        _dirty = Text != _saved;
    }
}
