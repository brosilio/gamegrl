using Gamegrl.UI;

namespace Gamegrl.Editor;

public enum DockRegion { Left, Center, Right, Bottom }
public sealed record EditorPanel(string Id, string Title, DockRegion Region, Action<EditorContext, Rect> Draw);
/// <summary>A command palette entry. <paramref name="Shortcut"/> is display text only, such as "Ctrl+S".</summary>
public sealed record EditorCommand(string Id, string Label, Action<EditorContext> Execute, string? Shortcut = null);
public interface IEditorExtension { void Register(EditorRegistry registry); }
public delegate void ComponentDrawer<T>(ref T component, EditorContext context) where T : unmanaged, IComponent;

public sealed class EditorContext
{
    internal EditorContext(App app, CellUi ui, SceneDocument document, EditorJobs jobs)
    { App = app; Ui = ui; Document = document; Jobs = jobs; }
    public App App { get; }
    public World World => App.World;
    public CellUi Ui { get; }
    public SceneDocument Document { get; }
    public EditorJobs Jobs { get; }
    public void Edit(Action edit) => Document.Edit(edit);
}

/// <summary>Editor-only registration surface, shared by built-ins and extension assemblies.</summary>
public sealed class EditorRegistry : IDisposable
{
    private readonly Dictionary<string, EditorPanel> _panels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EditorCommand> _commands = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, Action<nint, EditorContext>> _drawers = new();
    /// <summary>Changes whenever a panel, command or drawer is added or removed, so hosts can cache derived lists.</summary>
    public int Version { get; private set; }
    public IReadOnlyCollection<EditorPanel> Panels => _panels.Values;
    public IReadOnlyCollection<EditorCommand> Commands => _commands.Values;
    public EditorCommand? FindCommand(string id) => _commands.GetValueOrDefault(id);
    public EditorPanel? FindPanel(string id) => _panels.GetValueOrDefault(id);
    public void AddPanel(string id, string title, DockRegion region, Action<EditorContext, Rect> draw)
    {
        ValidateId(id);
        _panels.Add(id, new(id, title, region, draw));
        Version++;
    }
    public void AddCommand(string id, string label, Action<EditorContext> execute, string? shortcut = null)
    {
        ValidateId(id);
        _commands.Add(id, new(id, label, execute, shortcut));
        Version++;
    }
    public unsafe void AddDrawer<T>(ComponentDrawer<T> drawer) where T : unmanaged, IComponent
    {
        _drawers.Add(ComponentRegistry.Get(Component<T>.Id).StableId, (pointer, context) => drawer(ref *(T*)pointer, context));
        Version++;
    }
    internal bool DrawComponent(ulong stableId, nint pointer, EditorContext context)
    {
        if (!_drawers.TryGetValue(stableId, out var draw)) return false;
        draw(pointer, context); return true;
    }
    public bool RemovePanel(string id) => Changed(_panels.Remove(id));
    public bool RemoveCommand(string id) => Changed(_commands.Remove(id));
    public bool RemoveDrawer<T>() where T : unmanaged, IComponent => Changed(_drawers.Remove(ComponentRegistry.Get(Component<T>.Id).StableId));
    private bool Changed(bool removed)
    {
        if (removed) Version++;
        return removed;
    }
    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Any(char.IsWhiteSpace)) throw new ArgumentException("Editor ids must be nonempty and contain no whitespace.", nameof(id));
    }
    /// <summary>Register one extension transactionally; dispose its scope before unloading its assembly.</summary>
    public IDisposable Register(IEditorExtension extension)
    {
        var panels = new Dictionary<string, EditorPanel>(_panels, StringComparer.Ordinal);
        var commands = new Dictionary<string, EditorCommand>(_commands, StringComparer.Ordinal);
        var drawers = new Dictionary<ulong, Action<nint, EditorContext>>(_drawers);
        void Restore()
        {
            _panels.Clear(); foreach (var item in panels) _panels.Add(item.Key, item.Value);
            _commands.Clear(); foreach (var item in commands) _commands.Add(item.Key, item.Value);
            _drawers.Clear(); foreach (var item in drawers) _drawers.Add(item.Key, item.Value);
            Version++;
        }
        try { extension.Register(this); }
        catch { Restore(); throw; }
        // Scopes are disposed in reverse registration order, restoring their predecessor's registrations.
        return new Registration(() => { Restore(); if (extension is IDisposable disposable) disposable.Dispose(); });
    }
    private sealed class Registration(Action restore) : IDisposable
    {
        private Action? _restore = restore;
        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
    public void Dispose()
    {
        _drawers.Clear(); _commands.Clear(); _panels.Clear();
        Version++;
    }
}

/// <summary>Tab ownership and selection, independent of rendering or OS input.</summary>
public sealed class DockLayout
{
    public static readonly DockRegion[] Regions = Enum.GetValues<DockRegion>();
    private readonly List<string>[] _tabs = [new(), new(), new(), new()];
    private readonly string?[] _selected = new string?[Regions.Length];

    public IReadOnlyList<string> Tabs(DockRegion region) => _tabs[(int)region];
    public string? Selected(DockRegion region) => _selected[(int)region];

    /// <summary>Drop tabs whose panels are gone and place new panels in their default region.</summary>
    public void Sync(IEnumerable<EditorPanel> panels)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var panel in panels) known.Add(panel.Id);
        for (int r = 0; r < _tabs.Length; r++)
        {
            _tabs[r].RemoveAll(id => !known.Contains(id));
            if (_selected[r] is not { } selected || !_tabs[r].Contains(selected)) _selected[r] = _tabs[r].Count > 0 ? _tabs[r][0] : null;
        }
        foreach (var panel in panels)
        {
            if (Find(panel.Id) >= 0) continue;
            int region = (int)panel.Region;
            _tabs[region].Add(panel.Id);
            _selected[region] ??= panel.Id;
        }
    }

    public void Select(DockRegion region, string id)
    {
        if (!_tabs[(int)region].Contains(id)) throw new ArgumentException("Panel is not in that region.");
        _selected[(int)region] = id;
    }

    public void Move(string id, DockRegion target, int index = int.MaxValue)
    {
        int source = Find(id);
        if (source < 0) throw new ArgumentException("Unknown panel: " + id);
        _tabs[source].Remove(id);
        if (_selected[source] == id) _selected[source] = _tabs[source].Count > 0 ? _tabs[source][0] : null;
        var tabs = _tabs[(int)target];
        tabs.Insert(Math.Clamp(index, 0, tabs.Count), id);
        _selected[(int)target] = id;
    }

    private int Find(string id)
    {
        for (int r = 0; r < _tabs.Length; r++)
            if (_tabs[r].Contains(id)) return r;
        return -1;
    }
}
