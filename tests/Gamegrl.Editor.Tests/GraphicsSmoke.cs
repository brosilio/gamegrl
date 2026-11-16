using System.Diagnostics;
using System.Numerics;
using System.Text;
using AnsiDotNet;
using Gamegrl.Assets;
using Gamegrl.Editor;
using Gamegrl.Platform;
using Gamegrl.Platform.Sdl3;
using Gamegrl.Rendering;
using Gamegrl.Rhi;
using Gamegrl.Rhi.Vulkan;
using Key = Gamegrl.Platform.Key;
using Rect = Gamegrl.UI.Rect;
namespace Gamegrl.Tests;

internal static class GraphicsSmoke
{
    public static int Run(bool terminalMode = false)
    {
        string source = Path.GetFullPath("samples/Spinners");
        string root = Path.Combine(Path.GetTempPath(), "gamegrl-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        foreach (string file in Directory.EnumerateFiles(Path.Combine(source, "Assets"))) File.Copy(file, Path.Combine(root, "Assets", Path.GetFileName(file)));
        var assets = new EditorAssets();
        using var backend = terminalMode ? new RecordingBackend() : null;
        using var terminal = backend is not null ? Terminal.Open(backend, new TerminalOptions { Mouse = MouseMode.AnyMotion }) : null;
        using var display = terminal is not null ? new TerminalDisplay(terminal) : null;
        var builder = EngineBuilder.Create();
        TerminalPlatform? terminalPlatform = null;
        if (terminal is not null)
        {
            terminalPlatform = new TerminalPlatform(terminal);
            builder.SetResource(new Input()).SetResource(new PlatformContext(terminalPlatform, terminalPlatform.CreateWindow(WindowDesc.Default("terminal smoke"))));
        }
        else builder.AddModule(new Sdl3PlatformModule(WindowDesc.Default("gamegrl · smoke")));
        using var app = builder.AddModule(new AssetsModule(assets)).AddModule<VulkanModule>().AddModule(new RenderingModule()).Build();
        using var editor = new EditorHost(app, assets, root, Path.Combine(source, "Game/Spinners.Game.csproj"),
            terminalMode ? null : Environment.GetEnvironmentVariable("GAMEGRL_FONT") ?? "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf", display);
        var platform = app.World.Resource<PlatformContext>(); var input = app.World.Resource<Input>();
        void Frame(Action? inject = null)
        { input.BeginFrame(); platform.Platform.PumpEvents(input); inject?.Invoke(); editor.Pump(); app.Tick(1.0 / 60); }
        void Wait(Func<bool> ready)
        {
            var timer = Stopwatch.StartNew();
            while (!ready()) { if (timer.Elapsed.TotalSeconds > 90) throw new TimeoutException("Editor smoke timed out"); Frame(); }
        }
        Vector2 Pixels(Vector2 p) => p * new Vector2(editor.CellWidth, editor.CellHeight);
        void Point(Vector2 p, bool? down = null)
        {
            if (terminalPlatform is not null)
                terminalPlatform.Feed(InputEvent.ForMouse(AnsiDotNet.MouseButton.Left, down is null ? MouseAction.Drag : down.Value ? MouseAction.Press : MouseAction.Release, (int)p.X, (int)p.Y), input);
            else
            { var pixel = Pixels(p); input.OnMouseMove(pixel, pixel - input.MousePosition); if (down is { } held) input.OnMouseButton(0, held); }
        }
        void Click(Vector2 point)
        { Frame(() => Point(point, true)); Frame(() => Point(point, false)); Frame(); }
        void Button(string label)
        { var rect = editor.Ui.ControlRect(label) ?? throw new Exception("Missing control: " + label); Click(rect.Center); }
        Rect Tab(string id, DockRegion region) => editor.TabRect(id);
        void Command(string id) { Check(editor.RunCommand(id), "Missing command: " + id); Frame(); }
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        Wait(() => !editor.Jobs.Busy && app.World.EntityCount > 0);
        if (terminalMode)
        {
            Wait(() => HasPreview(editor.Ui.Draw.Surface, editor.SceneArea));
            Check(editor.CellWidth == 1 && editor.CellHeight == 2, "Terminal cell aspect is incorrect");
            ReadbackCheck(app.World.Resource<GpuDevice>(), platform.MainWindow);
        }
        int count = app.World.EntityCount;
        Button("Create"); Check(app.World.EntityCount == count + 1, "Create failed");
        Check(!editor.Document.Selected.IsNull, "Created entity is not selected");
        Command("edit.undo"); Check(app.World.EntityCount == count, "Undo failed");
        Command("edit.redo"); Check(app.World.EntityCount == count + 1, "Redo failed");
        var selected = editor.Document.Selected;
        var camera = new SceneCamera(); var area = editor.SceneArea;
        var pixels = editor.PixelRect(area); var matrix = camera.Matrix(pixels.W / pixels.H);
        var position = app.World.Get<LocalTransform>(selected).Position;
        float length = Vector3.Distance(camera.Position, position) * 0.12f;
        Check(SceneCamera.Project(position + Vector3.UnitX * length, matrix, area, out var handle), "Gizmo behind camera");
        Frame(() => Point(handle, true)); Frame(() => Point(handle + new Vector2(3, 0))); Frame(() => Point(handle + new Vector2(3, 0), false)); Frame();
        Check(app.World.Get<LocalTransform>(selected).Position.X > position.X, "Move gizmo failed");
        Command("edit.undo"); Check(app.World.Get<LocalTransform>(editor.Document.Selected).Position == position, "Gizmo undo failed");
        editor.Document.Selected = default;
        SceneCamera.Project(new Vector3(0, 5, 0), matrix, area, out var meshPoint); Click(meshPoint);
        Check(app.World.IsAlive(editor.Document.Selected) && app.World.Has<MeshRenderer>(editor.Document.Selected), "Scene picking failed");
        string before = editor.Document.Text;
        Click(Tab("game", DockRegion.Center).Center); Button("Play");
        Check(editor.Document.Playing && app.World.EntityCount > count + 1, "Game startup failed");
        Check(!editor.GameInput.IsDown(Gamegrl.Platform.MouseButton.Left), "Toolbar input leaked");
        Click(editor.GameArea.Center); Frame(() => input.OnKey((int)Key.Space, true, false));
        Check(editor.GameInput.IsDown(Key.Space), "Game focus failed");
        Check(Vector2.Distance(editor.GameInput.MousePosition, (input.MousePosition - editor.PixelRect(editor.GameArea).Min) * editor.GamePreviewScale) < 0.01f, "Game coordinates failed");
        Click(editor.DockArea(DockRegion.Left).Center);
        Check(!editor.GameInput.IsDown(Key.Space) && input.IsDown(Key.Space), "Input isolation failed");
        Frame(() => input.OnKey((int)Key.Space, false, false));
        Check(app.World.Resource<ScreenSize>().Width == (int)(editor.GameArea.W * editor.CellWidth * editor.GamePreviewScale), "Logical sprite coordinates changed with preview resolution");
        int playingCount = app.World.EntityCount; string firstBuild = editor.GameAssemblyPath!;
        editor.BuildGame(); Wait(() => !editor.Jobs.Busy); Frame();
        Check(editor.Document.Playing && app.World.EntityCount == playingCount, "Reload duplicated or lost entities");
        GC.Collect(); GC.WaitForPendingFinalizers(); editor.DrainBuildCleanupAsync().GetAwaiter().GetResult();
        Check(!File.Exists(firstBuild) && File.Exists(editor.GameAssemblyPath), "Old build not retired");
        string current = editor.GameAssemblyPath!;
        using (var invalidBuilds = new BuildWorkspace(Path.Combine(root, "invalid-builds")))
        {
            var invalid = invalidBuilds.Create("invalid"); File.WriteAllText(invalid.AssemblyPath, "invalid"); invalid.Complete();
            editor.AcceptBuild(invalid); Frame();
            Check(editor.GameAssemblyPath == current && File.Exists(current) && editor.Document.Playing && app.World.EntityCount == playingCount, "Rollback failed");
        }
        Button("Stop"); Check(!editor.Document.Playing && editor.Document.Text == before, "Stop restore failed");
        Click(Tab("scene", DockRegion.Center).Center); Command("file.save"); Wait(() => !editor.Jobs.Busy);
        Check(File.ReadAllText(Path.Combine(root, "Assets/main.scene")) == before && !editor.Document.Dirty, "Save failed");
        Vector2 consoleTab = Tab("console", DockRegion.Bottom).Center, destination = editor.DockArea(DockRegion.Left).Center;
        Frame(() => Point(consoleTab, true)); Frame(() => Point(destination)); Frame(() => Point(destination, false)); Frame();
        Check(editor.Docks.Selected(DockRegion.Left) == "console", "Tab docking failed");
        Frame(() => { input.OnKey((int)Key.LeftCtrl, true, false); input.OnKey((int)Key.P, true, false); });
        Frame(() => { input.OnKey((int)Key.LeftCtrl, false, false); input.OnKey((int)Key.P, false, false); input.OnText("toggle sidebar"); });
        Check(editor.PaletteOpen, "Command palette did not open");
        Frame(() => input.OnKey((int)Key.Enter, true, false)); Frame(() => input.OnKey((int)Key.Enter, false, false));
        Check(!editor.PaletteOpen && editor.DockArea(DockRegion.Left).W == 0, "Palette command did not run");
        Command("view.sidebar");
        editor.Docks.Move("console", DockRegion.Bottom, 1); editor.Docks.Select(DockRegion.Left, "hierarchy"); Frame();
        Command("file.new"); Check(app.World.EntityCount == 0 && editor.Files.CurrentPath is null && editor.Document.Dirty, "New failed");
        Command("file.save"); Check(editor.Files.Dialog == SceneFileDialog.SaveAs, "Missing Save As dialog");
        Button("destination");
        Frame(() => { input.OnKey((int)Key.LeftCtrl, true, false); input.OnKey((int)Key.A, true, false); });
        Frame(() => { input.OnKey((int)Key.LeftCtrl, false, false); input.OnKey((int)Key.A, false, false); input.OnText("Assets/new.scene"); });
        Frame(() => input.OnKey((int)Key.Enter, true, false)); Frame(() => input.OnKey((int)Key.Enter, false, false));
        Button("Save"); Wait(() => !editor.Jobs.Busy);
        Check(editor.Files.CurrentPath == "Assets/new.scene" && !editor.Document.Dirty, "Save As failed");
        Command("file.saveAs"); Button("Save"); Wait(() => !editor.Jobs.Busy);
        Check(editor.Files.Dialog == SceneFileDialog.Overwrite, "Overwrite guard failed"); Button("Cancel");
        Command("file.new"); editor.RequestClose(); Frame(); Frame(); Check(editor.Files.Dialog == SceneFileDialog.Unsaved, "Unsaved guard failed");
        Button("Cancel"); Check(!editor.Files.ExitRequested, "Close cancellation failed");
        editor.RequestClose(); Frame(); Frame(); Button("Discard"); Check(editor.Files.ExitRequested, "Discard failed");
        if (backend is not null)
        {
            Wait(() => backend.Bytes > 1000);
            File.WriteAllBytes("/tmp/gamegrl-terminal-smoke.ansi", backend.Snapshot());
        }
        string finalBuild = editor.GameAssemblyPath!; editor.Dispose(); GC.Collect(); GC.WaitForPendingFinalizers(); editor.DrainBuildCleanupAsync().GetAwaiter().GetResult();
        Check(!File.Exists(finalBuild), "Shutdown cleanup failed");
        Console.Error.WriteLine((terminalMode ? "Terminal" : "Desktop") + " cell editor smoke passed: real viewports, editing, play/stop, reload/rollback, focus, docking, file dialogs and cleanup.");
        return 0;
    }
    private static bool HasPreview(Surface surface, Rect area)
    { var r = CellDraw.To(area); for (int y = r.Y; y < r.Bottom; y++) for (int x = r.X; x < r.Right; x++) if (surface[x, y].Rune.Value == '▀') return true; return false; }
    private static void ReadbackCheck(GpuDevice device, IWindow window)
    {
        using var swapchain = device.CreateSwapchain(window, false);
        using var texture = device.CreateTexture(new TextureDesc(4, 4, TextureFormat.Rgba8Unorm, TextureUsage.RenderTarget | TextureUsage.CopySrc));
        using var download = device.CreateReadback(4, 4);
        Frame frame = default;
        if (!SpinWait.SpinUntil(() => device.BeginFrame(swapchain, out frame), 5000)) throw new Exception("Offscreen BeginFrame failed");
        frame.Commands.BeginRenderPass(new RenderPassDesc { Color = new ColorAttachment { Texture = texture, Load = LoadOp.Clear, Store = StoreOp.Store, ClearColor = new Vector4(1, 0, 0, 1) } });
        frame.Commands.EndRenderPass(); frame.Commands.CopyToReadback(texture, download); device.EndFrame(frame);
        byte[] bytes = new byte[64];
        if (!SpinWait.SpinUntil(() => download.TryRead(bytes), 5000) || bytes[0] != 255 || bytes[1] != 0 || bytes[2] != 0) throw new Exception("Async GPU readback returned incorrect pixels");
    }
    private sealed class RecordingBackend : ITerminalBackend
    {
        private readonly Lock _gate = new(); private readonly MemoryStream _bytes = new();
        public TerminalSize Size => new(120, 40);
        public int Bytes { get { lock (_gate) return (int)_bytes.Length; } }
        public void Start(ITerminalInput input) { }
        public void Write(ReadOnlySpan<byte> bytes) { lock (_gate) { if (_bytes.Length < 8 << 20) _bytes.Write(bytes); } }
        public byte[] Snapshot() { lock (_gate) return _bytes.ToArray(); }
        public void Dispose() { }
    }
}
