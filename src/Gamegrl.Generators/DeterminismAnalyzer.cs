using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Gamegrl.Generators;

/// <summary>
/// Checks fixed-tick systems (<c>[System(Phase.FixedUpdate)]</c>) for code that breaks simulation determinism
/// (docs/DESIGN.md, "Simulation determinism"):
/// <list type="bullet">
/// <item>GG0201: math whose result depends on the platform (<c>MathF</c>/<c>Math</c> transcendentals, fused
/// multiply-add, estimates, and <c>System.Numerics</c> members that may fuse or reduce in a platform order). An error
/// under <c>CrossPlatform</c>; not reported under <c>Local</c>, where it's still bit-exact on one machine.</item>
/// <item>GG0202: sources that differ between runs (clocks, <c>System.Random</c>, new GUIDs, iterating a hash map). They
/// break replay in either mode, so they're always reported.</item>
/// </list>
/// Only the system method's own body is checked, not the methods it calls.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DeterminismAnalyzer : DiagnosticAnalyzer
{
#pragma warning disable RS2008 // release tracking: the engine ships its analyzers with itself
    private static readonly DiagnosticDescriptor PlatformMath = new("GG0201", "Platform-dependent math in a fixed-tick system",
        "'{0}' can give different bits on different platforms; use SimMath.{1} (required under CrossPlatform determinism)",
        "Determinism", DiagnosticSeverity.Error, isEnabledByDefault: true);
    private static readonly DiagnosticDescriptor Nondeterministic = new("GG0202", "Nondeterministic source in a fixed-tick system",
        "'{0}' differs between runs and breaks replay and rollback; {1}",
        "Determinism", DiagnosticSeverity.Warning, isEnabledByDefault: true);
#pragma warning restore RS2008

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(PlatformMath, Nondeterministic);

    private static readonly ImmutableHashSet<string> Transcendentals = ImmutableHashSet.Create(
        "Sin", "Cos", "Tan", "SinCos", "Asin", "Acos", "Atan", "Atan2", "Sinh", "Cosh", "Tanh", "Asinh", "Acosh", "Atanh",
        "Exp", "Log", "Log2", "Log10", "Pow", "Cbrt", "FusedMultiplyAdd", "ReciprocalEstimate", "ReciprocalSqrtEstimate", "ScaleB", "ILogB");

    // System.Numerics members that may fuse or reduce lanes in a platform-specific order.
    private static readonly ImmutableHashSet<string> VectorMembers = ImmutableHashSet.Create(
        "Dot", "Length", "LengthSquared", "Distance", "DistanceSquared", "Normalize", "Lerp", "Transform", "TransformNormal",
        "Cross", "Reflect", "Slerp", "Concatenate", "CreateFromAxisAngle", "CreateFromYawPitchRoll", "CreateFromRotationMatrix",
        "MultiplyAddEstimate", "FusedMultiplyAdd", "op_Multiply", "Multiply", "Invert", "Decompose");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            start.Options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.GamegrlDeterminism", out var mode);
            bool crossPlatform = mode == "CrossPlatform";
            var systemAttribute = start.Compilation.GetTypeByMetadataName("Gamegrl.SystemAttribute");
            if (systemAttribute is null) return;
            start.RegisterOperationBlockStartAction(block =>
            {
                if (block.OwningSymbol is not IMethodSymbol method || !IsFixedUpdateSystem(method, systemAttribute)) return;
                block.RegisterOperationAction(op => Check(op, crossPlatform), OperationKind.Invocation, OperationKind.PropertyReference,
                    OperationKind.Binary, OperationKind.ObjectCreation, OperationKind.Loop);
            });
        });
    }

    private static bool IsFixedUpdateSystem(IMethodSymbol method, INamedTypeSymbol systemAttribute)
    {
        foreach (var attribute in method.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, systemAttribute)) continue;
            // Phase.FixedUpdate is 1; the attribute's default phase is Update.
            return attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is int phase && phase == 1;
        }
        return false;
    }

    private static void Check(OperationAnalysisContext context, bool crossPlatform)
    {
        switch (context.Operation)
        {
            case IInvocationOperation invocation:
                Member(context, invocation.TargetMethod, crossPlatform);
                break;
            case IPropertyReferenceOperation property:
                Member(context, property.Property, crossPlatform);
                break;
            case IBinaryOperation { OperatorMethod: { } op }:
                Member(context, op, crossPlatform);
                break;
            case IObjectCreationOperation creation when Is(creation.Type, "System", "Random"):
                Report(context, Nondeterministic, "new Random()", "use a seeded SimRandom stored in a component or resource");
                break;
            case IForEachLoopOperation loop:
                // The collection is usually wrapped in a conversion to the enumerable interface; look through it.
                var type = loop.Collection is IConversionOperation conversion ? conversion.Operand.Type : loop.Collection.Type;
                if (type is INamedTypeSymbol named && named.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic" &&
                    named.Name is "Dictionary" or "HashSet")
                    Report(context, Nondeterministic, "foreach over " + named.Name, "hash order isn't stable across runs; iterate a list or sorted keys");
                break;
        }
    }

    private static void Member(OperationAnalysisContext context, ISymbol member, bool crossPlatform)
    {
        var type = member.ContainingType;
        if (type is null) return;
        string ns = type.ContainingNamespace?.ToDisplayString() ?? "";
        if (ns == "System")
        {
            if ((type.Name is "Math" or "MathF") && Transcendentals.Contains(member.Name))
            {
                if (crossPlatform) Report(context, PlatformMath, type.Name + "." + member.Name, member.Name == "FusedMultiplyAdd" ? "MulAdd" : member.Name);
                return;
            }
            if (type.Name is "DateTime" or "DateTimeOffset" && member.Name is "Now" or "UtcNow" or "Today")
                Report(context, Nondeterministic, type.Name + "." + member.Name, "use Time (the fixed tick) instead");
            else if (type.Name == "Environment" && member.Name is "TickCount" or "TickCount64")
                Report(context, Nondeterministic, "Environment." + member.Name, "use Time (the fixed tick) instead");
            else if (type.Name == "Guid" && member.Name == "NewGuid")
                Report(context, Nondeterministic, "Guid.NewGuid", "derive ids from SimRandom or a counter");
            else if (type.Name == "Random")
                Report(context, Nondeterministic, "Random." + member.Name, "use a seeded SimRandom stored in a component or resource");
            return;
        }
        if (ns == "System.Diagnostics" && type.Name == "Stopwatch")
        {
            Report(context, Nondeterministic, "Stopwatch." + member.Name, "use Time (the fixed tick) instead");
            return;
        }
        if (crossPlatform && ns == "System.Numerics" && type.Name is "Vector2" or "Vector3" or "Vector4" or "Quaternion" or "Matrix3x2" or "Matrix4x4" or "Plane"
            && VectorMembers.Contains(member.Name))
        {
            // Scaling a vector by a scalar is element-wise and exact; only products of quaternions and matrices fuse.
            if (member.Name == "op_Multiply" && type.Name is "Vector2" or "Vector3" or "Vector4") return;
            string name = member.Name == "op_Multiply" ? "Mul" : member.Name;
            Report(context, PlatformMath, type.Name + "." + (member.Name == "op_Multiply" ? "operator *" : member.Name), name);
        }
        if (ns == "System.Runtime.Intrinsics" && crossPlatform && member.Name.Contains("Estimate"))
            Report(context, PlatformMath, type.Name + "." + member.Name, "Sqrt");
    }

    private static bool Is(ITypeSymbol? type, string ns, string name) =>
        type is not null && type.Name == name && type.ContainingNamespace?.ToDisplayString() == ns;

    private static void Report(OperationAnalysisContext context, DiagnosticDescriptor descriptor, string what, string instead) =>
        context.ReportDiagnostic(Diagnostic.Create(descriptor, context.Operation.Syntax.GetLocation(), what, instead));
}
