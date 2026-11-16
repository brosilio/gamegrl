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
    private readonly List<string> _extensionPaths = new();
    public void AddExtension(string path) => _extensionPaths.Add(Path.GetFullPath(path));
    private readonly RenderContext _render;
    private readonly Ui _ui;
    private readonly UiRenderer _renderer;
    private readonly SceneCamera _camera = new();
    private readonly Query _cameras;
    private readonly Dictionary<ulong, bool> _expanded = new();
    private readonly InspectorVisitor _inspector;
    private GpuTexture? _sceneColor, _sceneDepth, _gameColor, _gameDepth;
    private Rect _sceneArea, _gameArea;
    private int _menuSelection;
    private float _left = 230, _right = 320, _bottom = 210;
    private double _clock;
    private bool _importPending, _buildPending, _importAgain, _buildAgain;
    private string? _reloadDll;
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

    public EditorHost(App app, EditorAssets assets, string root, string project, string fontPath)
    {
        _app = app; _assets = assets; _root = root; _project = project;
        _render = app.World.Resource<RenderContext>();
        _render.DrawMainCamera = false;
        var font = FontAtlas.Bake(new TrueTypeFont(File.ReadAllBytes(fontPath)), 14);
        _ui = new Ui(font);
        _renderer = new UiRenderer(_render.Device, _render.Swapchain.Format, font);
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
        if (_importPending || _reloadDll is not null) { _importAgain = true; return; }
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
        if (_reloadDll is { } dll && !_importPending)
        {
            _reloadDll = null;
            ApplyGame(dll);
        }
        if (_importAgain && !_importPending && _reloadDll is null) { _importAgain = false; Import(); }
        if (_buildAgain && !_buildPending) { _buildAgain = false; BuildGame(); }
    }
    public void BuildGame()
    {
        if (_buildPending) { _buildAgain = true; return; }
        _buildPending = true;
        Jobs.Run("Build game scripts", stop =>
        {
            return GameCode.Build(_project, stop);
        }, dll => { _reloadDll = dll; }, () => { _buildPending = false; });
    }
    private void ApplyGame(string dll)
    {
        Document.CommitEdit();
        ulong selectedId = Document.SelectedId;
        string state = Document.Text;
        bool playing = Document.Playing;
        string? previous = _gameDll;
        _game.Stop(_app);
        _app.World.Clear();
        _game.Unload(_app);
        try
        {
            _game.Load(dll, _app, Document, GameInput, Registry, _extensionPaths);
            _gameDll = dll;
        }
        catch (Exception e)
        {
            Log.Error("Game reload: " + e.Message);
            _game.Unload(_app);
            if (previous is not null) _game.Load(previous, _app, Document, GameInput, Registry, _extensionPaths);
        }
        Document.Restore(state);
        Document.SelectSceneId(selectedId);
        Document.ClearHistory();
        if (playing)
        {
            try { _game.Resume(); }
            catch (Exception e) { Document.Stop(); _app.RestoreClock(_editClock); Log.Error("Resume after reload: " + e.Message); }
        }
        if (!_loaded) Import();
    }
    public void StopPlay() { if (Document.Playing) TogglePlay(); }
    private void TogglePlay()
    {
        if (Document.Playing)
        {
            try { _game.Stop(_app); }
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
    private void Update()
    {
        var input = _app.World.Resource<Input>();
        var platform = _app.World.Resource<PlatformContext>();
        var (w, h) = platform.MainWindow.PixelSize;
        _clock += _app.World.Resource<Time>().Delta;
        _ui.Begin(input, platform.Platform, new Vector2(w, h), _clock, _renderer.FontTexture);
        _ui.Draw.Rect(_ui.Screen, _ui.Theme.Background);
        if (Files.Dialog != SceneFileDialog.None)
        {
            _sceneArea = _gameArea = default;
            GameInput.RouteFrom(input, false, default); _gameFocused = false;
            FileDialog(); _ui.End(); return;
        }
        Toolbar(w);
        float availableH = Math.Max(100, h - 62);
        _left = Math.Clamp(_left, 120, Math.Max(120, w * 0.35f));
        _right = Math.Clamp(_right, 150, Math.Max(150, w * 0.4f));
        _bottom = Math.Clamp(_bottom, 80, Math.Max(80, availableH * 0.65f));
        float middleW = Math.Max(1, w - _left - _right - 8), topH = availableH - _bottom - 4;
        _left = _ui.Splitter(new Rect(_left, 32, 4, availableH), "left", _left, true, 120, Math.Max(120, w * 0.35f));
        _right = w - _ui.Splitter(new Rect(w - _right - 4, 32, 4, availableH), "right", w - _right, true, w * 0.6f, w - 150);
        float split = _ui.Splitter(new Rect(_left + 4, 32 + topH, middleW, 4), "bottom", 32 + topH, false, 100, h - 110);
        _bottom = h - 30 - split - 4;
        Docks.Sync(Registry.Panels);
        _sceneArea = _gameArea = default;
        _dockAreas.Clear();
        DrawDock(new Rect(0, 32, _left, availableH), DockRegion.Left);
        DrawDock(new Rect(w - _right, 32, _right, availableH), DockRegion.Right);
        DrawDock(new Rect(_left + 4, 32, middleW, Math.Max(1, topH)), DockRegion.Center);
        DrawDock(new Rect(_left + 4, 32 + topH + 4, middleW, _bottom), DockRegion.Bottom);
        FinishDockDrag();
        _ui.TextIn(new Rect(0, h - 26, w, 26), ScenePath + (Document.Dirty ? " *" : "") + "    " + (Document.Playing ? "Playing" : "Editing") + (Jobs.Busy ? "    Working…" : ""), _ui.Theme.TextDim);
        bool click = input.WasPressed(MouseButton.Left) || input.WasPressed(MouseButton.Right) || input.WasPressed(MouseButton.Middle);
        if (click) _gameFocused = _gameArea.W > 0 && _ui.IsHovered(_gameArea);
        if (_gameArea.W <= 0 || !Document.Playing || _ui.WantsKeyboard) _gameFocused = false;
        GameInput.RouteFrom(input, _gameFocused, _gameArea.Min, [Key.F5]);
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
        _ui.End();
    }
    private void Toolbar(float width)
    {
        _ui.BeginArea(new Rect(5, 4, width - 10, 25));
        _ui.Row(65, 65, 65, 65, 65, 90, 65, 75, -1);
        if (_ui.Button("Save")) Files.Save();
        if (_ui.Button("Undo")) Document.Undo();
        if (_ui.Button("Redo")) Document.Redo();
        if (_ui.Button(Document.Playing ? "Stop" : "Play")) TogglePlay();
        if (_ui.Button("Build")) BuildGame();
        if (_ui.Button("Reimport")) Import();
        if (_ui.Button("New")) Files.RequestNew();
        if (_ui.Button("Save As")) Files.SaveAs();
        var menus = Registry.Menus.ToArray();
        if (_ui.Combo(_ui.Next(), "editor-menu", ref _menuSelection, new[] { "Menu" }.Concat(menus.Select(m => m.Label)).ToArray()) && _menuSelection > 0)
        { menus[_menuSelection - 1].Execute(_context); _menuSelection = 0; }
        _ui.EndArea();
    }
    private void EnsureTarget(ref GpuTexture? color, ref GpuTexture? depth, Rect area)
    {
        int w = Math.Max(1, (int)area.W), h = Math.Max(1, (int)area.H);
        if (color?.Width == w && color.Height == h) return;
        if (color is not null) { _renderer.Forget(color); color.Dispose(); }
        depth?.Dispose();
        color = _render.Device.CreateTexture(new TextureDesc(w, h, _render.Swapchain.Format, TextureUsage.RenderTarget | TextureUsage.Sampled));
        depth = _render.Device.CreateTexture(new TextureDesc(w, h, RenderContext.DepthFormat, TextureUsage.DepthStencil));
    }
    private void Render()
    {
        if (!_render.HasFrame) return;
        var frame = _render.Frame;
        if (_sceneArea.W > 0 && _sceneColor is not null)
            _render.Scene.DrawView(frame.Commands, new ViewDesc { Color = _sceneColor, Depth = _sceneDepth!, ViewProjection = _camera.Matrix(_sceneArea.W / _sceneArea.H), ClearColor = new Vector4(0.08f, 0.09f, 0.12f, 1), DebugLines = true });
        if (_gameArea.W > 0 && _gameColor is not null)
        {
            MainCameraSystem.TryMainCamera(_cameras, _gameArea.W / _gameArea.H, out var matrix, out var clear);
            _render.Scene.DrawView(frame.Commands, new ViewDesc { Color = _gameColor, Depth = _gameDepth!, ViewProjection = matrix, ClearColor = clear, Sprites = true });
        }
        frame.Commands.BeginRenderPass(new RenderPassDesc { Color = new ColorAttachment { Texture = frame.Backbuffer, Load = LoadOp.Clear, Store = StoreOp.Store, ClearColor = new Vector4(0.1f, 0.1f, 0.1f, 1) } });
        _renderer.Render(frame.Commands, frame.FrameIndex, _ui.Draw, frame.Backbuffer.Width, frame.Backbuffer.Height);
        frame.Commands.EndRenderPass();
    }
    public void Dispose()
    {
        Jobs.Dispose();
        _game.Stop(_app);
        _app.World.Clear();
        _game.Unload(_app);
        _render.Device.WaitIdle();
        Registry.Dispose();
        _renderer.Dispose();
        _sceneColor?.Dispose(); _sceneDepth?.Dispose(); _gameColor?.Dispose(); _gameDepth?.Dispose();
    }
}
