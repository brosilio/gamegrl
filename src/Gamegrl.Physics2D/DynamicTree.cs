using System.Numerics;

namespace Gamegrl.Physics2D;

/// <summary>Called for each leaf a tree query finds; return false to stop.</summary>
internal interface ITreeQuery
{
    bool Hit(int proxy, int item);
}

/// <summary>
/// A dynamic AABB tree (after Box2D): leaves hold fat boxes, internal nodes their unions. Inserts pick the sibling
/// that adds the least surface area; rotations keep it balanced. Queries walk it with a fixed-order stack, so results
/// come back in the same order on every run. Allocation-free once grown; copyable for snapshots.
/// </summary>
internal sealed class DynamicTree
{
    private struct Node
    {
        public Aabb Box;
        public int Parent, Child1, Child2, Height, Item;
        public readonly bool IsLeaf => Child1 < 0;
    }

    private Node[] _nodes = new Node[16];
    private int _root = -1, _free = -1, _count;
    private int[] _stack = new int[64];

    public DynamicTree Clone()
    {
        var t = (DynamicTree)MemberwiseClone();
        t._nodes = (Node[])_nodes.Clone();
        t._stack = new int[_stack.Length];
        return t;
    }

    public Aabb Box(int proxy) => _nodes[proxy].Box;
    public int Item(int proxy) => _nodes[proxy].Item;

    private int Allocate()
    {
        if (_free < 0)
        {
            int old = _nodes.Length;
            Array.Resize(ref _nodes, old * 2);
            for (int i = old; i < _nodes.Length - 1; i++) { _nodes[i].Parent = i + 1; _nodes[i].Height = -1; }
            _nodes[^1].Parent = -1; _nodes[^1].Height = -1;
            _free = old;
        }
        int n = _free;
        _free = _nodes[n].Parent;
        _nodes[n] = new Node { Parent = -1, Child1 = -1, Child2 = -1, Height = 0, Item = -1 };
        _count++;
        return n;
    }

    private void Release(int n)
    {
        _nodes[n].Parent = _free;
        _nodes[n].Height = -1;
        _free = n;
        _count--;
    }

    public DynamicTree()
    {
        for (int i = 0; i < _nodes.Length - 1; i++) { _nodes[i].Parent = i + 1; _nodes[i].Height = -1; }
        _nodes[^1].Parent = -1; _nodes[^1].Height = -1;
        _free = 0;
    }

    public int Create(in Aabb box, int item)
    {
        int leaf = Allocate();
        _nodes[leaf].Box = box;
        _nodes[leaf].Item = item;
        Insert(leaf);
        return leaf;
    }

    public void Destroy(int proxy)
    {
        Remove(proxy);
        Release(proxy);
    }

    public void Move(int proxy, in Aabb box)
    {
        Remove(proxy);
        _nodes[proxy].Box = box;
        Insert(proxy);
    }

    private static float Perimeter(in Aabb b) => 2 * ((b.Max.X - b.Min.X) + (b.Max.Y - b.Min.Y));
    private static Aabb Union(in Aabb a, in Aabb b) => new() { Min = Vector2.Min(a.Min, b.Min), Max = Vector2.Max(a.Max, b.Max) };

    private void Insert(int leaf)
    {
        if (_root < 0) { _root = leaf; _nodes[leaf].Parent = -1; return; }
        // Descend to the sibling that grows the tree's surface area least.
        var box = _nodes[leaf].Box;
        int index = _root;
        while (!_nodes[index].IsLeaf)
        {
            int c1 = _nodes[index].Child1, c2 = _nodes[index].Child2;
            float area = Perimeter(_nodes[index].Box);
            float combined = Perimeter(Union(_nodes[index].Box, box));
            float cost = 2 * combined;               // a new parent here
            float inherit = 2 * (combined - area);   // what every level below pays
            float Cost(int child)
            {
                var u = Union(box, _nodes[child].Box);
                return _nodes[child].IsLeaf ? Perimeter(u) + inherit : Perimeter(u) - Perimeter(_nodes[child].Box) + inherit;
            }
            float cost1 = Cost(c1), cost2 = Cost(c2);
            if (cost < cost1 && cost < cost2) break;
            index = cost1 <= cost2 ? c1 : c2;
        }
        int sibling = index;
        int oldParent = _nodes[sibling].Parent;
        int parent = Allocate();
        _nodes[parent].Parent = oldParent;
        _nodes[parent].Box = Union(box, _nodes[sibling].Box);
        _nodes[parent].Height = _nodes[sibling].Height + 1;
        _nodes[parent].Child1 = sibling;
        _nodes[parent].Child2 = leaf;
        _nodes[sibling].Parent = parent;
        _nodes[leaf].Parent = parent;
        if (oldParent < 0) _root = parent;
        else if (_nodes[oldParent].Child1 == sibling) _nodes[oldParent].Child1 = parent;
        else _nodes[oldParent].Child2 = parent;
        Refit(parent);
    }

    private void Remove(int leaf)
    {
        if (leaf == _root) { _root = -1; return; }
        int parent = _nodes[leaf].Parent, grand = _nodes[parent].Parent;
        int sibling = _nodes[parent].Child1 == leaf ? _nodes[parent].Child2 : _nodes[parent].Child1;
        if (grand >= 0)
        {
            if (_nodes[grand].Child1 == parent) _nodes[grand].Child1 = sibling; else _nodes[grand].Child2 = sibling;
            _nodes[sibling].Parent = grand;
            Release(parent);
            Refit(grand);
        }
        else
        {
            _root = sibling;
            _nodes[sibling].Parent = -1;
            Release(parent);
        }
    }

