// Ported from Box2D (https://github.com/erincatto/box2d), Copyright (c) 2022 Erin Catto, under the MIT License;
// see THIRD_PARTY_NOTICES.md.
using System.Numerics;

namespace Gamegrl.Physics2D;

public enum JointType2D : byte
{
    /// <summary>Keeps two anchors at a distance: rigid, or a spring (with a force range), within optional limits, driven by an optional motor.</summary>
    Distance,
    /// <summary>No constraint: only stops the two bodies colliding (and ties them into one island).</summary>
    Filter,
    /// <summary>Drives body B's velocity relative to A (up to a force and torque), with optional springs pulling the frames together (dragging with the mouse).</summary>
    Motor,
    /// <summary>Drives body B's linear velocity relative to A, each axis up to its own force (character movers).</summary>
    Mover,
    /// <summary>A one-dimensional spring along a normal, pushing up to a compression force and pulling up to a tension force (a character's legs on the ground).</summary>
    Pogo,
    /// <summary>Body B slides along frame A's x axis without turning; optional spring toward a target, limits and motor.</summary>
    Prismatic,
    /// <summary>Pins the frames' origins together; the bodies turn freely about it, with an optional spring toward a target angle, limits and motor.</summary>
    Revolute,
    /// <summary>Glues two bodies together (springy when the hertz values are above 0).</summary>
    Weld,
    /// <summary>A wheel on a suspension: B slides along frame A's x axis on a spring, within optional limits, and turns freely, driven by an optional motor.</summary>
    Wheel,
}

/// <summary>
/// A joint's settings (Box2D's joint definitions in one struct). Each body carries a frame: an anchor point and an
/// orientation in its own space. Revolute angles, prismatic and wheel translations are measured between the frames;
/// prismatic and wheel joints slide along frame A's x axis. Body A may be absent (index −1): the joint then attaches to
/// the world, frame A being a world pose.
/// </summary>
public struct JointDef2D
{
    public JointType2D Type;
    public Transform2D LocalFrameA, LocalFrameB;
    public bool CollideConnected;
    /// <summary>Stiffness and damping of the joint's rigid parts (Box2D's 60 Hz and 2 by default; capped at a quarter of the substep rate).</summary>
    public float ConstraintHertz, ConstraintDampingRatio;
    /// <summary>A joint event is raised in any step where the joint's force or torque reaches these (default: never).</summary>
    public float ForceThreshold, TorqueThreshold;

    /// <summary>Distance, prismatic, revolute, wheel: a spring (prismatic toward <see cref="TargetTranslation"/>, revolute toward <see cref="TargetAngle"/>, distance toward <see cref="Length"/>, wheel toward zero). Pogo: its spring.</summary>
    public bool EnableSpring;
    public float Hertz, DampingRatio;
    public float TargetAngle, TargetTranslation;
    /// <summary>Distance: rest length. Pogo: rest length along frame B's y axis.</summary>
    public float Length;
    /// <summary>Distance: the spring's force range (tension negative); defaults to unbounded.</summary>
    public float LowerSpringForce, UpperSpringForce;

    /// <summary>Revolute: angle range in radians. Prismatic and wheel: translation range. Distance: length range.</summary>
    public bool EnableLimit;
    public float Lower, Upper;

    /// <summary>Distance, prismatic: force. Revolute, wheel: torque.</summary>
    public bool EnableMotor;
    public float MotorSpeed, MaxMotorForce;

    /// <summary>Weld: softness of the linear and angular parts (0 = rigid).</summary>
    public float LinearHertz, LinearDampingRatio, AngularHertz, AngularDampingRatio;

    /// <summary>Motor and mover: the velocity to drive body B at relative to A, and the most force (mover: per axis) and torque to use.</summary>
    public Vector2 LinearVelocity;
    public float AngularVelocity;
    public float MaxVelocityForce, MaxVelocityTorque;
    public Vector2 MaxAxisForce;
    /// <summary>Motor: springs pulling the frames together, up to a force and torque (their hertz are <see cref="LinearHertz"/> and <see cref="AngularHertz"/>).</summary>
    public float MaxSpringForce, MaxSpringTorque;

