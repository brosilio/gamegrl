using Gamegrl.Editor;
namespace Gamegrl.Tests;
internal static class BuildWorkspaceTests
{
    public static void Register(Action<string, Action> test)
    {
        test("Build workspace: retain current outputs, retire completed builds, defer running builds", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "gamegrl-workspace-test-" + Guid.NewGuid().ToString("N"));
            using var workspace = new BuildWorkspace(root);
            var current = workspace.Create("current"); File.WriteAllText(current.AssemblyPath, "assembly"); current.Complete();
            var retired = workspace.Create("retired"); File.WriteAllText(retired.AssemblyPath, "assembly"); retired.Complete(); retired.Dispose();
            workspace.DrainAsync().GetAwaiter().GetResult();
            Check(File.Exists(current.AssemblyPath) && !Directory.Exists(retired.Directory));
            var running = workspace.Create("running"); running.Dispose();
            workspace.Dispose(); workspace.DrainAsync().GetAwaiter().GetResult();
            Check(Directory.Exists(running.Directory));
            running.Complete(); workspace.DrainAsync().GetAwaiter().GetResult();
            Check(!Directory.Exists(running.Directory) && !Directory.Exists(current.Directory));
            Directory.Delete(root, recursive: true);
        });
        test("Build workspace: stale cleanup skips live leases and foreign directories", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "gamegrl-workspace-test-" + Guid.NewGuid().ToString("N"));
            using var workspace = new BuildWorkspace(root);
            var active = workspace.Create("game"); active.Complete();
            string stale = Path.Combine(root, "session-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stale);
            File.WriteAllText(Path.Combine(stale, ".lease"), "gamegrl editor build workspace 1");
            string foreign = Path.Combine(root, "session-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(foreign);
            File.WriteAllText(Path.Combine(foreign, ".lease"), "other application");
            string unmarked = Path.Combine(root, "unknown"); Directory.CreateDirectory(unmarked);
            BuildWorkspace.CleanStaleSessions(root);
            Check(!Directory.Exists(stale) && Directory.Exists(active.Directory) && Directory.Exists(foreign) && Directory.Exists(unmarked));
            workspace.Dispose(); workspace.DrainAsync().GetAwaiter().GetResult(); Directory.Delete(root, recursive: true);
        });
        test("Build workspace: cancellation after a worker succeeds retires an unclaimed result", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "gamegrl-workspace-test-" + Guid.NewGuid().ToString("N"));
            using var workspace = new BuildWorkspace(root);
            using var jobs = new EditorJobs(); using var ready = new ManualResetEventSlim();
            string? directory = null;
            jobs.Run("cancel result", _ =>
            {
                var result = workspace.Create("unused"); directory = result.Directory; result.Complete(); ready.Set(); return result;
            }, _ => throw new Exception("Cancelled result was applied"));
            Check(ready.Wait(TimeSpan.FromSeconds(5))); jobs.CancelAll();
            Check(SpinWait.SpinUntil(() => { jobs.Pump(); return !jobs.Busy; }, 5000));
            workspace.DrainAsync().GetAwaiter().GetResult(); Check(!Directory.Exists(directory));
            workspace.Dispose(); workspace.DrainAsync().GetAwaiter().GetResult(); Directory.Delete(root, recursive: true);
        });
        test("Build workspace: failed and cancelled compiles remove their partial outputs", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "gamegrl-workspace-test-" + Guid.NewGuid().ToString("N"));
            using var workspace = new BuildWorkspace(root);
            using var stop = new CancellationTokenSource(); stop.Cancel();
            try { GameCode.Build("unused.csproj", stop.Token, workspace); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { }
            try { GameCode.Build(Path.Combine(root, "missing.csproj"), CancellationToken.None, workspace); throw new Exception("Expected failed build"); }
            catch (InvalidOperationException) { }
            workspace.DrainAsync().GetAwaiter().GetResult();
            Check(!Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Any(d => Path.GetFileName(d).Length == 32));
            workspace.Dispose(); workspace.DrainAsync().GetAwaiter().GetResult(); Directory.Delete(root, recursive: true);
        });
    }
    private static void Check(bool condition) { if (!condition) throw new Exception("Build workspace assertion failed"); }
}
