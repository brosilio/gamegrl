using System.Numerics;
using Gamegrl.Platform;
using static Gamegrl.Tests.Harness;
namespace Gamegrl.Tests;
internal static class InputTests
{
    public static void Register()
    {
        Test("input: action rebinding, aggregate edges, axes and same-frame taps", () =>
        {
            var input = new Input(); var actions = new ActionMap();
            actions.Bind("Jump", Key.Space); actions.Bind("Jump", MouseButton.Left);
            actions.BindAxis("Move", Key.A, Key.D); actions.BindPointerDelta("Look");
            input.OnKey((int)Key.Space, true, false);
            Assert.True(actions.Read("Jump", input).Pressed);
            input.BeginFrame(); input.OnMouseButton(0, true); input.OnKey((int)Key.Space, false, false);
            Assert.True(actions.Read("Jump", input).Down && !actions.Read("Jump", input).Released && !actions.Read("Jump", input).Pressed);
            input.BeginFrame(); input.OnMouseButton(0, false);
            Assert.True(actions.Read("Jump", input).Released);
            input.OnKey((int)Key.D, true, false); Assert.Equal(1f, actions.Read("Move", input).Axis);
            input.OnMouseMove(default, new Vector2(30, -12)); Assert.Equal(new Vector2(30, -12), actions.Read("Look", input).Value);
            actions.Clear("Jump"); actions.Bind("Jump", Key.Enter);
            input.BeginFrame(); input.OnKey((int)Key.Enter, true, false); input.OnKey((int)Key.Enter, false, false);
            Assert.True(actions.Read("Jump", input).Pressed && actions.Read("Jump", input).Released);
            actions.Enabled = false; Assert.Equal(default, actions.Read("Move", input));
        });
        Test("input: view routing releases held buttons, reserves shortcuts and translates coordinates", () =>
        {
            var raw = new Input(); var view = new Input();
            raw.OnKey((int)Key.Space, true, false); raw.OnKey((int)Key.F5, true, false);
            raw.OnMouseButton(0, true); raw.OnMouseMove(new Vector2(220, 120), new Vector2(10, 3));
            view.RouteFrom(raw, true, new Vector2(200, 100), [Key.F5]);
            Assert.Equal(new Vector2(20, 20), view.MousePosition); Assert.True(view.WasPressed(Key.Space) && !view.IsDown(Key.F5));
            view.RouteFrom(raw, false, default);
            Assert.True(view.WasReleased(Key.Space) && view.WasReleased(MouseButton.Left));
            view.RouteFrom(raw, false, default); Assert.True(!view.WasReleased(Key.Space) && !view.IsDown(Key.Space));
            Assert.True(raw.IsDown(Key.Space));
        });
    }
}
