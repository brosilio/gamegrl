using System.Numerics;
using Gamegrl;

namespace Fixture;

public struct Body : IComponent { public Vector3 Position, Velocity; public Quaternion Rotation; }

public static partial class Violations
{
    [System(Phase.FixedUpdate)]
    private static void PlatformMath(ref Body b, [Res] in Time time)
    {
        b.Position.X = MathF.Sin(b.Position.Y);                       // GG0201
        b.Velocity = Vector3.Normalize(b.Velocity);                   // GG0201
        b.Rotation = b.Rotation * Quaternion.Identity;                // GG0201
        b.Position.Z = MathF.FusedMultiplyAdd(1, 2, 3);               // GG0201
        b.Position *= 2;                                              // fine: element-wise
    }

    [System(Phase.FixedUpdate)]
    private static void Sources(ref Body b, [Res] in Time time)
    {
        b.Position.X = DateTime.Now.Millisecond;                      // GG0202
        b.Position.Y = new Random().Next();                           // GG0202
        var seen = new System.Collections.Generic.HashSet<int> { 1 };
        foreach (int i in seen) b.Position.Z += i;                    // GG0202
    }

    [System(Phase.Update)]
    private static void NotFixed(ref Body b) => b.Position.X = MathF.Sin(1); // fine: not the simulation
}
