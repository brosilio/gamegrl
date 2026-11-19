using System.Numerics;
using System.Runtime.InteropServices;

namespace Gamegrl.Physics2D;

public enum BodyType : byte { Static, Kinematic, Dynamic }

/// <summary>World-wide settings (a resource read each step).</summary>
public struct PhysicsSettings2D
{
    public Vector2 Gravity;
    /// <summary>Solver substeps per fixed step: more is stiffer and more accurate (4 is a good default).</summary>
    public int Substeps;
    /// <summary>Contact stiffness, capped at a quarter of the substep rate.</summary>
    public float ContactHertz;
    public float ContactDampingRatio;
    /// <summary>The fastest overlapping bodies are pushed apart, in m/s.</summary>
    public float MaxPushout;
    /// <summary>Approach speed below which collisions don't bounce, in m/s.</summary>
    public float RestitutionThreshold;

    public static PhysicsSettings2D Default => new()
    {
        Gravity = new Vector2(0, -10), Substeps = 4, ContactHertz = 30, ContactDampingRatio = 10, MaxPushout = 3, RestitutionThreshold = 1,
    };
}

/// <summary>A ray hit: the entity, the point, the surface normal and the fraction of the ray travelled.</summary>
public struct RayHit2D
{
    public Entity Entity;
    public Vector2 Point, Normal;
    public float Fraction;
}

/// <summary>
/// The 2D physics state: bodies in struct-of-arrays storage keyed by dense indices, a sort-and-sweep broadphase, cached
/// contacts for warm starting, and a soft-step solver (substeps with soft contacts, relaxation, restitution; after
/// Box2D v3). Single-threaded and deterministic: the same steps give the same bits, on every platform under
/// <c>CrossPlatform</c>. Allocation-free once its arrays have grown. Copyable for snapshots (<see cref="Clone"/>).
/// </summary>
public sealed class PhysicsWorld2D
{
    // ---------------------------------------------------------------- bodies (struct of arrays)
    private int _capacity, _count;           // _count = high-water mark of indices
    private int[] _free = new int[16];
    private int _freeCount;
    internal Entity[] Entities = [];
    internal BodyType[] Types = [];
    internal bool[] Alive = [];
    internal Vector2[] Positions = [], Velocities = [], DeltaPositions = [], Written = [];
    internal Rot[] Rotations = [], StartRotations = [], WrittenRotations = [];
    internal float[] AngularVelocities = [], InvMass = [], InvInertia = [], GravityScale = [], LinearDamping = [], AngularDamping = [];
    internal float[] Friction = [], Restitution = [];
    internal Shape[] Shapes = [];
    internal Aabb[] Bounds = [];

    // ---------------------------------------------------------------- broadphase and contacts
    private int[] _sorted = [];
    private int _sortedCount;
    private Contact[] _contacts = new Contact[64];
    private int _contactCount;
    private Dictionary<ulong, int> _cache = new(), _nextCache = new();
    private CachedImpulse[] _cached = new CachedImpulse[64], _nextCached = new CachedImpulse[64];
    private int _cachedCount, _nextCachedCount;

    public PhysicsSettings2D Settings = PhysicsSettings2D.Default;
    public int BodyCount => _count - _freeCount;
    public int ContactCount => _contactCount;

    private struct CachedImpulse { public ushort Id0, Id1; public float Normal0, Tangent0, Normal1, Tangent1; }

    private struct ContactPoint
    {
        public Vector2 AnchorA, AnchorB;
        public float AdjustedSeparation, NormalImpulse, TangentImpulse, MaxNormalImpulse, NormalMass, TangentMass, RelativeVelocity;
        public ushort Id;
    }

    private struct Contact
    {
        public int A, B;
        public Vector2 Normal;
        public float Friction, Restitution;
        public int Count;
        public ContactPoint P0, P1;
    }

    // ---------------------------------------------------------------- body management

    public int CreateBody(Entity entity, BodyType type, in Shape shape, float density, float friction, float restitution, Vector2 position, Rot rotation)
    {
        int i;
        if (_freeCount > 0) i = _free[--_freeCount];
        else
        {
            if (_count == _capacity) Grow(Math.Max(64, _capacity * 2));
            i = _count++;
        }
        Entities[i] = entity; Types[i] = type; Alive[i] = true;
        Positions[i] = position; Rotations[i] = rotation; Velocities[i] = default; AngularVelocities[i] = 0;
        Written[i] = position; WrittenRotations[i] = rotation;
        Shapes[i] = shape; Friction[i] = friction; Restitution[i] = restitution;
        GravityScale[i] = 1; LinearDamping[i] = 0; AngularDamping[i] = 0;
        SetMass(i, density);
        _sorted[_sortedCount++] = i;
        return i;
    }

