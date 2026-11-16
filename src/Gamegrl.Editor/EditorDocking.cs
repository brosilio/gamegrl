using System.Numerics;
using Gamegrl.Platform;
using Gamegrl.UI;

namespace Gamegrl.Editor;

/// <summary>
/// The editor's frame: an unboxed sidebar of stacked sections, boxed panes with their tabs in the top border, borders
/// that double as splitters, a one-row top line and a command palette in place of a toolbar and menus.
/// </summary>
internal sealed partial class EditorHost
{
    public EditorRegistry Registry { get; } = new();
    internal DockLayout Docks { get; } = new();
    private EditorContext _context = null!;
    private readonly Dictionary<DockRegion, Rect> _dockAreas = new();
    /// <summary>Last drawn tab (or sidebar section header) per panel, for hit tests, drop positions and automation.</summary>
    private readonly Dictionary<string, Rect> _tabRects = new(StringComparer.Ordinal);
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private DockRegion _focusRegion = DockRegion.Center;
    private bool _sidebarHidden;
    private string? _dragTab;
    private Vector2 _tabPress;
    internal Rect TabRect(string id) => _tabRects.TryGetValue(id, out var rect) ? rect : throw new ArgumentException("Tab not drawn: " + id);

    private void RegisterPanels()
    {
        _context = new EditorContext(_app, _ui, Document, Jobs);
        Registry.AddPanel("hierarchy", "Hierarchy", DockRegion.Left, (_, r) => Hierarchy(r));
        Registry.AddPanel("project", "Project", DockRegion.Left, (_, r) => Project(r));
        Registry.AddPanel("scene", "Scene", DockRegion.Center, (_, r) => ScenePanel(r));
        Registry.AddPanel("game", "Game", DockRegion.Center, (_, r) => GamePanel(r));
        Registry.AddPanel("inspector", "Inspector", DockRegion.Right, (_, r) => Inspector(r));
        Registry.AddPanel("console", "Console", DockRegion.Bottom, (_, r) => ConsolePanel(r));
        Registry.AddPanel("tasks", "Tasks", DockRegion.Bottom, (_, r) => Tasks(r));
        Registry.AddCommand("file.new", "File: New scene", _ => Files.RequestNew(), "Ctrl+N");
        Registry.AddCommand("file.save", "File: Save", _ => Files.Save(), "Ctrl+S");
        Registry.AddCommand("file.saveAs", "File: Save as…", _ => Files.SaveAs(), "Ctrl+Shift+S");
        Registry.AddCommand("file.recover", "File: Recover disconnected scene", _ => RecoverScene());
        Registry.AddCommand("edit.undo", "Edit: Undo", _ => Document.Undo(), "Ctrl+Z");
        Registry.AddCommand("edit.redo", "Edit: Redo", _ => Document.Redo(), "Ctrl+Y");
        for (int i = 0; i < CreateKinds.Length; i++)
        {
            int kind = i;
            Registry.AddCommand("entity.create." + CreateKinds[i].ToLowerInvariant(), "Entity: Create " + CreateKinds[i].ToLowerInvariant(), _ =>
            {
                _newKind = kind;
                Document.Edit(CreateEntity);
            });
        }
        Registry.AddCommand("entity.delete", "Entity: Delete", _ => { if (CanEditSelection) Document.Edit(DeleteSelected); }, "Del");
        Registry.AddCommand("entity.makeRoot", "Entity: Make root", _ => { if (CanEditSelection) MakeRoot(Document.Selected); });
        Registry.AddCommand("entity.parentNext", "Entity: Parent to next root", _ => { if (CanEditSelection) ParentToNextRoot(Document.Selected); });
        Registry.AddCommand("play.toggle", "Play: Start or stop", _ => TogglePlay(), "F5");
        Registry.AddCommand("play.pause", "Play: Pause or resume", _ => TogglePause(), "F6");
        Registry.AddCommand("game.build", "Game: Build scripts", _ => BuildGame());
        Registry.AddCommand("assets.reimport", "Assets: Reimport", _ => Import());
        Registry.AddCommand("view.sidebar", "View: Toggle sidebar", _ => _sidebarHidden = !_sidebarHidden, "Ctrl+B");
        Registry.AddCommand("view.reset", "View: Reset layout", _ => ResetLayout());
        Docks.Sync(Registry.Panels);
    }

