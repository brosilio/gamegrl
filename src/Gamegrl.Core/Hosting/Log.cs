using System.Collections.Concurrent;

namespace Gamegrl;

public enum LogLevel : byte { Info, Warning, Error }

public readonly record struct LogEntry(DateTime Time, LogLevel Level, string Message);

/// <summary>
/// The engine log: thread-safe, keeps the most recent entries for the editor's Console, and forwards to
/// listeners (stderr by default). Logging never blocks a frame on I/O beyond the listener's own cost.
/// </summary>
public static class Log
{
    private const int Capacity = 2000;
    private static readonly ConcurrentQueue<LogEntry> Recent = new();
    private static int _count;

    /// <summary>Called for every entry, on the logging thread.</summary>
    public static event Action<LogEntry>? Written;

    /// <summary>Write entries to stderr (on by default; the editor turns it off and shows its Console).</summary>
    public static bool ToStderr { get; set; } = true;

    public static long Version { get; private set; }

    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warning, message);
    public static void Error(string message) => Write(LogLevel.Error, message);

    public static void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        Recent.Enqueue(entry);
        if (Interlocked.Increment(ref _count) > Capacity && Recent.TryDequeue(out _)) Interlocked.Decrement(ref _count);
        Version++;
        if (ToStderr) Console.Error.WriteLine($"[{level.ToString().ToLowerInvariant()}] {message}");
        Written?.Invoke(entry);
    }

    public static LogEntry[] Snapshot() => Recent.ToArray();

    public static void Clear()
    {
        while (Recent.TryDequeue(out _)) Interlocked.Decrement(ref _count);
        Version++;
    }
}
