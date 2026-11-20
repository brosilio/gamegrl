using System.Numerics;

namespace Gamegrl.Physics2D;

// Joint solvers: ports of Box2D v3's *_joint.c (Erin Catto). Each joint keeps its frames relative to the bodies' centres
// of mass with world rotation, prepared once per step; the substeps then work on the bodies' deltas, as contacts do.
// Joints are coloured into the constraint graph with the contacts and solved in parallel, colour by colour; a joint
// never writes a body that isn't dynamic, so static and kinematic bodies are shared safely.
public sealed partial class PhysicsWorld2D
{
    private struct Joint
    {
        public bool Alive;
        public Entity Owner;
        public int A, B; // A is −1 for a joint to the world
        public JointDef2D Def;
        // Per step
        public float MA, MB, IA, IB;
        public Softness Constraint, Spring, Spring2;
        public Transform2D FrameA, FrameB; // anchors relative to the centres of mass, world-rotated
        public Vector2 DeltaCenter;
        public float AxialMass, PerpMass, MotorMass, ScalarMass;
        public Mat22 LinearMass;
        // Accumulated impulses (warm started across substeps and steps)
        public Vector2 Linear, LinearSpring;
        public float Impulse, SpringImpulse, MotorImpulse, LowerImpulse, UpperImpulse, Angular, AngularSpring, Perp, PogoVelocity;
        public bool ThresholdHit;
        // Links: place in each body's joint list (keys are joint × 2 + side), in its island and in the constraint graph.
        public int PrevA, NextA, PrevB, NextB, Island, IslandIndex, GraphColor, GraphSlot;
    }

    internal struct Mat22
    {
        public Vector2 Cx, Cy;
        public readonly Vector2 Mul(Vector2 v) => new(Cx.X * v.X + Cy.X * v.Y, Cx.Y * v.X + Cy.Y * v.Y);
        public readonly Vector2 Solve(Vector2 b)
        {
            float a11 = Cx.X, a12 = Cy.X, a21 = Cx.Y, a22 = Cy.Y;
            float det = a11 * a22 - a12 * a21;
            if (det != 0) det = 1 / det;
            return new Vector2(det * (a22 * b.X - a12 * b.Y), det * (a11 * b.Y - a21 * b.X));
        }
        public readonly Mat22 Inverse()
        {
            float a = Cx.X, b = Cy.X, c = Cx.Y, d = Cy.Y;
            float det = a * d - b * c;
            if (det != 0) det = 1 / det;
            return new Mat22 { Cx = new Vector2(det * d, -det * c), Cy = new Vector2(-det * b, det * a) };
        }
    }

    private Joint[] _joints = new Joint[16];
    private int _jointHigh;
    private int[] _freeJoints = new int[8];
    private int _freeJointCount;
    private int[] _jointHead = [], _jointDegree = []; // per body: first joint key, and how many
    private JointEvent2D[] _jointEvents = new JointEvent2D[8];
    private int _jointEventCount;

    /// <summary>Joints whose force or torque reached their threshold in the last step.</summary>
    public ReadOnlySpan<JointEvent2D> JointEvents => _jointEvents.AsSpan(0, _jointEventCount);

    public int CreateJoint(Entity owner, int bodyA, int bodyB, in JointDef2D def)
    {
        int j;
        if (_freeJointCount > 0) j = _freeJoints[--_freeJointCount];
        else
        {
            if (_jointHigh == _joints.Length) Array.Resize(ref _joints, _joints.Length * 2);
            j = _jointHigh++;
        }
        _joints[j] = new Joint { Alive = true, Owner = owner, A = bodyA, B = bodyB, Def = Sanitized(def), Island = -1, IslandIndex = -1 };
        if (def.Type == JointType2D.Pogo) { _joints[j].Impulse = def.Impulse; _joints[j].PogoVelocity = def.Velocity; }
        AddJointEdges(j);
        if (bodyA >= 0) Wake(bodyA);
        Wake(bodyB);
        LinkJoint(j);
        if (!_recolor && JointSolvable(j)) AddJointToGraph(j);
        if (bodyA >= 0 && !def.CollideConnected) DestroyContactsBetween(bodyA, bodyB);
        return j;
    }

    private static JointDef2D Sanitized(JointDef2D def)
    {
        if (def.ConstraintHertz <= 0) { def.ConstraintHertz = 60; def.ConstraintDampingRatio = 2; }
        if (def.ForceThreshold == 0 && def.TorqueThreshold == 0) { def.ForceThreshold = float.MaxValue; def.TorqueThreshold = float.MaxValue; }
        if (def.Type == JointType2D.Distance)
        {
            def.Length = Math.Clamp(def.Length, LinearSlop, 1e5f);
            def.Lower = Math.Clamp(def.Lower, LinearSlop, 1e5f);
            def.Upper = Math.Clamp(def.Upper, LinearSlop, 1e5f);
            if (def.LowerSpringForce == 0 && def.UpperSpringForce == 0) { def.LowerSpringForce = float.MinValue; def.UpperSpringForce = float.MaxValue; }
        }
        return def;
    }

    public void DestroyJoint(int j)
    {
        if (!IsJointAlive(j)) return;
        int a = _joints[j].A, b = _joints[j].B;
        if (a >= 0) Wake(a);
        Wake(b);
        if (_joints[j].GraphColor != 0) { if (!_recolor) RemoveJointFromGraph(j); _joints[j].GraphColor = 0; }
        UnlinkJoint(j);
        RemoveJointEdges(j);
        bool collide = _joints[j].Def.CollideConnected;
        _joints[j] = default;
        if (_freeJointCount == _freeJoints.Length) Array.Resize(ref _freeJoints, _freeJoints.Length * 2);
        _freeJoints[_freeJointCount++] = j;
        if (a >= 0 && !collide) RefindPairs(a, b);
    }

    public bool IsJointAlive(int j) => (uint)j < (uint)_jointHigh && _joints[j].Alive;
    public Entity OwnerOfJoint(int j) => _joints[j].Owner;
    public int JointCapacity => _jointHigh;
    public (int A, int B) JointBodies(int j) => (_joints[j].A, _joints[j].B);
    public ref readonly JointDef2D JointDef(int j) => ref _joints[j].Def;

    /// <summary>Change a joint's settings in place (keeps its accumulated impulses); its type and bodies stay.</summary>
    public void UpdateJoint(int j, in JointDef2D def)
    {
        ref var joint = ref _joints[j];
        bool collide = joint.Def.CollideConnected;
        var type = joint.Def.Type;
        joint.Def = Sanitized(def);
        joint.Def.Type = type;
        if (collide != def.CollideConnected && joint.A >= 0)
        {
            if (def.CollideConnected) RefindPairs(joint.A, joint.B);
            else DestroyContactsBetween(joint.A, joint.B);
        }
    }

