using System.Globalization;
using System.Numerics;
using Gamegrl.Physics2D;
using static Gamegrl.Tests.Harness;
namespace Gamegrl.Tests;

/// <summary>2D physics behaviour: resting, stacking, bouncing, friction, fast bodies, queries, sync, snapshots, determinism.</summary>
internal static class Physics2DTests
{
    private static (App App, World World) Scene(Action<World> populate)
    {
        var b = EngineBuilder.Create().AddModule(new Physics2DModule());
        b.WorkerThreads = 0;
        populate(b.World);
        var app = b.Build();
        return (app, app.World);
    }

    private static Entity Body(World w, Vector2 position, Collider2D collider, RigidBody2D? body = null, float angle = 0) =>
        w.Spawn(new LocalTransform { Position = new Vector3(position, 0), Rotation = Rot.FromAngle(angle).ToQuaternion(), Scale = Vector3.One },
            new LocalToWorld(), body ?? RigidBody2D.Default, collider);

    private static Entity Ground(World w, float width = 100, float y = 0, float angle = 0, float friction = 0.6f) =>
        Body(w, new Vector2(0, y - 0.5f), Collider2D.Box(new Vector2(width, 1)) with { Friction = friction }, RigidBody2D.Static, angle);

    private static void Run(App app, float seconds) { for (int i = 0; i < (int)(seconds * 60); i++) app.Tick(1 / 60.0); }
    private static Vector2 P(World w, Entity e) { var p = w.Get<LocalTransform>(e).Position; return new Vector2(p.X, p.Y); }

