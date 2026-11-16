using System.Numerics;
using Gamegrl.Platform;
using Gamegrl.Rendering;
using Gamegrl.UI;

namespace Gamegrl.Editor;

public sealed class SceneCamera
{
    public Vector3 Target = new(0, 2, 0);
    public float Distance = 18, Yaw = 0.65f, Pitch = 0.35f;
    public Vector3 Position => Target + new Vector3(MathF.Sin(Yaw) * MathF.Cos(Pitch), MathF.Sin(Pitch), MathF.Cos(Yaw) * MathF.Cos(Pitch)) * Distance;
    public Matrix4x4 Matrix(float aspect) => Matrix4x4.CreateLookAt(Position, Target, Vector3.UnitY) * Camera.Perspective().Projection(aspect);
    public void Update(Input input, Rect area)
    {
        if (!area.Contains(input.MousePosition)) return;
        if (input.IsDown(MouseButton.Right))
        {
            Yaw -= input.MouseDelta.X * 0.006f;
            Pitch = Math.Clamp(Pitch + input.MouseDelta.Y * 0.006f, -1.5f, 1.5f);
        }
        if (input.IsDown(MouseButton.Middle))
        {
            var right = Vector3.Normalize(Vector3.Cross(Target - Position, Vector3.UnitY));
            var up = Vector3.Normalize(Vector3.Cross(right, Target - Position));
            Target += (-right * input.MouseDelta.X + up * input.MouseDelta.Y) * Distance * 0.0015f;
        }
        Distance = Math.Clamp(Distance * MathF.Exp(-input.Wheel.Y * 0.12f), 0.2f, 10000);
    }
    public static bool Project(Vector3 p, Matrix4x4 matrix, Rect area, out Vector2 screen)
    {
        var clip = Vector4.Transform(new Vector4(p, 1), matrix);
        screen = default;
        if (clip.W <= 0) return false;
        screen = new Vector2(area.X + (clip.X / clip.W + 1) * area.W * 0.5f, area.Y + (1 - clip.Y / clip.W) * area.H * 0.5f);
        return true;
    }
    public static (Vector3 Origin, Vector3 Direction) Ray(Vector2 mouse, Matrix4x4 matrix, Rect area)
    {
        Matrix4x4.Invert(matrix, out var inverse);
        var near = Vector4.Transform(new Vector4((mouse.X - area.X) / area.W * 2 - 1, 1 - (mouse.Y - area.Y) / area.H * 2, 1, 1), inverse);
        var far = Vector4.Transform(new Vector4((mouse.X - area.X) / area.W * 2 - 1, 1 - (mouse.Y - area.Y) / area.H * 2, 0.001f, 1), inverse);
        var a = new Vector3(near.X, near.Y, near.Z) / near.W;
        var b = new Vector3(far.X, far.Y, far.Z) / far.W;
        return (a, Vector3.Normalize(b - a));
    }
    public static bool HitBox(Vector3 origin, Vector3 direction, Vector3 min, Vector3 max, out float distance)
    {
        float lo = 0, hi = float.PositiveInfinity;
        for (int i = 0; i < 3; i++)
        {
            if (MathF.Abs(direction[i]) < 1e-8f)
            {
                if (origin[i] < min[i] || origin[i] > max[i]) { distance = 0; return false; }
                continue;
            }
            float a = (min[i] - origin[i]) / direction[i], b = (max[i] - origin[i]) / direction[i];
            lo = MathF.Max(lo, MathF.Min(a, b)); hi = MathF.Min(hi, MathF.Max(a, b));
        }
        distance = lo;
        return hi >= lo;
    }
}
