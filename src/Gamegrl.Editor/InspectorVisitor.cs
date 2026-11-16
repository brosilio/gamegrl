using System.Globalization;
using System.Numerics;
using Gamegrl.UI;

namespace Gamegrl.Editor;

/// <summary>Edits the very same field stream used by scene files and the AOT player.</summary>
internal sealed class InspectorVisitor(Ui ui, Func<(string Label, Guid Id)[]> assets) : IFieldVisitor
{
    public void Begin(string name) { ui.PushId(name); ui.Header(name); ui.Indent(); }
    public void End() { ui.Unindent(); ui.PopId(); }
    public void Field(string name, ref bool value) => ui.Checkbox(ui.Property(name), name, ref value);
    private void Number<T>(string name, ref T value) where T : struct, ISpanParsable<T>, IFormattable
    {
        string text = value.ToString(null, CultureInfo.InvariantCulture);
        if (ui.TextField(ui.Property(name), name, ref text) && T.TryParse(text, CultureInfo.InvariantCulture, out var parsed)) value = parsed;
    }
    public void Field(string n, ref byte v) => Number(n, ref v);
    public void Field(string n, ref sbyte v) => Number(n, ref v);
    public void Field(string n, ref short v) => Number(n, ref v);
    public void Field(string n, ref ushort v) => Number(n, ref v);
    public void Field(string n, ref int v) => ui.DragInt(ui.Property(n), n, ref v);
    public void Field(string n, ref uint v) => Number(n, ref v);
    public void Field(string n, ref long v) => Number(n, ref v);
    public void Field(string n, ref ulong v) => Number(n, ref v);
    public void Field(string n, ref float v) => ui.DragFloat(ui.Property(n), n, ref v);
    public void Field(string n, ref double v) => Number(n, ref v);
    public void Field(string n, ref Vector2 v)
    {
        Span<float> c = stackalloc float[] { v.X, v.Y };
        if (ui.Vector(ui.Property(n), n, c)) v = new(c[0], c[1]);
    }
    public void Field(string n, ref Vector3 v) => ui.Vector3(ui.Property(n), n, ref v);
    public void Field(string n, ref Vector4 v)
    {
        Span<float> c = stackalloc float[] { v.X, v.Y, v.Z, v.W };
        if (ui.Vector(ui.Property(n), n, c)) v = new(c[0], c[1], c[2], c[3]);
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
        if (ui.TextField(ui.Property(n), n, ref s) && Guid.TryParse(s, out var parsed)) v = parsed;
    }
    public void Asset(string n, ref Guid v)
    {
        var items = assets();
        Guid current = v;
        int index = Array.FindIndex(items, item => item.Id == current);
        if (ui.Combo(ui.Property(n), n, ref index, items.Select(item => item.Label).ToArray()) && index >= 0) v = items[index].Id;
    }
    public void Field(string n, ref FixedString64 v)
    {
        string s = v.ToString();
        if (ui.TextField(ui.Property(n), n, ref s)) v = new FixedString64(s);
    }
    public void Field(string n, ref Entity v) => ui.TextIn(ui.Property(n), v.ToString(), ui.Theme.TextDim);
    public void Enum(string n, ref long v, ReadOnlySpan<string> names, ReadOnlySpan<long> values)
    {
        int selected = values.IndexOf(v);
        if (ui.Combo(ui.Property(n), n, ref selected, names.ToArray()) && selected >= 0) v = values[selected];
    }
}
