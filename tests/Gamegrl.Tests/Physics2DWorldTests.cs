using System.Numerics;
using Gamegrl.Physics2D;
using static Gamegrl.Tests.Harness;
namespace Gamegrl.Tests;

/// <summary>The 2D physics world used directly (no ECS): geometry, collision algorithms and the Box2D-parity features.</summary>
internal static class Physics2DWorldTests
{
    private static PhysicsWorld2D World(bool sleep = true)
    {
        var w = new PhysicsWorld2D();
        w.Settings.EnableSleep = sleep;
        return w;
    }

    private static void Steps(PhysicsWorld2D w, int n) { for (int i = 0; i < n; i++) w.Step(1 / 60f); }

    private static int Dynamic(PhysicsWorld2D w, Vector2 at, in Shape shape, float friction = 0.6f, BodyDef2D? def = null)
    {
        int b = w.CreateBody(default, def ?? BodyDef2D.Dynamic(at));
        w.CreateShape(b, default, shape, ShapeDef2D.Default with { Friction = friction });
        return b;
    }

    public static void Register()
    {
        Test("physics2d world: convex hulls weld, drop collinear points and wind counter-clockwise", () =>
        {
            Span<Vector2> hull = stackalloc Vector2[8];
            // A square given out of order, with a duplicate and a point on an edge.
            Vector2[] points = [new(1, 1), new(-1, -1), new(1, -1), new(0, -1), new(-1, 1), new(1, 1.0001f)];
            int n = Hull.Compute(points, hull);
            Assert.Equal(4, n);
            Assert.True(Hull.Validate(hull[..n]), "valid and counter-clockwise");
            var square = Shape.ConvexHull(points);
            Assert.True(MathF.Abs(square.MassData(1).Mass - 4) < 1e-4f, $"area {square.MassData(1).Mass}");
            Assert.Equal(0, Hull.Compute([new(0, 0), new(1, 1), new(2, 2)], hull));
            // Box2D's junkyard rock: five points on a circle, in Fibonacci order.
            Span<Vector2> rock = stackalloc Vector2[5];
            float phi = MathF.PI * (MathF.Sqrt(5) - 1);
            for (int i = 0; i < 5; i++) rock[i] = 0.25f * new Vector2(MathF.Cos(phi * i), MathF.Sin(phi * i));
            Assert.Equal(5, Hull.Compute(rock, hull));
        });

        Test("physics2d world: GJK distance, shape casts and time of impact", () =>
        {
            var box = ShapeProxy.Of(Shape.Box(0.5f, 0.5f));
            var circle = ShapeProxy.Of(Shape.Circle(0.25f, Vector2.Zero));
            var d = Distance.Compute(box, circle, new Transform2D(new Vector2(3, 0), Rot.Identity), true);
            Assert.True(MathF.Abs(d.Distance - 2.25f) < 1e-4f, $"distance {d.Distance}");
            Assert.True(Vector2.Distance(d.Normal, Vector2.UnitX) < 1e-4f, $"normal {d.Normal}");
            var overlap = Distance.Compute(box, circle, new Transform2D(new Vector2(0.6f, 0), Rot.Identity), true);
            Assert.Equal(0f, overlap.Distance);
            // A circle swept at the box from 5 m away hits after 4.25 m (less the slop the cast stops short by).
            var cast = Distance.Cast(box, circle, new Transform2D(new Vector2(5, 0), Rot.Identity), new Vector2(-10, 0), 1);
            Assert.True(cast.Hit && MathF.Abs(cast.Fraction * 10 - 4.25f) < 0.01f, $"cast {cast.Hit} at {cast.Fraction * 10}");
            Assert.True(!Distance.Cast(box, circle, new Transform2D(new Vector2(5, 0), Rot.Identity), new Vector2(0, -10), 1).Hit, "a cast that passes by misses");
            // A small box crossing a thin wall in one step: TOI finds where it arrives.
            var wall = ShapeProxy.Of(Shape.Box(0.05f, 2));
            var bullet = ShapeProxy.Of(Shape.Box(0.1f, 0.1f));
            var still = new Sweep { C1 = Vector2.Zero, C2 = Vector2.Zero, Q1 = Rot.Identity, Q2 = Rot.Identity };
            var moving = new Sweep { C1 = new Vector2(-3, 0), C2 = new Vector2(3, 0), Q1 = Rot.Identity, Q2 = Rot.FromAngle(0.5f) };
            var toi = Distance.TimeOfImpact(wall, still, bullet, moving, 1);
            Assert.Equal(ToiState.Hit, toi.State);
            float x = -3 + 6 * toi.Fraction;
            Assert.True(x < -0.15f + 0.01f && x > -0.3f, $"arrives at x={x}");
        });

        Test("physics2d world: a box rests on a segment; a ball rolls along a chain without catching on its joins", () =>
        {
            var w = World(sleep: false);
            int ground = w.CreateBody(default, BodyDef2D.Default);
            w.CreateShape(ground, default, Shape.Segment(new Vector2(-20, 0), new Vector2(20, 0)), ShapeDef2D.Default);
            int box = Dynamic(w, new Vector2(-10, 2), Shape.Box(0.5f, 0.5f));
            // A flat chain made of many short pieces, running right to left so its outside (its right) faces up.
            var points = new Vector2[41];
            for (int i = 0; i < points.Length; i++) points[i] = new Vector2(20 - i, 5);
            w.CreateChain(ground, default, points, ChainDef2D.Default with { Ghost1 = new Vector2(21, 5), Ghost2 = new Vector2(-21, 5) });
            int puck = Dynamic(w, new Vector2(-15, 5.5f), Shape.Box(0.5f, 0.5f), friction: 0);
            w.SetVelocity(puck, new Vector2(8, 0), 0);
            Steps(w, 150);
            Assert.True(MathF.Abs(w.GetPosition(box).Y - 0.5f) < 0.02f, $"box rests at {w.GetPosition(box)}");
            var p = w.GetPosition(puck);
            // Frictionless: it keeps its speed over the joins (a catch would kick it up or slow it).
            Assert.True(p.X > 3 && MathF.Abs(p.Y - 5.5f) < 0.02f && MathF.Abs(w.GetVelocity(puck).X - 8) < 0.05f, $"puck at {p}, v {w.GetVelocity(puck)}");
        });

        Test("physics2d world: chains are one-sided, and a loop holds balls inside it", () =>
        {
            var w = World(sleep: false);
            int ground = w.CreateBody(default, BodyDef2D.Default);
            // A clockwise loop collides on its inside (Box2D's spinner container).
            var loop = new Vector2[36];
            for (int i = 0; i < loop.Length; i++) { float a = -2 * MathF.PI * i / loop.Length; loop[i] = 5 * new Vector2(MathF.Cos(a), MathF.Sin(a)); }
            w.CreateChain(ground, default, loop, ChainDef2D.Default with { IsLoop = true });
            var balls = new List<int>();
            for (int i = 0; i < 10; i++) balls.Add(Dynamic(w, new Vector2(-2 + 0.45f * i, 0), Shape.Circle(0.2f, Vector2.Zero)));
            int box = Dynamic(w, new Vector2(1, 2), Shape.Box(0.3f, 0.3f));
            // A ball under a one-sided floor falls up through it... from below, with gravity reversed for it.
            var floor = new[] { new Vector2(20, 8), new Vector2(10, 8) }; // right to left: its solid side faces up
            w.CreateChain(ground, default, floor, ChainDef2D.Default with { Ghost1 = new Vector2(21, 8), Ghost2 = new Vector2(9, 8) });
            int riser = Dynamic(w, new Vector2(15, 7), Shape.Circle(0.25f, Vector2.Zero));
            w.SetDamping(riser, 0, 0, -1);
            Steps(w, 180);
            foreach (int b in balls) Assert.True(w.GetPosition(b).Length() < 4.82f, $"ball inside the loop at {w.GetPosition(b)}");
            Assert.True(w.GetPosition(box).Length() < 4.8f, $"box inside the loop at {w.GetPosition(box)}");
            float lowest = balls.Min(b => w.GetPosition(b).Y);
            Assert.True(MathF.Abs(lowest - (-5 * MathF.Cos(MathF.PI / 36) + 0.2f)) < 0.05f, $"the lowest ball rests on the bottom at y={lowest}");
            Assert.True(w.GetPosition(riser).Y > 9, $"the riser passed up through the floor's back: {w.GetPosition(riser)}");
        });

        Test("physics2d world: filter groups override masks (ragdoll parts); 64-bit categories", () =>
        {
            var w = World(sleep: false);
            w.Settings.Gravity = Vector2.Zero;
            var high = new Filter2D { Category = 1UL << 40, Mask = 1UL << 40 };
            int Make(Vector2 at, Filter2D f, Vector2 v)
            {
                int b = w.CreateBody(default, BodyDef2D.Dynamic(at) with { LinearVelocity = v });
                w.CreateShape(b, default, Shape.Circle(0.5f, Vector2.Zero), ShapeDef2D.Default with { Filter = f });
                return b;
            }
            // Same negative group: pass through each other even though the masks match.
            int a1 = Make(new Vector2(-2, 0), high with { Group = -3 }, new Vector2(2, 0)), a2 = Make(new Vector2(2, 0), high with { Group = -3 }, new Vector2(-2, 0));
            // Same positive group: collide even though the masks don't.
            int b1 = Make(new Vector2(-2, 5), new Filter2D { Category = 1, Mask = 0, Group = 2 }, new Vector2(2, 0));
            int b2 = Make(new Vector2(2, 5), new Filter2D { Category = 1, Mask = 0, Group = 2 }, new Vector2(-2, 0));
            // Different groups fall back to the 64-bit masks.
            int c1 = Make(new Vector2(-2, 10), high with { Group = -1 }, new Vector2(2, 0)), c2 = Make(new Vector2(2, 10), high with { Group = -2 }, new Vector2(-2, 0));
            Steps(w, 120);
            Assert.True(w.GetPosition(a1).X > 1.5f && w.GetPosition(a2).X < -1.5f, "the negative group passed through");
            Assert.True(w.GetPosition(b1).X < -0.4f && w.GetPosition(b2).X > 0.4f, "the positive group bounced off");
            Assert.True(w.GetPosition(c1).X < -0.4f && w.GetPosition(c2).X > 0.4f, "different groups used the masks");
        });

        Test("physics2d world: bodies created asleep stay asleep until hit; per-body sleep thresholds", () =>
        {
            var w = World();
            w.Settings.Gravity = Vector2.Zero;
            var sleepers = new List<int>();
            for (int i = 0; i < 5; i++) sleepers.Add(Dynamic(w, new Vector2(i * 1.01f, 0), Shape.Box(0.5f, 0.5f), def: BodyDef2D.Dynamic(new Vector2(i * 1.01f, 0)) with { IsAwake = false }));
            Steps(w, 30);
            Assert.True(sleepers.All(b => !w.IsAwake(b)), "still asleep");
            Assert.Equal(0, w.AwakeBodyCount);
            int hammer = Dynamic(w, new Vector2(-5, 0), Shape.Box(0.5f, 0.5f), def: BodyDef2D.Dynamic(new Vector2(-5, 0)) with { LinearVelocity = new Vector2(10, 0) });
            Steps(w, 60);
            Assert.True(sleepers.All(b => w.IsAwake(b) || w.GetPosition(b).X > 4), "the hit woke the row");
            Assert.True(w.GetPosition(sleepers[^1]).X > 4.5f, $"and pushed it: {w.GetPosition(sleepers[^1])}");
            // A drifting body with a high threshold sleeps; the same drift with the default doesn't.
            var w2 = World();
            w2.Settings.Gravity = Vector2.Zero;
            int lazy = Dynamic(w2, Vector2.Zero, Shape.Circle(0.5f, Vector2.Zero), def: BodyDef2D.Dynamic(Vector2.Zero) with { SleepThreshold = 1, LinearVelocity = new Vector2(0.5f, 0) });
            int keen = Dynamic(w2, new Vector2(0, 5), Shape.Circle(0.5f, Vector2.Zero), def: BodyDef2D.Dynamic(new Vector2(0, 5)) with { LinearVelocity = new Vector2(0.5f, 0) });
            Steps(w2, 60);
            Assert.True(!w2.IsAwake(lazy) && w2.IsAwake(keen), $"lazy awake {w2.IsAwake(lazy)}, keen awake {w2.IsAwake(keen)}");
        });

        Test("physics2d world: a body touching more contacts than there are colours solves through the overflow", () =>
        {
            var w = World(sleep: false);
            int ground = w.CreateBody(default, BodyDef2D.Default);
            w.CreateShape(ground, default, Shape.Segment(new Vector2(-50, 0), new Vector2(50, 0)), ShapeDef2D.Default);
            int plank = Dynamic(w, new Vector2(0, 0.25f), Shape.Box(20, 0.25f));
            var boxes = new List<int>();
            for (int i = 0; i < 40; i++) boxes.Add(Dynamic(w, new Vector2(-19.5f + i, 1), Shape.Box(0.4f, 0.4f)));
            Steps(w, 120);
            Assert.True(w.OverflowConstraints > 0, "some constraints overflowed the colours");
            Assert.True(MathF.Abs(w.GetPosition(plank).Y - 0.25f) < 0.01f, $"plank at {w.GetPosition(plank)}");
            Assert.True(boxes.All(b => MathF.Abs(w.GetPosition(b).Y - 0.9f) < 0.01f), "every box rests on the plank");
        });

        Test("physics2d world: islands sleep apart, wake apart, and split before sleeping again", () =>
        {
            var w = World();
            int ground = w.CreateBody(default, BodyDef2D.Default);
            w.CreateShape(ground, default, Shape.Segment(new Vector2(-100, 0), new Vector2(100, 0)), ShapeDef2D.Default);
            var left = new List<int>(); var right = new List<int>();
            for (int row = 0; row < 6; row++)
                for (int i = 0; i < 6 - row; i++)
                {
                    left.Add(Dynamic(w, new Vector2(-20 + i + 0.5f * row, 0.5f + row), Shape.Box(0.5f, 0.5f)));
                    right.Add(Dynamic(w, new Vector2(20 + i + 0.5f * row, 0.5f + row), Shape.Box(0.5f, 0.5f)));
                }
            Steps(w, 180);
            Assert.Equal(0, w.AwakeBodyCount);
            Assert.Equal(0, w.AwakeContactCount); // nothing between sleeping bodies is collided
            // A joint made and destroyed inside the left pile wakes it alone (Box2D's sleep benchmark) and leaves its
            // island needing a split; it still falls asleep again.
            int j = w.CreateJoint(default, left[0], left[1], JointDef2D.Default(JointType2D.Filter));
            w.DestroyJoint(j);
            Assert.True(left.All(w.IsAwake) && right.All(b => !w.IsAwake(b)), "only the left pile woke");
            Steps(w, 120);
            Assert.True(left.All(b => !w.IsAwake(b)), "and it slept again after splitting");
            // Destroying a body wakes what touched it.
            w.DestroyBody(right[^1]);
            Assert.True(w.IsAwake(right[^2]) || w.IsAwake(right[^3]), "the pile under the removed box woke");
            Steps(w, 180);
            Assert.Equal(0, w.AwakeBodyCount);
            // A moving kinematic body keeps what it touches awake; stopped, everything sleeps.
            int belt = w.CreateBody(default, BodyDef2D.Default with { Type = BodyType.Kinematic, Position = new Vector2(0, 1) });
            w.CreateShape(belt, default, Shape.Box(5, 0.25f), ShapeDef2D.Default);
            int rider = Dynamic(w, new Vector2(0, 1.75f), Shape.Box(0.5f, 0.5f));
            w.SetVelocity(belt, new Vector2(0.5f, 0), 0);
            Steps(w, 120);
            Assert.True(w.IsAwake(rider) && w.GetPosition(rider).X > 0.8f, $"the rider rides along: {w.GetPosition(rider)}");
            w.SetVelocity(belt, Vector2.Zero, 0);
            Steps(w, 120);
            Assert.True(!w.IsAwake(rider) && !w.IsAwake(belt), "and both sleep once the belt stops");
            // A snapshot of a half-asleep world continues exactly like the original.
            w.Wake(left[3]);
            var copy = w.Clone();
            Steps(w, 30); Steps(copy, 30);
            for (int b = 0; b < w.BodyCapacity; b++)
                if (w.IsAlive(b)) Assert.True(w.GetPosition(b) == copy.GetPosition(b) && w.IsAwake(b) == copy.IsAwake(b), $"body {b} matches");
        });

        Test("physics2d world: continuous collision stops fast bodies at static walls; bullets at moving ones too", () =>
        {
            (float X, bool Hit) Shoot(bool bullet, bool staticWall, bool continuous = true)
            {
                var w = World(sleep: false);
                w.Settings.Gravity = Vector2.Zero;
                w.Settings.EnableContinuous = continuous;
                int wall = w.CreateBody(default, BodyDef2D.Default with { Type = staticWall ? BodyType.Static : BodyType.Dynamic });
                w.CreateShape(wall, default, Shape.Box(0.05f, 2), ShapeDef2D.Default with { Density = 50 });
                int shot = w.CreateBody(default, BodyDef2D.Dynamic(new Vector2(-3, 0.3f)) with { LinearVelocity = new Vector2(300, 0), IsBullet = bullet });
                w.CreateShape(shot, default, Shape.Circle(0.05f, Vector2.Zero), ShapeDef2D.Default);
                Steps(w, 10);
                return (w.GetPosition(shot).X, w.GetPosition(wall).X > 0.01f);
            }
            var stat = Shoot(bullet: false, staticWall: true);
            Assert.True(stat.X < 0, $"stopped by the static wall: x={stat.X}");
            Assert.True(Shoot(false, true, continuous: false).X > 1, "without continuous collision it tunnels");
            var plain = Shoot(bullet: false, staticWall: false);
            Assert.True(plain.X > 1, $"an ordinary fast body passes a moving wall (as in Box2D): x={plain.X}");
            var bullet = Shoot(bullet: true, staticWall: false);
            Assert.True(bullet.X < 0.1f && bullet.Hit, $"a bullet hits it and pushes it: x={bullet.X}, wall moved {bullet.Hit}");
        });

        Test("physics2d world: a kinematic body driven to targets pushes boxes along", () =>
        {
            var w = World();
            int ground = w.CreateBody(default, BodyDef2D.Default);
            w.CreateShape(ground, default, Shape.Segment(new Vector2(-50, 0), new Vector2(50, 0)), ShapeDef2D.Default);
            int pusher = w.CreateBody(default, BodyDef2D.Default with { Type = BodyType.Kinematic, Position = new Vector2(-5, 0) });
            w.CreateShape(pusher, default, Shape.Box(new Vector2(0.5f, 1), new Vector2(0, 1), Rot.Identity), ShapeDef2D.Default);
            int box = Dynamic(w, new Vector2(0, 0.5f), Shape.Box(0.5f, 0.5f));
            for (int i = 0; i < 120; i++)
            {
                w.SetTargetTransform(pusher, new Vector2(-5 + 10 * (i + 1) / 120f, 0), Rot.Identity, 1 / 60f);
                w.Step(1 / 60f);
            }
            Assert.True(MathF.Abs(w.GetPosition(pusher).X - 5) < 1e-3f, $"pusher reached its target: {w.GetPosition(pusher)}");
            Assert.True(w.GetPosition(box).X > 5.9f, $"and pushed the box: {w.GetPosition(box)}");
            // (Rotation integrates without trig, as in Box2D, so a large turn in one step falls a little short.)
            w.SetTargetTransform(pusher, w.GetPosition(pusher), Rot.FromAngle(0.2f), 1 / 60f);
            w.Step(1 / 60f);
            Assert.True(MathF.Abs(w.GetRotation(pusher).Angle - 0.2f) < 2e-3f, $"turned to {w.GetRotation(pusher).Angle}");
        });

        Test("physics2d world: wheel joints sag to g/ω² on their spring, stop at limits, and their motor turns the wheel", () =>
        {
            var w = World(sleep: false);
            int chassis = w.CreateBody(default, BodyDef2D.Static(new Vector2(0, 5)));
            w.CreateShape(chassis, default, Shape.Box(3, 0.2f), ShapeDef2D.Default);
            int Wheel(float x, JointDef2D def, out int joint)
            {
                int b = Dynamic(w, new Vector2(x, 5), Shape.Circle(0.4f, Vector2.Zero));
                joint = w.CreateJoint(default, chassis, b, def);
                return b;
            }
            // Vertical suspension: the spring's stiffness is the wheel's mass × ω², so it sags g/ω² whatever the mass.
            var suspension = JointDef2D.Wheel(new Vector2(-2, 0), Vector2.Zero, Vector2.UnitY) with { Hertz = 1, DampingRatio = 0.7f };
            int free = Wheel(-2, suspension, out int jFree);
            Wheel(0, suspension with { LocalFrameA = new Transform2D(Vector2.Zero, suspension.LocalFrameA.Q), EnableLimit = true, Lower = -0.1f, Upper = 0.1f }, out int jLimited);
            int driven = Wheel(2, suspension with { LocalFrameA = new Transform2D(new Vector2(2, 0), suspension.LocalFrameA.Q), EnableMotor = true, MotorSpeed = -5, MaxMotorForce = 50 }, out int jDriven);
            Steps(w, 300);
            float sag = 10 / (2 * MathF.PI * 1) / (2 * MathF.PI * 1);
            Assert.True(MathF.Abs(w.JointTranslation(jFree) + sag) < 0.005f, $"sagged {w.JointTranslation(jFree)}, expected {-sag}");
            Assert.True(MathF.Abs(w.GetPosition(free).Y - (5 - sag)) < 0.005f && MathF.Abs(w.GetPosition(free).X + 2) < 0.002f, $"and stayed on its axis at {w.GetPosition(free)}");
            Assert.True(MathF.Abs(w.JointTranslation(jLimited) + 0.1f) < 0.01f, $"the limited wheel stops at its lower limit: {w.JointTranslation(jLimited)}");
            Assert.True(MathF.Abs(w.GetAngularVelocity(driven) + 5) < 0.01f && MathF.Abs(w.JointSpeed(jDriven) + 5) < 0.01f, $"the motor spins the wheel at {w.GetAngularVelocity(driven)}");
            Assert.True(MathF.Abs(w.JointTranslation(jDriven) + sag) < 0.005f, "spinning doesn't change the sag");
            // The joint carries the wheel's weight: along the axis on the spring, or at the limit.
            float weight = 10 * MathF.PI * 0.4f * 0.4f;
            var (springForce, _) = w.JointConstraintForce(jFree);
            Assert.True(Vector2.Distance(springForce, new Vector2(0, weight)) < 0.02f * weight, $"spring force {springForce}, weight {weight}");
            var (limitForce, _) = w.JointConstraintForce(jLimited);
            Assert.True(Vector2.Distance(limitForce, new Vector2(0, weight)) < 0.02f * weight, $"limit force {limitForce}");
            Assert.True(MathF.Abs(w.JointMotorForce(jDriven)) < 0.01f, $"a free-spinning wheel needs no motor torque ({w.JointMotorForce(jDriven)})");
        });

        Test("physics2d world: motor joints drive velocity within their force; movers per axis", () =>
        {
            var w = World(sleep: false);
            // Unit-mass boxes (1 × 1, density 1) attached to the world.
            int Box(float x) => Dynamic(w, new Vector2(x, 0), Shape.Box(0.5f, 0.5f));
            var motor = JointDef2D.Default(JointType2D.Motor);
            int strong = Box(0), weak = Box(10), held = Box(20);
            w.CreateJoint(default, -1, strong, motor with { LinearVelocity = new Vector2(2, 0), AngularVelocity = 1, MaxVelocityForce = 1000, MaxVelocityTorque = 1000 });
            int jWeak = w.CreateJoint(default, -1, weak, motor with { MaxVelocityForce = 5, MaxVelocityTorque = 1000 }); // half the weight
            int jHeld = w.CreateJoint(default, -1, held, motor with { MaxVelocityForce = 1000, MaxVelocityTorque = 1000 });
            var mover = JointDef2D.Default(JointType2D.Mover) with { LinearVelocity = new Vector2(3, 3) };
            int sideways = Box(30), both = Box(40);
            int jSideways = w.CreateJoint(default, -1, sideways, mover with { MaxAxisForce = new Vector2(1000, 0) });
            int jBoth = w.CreateJoint(default, -1, both, mover with { MaxAxisForce = new Vector2(1000, 1000) });
            Steps(w, 60);
            Assert.True(Vector2.Distance(w.GetVelocity(strong), new Vector2(2, 0)) < 0.01f && MathF.Abs(w.GetAngularVelocity(strong) - 1) < 0.01f,
                $"the motor drives {w.GetVelocity(strong)}, {w.GetAngularVelocity(strong)}");
            Assert.True(MathF.Abs(w.GetPosition(strong).X - 2) < 0.05f && MathF.Abs(w.GetPosition(strong).Y) < 0.01f, $"and moved to {w.GetPosition(strong)}");
            // Half its weight held up: it falls at half g.
            Assert.True(MathF.Abs(w.GetVelocity(weak).Y + 5) < 0.1f && MathF.Abs(w.GetVelocity(weak).X) < 1e-3f, $"the weak motor falls at {w.GetVelocity(weak)}");
            Assert.True(Vector2.Distance(w.JointConstraintForce(jWeak).Force, new Vector2(0, 5)) < 0.01f, $"at its force limit {w.JointConstraintForce(jWeak).Force}");
            Assert.True(Vector2.Distance(w.JointConstraintForce(jHeld).Force, new Vector2(0, 10)) < 0.05f && w.GetPosition(held).Length() < 20.01f,
                $"a still motor holds the weight: {w.JointConstraintForce(jHeld).Force}");
            Assert.True(MathF.Abs(w.GetVelocity(sideways).X - 3) < 0.01f && MathF.Abs(w.GetVelocity(sideways).Y + 10) < 0.1f, $"a mover with no vertical force falls freely: {w.GetVelocity(sideways)}");
            Assert.True(MathF.Abs(w.JointConstraintForce(jSideways).Force.Y) < 1e-3f, "and pushes nothing vertically");
            Assert.True(Vector2.Distance(w.GetVelocity(both), new Vector2(3, 3)) < 0.01f, $"a mover on both axes drives {w.GetVelocity(both)}");
            Assert.True(Vector2.Distance(w.JointConstraintForce(jBoth).Force, new Vector2(0, 10)) < 0.05f, $"holding up its weight {w.JointConstraintForce(jBoth).Force}");
        });

        Test("physics2d world: pogo joints: tension pulls a body down to the rest length, none lets a jump go, compression gives way past its force", () =>
        {
            var w = World(sleep: false);
            var pogo = JointDef2D.Default(JointType2D.Pogo) with { Normal = Vector2.UnitY, Length = 1, Hertz = 5, DampingRatio = 1, MaxCompressionForce = 1000 };
            // Unit-mass boxes on pogos standing on the world's y = 0.
            int Rider(float x, float y, float vy, JointDef2D def, out int joint)
            {
                int b = Dynamic(w, default, Shape.Box(0.5f, 0.5f), def: BodyDef2D.Dynamic(new Vector2(x, y)) with { FixedRotation = true, LinearVelocity = new Vector2(0, vy) });
                joint = w.CreateJoint(default, -1, b, def with { LocalFrameA = new Transform2D(new Vector2(x, 0), Rot.Identity) });
                return b;
            }
            int jumper = Rider(0, 1, 5, pogo, out int jJumper);
            // Box2D's character mover gives its pogo tension to pull the body onto the ground, and none while jumping.
            int rider = Rider(5, 3, 0, pogo with { MaxTensionForce = 1000 }, out int jRider); // stretched 2 m
            int heavy = Rider(10, 1, 0, pogo with { MaxCompressionForce = 5 }, out int jHeavy); // half its weight
            Steps(w, 18);
            // Rising off its rest length, the pogo doesn't hold it back: gravity alone slows it.
            Assert.True(MathF.Abs(w.GetVelocity(jumper).Y - 2) < 0.02f, $"the jumper rises at {w.GetVelocity(jumper).Y} after 0.3 s");
            Assert.True(w.JointConstraintForce(jJumper).Force.Length() < 1e-3f, "with no force from its pogo");
            Steps(w, 180);
            Assert.True(MathF.Abs(w.JointTranslation(jRider) - 1) < 0.01f && MathF.Abs(w.GetVelocity(rider).Y) < 0.01f, $"the rider settled at the rest length: {w.JointTranslation(jRider)}");
            Assert.True(Vector2.Distance(w.JointConstraintForce(jRider).Force, new Vector2(0, 10)) < 0.05f, $"carrying its weight {w.JointConstraintForce(jRider).Force}");
            Assert.True(w.GetPosition(heavy).Y < -5, $"a pogo weaker than the weight gives way: {w.GetPosition(heavy)}");
            Assert.True(MathF.Abs(w.JointConstraintForce(jHeavy).Force.Y - 5) < 0.01f, $"pushing at its limit {w.JointConstraintForce(jHeavy).Force}");
        });

        Test("physics2d world: constraint forces of hanging bodies; joint events when a threshold is reached", () =>
        {
            var w = World(sleep: false);
            int wall = w.CreateBody(default, BodyDef2D.Static(new Vector2(0, 10)));
            w.CreateShape(wall, default, Shape.Box(0.2f, 0.2f), ShapeDef2D.Default);
            // Unit-mass bodies: a bob hanging on a hinge, a beam welded at one end, a beam held level by a hinge's motor, a rope.
            int bob = Dynamic(w, new Vector2(0, 8), Shape.Box(0.5f, 0.5f));
            int jHinge = w.CreateJoint(default, wall, bob, JointDef2D.Revolute(Vector2.Zero, new Vector2(0, 2)));
            int beam = Dynamic(w, new Vector2(11, 10), Shape.Box(1, 0.25f));
            int wall2 = w.CreateBody(default, BodyDef2D.Static(new Vector2(10, 10)));
            int jWeld = w.CreateJoint(default, wall2, beam, JointDef2D.Weld(Vector2.Zero, new Vector2(-1, 0)));
            int lever = Dynamic(w, new Vector2(21, 10), Shape.Box(1, 0.25f));
            int wall3 = w.CreateBody(default, BodyDef2D.Static(new Vector2(20, 10)));
            int jLever = w.CreateJoint(default, wall3, lever, JointDef2D.Revolute(Vector2.Zero, new Vector2(-1, 0)) with { EnableMotor = true, MotorSpeed = 0, MaxMotorForce = 1000 });
            int weight = Dynamic(w, new Vector2(30, 7), Shape.Box(0.5f, 0.5f));
            int wall4 = w.CreateBody(default, BodyDef2D.Static(new Vector2(30, 10)));
            int jRope = w.CreateJoint(default, wall4, weight, JointDef2D.Distance(Vector2.Zero, Vector2.Zero, 3));
            Steps(w, 120);
            bool Near((Vector2 Force, float Torque) got, Vector2 force, float torque) => Vector2.Distance(got.Force, force) < 0.05f && MathF.Abs(got.Torque - torque) < 0.05f;
            Assert.True(Near(w.JointConstraintForce(jHinge), new Vector2(0, 10), 0), $"hinge {w.JointConstraintForce(jHinge)}");
            // The weld holds the weight and the moment of a beam whose center is 1 m out.
            Assert.True(Near(w.JointConstraintForce(jWeld), new Vector2(0, 10), 10), $"weld {w.JointConstraintForce(jWeld)}");
            Assert.True(Near(w.JointConstraintForce(jLever), new Vector2(0, 10), 10) && MathF.Abs(w.JointMotorForce(jLever) - 10) < 0.05f, $"motor {w.JointConstraintForce(jLever)}, {w.JointMotorForce(jLever)}");
            Assert.True(MathF.Abs(w.GetRotation(lever).Angle) < 0.01f, $"the motor keeps the lever level ({w.GetRotation(lever).Angle})");
            Assert.True(Near(w.JointConstraintForce(jRope), new Vector2(0, 10), 0), $"rope {w.JointConstraintForce(jRope)}");

            // A pendulum dropped from level pulls 3 × its weight at the bottom: a 2 × threshold raises events there, 4 × never does.
            var events = World(sleep: false);
            int pivot = events.CreateBody(default, BodyDef2D.Static(new Vector2(0, 10)));
            var owner = new Entity(7, 1);
            int swinging = Dynamic(events, new Vector2(2, 10), Shape.Circle(0.25f, Vector2.Zero));
            float m = MathF.PI * 0.25f * 0.25f;
            int jLow = events.CreateJoint(owner, pivot, swinging, JointDef2D.Revolute(Vector2.Zero, new Vector2(-2, 0)) with { ForceThreshold = 2 * 10 * m });
            int swinging2 = Dynamic(events, new Vector2(12, 10), Shape.Circle(0.25f, Vector2.Zero));
            int pivot2 = events.CreateBody(default, BodyDef2D.Static(new Vector2(10, 10)));
            events.CreateJoint(new Entity(8, 1), pivot2, swinging2, JointDef2D.Revolute(Vector2.Zero, new Vector2(-2, 0)) with { ForceThreshold = 4 * 10 * m });
            int first = -1, count = 0;
            for (int i = 0; i < 90; i++)
            {
                events.Step(1 / 60f);
                var raised = events.JointEvents;
                foreach (var e in raised)
                {
                    Assert.True(e.Joint == jLow && e.Owner.Equals(owner), $"only the low-threshold joint raises events (got joint {e.Joint})");
                    if (first < 0) first = i;
                    count++;
                }
                Assert.True(raised.Length <= 1, "one event per joint per step");
            }
            // Tension is 3mg·sin θ below level: past 2 × weight from θ ≈ 42°; the bottom comes after a quarter of the 90° swing's 3.3 s period.
            Assert.True(first > 20 && first < 50 && count > 10, $"events from step {first}, {count} of them");
            events.DestroyJoint(jLow);
            events.Step(1 / 60f);
            Assert.Equal(0, events.JointEvents.Length);
        });
    }
}
