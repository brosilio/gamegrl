using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Gamegrl.Generators;

/// <summary>
/// Turns <c>[System]</c> methods into <c>ISystem</c> classes with tight, pointer-based loops over the matching
/// archetypes, and emits one <c>Gamegrl.Generated.&lt;Assembly&gt;Systems.AddTo(Schedule)</c> per assembly.
/// No reflection at runtime: everything a system needs is decided here.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class SystemGenerator : IIncrementalGenerator
{
    private const string SystemAttribute = "Gamegrl.SystemAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var systems = context.SyntaxProvider.ForAttributeWithMetadataName(
            SystemAttribute,
            static (node, _) => node is MethodDeclarationSyntax,
            static (ctx, ct) => Analyze(ctx, ct));

        context.RegisterSourceOutput(systems, static (spc, model) =>
        {
            foreach (var d in model.Diagnostics) spc.ReportDiagnostic(d.ToDiagnostic());
            if (model.Diagnostics.Count == 0) spc.AddSource(model.HintName, Emit.System(model));
        });

        var assemblyName = context.CompilationProvider.Select(static (c, _) => c.AssemblyName ?? "Assembly");
        var all = systems.Where(static m => m.Diagnostics.Count == 0).Collect().Combine(assemblyName);
        context.RegisterSourceOutput(all, static (spc, pair) =>
            spc.AddSource("GamegrlSystems.Registration.g.cs", Emit.Registration(pair.Right, pair.Left)));
    }

    private static SystemModel Analyze(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        var method = (IMethodSymbol)ctx.TargetSymbol;
        var syntax = (MethodDeclarationSyntax)ctx.TargetNode;
        var diagnostics = new List<DiagnosticInfo>();
        var location = LocationInfo.From(syntax.Identifier.GetLocation());

        var attr = ctx.Attributes[0];
        int phase = attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is int p ? p : 2; // Update
        bool parallel = false;
        int order = 0;
        foreach (var named in attr.NamedArguments)
        {
            if (named.Key == "Parallel" && named.Value.Value is bool b) parallel = b;
            if (named.Key == "Order" && named.Value.Value is int o) order = o;
        }

        if (!method.IsStatic) diagnostics.Add(Diag.NotStatic(location, method.Name));
        if (method.IsGenericMethod) diagnostics.Add(Diag.Generic(location, method.Name));

        // The containing types must all be partial so the generated class can nest inside and call private methods.
        var containers = new List<ContainerInfo>();
        for (var t = method.ContainingType; t is not null; t = t.ContainingType)
        {
            bool isPartial = t.DeclaringSyntaxReferences.Any(r =>
                r.GetSyntax(ct) is TypeDeclarationSyntax tds && tds.Modifiers.Any(SyntaxKind.PartialKeyword));
            if (!isPartial) diagnostics.Add(Diag.NotPartial(location, t.Name));
            string kind = t.TypeKind == TypeKind.Struct ? "struct" : t.IsRecord ? "record" : "class";
            containers.Insert(0, new ContainerInfo(t.Name, kind, t.IsStatic));
        }

        var parameters = new List<ParamInfo>();
        foreach (var prm in method.Parameters)
        {
            var type = prm.Type;
            string typeName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            bool isRes = prm.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "Gamegrl.ResAttribute");
            bool byRef = prm.RefKind == RefKind.Ref;
            bool readOnly = prm.RefKind is RefKind.In or RefKind.RefReadOnlyParameter or RefKind.None;
            var ploc = LocationInfo.From(prm.Locations.FirstOrDefault());

            ParamKind kind;
            if (isRes)
            {
                kind = byRef ? ParamKind.ResourceRef : ParamKind.ResourceIn;
                if (byRef && parallel) diagnostics.Add(Diag.ParallelRefResource(ploc, prm.Name));
            }
            else if (typeName == "global::Gamegrl.Entity" && prm.RefKind == RefKind.None)
            {
                kind = ParamKind.Entity;
                if (parallel) diagnostics.Add(Diag.ParallelUnsupported(ploc, prm.Name));
            }
            else if (typeName == "global::Gamegrl.CommandBuffer" && prm.RefKind == RefKind.None)
            {
                kind = ParamKind.Commands;
                if (parallel) diagnostics.Add(Diag.ParallelUnsupported(ploc, prm.Name));
            }
            else if (type.AllInterfaces.Any(i => i.ToDisplayString() == "Gamegrl.IComponent") && type.IsUnmanagedType)
            {
                kind = byRef ? ParamKind.ComponentRef : prm.RefKind == RefKind.None ? ParamKind.ComponentValue : ParamKind.ComponentIn;
            }
            else
            {
                diagnostics.Add(Diag.BadParameter(ploc, prm.Name, type.Name));
                continue;
            }
            _ = readOnly;
            parameters.Add(new ParamInfo(prm.Name, typeName, kind));
        }

        var with = new List<string>();
        var without = new List<string>();
        foreach (var a in method.GetAttributes())
        {
            var cls = a.AttributeClass;
            if (cls is null || !cls.IsGenericType || cls.ContainingNamespace?.ToDisplayString() != "Gamegrl") continue;
            string arg = cls.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (cls.MetadataName == "WithAttribute`1") with.Add(arg);
            else if (cls.MetadataName == "WithoutAttribute`1") without.Add(arg);
        }

        if (!parameters.Any(x => x.IsComponent) && with.Count == 0 && parameters.Any(x => x.Kind is ParamKind.Entity))
        {
            // Entity-only systems would match every entity, which is legal; nothing to report.
        }

        string ns = method.ContainingNamespace.IsGlobalNamespace ? "" : method.ContainingNamespace.ToDisplayString();
        string containerPath = string.Join(".", containers.Select(c => c.Name));
        string fullName = (ns.Length > 0 ? ns + "." : "") + containerPath + "." + method.Name;
        string hint = fullName.Replace('<', '_').Replace('>', '_') + ".System.g.cs";
        var span = syntax.GetLocation().GetLineSpan();

        return new SystemModel(
            ns, containers.ToEquatableArray(), method.Name, fullName, phase, parallel, order,
            parameters.ToEquatableArray(), with.ToEquatableArray(), without.ToEquatableArray(),
            diagnostics.ToEquatableArray(), hint, span.Path ?? "", span.StartLinePosition.Line);
    }
}