    public void DestroyBody(int body)
    {
        if (!Alive[body]) return;
        Alive[body] = false;
        Entities[body] = default;
        if (_freeCount == _free.Length) Array.Resize(ref _free, _free.Length * 2);
        _free[_freeCount++] = body;
        int k = Array.IndexOf(_sorted, body, 0, _sortedCount);
        Array.Copy(_sorted, k + 1, _sorted, k, _sortedCount - k - 1);
        _sortedCount--;
    }

    public bool IsAlive(int body) => (uint)body < (uint)_count && Alive[body];
    public Entity EntityOf(int body) => Entities[body];

    public void SetMass(int body, float density)
    {
        if (Types[body] != BodyType.Dynamic) { InvMass[body] = 0; InvInertia[body] = 0; return; }
        var (mass, inertia, center) = Shapes[body].MassData(Math.Max(density, 1e-6f));
        // Bodies rotate about their origin; inertia about the origin includes the offset of the mass.
        InvMass[body] = 1 / mass;
        InvInertia[body] = inertia > 0 ? 1 / inertia : 0;
    }

    public void SetType(int body, BodyType type, float density) { Types[body] = type; SetMass(body, density); }
    public void SetShape(int body, in Shape shape, float density) { Shapes[body] = shape; SetMass(body, density); }
    public void FixRotation(int body) => InvInertia[body] = 0;
    public ref Vector2 Position(int body) => ref Positions[body];
    public ref Rot Rotation(int body) => ref Rotations[body];
    public ref Vector2 Velocity(int body) => ref Velocities[body];
    public ref float AngularVelocity(int body) => ref AngularVelocities[body];
    public ref float GravityScaleOf(int body) => ref GravityScale[body];
    public ref float LinearDampingOf(int body) => ref LinearDamping[body];
    public ref float AngularDampingOf(int body) => ref AngularDamping[body];
    public ref float FrictionOf(int body) => ref Friction[body];
    public ref float RestitutionOf(int body) => ref Restitution[body];
    public BodyType TypeOf(int body) => Types[body];
    /// <summary>The pose last written to the entity's transform (as it reads back), to tell a teleport from our own write.</summary>
    public ref Vector2 WrittenPosition(int body) => ref Written[body];
    public ref Rot WrittenRotation(int body) => ref WrittenRotations[body];

    private void Grow(int capacity)
    {
        Array.Resize(ref Entities, capacity); Array.Resize(ref Types, capacity); Array.Resize(ref Alive, capacity);
        Array.Resize(ref Positions, capacity); Array.Resize(ref Velocities, capacity); Array.Resize(ref DeltaPositions, capacity); Array.Resize(ref Written, capacity);
        Array.Resize(ref Rotations, capacity); Array.Resize(ref StartRotations, capacity); Array.Resize(ref WrittenRotations, capacity);
        Array.Resize(ref AngularVelocities, capacity); Array.Resize(ref InvMass, capacity); Array.Resize(ref InvInertia, capacity);
        Array.Resize(ref GravityScale, capacity); Array.Resize(ref LinearDamping, capacity); Array.Resize(ref AngularDamping, capacity);
        Array.Resize(ref Friction, capacity); Array.Resize(ref Restitution, capacity);
        Array.Resize(ref Shapes, capacity); Array.Resize(ref Bounds, capacity); Array.Resize(ref _sorted, capacity);
        _capacity = capacity;
    }

    // ---------------------------------------------------------------- step

    private static readonly ProfileMarker BroadMarker = ProfileMarker.Get("Physics2D.Broadphase", ProfileCategory.Engine);
    private static readonly ProfileMarker NarrowMarker = ProfileMarker.Get("Physics2D.Narrowphase", ProfileCategory.Engine);
    private static readonly ProfileMarker SolveMarker = ProfileMarker.Get("Physics2D.Solve", ProfileCategory.Engine);

    /// <summary>Advance by <paramref name="dt"/> seconds.</summary>
    public void Step(float dt)
    {
        if (dt <= 0) return;
        using (BroadMarker.Scope()) Broadphase(dt);
        using (SolveMarker.Scope()) Solve(dt);
    }

