using System.Diagnostics.CodeAnalysis;

namespace Gamegrl;

/// <summary>
/// The game's simulation determinism mode, chosen in its project with <c>&lt;GamegrlDeterminism&gt;</c>:
/// <c>Local</c> (default; bit-exact on the same build and CPU features, fastest) or <c>CrossPlatform</c> (also
/// bit-identical across platforms and instruction sets). A runtime feature switch: NativeAOT substitutes the constant
/// and trims the unused path; the JIT folds it in optimized code. See docs/DESIGN.md, "Simulation determinism".
/// </summary>
public static class Determinism
{
    public const string Switch = "Gamegrl.Determinism.CrossPlatform";

    [FeatureSwitchDefinition(Switch)]
    public static bool CrossPlatform { get; } = AppContext.TryGetSwitch(Switch, out bool on) && on;

    public static string Mode => CrossPlatform ? "CrossPlatform" : "Local";
}

/// <summary>Declares a game assembly's determinism mode (written by the build from <c>&lt;GamegrlDeterminism&gt;</c>).</summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class GamegrlDeterminismAttribute(string mode) : Attribute
{
    public string Mode { get; } = mode;
    public bool CrossPlatform => Mode == "CrossPlatform";
}