    public static void Register()
    {
        Test("physics2d: a falling ball comes to rest on the ground at its radius", () =>
        {
            Entity ball = default;
            var (app, w) = Scene(w => { Ground(w); ball = Body(w, new Vector2(0, 5), Collider2D.Circle(0.5f)); });
            using (app)
            {
                Run(app, 3);
                Assert.True(MathF.Abs(P(w, ball).Y - 0.5f) < 0.02f, $"ball rests at y={P(w, ball).Y}");
                Assert.True(w.Get<RigidBody2D>(ball).LinearVelocity.Length() < 0.05f, "and stops");
            }
        });

        Test("physics2d: a stack of 10 boxes and a 20-row pyramid stay standing", () =>
        {
            var stack = new List<Entity>(); var pyramid = new List<Entity>();
            var (app, w) = Scene(w =>
            {
                Ground(w, 200);
                for (int i = 0; i < 10; i++) stack.Add(Body(w, new Vector2(-20, 0.5f + i * 1.0f), Collider2D.Box(Vector2.One)));
                for (int row = 0; row < 20; row++)
                    for (int i = 0; i < 20 - row; i++)
                        pyramid.Add(Body(w, new Vector2(10 + (i - (20 - row) / 2f) * 1.05f, 0.5f + row * 1.0f), Collider2D.Box(Vector2.One)));
            });
            using (app)
            {
                Run(app, 6);
                var top = P(w, stack[^1]);
                Assert.True(MathF.Abs(top.X + 20) < 0.05f && MathF.Abs(top.Y - 9.5f) < 0.1f, $"stack top at {top}");
                var apex = P(w, pyramid[^1]);
                Assert.True(apex.Y > 18.8f && MathF.Abs(apex.X - 10f + 0.525f) < 0.3f, $"pyramid apex at {apex}");
                Assert.True(pyramid.All(e => P(w, e).Y > 0.4f), "nothing sank into the ground");
            }
        });

        Test("physics2d: restitution bounces to about e² of the drop height; friction holds or slides on a slope", () =>
        {
            Entity ball = default, sticky = default, slippery = default;
            var (app, w) = Scene(w =>
            {
                Ground(w, 20);
                ball = Body(w, new Vector2(0, 10.5f), Collider2D.Circle(0.5f) with { Restitution = 0.8f });
                // A 30° slope: tan 30° ≈ 0.577, so friction 0.9 (combined √(0.9·0.9)) holds and 0.2 slides.
                float angle = MathF.PI / 6;
                Ground(w, 40, -20, angle, friction: 0.9f);
                // The slope's top surface passes through its center (0, -20.5) plus half its thickness along its up vector.
                var up = new Vector2(-MathF.Sin(angle), MathF.Cos(angle));
                var along = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                var surface = new Vector2(0, -20.5f) + up * 0.5f;
                // The slippery box starts below the sticky one, so it slides away from it instead of into it.
                sticky = Body(w, surface + along * 6 + up * 0.5f, Collider2D.Box(Vector2.One) with { Friction = 0.9f }, angle: angle);
                slippery = Body(w, surface - along * 6 + up * 0.5f, Collider2D.Box(Vector2.One) with { Friction = 0.2f }, angle: angle);
            });
            using (app)
            {
                var stickyStart = P(w, sticky); var slipperyStart = P(w, slippery);
                float peak = 0; bool falling = true, bounced = false;
                for (int i = 0; i < 240; i++)
                {
                    app.Tick(1 / 60.0);
                    float vy = w.Get<RigidBody2D>(ball).LinearVelocity.Y, y = P(w, ball).Y;
                    if (falling && vy > 0) { falling = false; bounced = true; }
                    if (bounced && vy > 0) peak = Math.Max(peak, y);
                    if (bounced && vy < 0 && peak > 0) break;
                }
                float height = peak - 0.5f;
                Assert.True(height > 10 * 0.64f * 0.8f && height < 10 * 0.64f * 1.15f, $"bounced to {height} (expected about 6.4)");
                Run(app, 2);
                Assert.True(Vector2.Distance(P(w, sticky), stickyStart) < 0.1f, $"high friction holds (moved {Vector2.Distance(P(w, sticky), stickyStart)})");
                Assert.True(Vector2.Distance(P(w, slippery), slipperyStart) > 1f, "low friction slides");
            }
        });

        Test("physics2d: a 200 m/s bullet doesn't tunnel through a 0.1 m wall", () =>
        {
            Entity bullet = default;
            var (app, w) = Scene(w =>
            {
                Body(w, new Vector2(10, 0), Collider2D.Box(new Vector2(0.1f, 10)), RigidBody2D.Static);
                bullet = Body(w, new Vector2(0, 0), Collider2D.Circle(0.05f), RigidBody2D.Default with { GravityScale = 0, LinearVelocity = new Vector2(200, 0) });
            });
            using (app)
            {
                Run(app, 0.5f);
                Assert.True(P(w, bullet).X < 10, $"bullet at x={P(w, bullet).X}");
            }
        });

        Test("physics2d: ray and box queries; teleports, velocities and despawns sync with entities", () =>
        {
            Entity ground = default, box = default;
            var (app, w) = Scene(w => { ground = Ground(w); box = Body(w, new Vector2(0, 3), Collider2D.Box(Vector2.One)); });
            using (app)
            {
                app.Tick(1 / 60.0);
                var physics = w.Resource<PhysicsWorld2D>();
                Assert.True(physics.RayCast(new Vector2(5, 10), new Vector2(0, -20), out var hit) && hit.Entity == ground, "ray hits the ground");
                Assert.True(MathF.Abs(hit.Point.Y) < 1e-4f && hit.Normal.Y > 0.999f, $"at its surface ({hit.Point}, {hit.Normal})");
                Span<Entity> found = stackalloc Entity[4];
                Assert.Equal(1, physics.OverlapBox(new Vector2(-0.2f, 2), new Vector2(0.2f, 4), found));
                Assert.True(found[0] == box);
                w.Get<LocalTransform>(box).Position = new Vector3(30, 20, 0); // teleport
                w.Get<RigidBody2D>(box).LinearVelocity = new Vector2(5, 0);
                app.Tick(1 / 60.0);
                Assert.True(P(w, box).X > 30 && P(w, box).Y > 19.9f, $"teleported and moving: {P(w, box)}");
                int before = physics.BodyCount;
                w.Destroy(box);
                app.Tick(1 / 60.0);
                Assert.Equal(before - 1, physics.BodyCount);
            }
        });

        Test("physics2d: a snapshot continues bit for bit; a scene matches its golden per-tick hashes", () =>
        {
            var world = new PhysicsWorld2D();
            world.CreateBody(default, BodyType.Static, Shape.Box(new Vector2(50, 0.5f), Vector2.Zero, Rot.Identity), 1, 0.6f, 0, new Vector2(0, -0.5f), Rot.Identity);
            var r = new SimRandom(5);
            for (int i = 0; i < 60; i++)
                world.CreateBody(default, BodyType.Dynamic, i % 2 == 0 ? Shape.Circle(r.Range(0.2f, 0.6f), Vector2.Zero) : Shape.Box(new Vector2(r.Range(0.2f, 0.6f), r.Range(0.2f, 0.6f)), Vector2.Zero, Rot.Identity),
                    1, 0.6f, 0.1f, new Vector2(r.Range(-5f, 5f), 1 + i * 0.8f), Rot.FromAngle(r.Range(-1f, 1f)));
            for (int i = 0; i < 90; i++) world.Step(1 / 60f);
            var copy = world.Clone();
            for (int i = 0; i < 90; i++) { world.Step(1 / 60f); copy.Step(1 / 60f); }
            for (int i = 0; i < world.BodyCapacity; i++)
                Assert.True(world.Position(i) == copy.Position(i) && world.Rotation(i).C == copy.Rotation(i).C && world.Velocity(i) == copy.Velocity(i), $"body {i} diverged after restore");

            // The golden scene, through the ECS: CrossPlatform hashes must match on every runtime and instruction set.
            var hashes = new TickHashRecorder();
            var (app, w) = Scene(w =>
            {
                Ground(w, 60);
                var rr = new SimRandom(9);
                for (int i = 0; i < 120; i++)
                    Body(w, new Vector2(rr.Range(-8f, 8f), 1 + i * 0.6f), i % 3 == 0 ? Collider2D.Circle(rr.Range(0.2f, 0.5f)) with { Restitution = 0.3f } : Collider2D.Box(new Vector2(rr.Range(0.4f, 1f), rr.Range(0.4f, 1f))), angle: rr.Range(-1f, 1f));
            });
            using (app)
            {
                app.Schedule.Add(hashes);
                Run(app, 5);
            }
            DeterminismTests.CheckGolden("physics2d", string.Join(',', hashes.Hashes.Where((_, i) => i % 10 == 9).Select(h => h.ToString("x16", CultureInfo.InvariantCulture))));
        });
    }
}