    /// <summary>Wake both bodies of a joint (after changing what drives it).</summary>
    public void WakeJoint(int j)
    {
        if (_joints[j].A >= 0) Wake(_joints[j].A);
        Wake(_joints[j].B);
    }

    // ---------------------------------------------------------------- reading joints

    private Transform2D BodyTransform(int b) => b >= 0 ? new Transform2D(GetPosition(b), Rotation[b]) : Transform2D.Identity;

    /// <summary>Revolute: the angle between the frames. Others: 0.</summary>
    public float JointAngle(int j)
    {
        ref readonly var joint = ref _joints[j];
        var qA = Rot.Mul(BodyTransform(joint.A).Q, joint.Def.LocalFrameA.Q);
        var qB = Rot.Mul(BodyTransform(joint.B).Q, joint.Def.LocalFrameB.Q);
        return RelativeAngle(qA, qB);
    }

    /// <summary>Prismatic and wheel: body B's frame along frame A's x axis. Distance: the anchors' distance. Pogo: the length along frame B's y axis.</summary>
    public float JointTranslation(int j)
    {
        ref readonly var joint = ref _joints[j];
        var xfA = BodyTransform(joint.A); var xfB = BodyTransform(joint.B);
        var pA = xfA.Apply(joint.Def.LocalFrameA.P); var pB = xfB.Apply(joint.Def.LocalFrameB.P);
        var d = pB - pA;
        switch (joint.Def.Type)
        {
            case JointType2D.Distance: return SimMath.Length(d);
            case JointType2D.Pogo: return V.Dot(d, Rot.Mul(xfB.Q, joint.Def.LocalFrameB.Q).Rotate(Vector2.UnitY));
            default: return V.Dot(d, Rot.Mul(xfA.Q, joint.Def.LocalFrameA.Q).Rotate(Vector2.UnitX));
        }
    }

    /// <summary>Revolute and wheel: relative angular speed. Prismatic: speed along the axis.</summary>
    public float JointSpeed(int j)
    {
        ref readonly var joint = ref _joints[j];
        int a = joint.A, b = joint.B;
        float wA = a >= 0 ? AngularVelocityOf[a] : 0, wB = AngularVelocityOf[b];
        if (joint.Def.Type != JointType2D.Prismatic) return wB - wA;
        var xfA = BodyTransform(a);
        var axis = Rot.Mul(xfA.Q, joint.Def.LocalFrameA.Q).Rotate(Vector2.UnitX);
        var cA = a >= 0 ? Center[a] : Vector2.Zero; var vA = a >= 0 ? Velocity[a] : Vector2.Zero;
        var rA = xfA.Q.Rotate(joint.Def.LocalFrameA.P - (a >= 0 ? LocalCenter[a] : Vector2.Zero));
        var rB = Rotation[b].Rotate(joint.Def.LocalFrameB.P - LocalCenter[b]);
        var d = (Center[b] - cA) + (rB - rA);
        var vB = Velocity[b];
        var vr = (vB + V.Cross(wB, rB)) - (vA + V.Cross(wA, rA));
        return V.Dot(d, V.Cross(wA, axis)) + V.Dot(axis, vr);
    }

    /// <summary>The joint's motor force (or torque, for revolute and wheel) in the last step.</summary>
    public float JointMotorForce(int j) => _joints[j].MotorImpulse * _invH;

    /// <summary>
    /// The force and torque the joint applied to body B in the last step (in world space; body A felt the opposite).
    /// Revolute torque and prismatic force include the spring's, which Box2D leaves out.
    /// </summary>
    public (Vector2 Force, float Torque) JointConstraintForce(int j)
    {
        ref readonly var joint = ref _joints[j];
        float invH = _invH;
        switch (joint.Def.Type)
        {
            case JointType2D.Distance:
            {
                var xfA = BodyTransform(joint.A); var xfB = BodyTransform(joint.B);
                var axis = SimMath.Normalize(xfB.Apply(joint.Def.LocalFrameB.P) - xfA.Apply(joint.Def.LocalFrameA.P));
                return (invH * (joint.Impulse + joint.LowerImpulse - joint.UpperImpulse + joint.MotorImpulse) * axis, 0);
            }
            case JointType2D.Prismatic:
            {
                var axis = Rot.Mul(BodyTransform(joint.A).Q, joint.Def.LocalFrameA.Q).Rotate(Vector2.UnitX);
                float axial = joint.SpringImpulse + joint.MotorImpulse + joint.LowerImpulse - joint.UpperImpulse;
                return (invH * (joint.Linear.X * LeftPerp(axis) + axial * axis), invH * joint.Linear.Y);
            }
            case JointType2D.Wheel:
            {
                var axis = Rot.Mul(BodyTransform(joint.A).Q, joint.Def.LocalFrameA.Q).Rotate(Vector2.UnitX);
                float axial = joint.SpringImpulse + joint.LowerImpulse - joint.UpperImpulse;
                return (invH * (joint.Perp * LeftPerp(axis) + axial * axis), invH * joint.MotorImpulse);
            }
            case JointType2D.Revolute: return (invH * joint.Linear, invH * (joint.SpringImpulse + joint.MotorImpulse + joint.LowerImpulse - joint.UpperImpulse));
            case JointType2D.Weld: return (invH * joint.Linear, invH * joint.Angular);
            case JointType2D.Motor: return (invH * (joint.Linear + joint.LinearSpring), invH * (joint.Angular + joint.AngularSpring));
            case JointType2D.Mover: return (invH * joint.Linear, 0);
            case JointType2D.Pogo: return (invH * joint.Impulse * joint.Def.Normal, 0);
            default: return (Vector2.Zero, 0);
        }
    }

