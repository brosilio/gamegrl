using System.Numerics;
using Gamegrl.Physics2D;

namespace Gamegrl.PhysicsBench;

/// <summary>One of Box2D's benchmark scenes: builds the world, then does its per-step work (spawning, driving bodies, queries).</summary>
internal abstract class Scene
{
    public abstract void Create(PhysicsWorld2D w);
    public virtual float Step(PhysicsWorld2D w, int step) => 0;

    protected static int Body(PhysicsWorld2D w, in BodyDef2D def) => w.CreateBody(default, def);
    protected static int Add(PhysicsWorld2D w, int body, in Shape shape, in ShapeDef2D def) => w.CreateShape(body, default, shape, def);
    protected static int Add(PhysicsWorld2D w, int body, in Shape shape) => w.CreateShape(body, default, shape, ShapeDef2D.Default);
    protected static BodyDef2D Dynamic(Vector2 p) => BodyDef2D.Dynamic(p);
    protected static BodyDef2D Static(Vector2 p) => BodyDef2D.Static(p);
    protected static Shape Box(float hx, float hy) => Shape.Box(hx, hy);
    protected static Shape OffsetBox(float hx, float hy, Vector2 center) => Shape.Box(new Vector2(hx, hy), center, Rot.Identity);
    protected static JointDef2D Revolute(Vector2 anchorA, Vector2 anchorB) => JointDef2D.Revolute(anchorA, anchorB);
}

internal sealed record SceneInfo(string Name, int Steps, Func<Scene> Make);

/// <summary>The scenes of Box2D's benchmark/main.c and shared/benchmarks.c (release sizes), in its order.</summary>
internal static class Scenes
{
    public static readonly SceneInfo[] All =
    [
        new("compounds", 500, () => new Compounds()),
        new("joint_grid", 500, () => new JointGrid()),
        new("junkyard", 800, () => new Junkyard()),
        new("large_pyramid", 500, () => new LargePyramid()),
        new("many_pyramids", 200, () => new ManyPyramids()),
        new("smash", 300, () => new Smash()),
        new("spinner", 500, () => new Spinner()),
        new("tumbler", 750, () => new Tumbler()),
        new("washer", 500, () => new Washer()),
        new("sleep", 300, () => new SleepScene()),
    ];
}

internal sealed class JointGrid : Scene
{
    public override void Create(PhysicsWorld2D w)
    {
        w.Settings.EnableSleep = false;
        const int n = 100;
        var bodies = new int[n * n];
        int index = 0;
        var shapeDef = ShapeDef2D.Default with { Filter = new Filter2D { Category = 2, Mask = ~2UL } };
        var circle = Shape.Circle(0.4f, Vector2.Zero);
        for (int k = 0; k < n; k++)
        {
            for (int i = 0; i < n; i++)
            {
                var type = k >= n / 2 - 3 && k <= n / 2 + 3 && i == 0 ? BodyType.Static : BodyType.Dynamic;
                int body = Body(w, BodyDef2D.Default with { Type = type, Position = new Vector2(k, -i) });
                Add(w, body, circle, shapeDef);
                if (i > 0) w.CreateJoint(default, bodies[index - 1], body, Revolute(new Vector2(0, -0.5f), new Vector2(0, 0.5f)));
                if (k > 0) w.CreateJoint(default, bodies[index - n], body, Revolute(new Vector2(0.5f, 0), new Vector2(-0.5f, 0)));
                bodies[index++] = body;
            }
        }
    }
}

internal sealed class LargePyramid : Scene
{
    public override void Create(PhysicsWorld2D w)
    {
        w.Settings.EnableSleep = false;
        const int baseCount = 100;
        int ground = Body(w, Static(new Vector2(0, -1)));
        Add(w, ground, Box(100, 1));
        const float a = 0.5f, shift = a;
        var box = Box(a, a);
        for (int i = 0; i < baseCount; i++)
        {
            float y = (2 * i + 1) * shift;
            for (int j = i; j < baseCount; j++)
            {
                float x = (i + 1) * shift + 2 * (j - i) * shift - a * baseCount;
                Add(w, Body(w, Dynamic(new Vector2(x, y))), box);
            }
        }
    }
}

