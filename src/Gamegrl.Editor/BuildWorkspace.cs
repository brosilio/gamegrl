namespace Gamegrl.Editor;

/// <summary>Owns only marked per-session build directories. Cleanup and retries run off the frame thread.</summary>
internal sealed class BuildWorkspace : IDisposable
{
    private const string Signature = "gamegrl editor build workspace 1";
    private readonly string _root;
    private readonly string _session;
    private readonly Lock _gate = new();
    private readonly List<BuildOutput> _outputs = new();
    private readonly List<Task> _cleanup = new();
    private FileStream? _lease;
    private bool _disposed, _sessionCleanupQueued;
    public BuildWorkspace(string? root = null)
    {
        _root = root ?? Path.Combine(Path.GetTempPath(), "gamegrl-editor");
        _session = Path.Combine(_root, "session-" + Guid.NewGuid().ToString("N"));
    }
    public BuildOutput Create(string assemblyName)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_lease is null)
            {
                Directory.CreateDirectory(_root);
                CleanStaleSessions(_root);
                Directory.CreateDirectory(_session);
                _lease = new FileStream(Path.Combine(_session, ".lease"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                using var writer = new StreamWriter(_lease, leaveOpen: true); writer.Write(Signature); writer.Flush();
            }
            string directory = Path.Combine(_session, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var output = new BuildOutput(this, directory, Path.Combine(directory, assemblyName + ".dll"));
            _outputs.Add(output); return output;
        }
    }
    internal void Complete(BuildOutput output)
    {
        lock (_gate) { output.Building = false; if (output.Retired) QueueDelete(output.Directory); QueueSessionCleanup(); }
    }
    internal void Retire(BuildOutput output)
    {
        lock (_gate)
        {
            if (output.Retired) return;
            output.Retired = true;
            if (!output.Building) QueueDelete(output.Directory);
        }
    }
    public Task DrainAsync()
    { lock (_gate) return Task.WhenAll(_cleanup.ToArray()); }
    private void QueueDelete(string path) => _cleanup.Add(Task.Run(() => DeleteWithRetries(path)));
    private static async Task DeleteWithRetries(string path)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt == 4) { Log.Warn("Build cleanup deferred until a later session: " + path); return; }
                await Task.Delay(100 * (1 << attempt)).ConfigureAwait(false);
            }
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var output in _outputs)
            { output.Retired = true; if (!output.Building) QueueDelete(output.Directory); }
            QueueSessionCleanup();
        }
    }
    private void QueueSessionCleanup()
    {
        if (!_disposed || _sessionCleanupQueued || _outputs.Any(o => o.Building)) return;
        _sessionCleanupQueued = true;
        // Release the lease on the worker only after all compiler processes have exited.
        var lease = _lease; _lease = null;
        Task pending = Task.WhenAll(_cleanup.ToArray());
        _cleanup.Add(Task.Run(async () =>
        { await pending.ConfigureAwait(false); lease?.Dispose(); await DeleteWithRetries(_session).ConfigureAwait(false); }));
    }
    internal static void CleanStaleSessions(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (string directory in Directory.EnumerateDirectories(root, "session-*"))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory)[8..], "N", out _) || new DirectoryInfo(directory).LinkTarget is not null) continue;
            string lease = Path.Combine(directory, ".lease");
            if (!File.Exists(lease) || new FileInfo(lease).LinkTarget is not null) continue;
            try
            {
                using (var stream = new FileStream(lease, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                using (var reader = new StreamReader(stream))
                    if (reader.ReadToEnd() != Signature) continue;
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Live session or locked DLL: retry at next startup. */ }
        }
    }
}

internal sealed class BuildOutput(BuildWorkspace owner, string directory, string assemblyPath) : IDisposable
{
    public string Directory { get; } = directory;
    public string AssemblyPath { get; } = assemblyPath;
    internal bool Building { get; set; } = true;
    internal bool Retired { get; set; }
    public void Complete() => owner.Complete(this);
    public void Dispose() => owner.Retire(this);
}
