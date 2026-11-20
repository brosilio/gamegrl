using System.Numerics;

namespace Gamegrl.Physics2D;

public enum JointType2D : byte
{
    /// <summary>Keeps two anchors at a distance: rigid, a spring (<see cref="JointDef2D.Hertz"/> above 0), and/or between a minimum and maximum.</summary>
    Distance,
    /// <summary>Pins two anchors together; the bodies turn freely about it, within optional angle limits, driven by an optional motor.</summary>
    Revolute,
    /// <summary>Lets body B slide along an axis fixed in body A, without turning; optional limits and motor.</summary>
    Prismatic,
    /// <summary>Glues two bodies together (soft when the hertz values are above 0).</summary>
    Weld,
    /// <summary>Pulls body B's anchor toward a target point with a spring, up to a maximum force (dragging with the mouse).</summary>
    Target,
    /// <summary>No constraint: only stops the two bodies colliding (and ties them into one island).</summary>
    Filter,
}

/// <summary>A joint's settings. Anchors are in each body's own frame (for a target joint, the target is in world space).</summary>
public struct JointDef2D
{
    public JointType2D Type;
    public Vector2 AnchorA, AnchorB;
    /// <summary>Prismatic: the slide axis in body A's frame.</summary>
    public Vector2 Axis;
    /// <summary>Distance: rest length, and the range it may stretch over (equal to <see cref="Length"/> for none).</summary>
    public float Length, MinLength, MaxLength;
    /// <summary>Distance: spring frequency (0 = rigid). Weld: linear softness. Target: spring frequency.</summary>
    public float Hertz, DampingRatio;
    /// <summary>Weld: angular softness (0 = rigid).</summary>
    public float AngularHertz;
    public bool EnableLimit;
    /// <summary>Revolute: angle range in radians. Prismatic: translation range along the axis.</summary>
    public float Lower, Upper;
    public bool EnableMotor;
    public float MotorSpeed, MaxMotorForce;
    /// <summary>Target: where body B's anchor is pulled, and the most force the pull may use.</summary>
    public Vector2 Target;
    public float MaxForce;
    public bool CollideConnected;
}

public sealed partial class PhysicsWorld2D
{
    private struct Joint
    {
        public bool Alive;
        public Entity Owner;
        public int A, B; // B is always a body; A is -1 for a target joint
        public JointDef2D Def;
        public float ReferenceAngle;
        // Per step
        public Vector2 RA, RB, DeltaCenter, AxisA;
        public float DeltaAngle, MA, MB, IA, IB, AxialMass;
        public Softness Spring, AngularSpring;
        // Accumulated impulses (warm started across substeps and steps)
        public Vector2 Linear;
        public float Angular, Axial, Lower, Upper, Motor, Perp;
        // Links: place in each body's joint list (keys are joint × 2 + side) and in its island.
        public int PrevA, NextA, PrevB, NextB, Island, IslandIndex;
    }

    private Joint[] _joints = new Joint[16];
    private int _jointHigh;
    private int[] _freeJoints = new int[8];
    private int _freeJointCount;
    private int[] _jointHead = [], _jointDegree = []; // per body: first joint key, and how many

    public int CreateJoint(Entity owner, int bodyA, int bodyB, in JointDef2D def)
    {
        int j;
        if (_freeJointCount > 0) j = _freeJoints[--_freeJointCount];
        else
        {
            if (_jointHigh == _joints.Length) Array.Resize(ref _joints, _joints.Length * 2);
            j = _jointHigh++;
        }
        if (def.Type == JointType2D.Target) bodyA = -1;
        _joints[j] = new Joint { Alive = true, Owner = owner, A = bodyA, B = bodyB, Def = def, Island = -1, IslandIndex = -1 };
        _joints[j].ReferenceAngle = bodyA >= 0 ? RelativeAngle(Rotation[bodyA], Rotation[bodyB]) : 0;
        AddJointEdges(j);
        if (bodyA >= 0) Wake(bodyA);
        Wake(bodyB);
        LinkJoint(j);
        if (bodyA >= 0 && !def.CollideConnected) DestroyContactsBetween(bodyA, bodyB);
        return j;
    }

