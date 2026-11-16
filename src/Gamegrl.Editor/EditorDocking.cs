using System.Numerics;
using Gamegrl.Platform;
using Gamegrl.UI;

namespace Gamegrl.Editor;

internal sealed partial class EditorHost
{
    public EditorRegistry Registry { get; } = new();
    internal DockLayout Docks { get; } = new();
    private EditorContext _context = null!;
    private readonly Dictionary<DockRegion, Rect> _dockAreas = new();
    private string? _dragTab;
    private Vector2 _tabPress;
    private void RegisterPanels()
    {
        _context = new EditorContext(_app, _ui, Document, Jobs);
        Registry.AddPanel("hierarchy", "Hierarchy", DockRegion.Left, (_, r) => Hierarchy(r.Shrink(1)));
        Registry.AddPanel("scene", "Scene", DockRegion.Center, (_, r) =>
        {
            _sceneArea = r; EnsureTarget(ref _sceneColor, ref _sceneDepth, r); _ui.Image(r, _sceneColor!); _sceneTerminal?.Paint(_ui.Draw, r, _sceneColor!);
            SceneInteraction(_ui.Input);
        });
        Registry.AddPanel("game", "Game", DockRegion.Center, (_, r) =>
        {
            _gameArea = r; EnsureTarget(ref _gameColor, ref _gameDepth, r); _ui.Image(r, _gameColor!); _gameTerminal?.Paint(_ui.Draw, r, _gameColor!);
            _app.World.Resource<Gamegrl.Rendering.ScreenSize>() = new() { Width = _gameColor!.Width, Height = _gameColor.Height };
        });
        Registry.AddPanel("inspector", "Inspector", DockRegion.Right, (_, r) => Inspector(r.Shrink(1)));
        Registry.AddPanel("project", "Project", DockRegion.Bottom, (_, r) => Project(r));
        Registry.AddPanel("console", "Console", DockRegion.Bottom, (_, r) => ConsolePanel(r));
        Registry.AddPanel("tasks", "Tasks", DockRegion.Bottom, (_, r) => Tasks(r));
        Registry.AddMenu("file.new", "File / New", _ => Files.RequestNew());
        Registry.AddMenu("file.save", "File / Save", _ => Files.Save());
        Registry.AddMenu("file.saveAs", "File / Save As", _ => Files.SaveAs());
        Registry.AddMenu("file.recover", "File / Recover disconnected scene", _ =>
        {
            Jobs.Run("Read recovered scene", _ =>
            {
                string directory = Path.Combine(_root, "Library", "recovery");
                string? path = Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.scene").OrderDescending(StringComparer.Ordinal).FirstOrDefault() : null;
                return path is null ? throw new FileNotFoundException("No disconnected scene recovery is available.") : File.ReadAllText(path);
            }, Files.RequestRecovered);
        });
        Registry.AddMenu("layout.reset", "Layout / Reset", _ =>
        {
            foreach (var panel in Registry.Panels) Docks.Move(panel.Id, panel.Region);
            foreach (var region in Enum.GetValues<DockRegion>())
                if (Docks.Tabs(region).FirstOrDefault() is { } first) Docks.Select(region, first);
        });
        Docks.Sync(Registry.Panels);
    }
    private void DrawDock(Rect area, DockRegion region)
    {
        _dockAreas[region] = area;
        _ui.Draw.Rect(area, _ui.Theme.Panel);
        var strip = area with { H = _ui.Theme.TabHeight };
        _ui.Draw.Rect(strip, _ui.Theme.PanelHeader);
        float total = Docks.Tabs(region).Sum(id => _ui.Font.Measure(Registry.FindPanel(id)!.Title) + _ui.Theme.Padding * 4 + 1);
        uint scrollId = _ui.Id("dock-scroll#" + region);
        float scroll = _ui.GetFloat(scrollId);
        if (_ui.IsHovered(strip)) scroll -= _ui.Input.Wheel.Y * 4;
        scroll = Math.Clamp(scroll, 0, Math.Max(0, total - strip.W));
        _ui.SetFloat(scrollId, scroll);
        float x = strip.X - scroll;
        _ui.Draw.PushClip(strip);
        foreach (string id in Docks.Tabs(region))
        {
            var panel = Registry.FindPanel(id)!;
            float width = _ui.Font.Measure(panel.Title) + _ui.Theme.Padding * 4;
            var tab = new Rect(x, strip.Y, width, strip.H);
            bool clicked = _ui.Behavior(tab, _ui.Id("dock#" + id), out bool hover, out _);
            if (hover && _ui.Input.WasPressed(MouseButton.Left)) { _dragTab = id; _tabPress = _ui.Mouse; }
            if (clicked && Vector2.Distance(_tabPress, _ui.Mouse) < 1) Docks.Select(region, id);
            _ui.Draw.Rect(tab, Docks.Selected(region) == id ? _ui.Theme.Panel : hover ? _ui.Theme.WidgetHover : _ui.Theme.PanelHeader);
            if (Docks.Selected(region) == id) _ui.Draw.Rect(tab with { H = 1 }, _ui.Theme.Accent);
            _ui.TextIn(tab, panel.Title, _ui.Theme.Text);
            x += width + 1;
        }
        _ui.Draw.PopClip();
        var content = new Rect(area.X, strip.Bottom, area.W, Math.Max(1, area.H - strip.H));
        if (Docks.Selected(region) is { } selected) Registry.FindPanel(selected)!.Draw(_context, content);
        else _ui.TextIn(content with { H = 1 }, "Drop a tab here", _ui.Theme.TextDim);
    }
    private void FinishDockDrag()
    {
        if (_dragTab is not { } id) return;
        bool dragged = Vector2.Distance(_tabPress, _ui.Mouse) >= 1;
        if (dragged) _ui.Tooltip("Dock " + Registry.FindPanel(id)?.Title);
        if (!_ui.Input.WasReleased(MouseButton.Left)) return;
        if (dragged)
            foreach (var (region, area) in _dockAreas)
                if (area.Contains(_ui.Mouse))
                {
                    int index = Docks.Tabs(region).Count;
                    if (_ui.Mouse.Y < area.Y + _ui.Theme.TabHeight)
                    {
                        float x = area.X - _ui.GetFloat(_ui.Id("dock-scroll#" + region)); index = 0;
                        foreach (string tab in Docks.Tabs(region))
                        {
                            if (_ui.Mouse.X < x + (_ui.Font.Measure(Registry.FindPanel(tab)!.Title) + _ui.Theme.Padding * 4) * 0.5f) break;
                            x += _ui.Font.Measure(Registry.FindPanel(tab)!.Title) + _ui.Theme.Padding * 4 + 1; index++;
                        }
                    }
                    int original = Docks.Tabs(region).ToList().IndexOf(id);
                    if (original >= 0 && original < index) index--;
                    Docks.Move(id, region, index); break;
                }
        _dragTab = null;
    }
}