    /// <summary>Box2D's reaction measure for thresholds: linear and angular impulse over the substep.</summary>
    private static void Reaction(in Joint joint, float invH, out float force, out float torque)
    {
        float linear = 0, angular = 0;
        switch (joint.Def.Type)
        {
            case JointType2D.Distance: linear = MathF.Abs(joint.Impulse + joint.LowerImpulse - joint.UpperImpulse + joint.MotorImpulse); break;
            case JointType2D.Motor: linear = SimMath.Length(joint.Linear + joint.LinearSpring); angular = MathF.Abs(joint.Angular + joint.AngularSpring); break;
            case JointType2D.Mover: linear = SimMath.Length(joint.Linear); break;
            case JointType2D.Pogo: linear = MathF.Abs(joint.Impulse); break;
            case JointType2D.Prismatic:
            {
                float perp = joint.Linear.X, axial = joint.MotorImpulse + joint.LowerImpulse - joint.UpperImpulse;
                linear = SimMath.Sqrt(perp * perp + axial * axial); angular = MathF.Abs(joint.Linear.Y);
                break;
            }
            case JointType2D.Revolute: linear = SimMath.Length(joint.Linear); angular = MathF.Abs(joint.MotorImpulse + joint.LowerImpulse - joint.UpperImpulse); break;
            case JointType2D.Weld: linear = SimMath.Length(joint.Linear); angular = MathF.Abs(joint.Angular); break;
            case JointType2D.Wheel:
            {
                float perp = joint.Perp, axial = joint.SpringImpulse + joint.LowerImpulse - joint.UpperImpulse;
                linear = SimMath.Sqrt(perp * perp + axial * axial); angular = MathF.Abs(joint.MotorImpulse);
                break;
            }
        }
        force = linear * invH;
        torque = angular * invH;
    }

    internal static float RelativeAngle(Rot a, Rot b) => SimMath.Atan2(a.C * b.S - a.S * b.C, a.C * b.C + a.S * b.S);
    private static float Angle(Rot q) => SimMath.Atan2(q.S, q.C);
    private static float Unwind(float r) => r < -SimMath.Pi ? r + 2 * SimMath.Pi : r > SimMath.Pi ? r - 2 * SimMath.Pi : r;
    private static Vector2 LeftPerp(Vector2 v) => new(-v.Y, v.X);

    // ---------------------------------------------------------------- the constraint graph

    /// <summary>A joint is solved while it has an awake dynamic body (filter joints never are).</summary>
    private bool JointSolvable(int j)
    {
        ref readonly var joint = ref _joints[j];
        if (!joint.Alive || joint.Def.Type == JointType2D.Filter) return false;
        int a = joint.A, b = joint.B;
        return (Types[b] == BodyType.Dynamic && Awake[b]) || (a >= 0 && Types[a] == BodyType.Dynamic && Awake[a]);
    }

    private void AddJointToGraph(int j)
    {
        ref var joint = ref _joints[j];
        int color = AssignColor(joint.A, joint.B);
        ref var list = ref _colorJoints[color];
        ref int count = ref _colorJointCounts[color];
        if (count == list.Length) Array.Resize(ref list, count * 2);
        joint.GraphColor = color + 1;
        joint.GraphSlot = count;
        list[count++] = j;
    }

    private void RemoveJointFromGraph(int j)
    {
        ref var joint = ref _joints[j];
        int color = joint.GraphColor - 1;
        ReleaseColor(color, joint.A, joint.B);
        var list = _colorJoints[color];
        int last = list[--_colorJointCounts[color]];
        list[joint.GraphSlot] = last;
        _joints[last].GraphSlot = joint.GraphSlot;
        joint.GraphColor = 0;
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

    private void RemoveJointsOf(int body)
    {
        while (_jointHead[body] >= 0) DestroyJoint(_jointHead[body] >> 1);
    }

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
        int body = ShapeCountOf(a) <= ShapeCountOf(b) ? a : b;
        for (int s = FirstShape[body]; s >= 0; s = NextShape[s]) MarkMoved(s);
    }

    private int ShapeCountOf(int b) { int n = 0; for (int s = FirstShape[b]; s >= 0; s = NextShape[s]) n++; return n; }

    private void CloneJoints(PhysicsWorld2D w)
    {
        w._joints = (Joint[])_joints.Clone();
        w._freeJoints = (int[])_freeJoints.Clone();
        w._jointEvents = (JointEvent2D[])_jointEvents.Clone();
    }

    // ---------------------------------------------------------------- solving

    /// <summary>Box2D's dummy state for bodies that aren't moving this step: no velocity, no motion.</summary>
    private static BodyState Still => new() { DQ = Rot.Identity };

    private void PrepareJoint(int j)
    {
        ref var joint = ref _joints[j];
        int a = joint.A, b = joint.B;
        float h = _h;
        // What the constraint sees: a sleeping or static body is immovable (_stepMass is zero unless awake).
        joint.MA = a >= 0 ? _stepMass[a].X : 0; joint.IA = a >= 0 ? _stepMass[a].Y : 0;
        joint.MB = _stepMass[b].X; joint.IB = _stepMass[b].Y;
        float hertz = F.Min(joint.Def.ConstraintHertz, 0.25f * _invH);
        joint.Constraint = Softness.Make(hertz, joint.Def.ConstraintDampingRatio, h);
        var qA = a >= 0 ? Rotation[a] : Rot.Identity; var qB = Rotation[b];
        joint.FrameA = new Transform2D(qA.Rotate(joint.Def.LocalFrameA.P - (a >= 0 ? LocalCenter[a] : Vector2.Zero)), Rot.Mul(qA, joint.Def.LocalFrameA.Q));
        joint.FrameB = new Transform2D(qB.Rotate(joint.Def.LocalFrameB.P - LocalCenter[b]), Rot.Mul(qB, joint.Def.LocalFrameB.Q));
        joint.DeltaCenter = Center[b] - (a >= 0 ? Center[a] : Vector2.Zero);
        float mA = joint.MA, mB = joint.MB, iA = joint.IA, iB = joint.IB;
        var rA = joint.FrameA.P; var rB = joint.FrameB.P;
        ref readonly var def = ref joint.Def;
        switch (def.Type)
        {
            case JointType2D.Distance:
            {
                var axis = SimMath.Normalize(rB - rA + joint.DeltaCenter);
                float crA = V.Cross(rA, axis), crB = V.Cross(rB, axis);
                float k = mA + mB + iA * crA * crA + iB * crB * crB;
                joint.AxialMass = k > 0 ? 1 / k : 0;
                joint.Spring = Softness.Make(def.Hertz, def.DampingRatio, h);
                break;
            }
            case JointType2D.Motor:
            {
                joint.Spring = Softness.Make(def.LinearHertz, def.LinearDampingRatio, h);
                joint.Spring2 = Softness.Make(def.AngularHertz, def.AngularDampingRatio, h);
                joint.LinearMass = PointMass(mA, mB, iA, iB, rA, rB).Inverse();
                float ka = iA + iB;
                joint.AxialMass = ka > 0 ? 1 / ka : 0;
                break;
            }
            case JointType2D.Mover:
            {
                float k = mA + mB;
                joint.ScalarMass = k > 0 ? 1 / k : 0;
                break;
            }
            case JointType2D.Pogo:
            {
                float crA = V.Cross(rA, def.Normal), crB = V.Cross(rB, def.Normal);
                float k = mA + mB + iA * crA * crA + iB * crB * crB;
                joint.ScalarMass = k > 0 ? 1 / k : 0;
                break;
            }
            case JointType2D.Prismatic:
            case JointType2D.Revolute:
            {
                float k = iA + iB;
                joint.AxialMass = k > 0 ? 1 / k : 0;
                joint.Spring = Softness.Make(def.Hertz, def.DampingRatio, h);
                break;
            }
            case JointType2D.Weld:
            {
                float ka = iA + iB;
                joint.AxialMass = ka > 0 ? 1 / ka : 0;
                joint.Spring = def.LinearHertz == 0 ? joint.Constraint : Softness.Make(def.LinearHertz, def.LinearDampingRatio, h);
                joint.Spring2 = def.AngularHertz == 0 ? joint.Constraint : Softness.Make(def.AngularHertz, def.AngularDampingRatio, h);
                break;
            }
            case JointType2D.Wheel:
            {
                var d = joint.DeltaCenter + (rB - rA);
                var axisA = joint.FrameA.Q.Rotate(Vector2.UnitX);
                var perpA = LeftPerp(axisA);
                float s1 = V.Cross(d + rA, perpA), s2 = V.Cross(rB, perpA);
                float kp = mA + mB + iA * s1 * s1 + iB * s2 * s2;
                joint.PerpMass = kp > 0 ? 1 / kp : 0;
                float a1 = V.Cross(d + rA, axisA), a2 = V.Cross(rB, axisA);
                float ka = mA + mB + iA * a1 * a1 + iB * a2 * a2;
                joint.AxialMass = ka > 0 ? 1 / ka : 0;
                joint.Spring = Softness.Make(def.Hertz, def.DampingRatio, h);
                float km = iA + iB;
                joint.MotorMass = km > 0 ? 1 / km : 0;
                break;
            }
        }
        if (!def.EnableLimit) { joint.LowerImpulse = 0; joint.UpperImpulse = 0; }
        if (!def.EnableMotor) joint.MotorImpulse = 0;
        if (!def.EnableSpring) joint.SpringImpulse = 0;
    }