    public void DestroyJoint(int j)
    {
        if (!IsJointAlive(j)) return;
        if (_joints[j].A >= 0) Wake(_joints[j].A);
        Wake(_joints[j].B);
        UnlinkJoint(j);
        RemoveJointEdges(j);
        bool collide = _joints[j].Def.CollideConnected;
        int a = _joints[j].A, b = _joints[j].B;
        _joints[j] = default;
        if (_freeJointCount == _freeJoints.Length) Array.Resize(ref _freeJoints, _freeJoints.Length * 2);
        _freeJoints[_freeJointCount++] = j;
        if (a >= 0 && !collide) RefindPairs(a, b);
    }

    public bool IsJointAlive(int j) => (uint)j < (uint)_jointHigh && _joints[j].Alive;
    public Entity OwnerOfJoint(int j) => _joints[j].Owner;
    public int JointCapacity => _jointHigh;
    public (int A, int B) JointBodies(int j) => (_joints[j].A, _joints[j].B);

    /// <summary>Change a joint's settings in place (keeps its accumulated impulses).</summary>
    public void UpdateJoint(int j, in JointDef2D def)
    {
        ref var joint = ref _joints[j];
        bool collide = joint.Def.CollideConnected;
        joint.Def = def;
        if (collide != def.CollideConnected && joint.A >= 0)
        {
            if (def.CollideConnected) RefindPairs(joint.A, joint.B);
            else DestroyContactsBetween(joint.A, joint.B);
        }
        if (joint.Def.Type == JointType2D.Target) Wake(joint.B);
    }

    /// <summary>The joint's current angle (revolute) or translation along its axis (prismatic).</summary>
    public float JointValue(int j)
    {
        ref readonly var joint = ref _joints[j];
        if (joint.A < 0) return 0;
        if (joint.Def.Type == JointType2D.Prismatic)
        {
            var pa = GetPosition(joint.A) + Rotation[joint.A].Rotate(joint.Def.AnchorA);
            var pb = GetPosition(joint.B) + Rotation[joint.B].Rotate(joint.Def.AnchorB);
            return V.Dot(pb - pa, Rotation[joint.A].Rotate(SimMath.Normalize(joint.Def.Axis)));
        }
        return RelativeAngle(Rotation[joint.A], Rotation[joint.B]) - joint.ReferenceAngle;
    }

    private void RemoveJointsOf(int body)
    {
        while (_jointHead[body] >= 0) DestroyJoint(_jointHead[body] >> 1);
    }

    // ---------------------------------------------------------------- per-body joint lists

    private void AddJointEdges(int j)
    {
        ref var joint = ref _joints[j];
        joint.PrevA = joint.NextA = joint.PrevB = joint.NextB = -1;
        if (joint.A >= 0) PushJointEdge(joint.A, j << 1);
        PushJointEdge(joint.B, j << 1 | 1);
    }

    private void PushJointEdge(int body, int key)
    {
        int head = _jointHead[body];
        if ((key & 1) == 0) _joints[key >> 1].NextA = head; else _joints[key >> 1].NextB = head;
        if (head >= 0) SetJointPrev(head, key);
        _jointHead[body] = key;
        _jointDegree[body]++;
    }

    private void RemoveJointEdges(int j)
    {
        ref var joint = ref _joints[j];
        if (joint.A >= 0) UnchainJoint(joint.PrevA, joint.NextA, joint.A);
        UnchainJoint(joint.PrevB, joint.NextB, joint.B);
    }

    private void UnchainJoint(int prev, int next, int body)
    {
        if (prev >= 0) { if ((prev & 1) == 0) _joints[prev >> 1].NextA = next; else _joints[prev >> 1].NextB = next; }
        else _jointHead[body] = next;
        if (next >= 0) SetJointPrev(next, prev);
        _jointDegree[body]--;
    }

    private void SetJointPrev(int key, int prev)
    {
        if ((key & 1) == 0) _joints[key >> 1].PrevA = prev; else _joints[key >> 1].PrevB = prev;
    }

