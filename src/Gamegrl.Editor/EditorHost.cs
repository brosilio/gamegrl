using System.Numerics;
using Gamegrl.Assets;
using Gamegrl.Platform;
using Gamegrl.Rendering;
using Gamegrl.Rhi;
using Gamegrl.UI;
using Gamegrl.UI.Fonts;

namespace Gamegrl.Editor;

internal sealed partial class EditorHost : IDisposable
{
    private readonly App _app;
    private readonly string _root, _project;
    private readonly EditorAssets _assets;
    internal Input GameInput { get; } = new();
    private bool _gameFocused;
    private readonly GameCode _game = new();
    private string? _gameDll;
    private readonly BuildWorkspace _builds = new();
    private BuildOutput? _activeBuild;
    private bool _disposed;
    internal string? GameAssemblyPath => _gameDll;
    internal Task DrainBuildCleanupAsync() => _builds.DrainAsync();
    internal void AcceptBuild(BuildOutput output) { _reloadBuild?.Dispose(); _reloadBuild = output; }
    private readonly List<string> _extensionPaths = new();
    public void AddExtension(string path) => _extensionPaths.Add(Path.GetFullPath(path));
    private readonly RenderContext _render;
    private readonly CellUi _ui;
    private readonly Input _cellInput = new();
    private readonly CellRenderer? _renderer;
    private readonly TerminalDisplay? _terminal;
    private readonly TerminalViewport? _sceneTerminal, _gameTerminal;
    internal int CellWidth => _renderer?.CellWidth ?? 1;
    internal float GamePreviewScale => _terminal is not null ? 8 : 1;
    internal int CellHeight => _renderer?.CellHeight ?? 2;
    internal CellUi Ui => _ui;
    internal Rect SceneArea => _sceneArea;
    internal Rect GameArea => _gameArea;
    internal Rect PixelRect(Rect rect) => new(rect.X * CellWidth, rect.Y * CellHeight, rect.W * CellWidth, rect.H * CellHeight);
    private readonly EditorCellLayout _layout = new();
    internal Rect DockArea(DockRegion region) => _dockAreas.GetValueOrDefault(region);
    private readonly SceneCamera _camera = new();
    private readonly Query _cameras;
    private readonly Dictionary<ulong, bool> _expanded = new();
    private readonly InspectorVisitor _inspector;
    private GpuTexture? _sceneColor, _sceneDepth, _gameColor, _gameDepth;
    private Rect _sceneArea, _gameArea;
    private int _menuSelection;
    private float _left = 26, _right = 38, _bottom = 12;
    private double _clock;
    private bool _importPending, _buildPending, _importAgain, _buildAgain;
    private BuildOutput? _reloadBuild;
    private bool _loaded;
    private long _editClock;
    private bool _closeRequested;
    public void RequestClose()
    {
        _closeRequested = true;
        _ui.CommitTextEdit();
    }
    public SceneFiles Files { get; }
    private string ScenePath => Files.CurrentPath ?? "Untitled";
    public SceneDocument Document { get; }
    public EditorJobs Jobs { get; } = new();