    private static Mat22 PointMass(float mA, float mB, float iA, float iB, Vector2 rA, Vector2 rB)
    {
        float xy = -rA.Y * rA.X * iA - rB.Y * rB.X * iB;
        return new Mat22 { Cx = new Vector2(mA + mB + rA.Y * rA.Y * iA + rB.Y * rB.Y * iB, xy), Cy = new Vector2(xy, mA + mB + rA.X * rA.X * iA + rB.X * rB.X * iB) };
    }

    private bool MovesThisStep(int b) => b >= 0 && Awake[b];
    private bool IsDynamicNow(int b) => b >= 0 && Types[b] == BodyType.Dynamic && Awake[b];

    private void WarmStartJoint(int j)
    {
        ref var joint = ref _joints[j];
        var dummyA = Still; var dummyB = Still;
        ref var sa = ref MovesThisStep(joint.A) ? ref States[joint.A] : ref dummyA;
        ref var sb = ref MovesThisStep(joint.B) ? ref States[joint.B] : ref dummyB;
        float mA = joint.MA, mB = joint.MB, iA = joint.IA, iB = joint.IB;
        var rA = sa.DQ.Rotate(joint.FrameA.P); var rB = sb.DQ.Rotate(joint.FrameB.P);
        Vector2 P = default; float LA = 0, LB = 0;
        switch (joint.Def.Type)
        {
            case JointType2D.Distance:
            {
                var axis = SimMath.Normalize(joint.DeltaCenter + (sb.DP - sa.DP) + (rB - rA));
                P = (joint.Impulse + joint.LowerImpulse - joint.UpperImpulse + joint.MotorImpulse) * axis;
                LA = V.Cross(rA, P); LB = V.Cross(rB, P);
                break;
            }
            case JointType2D.Motor:
            {
                P = joint.Linear + joint.LinearSpring;
                float angular = joint.Angular + joint.AngularSpring;
                LA = V.Cross(rA, P) + angular; LB = V.Cross(rB, P) + angular;
                break;
            }
            case JointType2D.Mover: P = joint.Linear; break;
            case JointType2D.Pogo:
            {
                if (joint.Def.Hertz == 0) { joint.Impulse = 0; return; }
                P = joint.Impulse * joint.Def.Normal;
                LA = V.Cross(rA, P); LB = V.Cross(rB, P);
                break;
            }
            case JointType2D.Prismatic:
            {
                var d = (sb.DP - sa.DP) + joint.DeltaCenter + (rB - rA);
                var axisA = sa.DQ.Rotate(joint.FrameA.Q.Rotate(Vector2.UnitX));
                float a1 = V.Cross(rA + d, axisA), a2 = V.Cross(rB, axisA);
                float axial = joint.SpringImpulse + joint.MotorImpulse + joint.LowerImpulse - joint.UpperImpulse;
                var perpA = LeftPerp(axisA);
                float s1 = V.Cross(rA + d, perpA), s2 = V.Cross(rB, perpA);
                float perp = joint.Linear.X, angle = joint.Linear.Y;
                P = axial * axisA + perp * perpA;
                LA = axial * a1 + perp * s1 + angle; LB = axial * a2 + perp * s2 + angle;
                break;
            }
            case JointType2D.Revolute:
            {
                float axial = joint.SpringImpulse + joint.MotorImpulse + joint.LowerImpulse - joint.UpperImpulse;
                P = joint.Linear;
                LA = V.Cross(rA, P) + axial; LB = V.Cross(rB, P) + axial;
                break;
            }
            case JointType2D.Weld:
                P = joint.Linear;
                LA = V.Cross(rA, P) + joint.Angular; LB = V.Cross(rB, P) + joint.Angular;
                break;
            case JointType2D.Wheel:
            {
                var d = (sb.DP - sa.DP) + joint.DeltaCenter + (rB - rA);
                var axisA = sa.DQ.Rotate(joint.FrameA.Q.Rotate(Vector2.UnitX));
                var perpA = LeftPerp(axisA);
                float a1 = V.Cross(d + rA, axisA), a2 = V.Cross(rB, axisA), s1 = V.Cross(d + rA, perpA), s2 = V.Cross(rB, perpA);
                float axial = joint.SpringImpulse + joint.LowerImpulse - joint.UpperImpulse;
                P = axial * axisA + joint.Perp * perpA;
                LA = axial * a1 + joint.Perp * s1 + joint.MotorImpulse; LB = axial * a2 + joint.Perp * s2 + joint.MotorImpulse;
                break;
            }
            default: return;
        }
        if (IsDynamicNow(joint.A)) { sa.V -= mA * P; sa.W -= iA * LA; }
        if (IsDynamicNow(joint.B)) { sb.V += mB * P; sb.W += iB * LB; }
    }

