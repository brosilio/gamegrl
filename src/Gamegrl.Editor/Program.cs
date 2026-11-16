using AnsiDotNet;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Gamegrl;
using Gamegrl.Assets;
using Gamegrl.Editor;
using Gamegrl.Platform;
using Gamegrl.Platform.Sdl3;
using Gamegrl.Rendering;
using Gamegrl.Rhi.Vulkan;
using Key = Gamegrl.Platform.Key;

string root = Path.GetFullPath(args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "samples/Spinners");
string? Option(string name) { int index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
string project = Path.GetFullPath(args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : Path.Combine(root, "Game", Path.GetFileName(root) + ".Game.csproj"));
int secondsAt = Array.IndexOf(args, "--seconds");
double seconds = secondsAt >= 0 ? double.Parse(args[secondsAt + 1], CultureInfo.InvariantCulture) : 0;
bool terminalMode = args.Contains("--terminal") || args.Contains("--stdio");
string font = Environment.GetEnvironmentVariable("GAMEGRL_FONT") ?? "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf";
if (!File.Exists(project) || !terminalMode && !File.Exists(font))
{
    Console.Error.WriteLine("Usage: Gamegrl.Editor [project-root] [game.csproj] [--terminal|--stdio] [--columns N --rows N] [--colors truecolor|256|16|none] [--ascii] [--seconds N]. GAMEGRL_FONT selects the desktop monospace font.");
    return 1;
}
var assets = new EditorAssets();
Terminal? terminal = null;
TerminalDisplay? display = null;
TerminalPlatform? terminalPlatform = null;
if (terminalMode)
{
    Log.ToStderr = false;
    var options = new TerminalOptions { Mouse = MouseMode.AnyMotion, FocusEvents = true, KittyKeyboard = true, Title = "gamegrl editor" };
    terminal = args.Contains("--stdio")
        ? Terminal.Open(new EditorPipeBackend(Console.OpenStandardInput(), Console.OpenStandardOutput(), new TerminalSize(int.Parse(Option("--columns") ?? "120", CultureInfo.InvariantCulture), int.Parse(Option("--rows") ?? "40", CultureInfo.InvariantCulture))), options)
        : Terminal.OpenConsole(options);
    if (Option("--colors") is { } colors) terminal.ColorMode = colors switch { "none" => ColorMode.None, "16" => ColorMode.Basic16, "256" => ColorMode.Indexed256, "truecolor" => ColorMode.TrueColor, _ => throw new ArgumentException("--colors must be none, 16, 256 or truecolor.") };
    terminal.Output.TrueColorPrecision = 5;
    display = new TerminalDisplay(terminal); terminalPlatform = new TerminalPlatform(terminal);
}
using var session = terminal;
using var presentation = display;
var builder = EngineBuilder.Create();
if (terminalPlatform is not null)
{
    builder.SetResource(new Input());
    builder.SetResource(new PlatformContext(terminalPlatform, terminalPlatform.CreateWindow(WindowDesc.Default("gamegrl editor"))));
}
else builder.AddModule(new Sdl3PlatformModule(WindowDesc.Default(Option("--title") ?? "gamegrl · editor")));
using var app = builder.AddModule(new AssetsModule(assets)).AddModule<VulkanModule>().AddModule(new RenderingModule()).Build();
using var editor = new EditorHost(app, assets, root, project, terminalMode ? null : font, display, args.Contains("--ascii") || terminal?.ColorMode == ColorMode.None);
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
int terminalHangup = 0;
using var hangup = terminalMode && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
    ? PosixSignalRegistration.Create(PosixSignal.SIGHUP, signal => { signal.Cancel = true; Interlocked.Exchange(ref terminalHangup, 1); })
    : null;
var elapsed = Stopwatch.StartNew();
bool closePending = false, recovering = false, disconnected = false;
while (!editor.Files.ExitRequested && (seconds <= 0 || elapsed.Elapsed.TotalSeconds < seconds))
{
    input.BeginFrame();
    platform.Platform.PumpEvents(input);
    if (terminalPlatform?.Disconnected == true || Volatile.Read(ref terminalHangup) != 0)
    {
        if (!recovering) { recovering = true; editor.RecoverOnDisconnect(() => disconnected = true); }
        editor.Pump(); if (disconnected) break; Thread.Sleep(10); continue;
    }
    if (input.Ctrl && input.WasPressed(Key.Q)) closePending = true;
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
    if (terminalMode) Thread.Sleep(33);
    if (app.QuitRequested) { if (editor.Document.Playing) editor.StopPlay(); app.QuitRequested = false; }
}
return 0;