    public EditorHost(App app, EditorAssets assets, string root, string project, string? fontPath, TerminalDisplay? terminal = null, bool ascii = false)
    {
        _app = app; _assets = assets; _root = root; _project = project;
        _render = app.World.Resource<RenderContext>();
        _render.DrawMainCamera = false;
        _ui = new CellUi(ascii);
        _terminal = terminal;
        if (terminal is null) _renderer = new CellRenderer(_render.Device, _render.Swapchain.Format, fontPath ?? throw new ArgumentNullException(nameof(fontPath)));
        else { _sceneTerminal = new TerminalViewport(_render.Device); _gameTerminal = new TerminalViewport(_render.Device); }
        _inspector = new InspectorVisitor(_ui, () => new[] { ("None", Guid.Empty), ("Cube", BuiltinAssets.Cube.Guid), ("White", BuiltinAssets.White.Guid) }
            .Concat(_assets.Files.Select(path => (path, _assets.FindByPath(path)))).ToArray());
        Document = new SceneDocument(app.World, assets.PrefabText);
        Document.Open(Document.Text);
        Files = new SceneFiles(root, Document, Jobs, Import);
        RegisterPanels();
        _cameras = app.World.Query(new QueryDesc().With<Camera>().With<LocalToWorld>());
        app.Schedule.Add(new HostSystem("Editor.UI", Phase.First, -10000, _ => Update()));
        app.Schedule.Add(new HostSystem("Editor.Draw", Phase.Render, 100, _ => Render()));
        BuildGame();
    }
    private sealed class HostSystem(string name, Phase phase, int order, Action<World> run) : ISystem
    {
        public string Name => name;
        public Phase Phase => phase;
        public int Order => order;
        public void Initialize(World world) { }
        public void Run(World world) => run(world);
    }
    public void Import()
    {
        if (_importPending || _reloadBuild is not null) { _importAgain = true; return; }
        _importPending = true;
        Jobs.Run("Import assets", stop => EditorAssets.Import(_root, stop), catalog =>
        {
            _assets.Apply(catalog);
            if (!_loaded)
            {
                _loaded = true;
                Files.RequestOpen("Assets/main.scene");
            }
        }, () => { _importPending = false; });
    }
    public void Pump()
    {
        Jobs.Pump();
        if (_reloadBuild is { } output && !_importPending)
        {
            _reloadBuild = null;
            try { ApplyGame(output); }
            catch (Exception e) { output.Dispose(); Log.Error("Reload failed: " + e.Message); }
        }
        if (_importAgain && !_importPending && _reloadBuild is null) { _importAgain = false; Import(); }
        if (_buildAgain && !_buildPending) { _buildAgain = false; BuildGame(); }
    }
    public void BuildGame()
    {
        if (_buildPending) { _buildAgain = true; return; }
        _buildPending = true;
        Jobs.Run("Build game scripts", stop =>
        {
            return GameCode.Build(_project, stop, _builds);
        }, AcceptBuild, () => { _buildPending = false; });
    }
    private void ApplyGame(BuildOutput output)
    {
        Document.CommitEdit();
        ulong selectedId = Document.SelectedId;
        string state = Document.Text; // Held text if a previous load failed, so the scene returns with working code.
        bool playing = Document.Playing;
        string? previous = _gameDll;
        try { _game.Stop(_app); } catch (Exception e) { Report("Stop before reload", e); }
        _app.World.Clear(); _game.Unload(_app);
        _reported.Clear();
        void RestoreState(bool schemaMayChange)
        {
            Document.Restore(state); Document.SelectSceneId(selectedId);
            if (schemaMayChange) Document.ClearHistory();
            if (playing) _game.Resume();
        }
        try
        {
            _game.Load(output.AssemblyPath, _app, Document, GameInput, Registry, _extensionPaths);
            RestoreState(schemaMayChange: true);
        }
        catch (Exception e)
        {
            Log.Error("Game reload: " + e);
            _app.World.Clear(); _game.Unload(_app); output.Dispose();
            try
            {
                if (previous is null) throw new InvalidOperationException("No earlier game build to return to.");
                // Same assembly as before the attempt: undo history stays valid.
                _game.Load(previous, _app, Document, GameInput, Registry, _extensionPaths);
                RestoreState(schemaMayChange: false);
            }
            catch (Exception rollback)
            {
                Log.Error("Previous game build could not be restored; the scene is held unchanged until the game builds again: " + rollback);
                _app.World.Clear(); _game.Unload(_app);
                if (playing) _app.RestoreClock(_editClock);
                Document.Hold(state);
                _gameDll = null; _activeBuild?.Dispose(); _activeBuild = null;
            }
            return;
        }
        _gameDll = output.AssemblyPath;
        _activeBuild?.Dispose(); _activeBuild = output;
        if (!_loaded) Import();
    }
    internal void RecoverOnDisconnect(Action completed)
    {
        StopPlay(); Document.CommitEdit();
        if (!Document.Dirty) { completed(); return; }
        string text = Document.Text;
        string directory = Path.Combine(_root, "Library", "recovery");
        string path = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".scene");
        Jobs.Run("Recover disconnected scene", _ => { Directory.CreateDirectory(directory); File.WriteAllText(path, text); return path; },
            recovered => Log.Info("Disconnected scene recovered to " + recovered), completed);
    }
    public void StopPlay() { if (Document.Playing) TogglePlay(); }
    private void TogglePlay()
    {
        if (Document.Playing)
        {
            try { _game.Stop(_app); }
            catch (Exception e) { Report("Stop play mode", e); }
            finally { Document.Stop(); _app.RestoreClock(_editClock); _app.QuitRequested = false; }
        }
        else
        {
            if (!_loaded || _gameDll is null || Files.Busy || Files.Dialog != SceneFileDialog.None) return;
            _editClock = _app.CaptureClock(); _app.RestoreClock(0); Document.Play();
            try { _game.Start(_app); }
            catch (Exception e) { Document.Stop(); _app.RestoreClock(_editClock); Log.Error("Enter play mode: " + e.Message); }
        }
    }
    private readonly Dictionary<string, string> _reported = new(StringComparer.Ordinal);
    /// <summary>Logs a failure once per source until its text changes, so a fault repeating every frame cannot flood the Console.</summary>
    internal void Report(string source, Exception e)
    {
        string text = e.ToString();
        if (_reported.TryGetValue(source, out var last) && last == text) return;
        _reported[source] = text;
        Log.Error(source + ": " + text);
    }
    private void Update()
    {
        // The editor outlives its own bugs: a failed frame is logged and the next frame starts clean.
        try { UpdateFrame(); }
        catch (Exception e) { Report("Editor frame", e); }
    }
    private void UpdateFrame()
    {
        var input = _app.World.Resource<Input>();
        var platform = _app.World.Resource<PlatformContext>();
        var (w, h) = platform.MainWindow.PixelSize;
        _clock += _app.World.Resource<Time>().Delta;
        _cellInput.RouteFrom(input, true, default);
        _cellInput.ScaleMouse(new Vector2(1f / CellWidth, 1f / CellHeight));
        w = Math.Max(1, w / CellWidth); h = Math.Max(1, h / CellHeight);
        input = _cellInput;
        _ui.KeyboardNavigation = !_gameFocused;
        _ui.Begin(input, platform.Platform, new Vector2(w, h), _clock);
        _ui.Draw.Rect(_ui.Screen, _ui.Theme.Background);
        if (Files.Dialog != SceneFileDialog.None)
        {
            _sceneArea = _gameArea = default;
            GameInput.RouteFrom(input, false, default); _gameFocused = false;
            FileDialog(); _ui.End(); _terminal?.Publish(_ui.Draw.Surface); return;
        }
        Toolbar(w);
        float availableH = Math.Max(4, h - 3);
        _left = Math.Clamp(_left, 10, Math.Max(10, w * 0.3f));
        _right = Math.Clamp(_right, 12, Math.Max(12, w * 0.35f));
        _bottom = Math.Clamp(_bottom, 3, Math.Max(3, availableH * 0.55f));
        float middleW = Math.Max(1, w - _left - _right - 2), topH = Math.Max(2, availableH - _bottom - 1);
        _left = _ui.Splitter(new Rect(_left, 2, 1, availableH), "left", _left, true, 10, Math.Max(10, w * 0.3f));
        _right = w - _ui.Splitter(new Rect(w - _right - 1, 2, 1, availableH), "right", w - _right, true, w * 0.65f, Math.Max(w * 0.65f, w - 12));
        float split = _ui.Splitter(new Rect(_left + 1, 2 + topH, middleW, 1), "bottom", 2 + topH, false, 4, Math.Max(4, h - 5));
        _bottom = h - 2 - split;
        Docks.Sync(Registry.Panels);
        _sceneArea = _gameArea = default;
        _dockAreas.Clear();
        _layout.Arrange(w, h, (int)_left, (int)_right, (int)_bottom);
        foreach (var region in Enum.GetValues<DockRegion>()) DrawDock(CellDraw.From(_layout.Region(region)), region);
        FinishDockDrag();
        _ui.TextIn(new Rect(0, h - 1, w, 1), ScenePath + (Document.Dirty ? " *" : "") + "    " + (Document.Playing ? _game.Paused ? "Paused" : "Playing" : "Editing") + (Document.Held is not null ? "    Scene held until the game builds" : "") + (Jobs.Busy ? "    Working…" : ""), _ui.Theme.TextDim);
        bool click = input.WasPressed(MouseButton.Left) || input.WasPressed(MouseButton.Right) || input.WasPressed(MouseButton.Middle);
        if (click) _gameFocused = _gameArea.W > 0 && _ui.IsHovered(_gameArea);
        if (_gameArea.W <= 0 || !Document.Playing || _ui.WantsKeyboard) _gameFocused = false;
        GameInput.RouteFrom(_app.World.Resource<Input>(), _gameFocused, PixelRect(_gameArea).Min, [Key.F5]);
        GameInput.ScaleMouse(new Vector2(GamePreviewScale));
        if (!_ui.WantsKeyboard && !_gameFocused)
        {
            if (input.Ctrl && input.WasPressed(Key.S)) { if (input.Shift) Files.SaveAs(); else Files.Save(); }
            if (input.Ctrl && input.WasPressed(Key.N)) Files.RequestNew();
            if (input.Ctrl && input.WasPressed(Key.Z)) { if (input.Shift) Document.Redo(); else Document.Undo(); }
            if (input.Ctrl && input.WasPressed(Key.Y)) Document.Redo();

        }
        if (!_ui.WantsKeyboard && input.WasPressed(Key.F5)) TogglePlay();
        if (_closeRequested)
        {
            _closeRequested = false;
            Document.CommitEdit();
            Files.RequestClose();
        }
        _ui.End(); _terminal?.Publish(_ui.Draw.Surface);
    }
    private void Toolbar(float width)
    {
        _ui.BeginArea(new Rect(0, 0, width, 1));
        _ui.Row(6, 6, 6, 6, 8, 7, 9, 5, 8, -1);
        if (_ui.Button("Save")) Files.Save();
        if (_ui.Button("Undo")) Document.Undo();
        if (_ui.Button("Redo")) Document.Redo();
        if (_ui.Button(Document.Playing ? "Stop" : "Play")) TogglePlay();
        if (_ui.Button(_game.Paused ? "Resume" : "Pause") && Document.Playing) _game.Paused = !_game.Paused;
        if (_ui.Button("Build")) BuildGame();
        if (_ui.Button("Reimport")) Import();
        if (_ui.Button("New")) Files.RequestNew();
        if (_ui.Button("Save As")) Files.SaveAs();
        var menus = Registry.Menus.ToArray();
        if (_ui.Combo(_ui.Next(), "editor-menu", ref _menuSelection, new[] { "Menu" }.Concat(menus.Select(m => m.Label)).ToArray()) && _menuSelection > 0)
        {
            var menu = menus[_menuSelection - 1]; _menuSelection = 0;
            try { menu.Execute(_context); }
            catch (Exception e) { Report("Menu " + menu.Label, e); }
        }
        _ui.EndArea();
    }
    private void EnsureTarget(ref GpuTexture? color, ref GpuTexture? depth, Rect area)
    {
        int w = Math.Max(1, (int)area.W * CellWidth), h = Math.Max(1, (int)area.H * CellHeight);
        if (color?.Width == w && color.Height == h) return;
        if (color is not null) { _renderer?.Forget(color); color.Dispose(); }
        depth?.Dispose();
        color = _render.Device.CreateTexture(new TextureDesc(w, h, _render.Swapchain.Format, TextureUsage.RenderTarget | TextureUsage.Sampled | TextureUsage.CopySrc));
        depth = _render.Device.CreateTexture(new TextureDesc(w, h, RenderContext.DepthFormat, TextureUsage.DepthStencil));
    }
    private void Render()
    {
        if (!_render.HasFrame) return;
        var frame = _render.Frame;
        if (_sceneArea.W > 0 && _sceneColor is not null)
            _render.Scene.DrawView(frame.Commands, new ViewDesc { Color = _sceneColor, Depth = _sceneDepth!, ViewProjection = _camera.Matrix(_sceneColor.Width / (float)_sceneColor.Height), ClearColor = new Vector4(0.08f, 0.09f, 0.12f, 1), DebugLines = true });
        if (_gameArea.W > 0 && _gameColor is not null)
        {
            MainCameraSystem.TryMainCamera(_cameras, _gameColor.Width / (float)_gameColor.Height, out var matrix, out var clear);
            _render.Scene.DrawView(frame.Commands, new ViewDesc { Color = _gameColor, Depth = _gameDepth!, ViewProjection = matrix, ClearColor = clear, Sprites = true, SpriteSize = new Vector2(_gameColor.Width, _gameColor.Height) * GamePreviewScale });
        }
        if (_terminal is not null)
        {
            if (_sceneArea.W > 0 && _sceneColor is not null) _sceneTerminal!.Capture(frame.Commands, _sceneColor);
            if (_gameArea.W > 0 && _gameColor is not null) _gameTerminal!.Capture(frame.Commands, _gameColor);
            return;
        }
        frame.Commands.BeginRenderPass(new RenderPassDesc { Color = new ColorAttachment { Texture = frame.Backbuffer, Load = LoadOp.Clear, Store = StoreOp.Store, ClearColor = new Vector4(0.1f, 0.1f, 0.1f, 1) } });
        _renderer!.Render(frame.Commands, frame.FrameIndex, _ui.Draw, frame.Backbuffer.Width, frame.Backbuffer.Height);
        frame.Commands.EndRenderPass();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Jobs.Dispose();
        try { _game.Stop(_app); } catch (Exception e) { Log.Error("Game stop during shutdown: " + e.Message); }
        _app.World.Clear();
        _game.Unload(_app);
        _reloadBuild?.Dispose(); _activeBuild?.Dispose(); _builds.Dispose();
        _render.Device.WaitIdle();
        Registry.Dispose();
        _sceneTerminal?.Dispose(); _gameTerminal?.Dispose();
        _renderer?.Dispose();
        _sceneColor?.Dispose(); _sceneDepth?.Dispose(); _gameColor?.Dispose(); _gameDepth?.Dispose();
    }
}
