using System.Diagnostics;
using System.Numerics;
using Gamegrl.Assets;
using Gamegrl.Editor;
using Gamegrl.Platform;
using Gamegrl.Platform.Sdl3;
using Gamegrl.Rendering;
using Gamegrl.Rhi.Vulkan;

namespace Gamegrl.Tests;

internal static class GraphicsSmoke
{
    public static int Run()
    {
        string source = Path.GetFullPath("samples/Spinners");
        string root = Path.Combine(Path.GetTempPath(), "gamegrl-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        foreach (string file in Directory.EnumerateFiles(Path.Combine(source, "Assets"))) File.Copy(file, Path.Combine(root, "Assets", Path.GetFileName(file)));
        var assets = new EditorAssets();
        using var app = EngineBuilder.Create()
            .AddModule(new Sdl3PlatformModule(WindowDesc.Default("gamegrl · smoke")))
            .AddModule(new AssetsModule(assets))
            .AddModule<VulkanModule>()
            .AddModule(new RenderingModule()).Build();
        using var editor = new EditorHost(app, assets, root, Path.Combine(source, "Game/Spinners.Game.csproj"),
            Environment.GetEnvironmentVariable("GAMEGRL_FONT") ?? "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf");
        var platform = app.World.Resource<PlatformContext>();
        var input = app.World.Resource<Input>();
        void Frame(Action? inject = null)
        {
            input.BeginFrame(); platform.Platform.PumpEvents(input); inject?.Invoke(); editor.Pump(); app.Tick(1.0 / 60);
        }
        void Wait(Func<bool> ready)
        {
            var timer = Stopwatch.StartNew();
            while (!ready()) { if (timer.Elapsed.TotalSeconds > 60) throw new TimeoutException("Editor smoke timed out"); Frame(); }
        }
        void Click(float x, float y)
        {
            Frame(() => { input.OnMouseMove(new Vector2(x, y), Vector2.Zero); input.OnMouseButton(0, true); });
            Frame(() => input.OnMouseButton(0, false)); Frame();
        }
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        Wait(() => !editor.Jobs.Busy && app.World.EntityCount > 0);
        int count = app.World.EntityCount;
        Click(50, 70); // Create, through the actual UI.
        Check(app.World.EntityCount == count + 1, "Create button did not create an entity");
        Check(!editor.Document.Selected.IsNull, "Created entity was not selected");
        Click(100, 15); // Undo.
        Check(app.World.EntityCount == count, "Undo button did not restore the scene");
        Click(170, 15); // Redo.
        Check(app.World.EntityCount == count + 1, "Redo button did not restore the created entity");
        // Drag the selected entity's world-X handle through the UI, then undo it.
        var selected = editor.Document.Selected;
        var camera = new SceneCamera();
        var area = new Gamegrl.UI.Rect(234, 56, 722, 420);
        var matrix = camera.Matrix(area.W / area.H);
        var position = app.World.Get<LocalTransform>(selected).Position;
        float length = Vector3.Distance(camera.Position, position) * 0.12f;
        Check(SceneCamera.Project(position + Vector3.UnitX * length, matrix, area, out var handle), "Gizmo is behind camera");
        var moved = handle + new Vector2(30, 0);
        Frame(() => { input.OnMouseMove(handle, Vector2.Zero); input.OnMouseButton(0, true); });
        Frame(() => input.OnMouseMove(moved, new Vector2(30, 0)));
        Frame(() => input.OnMouseButton(0, false)); Frame();
        Check(app.World.Get<LocalTransform>(selected).Position.X > position.X, "Move gizmo did not translate the entity");
        Click(100, 15);
        Check(app.World.Get<LocalTransform>(editor.Document.Selected).Position == position, "Undo did not revert the gizmo drag");
        // Select a mesh through the scene view rather than the hierarchy.
        editor.Document.Selected = default;
        SceneCamera.Project(new Vector3(0, 5, 0), matrix, area, out var meshPoint);
        Click(meshPoint.X, meshPoint.Y);
        Check(app.World.IsAlive(editor.Document.Selected) && app.World.Has<MeshRenderer>(editor.Document.Selected), "Scene picking missed the mesh");
        string before = editor.Document.Text;
        Click(335, 44); // Game view, rendered into a sampled texture.
        Click(240, 15); // Play.
        Check(editor.Document.Playing && app.World.EntityCount > count + 1, "Play did not run game startup");
        Check(!editor.GameInput.IsDown(MouseButton.Left), "Toolbar input leaked into the game");
        Click(500, 200);
        Frame(() => input.OnKey((int)Key.Space, true, false));
        Check(editor.GameInput.IsDown(Key.Space), "Focused Game view did not receive input");
        Check(editor.GameInput.MousePosition == new Vector2(266, 144), "Game mouse coordinates were not translated");
        Click(300, 550);
        Check(!editor.GameInput.IsDown(Key.Space) && input.IsDown(Key.Space), "Losing Game focus did not isolate held input");
        Frame(() => input.OnKey((int)Key.Space, false, false));
        int playingCount = app.World.EntityCount;
        string firstBuild = editor.GameAssemblyPath!;
        editor.BuildGame();
        Wait(() => !editor.Jobs.Busy);
        Frame();
        Check(editor.Document.Playing && app.World.EntityCount == playingCount, "Code reload duplicated or lost startup entities");
        GC.Collect(); GC.WaitForPendingFinalizers();
        editor.DrainBuildCleanupAsync().GetAwaiter().GetResult();
        Check(!File.Exists(firstBuild) && File.Exists(editor.GameAssemblyPath), "Superseded build was not retired");
        string currentBuild = editor.GameAssemblyPath!;
        using (var invalidBuilds = new BuildWorkspace(Path.Combine(root, "invalid-builds")))
        {
            var invalid = invalidBuilds.Create("invalid"); File.WriteAllText(invalid.AssemblyPath, "invalid assembly"); invalid.Complete();
            editor.AcceptBuild(invalid); Frame();
            Check(editor.GameAssemblyPath == currentBuild && File.Exists(currentBuild) && editor.Document.Playing && app.World.EntityCount == playingCount,
                "Failed reload did not retain the current build and running scene");
        }
        Click(240, 15); // Stop.
        Check(!editor.Document.Playing && editor.Document.Text == before, "Stop did not restore the edit scene");
        Click(265, 44); // Scene view.
        Click(35, 15); // Save; only the temporary copy is written.
        Wait(() => !editor.Jobs.Busy);
        Check(File.ReadAllText(Path.Combine(root, "Assets/main.scene")) == before, "Save did not write the edited scene");
        Check(!editor.Document.Dirty, "Save left the document dirty");
        Frame(() => { input.OnMouseMove(new Vector2(350, 492), Vector2.Zero); input.OnMouseButton(0, true); });
        Frame(() => input.OnMouseMove(new Vector2(100, 300), new Vector2(-250, -192)));
        Frame(() => input.OnMouseButton(0, false)); Frame();
        Check(editor.Docks.Selected(DockRegion.Left) == "console", "Dragging a tab did not dock it in the left region");
        editor.Docks.Move("console", DockRegion.Bottom, 1); editor.Docks.Select(DockRegion.Left, "hierarchy"); Frame();
        Click(460, 15); // New scene.
        Check(app.World.EntityCount == 0 && editor.Files.CurrentPath is null && editor.Document.Dirty, "New scene failed");
        Click(35, 15); // Untitled Save opens Save As.
        Check(editor.Files.Dialog == SceneFileDialog.SaveAs, "Untitled Save did not ask for a path");
        Click(500, 313); // Path field.
        Frame(() => { input.OnKey((int)Key.LeftCtrl, true, false); input.OnKey((int)Key.A, true, false); });
        Frame(() => { input.OnKey((int)Key.LeftCtrl, false, false); input.OnKey((int)Key.A, false, false); input.OnText("Assets/new.scene"); });
        Frame(() => input.OnKey((int)Key.Enter, true, false));
        Frame(() => input.OnKey((int)Key.Enter, false, false));
        Click(500, 338); // Save As confirmation.
        Wait(() => !editor.Jobs.Busy);
        Check(editor.Files.CurrentPath == "Assets/new.scene" && !editor.Document.Dirty, "Save As did not save the new scene");
        Check(File.Exists(Path.Combine(root, "Assets/new.scene")), "New scene file is missing");
        Click(545, 15); // Save As the same name requires an overwrite decision.
        Click(500, 338);
        Wait(() => !editor.Jobs.Busy);
        Check(editor.Files.Dialog == SceneFileDialog.Overwrite, "Existing file was replaced without asking");
        Click(800, 313); // Cancel overwrite.
        Click(460, 15); // New untitled scene, then cancel closing it.
        editor.RequestClose(); Frame();
        Check(editor.Files.Dialog == SceneFileDialog.Unsaved, "Dirty close did not ask");
        Click(825, 313);
        Check(!editor.Files.ExitRequested && editor.Files.Dialog == SceneFileDialog.None, "Cancel close failed");
        editor.RequestClose(); Frame();
        Click(640, 313); // Discard and close.
        Check(editor.Files.ExitRequested, "Discard did not finish closing");
        string finalBuild = editor.GameAssemblyPath!;
        editor.Dispose(); GC.Collect(); GC.WaitForPendingFinalizers();
        editor.DrainBuildCleanupAsync().GetAwaiter().GetResult();
        Check(!File.Exists(finalBuild), "Shutdown did not retire the final build");
        Console.WriteLine("Editor graphics smoke passed: create, Inspector, undo/redo, move gizmo, mesh picking, Game and Scene textures, play/stop, live code reload, save, New/Save As, overwrite and close dialogs, input focus, tab docking, reload rollback and build cleanup.");
        return 0;
    }
}