    internal int NextJointKey(int key) => (key & 1) == 0 ? _joints[key >> 1].NextA : _joints[key >> 1].NextB;

    /// <summary>Whether a joint between the two bodies turns their collision off (read-only: safe in the parallel pair queries).</summary>
    private bool JointPreventsCollision(int a, int b)
    {
        if (_jointDegree[b] < _jointDegree[a]) (a, b) = (b, a);
        for (int key = _jointHead[a]; key >= 0; key = NextJointKey(key))
        {
            ref readonly var joint = ref _joints[key >> 1];
            int other = (key & 1) == 0 ? joint.B : joint.A;
            if (other == b && !joint.Def.CollideConnected) return true;
        }
        return false;
    }

    /// <summary>Let the broadphase find the two bodies' pairs again (their collision was turned back on).</summary>
    private void RefindPairs(int a, int b)
    {
        int body = _shapeCountOf(a) <= _shapeCountOf(b) ? a : b;
        for (int s = FirstShape[body]; s >= 0; s = NextShape[s]) MarkMoved(s);
    }

    private int _shapeCountOf(int b) { int n = 0; for (int s = FirstShape[b]; s >= 0; s = NextShape[s]) n++; return n; }

    private void CloneJoints(PhysicsWorld2D w)
    {
        w._joints = (Joint[])_joints.Clone();
        w._freeJoints = (int[])_freeJoints.Clone();
    }

    internal static float RelativeAngle(Rot a, Rot b) => SimMath.Atan2(a.C * b.S - a.S * b.C, a.C * b.C + a.S * b.S);

    private bool Active(in Joint j) => j.Alive && j.Def.Type != JointType2D.Filter && (Awake[j.B] || (j.A >= 0 && Awake[j.A])) && (Types[j.B] == BodyType.Dynamic || (j.A >= 0 && Types[j.A] == BodyType.Dynamic));

    private BodyState Ground => new() { DQ = Rot.Identity };

    // ---------------------------------------------------------------- solving (serial: joints are few and share bodies)

    private void PrepareJoints(float h)
    {
        for (int i = 0; i < _jointHigh; i++)
        {
            ref var j = ref _joints[i];
            if (!Active(j)) continue;
            int a = j.A, b = j.B;
            j.MA = a >= 0 && Awake[a] ? InvMass[a] : 0; j.IA = a >= 0 && Awake[a] ? InvInertia[a] : 0;
            j.MB = Awake[b] ? InvMass[b] : 0; j.IB = Awake[b] ? InvInertia[b] : 0;
            // Anchors relative to the centers of mass, world-oriented.
            j.RA = a >= 0 ? Rotation[a].Rotate(j.Def.AnchorA - LocalCenter[a]) : default;
            j.RB = Rotation[b].Rotate(j.Def.AnchorB - LocalCenter[b]);
            j.DeltaCenter = Center[b] - (a >= 0 ? Center[a] : j.Def.Target);
            j.DeltaAngle = a >= 0 ? RelativeAngle(Rotation[a], Rotation[b]) - j.ReferenceAngle : 0;
            j.AxisA = a >= 0 ? Rotation[a].Rotate(SimMath.Normalize(j.Def.Axis == default ? Vector2.UnitX : j.Def.Axis)) : default;
            float k = j.IA + j.IB;
            j.AxialMass = k > 0 ? 1 / k : 0;
            j.Spring = Softness.Make(j.Def.Hertz, j.Def.DampingRatio, h);
            j.AngularSpring = Softness.Make(j.Def.AngularHertz, j.Def.DampingRatio, h);
            if (!j.Def.EnableLimit) { j.Lower = 0; j.Upper = 0; }
            if (!j.Def.EnableMotor) j.Motor = 0;
        }
    }