    private bool CanEditSelection => !Document.Playing && _app.World.IsAlive(Document.Selected);

    private void TogglePause()
    {
        if (Document.Playing) _game.Paused = !_game.Paused;
    }

    private void RecoverScene() => Jobs.Run("Read recovered scene", _ =>
    {
        string directory = Path.Combine(_root, "Library", "recovery");
        string? path = Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.scene").OrderDescending(StringComparer.Ordinal).FirstOrDefault() : null;
        return path is null ? throw new FileNotFoundException("No disconnected scene recovery is available.") : File.ReadAllText(path);
    }, Files.RequestRecovered);

    private void ResetLayout()
    {
        foreach (var panel in Registry.Panels) Docks.Move(panel.Id, panel.Region);
        foreach (var region in DockLayout.Regions)
            if (Docks.Tabs(region).Count > 0) Docks.Select(region, Docks.Tabs(region)[0]);
        _collapsed.Clear();
        _sidebarHidden = false;
        (_left, _right, _bottom) = (DefaultLeft, DefaultRight, DefaultBottom);
    }

    internal bool RunCommand(string id)
    {
        if (Registry.FindCommand(id) is not { } command) return false;
        RunCommand(command);
        return true;
    }

    private void RunCommand(EditorCommand command)
    {
        try { command.Execute(_context); }
        catch (Exception e) { Report("Command " + command.Label, e); }
    }

    // ------------------------------------------------------------------ panes

    private void ScenePanel(Rect area)
    {
        _sceneArea = area;
        EnsureTarget(ref _sceneColor, ref _sceneDepth, area);
        _ui.Image(area, _sceneColor!);
        _sceneTerminal?.Paint(_ui.Draw, area, _sceneColor!);
        SceneInteraction(_ui.Input);
    }

    private void GamePanel(Rect area)
    {
        _gameArea = area;
        EnsureTarget(ref _gameColor, ref _gameDepth, area);
        _ui.Image(area, _gameColor!);
        _gameTerminal?.Paint(_ui.Draw, area, _gameColor!);
        var size = new Vector2(_gameColor!.Width, _gameColor.Height) * GamePreviewScale;
        _app.World.Resource<Gamegrl.Rendering.ScreenSize>() = new() { Width = (int)size.X, Height = (int)size.Y };
    }

    private void DrawDock(Rect area, DockRegion region)
    {
        _dockAreas[region] = area;
        if (area.W < 1 || area.H < 1) return;
        if (_ui.Input.AnyMousePressed && _ui.IsHovered(area)) _focusRegion = region;
        if (region == DockRegion.Left) Sidebar(area);
        else Pane(area, region);
    }