internal enum ParamKind { ComponentRef, ComponentIn, ComponentValue, ResourceRef, ResourceIn, Entity, Commands }

internal sealed record ParamInfo(string Name, string Type, ParamKind Kind)
{
    public bool IsComponent => Kind is ParamKind.ComponentRef or ParamKind.ComponentIn or ParamKind.ComponentValue;
}

internal sealed record ContainerInfo(string Name, string Kind, bool IsStatic);

internal sealed record SystemModel(
    string Namespace,
    EquatableArray<ContainerInfo> Containers,
    string MethodName,
    string FullName,
    int Phase,
    bool Parallel,
    int Order,
    EquatableArray<ParamInfo> Parameters,
    EquatableArray<string> With,
    EquatableArray<string> Without,
    EquatableArray<DiagnosticInfo> Diagnostics,
    string HintName,
    string FilePath,
    int Line)
{
    public string ClassName => MethodName + "_System";

    public string QualifiedClassName =>
        "global::" + (Namespace.Length > 0 ? Namespace + "." : "") + string.Join(".", Containers.Select(c => c.Name)) + "." + ClassName;

    public string QualifiedMethod =>
        "global::" + (Namespace.Length > 0 ? Namespace + "." : "") + string.Join(".", Containers.Select(c => c.Name)) + "." + MethodName;
}

internal sealed record LocationInfo(string Path, TextSpan Span, LinePositionSpan LineSpan)
{
    public static LocationInfo? From(Location? location) =>
        location is null || location.SourceTree is null
            ? null
            : new LocationInfo(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);

