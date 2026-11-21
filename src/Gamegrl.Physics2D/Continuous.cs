// Ported from Box2D (https://github.com/erincatto/box2d), Copyright (c) 2022 Erin Catto, under the MIT License;
// see THIRD_PARTY_NOTICES.md.
using System.Numerics;

namespace Gamegrl.Physics2D;

// Continuous collision (after Box2D v3's b2SolveContinuous by Erin Catto). A dynamic body that moved more than half its
// thinnest extent in a step may have passed through something; its sweep over the step is tested against static
// shapes by time of impact, and the body is moved back to the first hit. Bullets are also tested against kinematic and
// non-bullet dynamic bodies, after those have finished their own steps. Contacts then handle the touch as usual.
public sealed partial class PhysicsWorld2D
{
    private const float CoreFraction = 0.25f, SafetyFactor = 0.5f;
    private int[] _bullets = new int[16];
    private int _bulletCount;

    private struct ContinuousQuery : ITreeQuery
    {
        public PhysicsWorld2D World;
        public int FastShape, FastBody;
        public ShapeProxy Proxy;
        public Sweep Sweep;
        public Vector2 Centroid1, Centroid2;
        public float Fraction;
        public bool Bullet;

        public bool Hit(int proxy, int s)
        {
            var w = World;
            int b = w.ShapeBody[s];
            if (b == FastBody || w.Sensor[s] || !w.Filters[FastShape].Accepts(w.Filters[s])) return true;
            if (Bullet && w.Bullet[b]) return true; // bullets don't stop each other
            if (w._jointDegree[b] > 0 && w._jointDegree[FastBody] > 0 && w.JointPreventsCollision(b, FastBody)) return true;
            ref readonly var shape = ref w.Geometry[s];
            var q = w.Rotation[b]; var origin = w.GetPosition(b);
            if (shape.Kind == ShapeKind.ChainSegment)
            {
                // A fast body skimming along a chain, or behind it, needs no time of impact.
                var p1 = origin + q.Rotate(shape.Vertex(0)); var p2 = origin + q.Rotate(shape.Vertex(1));
                var e = p2 - p1;
                float length = SimMath.Length(e);
                if (length > LinearSlop)
                {
                    e /= length;
                    float separation1 = V.Cross(Centroid1 - p1, e), separation2 = V.Cross(Centroid2 - p1, e);
                    float core = CoreFraction * w.MinExtent[FastBody];
                    if (separation1 < 0 || (separation1 - separation2 < core && separation2 > core)) return true;
                }
            }
            var proxyA = ShapeProxy.Of(shape);
            // The other body's own motion this step (none for static bodies; bullets see moving ones' sweeps).
            var sweepA = new Sweep { LocalCenter = w.LocalCenter[b], C1 = w.Center[b], C2 = w.Center[b], Q1 = q, Q2 = q };
            if (Bullet && w.Awake[b]) { sweepA.C1 = w.Center0[b]; sweepA.Q1 = w.Rotation0[b]; } // it moved this step too
            var toi = Distance.TimeOfImpact(proxyA, sweepA, Proxy, Sweep, Fraction);
            if (toi.Fraction > 0 && toi.Fraction < Fraction) Fraction = toi.Fraction;
            else if (toi.Fraction == 0)
            {
                // Already touching at the start: try a small circle at the shape's centre instead, so the body still
                // stops before passing through.
                ref readonly var fast = ref w.Geometry[FastShape];
                var core = new ShapeProxy { Count = 1, Radius = CoreFraction * w.MinExtent[FastBody] };
                core.Set(0, fast.Center);
                toi = Distance.TimeOfImpact(proxyA, sweepA, core, Sweep, Fraction);
                if (toi.Fraction > 0 && toi.Fraction < Fraction) Fraction = toi.Fraction;
            }
            return true;
        }
    }

    /// <summary>
    /// Whether body <paramref name="b"/> moved fast enough this step to need continuous collision (from its finalize,
    /// with the step's motion still in hand). Non-bullets are solved here (they read only static shapes and write only
    /// themselves, so in parallel); bullets are queued for after every other body has moved.
    /// </summary>
    private void Continuous(int b, float maxDelta, float maxVelocity, float dt)
    {
        if (Types[b] != BodyType.Dynamic || !Settings.EnableContinuous) return;
        if (F.Max(maxDelta, maxVelocity * dt) <= SafetyFactor * MinExtent[b]) return;
        if (Bullet[b])
        {
            int at = Interlocked.Increment(ref _bulletCount) - 1;
            _bullets[at] = b; // sized for every awake body before the solve
            return;
        }
        SolveContinuous(b, bullet: false);
    }

    private void SolveContinuous(int b, bool bullet)
    {
        var sweep = new Sweep { LocalCenter = LocalCenter[b], C1 = Center0[b], C2 = Center[b], Q1 = Rotation0[b], Q2 = Rotation[b] };
        var xf1 = new Transform2D(sweep.C1 - sweep.Q1.Rotate(sweep.LocalCenter), sweep.Q1);
        var xf2 = new Transform2D(sweep.C2 - sweep.Q2.Rotate(sweep.LocalCenter), sweep.Q2);
        var query = new ContinuousQuery { World = this, FastBody = b, Sweep = sweep, Fraction = 1, Bullet = bullet };
        for (int s = FirstShape[b]; s >= 0; s = NextShape[s])
        {
            if (Sensor[s]) continue;
            ref readonly var shape = ref Geometry[s];
            query.FastShape = s;
            query.Proxy = ShapeProxy.Of(shape);
            query.Centroid1 = xf1.Apply(shape.Center);
            query.Centroid2 = xf2.Apply(shape.Center);
            var box1 = shape.Bounds(xf1.P, xf1.Q); var box2 = shape.Bounds(xf2.P, xf2.Q);
            var swept = new Aabb { Min = SimMath.Min(box1.Min, box2.Min), Max = SimMath.Max(box1.Max, box2.Max) };
            _trees[(int)BodyType.Static].Query(swept, ref query);
            if (bullet) { _trees[(int)BodyType.Kinematic].Query(swept, ref query); _trees[(int)BodyType.Dynamic].Query(swept, ref query); }
        }
        if (query.Fraction >= 1) return;
        // Back to the time of impact; the gravity of the lost time is given back (other forces are ignored, as in Box2D).
        float t = query.Fraction, omt = 1 - t;
        var c = omt * sweep.C1 + t * sweep.C2;
        float qc = omt * sweep.Q1.C + t * sweep.Q2.C, qs = omt * sweep.Q1.S + t * sweep.Q2.S;
        float l = SimMath.Sqrt(qc * qc + qs * qs);
        Rotation[b] = l > 0 ? new Rot { C = qc / l, S = qs / l } : Rotation[b];
        Center[b] = c;
        Velocity[b] -= ((1 - t) * _dt * GravityScale[b]) * Settings.Gravity;
    }

    /// <summary>Bullets, after every other body has finished its step: in body order, so the result doesn't depend on threads.</summary>
    private void SolveBullets()
    {
        if (_bulletCount == 0) return;
        Sort.Heap(_bullets.AsSpan(0, _bulletCount));
        for (int k = 0; k < _bulletCount; k++) SolveContinuous(_bullets[k], bullet: true);
        _bulletCount = 0;
    }
}