    /// <summary>Pogo: the ground normal, the most pull and push, and the spring's starting state.</summary>
    public Vector2 Normal;
    public float MaxTensionForce, MaxCompressionForce, Impulse, Velocity;

    public static JointDef2D Default(JointType2D type) => new()
    {
        Type = type, LocalFrameA = Transform2D.Identity, LocalFrameB = Transform2D.Identity,
        ConstraintHertz = 60, ConstraintDampingRatio = 2, ForceThreshold = float.MaxValue, TorqueThreshold = float.MaxValue,
        Length = 1, LowerSpringForce = float.MinValue, UpperSpringForce = float.MaxValue, Upper = float.MaxValue, Lower = type == JointType2D.Distance ? 0 : float.MinValue,
    };

    /// <summary>A hinge at the given anchors (in each body's frame).</summary>
    public static JointDef2D Revolute(Vector2 anchorA, Vector2 anchorB) => Default(JointType2D.Revolute) with { LocalFrameA = new Transform2D(anchorA, Rot.Identity), LocalFrameB = new Transform2D(anchorB, Rot.Identity) };
    /// <summary>A rod (or spring, with <see cref="EnableSpring"/>) between the anchors, of the given length.</summary>
    public static JointDef2D Distance(Vector2 anchorA, Vector2 anchorB, float length) => Default(JointType2D.Distance) with { LocalFrameA = new Transform2D(anchorA, Rot.Identity), LocalFrameB = new Transform2D(anchorB, Rot.Identity), Length = length, Lower = 0, Upper = float.MaxValue };
    /// <summary>A slider along <paramref name="axis"/> (in body A's frame) through the anchors.</summary>
    public static JointDef2D Prismatic(Vector2 anchorA, Vector2 anchorB, Vector2 axis)
    {
        var a = SimMath.Normalize(axis);
        return Default(JointType2D.Prismatic) with { LocalFrameA = new Transform2D(anchorA, new Rot { C = a.X, S = a.Y }), LocalFrameB = new Transform2D(anchorB, new Rot { C = a.X, S = a.Y }) };
    }
    /// <summary>A weld at the anchors.</summary>
    public static JointDef2D Weld(Vector2 anchorA, Vector2 anchorB) => Default(JointType2D.Weld) with { LocalFrameA = new Transform2D(anchorA, Rot.Identity), LocalFrameB = new Transform2D(anchorB, Rot.Identity) };
    /// <summary>A wheel at the anchors, its suspension along <paramref name="axis"/> (in body A's frame).</summary>
    public static JointDef2D Wheel(Vector2 anchorA, Vector2 anchorB, Vector2 axis)
    {
        var a = SimMath.Normalize(axis);
        return Default(JointType2D.Wheel) with { LocalFrameA = new Transform2D(anchorA, new Rot { C = a.X, S = a.Y }), LocalFrameB = new Transform2D(anchorB, Rot.Identity), EnableSpring = true, Hertz = 1, DampingRatio = 0.7f };
    }
    /// <summary>Drag body B's anchor toward a world point with a spring, up to a force (attach to the world: body A −1).</summary>
    public static JointDef2D Target(Vector2 target, Vector2 anchorB, float hertz, float dampingRatio, float maxForce) => Default(JointType2D.Motor) with
    {
        LocalFrameA = new Transform2D(target, Rot.Identity), LocalFrameB = new Transform2D(anchorB, Rot.Identity),
        LinearHertz = hertz, LinearDampingRatio = dampingRatio, MaxSpringForce = maxForce, MaxVelocityTorque = 0.1f * maxForce,
    };
}

/// <summary>A joint whose force or torque reached its threshold this step.</summary>
public struct JointEvent2D
{
    public int Joint;
    public Entity Owner;
}