    public Location ToLocation() => Location.Create(Path, Span, LineSpan);
}

internal sealed record DiagnosticInfo(string Id, string Title, string Message, LocationInfo? Location)
{
    public Diagnostic ToDiagnostic() => Diagnostic.Create(
        new DiagnosticDescriptor(Id, Title, "{0}", "Gamegrl", DiagnosticSeverity.Error, isEnabledByDefault: true),
        Location?.ToLocation() ?? Microsoft.CodeAnalysis.Location.None,
        Message);
}

internal static class Diag
{
    public static DiagnosticInfo NotStatic(LocationInfo? l, string m) =>
        new("GG0001", "System must be static", $"System '{m}' must be a static method.", l);

    public static DiagnosticInfo NotPartial(LocationInfo? l, string t) =>
        new("GG0002", "Containing type must be partial", $"Type '{t}' contains a [System] and must be declared partial.", l);

    public static DiagnosticInfo BadParameter(LocationInfo? l, string p, string t) =>
        new("GG0003", "Unsupported system parameter",
            $"Parameter '{p}' of type '{t}' is not a component (an unmanaged IComponent), a [Res] resource, an Entity or a CommandBuffer.", l);

    public static DiagnosticInfo ParallelUnsupported(LocationInfo? l, string p) =>
        new("GG0004", "Not supported in parallel systems", $"Parameter '{p}' can't be used in a Parallel system.", l);

    public static DiagnosticInfo ParallelRefResource(LocationInfo? l, string p) =>
        new("GG0005", "Writable resource in a parallel system",
            $"Resource '{p}' is passed by ref, but a Parallel system runs on many threads at once; pass it with 'in'.", l);

    public static DiagnosticInfo Generic(LocationInfo? l, string m) =>
        new("GG0006", "System must not be generic", $"System '{m}' must not be generic.", l);
}

internal static class Emit
{
    private const int ParallelBatch = 512;

    public static string System(SystemModel m)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable CS1591");
        if (m.Namespace.Length > 0) sb.Append("namespace ").Append(m.Namespace).AppendLine(";");
        sb.AppendLine();

        int depth = 0;
        foreach (var c in m.Containers)
        {
            Indent(sb, depth).Append(c.IsStatic ? "static " : "").Append("partial ").Append(c.Kind).Append(' ').AppendLine(c.Name);
            Indent(sb, depth).AppendLine("{");
            depth++;
        }

        var comps = m.Parameters.Where(p => p.IsComponent).ToList();
        string I(int extra = 0) => new(' ', (depth + extra) * 4);

        sb.Append(I()).AppendLine("[global::System.CodeDom.Compiler.GeneratedCode(\"Gamegrl.Generators\", \"1.0\")]");
        sb.Append(I()).Append("internal sealed unsafe class ").Append(m.ClassName).AppendLine(" : global::Gamegrl.ISystem");
        sb.Append(I()).AppendLine("{");
        sb.Append(I(1)).AppendLine("private global::Gamegrl.Query _query = null!;");
        sb.Append(I(1)).Append("public string Name => \"").Append(m.FullName).AppendLine("\";");
        sb.Append(I(1)).Append("public global::Gamegrl.Phase Phase => (global::Gamegrl.Phase)").Append(m.Phase).AppendLine(";");
        sb.Append(I(1)).Append("public int Order => ").Append(m.Order).AppendLine(";");
        sb.AppendLine();

        // Initialize: the cached query.
        sb.Append(I(1)).AppendLine("public void Initialize(global::Gamegrl.World world)");
        sb.Append(I(1)).AppendLine("{");
        sb.Append(I(2)).Append("_query = world.Query(new global::Gamegrl.QueryDesc()");
        foreach (var c in comps.Select(c => c.Type).Concat(m.With).Distinct()) sb.Append(".With<").Append(c).Append(">()");
        foreach (var c in m.Without) sb.Append(".Without<").Append(c).Append(">()");
        sb.AppendLine(");");
        sb.Append(I(1)).AppendLine("}");
        sb.AppendLine();