    /// <summary>A boxed pane: tabs and the selected panel's status live in the top border; the border is accented while focused.</summary>
    private void Pane(Rect area, DockRegion region)
    {
        var theme = _ui.Theme;
        bool focused = _focusRegion == region;
        _ui.Draw.Box(area, focused ? theme.Accent : theme.Border);
        var strip = new Rect(area.X + 1, area.Y, Math.Max(0, area.W - 2), 1);
        var tabs = Docks.Tabs(region);
        float total = 0;
        foreach (string id in tabs) total += TabWidth(id) + 1;
        _ui.PushId((int)region);
        uint scrollId = _ui.Id("dock-scroll");
        float scroll = _ui.GetFloat(scrollId);
        if (_ui.IsHovered(strip)) scroll -= _ui.Input.Wheel.Y * 4;
        scroll = Math.Clamp(scroll, 0, Math.Max(0, total - strip.W));
        _ui.SetFloat(scrollId, scroll);
        float x = strip.X - scroll;
        _ui.Draw.PushClip(strip);
        foreach (string id in tabs)
        {
            var panel = Registry.FindPanel(id)!;
            var tab = new Rect(x, strip.Y, TabWidth(id), 1);
            _tabRects[id] = tab;
            bool selected = Docks.Selected(region) == id;
            bool clicked = _ui.Behavior(tab, _ui.Id(id), out bool hover, out _);
            if (hover && _ui.Input.WasPressed(MouseButton.Left))
            {
                _dragTab = id;
                _tabPress = _ui.Mouse;
            }
            if (clicked && Vector2.Distance(_tabPress, _ui.Mouse) < 1) Docks.Select(region, id);
            var color = selected ? focused ? theme.Accent : theme.Text : hover ? theme.Text : theme.Dim;
            _ui.Draw.Text(new Vector2(tab.X + 1, tab.Y), panel.Title, color);
            _ui.Draw.Repeat(new Rect(tab.X, tab.Y, 1, 1), ' ', color);
            _ui.Draw.Repeat(new Rect(tab.Right - 1, tab.Y, 1, 1), ' ', color);
            x += tab.W + 1;
        }
        _ui.Draw.PopClip();
        _ui.PopId();
        if (Docks.Selected(region) is { } current)
        {
            Span<char> meta = stackalloc char[96];
            int length = PanelMeta(current, meta);
            float start = area.Right - 3 - length;
            if (length > 0 && start > x)
            {
                _ui.Draw.PushClip(strip);
                _ui.Draw.Text(new Vector2(start + 1, area.Y), meta[..length], theme.Dim);
                _ui.Draw.Repeat(new Rect(start, area.Y, 1, 1), ' ', theme.Dim);
                _ui.Draw.Repeat(new Rect(start + 1 + length, area.Y, 1, 1), ' ', theme.Dim);
                _ui.Draw.PopClip();
            }
            if (area.W > 2 && area.H > 2) DrawPanel(Registry.FindPanel(current)!, area.Shrink(1));
        }
        else if (area.W > 2 && area.H > 2) _ui.TextIn(area.Shrink(1) with { H = 1 }, "drop a tab here", theme.Dim);
    }

    /// <summary>
    /// The sidebar has no box: a rule separates it from the panes, and its panels stack as collapsible sections with
    /// a header (<c>▾ title … status</c>) and a faint rule between them. Expanded sections share the height.
    /// </summary>
    private void Sidebar(Rect area)
    {
        var theme = _ui.Theme;
        var rule = CellDraw.From(_layout.Rule);
        for (float y = rule.Y; y < rule.Bottom; y++) _ui.Draw.Glyph(rule.X, y, '│', theme.Border);
        var tabs = Docks.Tabs(DockRegion.Left);
        if (tabs.Count == 0)
        {
            _ui.TextIn(area with { X = area.X + 1, H = 1 }, "drop a tab here", theme.Dim);
            return;
        }
        int open = 0;
        foreach (string id in tabs) if (!_collapsed.Contains(id)) open++;
        float free = Math.Max(0, area.H - tabs.Count * 2 + 1);
        float share = open > 0 ? MathF.Floor(free / open) : 0, extra = free - share * open;
        float y0 = area.Y;
        Span<char> meta = stackalloc char[96];
        bool focused = _focusRegion == DockRegion.Left;
        for (int i = 0; i < tabs.Count; i++)
        {
            string id = tabs[i];
            var panel = Registry.FindPanel(id)!;
            bool expanded = !_collapsed.Contains(id);
            var header = new Rect(area.X, y0, area.W, 1);
            _tabRects[id] = header;
            _ui.PushId(id);
            bool clicked = _ui.Behavior(header, _ui.Id("section"), out bool hover, out _);
            _ui.PopId();
            if (hover && _ui.Input.WasPressed(MouseButton.Left))
            {
                _dragTab = id;
                _tabPress = _ui.Mouse;
            }
            if (clicked && Vector2.Distance(_tabPress, _ui.Mouse) < 1)
            {
                if (!_collapsed.Remove(id)) _collapsed.Add(id);
                Docks.Select(DockRegion.Left, id);
            }
            if (hover) _ui.Draw.Rect(header, theme.Raised);
            bool active = focused && Docks.Selected(DockRegion.Left) == id;
            _ui.Draw.PushClip(header);
            float x = _ui.Draw.Text(new Vector2(header.X + 1, y0), expanded ? "▾" : "▸", theme.Dim);
            x = _ui.Draw.Text(new Vector2(x + 1, y0), panel.Title, active ? theme.Accent : theme.Text);
            int length = PanelMeta(id, meta);
            if (length > 0 && header.Right - 1 - length > x + 1) _ui.Draw.Text(new Vector2(header.Right - 1 - length, y0), meta[..length], theme.Dim);
            _ui.Draw.PopClip();
            y0++;
            if (expanded)
            {
                float height = share + (--open == 0 ? extra : 0);
                if (height > 0) DrawPanel(panel, new Rect(area.X + 1, y0, Math.Max(0, area.W - 1), height));
                y0 += height;
            }
            if (i < tabs.Count - 1)
            {
                _ui.Draw.Repeat(new Rect(area.X, y0, area.W, 1), '─', theme.Border);
                y0++;
            }
        }
    }