    private void SolveJoint(int j, bool useBias)
    {
        ref var joint = ref _joints[j];
        var dummyA = Still; var dummyB = Still;
        ref var sa = ref MovesThisStep(joint.A) ? ref States[joint.A] : ref dummyA;
        ref var sb = ref MovesThisStep(joint.B) ? ref States[joint.B] : ref dummyB;
        var vA = sa.V; float wA = sa.W; var vB = sb.V; float wB = sb.W;
        switch (joint.Def.Type)
        {
            case JointType2D.Distance: SolveDistance(ref joint, sa, sb, ref vA, ref wA, ref vB, ref wB, useBias); break;
            case JointType2D.Motor: SolveMotor(ref joint, sa, sb, ref vA, ref wA, ref vB, ref wB); break;
            case JointType2D.Mover: SolveMover(ref joint, ref vA, ref vB); break;
            case JointType2D.Pogo: if (!SolvePogo(ref joint, sa, sb, ref vA, ref wA, ref vB, ref wB, useBias)) return; break;
            case JointType2D.Prismatic: SolvePrismatic(ref joint, sa, sb, ref vA, ref wA, ref vB, ref wB, useBias); break;
            case JointType2D.Revolute: SolveRevolute(ref joint, sa, sb, ref vA, ref wA, ref vB, ref wB, useBias); break;
            case JointType2D.Weld: SolveWeld(ref joint, sa, sb, ref vA, ref wA, ref vB, ref wB, useBias); break;
            case JointType2D.Wheel: SolveWheel(ref joint, sa, sb, ref vA, ref wA, ref vB, ref wB, useBias); break;
            default: return;
        }
        if (IsDynamicNow(joint.A)) { sa.V = vA; sa.W = wA; }
        if (IsDynamicNow(joint.B)) { sb.V = vB; sb.W = wB; }
        // Box2D checks in the biased solve, when a soft joint has yet to build up its full impulse; after the relax
        // pass the measure matches what JointConstraintForce reports.
        if (!useBias && !joint.ThresholdHit && (joint.Def.ForceThreshold < float.MaxValue || joint.Def.TorqueThreshold < float.MaxValue))
        {
            Reaction(joint, _invH, out float force, out float torque);
            if (force >= joint.Def.ForceThreshold || torque >= joint.Def.TorqueThreshold) joint.ThresholdHit = true;
        }
    }

    private static void Apply(in Joint j, Vector2 P, float LA, float LB, ref Vector2 vA, ref float wA, ref Vector2 vB, ref float wB)
    {
        vA -= j.MA * P; wA -= j.IA * LA;
        vB += j.MB * P; wB += j.IB * LB;
    }

    /// <summary>A unilateral bound C ≥ 0 on a row: speculative when apart, soft when violated (Box2D's limits).</summary>
    private float LimitRow(in Joint j, float c, float cdot, float mass, ref float accumulated, bool useBias, float speculativeCap = float.MaxValue)
    {
        float bias = 0, massScale = 1, impulseScale = 0;
        if (c > 0) bias = F.Min(c, speculativeCap) * _invH;
        else if (useBias) { bias = j.Constraint.BiasRate * c; massScale = j.Constraint.MassScale; impulseScale = j.Constraint.ImpulseScale; }
        float old = accumulated;
        float impulse = -massScale * mass * (cdot + bias) - impulseScale * old;
        accumulated = F.Max(old + impulse, 0);
        return accumulated - old;
    }

    private void SolveDistance(ref Joint j, in BodyState sa, in BodyState sb, ref Vector2 vA, ref float wA, ref Vector2 vB, ref float wB, bool useBias)
    {
        ref readonly var def = ref j.Def;
        var rA = sa.DQ.Rotate(j.FrameA.P); var rB = sb.DQ.Rotate(j.FrameB.P);
        var separation = j.DeltaCenter + ((sb.DP - sa.DP) + (rB - rA));
        float length = SimMath.Length(separation);
        var axis = SimMath.Normalize(separation);
        float crA = V.Cross(rA, axis), crB = V.Cross(rB, axis);
        Vector2 Vr(Vector2 vA, float wA, Vector2 vB, float wB) => (vB - vA) + (V.Cross(wB, rB) - V.Cross(wA, rA));
        if (def.EnableSpring && (def.Lower < def.Upper || !def.EnableLimit))
        {
            if (def.Hertz > 0)
            {
                float cdot = V.Dot(axis, Vr(vA, wA, vB, wB));
                float c = length - def.Length;
                float old = j.Impulse;
                float impulse = -j.Spring.MassScale * j.AxialMass * (cdot + j.Spring.BiasRate * c) - j.Spring.ImpulseScale * old;
                j.Impulse = F.Clamp(old + impulse, def.LowerSpringForce * _h, def.UpperSpringForce * _h);
                impulse = j.Impulse - old;
                var P = impulse * axis;
                Apply(j, P, V.Cross(rA, P), V.Cross(rB, P), ref vA, ref wA, ref vB, ref wB);
            }
            if (def.EnableMotor)
            {
                float cdot = V.Dot(axis, Vr(vA, wA, vB, wB));
                float impulse = j.AxialMass * (def.MotorSpeed - cdot);
                float old = j.MotorImpulse, max = _h * def.MaxMotorForce;
                j.MotorImpulse = F.Clamp(old + impulse, -max, max);
                var P = (j.MotorImpulse - old) * axis;
                Apply(j, P, V.Cross(rA, P), V.Cross(rB, P), ref vA, ref wA, ref vB, ref wB);
            }
            if (def.EnableLimit)
            {
                float lower = LimitRow(j, length - def.Lower, V.Dot(axis, Vr(vA, wA, vB, wB)), j.AxialMass, ref j.LowerImpulse, useBias);
                var P = lower * axis;
                Apply(j, P, V.Cross(rA, P), V.Cross(rB, P), ref vA, ref wA, ref vB, ref wB);
                float upper = LimitRow(j, def.Upper - length, -V.Dot(axis, Vr(vA, wA, vB, wB)), j.AxialMass, ref j.UpperImpulse, useBias);
                P = -upper * axis;
                Apply(j, P, V.Cross(rA, P), V.Cross(rB, P), ref vA, ref wA, ref vB, ref wB);
            }
        }
        else
        {
            // Rigid.
            float cdot = V.Dot(axis, Vr(vA, wA, vB, wB));
            float c = length - def.Length;
            float bias = 0, massScale = 1, impulseScale = 0;
            if (useBias) { bias = j.Constraint.BiasRate * c; massScale = j.Constraint.MassScale; impulseScale = j.Constraint.ImpulseScale; }
            float impulse = -massScale * j.AxialMass * (cdot + bias) - impulseScale * j.Impulse;
            j.Impulse += impulse;
            var P = impulse * axis;
            Apply(j, P, V.Cross(rA, P), V.Cross(rB, P), ref vA, ref wA, ref vB, ref wB);
        }
    }

