using System.Runtime.CompilerServices;

namespace Gamegrl.Tests;

/// <summary>A dependency-free test runner: tests register themselves, failures throw, the exit code counts them.</summary>
internal static class Harness
{
    private static readonly List<(string Name, Action Body)> Tests = new();

    public static void Test(string name, Action body) => Tests.Add((name, body));

    public static int Run(string? filter)
    {
        int failed = 0, ran = 0;
        foreach (var (name, body) in Tests)
        {
            if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            ran++;
            try
            {
                body();
                Console.WriteLine($"  ok    {name}");
            }
            catch (Exception e)
            {
                failed++;
                Console.WriteLine($"  FAIL  {name}\n        {e.GetType().Name}: {e.Message}");
            }
        }
        Console.WriteLine($"{ran - failed}/{ran} passed");
        return failed;
    }
}

internal static class Assert
{
    public static void True(bool condition, string? message = null, [CallerArgumentExpression(nameof(condition))] string? expr = null)
    {
        if (!condition) throw new Exception(message ?? $"expected true: {expr}");
    }

    public static void Equal<T>(T expected, T actual, [CallerArgumentExpression(nameof(actual))] string? expr = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{expr}: expected {expected}, got {actual}");
    }

    public static void Throws<TException>(Action body, [CallerArgumentExpression(nameof(body))] string? expr = null) where TException : Exception
    {
        try
        {
            body();
        }
        catch (TException)
        {
            return;
        }
        throw new Exception($"expected {typeof(TException).Name} from {expr}");
    }
}
