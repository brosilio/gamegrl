using System.Numerics;
using Gamegrl.Assets;
using Gamegrl.Platform;
using Gamegrl.Rendering;
using Gamegrl.UI;

namespace Gamegrl.Editor;

internal sealed partial class EditorHost
{
    private static readonly string[] CreateKinds = ["Empty", "Cube", "Camera", "Sprite"];
    private int _newKind;
    private readonly List<Entity> _roots = new();
    // Add-component choices for the selected archetype, rebuilt only when it, the registry or game code changes.
    private readonly List<int> _componentIds = new();
    private readonly List<string> _componentNames = new();
    private (int Archetype, int Registered, int Code) _componentKey = (-1, -1, -1);
    private int _codeGeneration;
    private readonly List<(string Text, LogLevel Level)> _consoleLines = new();
    private long _consoleVersion = -1;
    /// <summary>Console entries per level: info, warning, error.</summary>
    private readonly int[] _consoleCounts = new int[3];
    private bool _showInfo = true, _showWarnings = true, _showErrors = true;

    /// <summary>A kind picker with create and delete beside it, then the entity tree; its scrollbar sits on the sidebar rule.</summary>
    private void Hierarchy(Rect area)
    {
        float kind = Math.Max(0, area.W - 7);
        _ui.Combo(new Rect(area.X, area.Y, kind, 1), "create-kind", ref _newKind, CreateKinds);
        if (_ui.Button(new Rect(area.X + kind + 1, area.Y, 3, 1), "Create", "+")) Document.Edit(CreateEntity);
        if (_ui.Link(new Rect(area.X + kind + 4, area.Y, 3, 1), "Delete", "×", _ui.Theme.Dim, CanEditSelection)) Document.Edit(DeleteSelected);
        _ui.BeginScroll(new Rect(area.X, area.Y + 1, area.W, Math.Max(0, area.H - 1)), "hierarchy-scroll");
        _roots.Clear();
        foreach (var archetype in _app.World.Archetypes)
            foreach (var e in archetype.Entities)
                if (!_app.World.HasParent(e)) _roots.Add(e);
        foreach (var e in _roots) EntityRow(e, 0);
        _ui.EndScroll();
    }

    private void CreateEntity()
    {
        var world = _app.World;
        var e = world.Spawn(SceneId.New(), new Name(CreateKinds[_newKind] is "Empty" ? "Entity" : CreateKinds[_newKind]), LocalTransform.Default, new LocalToWorld());
        if (_newKind == 1) world.Add(e, MeshRenderer.Default);
        if (_newKind == 2) world.Add(e, Camera.Default);
        if (_newKind == 3) world.Add(e, Sprite.Default);
        Document.Selected = e;
    }

    private void DeleteSelected()
    {
        _app.World.Destroy(Document.Selected);
        Document.Selected = default;
    }

    private void EntityRow(Entity e, int depth)
    {
        var world = _app.World;
        ulong id = world.Has<SceneId>(e) ? world.Get<SceneId>(e).Value : e.Index;
        bool open = _expanded.GetValueOrDefault(id, true), children = false;
        foreach (var unused in world.Children(e))
        {
            children = true;
            break;
        }
        Span<char> label = stackalloc char[FixedString64.MaxBytes + 16];
        int length = EntityLabel(world, e, label);
        if (_ui.TreeNode(_ui.Next(), id, label[..length], children, Document.Selected == e, ref open, depth)) Document.Selected = e;
        _expanded[id] = open;
        if (open)
            foreach (var child in world.Children(e)) EntityRow(child, depth + 1);
    }

    private static int EntityLabel(World world, Entity e, Span<char> destination)
    {
        if (world.Has<Name>(e)) return System.Text.Encoding.UTF8.GetChars(world.Get<Name>(e).Value.Utf8, destination);
        "Entity ".CopyTo(destination);
        e.Index.TryFormat(destination[7..], out int digits, default, System.Globalization.CultureInfo.InvariantCulture);
        return 7 + digits;
    }