    private void SolveMotor(ref Joint j, in BodyState sa, in BodyState sb, ref Vector2 vA, ref float wA, ref Vector2 vB, ref float wB)
    {
        ref readonly var def = ref j.Def;
        float iA = j.IA, iB = j.IB;
        if (def.MaxSpringTorque > 0 && def.AngularHertz > 0)
        {
            var qA = Rot.Mul(sa.DQ, j.FrameA.Q); var qB = Rot.Mul(sb.DQ, j.FrameB.Q);
            float c = Angle(Rot.InvMul(qA, qB));
            float old = j.AngularSpring, max = _h * def.MaxSpringTorque;
            float impulse = -j.Spring2.MassScale * j.AxialMass * (wB - wA + j.Spring2.BiasRate * c) - j.Spring2.ImpulseScale * old;
            j.AngularSpring = F.Clamp(old + impulse, -max, max);
            impulse = j.AngularSpring - old;
            wA -= iA * impulse; wB += iB * impulse;
        }
        if (def.MaxVelocityTorque > 0)
        {
            float impulse = -j.AxialMass * (wB - wA - def.AngularVelocity);
            float old = j.Angular, max = _h * def.MaxVelocityTorque;
            j.Angular = F.Clamp(old + impulse, -max, max);
            impulse = j.Angular - old;
            wA -= iA * impulse; wB += iB * impulse;
        }
        var rA = sa.DQ.Rotate(j.FrameA.P); var rB = sb.DQ.Rotate(j.FrameB.P);
        if (def.MaxSpringForce > 0 && def.LinearHertz > 0)
        {
            var c = ((sb.DP - sa.DP) + (rB - rA)) + j.DeltaCenter;
            var cdot = (vB + V.Cross(wB, rB)) - (vA + V.Cross(wA, rA)) + j.Spring.BiasRate * c;
            j.LinearMass = PointMass(j.MA, j.MB, iA, iB, rA, rB).Inverse();
            var b = j.LinearMass.Mul(cdot);
            var old = j.LinearSpring;
            j.LinearSpring += -j.Spring.MassScale * b - j.Spring.ImpulseScale * old;
            float max = _h * def.MaxSpringForce;
            if (SimMath.LengthSquared(j.LinearSpring) > max * max) j.LinearSpring = max * SimMath.Normalize(j.LinearSpring);
            var impulse = j.LinearSpring - old;
            Apply(j, impulse, V.Cross(rA, impulse), V.Cross(rB, impulse), ref vA, ref wA, ref vB, ref wB);
        }
        if (def.MaxVelocityForce > 0)
        {
            var cdot = (vB + V.Cross(wB, rB)) - (vA + V.Cross(wA, rA)) - def.LinearVelocity;
            var impulse = -j.LinearMass.Mul(cdot);
            var old = j.Linear;
            j.Linear += impulse;
            float max = _h * def.MaxVelocityForce;
            if (SimMath.LengthSquared(j.Linear) > max * max) j.Linear = max * SimMath.Normalize(j.Linear);
            impulse = j.Linear - old;
            Apply(j, impulse, V.Cross(rA, impulse), V.Cross(rB, impulse), ref vA, ref wA, ref vB, ref wB);
        }
    }

    private void SolveMover(ref Joint j, ref Vector2 vA, ref Vector2 vB)
    {
        ref readonly var def = ref j.Def;
        if (def.MaxAxisForce.X <= 0 && def.MaxAxisForce.Y <= 0) { j.Linear = default; return; }
        var cdot = (vB - vA) - def.LinearVelocity;
        var old = j.Linear;
        var max = _h * def.MaxAxisForce;
        j.Linear = SimMath.Min(SimMath.Max(old - j.ScalarMass * cdot, -max), max);
        var impulse = j.Linear - old;
        vA -= j.MA * impulse; vB += j.MB * impulse;
    }

    private bool SolvePogo(ref Joint j, in BodyState sa, in BodyState sb, ref Vector2 vA, ref float wA, ref Vector2 vB, ref float wB, bool useBias)
    {
        ref readonly var def = ref j.Def;
        if (def.Hertz == 0) { j.Impulse = 0; return false; }
        var rA = sa.DQ.Rotate(j.FrameA.P); var rB = sb.DQ.Rotate(j.FrameB.P);
        float bias = 0;
        if (useBias)
        {
            var d = ((sb.DP - sa.DP) + (rB - rA)) + j.DeltaCenter;
            var axis = j.FrameB.Q.Rotate(Vector2.UnitY);
            float c = V.Dot(axis, d) - def.Length;
            // Box2D's implicit spring-damper on the joint's own velocity.
            float omega = 2 * SimMath.Pi * def.Hertz, omegaH = omega * _h;
            j.PogoVelocity = (j.PogoVelocity - omega * omegaH * c) / (1 + 2 * def.DampingRatio * omegaH + omegaH * omegaH);
            bias = -j.PogoVelocity;
        }
        float cdot = V.Dot(def.Normal, (vB + V.Cross(wB, rB)) - (vA + V.Cross(wA, rA)));
        float old = j.Impulse;
        j.Impulse = F.Clamp(old - j.ScalarMass * (cdot + bias), -_h * def.MaxTensionForce, _h * def.MaxCompressionForce);
        var P = (j.Impulse - old) * def.Normal;
        Apply(j, P, V.Cross(rA, P), V.Cross(rB, P), ref vA, ref wA, ref vB, ref wB);
        return true;
    }