internal sealed class ManyPyramids : Scene
{
    public override void Create(PhysicsWorld2D w)
    {
        w.Settings.EnableSleep = false;
        const int baseCount = 10, rowCount = 20, columnCount = 20;
        const float extent = 0.5f;
        int ground = Body(w, Static(Vector2.Zero));
        float groundDeltaY = 2 * extent * (baseCount + 1), groundWidth = 2 * extent * columnCount * (baseCount + 1);
        float groundY = 0;
        for (int i = 0; i < rowCount; i++)
        {
            Add(w, ground, Shape.Segment(new Vector2(-0.5f * groundWidth, groundY), new Vector2(0.5f * groundWidth, groundY)));
            groundY += groundDeltaY;
        }
        float baseWidth = 2 * extent * baseCount, baseY = 0;
        for (int i = 0; i < rowCount; i++)
        {
            for (int j = 0; j < columnCount; j++)
            {
                float centerX = -0.5f * groundWidth + j * (baseWidth + 2 * extent) + 2 * extent;
                SmallPyramid(w, baseCount, extent, centerX, baseY);
            }
            baseY += groundDeltaY;
        }
    }

    private static void SmallPyramid(PhysicsWorld2D w, int baseCount, float extent, float centerX, float baseY)
    {
        var box = Box(extent, extent);
        for (int i = 0; i < baseCount; i++)
        {
            float y = (2 * i + 1) * extent + baseY;
            for (int j = i; j < baseCount; j++)
            {
                float x = (i + 1) * extent + 2 * (j - i) * extent + centerX - 0.5f;
                Add(w, Body(w, Dynamic(new Vector2(x, y))), box);
            }
        }
    }
}

internal sealed class Spinner : Scene
{
    public override void Create(PhysicsWorld2D w)
    {
        int ground = Body(w, BodyDef2D.Default);
        const int pointCount = 360;
        var points = new Vector2[pointCount];
        var q = Rot.FromAngle(-2 * MathF.PI / pointCount);
        var p = new Vector2(40, 0);
        for (int i = 0; i < pointCount; i++)
        {
            points[i] = new Vector2(p.X, p.Y + 32);
            p = q.Rotate(p);
        }
        w.CreateChain(ground, default, points, ChainDef2D.Default with { IsLoop = true, Friction = 0.1f });

        var spinnerPosition = new Vector2(0, 12);
        int spinner = Body(w, Dynamic(spinnerPosition) with { EnableSleep = false });
        Add(w, spinner, Shape.Box(0.4f, 20, 0.2f), ShapeDef2D.Default with { Friction = 0 });
        w.CreateJoint(default, ground, spinner, Revolute(spinnerPosition, Vector2.Zero) with { EnableMotor = true, MotorSpeed = 5, MaxMotorForce = float.MaxValue });

        var capsule = Shape.Capsule(new Vector2(-0.25f, 0), new Vector2(0.25f, 0), 0.25f);
        var circle = Shape.Circle(0.35f, Vector2.Zero);
        var square = Box(0.35f, 0.35f);
        var shapeDef = ShapeDef2D.Default with { Friction = 0.1f, Restitution = 0.1f, Density = 0.25f };
        const int bodyCount = 2 * 3038;
        float x = -23, y = 2;
        for (int i = 0; i < bodyCount; i++)
        {
            int body = Body(w, Dynamic(new Vector2(x, y)));
            Add(w, body, (i % 3) switch { 0 => capsule, 1 => circle, _ => square }, shapeDef);
            x += 0.5f;
            if (x >= 23) { x = -23; y += 0.5f; }
        }
    }
}

internal sealed class Smash : Scene
{
    public override void Create(PhysicsWorld2D w)
    {
        w.Settings.Gravity = Vector2.Zero;
        int hammer = Body(w, Dynamic(new Vector2(-20, 0)) with { LinearVelocity = new Vector2(40, 0) });
        Add(w, hammer, Box(4, 4), ShapeDef2D.Default with { Density = 8 });
        const float d = 0.4f;
        var box = Box(0.5f * d, 0.5f * d);
        const int columns = 120, rows = 80;
        for (int i = 0; i < columns; i++)
            for (int j = 0; j < rows; j++)
                Add(w, Body(w, Dynamic(new Vector2(i * d + 30, (j - rows / 2f) * d)) with { IsAwake = false }), box);
    }
}