    private void WarmStartJoints()
    {
        for (int i = 0; i < _jointHigh; i++)
        {
            ref var j = ref _joints[i];
            if (!Active(j)) continue;
            ref var sb = ref States[j.B];
            var sa = j.A >= 0 ? States[j.A] : Ground;
            var rA = sa.DQ.Rotate(j.RA); var rB = sb.DQ.Rotate(j.RB);
            Vector2 linear = j.Linear;
            float angular = j.Angular + j.Motor + j.Lower - j.Upper;
            switch (j.Def.Type)
            {
                case JointType2D.Distance:
                {
                    var d = AxisNow(j, sa, sb, rA, rB, out _);
                    linear = (j.Axial + j.Lower - j.Upper) * d;
                    angular = 0;
                    break;
                }
                case JointType2D.Prismatic:
                {
                    // Same lever arms as the solver uses.
                    var d = (sb.DP - sa.DP) + j.DeltaCenter + (rB - rA);
                    var axis = sa.DQ.Rotate(j.AxisA); var perp = V.RightPerp(axis) * -1;
                    ApplyAxial(ref j, ref sa, ref sb, axis, V.Cross(d + rA, axis), V.Cross(rB, axis), j.Motor + j.Lower - j.Upper);
                    ApplyAxial(ref j, ref sa, ref sb, perp, V.Cross(d + rA, perp), V.Cross(rB, perp), j.Perp);
                    sa.W -= j.IA * j.Angular; sb.W += j.IB * j.Angular;
                    if (j.A >= 0) States[j.A] = sa;
                    continue;
                }
                case JointType2D.Target: angular = j.Angular; break;
            }
            ApplyJoint(ref j, ref sa, ref sb, rA, rB, linear, angular);
            if (j.A >= 0) States[j.A] = sa;
        }
    }

    private void ApplyJoint(ref Joint j, ref BodyState sa, ref BodyState sb, Vector2 rA, Vector2 rB, Vector2 linear, float angular)
    {
        sa.V -= j.MA * linear; sa.W -= j.IA * (V.Cross(rA, linear) + angular);
        sb.V += j.MB * linear; sb.W += j.IB * (V.Cross(rB, linear) + angular);
    }

    private static Vector2 AxisNow(in Joint j, in BodyState sa, in BodyState sb, Vector2 rA, Vector2 rB, out float length)
    {
        var d = (sb.DP - sa.DP) + j.DeltaCenter + (rB - rA);
        length = SimMath.Length(d);
        return length > 1e-6f ? d / length : Vector2.UnitX;
    }

    private void SolveJoints(float h, float invH, bool useBias)
    {
        for (int i = 0; i < _jointHigh; i++)
        {
            ref var j = ref _joints[i];
            if (!Active(j)) continue;
            ref var sb = ref States[j.B];
            var sa = j.A >= 0 ? States[j.A] : Ground;
            switch (j.Def.Type)
            {
                case JointType2D.Distance: SolveDistance(ref j, ref sa, ref sb, h, invH, useBias); break;
                case JointType2D.Revolute: SolveRevolute(ref j, ref sa, ref sb, h, invH, useBias); break;
                case JointType2D.Prismatic: SolvePrismatic(ref j, ref sa, ref sb, h, invH, useBias); break;
                case JointType2D.Weld: SolveWeld(ref j, ref sa, ref sb, invH, useBias); break;
                case JointType2D.Target: SolveTarget(ref j, ref sb, h, useBias); break;
                case JointType2D.Filter: break;
            }
            if (j.A >= 0) States[j.A] = sa;
        }
    }

    private void StoreJoints() { }

    // A one-dimensional bound on C ≥ 0 (limits): speculative when apart, soft when violated.
    private float Unilateral(float c, float cdot, float mass, ref float accumulated, float invH, bool useBias)
    {
        float bias = 0, massScale = 1, impulseScale = 0;
        if (c > 0) bias = c * invH;
        else if (useBias) { bias = Math.Max(_jointSoft.BiasRate * c, -Settings.MaxPushout); massScale = _jointSoft.MassScale; impulseScale = _jointSoft.ImpulseScale; }
        float impulse = -mass * massScale * (cdot + bias) - impulseScale * accumulated;
        float total = Math.Max(accumulated + impulse, 0);
        impulse = total - accumulated;
        accumulated = total;
        return impulse;
    }