    private void SolvePrismatic(ref Joint j, in BodyState sa, in BodyState sb, ref Vector2 vA, ref float wA, ref Vector2 vB, ref float wB, bool useBias)
    {
        ref readonly var def = ref j.Def;
        float mA = j.MA, mB = j.MB, iA = j.IA, iB = j.IB;
        var qA = Rot.Mul(sa.DQ, j.FrameA.Q); var qB = Rot.Mul(sb.DQ, j.FrameB.Q);
        var relQ = Rot.InvMul(qA, qB);
        var rA = sa.DQ.Rotate(j.FrameA.P); var rB = sb.DQ.Rotate(j.FrameB.P);
        var d = ((sb.DP - sa.DP) + j.DeltaCenter) + (rB - rA);
        var axisA = sa.DQ.Rotate(j.FrameA.Q.Rotate(Vector2.UnitX));
        float translation = V.Dot(axisA, d);
        float a1 = V.Cross(rA + d, axisA), a2 = V.Cross(rB, axisA);
        float k = mA + mB + iA * a1 * a1 + iB * a2 * a2;
        float axialMass = k > 0 ? 1 / k : 0;
        float Rate(Vector2 vA, float wA, Vector2 vB, float wB) => V.Dot(axisA, vB - vA) + a2 * wB - a1 * wA;
        if (def.EnableSpring)
        {
            float c = translation - def.TargetTranslation;
            float impulse = -j.Spring.MassScale * axialMass * (Rate(vA, wA, vB, wB) + j.Spring.BiasRate * c) - j.Spring.ImpulseScale * j.SpringImpulse;
            j.SpringImpulse += impulse;
            Apply(j, impulse * axisA, impulse * a1, impulse * a2, ref vA, ref wA, ref vB, ref wB);
        }
        if (def.EnableMotor)
        {
            float impulse = axialMass * (def.MotorSpeed - Rate(vA, wA, vB, wB));
            float old = j.MotorImpulse, max = _h * def.MaxMotorForce;
            j.MotorImpulse = F.Clamp(old + impulse, -max, max);
            impulse = j.MotorImpulse - old;
            Apply(j, impulse * axisA, impulse * a1, impulse * a2, ref vA, ref wA, ref vB, ref wB);
        }
        if (def.EnableLimit)
        {
            float speculative = 0.25f * (def.Upper - def.Lower);
            float c = translation - def.Lower;
            if (c < speculative)
            {
                float impulse = LimitRow(j, c, Rate(vA, wA, vB, wB), axialMass, ref j.LowerImpulse, useBias, 1);
                Apply(j, impulse * axisA, impulse * a1, impulse * a2, ref vA, ref wA, ref vB, ref wB);
            }
            else j.LowerImpulse = 0;
            c = def.Upper - translation;
            if (c < speculative)
            {
                float impulse = LimitRow(j, c, -Rate(vA, wA, vB, wB), axialMass, ref j.UpperImpulse, useBias, 1);
                Apply(j, -impulse * axisA, -impulse * a1, -impulse * a2, ref vA, ref wA, ref vB, ref wB);
            }
            else j.UpperImpulse = 0;
        }
        {
            // Off the axis, and turning: a 2×2 block.
            var perpA = LeftPerp(axisA);
            float s1 = V.Cross(d + rA, perpA), s2 = V.Cross(rB, perpA);
            var cdot = new Vector2(V.Dot(perpA, vB - vA) + s2 * wB - s1 * wA, wB - wA);
            var bias = Vector2.Zero;
            float massScale = 1, impulseScale = 0;
            if (useBias)
            {
                bias = j.Constraint.BiasRate * new Vector2(V.Dot(perpA, d), Angle(relQ));
                massScale = j.Constraint.MassScale; impulseScale = j.Constraint.ImpulseScale;
            }
            float k11 = mA + mB + iA * s1 * s1 + iB * s2 * s2, k12 = iA * s1 + iB * s2, k22 = iA + iB;
            if (k22 == 0) k22 = 1;
            var K = new Mat22 { Cx = new Vector2(k11, k12), Cy = new Vector2(k12, k22) };
            var b = K.Solve(cdot + bias);
            var impulse = -massScale * b - impulseScale * j.Linear;
            j.Linear += impulse;
            Apply(j, impulse.X * perpA, impulse.X * s1 + impulse.Y, impulse.X * s2 + impulse.Y, ref vA, ref wA, ref vB, ref wB);
        }
    }

    private void SolveRevolute(ref Joint j, in BodyState sa, in BodyState sb, ref Vector2 vA, ref float wA, ref Vector2 vB, ref float wB, bool useBias)
    {
        ref readonly var def = ref j.Def;
        float mA = j.MA, mB = j.MB, iA = j.IA, iB = j.IB;
        var qA = Rot.Mul(sa.DQ, j.FrameA.Q); var qB = Rot.Mul(sb.DQ, j.FrameB.Q);
        var relQ = Rot.InvMul(qA, qB);
        var rA = sa.DQ.Rotate(j.FrameA.P); var rB = sb.DQ.Rotate(j.FrameB.P);
        var linearCdot = (vB + V.Cross(wB, rB)) - (vA + V.Cross(wA, rA));
        float angularCdot = wB - wA, axialImpulse = 0;
        float k = iA + iB;
        bool fixedRotation = k == 0;
        if (def.EnableSpring && !fixedRotation)
        {
            float c = Unwind(Angle(relQ) - def.TargetAngle);
            float impulse = -j.Spring.MassScale * j.AxialMass * (angularCdot + j.Spring.BiasRate * c) - j.Spring.ImpulseScale * j.SpringImpulse;
            j.SpringImpulse += impulse;
            angularCdot += k * impulse;
            axialImpulse += impulse;
        }
        if (def.EnableMotor && !fixedRotation)
        {
            float old = j.MotorImpulse, max = _h * def.MaxMotorForce;
            float impulse = F.Clamp(-j.AxialMass * (angularCdot - def.MotorSpeed), -max - old, max - old);
            j.MotorImpulse = old + impulse;
            angularCdot += k * impulse;
            axialImpulse += impulse;
        }
        if (def.EnableLimit && !fixedRotation)
        {
            float angle = Angle(relQ);
            float lower = BoundRevolute(j, angle - def.Lower, angularCdot, ref j.LowerImpulse, useBias);
            angularCdot += k * lower;
            axialImpulse += lower;
            float upper = BoundRevolute(j, def.Upper - angle, -angularCdot, ref j.UpperImpulse, useBias);
            axialImpulse -= upper;
        }
        {
            var cdot = linearCdot + axialImpulse * (V.Cross(iA, rA) + V.Cross(iB, rB));
            var bias = Vector2.Zero;
            float massScale = 1, impulseScale = 0;
            if (useBias)
            {
                var separation = ((sb.DP - sa.DP) + (rB - rA)) + j.DeltaCenter;
                bias = j.Constraint.BiasRate * separation;
                massScale = j.Constraint.MassScale; impulseScale = j.Constraint.ImpulseScale;
            }
            var b = PointMass(mA, mB, iA, iB, rA, rB).Solve(cdot + bias);
            var impulse = -massScale * b - impulseScale * j.Linear;
            j.Linear += impulse;
            vA -= mA * impulse; wA -= iA * (V.Cross(rA, impulse) + axialImpulse);
            vB += mB * impulse; wB += iB * (V.Cross(rB, impulse) + axialImpulse);
        }
    }