internal sealed class Tumbler : Scene
{
    public override void Create(PhysicsWorld2D w)
    {
        int ground = Body(w, BodyDef2D.Default);
        int drum = Body(w, Dynamic(new Vector2(0, 10)));
        var heavy = ShapeDef2D.Default with { Density = 50 };
        Add(w, drum, OffsetBox(0.5f, 10, new Vector2(10, 0)), heavy);
        Add(w, drum, OffsetBox(0.5f, 10, new Vector2(-10, 0)), heavy);
        Add(w, drum, OffsetBox(10, 0.5f, new Vector2(0, 10)), heavy);
        Add(w, drum, OffsetBox(10, 0.5f, new Vector2(0, -10)), heavy);
        w.CreateJoint(default, ground, drum, Revolute(new Vector2(0, 10), Vector2.Zero) with { EnableMotor = true, MotorSpeed = MathF.PI / 180 * 25, MaxMotorForce = 1e8f });
        const int gridCount = 45;
        var box = Box(0.125f, 0.125f);
        float y = -0.2f * gridCount + 10;
        for (int i = 0; i < gridCount; i++)
        {
            float x = -0.2f * gridCount;
            for (int j = 0; j < gridCount; j++)
            {
                Add(w, Body(w, Dynamic(new Vector2(x, y))), box);
                x += 0.4f;
            }
            y += 0.4f;
        }
    }
}

internal sealed class Washer : Scene
{
    public override void Create(PhysicsWorld2D w)
    {
        const float motorSpeed = 25;
        int drum = Body(w, BodyDef2D.Default with
        {
            Type = BodyType.Kinematic, Position = new Vector2(0, 10), AngularVelocity = MathF.PI / 180 * motorSpeed, LinearVelocity = new Vector2(0.001f, -0.002f),
        });
        const float r0 = 14, r1 = 16, r2 = 18, angle = MathF.PI / 18;
        var q = new Rot { C = MathF.Cos(angle), S = MathF.Sin(angle) };
        var qo = new Rot { C = MathF.Cos(0.1f * angle), S = MathF.Sin(0.1f * angle) };
        var u1 = new Vector2(1, 0);
        Span<Vector2> points = stackalloc Vector2[4];
        for (int i = 0; i < 36; i++)
        {
            var u2 = i == 35 ? new Vector2(1, 0) : q.Rotate(u1);
            {
                var a1 = qo.InvRotate(u1); var a2 = qo.Rotate(u2);
                points[0] = r1 * a1; points[1] = r2 * a1; points[2] = r1 * a2; points[3] = r2 * a2;
                Add(w, drum, Shape.ConvexHull(points));
            }
            if (i % 9 == 0)
            {
                points[0] = r0 * u1; points[1] = r1 * u1; points[2] = r0 * u2; points[3] = r1 * u2;
                Add(w, drum, Shape.ConvexHull(points));
            }
            u1 = u2;
        }
        const int gridCount = 90;
        const float a = 0.1f;
        var square = Box(a, a);
        float y = -1.1f * a * gridCount + 10;
        for (int i = 0; i < gridCount; i++)
        {
            float x = -1.1f * a * gridCount;
            for (int j = 0; j < gridCount; j++)
            {
                Add(w, Body(w, Dynamic(new Vector2(x, y))), square);
                x += 2.1f * a;
            }
            y += 2.1f * a;
        }
    }
}

internal sealed class Junkyard : Scene
{
    private int _pusher;

    public override void Create(PhysicsWorld2D w)
    {
        const float gridSize = 1;
        int ground = Body(w, BodyDef2D.Default);
        float x = -80 * gridSize, y = 0;
        for (int i = 0; i < 161; i++) { Add(w, ground, OffsetBox(0.55f * gridSize, 0.5f * gridSize, new Vector2(x, y))); x += gridSize; }
        y = gridSize; x = -80 * gridSize;
        for (int i = 0; i < 50; i++) { Add(w, ground, OffsetBox(0.5f * gridSize, 0.55f * gridSize, new Vector2(x, y))); y += gridSize; }
        y = gridSize; x = 80 * gridSize;
        for (int i = 0; i < 50; i++) { Add(w, ground, OffsetBox(0.5f * gridSize, 0.55f * gridSize, new Vector2(x, y))); y += gridSize; }

        const int columnCount = 200, rowCount = 40;
        const float radius = 0.25f;
        // Five points of a Fibonacci spiral on a circle: an irregular pentagon.
        float phi = MathF.PI * (MathF.Sqrt(5) - 1);
        Span<Vector2> points = stackalloc Vector2[5];
        for (int i = 0; i < 5; i++) { var cs = Rot.FromAngle(phi * i); points[i] = radius * new Vector2(cs.C, cs.S); }
        var rock = Shape.ConvexHull(points);
        float side = -0.1f;
        const float yStart = 15;
        for (int i = 0; i < columnCount; i++)
        {
            float cx = 1.5f * (2 * i - columnCount) * radius;
            for (int j = 0; j < rowCount; j++)
            {
                float cy = 4 * j * radius + yStart;
                Add(w, Body(w, Dynamic(new Vector2(cx + side, cy))), rock);
                side = -side;
            }
        }
        _pusher = Body(w, BodyDef2D.Default with { Type = BodyType.Kinematic });
        Add(w, _pusher, OffsetBox(2, 4, new Vector2(0, 4)));
    }

