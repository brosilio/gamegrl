using System.Numerics;

namespace Gamegrl.Rendering;

public readonly record struct MeshHandle(int Id);
public readonly record struct TextureHandle(int Id);

/// <summary>
/// A 3D perspective camera; its pose comes from the entity's <see cref="LocalToWorld"/>. It renders N views:
/// one on a desktop window, two in a headset. TODO(milestone 7): view arrays and multiview.
/// </summary>
public struct Camera : IComponent
{
    public float FovYRadians;
    public float Near;
    public Vector4 ClearColor;

    public static Camera Perspective(float fovYDegrees = 60, float near = 0.1f) =>
        new() { FovYRadians = float.DegreesToRadians(fovYDegrees), Near = near, ClearColor = new Vector4(0.05f, 0.06f, 0.09f, 1) };

    /// <summary>
    /// A right-handed, reverse-Z, infinite-far projection (near maps to depth 1, infinity to 0), in
    /// System.Numerics' row-vector convention. Reverse-Z keeps depth precision even across huge distances.
    /// </summary>
    public readonly Matrix4x4 Projection(float aspect)
    {
        float f = 1f / MathF.Tan(FovYRadians * 0.5f);
        return new Matrix4x4(
            f / aspect, 0, 0, 0,
            0, f, 0, 0,
            0, 0, 0, -1,
            0, 0, Near, 0);
    }
}

/// <summary>Draws a mesh at the entity's <see cref="LocalToWorld"/>.</summary>
public struct MeshRenderer : IComponent
{
    public MeshHandle Mesh;
    public Vector4 Color;
}

/// <summary>
/// A 2D sprite, positioned in screen pixels (origin bottom-left, y up) by the entity's <see cref="LocalTransform"/>:
/// position x/y, rotation about z, scale x/y.
/// TODO(milestone 2): 2D cameras and render-to-texture targets.
/// </summary>
public struct Sprite : IComponent
{
    public TextureHandle Texture;
    public Vector2 Size;
    public Vector4 Color;
    /// <summary>The point rotation and position refer to, in 0..1 of the size (0.5, 0.5 = center).</summary>
    public Vector2 Pivot;
    /// <summary>u0, v0, u1, v1; (0, 0, 1, 1) is the whole texture.</summary>
    public Vector4 UvRect;

    public static Sprite Of(TextureHandle texture, Vector2 size) =>
        new() { Texture = texture, Size = size, Color = Vector4.One, Pivot = new Vector2(0.5f), UvRect = new Vector4(0, 0, 1, 1) };

    public Sprite Tinted(Vector4 color)
    {
        Color = color;
        return this;
    }
}

/// <summary>The drawable size in pixels, as a world resource (updated every frame).</summary>
public struct ScreenSize
{
    public int Width;
    public int Height;
}

/// <summary>A mesh vertex: position and normal.</summary>
public struct MeshVertex(Vector3 position, Vector3 normal)
{
    public Vector3 Position = position;
    public Vector3 Normal = normal;
}