    // Box2D's revolute limit row, which clamps the step (impulse ≥ −accumulated) rather than the total.
    private float BoundRevolute(in Joint j, float c, float cdot, ref float accumulated, bool useBias)
    {
        float bias = 0, massScale = 1, impulseScale = 0;
        if (c > 0) bias = c * _invH;
        else if (useBias) { bias = j.Constraint.BiasRate * c; massScale = j.Constraint.MassScale; impulseScale = j.Constraint.ImpulseScale; }
        float old = accumulated;
        float impulse = F.Max(-massScale * j.AxialMass * (cdot + bias) - impulseScale * old, -old);
        accumulated = old + impulse;
        return impulse;
    }

    private void SolveWeld(ref Joint j, in BodyState sa, in BodyState sb, ref Vector2 vA, ref float wA, ref Vector2 vB, ref float wB, bool useBias)
    {
        ref readonly var def = ref j.Def;
        float mA = j.MA, mB = j.MB, iA = j.IA, iB = j.IB;
        {
            var qA = Rot.Mul(sa.DQ, j.FrameA.Q); var qB = Rot.Mul(sb.DQ, j.FrameB.Q);
            float bias = 0, massScale = 1, impulseScale = 0;
            if (useBias || def.AngularHertz > 0)
            {
                bias = j.Spring2.BiasRate * Angle(Rot.InvMul(qA, qB));
                massScale = j.Spring2.MassScale; impulseScale = j.Spring2.ImpulseScale;
            }
            float impulse = -massScale * j.AxialMass * (wB - wA + bias) - impulseScale * j.Angular;
            j.Angular += impulse;
            wA -= iA * impulse; wB += iB * impulse;
        }
        {
            var rA = sa.DQ.Rotate(j.FrameA.P); var rB = sb.DQ.Rotate(j.FrameB.P);
            var bias = Vector2.Zero;
            float massScale = 1, impulseScale = 0;
            if (useBias || def.LinearHertz > 0)
            {
                var c = ((sb.DP - sa.DP) + (rB - rA)) + j.DeltaCenter;
                bias = j.Spring.BiasRate * c;
                massScale = j.Spring.MassScale; impulseScale = j.Spring.ImpulseScale;
            }
            var cdot = (vB + V.Cross(wB, rB)) - (vA + V.Cross(wA, rA));
            var b = PointMass(mA, mB, iA, iB, rA, rB).Solve(cdot + bias);
            var impulse = -massScale * b - impulseScale * j.Linear;
            j.Linear += impulse;
            Apply(j, impulse, V.Cross(rA, impulse), V.Cross(rB, impulse), ref vA, ref wA, ref vB, ref wB);
        }
    }

    private void SolveWheel(ref Joint j, in BodyState sa, in BodyState sb, ref Vector2 vA, ref float wA, ref Vector2 vB, ref float wB, bool useBias)
    {
        ref readonly var def = ref j.Def;
        float iA = j.IA, iB = j.IB;
        bool fixedRotation = iA + iB == 0;
        var rA = sa.DQ.Rotate(j.FrameA.P); var rB = sb.DQ.Rotate(j.FrameB.P);
        var d = ((sb.DP - sa.DP) + j.DeltaCenter) + (rB - rA);
        var axisA = sa.DQ.Rotate(j.FrameA.Q.Rotate(Vector2.UnitX));
        float translation = V.Dot(axisA, d);
        float a1 = V.Cross(d + rA, axisA), a2 = V.Cross(rB, axisA);
        float Rate(Vector2 vA, float wA, Vector2 vB, float wB) => V.Dot(axisA, vB - vA) + a2 * wB - a1 * wA;
        if (def.EnableMotor && !fixedRotation)
        {
            float impulse = -j.MotorMass * (wB - wA - def.MotorSpeed);
            float old = j.MotorImpulse, max = _h * def.MaxMotorForce;
            j.MotorImpulse = F.Clamp(old + impulse, -max, max);
            impulse = j.MotorImpulse - old;
            wA -= iA * impulse; wB += iB * impulse;
        }
        if (def.EnableSpring)
        {
            float impulse = -j.Spring.MassScale * j.AxialMass * (Rate(vA, wA, vB, wB) + j.Spring.BiasRate * translation) - j.Spring.ImpulseScale * j.SpringImpulse;
            j.SpringImpulse += impulse;
            Apply(j, impulse * axisA, impulse * a1, impulse * a2, ref vA, ref wA, ref vB, ref wB);
        }
        if (def.EnableLimit)
        {
            float lower = LimitRow(j, translation - def.Lower, Rate(vA, wA, vB, wB), j.AxialMass, ref j.LowerImpulse, useBias);
            Apply(j, lower * axisA, lower * a1, lower * a2, ref vA, ref wA, ref vB, ref wB);
            float upper = LimitRow(j, def.Upper - translation, -Rate(vA, wA, vB, wB), j.AxialMass, ref j.UpperImpulse, useBias);
            Apply(j, -upper * axisA, -upper * a1, -upper * a2, ref vA, ref wA, ref vB, ref wB);
        }
        {
            var perpA = LeftPerp(axisA);
            float bias = 0, massScale = 1, impulseScale = 0;
            if (useBias) { bias = j.Constraint.BiasRate * V.Dot(perpA, d); massScale = j.Constraint.MassScale; impulseScale = j.Constraint.ImpulseScale; }
            float s1 = V.Cross(d + rA, perpA), s2 = V.Cross(rB, perpA);
            float cdot = V.Dot(perpA, vB - vA) + s2 * wB - s1 * wA;
            float impulse = -massScale * j.PerpMass * (cdot + bias) - impulseScale * j.Perp;
            j.Perp += impulse;
            Apply(j, impulse * perpA, impulse * s1, impulse * s2, ref vA, ref wA, ref vB, ref wB);
        }
    }

    /// <summary>After the solve: joints that reached their thresholds become events, in graph order.</summary>
    private void CollectJointEvents()
    {
        _jointEventCount = 0;
        for (int k = 0; k < _jointOrderCount; k++)
        {
            int j = _jointOrder[k];
            if (!_joints[j].ThresholdHit) continue;
            _joints[j].ThresholdHit = false;
            if (_jointEventCount == _jointEvents.Length) Array.Resize(ref _jointEvents, _jointEventCount * 2);
            _jointEvents[_jointEventCount++] = new JointEvent2D { Joint = j, Owner = _joints[j].Owner };
        }
    }
}
