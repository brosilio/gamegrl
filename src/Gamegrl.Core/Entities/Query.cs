using System.Runtime.InteropServices;

namespace Gamegrl;

/// <summary>What a query matches: archetypes having all of <see cref="All"/> and none of <see cref="None"/>.</summary>
public struct QueryDesc : IEquatable<QueryDesc>
{
    public ComponentSet All;
    public ComponentSet None;

    public readonly QueryDesc With<T>() where T : unmanaged, IComponent
    {
        var d = this;
        d.All.Add(Component<T>.Id);
        return d;
    }

    public readonly QueryDesc Without<T>() where T : unmanaged, IComponent
    {
        var d = this;
        d.None.Add(Component<T>.Id);
        return d;
    }

    public readonly bool Matches(in ComponentSet set) => set.ContainsAll(All) && !set.Intersects(None);

    public readonly bool Equals(QueryDesc other) => All == other.All && None == other.None;
    public override readonly bool Equals(object? obj) => obj is QueryDesc d && Equals(d);
    public override readonly int GetHashCode() => HashCode.Combine(All, None);
}

/// <summary>
/// A cached query: the list of matching archetypes is kept up to date as archetypes are created, so running a
/// query never searches. Get one from <see cref="World.Query"/>.
/// </summary>
public sealed class Query
{
    private readonly List<Archetype> _matches = new();

    public readonly QueryDesc Desc;
    public readonly World World;

    internal Query(World world, in QueryDesc desc)
    {
        World = world;
        Desc = desc;
    }

    /// <summary>The matching archetypes. Some may be empty.</summary>
    public ReadOnlySpan<Archetype> Archetypes => CollectionsMarshal.AsSpan(_matches);

    /// <summary>The number of matching entities (sums archetype counts).</summary>
    public int Count()
    {
        int n = 0;
        foreach (var a in Archetypes) n += a.Count;
        return n;
    }

    internal void Consider(Archetype archetype)
    {
        if (Desc.Matches(archetype.Set)) _matches.Add(archetype);
    }
}