    private float TabWidth(string id) => _ui.Font.Measure(Registry.FindPanel(id)!.Title) + 2;

    /// <summary>Short status text shown beside a panel's tab or section header.</summary>
    private int PanelMeta(string id, Span<char> destination)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        int written;
        switch (id)
        {
            case "hierarchy":
                return _app.World.EntityCount.TryFormat(destination, out written, default, invariant) ? written : 0;
            case "project":
                return _assets.Files.Length.TryFormat(destination, out written, default, invariant) ? written : 0;
            case "inspector":
                return CanEditSelection ? EntityLabel(_app.World, Document.Selected, destination) : 0;
            case "game":
                return Append(destination, 0, !Document.Playing ? "" : _game.Paused ? "paused" : "playing");
            case "tasks":
                return Jobs.Busy && Jobs.Running.Count.TryFormat(destination, out written, default, invariant) ? Append(destination, written, " running") : 0;
            case "console":
                RefreshConsole();
                int length = 0;
                if (_consoleCounts[2] > 0 && _consoleCounts[2].TryFormat(destination, out written, default, invariant)) length = Append(destination, written, " err");
                if (_consoleCounts[1] > 0)
                {
                    if (length > 0) length = Append(destination, length, "  ");
                    if (_consoleCounts[1].TryFormat(destination[length..], out written, default, invariant)) length = Append(destination, length + written, " warn");
                }
                return length;
            default:
                return 0;
        }
    }

    private static int Append(Span<char> destination, int length, ReadOnlySpan<char> text)
    {
        int count = Math.Min(text.Length, destination.Length - length);
        text[..count].CopyTo(destination[length..]);
        return length + count;
    }

    /// <summary>Panels draw unclipped to the right by one column so a scroll view can put its bar on the border.</summary>
    private void DrawPanel(EditorPanel panel, Rect content)
    {
        var checkpoint = _ui.Save();
        _ui.Draw.PushClip(content with { W = content.W + 1 });
        try { panel.Draw(_context, content); }
        catch (Exception e)
        {
            _ui.Rewind(checkpoint);
            _ui.Draw.PushClip(content);
            Report("Panel " + panel.Title, e);
            _ui.TextIn(content with { H = 1 }, panel.Title + " failed: " + e.Message, _ui.Theme.Error);
        }
        _ui.Draw.PopClip();
    }

    private void FinishDockDrag()
    {
        if (_dragTab is not { } id) return;
        bool dragged = Vector2.Distance(_tabPress, _ui.Mouse) >= 1;
        if (dragged) _ui.Tooltip(Registry.FindPanel(id)?.Title ?? id);
        if (!_ui.Input.WasReleased(MouseButton.Left)) return;
        _dragTab = null;
        if (!dragged) return;
        foreach (var (region, area) in _dockAreas)
        {
            if (!area.Contains(_ui.Mouse)) continue;
            var tabs = Docks.Tabs(region);
            int index = tabs.Count;
            bool sidebar = region == DockRegion.Left;
            if (sidebar || _ui.Mouse.Y < area.Y + 1)
            {
                // Insert before the first tab right of the pointer, or below the sections above it in the sidebar.
                index = 0;
                foreach (string tab in tabs)
                {
                    if (tab != id && _tabRects.TryGetValue(tab, out var rect) && (sidebar ? _ui.Mouse.Y < rect.Y : _ui.Mouse.X < rect.X + rect.W * 0.5f)) break;
                    index++;
                }
            }
            int original = -1;
            for (int i = 0; i < tabs.Count; i++) if (tabs[i] == id) original = i;
            if (original >= 0 && original < index) index--;
            Docks.Move(id, region, index);
            _collapsed.Remove(id);
            break;
        }
    }

    // ------------------------------------------------------------------ top line

    /// <summary>
    /// Scene and its unsaved dot, play controls, play state and warnings on the left; background work and the
    /// command palette on the right. It replaces the toolbar, the menu and the status bar.
    /// </summary>
    private void TopLine(Rect line)
    {
        var theme = _ui.Theme;
        var draw = _ui.Draw;
        draw.PushClip(line);
        float x = draw.Text(line.Min, ScenePath, theme.Text);
        if (Document.Dirty) x = draw.Text(new Vector2(x + 1, line.Y), "●", theme.Accent);
        x += 2;
        bool playing = Document.Playing;
        if (_ui.Button(new Rect(x, line.Y, 3, 1), playing ? "Stop" : "Play", playing ? "■" : "▶")) TogglePlay();
        if (_ui.Link(new Rect(x + 3, line.Y, 3, 1), _game.Paused ? "Resume" : "Pause", "‖", _game.Paused ? theme.Warning : theme.Text, playing)) TogglePause();
        x += 7;
        if (playing) x = draw.Text(new Vector2(x, line.Y), _game.Paused ? "paused" : "playing", _game.Paused ? theme.Warning : theme.Accent) + 2;
        if (Document.Held is not null) draw.Text(new Vector2(x, line.Y), "scene held until the game builds", theme.Warning);

        const string hint = "^P commands";
        float right = line.Right - _ui.Font.Measure(hint);
        if (_ui.Link(new Rect(right, line.Y, _ui.Font.Measure(hint), 1), "Commands", hint, theme.Dim)) OpenPalette();
        Span<char> status = stackalloc char[96];
        int length = 0;
        var dot = theme.Accent;
        if (Jobs.Busy)
        {
            var running = Jobs.Running;
            length = Append(status, 0, running.Count > 0 ? running[0] : "working");
            if (running.Count > 1)
            {
                length = Append(status, length, " +");
                if ((running.Count - 1).TryFormat(status[length..], out int digits)) length += digits;
            }
        }
        else if (_gameDll is null)
        {
            length = Append(status, 0, "no game build");
            dot = theme.Warning;
        }
        if (length > 0)
        {
            float start = right - 3 - length;
            if (start > x)
            {
                draw.Text(new Vector2(start, line.Y), Jobs.Busy ? "●" : "○", dot);
                draw.Text(new Vector2(start + 2, line.Y), status[..length], theme.Dim);
            }
        }
        draw.PopClip();
    }

    // ------------------------------------------------------------------ command palette

    private bool _paletteOpen;
    private string _paletteQuery = "";
    private string? _paletteFiltered;
    private int _paletteIndex, _paletteScroll, _paletteVersion = -1;
    private readonly List<EditorCommand> _paletteCommands = new();
    private readonly List<int> _paletteMatches = new();
    internal bool PaletteOpen => _paletteOpen;

    private void OpenPalette()
    {
        _paletteOpen = true;
        _paletteQuery = "";
        _paletteFiltered = null;
        _ui.CaptureKeyboard(_ui.Id("palette"));
    }

    /// <summary>Ctrl+P: type to filter every registered command by subsequence, arrows to choose, Enter to run.</summary>
    private void Palette()
    {
        if (!_paletteOpen) return;
        var input = _ui.Input;
        _ui.CaptureKeyboard(_ui.Id("palette"));
        if (input.WasPressed(Key.Escape))
        {
            _paletteOpen = false;
            return;
        }
        if (input.HasTypedText) _paletteQuery += input.TypedText;
        if (input.WasPressedOrRepeated(Key.Backspace) && _paletteQuery.Length > 0)
            _paletteQuery = _paletteQuery[..(_paletteQuery.Length - (_paletteQuery.Length > 1 && char.IsLowSurrogate(_paletteQuery[^1]) ? 2 : 1))];
        if (_paletteVersion != Registry.Version)
        {
            _paletteVersion = Registry.Version;
            _paletteCommands.Clear();
            _paletteCommands.AddRange(Registry.Commands);
            _paletteCommands.Sort(static (a, b) => string.CompareOrdinal(a.Label, b.Label));
            _paletteFiltered = null;
        }
        if (!ReferenceEquals(_paletteFiltered, _paletteQuery))
        {
            _paletteFiltered = _paletteQuery;
            _paletteMatches.Clear();
            for (int i = 0; i < _paletteCommands.Count; i++)
                if (Subsequence(_paletteCommands[i].Label, _paletteQuery)) _paletteMatches.Add(i);
            _paletteIndex = _paletteScroll = 0;
        }
        int count = _paletteMatches.Count;
        if (input.WasPressedOrRepeated(Key.Down)) _paletteIndex++;
        if (input.WasPressedOrRepeated(Key.Up)) _paletteIndex--;
        _paletteIndex = count == 0 ? 0 : (_paletteIndex + count) % count;

        var theme = _ui.Theme;
        float width = Math.Min(72, _ui.Screen.W - 4);
        int visible = Math.Max(1, Math.Min(count, (int)_ui.Screen.H - 6));
        _paletteScroll = Math.Clamp(_paletteScroll, Math.Max(0, _paletteIndex - visible + 1), _paletteIndex);
        var rect = new Rect(MathF.Floor((_ui.Screen.W - width) / 2), 2, width, visible + 3);
        EditorCommand? run = null;
        _ui.BeginPopup(rect, "commands");
        var field = _ui.Next();
        float x = _ui.Draw.Text(field.Min, "›", theme.Accent) + 1;
        x = _ui.Draw.Text(new Vector2(x, field.Y), _paletteQuery, theme.Text);
        _ui.Draw.Text(new Vector2(x, field.Y), "▏", theme.Accent);
        if (count == 0) _ui.Label("no matching command", true);
        for (int i = _paletteScroll; i < Math.Min(count, _paletteScroll + visible); i++)
        {
            var command = _paletteCommands[_paletteMatches[i]];
            var row = _ui.Next();
            bool clicked = _ui.Behavior(row, _ui.Id((ulong)i), out bool hover, out _);
            if (hover && input.MouseDelta != Vector2.Zero) _paletteIndex = i;
            if (clicked) run = command;
            bool selected = i == _paletteIndex;
            if (selected) _ui.Draw.Rect(row, theme.Selection);
            _ui.TextIn(row with { X = row.X + 1, W = row.W - 1 }, command.Label, selected ? theme.Accent : theme.Text);
            if (command.Shortcut is { } shortcut) _ui.TextIn(row with { W = row.W - 1 }, shortcut, theme.Dim, 1);
        }
        _ui.EndPopup();
        if (input.WasPressed(Key.Enter) && count > 0) run = _paletteCommands[_paletteMatches[_paletteIndex]];
        if (input.WasPressed(MouseButton.Left) && !rect.Contains(_ui.Mouse)) _paletteOpen = false;
        if (run is null) return;
        _paletteOpen = false;
        RunCommand(run);
    }

    private static bool Subsequence(string text, string query)
    {
        int j = 0;
        foreach (char c in text)
            if (j < query.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(query[j])) j++;
        return j == query.Length;
    }
}
