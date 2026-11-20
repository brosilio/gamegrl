using System.Numerics;

namespace Gamegrl.Audio.Acoustics;

/// <summary>
/// A specular reflection path from a source to the listener: its total length, the reflection point nearest the
/// listener (where it's heard from), how many surfaces it met, and its per-band amplitude factor from those surfaces
/// (√((1 − absorption)·(1 − scattering)) each: the energy reflected specularly).
/// <para>
/// It also keeps the planes it reflects in, so its image source can follow a moving source and listener between
/// searches (<see cref="ImageOf"/>): the length and direction heard are then exact every frame, and a reflection's
/// delay changes smoothly, at the true Doppler rate, rather than jumping when a new search lands.
/// </para>
/// </summary>
public struct ReflectionPath
{
    public float Length;
    public Vector3 Point;
    /// <summary>The reflection point nearest the source (the same as <see cref="Point"/> for first order).</summary>
    public Vector3 First;
    public int Order;
    public Bands3 Surfaces;
    /// <summary>The image source when it was found (the source mirrored in each plane in turn).</summary>
    public Vector3 Image;
    /// <summary>The planes, nearest the source first (the same twice for first order).</summary>
    public AcousticPlane FirstPlane, LastPlane;
    /// <summary>Which planes, in order: the same path from one search to the next has the same key (never 0).</summary>
    public int Key;

    /// <summary>The image of a source at <paramref name="source"/> in this path's planes.</summary>
    public readonly Vector3 ImageOf(Vector3 source) => Order == 1 ? FirstPlane.Mirror(source) : LastPlane.Mirror(FirstPlane.Mirror(source));
}

/// <summary>
/// Early reflections by the image-source method (Allen and Berkley 1979; Borish 1984 for arbitrary planes): the source
/// mirrored in each plane of the scene is a first-order image, mirrored again in another plane a second-order one. An
/// image is real only if the path it implies works: walking back from the listener toward the image, the ray must hit
/// a triangle of the last plane first; from there toward the previous image, a triangle of the plane before; and the
/// last leg to the source must be clear. Exact for planar geometry, so a shoebox room gives its textbook 6 first-order
/// and 18 second-order reflections at exact delays.
/// </summary>
public static class ImageSources
{
    /// <summary>
    /// Finds valid paths up to <paramref name="maxOrder"/> (1 or 2), strongest first by distance and surfaces, into
    /// <paramref name="paths"/>; returns how many. Planes are taken largest and nearest first, up to
    /// <paramref name="maxPlanes"/> for first order and the square root of the budget for second.
    /// </summary>
    public static int Find(AcousticScene scene, Vector3 listener, Vector3 source, Span<ReflectionPath> paths, int maxOrder = 2, int maxPlanes = 64)
    {
        var planes = scene.Planes;
        if (planes.Length == 0 || paths.Length == 0) return 0;
        // Rank planes: big and near matter most.
        int n = Math.Min(planes.Length, maxPlanes);
        Span<int> order = stackalloc int[planes.Length];
        Span<float> weight = stackalloc float[planes.Length];
        for (int i = 0; i < planes.Length; i++)
        {
            order[i] = i;
            float d = MathF.Abs(planes[i].Distance(listener)) + 0.5f;
            weight[i] = planes[i].Area / (d * d);
        }
        for (int i = 1; i < order.Length; i++) // insertion sort, descending weight (a handful to a few hundred planes)
        {
            int k = order[i]; float w = weight[k]; int j = i - 1;
            while (j >= 0 && weight[order[j]] < w) { order[j + 1] = order[j]; j--; }
            order[j + 1] = k;
        }

        int count = 0;
        void Keep(in ReflectionPath p, Span<ReflectionPath> paths)
        {
            float score = p.Surfaces.Mid / p.Length;
            if (count < paths.Length) { paths[count++] = p; return; }
            int weakest = 0;
            for (int i = 1; i < count; i++) if (paths[i].Surfaces.Mid / paths[i].Length < paths[weakest].Surfaces.Mid / paths[weakest].Length) weakest = i;
            if (score > paths[weakest].Surfaces.Mid / paths[weakest].Length) paths[weakest] = p;
        }

        for (int a = 0; a < n; a++)
        {
            int pa = order[a];
            var plane = planes[pa];
            if (!SameSide(plane, listener, source)) continue;
            var image = plane.Mirror(source);
            if (!LegHits(scene, pa, listener, image, out var point, out var bands)) continue;
            if (scene.Blocked(Off(point, plane, source), source)) continue;
            Keep(new ReflectionPath { Length = Vector3.Distance(listener, image), Point = point, First = point, Order = 1, Surfaces = bands,
                Image = image, FirstPlane = plane, LastPlane = plane, Key = pa + 1 }, paths);
        }
        if (maxOrder < 2) return Sort(paths[..count]);

        int n2 = Math.Min(n, (int)MathF.Ceiling(MathF.Sqrt(maxPlanes * 4)));
        for (int a = 0; a < n2; a++)        // first reflection (nearest the source)
        for (int b = 0; b < n2; b++)        // second reflection (nearest the listener)
        {
            if (a == b) continue;
            int pa = order[a], pb = order[b];
            var first = planes[pa]; var second = planes[pb];
            if (!SameSide(first, source, source)) continue;
            var image1 = first.Mirror(source);
            var image2 = second.Mirror(image1);
            if (!SameSide(second, listener, image1)) continue;
            if (!LegHits(scene, pb, listener, image2, out var point2, out var bands2)) continue;
            if (!SameSide(first, point2, source)) continue;
            if (!LegHits(scene, pa, Off(point2, second, listener), image1, out var point1, out var bands1)) continue;
            if (scene.Blocked(Off(point1, first, source), source)) continue;
            Keep(new ReflectionPath { Length = Vector3.Distance(listener, image2), Point = point2, First = point1, Order = 2, Surfaces = bands1 * bands2,
                Image = image2, FirstPlane = first, LastPlane = second, Key = (pa + 1) | ((pb + 1) << 16) }, paths);
        }
        return Sort(paths[..count]);
    }