    private unsafe void Inspector(Rect area)
    {
        _ui.BeginScroll(area, "inspector-scroll");
        var world = _app.World;
        var e = Document.Selected;
        if (!world.IsAlive(e) || Document.Playing)
        {
            _ui.Label(Document.Playing ? "Stop play mode to edit" : "Select an entity", Document.Playing);
            _ui.EndScroll();
            return;
        }
        // Value edits capture only this entity's component bytes; structural buttons below use Document.Edit.
        bool click = _ui.Input.WasPressed(MouseButton.Left) || _ui.Input.WasReleased(MouseButton.Left);
        if (click && _ui.IsHovered(area)) Document.BeginEntityEdit(e);
        _ui.PushId(world.Has<SceneId>(e) ? world.Get<SceneId>(e).Value : e.Index);
        var (archetype, row) = world.Locate(e);
        int remove = -1;
        foreach (int type in archetype.Types)
        {
            if (type == Component<SceneId>.Id || type == Component<PrefabInstance>.Id || type == Component<LocalToWorld>.Id) continue;
            var info = ComponentRegistry.Get(type);
            _ui.PushId(type);
            _ui.Row(-1, 1);
            bool open = _ui.Foldout(_ui.Next(), info.Name.AsSpan(info.Name.LastIndexOf('.') + 1));
            if (_ui.Link(_ui.Next(), "Remove component", "×", _ui.Theme.Dim)) remove = type;
            if (open)
            {
                _ui.Indent();
                DrawComponent(info, archetype.Ptr(archetype.ColumnOf(type), row));
                _ui.Unindent();
            }
            _ui.PopId();
        }
        if (remove >= 0) RemoveComponent(e, remove);
        RefreshComponentChoices(archetype);
        if (_componentIds.Count > 0)
        {
            // Entry 0 is the prompt; picking any other entry adds that component at once.
            int choice = 0;
            if (_ui.Combo(_ui.Next(), "component-choice", ref choice, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_componentNames)) && choice > 0)
                AddComponent(e, _componentIds[choice - 1]);
        }
        _ui.PopId();
        if (!_ui.WantsKeyboard && (_ui.Active == 0 || _ui.Input.WasReleased(MouseButton.Left))) Document.CommitEdit();
        _ui.EndScroll();
    }

    private unsafe void DrawComponent(ComponentInfo info, byte* pointer)
    {
        var checkpoint = _ui.Save();
        try
        {
            if (!Registry.DrawComponent(info.StableId, (nint)pointer, _context)) info.Serializer?.Visit(pointer, _inspector);
        }
        catch (Exception e)
        {
            _ui.Rewind(checkpoint);
            Report("Inspector " + info.Name, e);
            _ui.TextIn(_ui.Next(), "Drawer failed: " + e.Message, _ui.Theme.Error);
        }
    }

    private void RefreshComponentChoices(Archetype archetype)
    {
        var key = (archetype.Id, ComponentRegistry.Count, _codeGeneration);
        if (key == _componentKey) return;
        _componentKey = key;
        _componentIds.Clear();
        _componentNames.Clear();
        _componentNames.Add("+ add component");
        for (int id = 0; id < ComponentRegistry.Count; id++)
        {
            var info = ComponentRegistry.Get(id);
            if (info.Released || info.Serializer is null || archetype.Set.Contains(id)) continue;
            if (id == Component<SceneId>.Id || id == Component<PrefabInstance>.Id) continue;
            _componentIds.Add(id);
            _componentNames.Add(info.Name);
        }
    }

    // Structural edits get their own methods so their closures are allocated only on click, not every frame.
    private void MakeRoot(Entity e) => Document.Edit(() => _app.World.SetParent(e, default));
    private void RemoveComponent(Entity e, int type) => Document.Edit(() => _app.World.RemoveRaw(e, type));

    private unsafe void AddComponent(Entity e, int type) => Document.Edit(() =>
    {
        var info = ComponentRegistry.Get(type);
        Span<byte> data = stackalloc byte[info.Size];
        fixed (byte* p = data)
        {
            info.Serializer!.Reset(p);
            _app.World.AddRaw(e, type, p);
        }
    });

    private void ParentToNextRoot(Entity e) => Document.Edit(() =>
    {
        foreach (var a in _app.World.Archetypes)
            foreach (var parent in a.Entities)
                if (parent != e && !_app.World.HasParent(parent))
                {
                    _app.World.SetParent(e, parent);
                    return;
                }
    });

    private void Project(Rect area)
    {
        _ui.BeginScroll(area, "project-scroll");
        foreach (string path in _assets.Files)
        {
            if (!_ui.RowVisible())
            {
                _ui.Next();
                continue;
            }
            if (!_ui.Selectable(_ui.Next(), path, path == Files.CurrentPath, path.LastIndexOf('/') + 1)) continue;
            if (path.EndsWith(".scene", StringComparison.Ordinal)) Files.RequestOpen(path);
            else if (path.EndsWith(".prefab", StringComparison.Ordinal)) InstantiatePrefab(path);
        }
        _ui.EndScroll();
    }

    private void InstantiatePrefab(string path) => Document.Edit(() =>
    {
        string text = $"{Scene.TextHeader}\nentity {SceneId.New().Value:x16}\n  prefab asset:{_assets.FindByPath(path):N}\n";
        var entities = Scene.LoadText(_app.World, text, _assets.PrefabText, Log.Warn);
        if (entities.Length > 0) Document.Selected = entities[0];
    });

    /// <summary>Split log entries into lines and count levels only when the log changes.</summary>
    private void RefreshConsole()
    {
        if (_consoleVersion == Log.Version) return;
        _consoleVersion = Log.Version;
        _consoleLines.Clear();
        Array.Clear(_consoleCounts);
        foreach (var entry in Log.Snapshot())
        {
            _consoleCounts[entry.Level == LogLevel.Error ? 2 : entry.Level == LogLevel.Warning ? 1 : 0]++;
            foreach (var line in entry.Message.Split('\n')) _consoleLines.Add((line.TrimEnd('\r'), entry.Level));
        }
    }

    /// <summary>Level filters as status-dot toggles with counts, a clear link, then the filtered log.</summary>
    private void ConsolePanel(Rect area)
    {
        RefreshConsole();
        var theme = _ui.Theme;
        float x = area.X;
        x = ConsoleFilter(x, area.Y, "console-info", "info", _consoleCounts[0], ref _showInfo, theme.Text);
        x = ConsoleFilter(x, area.Y, "console-warnings", "warn", _consoleCounts[1], ref _showWarnings, theme.Warning);
        ConsoleFilter(x, area.Y, "console-errors", "error", _consoleCounts[2], ref _showErrors, theme.Error);
        if (_ui.Link(new Rect(area.Right - 5, area.Y, 5, 1), "Clear", "clear", theme.Dim)) Log.Clear();
        _ui.BeginScroll(new Rect(area.X, area.Y + 1, area.W, Math.Max(0, area.H - 1)), "console-scroll");
        foreach (var (text, level) in _consoleLines)
        {
            if (!(level == LogLevel.Error ? _showErrors : level == LogLevel.Warning ? _showWarnings : _showInfo)) continue;
            if (!_ui.RowVisible())
            {
                _ui.Next();
                continue;
            }
            var color = level == LogLevel.Error ? theme.Error : level == LogLevel.Warning ? theme.Warning : theme.Text;
            _ui.TextIn(_ui.Next(), text, color);
        }
        _ui.EndScroll();
    }

    private float ConsoleFilter(float x, float y, string label, string name, int count, ref bool value, Color32 dot)
    {
        Span<char> text = stackalloc char[32];
        name.CopyTo(text);
        int length = name.Length;
        text[length++] = ' ';
        if (count.TryFormat(text[length..], out int digits, default, System.Globalization.CultureInfo.InvariantCulture)) length += digits;
        float width = length + 2;
        _ui.Toggle(new Rect(x, y, width, 1), label, text[..length], ref value, dot);
        return x + width + 2;
    }

    private void Tasks(Rect area)
    {
        _ui.BeginArea(area);
        if (!Jobs.Busy) _ui.Label("no running tasks", true);
        else
        {
            if (_ui.Link(new Rect(area.Right - 10, area.Y, 10, 1), "Cancel tasks", "cancel all", _ui.Theme.Dim)) Jobs.CancelAll();
            foreach (string job in Jobs.Running)
            {
                var row = _ui.Next();
                _ui.TextIn(row with { W = 2 }, "●", _ui.Theme.Accent);
                _ui.TextIn(row with { X = row.X + 2, W = Math.Max(0, row.W - 13) }, job, _ui.Theme.Text);
            }
        }
        _ui.EndArea();
    }
}
