using Gamegrl.Editor;

namespace Gamegrl.Tests;

internal static class SceneFilesTests
{
    public static void Register(Action<string, Action> test)
    {
        test("Scene files: new, Save As, overwrite, and close cancellation", () =>
        {
            using var f = new Fixture();
            f.Files.RequestNew();
            Check(f.Files.CurrentPath is null && f.Document.Dirty);
            f.Files.RequestClose();
            Check(f.Files.Dialog == SceneFileDialog.Unsaved && !f.Files.ExitRequested);
            f.Files.Cancel();
            Check(!f.Files.ExitRequested && f.Files.Dialog == SceneFileDialog.None);
            f.Files.RequestClose(); f.Files.SaveChanges();
            Check(f.Files.Dialog == SceneFileDialog.SaveAs);
            f.Files.Destination = "Assets/scenes/new.scene";
            f.Files.ConfirmDestination(); f.Wait();
            Check(f.Files.ExitRequested && !f.Document.Dirty);
            Check(File.ReadAllText(Path.Combine(f.Root, "Assets/scenes/new.scene")) == f.Document.Text);
            Check(f.Files.CurrentPath == "Assets/scenes/new.scene");
        });
        test("Scene files: overwrite requires confirmation and preserves destination on cancel", () =>
        {
            using var f = new Fixture();
            string path = Path.Combine(f.Root, "Assets/existing.scene");
            File.WriteAllText(path, "keep");
            f.Files.SaveAs(); f.Files.Destination = "Assets/existing.scene"; f.Files.ConfirmDestination(); f.Wait();
            Check(f.Files.Dialog == SceneFileDialog.Overwrite && File.ReadAllText(path) == "keep");
            Check(f.Files.CurrentPath is null);
            f.Files.Cancel();
            f.Files.SaveAs(); f.Files.Destination = "Assets/existing.scene"; f.Files.ConfirmDestination(); f.Wait();
            f.Files.ConfirmDestination(overwrite: true); f.Wait();
            Check(f.Files.CurrentPath == "Assets/existing.scene" && File.ReadAllText(path) == f.Document.Text);
        });
        test("Scene files: failed saves keep edits and pending navigation until retry or cancel", () =>
        {
            using var f = new Fixture();
            f.Document.Edit(() => f.World.Spawn(new Name("Keep")));
            string before = f.Document.Text;
            File.WriteAllText(Path.Combine(f.Root, "Assets/blocked"), "file, not a directory");
            f.Files.RequestNew(); f.Files.SaveChanges();
            f.Files.Destination = "Assets/blocked/no.scene"; f.Files.ConfirmDestination(); f.Wait();
            Check(f.Files.Error is not null && f.Document.Text == before && f.Document.Dirty);
            f.Files.Destination = "Assets/retry.scene"; f.Files.ConfirmDestination(); f.Wait();
            Check(File.ReadAllText(Path.Combine(f.Root, "Assets/retry.scene")) == before);
            Check(f.World.EntityCount == 0 && f.Files.CurrentPath is null && f.Document.Dirty);
        });
        test("Scene files: edits during save prompt again before discarding newer data", () =>
        {
            using var f = new Fixture();
            f.Files.SaveAs(); f.Files.Destination = "Assets/a.scene"; f.Files.ConfirmDestination(); f.Wait();
            f.Document.Edit(() => f.World.Spawn(new Name("First")));
            f.Files.RequestNew(); f.Files.SaveChanges();
            // Completion only applies through Pump, so this race is deterministic.
            f.Document.Edit(() => f.World.Spawn(new Name("Newer")));
            f.Wait();
            Check(f.Files.Dialog == SceneFileDialog.Unsaved && f.World.EntityCount == 2 && f.Document.Dirty);
            f.Files.Discard();
            Check(f.World.EntityCount == 0 && f.Files.CurrentPath is null);
        });
        test("Scene files: open cancellation, play guard, and path validation", () =>
        {
            using var f = new Fixture();
            File.WriteAllText(Path.Combine(f.Root, "Assets/empty.scene"), Scene.TextHeader + "\n");
            f.Document.Edit(() => f.World.Spawn(new Name("Keep")));
            f.Files.RequestOpen("Assets/empty.scene"); f.Files.Cancel();
            Check(f.World.EntityCount == 1);
            f.Files.RequestOpen("Assets/empty.scene"); f.Files.Discard(); f.Wait();
            Check(f.World.EntityCount == 0 && f.Files.CurrentPath == "Assets/empty.scene");
            f.Document.Play(); f.Files.RequestNew(); f.Files.SaveAs();
            Check(f.Files.Dialog == SceneFileDialog.None && f.Files.CurrentPath == "Assets/empty.scene");
            foreach (string path in new[] { "../outside.scene", "Assets/../../outside.scene", "Assets/a.txt", "/tmp/outside.scene" })
            {
                bool rejected = false;
                try { SceneFiles.ValidatePath(f.Root, path); } catch (ArgumentException) { rejected = true; }
                Check(rejected);
            }
        });
    }
    private static void Check(bool condition) { if (!condition) throw new Exception("Scene file assertion failed"); }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gamegrl-files-" + Guid.NewGuid().ToString("N"));
        public World World { get; } = new();
        public SceneDocument Document { get; }
        public EditorJobs Jobs { get; } = new();
        public SceneFiles Files { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, "Assets"));
            Document = new SceneDocument(World); Document.Open(Document.Text);
            Files = new SceneFiles(Root, Document, Jobs, () => { });
        }
        public void Wait()
        {
            if (!SpinWait.SpinUntil(() => { Jobs.Pump(); return !Jobs.Busy; }, 10000)) throw new TimeoutException();
        }
        public void Dispose() { Jobs.Dispose(); World.Dispose(); Directory.Delete(Root, recursive: true); }
    }
}