    private void SolveDistance(ref Joint j, ref BodyState sa, ref BodyState sb, float h, float invH, bool useBias)
    {
        var rA = sa.DQ.Rotate(j.RA); var rB = sb.DQ.Rotate(j.RB);
        var axis = AxisNow(j, sa, sb, rA, rB, out float length);
        float crA = V.Cross(rA, axis), crB = V.Cross(rB, axis);
        float k = j.MA + j.MB + j.IA * crA * crA + j.IB * crB * crB;
        float mass = k > 0 ? 1 / k : 0;
        static float Rate(in BodyState sa, in BodyState sb, Vector2 axis, Vector2 rA, Vector2 rB) => V.Dot(axis, sb.V + V.Cross(sb.W, rB) - sa.V - V.Cross(sa.W, rA));
        var def = j.Def;
        bool ranged = def.MinLength < def.MaxLength;
        if (def.Hertz > 0)
        {
            // A spring toward the rest length (always, so a spring with a range still pulls toward its middle).
            float c = length - def.Length;
            float impulse = -mass * j.Spring.MassScale * (Rate(sa, sb, axis, rA, rB) + j.Spring.BiasRate * c) - j.Spring.ImpulseScale * j.Axial;
            j.Axial += impulse;
            ApplyJoint(ref j, ref sa, ref sb, rA, rB, impulse * axis, 0);
        }
        else if (!ranged)
        {
            float c = length - def.Length;
            float bias = 0, massScale = 1, impulseScale = 0;
            if (useBias) { bias = _jointSoft.BiasRate * c; massScale = _jointSoft.MassScale; impulseScale = _jointSoft.ImpulseScale; }
            float impulse = -mass * massScale * (Rate(sa, sb, axis, rA, rB) + bias) - impulseScale * j.Axial;
            j.Axial += impulse;
            ApplyJoint(ref j, ref sa, ref sb, rA, rB, impulse * axis, 0);
        }
        if (ranged)
        {
            float lower = Unilateral(length - def.MinLength, Rate(sa, sb, axis, rA, rB), mass, ref j.Lower, invH, useBias);
            ApplyJoint(ref j, ref sa, ref sb, rA, rB, lower * axis, 0);
            float upper = Unilateral(def.MaxLength - length, -Rate(sa, sb, axis, rA, rB), mass, ref j.Upper, invH, useBias);
            ApplyJoint(ref j, ref sa, ref sb, rA, rB, -upper * axis, 0);
        }
    }

    /// <summary>Solve a 2×2 point constraint pulling rA and rB together, with the given extra separation.</summary>
    private void PointConstraint(ref Joint j, ref BodyState sa, ref BodyState sb, Vector2 rA, Vector2 rB, Vector2 separation, Softness soft, bool useBias, bool spring = false)
    {
        var cdot = sb.V + V.Cross(sb.W, rB) - sa.V - V.Cross(sa.W, rA);
        var bias = Vector2.Zero; float massScale = 1, impulseScale = 0;
        // A rigid constraint corrects position only in biased iterations; a spring always pulls.
        if (useBias || spring) { bias = soft.BiasRate * separation; massScale = soft.MassScale; impulseScale = soft.ImpulseScale; }
        float k11 = j.MA + j.MB + rA.Y * rA.Y * j.IA + rB.Y * rB.Y * j.IB;
        float k12 = -rA.Y * rA.X * j.IA - rB.Y * rB.X * j.IB;
        float k22 = j.MA + j.MB + rA.X * rA.X * j.IA + rB.X * rB.X * j.IB;
        float det = k11 * k22 - k12 * k12;
        if (det != 0) det = 1 / det;
        var rhs = cdot + bias;
        var b = new Vector2(det * (k22 * rhs.X - k12 * rhs.Y), det * (k11 * rhs.Y - k12 * rhs.X));
        var impulse = -massScale * b - impulseScale * j.Linear;
        j.Linear += impulse;
        ApplyJoint(ref j, ref sa, ref sb, rA, rB, impulse, 0);
    }