    public override float Step(PhysicsWorld2D w, int step)
    {
        const float timeStep = 1 / 60f;
        float time = timeStep * step;
        var cs = Rot.FromAngle(0.2f * time);
        w.SetTargetTransform(_pusher, new Vector2(60 * cs.S, 0), Rot.Identity, timeStep);
        return 0;
    }
}

internal sealed class Compounds : Scene
{
    public override void Create(PhysicsWorld2D w)
    {
        const float gridSize = 1;
        int ground = Body(w, BodyDef2D.Default);
        float x = -40 * gridSize, y = 0;
        for (int i = 0; i < 81; i++) { Add(w, ground, OffsetBox(0.55f * gridSize, 0.5f * gridSize, new Vector2(x, y))); x += gridSize; }
        y = gridSize; x = -40 * gridSize;
        for (int i = 0; i < 100; i++) { Add(w, ground, OffsetBox(0.5f * gridSize, 0.55f * gridSize, new Vector2(x, y))); y += gridSize; }
        y = gridSize; x = 40 * gridSize;
        for (int i = 0; i < 100; i++) { Add(w, ground, OffsetBox(0.5f * gridSize, 0.55f * gridSize, new Vector2(x, y))); y += gridSize; }
        Add(w, ground, Shape.Segment(new Vector2(-800, -80), new Vector2(800, -80)));

        const int columnCount = 20, rowCount = 150;
        var left = Shape.ConvexHull([new(-1, 0), new(0.5f, 1), new(0, 2)]);
        var right = Shape.ConvexHull([new(1, 0), new(-0.5f, 1), new(0, 2)]);
        var shapeDef = ShapeDef2D.Default with { Density = 1, Friction = 0.5f };
        const float shift = 2, extray = 0.25f;
        float side = 0.25f;
        float centerx = shift * columnCount / 2 - 1, centery = 1.15f / 2;
        const float yStart = 5;
        for (int i = 0; i < columnCount; i++)
        {
            float cx = i * shift - centerx;
            for (int j = 0; j < rowCount; j++)
            {
                float cy = j * (shift + extray) + centery + yStart;
                int body = Body(w, Dynamic(new Vector2(cx + side, cy)));
                side = -side;
                Add(w, body, left, shapeDef);
                Add(w, body, right, shapeDef);
            }
        }
    }
}

/// <summary>Ten big pyramids that fall asleep; each step one sleeping pyramid gets a filter joint made and destroyed (waking it and forcing an island split).</summary>
internal sealed class SleepScene : Scene
{
    private const int PyramidCount = 10;
    private readonly int[] _a = new int[PyramidCount], _b = new int[PyramidCount];

    public override void Create(PhysicsWorld2D w)
    {
        const int baseCount = 60;
        const float extent = 0.5f;
        float baseWidth = 2 * extent * baseCount, pitch = baseWidth + 8 * extent, span = pitch * (PyramidCount - 1);
        int ground = Body(w, Static(new Vector2(0, -1)));
        Add(w, ground, Box(0.5f * span + baseWidth, 1));
        var box = Box(extent, extent);
        for (int p = 0; p < PyramidCount; p++)
        {
            float centerX = -0.5f * span + p * pitch - 0.5f * baseWidth;
            for (int i = 0; i < baseCount; i++)
            {
                float y = (2 * i + 1) * extent;
                for (int j = i; j < baseCount; j++)
                {
                    float x = (i + 1) * extent + 2 * (j - i) * extent + centerX;
                    int body = Body(w, Dynamic(new Vector2(x, y)) with { SleepThreshold = 1 });
                    Add(w, body, box);
                    if (i == 0 && j == 0) _a[p] = body;
                    else if (i == 1 && j == 1) _b[p] = body;
                }
            }
        }
    }

    public override float Step(PhysicsWorld2D w, int step)
    {
        for (int p = 0; p < PyramidCount; p++)
        {
            if (w.IsAwake(_a[p])) continue;
            int joint = w.CreateJoint(default, _a[p], _b[p], JointDef2D.Default(JointType2D.Filter));
            w.DestroyJoint(joint);
            break;
        }
        return 0;
    }
}
