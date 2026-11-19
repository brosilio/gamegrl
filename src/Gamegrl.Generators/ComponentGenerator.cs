using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Gamegrl.Generators;

/// <summary>
/// For every <c>IComponent</c> struct in the assembly, emits a field visitor (<c>IComponentSerializer</c>) and a
/// module initializer that registers it, so scenes can be saved and loaded without reflection. Public instance
/// fields are serialized unless marked <c>[NoSerialize]</c>; nested structs are walked recursively.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ComponentGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var components = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is StructDeclarationSyntax { BaseList: not null },
                static (ctx, ct) => Analyze(ctx, ct))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!);

        context.RegisterSourceOutput(components, static (spc, m) =>
        {
            foreach (var d in m.Diagnostics) spc.ReportDiagnostic(d.ToDiagnostic());
        });

        // The generated registration classes of referenced assemblies, so registering this assembly registers them too.
        var assembly = context.CompilationProvider.Select(static (c, _) =>
        {
            var referenced = new List<string>();
            foreach (var r in c.References)
            {
                if (c.GetAssemblyOrModuleSymbol(r) is not IAssemblySymbol asm) continue;
                string name = ClassName(asm.Name);
                if (asm.GetTypeByMetadataName("Gamegrl.Generated." + name) is { DeclaredAccessibility: Accessibility.Public })
                    referenced.Add("global::Gamegrl.Generated." + name);
            }
            referenced.Sort(System.StringComparer.Ordinal);
            return (Name: c.AssemblyName ?? "Assembly", Referenced: referenced.ToEquatableArray());
        });
        var all = components.Where(static m => m.Diagnostics.Count == 0).Collect().Combine(assembly);
        context.RegisterSourceOutput(all, static (spc, pair) =>
        {
            var unique = pair.Left.GroupBy(m => m.TypeName).Select(g => g.First()).OrderBy(m => m.TypeName, System.StringComparer.Ordinal).ToImmutableArray();
            spc.AddSource("GamegrlComponents.g.cs", EmitComponents(pair.Right.Name, unique, pair.Right.Referenced));
        });
    }

    private static ComponentModel? Analyze(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) is not INamedTypeSymbol type) return null;
        if (!type.AllInterfaces.Any(i => i.ToDisplayString() == "Gamegrl.IComponent")) return null;
        if (type.IsGenericType) return null;
        for (var t = type; t is not null; t = t.ContainingType)
            if (t.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected) return null;

        var diagnostics = new List<DiagnosticInfo>();
        var location = LocationInfo.From(type.Locations.FirstOrDefault());
        if (!type.IsUnmanagedType)
        {
            diagnostics.Add(new DiagnosticInfo("GG0101", "Component must be unmanaged",
                $"Component '{type.Name}' must be unmanaged (no reference-type fields), so world state stays plain memory.", location));
        }

        var ops = new List<FieldOp>();
        if (diagnostics.Count == 0) Walk(type, "c", ops, diagnostics, 0);

        // A public static Default (field or property of the component's own type) is the authoring default;
        // otherwise new T() runs any parameterless constructor and field initializers.
        bool hasDefault = type.GetMembers("Default").Any(m => m.IsStatic && m.DeclaredAccessibility == Accessibility.Public
            && (m is IFieldSymbol df && SymbolEqualityComparer.Default.Equals(df.Type, type)
                || m is IPropertySymbol { GetMethod: not null } dp && SymbolEqualityComparer.Default.Equals(dp.Type, type)));
        // The bytes that hold data (offset, length pairs), so hashing and snapshot comparison skip padding, whose
        // contents depend on how the compiler happened to write the struct. -1: layout not computable here.
        var ranges = new List<int>();
        int layoutSize = Layout(type, 0, ranges, out int size, out _, 0) ? size : -1;
        return new ComponentModel(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), hasDefault, ops.ToEquatableArray(), diagnostics.ToEquatableArray(),
            layoutSize, Merge(ranges).ToEquatableArray());
    }

    /// <summary>Sequential layout of an unmanaged type: its size and alignment, and the data ranges at <paramref name="at"/>.</summary>
    private static bool Layout(ITypeSymbol type, int at, List<int> ranges, out int size, out int align, int depth)
    {
        size = 0; align = 1;
        if (depth > 16) return false;
        if (type is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlying }) type = underlying;
        int primitive = type.SpecialType switch
        {
            SpecialType.System_Boolean or SpecialType.System_Byte or SpecialType.System_SByte => 1,
            SpecialType.System_Char or SpecialType.System_Int16 or SpecialType.System_UInt16 => 2,
            SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Single => 4,
            SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Double => 8,
            _ => 0,
        };
        if (primitive > 0) { size = align = primitive; ranges.Add(at); ranges.Add(size); return true; }
        // Reference assemblies replace Guid's private fields with placeholders; it's 16 data bytes, aligned to 4.
        if (type.ToDisplayString() == "System.Guid") { size = 16; align = 4; ranges.Add(at); ranges.Add(16); return true; }
        if (type is not INamedTypeSymbol { TypeKind: TypeKind.Struct } named) return false; // pointers, nint: platform-sized
        if (named.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "System.Runtime.InteropServices.StructLayoutAttribute")) return false;
        int offset = 0, maxAlign = 1;
        foreach (var member in named.GetMembers())
        {
            if (member is not IFieldSymbol { IsStatic: false, IsConst: false } field) continue;
            int fieldSize, fieldAlign;
            var fieldRanges = new List<int>();
            if (field.IsFixedSizeBuffer && field.Type is IPointerTypeSymbol pointer)
            {
                if (!Layout(pointer.PointedAtType, 0, fieldRanges, out int element, out fieldAlign, depth + 1)) return false;
                fieldSize = element * field.FixedSize;
                fieldRanges.Clear(); fieldRanges.Add(0); fieldRanges.Add(fieldSize);
            }
            else if (!Layout(field.Type, 0, fieldRanges, out fieldSize, out fieldAlign, depth + 1)) return false;
            offset = (offset + fieldAlign - 1) / fieldAlign * fieldAlign;
            for (int i = 0; i < fieldRanges.Count; i += 2) { ranges.Add(at + offset + fieldRanges[i]); ranges.Add(fieldRanges[i + 1]); }
            offset += fieldSize;
            maxAlign = System.Math.Max(maxAlign, fieldAlign);
        }
        size = offset == 0 ? 1 : (offset + maxAlign - 1) / maxAlign * maxAlign;
        align = maxAlign;
        return true;
    }

    private static List<int> Merge(List<int> ranges)
    {
        var merged = new List<int>();
        for (int i = 0; i < ranges.Count; i += 2)
        {
            int start = ranges[i], length = ranges[i + 1];
            if (merged.Count > 0 && merged[merged.Count - 2] + merged[merged.Count - 1] == start) merged[merged.Count - 1] += length;
            else { merged.Add(start); merged.Add(length); }
        }
        return merged;
    }

    private static readonly HashSet<string> Direct = new()
    {
        "bool", "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "float", "double",
        "System.Numerics.Vector2", "System.Numerics.Vector3", "System.Numerics.Vector4", "System.Numerics.Quaternion",
        "System.Numerics.Matrix4x4", "System.Guid", "Gamegrl.FixedString64", "Gamegrl.Entity",
    };

    private static void Walk(ITypeSymbol type, string path, List<FieldOp> ops, List<DiagnosticInfo> diagnostics, int depth)
    {
        foreach (var member in type.GetMembers())
        {
            if (member is not IFieldSymbol f || f.IsStatic || f.IsConst || f.DeclaredAccessibility != Accessibility.Public || f.IsImplicitlyDeclared) continue;
            if (f.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "Gamegrl.NoSerializeAttribute")) continue;
            var loc = LocationInfo.From(f.Locations.FirstOrDefault());
            if (f.IsReadOnly || f.IsFixedSizeBuffer)
            {
                diagnostics.Add(new DiagnosticInfo("GG0102", "Field can't be serialized",
                    $"Field '{f.Name}' is readonly or a fixed buffer; mark it [NoSerialize].", loc));
                continue;
            }
            string p = path + "." + f.Name;
            var ft = f.Type;
            string display = ft.ToDisplayString();
            ops.Add(new FieldOp(OpKind.Metadata, "", Metadata(f, display, diagnostics, loc), "", new EquatableArray<string>(), new EquatableArray<long>()));
            if (Direct.Contains(display))
            {
                ops.Add(new FieldOp(OpKind.Field, f.Name, p, "", EquatableArrayExtensions.ToEquatableArray<string>(new string[0]), new EquatableArray<long>()));
            }
            else if (ft is INamedTypeSymbol { IsGenericType: true } named && named.ConstructedFrom.ToDisplayString() == "Gamegrl.AssetRef<T>")
            {
                ops.Add(new FieldOp(OpKind.Asset, f.Name, p + ".Guid", "", new EquatableArray<string>(), new EquatableArray<long>()));
            }
            else if (ft.TypeKind == TypeKind.Enum && ft is INamedTypeSymbol en)
            {
                var names = new List<string>();
                var values = new List<long>();
                foreach (var m in en.GetMembers())
                {
                    if (m is IFieldSymbol { HasConstantValue: true } c)
                    {
                        names.Add(c.Name);
                        values.Add(System.Convert.ToInt64(c.ConstantValue));
                    }
                }
                ops.Add(new FieldOp(OpKind.Enum, f.Name, p, ft.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    names.ToEquatableArray(), values.ToEquatableArray()));
            }
            else if (ft.IsValueType && ft.TypeKind == TypeKind.Struct && depth < 8)
            {
                ops.Add(new FieldOp(OpKind.Begin, f.Name, "", "", new EquatableArray<string>(), new EquatableArray<long>()));
                Walk(ft, p, ops, diagnostics, depth + 1);
                ops.Add(new FieldOp(OpKind.End, f.Name, "", "", new EquatableArray<string>(), new EquatableArray<long>()));
            }
            else
            {
                diagnostics.Add(new DiagnosticInfo("GG0103", "Unsupported field type",
                    $"Field '{f.Name}' has type '{display}', which can't be serialized; mark it [NoSerialize].", loc));
            }
        }
    }

    private static string Metadata(IFieldSymbol field, string type, List<DiagnosticInfo> diagnostics, LocationInfo? location)
    {
        string header = "null", tooltip = "null", min = "null", max = "null";
        foreach (var a in field.GetAttributes())
        {
            string? name = a.AttributeClass?.ToDisplayString();
            if (name == "Gamegrl.HeaderAttribute" || name == "Gamegrl.TooltipAttribute")
            {
                string text = Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral((string?)a.ConstructorArguments[0].Value ?? "", true);
                if (name == "Gamegrl.HeaderAttribute") header = text; else tooltip = text;
            }
            else if (name == "Gamegrl.RangeAttribute")
            {
                double lo = System.Convert.ToDouble(a.ConstructorArguments[0].Value);
                double hi = System.Convert.ToDouble(a.ConstructorArguments[1].Value);
                bool numeric = new[] { "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "float", "double" }.Contains(type);
                if (!numeric || double.IsNaN(lo) || double.IsNaN(hi) || double.IsInfinity(lo) || double.IsInfinity(hi) || lo > hi
                    || (numeric && !HasRepresentableValue(type, lo, hi)))
                {
                    diagnostics.Add(new DiagnosticInfo("GG0104", "Invalid Inspector range",
                        $"Field '{field.Name}' requires a finite, ordered range on a numeric scalar, intersecting the field type's representable values.", location));
                    continue;
                }
                min = lo.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "d";
                max = hi.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "d";
            }
        }
        return header == "null" && tooltip == "null" && min == "null" ? "default"
            : $"new global::Gamegrl.FieldMetadata({header}, {tooltip}, {min}, {max})";
    }

    private static bool HasRepresentableValue(string type, double lo, double hi)
    {
        if (type == "double") return true;
        if (type == "float")
        {
            // Check that rounding upward to the first allowed float still lies below the upper bound.
            if (lo > float.MaxValue || hi < -float.MaxValue) return false;
            float candidate = (float)System.Math.Max(lo, -float.MaxValue);
            if (candidate < lo)
            {
                var bytes = System.BitConverter.GetBytes(candidate);
                int bits = System.BitConverter.ToInt32(bytes, 0);
                bits += candidate >= 0 ? 1 : -1;
                candidate = System.BitConverter.ToSingle(System.BitConverter.GetBytes(bits), 0);
            }
            return candidate <= hi;
        }
        var lower = new System.Numerics.BigInteger(System.Math.Ceiling(lo));
        var upper = new System.Numerics.BigInteger(System.Math.Floor(hi));
        System.Numerics.BigInteger min = type switch
        {
            "sbyte" => sbyte.MinValue, "short" => short.MinValue, "int" => int.MinValue, "long" => long.MinValue, _ => 0,
        };
        System.Numerics.BigInteger max = type switch
        {
            "byte" => byte.MaxValue, "sbyte" => sbyte.MaxValue, "short" => short.MaxValue, "ushort" => ushort.MaxValue,
            "int" => int.MaxValue, "uint" => uint.MaxValue, "long" => long.MaxValue, "ulong" => ulong.MaxValue, _ => 0,
        };
        return System.Numerics.BigInteger.Max(lower, min) <= System.Numerics.BigInteger.Min(upper, max);
    }

    private static string ClassName(string assemblyName) => new string(assemblyName.Where(char.IsLetterOrDigit).ToArray()) + "Components";

    private static string EmitComponents(string assemblyName, ImmutableArray<ComponentModel> components, EquatableArray<string> referenced)
    {
        string cls = ClassName(assemblyName);
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable CA2255 // module initializers register this assembly's components at load");
        sb.AppendLine("namespace Gamegrl.Generated;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Registers every component in this assembly, and in the assemblies it references, with its generated");
        sb.AppendLine("/// serializer. Runs when the assembly loads; call <c>EnsureRegistered</c> to force that.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("[global::System.CodeDom.Compiler.GeneratedCode(\"Gamegrl.Generators\", \"1.0\")]");
        sb.AppendLine("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
        sb.Append("public static unsafe class ").AppendLine(cls);
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>Loading this assembly registers its components; calling this guarantees it has loaded.</summary>");
        sb.AppendLine("    public static void EnsureRegistered() { }");
        sb.AppendLine();
        sb.AppendLine("    [global::System.Runtime.CompilerServices.ModuleInitializer]");
        sb.AppendLine("    internal static void Register()");
        sb.AppendLine("    {");
        foreach (var r in referenced) sb.Append("        ").Append(r).AppendLine(".EnsureRegistered();");
        for (int i = 0; i < components.Length; i++)
        {
            var c = components[i];
            sb.Append("        global::Gamegrl.ComponentRegistry.SetSerializer(global::Gamegrl.Component<").Append(c.TypeName)
              .Append(">.Id, new S").Append(i).AppendLine("());");
            // Only when the computed layout matches the runtime's size, and only if there's padding to skip.
            bool full = c.DataRanges.Count == 2 && c.DataRanges[0] == 0 && c.DataRanges[1] == c.LayoutSize;
            if (c.LayoutSize > 0 && !full)
            {
                sb.Append("        if (sizeof(").Append(c.TypeName).Append(") == ").Append(c.LayoutSize).Append(") global::Gamegrl.ComponentRegistry.SetDataRanges(global::Gamegrl.Component<")
                  .Append(c.TypeName).Append(">.Id, new int[] { ").Append(string.Join(", ", c.DataRanges)).AppendLine(" });");
            }
        }
        sb.AppendLine("    }");

        int enumCount = 0;
        for (int i = 0; i < components.Length; i++)
        {
            var m = components[i];
            sb.AppendLine();
            sb.Append("    private sealed class S").Append(i).AppendLine(" : global::Gamegrl.IComponentSerializer");
            sb.AppendLine("    {");
            var enumTables = new StringBuilder();
            sb.Append("        public void Reset(void* p) => global::System.Runtime.CompilerServices.Unsafe.AsRef<").Append(m.TypeName)
              .Append(">(p) = ").Append(m.HasDefault ? m.TypeName + ".Default" : "new " + m.TypeName + "()").AppendLine(";");
            sb.AppendLine("        public void Visit(void* p, global::Gamegrl.IFieldVisitor v)");
            sb.AppendLine("        {");
            sb.Append("            ref var c = ref global::System.Runtime.CompilerServices.Unsafe.AsRef<").Append(m.TypeName).AppendLine(">(p);");
            foreach (var op in m.Ops)
            {
                switch (op.Kind)
                {
                    case OpKind.Metadata:
                        sb.Append("            v.Metadata(").Append(op.Path).AppendLine(");");
                        break;
                    case OpKind.Field:
                        sb.Append("            v.Field(\"").Append(op.Name).Append("\", ref ").Append(op.Path).AppendLine(");");
                        break;
                    case OpKind.Asset:
                        sb.Append("            v.Asset(\"").Append(op.Name).Append("\", ref ").Append(op.Path).AppendLine(");");
                        break;
                    case OpKind.Begin:
                        sb.Append("            v.Begin(\"").Append(op.Name).AppendLine("\");");
                        break;
                    case OpKind.End:
                        sb.AppendLine("            v.End();");
                        break;
                    case OpKind.Enum:
                        int e = enumCount++;
                        enumTables.Append("        private static readonly string[] N").Append(e).Append(" = { ")
                            .Append(string.Join(", ", op.EnumNames.Select(n => "\"" + n + "\""))).AppendLine(" };");
                        enumTables.Append("        private static readonly long[] V").Append(e).Append(" = { ")
                            .Append(string.Join(", ", op.EnumValues.Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture) + "L"))).AppendLine(" };");
                        sb.Append("            { long t = (long)").Append(op.Path).Append("; v.Enum(\"").Append(op.Name)
                          .Append("\", ref t, N").Append(e).Append(", V").Append(e).Append("); ").Append(op.Path).Append(" = (")
                          .Append(op.EnumType).AppendLine(")t; }");
                        break;
                }
            }
            sb.AppendLine("        }");
            sb.Append(enumTables);
            sb.AppendLine("    }");
        }
        sb.AppendLine("}");
        return sb.ToString();
    }
}

internal enum OpKind { Metadata, Field, Asset, Enum, Begin, End }

internal sealed record FieldOp(OpKind Kind, string Name, string Path, string EnumType, EquatableArray<string> EnumNames, EquatableArray<long> EnumValues);

internal sealed record ComponentModel(string TypeName, bool HasDefault, EquatableArray<FieldOp> Ops, EquatableArray<DiagnosticInfo> Diagnostics,
    int LayoutSize, EquatableArray<int> DataRanges);