    /// <summary>Sort by min x (insertion sort: nearly sorted from the last step), sweep, and collide overlapping pairs.</summary>
    private void Broadphase(float dt)
    {
        for (int k = 0; k < _sortedCount; k++)
        {
            int i = _sorted[k];
            var b = Shapes[i].Bounds(Positions[i], Rotations[i]);
            // Grow by the motion this step, so speculative contacts find fast bodies before they pass through.
            var reach = Velocities[i] * dt;
            b.Min = Vector2.Min(b.Min, b.Min + reach) - new Vector2(Margin);
            b.Max = Vector2.Max(b.Max, b.Max + reach) + new Vector2(Margin);
            Bounds[i] = b;
        }
        for (int k = 1; k < _sortedCount; k++)
        {
            int i = _sorted[k], j = k - 1;
            float x = Bounds[i].Min.X;
            while (j >= 0 && (Bounds[_sorted[j]].Min.X > x || (Bounds[_sorted[j]].Min.X == x && _sorted[j] > i))) { _sorted[j + 1] = _sorted[j]; j--; }
            _sorted[j + 1] = i;
        }
        (_cache, _nextCache) = (_nextCache, _cache);
        (_cached, _nextCached) = (_nextCached, _cached);
        _cachedCount = _nextCachedCount;
        _nextCache.Clear();
        _nextCachedCount = 0;
        _contactCount = 0;
        using var narrow = NarrowMarker.Scope();
        for (int k = 0; k < _sortedCount; k++)
        {
            int a = _sorted[k];
            ref readonly var ba = ref Bounds[a];
            for (int l = k + 1; l < _sortedCount; l++)
            {
                int b = _sorted[l];
                ref readonly var bb = ref Bounds[b];
                if (bb.Min.X > ba.Max.X) break;
                if (bb.Min.Y > ba.Max.Y || ba.Min.Y > bb.Max.Y) continue;
                if (Types[a] != BodyType.Dynamic && Types[b] != BodyType.Dynamic) continue;
                // A pair is always ordered by index, so its key and its contact are the same whichever sorted first.
                int lo = Math.Min(a, b), hi = Math.Max(a, b);
                float margin = Speculative + SimMath.Length(Velocities[lo] - Velocities[hi]) * dt;
                Collision.Collide(Shapes[lo], Positions[lo], Rotations[lo], Shapes[hi], Positions[hi], Rotations[hi], margin, out var m);
                if (m.Count == 0) continue;
                AddContact(lo, hi, m);
            }
        }
    }

    private const float LinearSlop = 0.005f, Speculative = 4 * LinearSlop, Margin = 0.1f;

    private void AddContact(int a, int b, in Manifold m)
    {
        if (_contactCount == _contacts.Length) Array.Resize(ref _contacts, _contacts.Length * 2);
        ref var c = ref _contacts[_contactCount++];
        c = new Contact { A = a, B = b, Normal = m.Normal, Count = m.Count };
        c.Friction = SimMath.Sqrt(Friction[a] * Friction[b]);
        c.Restitution = Math.Max(Restitution[a], Restitution[b]);
        c.P0 = new ContactPoint { AnchorA = m.P0.AnchorA, AnchorB = m.P0.AnchorB, AdjustedSeparation = m.P0.Separation, Id = m.P0.Id };
        c.P1 = new ContactPoint { AnchorA = m.P1.AnchorA, AnchorB = m.P1.AnchorB, AdjustedSeparation = m.P1.Separation, Id = m.P1.Id };
        // Warm start from last step's impulses for the same features.
        ulong key = (ulong)(uint)a << 32 | (uint)b;
        if (_cache.TryGetValue(key, out int slot))
        {
            ref var old = ref _cached[slot];
            Match(ref c.P0, old); if (c.Count > 1) Match(ref c.P1, old);
        }
    }

    private static void Match(ref ContactPoint p, in CachedImpulse old)
    {
        if (p.Id == old.Id0) { p.NormalImpulse = old.Normal0; p.TangentImpulse = old.Tangent0; }
        else if (p.Id == old.Id1) { p.NormalImpulse = old.Normal1; p.TangentImpulse = old.Tangent1; }
    }

