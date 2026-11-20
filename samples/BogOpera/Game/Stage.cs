using System.Numerics;
using Gamegrl;
using Gamegrl.Audio;
using Gamegrl.Platform;
using Gamegrl.UI;

namespace BogOpera;

public readonly record struct Curtain(double Seconds);

public sealed class BogOperaModule : IModule
{
    public void Build(EngineBuilder builder)
    {
        builder.AddModule(new UiModule());
        builder.AddModule<AudioModule>();
        builder.SetResource(new Curtain());
        builder.OnStart(app => app.World.SetResource(new Choir(app.World.Resource<AudioContext>().Engine)));
        Gamegrl.Generated.BogOperaGameSystems.AddTo(builder.Schedule);
    }
}

public static partial class Stage
{
    private static readonly Color32 Ink = Color32.Hex(0x171b38), Paper = Color32.Hex(0xffebc6),
        Pond = Color32.Hex(0x193f50), Ripple = Color32.Hex(0x336678);
    private static readonly Color32[] Costumes = [Color32.Hex(0xa3d889), Color32.Hex(0xffa698), Color32.Hex(0x9bd9de)];
    private static readonly string[] Cast = ["A   Baron Bullfrog", "S   Madame Kazoo", "D   Pip Glassbell"];
    private static readonly string[] Parts = ["Bass", "Kazoo", "Bell"];
    private static readonly Key[] Keys = [Key.A, Key.S, Key.D];

    [System(Phase.Update)]
    private static void Perform([Res] in Ui ui, [Res] in Choir choir, [Res] in Time time,
        [Res] in Curtain curtain, [Res] in AppControl control)
    {
        if (ui.Input.WasPressed(Key.Escape) || (curtain.Seconds > 0 && time.Elapsed >= curtain.Seconds))
            control.QuitRequested = true;

        float w = ui.Screen.W, h = ui.Screen.H, water = h * 0.49f;
        var draw = ui.Draw;
        draw.Rect(ui.Screen, Ink);
        draw.Rect(new Rect(0, water, w, h - water), Pond);
        var moon = new Vector2(w * 0.78f, water * 0.35f);
        float radius = Math.Min(w, h) * 0.07f;
        Disc(draw, moon, radius, radius, Paper);
        Disc(draw, moon + new Vector2(radius * 0.48f, -radius * 0.18f), radius * 0.88f, radius * 0.88f, Ink);
        for (int i = 1; i <= 48; i++)
        {
            float x = w * ((i * 97 % 251) / 251f), y = water * ((i * 71 % 131) / 131f);
            float glow = MathF.Sin((float)time.Elapsed * 1.3f + i);
            draw.Rect(new Rect(x, y, 2 + glow, 2 + glow), Paper.WithAlpha((byte)(140 + 85 * glow)));
            draw.Rect(new Rect(x, water + (water - y) * 0.2f, 8 + 4 * glow, 1), Ripple);
        }

        ui.TextAt(new Vector2(16, 13), "BOG OPERA", Paper);
        ui.TextAt(new Vector2(w - 16, 13), "112 BPM", Paper, new Vector2(1, 0));
        for (int i = 0; i < 3; i++)
        {
            float x = w * (i + 0.5f) / 3;
            float unit = Math.Min(w / 75, h / 76);
            var seat = new Rect(x - 8 * unit, water - 12 * unit, 16 * unit, 14 * unit);
            if (ui.Input.WasPressed(Keys[i]) || ui.Behavior(seat, ui.Id(100 + i), out _, out _))
                choir.Solo(i);
            Frog(draw, x, water, unit, choir.Mouth(i), i);
            ui.TextAt(new Vector2(x, water + 2.8f * unit), Cast[i], Paper, new Vector2(0.5f, 0));
        }

        float margin = Math.Clamp(w * 0.06f, 12, 72);
        float top = h * 0.65f;
        ui.TextAt(new Vector2(margin, top - 22), "Click the score to add or lift a note", Paper);
        float width = Math.Max(1, (w - 2 * margin - 55) / Choir.Steps);
        float height = Math.Clamp(h * 0.05f, 10, 38);
        int current = choir.Column;
        for (int frog = 0; frog < 3; frog++)
        {
            float y = top + frog * height;
            ui.TextAt(new Vector2(margin, y + height / 2), Parts[frog], Costumes[frog], new Vector2(0, 0.5f));
            for (int step = 0; step < Choir.Steps; step++)
            {
                var cell = new Rect(margin + 55 + step * width, y, width, height);
                if (ui.Behavior(cell, ui.Id(frog * Choir.Steps + step), out bool hover, out _))
                    choir.ToggleNote(frog, step);
                var tile = cell.Shrink(2);
                draw.Rect(tile, choir.HasNote(frog, step) ? Costumes[frog] : Ink);
                if (hover || step == current) draw.RectOutline(tile, Paper, 2);
                if (step % 4 == 0 && frog == 0) draw.Rect(new Rect(tile.X, tile.Y - 4, tile.W, 2), Ripple);
            }
        }
        float buttons = top + 3 * height + 10;
        if (ui.Button(new Rect(margin, buttons, 145, 27), choir.Singing ? "Space  Hush" : "Space  Encore") || ui.Input.WasPressed(Key.Space))
            choir.ToggleChoir();
        if (ui.Button(new Rect(margin + 154, buttons, 155, 27), "E  Bridge echo", choir.Echo) || ui.Input.WasPressed(Key.E))
            choir.Echo = !choir.Echo;
        choir.Conduct();
    }