    private void AngleLimitsAndMotor(ref Joint j, ref BodyState sa, ref BodyState sb, float h, float invH, bool useBias, float angle)
    {
        var def = j.Def;
        if (def.EnableMotor)
        {
            float cdot = sb.W - sa.W - def.MotorSpeed;
            float impulse = -j.AxialMass * cdot, max = def.MaxMotorForce * h;
            float total = Math.Clamp(j.Motor + impulse, -max, max);
            impulse = total - j.Motor;
            j.Motor = total;
            sa.W -= j.IA * impulse; sb.W += j.IB * impulse;
        }
        if (def.EnableLimit)
        {
            float lower = Unilateral(angle - def.Lower, sb.W - sa.W, j.AxialMass, ref j.Lower, invH, useBias);
            sa.W -= j.IA * lower; sb.W += j.IB * lower;
            float upper = Unilateral(def.Upper - angle, sa.W - sb.W, j.AxialMass, ref j.Upper, invH, useBias);
            sa.W += j.IA * upper; sb.W -= j.IB * upper;
        }
    }

    private float AngleNow(in Joint j, in BodyState sa, in BodyState sb) => j.DeltaAngle + RelativeAngle(sa.DQ, sb.DQ);

    private void SolveRevolute(ref Joint j, ref BodyState sa, ref BodyState sb, float h, float invH, bool useBias)
    {
        AngleLimitsAndMotor(ref j, ref sa, ref sb, h, invH, useBias, AngleNow(j, sa, sb));
        var rA = sa.DQ.Rotate(j.RA); var rB = sb.DQ.Rotate(j.RB);
        var separation = (sb.DP - sa.DP) + (rB - rA) + j.DeltaCenter;
        PointConstraint(ref j, ref sa, ref sb, rA, rB, separation, _jointSoft, useBias);
    }

    private void SolveWeld(ref Joint j, ref BodyState sa, ref BodyState sb, float invH, bool useBias)
    {
        // Angular part, rigid or soft.
        var angularSoft = j.Def.AngularHertz > 0 ? j.AngularSpring : _jointSoft;
        float c = AngleNow(j, sa, sb);
        float bias = 0, massScale = 1, impulseScale = 0;
        if (useBias || j.Def.AngularHertz > 0) { bias = angularSoft.BiasRate * c; massScale = angularSoft.MassScale; impulseScale = angularSoft.ImpulseScale; }
        float impulse = -j.AxialMass * massScale * (sb.W - sa.W + bias) - impulseScale * j.Angular;
        j.Angular += impulse;
        sa.W -= j.IA * impulse; sb.W += j.IB * impulse;
        var rA = sa.DQ.Rotate(j.RA); var rB = sb.DQ.Rotate(j.RB);
        var separation = (sb.DP - sa.DP) + (rB - rA) + j.DeltaCenter;
        PointConstraint(ref j, ref sa, ref sb, rA, rB, separation, j.Def.Hertz > 0 ? j.Spring : _jointSoft, useBias, spring: j.Def.Hertz > 0);
    }