        if (m.Parallel) EmitParallel(sb, m, comps, I);
        else EmitSerial(sb, m, comps, I);

        sb.Append(I()).AppendLine("}");
        while (depth-- > 0) Indent(sb, depth).AppendLine("}");
        return sb.ToString();
    }

    private static string Arg(ParamInfo p, int index, string row) => p.Kind switch
    {
        ParamKind.ComponentRef => $"ref c{index}[{row}]",
        ParamKind.ComponentIn => $"in c{index}[{row}]",
        ParamKind.ComponentValue => $"c{index}[{row}]",
        ParamKind.ResourceRef => $"ref r{index}",
        ParamKind.ResourceIn => $"in r{index}",
        ParamKind.Entity => $"entities[{row}]",
        ParamKind.Commands => "commands",
        _ => "default",
    };

    private static void EmitSerial(StringBuilder sb, SystemModel m, List<ParamInfo> comps, global::System.Func<int, string> I)
    {
        bool usesEntity = m.Parameters.Any(p => p.Kind == ParamKind.Entity);
        sb.Append(I(1)).AppendLine("public void Run(global::Gamegrl.World world)");
        sb.Append(I(1)).AppendLine("{");
        for (int i = 0; i < m.Parameters.Count; i++)
        {
            var p = m.Parameters[i];
            if (p.Kind == ParamKind.ResourceRef) sb.Append(I(2)).Append("ref var r").Append(i).Append(" = ref world.Resource<").Append(p.Type).AppendLine(">();");
            if (p.Kind == ParamKind.ResourceIn) sb.Append(I(2)).Append("ref readonly var r").Append(i).Append(" = ref world.Resource<").Append(p.Type).AppendLine(">();");
            if (p.Kind == ParamKind.Commands) sb.Append(I(2)).AppendLine("var commands = world.Commands;");
        }
        sb.Append(I(2)).AppendLine("world.BeginIteration();");
        sb.Append(I(2)).AppendLine("try");
        sb.Append(I(2)).AppendLine("{");
        sb.Append(I(3)).AppendLine("foreach (var archetype in _query.Archetypes)");
        sb.Append(I(3)).AppendLine("{");
        sb.Append(I(4)).AppendLine("int count = archetype.Count;");
        sb.Append(I(4)).AppendLine("if (count == 0) continue;");
        for (int i = 0; i < m.Parameters.Count; i++)
            if (m.Parameters[i].IsComponent)
                sb.Append(I(4)).Append("var c").Append(i).Append(" = archetype.GetPointer<").Append(m.Parameters[i].Type).AppendLine(">();");
        if (usesEntity) sb.Append(I(4)).AppendLine("var entities = archetype.Entities;");
        sb.Append(I(4)).AppendLine("for (int i = 0; i < count; i++)");
        sb.Append(I(5)).Append(m.QualifiedMethod).Append('(')
            .Append(string.Join(", ", m.Parameters.Select((p, i) => Arg(p, i, "i")))).AppendLine(");");
        sb.Append(I(3)).AppendLine("}");
        sb.Append(I(2)).AppendLine("}");
        sb.Append(I(2)).AppendLine("finally");
        sb.Append(I(2)).AppendLine("{");
        sb.Append(I(3)).AppendLine("world.EndIteration();");
        sb.Append(I(2)).AppendLine("}");
        sb.Append(I(1)).AppendLine("}");
    }

    private static void EmitParallel(StringBuilder sb, SystemModel m, List<ParamInfo> comps, global::System.Func<int, string> I)
    {
        // The job carries raw column pointers and copies of read-only resources: workers never touch the world.
        sb.Append(I(1)).AppendLine("private struct Job : global::Gamegrl.IJobFor");
        sb.Append(I(1)).AppendLine("{");
        for (int i = 0; i < m.Parameters.Count; i++)
        {
            var p = m.Parameters[i];
            if (p.IsComponent) sb.Append(I(2)).Append("public ").Append(p.Type).Append("* c").Append(i).AppendLine(";");
            if (p.Kind == ParamKind.ResourceIn) sb.Append(I(2)).Append("public ").Append(p.Type).Append(" r").Append(i).AppendLine(";");
        }
        sb.Append(I(2)).AppendLine("public void Execute(int start, int end)");
        sb.Append(I(2)).AppendLine("{");
        sb.Append(I(3)).AppendLine("for (int i = start; i < end; i++)");
        sb.Append(I(4)).Append(m.QualifiedMethod).Append('(')
            .Append(string.Join(", ", m.Parameters.Select((p, i) => Arg(p, i, "i")))).AppendLine(");");
        sb.Append(I(2)).AppendLine("}");
        sb.Append(I(1)).AppendLine("}");
        sb.AppendLine();

        sb.Append(I(1)).AppendLine("public void Run(global::Gamegrl.World world)");
        sb.Append(I(1)).AppendLine("{");
        sb.Append(I(2)).AppendLine("var jobs = world.Jobs;");
        sb.Append(I(2)).AppendLine("var job = default(Job);");
        for (int i = 0; i < m.Parameters.Count; i++)
            if (m.Parameters[i].Kind == ParamKind.ResourceIn)
                sb.Append(I(2)).Append("job.r").Append(i).Append(" = world.Resource<").Append(m.Parameters[i].Type).AppendLine(">();");
        sb.Append(I(2)).AppendLine("world.BeginIteration();");
        sb.Append(I(2)).AppendLine("try");
        sb.Append(I(2)).AppendLine("{");
        sb.Append(I(3)).AppendLine("foreach (var archetype in _query.Archetypes)");
        sb.Append(I(3)).AppendLine("{");
        sb.Append(I(4)).AppendLine("int count = archetype.Count;");
        sb.Append(I(4)).AppendLine("if (count == 0) continue;");
        for (int i = 0; i < m.Parameters.Count; i++)
            if (m.Parameters[i].IsComponent)
                sb.Append(I(4)).Append("job.c").Append(i).Append(" = archetype.GetPointer<").Append(m.Parameters[i].Type).AppendLine(">();");
        sb.Append(I(4)).Append("if (jobs is null) job.Execute(0, count); else jobs.ParallelFor(count, ")
            .Append(ParallelBatch).AppendLine(", ref job);");
        sb.Append(I(3)).AppendLine("}");
        sb.Append(I(2)).AppendLine("}");
        sb.Append(I(2)).AppendLine("finally");
        sb.Append(I(2)).AppendLine("{");
        sb.Append(I(3)).AppendLine("world.EndIteration();");
        sb.Append(I(2)).AppendLine("}");
        sb.Append(I(1)).AppendLine("}");
    }

    public static string Registration(string assemblyName, ImmutableArray<SystemModel> systems)
    {
        string cls = new string(assemblyName.Where(char.IsLetterOrDigit).ToArray()) + "Systems";
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("namespace Gamegrl.Generated;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>Every [System] in this assembly, for registering with a schedule.</summary>");
        sb.AppendLine("[global::System.CodeDom.Compiler.GeneratedCode(\"Gamegrl.Generators\", \"1.0\")]");
        sb.Append("internal static class ").AppendLine(cls);
        sb.AppendLine("{");
        sb.AppendLine("    public static void AddTo(global::Gamegrl.Schedule schedule)");
        sb.AppendLine("    {");
        foreach (var s in systems.OrderBy(s => s.FilePath, global::System.StringComparer.Ordinal).ThenBy(s => s.Line))
            sb.Append("        schedule.Add(new ").Append(s.QualifiedClassName).AppendLine("());");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static StringBuilder Indent(StringBuilder sb, int depth) => sb.Append(' ', depth * 4);
}