    private void Solve(float dt)
    {
        var s = Settings;
        int substeps = Math.Max(1, s.Substeps);
        float h = dt / substeps, invH = 1 / h;
        // Soft contact: a spring of the given stiffness and damping, expressed as bias and mass scales for the substep.
        float hertz = Math.Min(s.ContactHertz, 0.25f * invH);
        float omega = 2 * SimMath.Pi * hertz;
        float a1 = 2 * s.ContactDampingRatio + h * omega, a2 = h * omega * a1, a3 = 1 / (1 + a2);
        float biasRate = omega / a1, massScale = a2 * a3, impulseScale = a3;

        for (int i = 0; i < _count; i++) { DeltaPositions[i] = default; StartRotations[i] = Rotations[i]; }

        // Prepare: anchors are fixed for the step; separation is tracked through body motion.
        for (int k = 0; k < _contactCount; k++)
        {
            ref var c = ref _contacts[k];
            Prepare(ref c, ref c.P0);
            if (c.Count > 1) Prepare(ref c, ref c.P1);
        }

        for (int sub = 0; sub < substeps; sub++)
        {
            for (int i = 0; i < _count; i++)
            {
                if (!Alive[i] || Types[i] != BodyType.Dynamic) continue;
                var v = Velocities[i] + h * GravityScale[i] * s.Gravity;
                Velocities[i] = v / (1 + h * LinearDamping[i]);
                AngularVelocities[i] /= 1 + h * AngularDamping[i];
            }
            for (int k = 0; k < _contactCount; k++) WarmStart(ref _contacts[k]);
            for (int k = 0; k < _contactCount; k++) SolveContact(ref _contacts[k], invH, true, biasRate, massScale, impulseScale, s.MaxPushout);
            for (int i = 0; i < _count; i++)
            {
                if (!Alive[i] || Types[i] == BodyType.Static) continue;
                var dp = h * Velocities[i];
                Positions[i] += dp;
                DeltaPositions[i] += dp;
                Rotations[i] = Rot.Integrate(Rotations[i], h * AngularVelocities[i]);
            }
            for (int k = 0; k < _contactCount; k++) SolveContact(ref _contacts[k], invH, false, biasRate, massScale, impulseScale, s.MaxPushout);
        }

        for (int k = 0; k < _contactCount; k++) ApplyRestitution(ref _contacts[k], s.RestitutionThreshold);

        // Keep the impulses for warm starting next step.
        for (int k = 0; k < _contactCount; k++)
        {
            ref var c = ref _contacts[k];
            if (_nextCachedCount == _nextCached.Length) Array.Resize(ref _nextCached, _nextCached.Length * 2);
            _nextCached[_nextCachedCount] = new CachedImpulse
            {
                Id0 = c.P0.Id, Normal0 = c.P0.NormalImpulse, Tangent0 = c.P0.TangentImpulse,
                Id1 = c.Count > 1 ? c.P1.Id : (ushort)0xFFFF, Normal1 = c.P1.NormalImpulse, Tangent1 = c.P1.TangentImpulse,
            };
            _nextCache[(ulong)(uint)c.A << 32 | (uint)c.B] = _nextCachedCount++;
        }
    }

    private void Prepare(ref Contact c, ref ContactPoint p)
    {
        int a = c.A, b = c.B;
        float mA = InvMass[a], mB = InvMass[b], iA = InvInertia[a], iB = InvInertia[b];
        var n = c.Normal; var t = V.RightPerp(n);
        float rnA = V.Cross(p.AnchorA, n), rnB = V.Cross(p.AnchorB, n);
        float kNormal = mA + mB + iA * rnA * rnA + iB * rnB * rnB;
        p.NormalMass = kNormal > 0 ? 1 / kNormal : 0;
        float rtA = V.Cross(p.AnchorA, t), rtB = V.Cross(p.AnchorB, t);
        float kTangent = mA + mB + iA * rtA * rtA + iB * rtB * rtB;
        p.TangentMass = kTangent > 0 ? 1 / kTangent : 0;
        // Separation now = Δ(pB + rB) − Δ(pA + rA) along n, plus this constant.
        p.AdjustedSeparation -= V.Dot(p.AnchorB - p.AnchorA, n);
        var dv = Velocities[b] + V.Cross(AngularVelocities[b], p.AnchorB) - Velocities[a] - V.Cross(AngularVelocities[a], p.AnchorA);
        p.RelativeVelocity = V.Dot(n, dv);
        p.MaxNormalImpulse = 0;
    }