    private void SolvePrismatic(ref Joint j, ref BodyState sa, ref BodyState sb, float h, float invH, bool useBias)
    {
        var rA = sa.DQ.Rotate(j.RA); var rB = sb.DQ.Rotate(j.RB);
        var d = (sb.DP - sa.DP) + j.DeltaCenter + (rB - rA);
        var axis = sa.DQ.Rotate(j.AxisA);
        float translation = V.Dot(axis, d);
        // Motor and limits along the axis (lever arms include the offset d, as the bodies separate).
        float a1 = V.Cross(d + rA, axis), a2 = V.Cross(rB, axis);
        float k = j.MA + j.MB + j.IA * a1 * a1 + j.IB * a2 * a2;
        float axialMass = k > 0 ? 1 / k : 0;
        static float Rate(in BodyState sa, in BodyState sb, Vector2 axis, float a1, float a2) => V.Dot(axis, sb.V - sa.V) + a2 * sb.W - a1 * sa.W;
        var def = j.Def;
        if (def.EnableMotor)
        {
            float impulse = -axialMass * (Rate(sa, sb, axis, a1, a2) - def.MotorSpeed), max = def.MaxMotorForce * h;
            float total = Math.Clamp(j.Motor + impulse, -max, max);
            impulse = total - j.Motor;
            j.Motor = total;
            ApplyAxial(ref j, ref sa, ref sb, axis, a1, a2, impulse);
        }
        if (def.EnableLimit)
        {
            float lower = Unilateral(translation - def.Lower, Rate(sa, sb, axis, a1, a2), axialMass, ref j.Lower, invH, useBias);
            ApplyAxial(ref j, ref sa, ref sb, axis, a1, a2, lower);
            float upper = Unilateral(def.Upper - translation, -Rate(sa, sb, axis, a1, a2), axialMass, ref j.Upper, invH, useBias);
            ApplyAxial(ref j, ref sa, ref sb, axis, a1, a2, -upper);
        }
        // No turning.
        {
            float c = AngleNow(j, sa, sb);
            float bias = 0, massScale = 1, impulseScale = 0;
            if (useBias) { bias = _jointSoft.BiasRate * c; massScale = _jointSoft.MassScale; impulseScale = _jointSoft.ImpulseScale; }
            float impulse = -j.AxialMass * massScale * (sb.W - sa.W + bias) - impulseScale * j.Angular;
            j.Angular += impulse;
            sa.W -= j.IA * impulse; sb.W += j.IB * impulse;
        }
        // No sliding off the axis.
        {
            var perp = V.RightPerp(axis) * -1; // left perpendicular
            float s1 = V.Cross(d + rA, perp), s2 = V.Cross(rB, perp);
            float kp = j.MA + j.MB + j.IA * s1 * s1 + j.IB * s2 * s2;
            float mass = kp > 0 ? 1 / kp : 0;
            float c = V.Dot(perp, d);
            float cdot = V.Dot(perp, sb.V - sa.V) + s2 * sb.W - s1 * sa.W;
            float bias = 0, massScale = 1, impulseScale = 0;
            if (useBias) { bias = _jointSoft.BiasRate * c; massScale = _jointSoft.MassScale; impulseScale = _jointSoft.ImpulseScale; }
            float impulse = -mass * massScale * (cdot + bias) - impulseScale * j.Perp;
            j.Perp += impulse;
            var p = impulse * perp;
            sa.V -= j.MA * p; sa.W -= j.IA * impulse * s1;
            sb.V += j.MB * p; sb.W += j.IB * impulse * s2;
        }
    }

    private static void ApplyAxial(ref Joint j, ref BodyState sa, ref BodyState sb, Vector2 axis, float a1, float a2, float impulse)
    {
        var p = impulse * axis;
        sa.V -= j.MA * p; sa.W -= j.IA * impulse * a1;
        sb.V += j.MB * p; sb.W += j.IB * impulse * a2;
    }

    private void SolveTarget(ref Joint j, ref BodyState sb, float h, bool useBias)
    {
        // Angular damping keeps a dragged body from spinning forever.
        float angularMass = j.IB > 0 ? 1 / j.IB : 0;
        float angularImpulse = -0.1f * angularMass * sb.W;
        sb.W += j.IB * angularImpulse;
        var rB = sb.DQ.Rotate(j.RB);
        var separation = sb.DP + rB + j.DeltaCenter;
        var soft = j.Def.Hertz > 0 ? j.Spring : Softness.Make(5, 0.7f, h);
        var cdot = sb.V + V.Cross(sb.W, rB);
        float k11 = j.MB + rB.Y * rB.Y * j.IB, k12 = -rB.Y * rB.X * j.IB, k22 = j.MB + rB.X * rB.X * j.IB;
        float det = k11 * k22 - k12 * k12;
        if (det != 0) det = 1 / det;
        var rhs = cdot + soft.BiasRate * separation;
        var b = new Vector2(det * (k22 * rhs.X - k12 * rhs.Y), det * (k11 * rhs.Y - k12 * rhs.X));
        var old = j.Linear;
        var total = old - soft.MassScale * b - soft.ImpulseScale * old;
        float max = j.Def.MaxForce * h;
        if (max > 0 && SimMath.LengthSquared(total) > max * max) total = SimMath.Normalize(total) * max;
        var impulse = total - old;
        j.Linear = total;
        sb.V += j.MB * impulse;
        sb.W += j.IB * V.Cross(rB, impulse);
    }
}
