using System.Globalization;
using System.Numerics;
using Gamegrl.Physics2D;
using static Gamegrl.Tests.Harness;
namespace Gamegrl.Tests;

/// <summary>2D physics behaviour: resting, stacking, bouncing, friction, fast bodies, queries, sync, snapshots, determinism.</summary>
internal static class Physics2DTests
{
    private static (App App, World World) Scene(Action<World> populate, int workers = 0)
    {
        var b = EngineBuilder.Create().AddModule(new Physics2DModule());
        b.WorkerThreads = workers;
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

        Test("physics2d: a resting pyramid falls asleep; a dropped ball wakes what it hits and the pile still stands", () =>
        {
            var pile = new List<Entity>();
            Entity ball = default;
            var (app, w) = Scene(w =>
            {
                Ground(w, 100);
                for (int row = 0; row < 8; row++)
                    for (int i = 0; i < 8 - row; i++) pile.Add(Body(w, new Vector2((i - (8 - row) / 2f) * 1.05f, 0.5f + row), Collider2D.Box(Vector2.One)));
            });
            using (app)
            {
                var physics = w.Resource<PhysicsWorld2D>();
                Run(app, 4);
                Assert.Equal(0, physics.AwakeBodyCount);
                var apex = P(w, pile[^1]);
                ball = w.Spawn(new LocalTransform { Position = new Vector3(apex.X, 12, 0), Rotation = Quaternion.Identity, Scale = Vector3.One }, new LocalToWorld(), RigidBody2D.Default, Collider2D.Circle(0.3f));
                bool woke = false;
                for (int i = 0; i < 120; i++) { app.Tick(1 / 60.0); woke |= physics.AwakeBodyCount > 5; }
                Assert.True(woke, "the impact woke the pile");
                Assert.True(pile.All(e => P(w, e).Y > 0.4f) && P(w, pile[^1]).Y > 6, $"the pile still stands (apex at {P(w, pile[^1])})");
            }
        });

        Test("physics2d: contact and sensor events, and collision filtering", () =>
        {
            Entity ground = default, ball = default, zone = default, ghost = default, blocker = default;
            var (app, w) = Scene(w =>
            {
                ground = Ground(w, 40);
                ball = Body(w, new Vector2(0, 6), Collider2D.Circle(0.5f));
                zone = w.Spawn(new LocalTransform { Position = new Vector3(0, 3, 0), Rotation = Quaternion.Identity, Scale = Vector3.One }, Collider2D.Box(new Vector2(2, 1)) with { IsSensor = true });
                blocker = Body(w, new Vector2(10, 0.5f), Collider2D.Box(Vector2.One) with { Category = 2, Mask = ~4u });
                ghost = Body(w, new Vector2(10, 4), Collider2D.Box(Vector2.One) with { Category = 4, Mask = uint.MaxValue });
            });
            using (app)
            {
                var physics = w.Resource<PhysicsWorld2D>();
                int sensorBegins = 0, sensorEnds = 0, groundHits = 0;
                float speed = 0;
                for (int i = 0; i < 150; i++)
                {
                    app.Tick(1 / 60.0);
                    foreach (var e in physics.SensorBegins) if (e.A == zone && e.B == ball) sensorBegins++;
                    foreach (var e in physics.SensorEnds) if (e.A == zone && e.B == ball) sensorEnds++;
                    foreach (var e in physics.ContactBegins) if ((e.A == ball && e.B == ground) || (e.A == ground && e.B == ball)) { groundHits++; speed = e.Speed; }
                }
                Assert.True(sensorBegins == 1 && sensorEnds == 1, $"the ball entered and left the sensor once ({sensorBegins}, {sensorEnds})");
                Assert.True(groundHits >= 1 && speed > 5, $"it hit the ground at speed {speed}");
                Assert.True(MathF.Abs(P(w, ball).Y - 0.5f) < 0.05f, "the sensor didn't stop it");
                Assert.True(P(w, ghost).Y < 0.6f && P(w, ghost).Y > 0.4f, $"the filtered box fell through the blocker to the ground ({P(w, ghost).Y})");
            }
        });

        Test("physics2d: compound bodies, centers of mass, implicit static colliders and capsules at rest", () =>
        {
            Entity hammer = default, capsule = default, upright = default;
            var (app, w) = Scene(w =>
            {
                // Ground with no RigidBody2D at all: an implicit static body.
                w.Spawn(new LocalTransform { Position = new Vector3(0, -0.5f, 0), Rotation = Quaternion.Identity, Scale = Vector3.One }, Collider2D.Box(new Vector2(60, 1)));
                // A hammer: a light handle and a heavy head as child colliders of one body.
                hammer = w.Spawn(new LocalTransform { Position = new Vector3(0, 3, 0), Rotation = Quaternion.Identity, Scale = Vector3.One }, new LocalToWorld(), RigidBody2D.Default);
                var handle = w.Spawn(new LocalTransform { Position = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.One }, Collider2D.Box(new Vector2(3, 0.2f)));
                var head = w.Spawn(new LocalTransform { Position = new Vector3(1.5f, 0, 0), Rotation = Quaternion.Identity, Scale = Vector3.One }, Collider2D.Box(new Vector2(0.5f, 1), density: 10));
                w.SetParent(handle, hammer); w.SetParent(head, hammer);
                capsule = Body(w, new Vector2(10, 2), Collider2D.Capsule(0.6f, 2.4f), angle: MathF.PI / 2); // lying down
                upright = Body(w, new Vector2(15, 2), Collider2D.Capsule(0.6f, 2.4f));
            });
            using (app)
            {
                Run(app, 4);
                var physics = w.Resource<PhysicsWorld2D>();
                int b = w.Get<RigidBody2D>(hammer).Handle - 1;
                var com = physics.GetCenterOfMass(b) - physics.GetPosition(b);
                // Handle 3×0.2 at density 1 (0.6), head 0.5×1 at density 10 (5) at x = 1.5: center ≈ 5·1.5 / 5.6 ≈ 1.34 along the handle.
                Assert.True(MathF.Abs(com.Length() - 5 * 1.5f / 5.6f) < 0.01f, $"center of mass {com.Length()} from the origin");
                Assert.True(P(w, hammer).Y < 0.6f, $"the hammer lies on the ground ({P(w, hammer)})");
                Assert.True(MathF.Abs(P(w, capsule).Y - 0.3f) < 0.03f && MathF.Abs(w.Get<RigidBody2D>(capsule).AngularVelocity) < 0.01f, $"a lying capsule rests at its radius without rolling ({P(w, capsule)})");
                Assert.True(MathF.Abs(P(w, upright).Y - 1.2f) < 0.03f && MathF.Abs(P(w, upright).X - 15) < 0.02f, $"an upright capsule stands ({P(w, upright)})");
            }
        });

        Test("physics2d: joints — pendulum period, distance spring, prismatic limits, revolute motor, weld, target drag", () =>
        {
            Entity bob = default, springy = default, slider = default, wheel = default, cantilever = default, dragged = default;
            float length = 2;
            var (app, w) = Scene(w =>
            {
                var anchor = Body(w, new Vector2(0, 10), Collider2D.Box(new Vector2(0.2f, 0.2f)), RigidBody2D.Static);
                bob = Body(w, new Vector2(length, 10), Collider2D.Circle(0.1f));
                w.Spawn(new Joint2D { BodyA = anchor, BodyB = bob, Def = new JointDef2D { Type = JointType2D.Revolute, AnchorA = Vector2.Zero, AnchorB = new Vector2(-length, 0) } });

                var hook = Body(w, new Vector2(10, 10), Collider2D.Box(new Vector2(0.2f, 0.2f)), RigidBody2D.Static);
                springy = Body(w, new Vector2(10, 5), Collider2D.Circle(0.2f)); // stretched 2 m past its rest length
                w.Spawn(new Joint2D { BodyA = hook, BodyB = springy, Def = new JointDef2D { Type = JointType2D.Distance, Length = 3, MinLength = 3, MaxLength = 3, Hertz = 2, DampingRatio = 0.1f } });

                var rail = Body(w, new Vector2(20, 10), Collider2D.Box(new Vector2(0.2f, 0.2f)), RigidBody2D.Static);
                slider = Body(w, new Vector2(20, 10), Collider2D.Box(new Vector2(0.4f, 0.4f)));
                w.Spawn(new Joint2D { BodyA = rail, BodyB = slider, Def = new JointDef2D { Type = JointType2D.Prismatic, Axis = Vector2.Normalize(new Vector2(1, -1)), EnableLimit = true, Lower = -1, Upper = 2 } });

                var axle = Body(w, new Vector2(30, 10), Collider2D.Box(new Vector2(0.2f, 0.2f)), RigidBody2D.Static);
                wheel = Body(w, new Vector2(30, 10), Collider2D.Circle(0.5f));
                w.Spawn(new Joint2D { BodyA = axle, BodyB = wheel, Def = new JointDef2D { Type = JointType2D.Revolute, EnableMotor = true, MotorSpeed = 3, MaxMotorForce = 100 } });

                var wall = Body(w, new Vector2(40, 10), Collider2D.Box(new Vector2(0.2f, 2)), RigidBody2D.Static);
                cantilever = Body(w, new Vector2(41.1f, 10), Collider2D.Box(new Vector2(2, 0.2f)));
                w.Spawn(new Joint2D { BodyA = wall, BodyB = cantilever, Def = new JointDef2D { Type = JointType2D.Weld, AnchorA = new Vector2(0.1f, 0), AnchorB = new Vector2(-1, 0) } });

                dragged = Body(w, new Vector2(50, 0.5f), Collider2D.Box(Vector2.One), RigidBody2D.Default with { NeverSleep = true });
                w.Spawn(new Joint2D { BodyB = dragged, Def = new JointDef2D { Type = JointType2D.Target, Target = new Vector2(55, 5), Hertz = 5, DampingRatio = 0.7f, MaxForce = 1000 } });
            });
            using (app)
            {
                // Pendulum from horizontal: time to the first return to the far side, versus the large-angle period.
                float crossed = -1, minSpring = float.MaxValue, maxSpring = 0, minT = float.MaxValue, maxT = float.MinValue;
                float prevX = P(w, bob).X;
                for (int i = 0; i < 300; i++)
                {
                    app.Tick(1 / 60.0);
                    float x = P(w, bob).X;
                    if (crossed < 0 && i > 30 && prevX < -length * 0.99f + 0.2f && x > prevX) crossed = (i + 1) / 60f;
                    prevX = x;
                    float d = Vector2.Distance(P(w, springy), new Vector2(10, 10));
                    if (i > 10) { minSpring = Math.Min(minSpring, d); maxSpring = Math.Max(maxSpring, d); }
                    float t = Vector2.Dot(P(w, slider) - new Vector2(20, 10), Vector2.Normalize(new Vector2(1, -1)));
                    minT = Math.Min(minT, t); maxT = Math.Max(maxT, t);
                }
                Assert.True(MathF.Abs(Vector2.Distance(P(w, bob), new Vector2(0, 10)) - length) < 0.02f, "the pendulum keeps its length");
                // Half a period from horizontal: 1.18·π·√(L/g) ≈ 1.656 s for 90°.
                Assert.True(crossed > 1.55f && crossed < 1.76f, $"pendulum reached the far side at {crossed} s");
                Assert.True(maxSpring > 3.5f && minSpring < 3, $"the spring oscillates about its rest length ({minSpring}..{maxSpring})");
                Assert.True(minT > -0.05f && maxT < 2.05f && maxT > 1.9f, $"the slider stays within its limits ({minT}..{maxT})");
                float off = MathF.Abs(SimMath.Cross(P(w, slider) - new Vector2(20, 10), Vector2.Normalize(new Vector2(1, -1))));
                Assert.True(off < 0.01f, $"and on its axis ({off})");
                Assert.True(MathF.Abs(w.Get<RigidBody2D>(wheel).AngularVelocity - 3) < 0.05f, $"the motor spins at {w.Get<RigidBody2D>(wheel).AngularVelocity}");
                Assert.True(P(w, cantilever).Y > 9.8f && MathF.Abs(Rot.FromQuaternion(w.Get<LocalTransform>(cantilever).Rotation).Angle) < 0.05f, $"the weld holds ({P(w, cantilever)})");
                Assert.True(Vector2.Distance(P(w, dragged), new Vector2(55, 5)) < 0.1f, $"the target joint dragged its body ({P(w, dragged)})");
            }
        });

        Test("physics2d: worker threads don't change the results", () =>
        {
            List<ulong> Run2(int workers)
            {
                var hashes = new TickHashRecorder();
                var (app, w) = Scene(w =>
                {
                    Ground(w, 80);
                    var r = new SimRandom(4);
                    for (int i = 0; i < 400; i++)
                        Body(w, new Vector2(r.Range(-20f, 20f), 1 + i * 0.25f), i % 2 == 0 ? Collider2D.Circle(r.Range(0.2f, 0.5f)) : Collider2D.Box(new Vector2(r.Range(0.4f, 1f), r.Range(0.4f, 1f))), angle: r.Range(-1f, 1f));
                }, workers);
                using (app) { app.Schedule.Add(hashes); Run(app, 3); }
                return hashes.Hashes;
            }
            var serial = Run2(0); var parallel = Run2(4);
            Assert.True(serial.SequenceEqual(parallel), $"diverged at tick {serial.Zip(parallel).TakeWhile(p => p.First == p.Second).Count()}");
        });

        Test("physics2d: a snapshot continues bit for bit; a scene matches its golden per-tick hashes", () =>
        {
            var world = new PhysicsWorld2D();
            int ground = world.CreateBody(default, BodyType.Static, new Vector2(0, -0.5f), Rot.Identity);
            world.CreateShape(ground, default, Shape.Box(new Vector2(50, 0.5f), Vector2.Zero, Rot.Identity), 1, 0.6f, 0);
            var r = new SimRandom(5);
            for (int i = 0; i < 60; i++)
            {
                int body = world.CreateBody(default, BodyType.Dynamic, new Vector2(r.Range(-5f, 5f), 1 + i * 0.8f), Rot.FromAngle(r.Range(-1f, 1f)));
                world.CreateShape(body, default, i % 2 == 0 ? Shape.Circle(r.Range(0.2f, 0.6f), Vector2.Zero) : Shape.Box(new Vector2(r.Range(0.2f, 0.6f), r.Range(0.2f, 0.6f)), Vector2.Zero, Rot.Identity), 1, 0.6f, 0.1f);
            }
            for (int i = 0; i < 90; i++) world.Step(1 / 60f);
            var copy = world.Clone();
            for (int i = 0; i < 90; i++) { world.Step(1 / 60f); copy.Step(1 / 60f); }
            for (int i = 0; i < world.BodyCapacity; i++)
                Assert.True(world.GetPosition(i) == copy.GetPosition(i) && world.GetRotation(i).C == copy.GetRotation(i).C && world.GetVelocity(i) == copy.GetVelocity(i), $"body {i} diverged after restore");

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
