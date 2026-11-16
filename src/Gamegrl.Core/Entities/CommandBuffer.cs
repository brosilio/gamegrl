using System.Runtime.CompilerServices;

namespace Gamegrl;

/// <summary>
/// Structural changes recorded during iteration and applied later, in order, by <see cref="Playback"/>.
/// Deferred spawns get a real entity id immediately (reserved), so later commands can refer to it.
/// Single-threaded for now. TODO(milestone 4): per-worker buffers merged deterministically for parallel systems.
/// </summary>
public sealed unsafe class CommandBuffer
{
    private enum Op : byte { Spawn, Destroy, Add, Remove }

    private readonly World _world;
    private byte[] _buffer = new byte[4096];
    private int _length;

    internal CommandBuffer(World world) => _world = world;

    public bool IsEmpty => _length == 0;

    public Entity Spawn()
    {
        var e = _world.Reserve();
        Header(Op.Spawn, e, 0, 0);
        return e;
    }

    public void Destroy(Entity e) => Header(Op.Destroy, e, 0, 0);

    public void Add<T>(Entity e, in T value) where T : unmanaged, IComponent
    {
        int size = Unsafe.SizeOf<T>();
        int at = Header(Op.Add, e, Component<T>.Id, size);
        Unsafe.WriteUnaligned(ref _buffer[at], value);
    }

    public void Remove<T>(Entity e) where T : unmanaged, IComponent => Header(Op.Remove, e, Component<T>.Id, 0);

    /// <summary>Apply every recorded command in order, then clear. Commands on dead entities are skipped.</summary>
    public void Playback()
    {
        int p = 0;
        while (p < _length)
        {
            var op = (Op)_buffer[p];
            var e = Unsafe.ReadUnaligned<Entity>(ref _buffer[p + 1]);
            int component = Unsafe.ReadUnaligned<int>(ref _buffer[p + 9]);
            int size = Unsafe.ReadUnaligned<int>(ref _buffer[p + 13]);
            p += HeaderSize;
            if (_world.IsAlive(e))
            {
                switch (op)
                {
                    case Op.Spawn: _world.PlaceReserved(e); break;
                    case Op.Destroy: _world.Destroy(e); break;
                    case Op.Add:
                        fixed (byte* data = &_buffer[p]) _world.AddRaw(e, component, data);
                        break;
                    case Op.Remove: _world.RemoveRaw(e, component); break;
                }
            }
            p += size;
        }
        _length = 0;
    }

    private const int HeaderSize = 1 + 8 + 4 + 4;

    private int Header(Op op, Entity e, int component, int payload)
    {
        int need = _length + HeaderSize + payload;
        if (need > _buffer.Length) Array.Resize(ref _buffer, Math.Max(need, _buffer.Length * 2));
        _buffer[_length] = (byte)op;
        Unsafe.WriteUnaligned(ref _buffer[_length + 1], e);
        Unsafe.WriteUnaligned(ref _buffer[_length + 9], component);
        Unsafe.WriteUnaligned(ref _buffer[_length + 13], payload);
        int at = _length + HeaderSize;
        _length = need;
        return at;
    }
}
