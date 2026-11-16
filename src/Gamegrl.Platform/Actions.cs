using System.Numerics;

namespace Gamegrl.Platform;

public readonly record struct ActionState(Vector2 Value, bool Pressed, bool Released)
{
    public bool Down => Value != Vector2.Zero;
    public float Axis => Value.X;
}

/// <summary>Named, rebindable keyboard/mouse actions. Reads allocate nothing and have no global state.</summary>
public sealed class ActionMap
{
    private readonly Dictionary<string, List<Binding>> _bindings = new(StringComparer.Ordinal);
    private readonly record struct Binding(Key Key, MouseButton? Mouse, Vector2 Scale, bool Delta);
    public bool Enabled { get; set; } = true;
    public void Clear(string action) => _bindings.Remove(action);
    public void Bind(string action, Key key, float scale = 1) => Add(action, new(key, null, new Vector2(scale, 0), false));
    public void Bind(string action, MouseButton button) => Add(action, new(Key.None, button, Vector2.UnitX, false));
    public void BindAxis(string action, Key negative, Key positive)
    { Bind(action, negative, -1); Bind(action, positive); }
    public void BindVector(string action, Key left, Key right, Key down, Key up)
    {
        Add(action, new(left, null, -Vector2.UnitX, false)); Add(action, new(right, null, Vector2.UnitX, false));
        Add(action, new(down, null, -Vector2.UnitY, false)); Add(action, new(up, null, Vector2.UnitY, false));
    }
    public void BindPointerDelta(string action) => Add(action, new(Key.None, null, Vector2.One, true));
    private void Add(string action, Binding binding)
    {
        if (!_bindings.TryGetValue(action, out var list)) _bindings[action] = list = new();
        list.Add(binding);
    }
    public ActionState Read(string action, Input input)
    {
        if (!Enabled || !_bindings.TryGetValue(action, out var list)) return default;
        Vector2 value = default, previous = default, delta = default;
        bool pressed = false, released = false;
        foreach (var b in list)
        {
            if (b.Delta) { delta += input.MouseDelta; continue; }
            if (b.Mouse is { } mouse)
            { pressed |= input.WasPressed(mouse); released |= input.WasReleased(mouse); if (input.IsDown(mouse)) value += b.Scale; if (input.WasDown(mouse)) previous += b.Scale; }
            else
            { pressed |= input.WasPressed(b.Key); released |= input.WasReleased(b.Key); if (input.IsDown(b.Key)) value += b.Scale; if (input.WasDown(b.Key)) previous += b.Scale; }
        }
        bool down = value != Vector2.Zero, wasDown = previous != Vector2.Zero;
        return new ActionState(Vector2.Clamp(value, new Vector2(-1), Vector2.One) + delta, !wasDown && (down || pressed), !down && (wasDown || pressed && released));
    }
}
