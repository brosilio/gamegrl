using Gamegrl.UI;

namespace Gamegrl.Editor;

public enum DockRegion { Left, Center, Right, Bottom }
public sealed record EditorPanel(string Id, string Title, DockRegion Region, Action<EditorContext, Rect> Draw);
public sealed record EditorMenuItem(string Id, string Label, Action<EditorContext> Execute);
public interface IEditorExtension { void Register(EditorRegistry registry); }
public delegate void ComponentDrawer<T>(ref T component, EditorContext context) where T : unmanaged, IComponent;

public sealed class EditorContext
{
    internal EditorContext(App app, Ui ui, SceneDocument document, EditorJobs jobs)
    { App = app; Ui = ui; Document = document; Jobs = jobs; }
    public App App { get; }
    public World World => App.World;
    public Ui Ui { get; }
    public SceneDocument Document { get; }
    public EditorJobs Jobs { get; }
    public void Edit(Action edit) => Document.Edit(edit);
}

/// <summary>Editor-only registration surface, shared by built-ins and extension assemblies.</summary>
public sealed class EditorRegistry : IDisposable
{
    private readonly Dictionary<string, EditorPanel> _panels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EditorMenuItem> _menus = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, Action<nint, EditorContext>> _drawers = new();
    public IReadOnlyCollection<EditorPanel> Panels => _panels.Values;
    public IReadOnlyCollection<EditorMenuItem> Menus => _menus.Values;
    public EditorPanel? FindPanel(string id) => _panels.GetValueOrDefault(id);
    public void AddPanel(string id, string title, DockRegion region, Action<EditorContext, Rect> draw)
    { ValidateId(id); _panels.Add(id, new(id, title, region, draw)); }
    public void AddMenu(string id, string label, Action<EditorContext> execute)
    { ValidateId(id); _menus.Add(id, new(id, label, execute)); }
    public unsafe void AddDrawer<T>(ComponentDrawer<T> drawer) where T : unmanaged, IComponent =>
        _drawers.Add(ComponentRegistry.Get(Component<T>.Id).StableId, (pointer, context) => drawer(ref *(T*)pointer, context));
    internal bool DrawComponent(ulong stableId, nint pointer, EditorContext context)
    {
        if (!_drawers.TryGetValue(stableId, out var draw)) return false;
        draw(pointer, context); return true;
    }
    public bool RemovePanel(string id) => _panels.Remove(id);
    public bool RemoveMenu(string id) => _menus.Remove(id);
    public bool RemoveDrawer<T>() where T : unmanaged, IComponent => _drawers.Remove(ComponentRegistry.Get(Component<T>.Id).StableId);
    private static void ValidateId(string id)
    { if (string.IsNullOrWhiteSpace(id) || id.Any(char.IsWhiteSpace)) throw new ArgumentException("Editor ids must be nonempty and contain no whitespace.", nameof(id)); }
    /// <summary>Register one extension transactionally; dispose its scope before unloading its assembly.</summary>
    public IDisposable Register(IEditorExtension extension)
    {
        var panels = new Dictionary<string, EditorPanel>(_panels, StringComparer.Ordinal);
        var menus = new Dictionary<string, EditorMenuItem>(_menus, StringComparer.Ordinal);
        var drawers = new Dictionary<ulong, Action<nint, EditorContext>>(_drawers);
        void Restore()
        {
            _panels.Clear(); foreach (var item in panels) _panels.Add(item.Key, item.Value);
            _menus.Clear(); foreach (var item in menus) _menus.Add(item.Key, item.Value);
            _drawers.Clear(); foreach (var item in drawers) _drawers.Add(item.Key, item.Value);
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
    public void Dispose() { _drawers.Clear(); _menus.Clear(); _panels.Clear(); }
}

/// <summary>Tab ownership and selection, independent of rendering or OS input.</summary>
public sealed class DockLayout
{
    private readonly Dictionary<DockRegion, List<string>> _tabs = Enum.GetValues<DockRegion>().ToDictionary(r => r, _ => new List<string>());
    private readonly Dictionary<DockRegion, string?> _selected = new();
    public IReadOnlyList<string> Tabs(DockRegion region) => _tabs[region];
    public string? Selected(DockRegion region) => _selected.GetValueOrDefault(region);
    public void Sync(IEnumerable<EditorPanel> panels)
    {
        var all = panels.ToDictionary(p => p.Id, StringComparer.Ordinal);
        foreach (var region in _tabs.Keys)
        {
            _tabs[region].RemoveAll(id => !all.ContainsKey(id));
            if (Selected(region) is not { } selected || !_tabs[region].Contains(selected)) _selected[region] = _tabs[region].FirstOrDefault();
        }
        foreach (var panel in all.Values)
            if (!_tabs.Values.Any(t => t.Contains(panel.Id))) { _tabs[panel.Region].Add(panel.Id); _selected.TryAdd(panel.Region, panel.Id); if (_selected[panel.Region] is null) _selected[panel.Region] = panel.Id; }
    }
    public void Select(DockRegion region, string id)
    { if (!_tabs[region].Contains(id)) throw new ArgumentException("Panel is not in that region."); _selected[region] = id; }
    public void Move(string id, DockRegion target, int index = int.MaxValue)
    {
        var source = _tabs.FirstOrDefault(pair => pair.Value.Contains(id));
        if (source.Value is null) throw new ArgumentException("Unknown panel: " + id);
        source.Value.Remove(id);
        if (Selected(source.Key) == id) _selected[source.Key] = source.Value.FirstOrDefault();
        _tabs[target].Insert(Math.Clamp(index, 0, _tabs[target].Count), id); _selected[target] = id;
    }
}
