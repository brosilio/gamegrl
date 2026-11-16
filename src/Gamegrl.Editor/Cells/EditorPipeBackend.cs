using AnsiDotNet;
namespace Gamegrl.Editor;

/// <summary>Input EOF is a half-close: keep stdout writable for the final frame and terminal mode restoration.</summary>
internal sealed class EditorPipeBackend : ITerminalBackend
{
    private readonly StreamBackend _input;
    private readonly Stream _output;
    private readonly Lock _gate = new();
    private bool _disposed;
    public EditorPipeBackend(Stream input, Stream output, TerminalSize size)
    { _input = new StreamBackend(input, Stream.Null, size, ownsStreams: false); _output = output; }
    public TerminalSize Size => _input.Size;
    public void Start(ITerminalInput input) => _input.Start(input);
    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (_disposed) return;
            try { _output.Write(data); _output.Flush(); }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        }
    }
    public void Dispose()
    {
        _input.Dispose();
        lock (_gate) _disposed = true;
    }
}
