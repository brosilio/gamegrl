using System.Globalization;
using System.Numerics;
using Gamegrl.UI;

namespace Gamegrl.Editor;

/// <summary>Edits the very same field stream used by scene files and the AOT player.</summary>
internal sealed class InspectorVisitor(CellUi ui, Func<(string Label, Guid Id)[]> assets) : IFieldVisitor
{
    private FieldMetadata _metadata;
    public void Metadata(in FieldMetadata metadata) => _metadata = metadata;
    private void Decoration()
    {
        if (_metadata.Header is { } header) ui.Header(header);
    }
    private Rect Property(string name)
    {
        Decoration();
        var rect = ui.Property(name);
        if (_metadata.Tooltip is { } tooltip && ui.IsHovered(rect with { X = ui.Remaining.X, W = ui.Remaining.W })) ui.Tooltip(tooltip);
        return rect;
    }
    internal static T Constrain<T>(T value, in FieldMetadata metadata) where T : INumber<T>
    {
        if (metadata.Minimum is not null && T.IsNaN(value)) value = T.Zero;
        bool integral = typeof(T) != typeof(float) && typeof(T) != typeof(double);
        if (metadata.Minimum is { } min)
        {
            T lower = T.CreateSaturating(integral ? Math.Ceiling(min) : min);
            if (typeof(T) == typeof(float) && double.CreateSaturating(lower) < min)
                lower = T.CreateSaturating(MathF.BitIncrement(float.CreateSaturating(lower)));
            value = T.Max(value, lower);
        }
        if (metadata.Maximum is { } max)
        {
            T upper = T.CreateSaturating(integral ? Math.Floor(max) : max);
            if (typeof(T) == typeof(float) && double.CreateSaturating(upper) > max)
                upper = T.CreateSaturating(MathF.BitDecrement(float.CreateSaturating(upper)));
            value = T.Min(value, upper);
        }
        return value;
    }
    public void Begin(string name)
    {
        Decoration(); ui.PushId(name);
        var rect = ui.Next();
        ui.Target.Rect(rect, ui.Theme.PanelHeader);
        ui.TextIn(rect, name, ui.Theme.Text);
        if (_metadata.Tooltip is { } tooltip && ui.IsHovered(rect)) ui.Tooltip(tooltip);
        ui.Indent(); _metadata = default;
    }
    public void End() { ui.Unindent(); ui.PopId(); }
    public void Field(string name, ref bool value) => ui.Checkbox(Property(name), name, ref value);
    private void Number<T>(string name, ref T value) where T : struct, INumber<T>
    {
        string text = value.ToString(null, CultureInfo.InvariantCulture);
        if (ui.TextField(Property(name), name, ref text) && T.TryParse(text, CultureInfo.InvariantCulture, out var parsed)) value = Constrain(parsed, _metadata);
    }
    public void Field(string n, ref byte v) => Number(n, ref v);
    public void Field(string n, ref sbyte v) => Number(n, ref v);
    public void Field(string n, ref short v) => Number(n, ref v);
    public void Field(string n, ref ushort v) => Number(n, ref v);
    public void Field(string n, ref int v)
    {
        if (ui.DragInt(Property(n), n, ref v)) v = Constrain(v, _metadata);
    }
    public void Field(string n, ref uint v) => Number(n, ref v);
    public void Field(string n, ref long v) => Number(n, ref v);
    public void Field(string n, ref ulong v) => Number(n, ref v);
    public void Field(string n, ref float v)
    {
        if (ui.DragFloat(Property(n), n, ref v)) v = Constrain(v, _metadata);
    }
    public void Field(string n, ref double v) => Number(n, ref v);
    public void Field(string n, ref Vector2 v)
    {
        Span<float> c = stackalloc float[] { v.X, v.Y };
        if (ui.Vector(Property(n), n, c)) v = new(c[0], c[1]);
    }
    public void Field(string n, ref Vector3 v) => ui.Vector3(Property(n), n, ref v);
    public void Field(string n, ref Vector4 v)
    {
        Span<float> c = stackalloc float[] { v.X, v.Y, v.Z, v.W };
        if (ui.Vector(Property(n), n, c)) v = new(c[0], c[1], c[2], c[3]);
    }
    public void Field(string n, ref Quaternion v)
    {
        var c = new Vector4(v.X, v.Y, v.Z, v.W);
        var before = c;
        Field(n, ref c);
        if (c != before && c.LengthSquared() > 1e-10f) v = Quaternion.Normalize(new(c.X, c.Y, c.Z, c.W));
    }
    public void Field(string n, ref Matrix4x4 v)
    {
        Begin(n);
        for (int i = 0; i < 4; i++)
        {
            var c = new Vector4(v[i, 0], v[i, 1], v[i, 2], v[i, 3]);
            Field(i.ToString(CultureInfo.InvariantCulture), ref c);
            v[i, 0] = c.X; v[i, 1] = c.Y; v[i, 2] = c.Z; v[i, 3] = c.W;
        }
        End();
    }
    public void Field(string n, ref Guid v)
    {
        string s = v.ToString("N");
        if (ui.TextField(Property(n), n, ref s) && Guid.TryParse(s, out var parsed)) v = parsed;
    }
    public void Asset(string n, ref Guid v)
    {
        var items = assets();
        Guid current = v;
        int index = Array.FindIndex(items, item => item.Id == current);
        if (ui.Combo(Property(n), n, ref index, items.Select(item => item.Label).ToArray()) && index >= 0) v = items[index].Id;
    }
    public void Field(string n, ref FixedString64 v)
    {
        string s = v.ToString();
        if (ui.TextField(Property(n), n, ref s)) v = new FixedString64(s);
    }
    public void Field(string n, ref Entity v) => ui.TextIn(Property(n), v.ToString(), ui.Theme.TextDim);
    public void Enum(string n, ref long v, ReadOnlySpan<string> names, ReadOnlySpan<long> values)
    {
        int selected = values.IndexOf(v);
        if (ui.Combo(Property(n), n, ref selected, names.ToArray()) && selected >= 0) v = values[selected];
    }
}
