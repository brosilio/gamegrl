namespace Gamegrl.Rhi;

/// <summary>
/// A persistently mapped buffer split into one region per frame in flight. Per-frame data (constants, instance
/// arrays, UI vertices) is bump-allocated into the current frame's region; a region is reused only after the GPU
/// has finished that frame, so there are no per-frame allocations and no stalls.
/// </summary>
public sealed unsafe class UploadRing : IDisposable
{
    private readonly long _regionSize;
    private long _regionStart, _cursor;

    public UploadRing(GpuDevice device, long bytesPerFrame, string name = "upload ring")
    {
        _regionSize = bytesPerFrame;
        Buffer = device.CreateBuffer(new BufferDesc(bytesPerFrame * GpuDevice.FramesInFlight,
            BufferUsage.Vertex | BufferUsage.Index | BufferUsage.Uniform | BufferUsage.Storage, MemoryKind.Upload, name));
    }

    public GpuBuffer Buffer { get; }

    public void BeginFrame(int frameIndex)
    {
        _regionStart = frameIndex * _regionSize;
        _cursor = _regionStart;
    }

    /// <summary>Reserve <paramref name="bytes"/> aligned to <paramref name="alignment"/>; returns the buffer offset.</summary>
    public long Allocate(long bytes, int alignment = 16)
    {
        long offset = (_cursor + alignment - 1) / alignment * alignment;
        if (offset + bytes > _regionStart + _regionSize)
            throw new InvalidOperationException($"Upload ring overflow: {bytes} bytes requested, region is {_regionSize}.");
        _cursor = offset + bytes;
        return offset;
    }

    /// <summary>Copy <paramref name="data"/> in; returns its buffer offset.</summary>
    public long Push<T>(ReadOnlySpan<T> data, int alignment = 16) where T : unmanaged
    {
        long bytes = (long)data.Length * sizeof(T);
        long offset = Allocate(bytes, alignment);
        fixed (T* src = data) System.Buffer.MemoryCopy(src, Buffer.Mapped + offset, bytes, bytes);
        return offset;
    }

    public long Push<T>(in T value, int alignment = 16) where T : unmanaged
    {
        long offset = Allocate(sizeof(T), alignment);
        *(T*)(Buffer.Mapped + offset) = value;
        return offset;
    }

    public void Dispose() => Buffer.Dispose();
}
