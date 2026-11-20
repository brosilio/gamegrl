using System.Numerics;

namespace Gamegrl.Physics2D;

/// <summary>A chain's material and shape: a closed loop, or an open run with ghost points beyond each end.</summary>
public struct ChainDef2D
{
    public float Friction, Restitution;
    public Filter2D Filter;
    /// <summary>Close the chain: the last point joins the first. A counter-clockwise loop collides on its outside.</summary>
    public bool IsLoop;
    /// <summary>Open chains: where the line would continue before the first point and after the last (smooth ends).</summary>
    public Vector2 Ghost1, Ghost2;

    public static ChainDef2D Default => new() { Friction = 0.6f, Filter = Filter2D.Default };
}

public sealed partial class PhysicsWorld2D
{
    private struct Chain
    {
        public int Body;
        public int[]? Segments;
    }

    private Chain[] _chains = new Chain[4];
    private int _chainHigh;
    private int[] _freeChains = new int[4];
    private int _freeChainCount;

    /// <summary>
    /// A chain of one-sided segments through <paramref name="points"/> (terrain): each segment collides on its right,
    /// and knows its neighbours, so shapes slide along the chain without catching on the joins. Chains have no mass.
    /// </summary>
    public int CreateChain(int body, Entity entity, ReadOnlySpan<Vector2> points, in ChainDef2D def)
    {
        int n = points.Length;
        if (def.IsLoop ? n < 3 : n < 2) throw new ArgumentException("A chain needs at least 2 points (3 for a loop).");
        int c;
        if (_freeChainCount > 0) c = _freeChains[--_freeChainCount];
        else
        {
            if (_chainHigh == _chains.Length) Array.Resize(ref _chains, _chains.Length * 2);
            c = _chainHigh++;
        }
        int segmentCount = def.IsLoop ? n : n - 1;
        var segments = new int[segmentCount];
        var shapeDef = new ShapeDef2D { Density = 0, Friction = def.Friction, Restitution = def.Restitution, Filter = def.Filter };
        for (int i = 0; i < segmentCount; i++)
        {
            Shape shape;
            if (def.IsLoop)
                shape = Shape.ChainSegment(points[i == 0 ? n - 1 : i - 1], points[i], points[(i + 1) % n], points[(i + 2) % n]);
            else
                shape = Shape.ChainSegment(i == 0 ? def.Ghost1 : points[i - 1], points[i], points[i + 1], i == segmentCount - 1 ? def.Ghost2 : points[i + 2]);
            int s = CreateShape(body, entity, shape, shapeDef);
            ChainOf[s] = c;
            segments[i] = s;
        }
        _chains[c] = new Chain { Body = body, Segments = segments };
        return c;
    }

    public bool IsChainAlive(int chain) => (uint)chain < (uint)_chainHigh && _chains[chain].Segments is not null;

    /// <summary>The chain's segment shapes, in order.</summary>
    public ReadOnlySpan<int> ChainSegments(int chain) => _chains[chain].Segments;

    public void DestroyChain(int chain)
    {
        if (!IsChainAlive(chain)) return;
        foreach (int s in _chains[chain].Segments!) { ChainOf[s] = -1; DestroyShape(s); }
        _chains[chain] = default;
        if (_freeChainCount == _freeChains.Length) Array.Resize(ref _freeChains, _freeChainCount * 2);
        _freeChains[_freeChainCount++] = chain;
    }

    private void CloneChains(PhysicsWorld2D w)
    {
        w._chains = (Chain[])_chains.Clone();
        for (int c = 0; c < _chainHigh; c++) if (_chains[c].Segments is { } s) w._chains[c].Segments = (int[])s.Clone();
        w._freeChains = (int[])_freeChains.Clone();
    }
}