    private static int Sort(Span<ReflectionPath> paths)
    {
        paths.Sort(static (x, y) => (y.Surfaces.Mid / y.Length).CompareTo(x.Surfaces.Mid / x.Length));
        return paths.Length;
    }

    private static bool SameSide(in AcousticPlane plane, Vector3 a, Vector3 b)
    {
        float da = plane.Distance(a), db = plane.Distance(b);
        return (da > 1e-4f && db > 1e-4f) || (da < -1e-4f && db < -1e-4f);
    }

    /// <summary>A point just off a plane, on the side of <paramref name="toward"/> (so rays from it don't hit the plane itself).</summary>
    private static Vector3 Off(Vector3 point, in AcousticPlane plane, Vector3 toward) =>
        point + plane.Normal * (plane.Distance(toward) > 0 ? 1e-3f : -1e-3f);

    /// <summary>
    /// Walks from <paramref name="from"/> toward an image: the first thing the ray meets must be a triangle of plane
    /// <paramref name="plane"/>, before the image. Gives the reflection point and that triangle's specular amplitude factor.
    /// </summary>
    private static bool LegHits(AcousticScene scene, int plane, Vector3 from, Vector3 image, out Vector3 point, out Bands3 bands)
    {
        point = default; bands = default;
        var dir = image - from;
        if (!scene.Raycast(from, dir, 1, out var hit) || scene.PlaneOf(hit.Triangle) != plane) return false;
        point = from + dir * hit.Distance;
        var m = scene.MaterialOf(hit.Triangle);
        float specular = 1 - m.Scattering;
        bands = new Bands3(MathF.Sqrt((1 - m.Absorption.Low) * specular), MathF.Sqrt((1 - m.Absorption.Mid) * specular), MathF.Sqrt((1 - m.Absorption.High) * specular));
        return true;
    }
}
