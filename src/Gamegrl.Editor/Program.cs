using System.Diagnostics;
using System.Globalization;
using Gamegrl;
using Gamegrl.Assets;
using Gamegrl.Editor;
using Gamegrl.Platform;
using Gamegrl.Platform.Sdl3;
using Gamegrl.Rendering;
using Gamegrl.Rhi.Vulkan;

string root = Path.GetFullPath(args.Length > 0 ? args[0] : "samples/Spinners");
string project = Path.GetFullPath(args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : Path.Combine(root, "Game", Path.GetFileName(root) + ".Game.csproj"));
int secondsAt = Array.IndexOf(args, "--seconds");
double seconds = secondsAt >= 0 ? double.Parse(args[secondsAt + 1], CultureInfo.InvariantCulture) : 0;
string font = Environment.GetEnvironmentVariable("GAMEGRL_FONT") ?? "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
if (!File.Exists(project) || !File.Exists(font))
{
    Console.Error.WriteLine("Usage: Gamegrl.Editor [project-root] [game.csproj] [--seconds N]. Set GAMEGRL_FONT to a TrueType font path.");
    return 1;
}
var assets = new EditorAssets();
using var app = EngineBuilder.Create()
    .AddModule(new Sdl3PlatformModule(WindowDesc.Default("gamegrl · editor")))
    .AddModule(new AssetsModule(assets))
    .AddModule<VulkanModule>()
    .AddModule(new RenderingModule())
    .Build();
using var editor = new EditorHost(app, assets, root, project, font);
for (int i = 0; i + 1 < args.Length; i++)
    if (args[i] == "--extension") editor.AddExtension(args[++i]);
var platform = app.World.Resource<PlatformContext>();
var input = app.World.Resource<Input>();
long assetChanged = 0, codeChanged = 0;
using var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
void Changed(object sender, FileSystemEventArgs e)
{
    string path = Path.GetRelativePath(root, e.FullPath).Replace('\\', '/');
    if (path.StartsWith("Assets/", StringComparison.Ordinal) && !path.EndsWith(".saving", StringComparison.Ordinal)) Interlocked.Exchange(ref assetChanged, Stopwatch.GetTimestamp());
    if (path.Split('/').Any(p => p is "bin" or "obj" or "Library")) return;
    if (path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".csproj", StringComparison.Ordinal)) Interlocked.Exchange(ref codeChanged, Stopwatch.GetTimestamp());
}
watcher.Changed += Changed; watcher.Created += Changed; watcher.Deleted += Changed; watcher.Renamed += (s, e) => Changed(s, e);
watcher.EnableRaisingEvents = true;
var elapsed = Stopwatch.StartNew();
bool closePending = false;
while (!editor.Files.ExitRequested && (seconds <= 0 || elapsed.Elapsed.TotalSeconds < seconds))
{
    input.BeginFrame();
    platform.Platform.PumpEvents(input);
    if (platform.Platform.QuitRequested || platform.MainWindow.CloseRequested)
    {
        platform.Platform.CancelQuitRequest();
        platform.MainWindow.CancelCloseRequest();
        editor.StopPlay();
        closePending = true;
    }
    if (closePending && !editor.Files.Busy && editor.Files.Dialog == SceneFileDialog.None)
    {
        closePending = false;
        editor.RequestClose();
    }
    editor.Pump();
    long changed = Interlocked.Read(ref assetChanged);
    if (changed != 0 && Stopwatch.GetElapsedTime(changed).TotalMilliseconds > 350)
    { Interlocked.CompareExchange(ref assetChanged, 0, changed); editor.Import(); }
    changed = Interlocked.Read(ref codeChanged);
    if (changed != 0 && Stopwatch.GetElapsedTime(changed).TotalMilliseconds > 500)
    { Interlocked.CompareExchange(ref codeChanged, 0, changed); editor.BuildGame(); }
    if (platform.MainWindow.Minimized) { Thread.Sleep(16); continue; }
    app.Tick();
    if (app.QuitRequested) { if (editor.Document.Playing) editor.StopPlay(); app.QuitRequested = false; }
}
return 0;
