using System.Runtime.CompilerServices;

namespace Gamegrl;

internal sealed record ResourcePolicy(int Slot, Func<World, Action> Capture, Action<World>? Prepare);

/// <summary>A play-mode checkpoint of module resources. Dispose after module stop callbacks to restore edit state.</summary>
public sealed class ResourceSession : IDisposable
{
    private readonly World _world;
    private readonly HashSet<int> _existing;
    private readonly List<Action> _restore = new();
    private bool _disposed;
    internal ResourceSession(World world, IEnumerable<ResourcePolicy> policies)
    {
        _world = world; _existing = world.ResourceSlots().ToHashSet();
        var all = policies.ToArray();
        // Capture all first: an unsupported resource must not partially enter play mode.
        foreach (var policy in all) _restore.Add(policy.Capture(world));
        try { foreach (var policy in all) policy.Prepare?.Invoke(world); }
        catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (int slot in _world.ResourceSlots())
            if (!_existing.Contains(slot)) _world.RemoveResource(slot);
        foreach (var restore in _restore) restore();
        _restore.Clear(); // Do not retain collectible game types after reload.
    }
    internal static Action CaptureValue<T>(World world)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            throw new InvalidOperationException($"Resource {typeof(T).FullName} needs SetResource(value, clone) or SetResourceFactory for editor play mode.");
        T copy = world.Resource<T>();
        return () => world.SetResource(copy);
    }
}