    private void WarmStart(ref Contact c)
    {
        var t = V.RightPerp(c.Normal);
        Apply(ref c, c.P0, c.P0.NormalImpulse * c.Normal + c.P0.TangentImpulse * t);
        if (c.Count > 1) Apply(ref c, c.P1, c.P1.NormalImpulse * c.Normal + c.P1.TangentImpulse * t);
    }

    private void Apply(ref Contact c, in ContactPoint p, Vector2 impulse)
    {
        int a = c.A, b = c.B;
        Velocities[a] -= InvMass[a] * impulse; AngularVelocities[a] -= InvInertia[a] * V.Cross(p.AnchorA, impulse);
        Velocities[b] += InvMass[b] * impulse; AngularVelocities[b] += InvInertia[b] * V.Cross(p.AnchorB, impulse);
    }

    private void SolveContact(ref Contact c, float invH, bool useBias, float biasRate, float massScale, float impulseScale, float maxPushout)
    {
        int a = c.A, b = c.B;
        var n = c.Normal;
        var dp = DeltaPositions[b] - DeltaPositions[a];
        var qA = Rot.InvMul(StartRotations[a], Rotations[a]);
        var qB = Rot.InvMul(StartRotations[b], Rotations[b]);
        for (int k = 0; k < c.Count; k++)
        {
            ref var p = ref k == 0 ? ref c.P0 : ref c.P1;
            var d = dp + qB.Rotate(p.AnchorB) - qA.Rotate(p.AnchorA);
            float separation = V.Dot(d, n) + p.AdjustedSeparation;
            float bias = 0, ms = 1, isc = 0;
            if (separation > 0) bias = separation * invH; // speculative: close the gap this substep, no further
            else if (useBias) { bias = Math.Max(biasRate * separation, -maxPushout); ms = massScale; isc = impulseScale; }
            var dv = Velocities[b] + V.Cross(AngularVelocities[b], p.AnchorB) - Velocities[a] - V.Cross(AngularVelocities[a], p.AnchorA);
            float vn = V.Dot(dv, n);
            float impulse = -p.NormalMass * ms * (vn + bias) - isc * p.NormalImpulse;
            float total = Math.Max(p.NormalImpulse + impulse, 0);
            impulse = total - p.NormalImpulse;
            p.NormalImpulse = total;
            p.MaxNormalImpulse = Math.Max(p.MaxNormalImpulse, impulse);
            Apply(ref c, p, impulse * n);
        }
        var t = V.RightPerp(n);
        for (int k = 0; k < c.Count; k++)
        {
            ref var p = ref k == 0 ? ref c.P0 : ref c.P1;
            var dv = Velocities[b] + V.Cross(AngularVelocities[b], p.AnchorB) - Velocities[a] - V.Cross(AngularVelocities[a], p.AnchorA);
            float vt = V.Dot(dv, t);
            float impulse = -p.TangentMass * vt;
            float limit = c.Friction * p.NormalImpulse;
            float total = Math.Clamp(p.TangentImpulse + impulse, -limit, limit);
            impulse = total - p.TangentImpulse;
            p.TangentImpulse = total;
            Apply(ref c, p, impulse * t);
        }
    }

    private void ApplyRestitution(ref Contact c, float threshold)
    {
        if (c.Restitution == 0) return;
        int a = c.A, b = c.B;
        for (int k = 0; k < c.Count; k++)
        {
            ref var p = ref k == 0 ? ref c.P0 : ref c.P1;
            if (p.RelativeVelocity > -threshold || p.MaxNormalImpulse == 0) continue;
            var dv = Velocities[b] + V.Cross(AngularVelocities[b], p.AnchorB) - Velocities[a] - V.Cross(AngularVelocities[a], p.AnchorA);
            float vn = V.Dot(dv, c.Normal);
            float impulse = -p.NormalMass * (vn + c.Restitution * p.RelativeVelocity);
            float total = Math.Max(p.NormalImpulse + impulse, 0);
            impulse = total - p.NormalImpulse;
            p.NormalImpulse = total;
            Apply(ref c, p, impulse * c.Normal);
        }
    }

    // ---------------------------------------------------------------- queries

    /// <summary>The closest body hit by the segment from <paramref name="origin"/> to <paramref name="origin"/> + <paramref name="translation"/>.</summary>
    public bool RayCast(Vector2 origin, Vector2 translation, out RayHit2D hit)
    {
        hit = default;
        hit.Fraction = 1;
        bool found = false;
        for (int k = 0; k < _sortedCount; k++)
        {
            int i = _sorted[k];
            if (RayShape(Shapes[i], Positions[i], Rotations[i], origin, translation, hit.Fraction, out float f, out var normal))
            {
                found = true;
                hit = new RayHit2D { Entity = Entities[i], Fraction = f, Point = origin + translation * f, Normal = normal };
            }
        }
        return found;
    }