    /// <summary>Walk up from <paramref name="index"/>, balancing and refitting.</summary>
    private void Refit(int index)
    {
        while (index >= 0)
        {
            index = Balance(index);
            int c1 = _nodes[index].Child1, c2 = _nodes[index].Child2;
            _nodes[index].Height = 1 + Math.Max(_nodes[c1].Height, _nodes[c2].Height);
            _nodes[index].Box = Union(_nodes[c1].Box, _nodes[c2].Box);
            index = _nodes[index].Parent;
        }
    }

    /// <summary>An AVL-style rotation when one subtree is two levels taller; returns the subtree's new root.</summary>
    private int Balance(int a)
    {
        ref var A = ref _nodes[a];
        if (A.IsLeaf || A.Height < 2) return a;
        int b = A.Child1, c = A.Child2;
        int balance = _nodes[c].Height - _nodes[b].Height;
        if (balance > 1) return Rotate(a, c, b);
        if (balance < -1) return Rotate(a, b, c);
        return a;
    }

    /// <summary>Promote <paramref name="up"/> (the taller child of <paramref name="a"/>) above it.</summary>
    private int Rotate(int a, int up, int other)
    {
        int f = _nodes[up].Child1, g = _nodes[up].Child2;
        _nodes[up].Child1 = a;
        _nodes[up].Parent = _nodes[a].Parent;
        _nodes[a].Parent = up;
        int parent = _nodes[up].Parent;
        if (parent >= 0) { if (_nodes[parent].Child1 == a) _nodes[parent].Child1 = up; else _nodes[parent].Child2 = up; }
        else _root = up;
        // Keep the taller grandchild under `up`, give the other to `a`.
        int keep = _nodes[f].Height > _nodes[g].Height ? f : g, give = keep == f ? g : f;
        _nodes[up].Child2 = keep;
        if (_nodes[a].Child1 == up) _nodes[a].Child1 = give; else _nodes[a].Child2 = give;
        _nodes[give].Parent = a;
        _nodes[a].Box = Union(_nodes[other].Box, _nodes[give].Box);
        _nodes[a].Height = 1 + Math.Max(_nodes[other].Height, _nodes[give].Height);
        _nodes[up].Box = Union(_nodes[a].Box, _nodes[keep].Box);
        _nodes[up].Height = 1 + Math.Max(_nodes[a].Height, _nodes[keep].Height);
        return up;
    }

    /// <summary>Every leaf whose box overlaps <paramref name="box"/>, in a fixed order.</summary>
    public void Query<T>(in Aabb box, ref T callback) where T : struct, ITreeQuery
    {
        if (_root < 0) return;
        // A stack per call (not shared), so several threads can query at once.
        Span<int> stack = stackalloc int[256];
        int top = 0;
        stack[top++] = _root;
        while (top > 0)
        {
            int n = stack[--top];
            ref readonly var node = ref _nodes[n];
            if (!node.Box.Overlaps(box)) continue;
            if (node.IsLeaf)
            {
                if (!callback.Hit(n, node.Item)) return;
                continue;
            }
            if (top + 2 > stack.Length) { var bigger = new int[stack.Length * 2]; stack.CopyTo(bigger); stack = bigger; }
            stack[top++] = node.Child2;
            stack[top++] = node.Child1;
        }
    }

    /// <summary>Leaves whose boxes the segment from <paramref name="origin"/> along <paramref name="translation"/> (up to <paramref name="maxFraction"/>) crosses.</summary>
    public void RayQuery<T>(Vector2 origin, Vector2 translation, float maxFraction, ref T callback) where T : struct, ITreeQuery
    {
        if (_root < 0) return;
        var end = origin + translation * maxFraction;
        var segment = new Aabb { Min = Vector2.Min(origin, end), Max = Vector2.Max(origin, end) };
        int top = 0;
        _stack[top++] = _root;
        while (top > 0)
        {
            int n = _stack[--top];
            ref readonly var node = ref _nodes[n];
            if (!node.Box.Overlaps(segment) || !SlabHit(node.Box, origin, translation, maxFraction)) continue;
            if (node.IsLeaf) { if (!callback.Hit(n, node.Item)) return; continue; }
            if (top + 2 > _stack.Length) Array.Resize(ref _stack, _stack.Length * 2);
            _stack[top++] = node.Child2;
            _stack[top++] = node.Child1;
        }
    }

    private static bool SlabHit(in Aabb b, Vector2 o, Vector2 d, float maxFraction)
    {
        float lo = 0, hi = maxFraction;
        for (int axis = 0; axis < 2; axis++)
        {
            float origin = axis == 0 ? o.X : o.Y, dir = axis == 0 ? d.X : d.Y;
            float min = axis == 0 ? b.Min.X : b.Min.Y, max = axis == 0 ? b.Max.X : b.Max.Y;
            if (dir == 0) { if (origin < min || origin > max) return false; continue; }
            float t1 = (min - origin) / dir, t2 = (max - origin) / dir;
            if (t1 > t2) (t1, t2) = (t2, t1);
            lo = Math.Max(lo, t1); hi = Math.Min(hi, t2);
            if (lo > hi) return false;
        }
        return true;
    }

    public int Height => _root < 0 ? 0 : _nodes[_root].Height;
}
