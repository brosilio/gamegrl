using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Gamegrl.Platform;

namespace Gamegrl.Editor;

/// <summary>Collectible game code. The editor never references a game assembly at compile time.</summary>
internal sealed class GameCode : IDisposable
{
    private sealed class GameContext(string path) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(path);
        private readonly List<AssemblyDependencyResolver> _extensionResolvers = new();
        public Assembly LoadExtension(string extension)
        { _extensionResolvers.Add(new AssemblyDependencyResolver(extension)); return LoadFromAssemblyPath(extension); }
        protected override Assembly? Load(AssemblyName name)
        {
            // Share engine identity, including the component registry, with the host.
            var shared = Default.Assemblies.FirstOrDefault(a => a.GetName().Name == name.Name);
            if (shared is not null) return shared;
            string? file = _resolver.ResolveAssemblyToPath(name) ?? _extensionResolvers.Select(r => r.ResolveAssemblyToPath(name)).FirstOrDefault(p => p is not null);
            return file is null ? null : LoadFromAssemblyPath(file);
        }
    }
    private GameContext? _context;
    internal WeakReference? ContextReference { get; private set; }
    private EngineBuilder? _registration;
    private readonly List<ISystem> _wrappers = new();
    private string? _assemblyName;
    private bool _started;
    private ResourceSession? _resources;
    private Input? _input;
    private readonly List<IDisposable> _extensions = new();
    private readonly struct InputScope : IDisposable
    {
        private readonly World _world;
        private readonly Input? _previous;
        public InputScope(World world, Input? input)
        {
            _world = world; _previous = input is null ? null : world.Resource<Input>();
            if (input is not null) world.SetResource(input);
        }
        public void Dispose() { if (_previous is not null) _world.SetResource(_previous); }
    }
    private sealed class PlaySystem(ISystem inner, SceneDocument document, Input? input) : ISystem
    {
        public string Name => inner.Name;
        public Phase Phase => inner.Phase;
        public int Order => inner.Order;
        public void Initialize(World world) { using var scope = new InputScope(world, input); inner.Initialize(world); }
        public void Run(World world)
        {
            if (!document.Playing) return;
            using var scope = new InputScope(world, input);
            inner.Run(world);
        }
    }
    public void Load(string path, App app, SceneDocument document, Input? input = null, EditorRegistry? registry = null, IReadOnlyList<string>? extensions = null)
    {
        _input = input;
        using var scope = new InputScope(app.World, input);
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
            var wrapper = new PlaySystem(system, document, input);
            _wrappers.Add(wrapper);
            app.Schedule.Add(wrapper);
        }
        if (registry is not null && extensions is not null)
            foreach (string extension in extensions)
            {
                var plugin = _context.LoadExtension(Path.GetFullPath(extension));
                var types = plugin.GetTypes().Where(t => !t.IsAbstract && typeof(IEditorExtension).IsAssignableFrom(t)).ToArray();
                if (types.Length == 0) throw new InvalidOperationException("No IEditorExtension in " + extension);
                foreach (var type in types) _extensions.Add(registry.Register((IEditorExtension)Activator.CreateInstance(type)!));
            }
        Log.Info("Loaded " + _assemblyName);
    }
    public void Start(App app)
    {
        if (_started || _registration is null) return;
        _resources = _registration.BeginResourceSession();
        _started = true;
        using var scope = new InputScope(app.World, _input);
        try { foreach (var start in _registration.StartActionsList) start(app); }
        catch { Stop(app); throw; }
    }
    public void Stop(App app)
    {
        if (!_started || _registration is null) return;
        using var scope = new InputScope(app.World, _input);
        try { foreach (var stop in _registration.StopActionsList.Reverse()) stop(app); }
        finally { _resources?.Dispose(); _resources = null; _started = false; }
    }
    // The restored live world already contains startup entities after a code swap.
    public void Resume()
    {
        if (_registration is null) return;
        _resources = _registration.BeginResourceSession();
        _started = true;
    }
    public void Unload(App app)
    {
        try { Stop(app); } catch (Exception e) { Log.Error("Game stop during unload: " + e.Message); }
        foreach (var extension in _extensions.AsEnumerable().Reverse())
            try { extension.Dispose(); } catch (Exception e) { Log.Error("Extension stop: " + e.Message); }
        _extensions.Clear();
        foreach (var wrapper in _wrappers) app.Schedule.Remove(wrapper);
        _wrappers.Clear();
        if (_registration is not null)
            foreach (int slot in _registration.ResourceSlots) app.World.RemoveResource(slot);
        _registration = null;
        _input = null;
        if (_context is not null)
            foreach (var assembly in _context.Assemblies) ComponentRegistry.ReleaseAssembly(assembly.GetName().Name ?? "");
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