    /// <summary>Entities whose shape bounds overlap the box; writes up to <c>results.Length</c> and returns how many matched.</summary>
    public int OverlapBox(Vector2 min, Vector2 max, Span<Entity> results)
    {
        var box = new Aabb { Min = min, Max = max };
        int n = 0;
        for (int k = 0; k < _sortedCount; k++)
        {
            int i = _sorted[k];
            if (!Shapes[i].Bounds(Positions[i], Rotations[i]).Overlaps(box)) continue;
            if (n < results.Length) results[n] = Entities[i];
            n++;
        }
        return n;
    }

    private static bool RayShape(in Shape s, Vector2 p, Rot q, Vector2 origin, Vector2 d, float maxFraction, out float fraction, out Vector2 normal)
    {
        fraction = 0; normal = default;
        if (s.Kind == ShapeKind.Circle)
        {
            var c = p + q.Rotate(s.Center);
            var m = origin - c;
            float a = V.Dot(d, d), b = V.Dot(m, d), cc = V.Dot(m, m) - s.Radius * s.Radius;
            if (a == 0) return false;
            float disc = b * b - a * cc;
            if (disc < 0) return false;
            float t = (-b - SimMath.Sqrt(disc)) / a;
            if (t < 0 || t > maxFraction) return false;
            fraction = t;
            normal = SimMath.Normalize(m + d * t);
            return true;
        }
        // Polygon: clip the ray against every face plane (in the polygon's frame).
        var o = q.InvRotate(origin - p); var dir = q.InvRotate(d);
        float lower = 0, upper = maxFraction;
        int index = -1;
        for (int i = 0; i < s.Count; i++)
        {
            var n = s.Normal(i);
            float numerator = V.Dot(n, s.Vertex(i) - o), denominator = V.Dot(n, dir);
            if (denominator == 0) { if (numerator < 0) return false; continue; }
            float t = numerator / denominator;
            if (denominator < 0 && t > lower) { lower = t; index = i; }
            else if (denominator > 0 && t < upper) upper = t;
            if (upper < lower) return false;
        }
        if (index < 0) return false;
        fraction = lower;
        normal = q.Rotate(s.Normal(index));
        return true;
    }

    // ---------------------------------------------------------------- snapshots

    /// <summary>A deep copy (play-mode checkpoints, rollback).</summary>
    public PhysicsWorld2D Clone()
    {
        var w = (PhysicsWorld2D)MemberwiseClone();
        w.Entities = (Entity[])Entities.Clone(); w.Types = (BodyType[])Types.Clone(); w.Alive = (bool[])Alive.Clone();
        w.Positions = (Vector2[])Positions.Clone(); w.Velocities = (Vector2[])Velocities.Clone(); w.DeltaPositions = (Vector2[])DeltaPositions.Clone();
        w.Rotations = (Rot[])Rotations.Clone(); w.StartRotations = (Rot[])StartRotations.Clone();
        w.Written = (Vector2[])Written.Clone(); w.WrittenRotations = (Rot[])WrittenRotations.Clone();
        w.AngularVelocities = (float[])AngularVelocities.Clone(); w.InvMass = (float[])InvMass.Clone(); w.InvInertia = (float[])InvInertia.Clone();
        w.GravityScale = (float[])GravityScale.Clone(); w.LinearDamping = (float[])LinearDamping.Clone(); w.AngularDamping = (float[])AngularDamping.Clone();
        w.Friction = (float[])Friction.Clone(); w.Restitution = (float[])Restitution.Clone();
        w.Shapes = (Shape[])Shapes.Clone(); w.Bounds = (Aabb[])Bounds.Clone();
        w._free = (int[])_free.Clone(); w._sorted = (int[])_sorted.Clone();
        w._contacts = (Contact[])_contacts.Clone();
        w._cache = new Dictionary<ulong, int>(_cache); w._nextCache = new Dictionary<ulong, int>(_nextCache);
        w._cached = (CachedImpulse[])_cached.Clone(); w._nextCached = (CachedImpulse[])_nextCached.Clone();
        return w;
    }

    /// <summary>Bodies, for tools: index, entity, type, position and rotation.</summary>
    public int BodyCapacity => _count;
}