    private static void Frog(DrawList draw, float x, float y, float u, float mouth, int singer)
    {
        var skin = Costumes[singer];
        Disc(draw, new(x, y + 1.5f * u), 8.5f * u, 1.6f * u, Ripple);
        Disc(draw, new(x, y + u), 7.6f * u, 1.5f * u, Color32.Hex(0x5f9c80));
        y -= mouth * u;
        void Oval(float dx, float dy, float rx, float ry, Color32 color) =>
            Disc(draw, new(x + dx * u, y + dy * u), rx * u, ry * u, color);
        Vector2 Point(float dx, float dy) => new(x + dx * u, y + dy * u);
        Oval(0, -2, 5.4f, 3.6f, skin);
        Oval(-4.5f, 0, 2, 1, skin);
        Oval(4.5f, 0, 2, 1, skin);
        for (int side = -1; side <= 1; side += 2)
        {
            Oval(3 * side, -5, 1.9f, 2.2f, skin);
            Oval(3 * side, -5.5f, 1.1f, 1.2f, Paper);
            Oval(2.8f * side, -5.4f, 0.5f, 0.7f, Ink);
        }
        Oval(0, -2.1f, 2.9f, 0.18f + mouth * 1.35f, Ink);
        if (singer == 0) // The baron's crooked stovepipe.
        {
            draw.Rect(new Rect(x - 3 * u, y - 7.6f * u, 5 * u, u), Ink.Lighter(55));
            draw.Rect(new Rect(x - 2 * u, y - 10.6f * u, 3 * u, 3 * u), Ink.Lighter(40));
            draw.Rect(new Rect(x - 2 * u, y - 8.6f * u, 3 * u, u), Paper);
        }
        else if (singer == 1)
        {
            draw.Triangle(Point(-5, -6), Point(-6, -10), Point(-2, -8), Paper);
            draw.Triangle(Point(-2, -8), Point(0, -10), Point(0, -6), Paper);
        }
        else
        {
            draw.Triangle(Point(-2, -6), Point(1, -12), Point(2, -6), Color32.Hex(0xffbf77));
            Oval(1, -12, 0.7f, 0.7f, Paper);
        }
    }

    private static void Disc(DrawList draw, Vector2 center, float rx, float ry, Color32 color)
    {
        var last = center + new Vector2(rx, 0);
        for (int i = 1; i <= 24; i++)
        {
            var (sin, cos) = MathF.SinCos(i * MathF.Tau / 24);
            var next = center + new Vector2(rx * cos, ry * sin);
            draw.Triangle(center, last, next, color);
            last = next;
        }
    }
}
