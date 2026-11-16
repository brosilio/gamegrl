using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace Gamegrl.Editor;

/// <summary>Collectible game code. The editor never references a game assembly at compile time.</summary>
internal sealed class GameCode : IDisposable
{
    private sealed class GameContext(string path) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(path);
        protected override Assembly? Load(AssemblyName name)
        {
            // Share engine identity, including the component registry, with the host.
            var shared = Default.Assemblies.FirstOrDefault(a => a.GetName().Name == name.Name);
            if (shared is not null) return shared;
            string? file = _resolver.ResolveAssemblyToPath(name);
            return file is null ? null : LoadFromAssemblyPath(file);
        }
    }
    private GameContext? _context;
    internal WeakReference? ContextReference { get; private set; }
    private EngineBuilder? _registration;
    private readonly List<ISystem> _wrappers = new();
    private string? _assemblyName;
    private bool _started;
    private sealed class PlaySystem(ISystem inner, SceneDocument document) : ISystem
    {
        public string Name => inner.Name;
        public Phase Phase => inner.Phase;
        public int Order => inner.Order;
        public void Initialize(World world) => inner.Initialize(world);
        public void Run(World world) { if (document.Playing) inner.Run(world); }
    }
    public void Load(string path, App app, SceneDocument document)
    {
        _context = new GameContext(path);
        ContextReference = new WeakReference(_context);
        var assembly = _context.LoadFromAssemblyPath(path);
        _assemblyName = assembly.GetName().Name!;
        RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
        var moduleType = assembly.GetTypes().Single(t => !t.IsAbstract && typeof(IModule).IsAssignableFrom(t));
        var module = (IModule)Activator.CreateInstance(moduleType)!;
        _registration = EngineBuilder.ForWorld(app.World).AddModule(module);
        foreach (var system in _registration.Systems)
        {
            var wrapper = new PlaySystem(system, document);
            _wrappers.Add(wrapper);
            app.Schedule.Add(wrapper);
        }
        Log.Info("Loaded " + _assemblyName);
    }
    public void Start(App app)
    {
        if (_started || _registration is null) return;
        _started = true;
        foreach (var start in _registration.StartActionsList) start(app);
    }
    public void Stop(App app)
    {
        if (!_started || _registration is null) return;
        foreach (var stop in _registration.StopActionsList.Reverse()) stop(app);
        _started = false;
    }
    // The restored live world already contains startup entities after a code swap.
    public void Resume() => _started = true;
    public void Unload(App app)
    {
        Stop(app);
        foreach (var wrapper in _wrappers) app.Schedule.Remove(wrapper);
        _wrappers.Clear();
        if (_registration is not null)
            foreach (int slot in _registration.ResourceSlots) app.World.RemoveResource(slot);
        _registration = null;
        if (_assemblyName is not null) ComponentRegistry.ReleaseAssembly(_assemblyName);
        _assemblyName = null;
        _context?.Unload();
        _context = null;
    }
    public static string Build(string project, CancellationToken stop)
    {
        string output = Path.Combine(Path.GetTempPath(), "gamegrl-editor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "build", project, "-o", output, "-m:1", "-nologo", "-v", "q" }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Could not start dotnet build.");
        using var cancel = stop.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var stdout = process.StandardOutput.ReadToEndAsync(stop);
        var stderr = process.StandardError.ReadToEndAsync(stop);
        process.WaitForExit();
        string log = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
        stop.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new InvalidOperationException(log);
        // The initial editor shell supports the SDK default assembly name.
        string dll = Path.Combine(output, Path.GetFileNameWithoutExtension(project) + ".dll");
        if (!File.Exists(dll)) throw new FileNotFoundException("Game output not found; use the SDK default AssemblyName.", dll);
        return dll;
    }
    public void Dispose() => _context?.Unload();
}
