// Ported from Box2D (https://github.com/erincatto/box2d), Copyright (c) 2022 Erin Catto, under the MIT License;
// see THIRD_PARTY_NOTICES.md.
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Gamegrl.Physics2D;

/// <summary>Called for each leaf a tree overlap query finds; return false to stop.</summary>
public interface ITreeQuery
{
    bool Hit(int proxyId, int userData);
}

/// <summary>Called for each leaf a tree cast reaches: return 0 to stop, a fraction to clip the cast, the input's max to go on, or -1 to skip the leaf.</summary>
public interface ITreeCast
{
    float Hit(Vector2 origin, Vector2 translation, float maxFraction, int proxyId, int userData);
}

/// <summary>Nodes and leaves a tree query visited (for benchmarks).</summary>
public struct TreeStats
{
    public int NodeVisits, LeafVisits;
    public static TreeStats operator +(TreeStats a, TreeStats b) => new() { NodeVisits = a.NodeVisits + b.NodeVisits, LeafVisits = a.LeafVisits + b.LeafVisits };
}

/// <summary>A tree node: a box, then the leaf and moved flags with a child pair or proxy index, then the height or shape.</summary>
[StructLayout(LayoutKind.Sequential, Size = 32)]
internal struct TreeNode
{
    public Aabb Box;
    public uint FlagIndex;
    public int HeightOrShape; // internal: height; leaf: the proxy's user data (a shape index)
}

/// <summary>
/// A dynamic AABB tree (a port of Box2D v3's dynamic_tree.c by Erin Catto). Nodes are 32 bytes and siblings sit side by
/// side, so a pair is one cache line and a node needs only its first child's index. Inserting picks the sibling by the
/// surface area heuristic with branch and bound, and rotations by area keep it tight. Moving proxies mark their
/// ancestors; a linear refit then fixes their boxes, and a partial rebuild (median splits, depth-first order) remakes
/// just the moved subtrees, which keeps the tree as good as new and cache friendly. Pair finding collides moved
/// subtrees with each other (<see cref="PhysicsWorld2D"/>). Queries walk with a stack on the caller's thread, so any
/// number may run at once while nothing changes the tree. Allocation-free once grown; copyable for snapshots.
/// </summary>
public sealed class DynamicTree
{
    internal const uint MovedNode = 1u << 30, LeafNode = 1u << 31, IndexMask = ~(MovedNode | LeafNode), EmptyNode = IndexMask | LeafNode;
    internal const int Root = 0, Null = -1, StackSize = 512;

    private struct Proxy
    {
        public int UserData;
        public ulong CategoryBits;
        public int Node; // Null for a free proxy
        public int Next; // free list
    }

    internal TreeNode[] Nodes;
    private int[] _parents;
    private Proxy[] _proxies;
    private int _nodeEnd, _pairFreeList, _proxyCount, _proxyFreeList;
    private bool _dfsOrdered;
    // Rebuild scratch
    private TreeNode[] _swapNodes = [];
    private int[] _leafIndices = [];
    private TreeNode[] _leafNodes = [];
    private Vector2[] _leafCenters = [];

    public DynamicTree(int proxyCapacity = 16)
    {
        int capacity = Math.Max(proxyCapacity, 16);
        Nodes = new TreeNode[2 * capacity];
        _parents = new int[2 * capacity];
        Nodes[Root] = Empty; Nodes[Root + 1] = Empty;
        _parents[Root] = Null; _parents[Root + 1] = Null;
        _nodeEnd = 2;
        _pairFreeList = Null;
        _dfsOrdered = true;
        _proxies = new Proxy[capacity];
        for (int i = 0; i < capacity; i++) _proxies[i] = new Proxy { Node = Null, Next = i + 1 < capacity ? i + 1 : Null };
        _proxyFreeList = 0;
    }

    public DynamicTree Clone()
    {
        var t = (DynamicTree)MemberwiseClone();
        t.Nodes = (TreeNode[])Nodes.Clone(); t._parents = (int[])_parents.Clone(); t._proxies = (Proxy[])_proxies.Clone();
        t._swapNodes = []; t._leafIndices = []; t._leafNodes = []; t._leafCenters = [];
        return t;
    }

    public int ProxyCount => _proxyCount;
    public int Height => GetHeight(Nodes[Root]);
    internal bool HasMoved => IsMoved(Nodes[Root]);
    internal bool NeedsRebuild => IsMoved(Nodes[Root]) || !_dfsOrdered;
    internal int NodeEnd => _nodeEnd;

    public Aabb ProxyBounds(int proxyId) => Nodes[_proxies[proxyId].Node].Box;
    public int UserData(int proxyId) => _proxies[proxyId].UserData;
    public ulong CategoryBits(int proxyId) => _proxies[proxyId].CategoryBits;
    public void SetCategoryBits(int proxyId, ulong bits) => _proxies[proxyId].CategoryBits = bits;

    private static TreeNode Empty => new() { Box = new Aabb { Min = new Vector2(float.PositiveInfinity), Max = new Vector2(float.NegativeInfinity) }, FlagIndex = EmptyNode };
    [MethodImpl(MethodImplOptions.AggressiveInlining)] internal static bool IsLeaf(in TreeNode n) => (n.FlagIndex & LeafNode) != 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] internal static bool IsMoved(in TreeNode n) => (n.FlagIndex & MovedNode) != 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] internal static int Child(in TreeNode n) => (int)(n.FlagIndex & IndexMask);
    private static bool IsEmpty(in TreeNode n) => n.FlagIndex == EmptyNode;
    private static int GetHeight(in TreeNode n) => IsLeaf(n) ? 0 : n.HeightOrShape;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] internal int RootPair() => IsLeaf(Nodes[Root]) ? Root : Child(Nodes[Root]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Perimeter(in Aabb b) => 2 * ((b.Max.X - b.Min.X) + (b.Max.Y - b.Min.Y));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Aabb Union(in Aabb a, in Aabb b) => new() { Min = SimMath.Min(a.Min, b.Min), Max = SimMath.Max(a.Max, b.Max) };

    // ---------------------------------------------------------------- allocation

    private int AllocatePair()
    {
        if (_pairFreeList != Null)
        {
            int pair = _pairFreeList;
            _pairFreeList = _parents[pair];
            return pair;
        }
        if (_nodeEnd + 2 > Nodes.Length)
        {
            int capacity = Nodes.Length + (Nodes.Length >> 1);
            capacity += capacity & 1;
            Array.Resize(ref Nodes, capacity);
            Array.Resize(ref _parents, capacity);
            _swapNodes = []; // must match; the rebuild makes it again
        }
        int p = _nodeEnd;
        _nodeEnd += 2;
        return p;
    }

    private void FreePair(int pair)
    {
        Nodes[pair] = Empty; Nodes[pair + 1] = Empty;
        _parents[pair] = _pairFreeList;
        _parents[pair + 1] = Null;
        _pairFreeList = pair;
    }

    private int AllocateProxy()
    {
        if (_proxyFreeList == Null)
        {
            int old = _proxies.Length;
            Array.Resize(ref _proxies, old + Math.Max(old >> 1, 1));
            for (int i = old; i < _proxies.Length; i++) _proxies[i] = new Proxy { Node = Null, Next = i + 1 < _proxies.Length ? i + 1 : Null };
            _proxyFreeList = old;
        }
        int id = _proxyFreeList;
        _proxyFreeList = _proxies[id].Next;
        _proxies[id] = new Proxy { Node = Null, Next = Null };
        _proxyCount++;
        return id;
    }

    private void FreeProxy(int id)
    {
        _proxies[id].Node = Null;
        _proxies[id].Next = _proxyFreeList;
        _proxyFreeList = id;
        _proxyCount--;
    }

    private static TreeNode MakeInternal(TreeNode[] nodes, int pair)
    {
        ref readonly var c1 = ref nodes[pair]; ref readonly var c2 = ref nodes[pair + 1];
        return new TreeNode
        {
            Box = Union(c1.Box, c2.Box),
            FlagIndex = (uint)pair | ((c1.FlagIndex | c2.FlagIndex) & MovedNode),
            HeightOrShape = 1 + Math.Max(GetHeight(c1), GetHeight(c2)),
        };
    }

    private static TreeNode MakeLeaf(in Aabb box, int proxyId, int userData, bool moved) =>
        new() { Box = box, FlagIndex = (uint)proxyId | LeafNode | (moved ? MovedNode : 0), HeightOrShape = userData };

    /// <summary>A node landed at a new index: point what hangs below it back at it.</summary>
    private void LinkChildren(int index)
    {
        ref readonly var node = ref Nodes[index];
        if (IsLeaf(node)) _proxies[Child(node)].Node = index;
        else { int pair = Child(node); _parents[pair] = index; _parents[pair + 1] = index; }
    }

    private bool IsOrdered(int index) => IsLeaf(Nodes[index]) || index < Child(Nodes[index]);

    // ---------------------------------------------------------------- insert and remove

    /// <summary>The cheapest sibling for a new leaf by the surface area heuristic, descending greedily with a lower bound to stop early.</summary>
    private int FindBestSibling(in Aabb boxD)
    {
        var centerD = 0.5f * (boxD.Min + boxD.Max);
        float areaD = Perimeter(boxD);
        var nodes = Nodes;
        int index = Root;
        if (IsLeaf(nodes[index])) return index;
        var rootBox = nodes[index].Box;
        float areaBase = Perimeter(rootBox);
        float directCost = Perimeter(Union(rootBox, boxD));
        float inheritedCost = 0;
        int bestSibling = index;
        float bestCost = directCost;
        for (; ; )
        {
            int child1 = Child(nodes[index]), child2 = child1 + 1;
            float cost = directCost + inheritedCost;
            if (cost < bestCost) { bestSibling = index; bestCost = cost; }
            inheritedCost += directCost - areaBase;
            bool leaf1 = IsLeaf(nodes[child1]), leaf2 = IsLeaf(nodes[child2]);
            float lowerCost1 = float.MaxValue;
            var box1 = nodes[child1].Box;
            float directCost1 = Perimeter(Union(box1, boxD)), area1 = 0;
            if (leaf1)
            {
                float cost1 = directCost1 + inheritedCost;
                if (cost1 < bestCost) { bestSibling = child1; bestCost = cost1; }
            }
            else
            {
                area1 = Perimeter(box1);
                lowerCost1 = inheritedCost + directCost1 + F.Min(areaD - area1, 0);
            }
            float lowerCost2 = float.MaxValue;
            var box2 = nodes[child2].Box;
            float directCost2 = Perimeter(Union(box2, boxD)), area2 = 0;
            if (leaf2)
            {
                float cost2 = directCost2 + inheritedCost;
                if (cost2 < bestCost) { bestSibling = child2; bestCost = cost2; }
            }
            else
            {
                area2 = Perimeter(box2);
                lowerCost2 = inheritedCost + directCost2 + F.Min(areaD - area2, 0);
            }
            if (leaf1 && leaf2) break;
            if (bestCost <= lowerCost1 && bestCost <= lowerCost2) break;
            if (lowerCost1 == lowerCost2 && !leaf1)
            {
                // No clear choice (both children contain D): the nearer centre.
                lowerCost1 = SimMath.LengthSquared(0.5f * (box1.Min + box1.Max) - centerD);
                lowerCost2 = SimMath.LengthSquared(0.5f * (box2.Min + box2.Max) - centerD);
            }
            if (lowerCost1 < lowerCost2 && !leaf1) { index = child1; areaBase = area1; directCost = directCost1; }
            else { index = child2; areaBase = area2; directCost = directCost2; }
        }
        return bestSibling;
    }

    private void SwapNodes(int down, int up)
    {
        (Nodes[down], Nodes[up]) = (Nodes[up], Nodes[down]);
        LinkChildren(down);
        LinkChildren(up);
        if (!IsOrdered(down) || !IsOrdered(up)) _dfsOrdered = false;
        int c = down ^ 1; // the sibling of the node that went down holds a different subtree now
        Nodes[c] = MakeInternal(Nodes, Child(Nodes[c]));
    }

    /// <summary>Swap a child of A with a grandchild under the other child when that shrinks the other child's box.</summary>
    private void RotateNodes(int a)
    {
        var nodes = Nodes;
        int b = Child(nodes[a]), c = b + 1;
        bool leafB = IsLeaf(nodes[b]), leafC = IsLeaf(nodes[c]);
        if (leafB && leafC) return;
        int bestDown = Null, bestUp = Null;
        float bestDelta = 0;
        if (!leafC)
        {
            int f = Child(nodes[c]), g = f + 1;
            float areaC = Perimeter(nodes[c].Box);
            float deltaBF = Perimeter(Union(nodes[b].Box, nodes[g].Box)) - areaC;
            if (deltaBF < bestDelta) { bestDown = b; bestUp = f; bestDelta = deltaBF; }
            float deltaBG = Perimeter(Union(nodes[b].Box, nodes[f].Box)) - areaC;
            if (deltaBG < bestDelta) { bestDown = b; bestUp = g; bestDelta = deltaBG; }
        }
        if (!leafB)
        {
            int d = Child(nodes[b]), e = d + 1;
            float areaB = Perimeter(nodes[b].Box);
            float deltaCD = Perimeter(Union(nodes[c].Box, nodes[e].Box)) - areaB;
            if (deltaCD < bestDelta) { bestDown = c; bestUp = d; bestDelta = deltaCD; }
            float deltaCE = Perimeter(Union(nodes[c].Box, nodes[d].Box)) - areaB;
            if (deltaCE < bestDelta) { bestDown = c; bestUp = e; }
        }
        if (bestDown != Null) SwapNodes(bestDown, bestUp);
    }

    private void InsertLeaf(in Aabb box, int proxyId, bool moved, bool rotate)
    {
        var leaf = MakeLeaf(box, proxyId, _proxies[proxyId].UserData, moved);
        if (IsEmpty(Nodes[Root])) { Nodes[Root] = leaf; _proxies[proxyId].Node = Root; return; }
        int sibling = FindBestSibling(box);
        // The sibling's slot becomes the new parent; the sibling moves down into a new pair beside the leaf.
        int pair = AllocatePair();
        var nodes = Nodes;
        nodes[pair] = nodes[sibling];
        nodes[pair + 1] = leaf;
        _parents[pair] = sibling; _parents[pair + 1] = sibling;
        LinkChildren(pair);
        _proxies[proxyId].Node = pair + 1;
        nodes[sibling] = MakeInternal(nodes, pair);
        if (!IsOrdered(sibling) || !IsOrdered(pair)) _dfsOrdered = false;
        for (int index = sibling; index != Null; index = _parents[index])
        {
            if (rotate) RotateNodes(index);
            Nodes[index] = MakeInternal(Nodes, Child(Nodes[index]));
        }
    }

    private void RemoveLeaf(int proxyId)
    {
        int leaf = _proxies[proxyId].Node;
        if (leaf == Root) { Nodes[Root] = Empty; return; }
        // The sibling takes the parent's slot and the pair is freed (the sibling only moves down: order holds).
        int parent = _parents[leaf];
        Nodes[parent] = Nodes[leaf ^ 1];
        LinkChildren(parent);
        FreePair(leaf & ~1);
        for (int index = _parents[parent]; index != Null; index = _parents[index])
            Nodes[index] = MakeInternal(Nodes, Child(Nodes[index]));
    }

    /// <summary>A new proxy; <paramref name="markMoved"/> has the pair finder look at it (new shapes of moving bodies).</summary>
    public int CreateProxy(in Aabb box, ulong categoryBits, int userData, bool markMoved = false)
    {
        int id = AllocateProxy();
        _proxies[id].CategoryBits = categoryBits;
        _proxies[id].UserData = userData;
        InsertLeaf(box, id, markMoved, rotate: true);
        return id;
    }

    public void DestroyProxy(int proxyId)
    {
        RemoveLeaf(proxyId);
        FreeProxy(proxyId);
    }

    /// <summary>Move a proxy to a new box now (remove and insert, no rotations), optionally marking it moved.</summary>
    public void MoveProxy(int proxyId, in Aabb box, bool markMoved = false)
    {
        RemoveLeaf(proxyId);
        InsertLeaf(box, proxyId, markMoved, rotate: false);
    }

    /// <summary>Grow a proxy's box in place (it must contain the old one), enlarging and marking its ancestors.</summary>
    internal void EnlargeProxy(int proxyId, in Aabb box)
    {
        int index = _proxies[proxyId].Node;
        Nodes[index].Box = box;
        Nodes[index].FlagIndex |= MovedNode;
        index = _parents[index];
        while (index != Null)
        {
            ref var node = ref Nodes[index];
            var old = node.Box;
            node.Box = Union(old, box);
            node.FlagIndex |= MovedNode;
            index = _parents[index];
            if (node.Box.Min == old.Min && node.Box.Max == old.Max) break;
        }
        while (index != Null && !IsMoved(Nodes[index])) { Nodes[index].FlagIndex |= MovedNode; index = _parents[index]; }
    }

    /// <summary>Give a proxy a new box and mark it and its ancestors moved; <see cref="Refit"/> then fixes the ancestors' boxes.</summary>
    internal void MarkProxyMoved(int proxyId, in Aabb box)
    {
        int index = _proxies[proxyId].Node;
        Nodes[index].Box = box;
        Nodes[index].FlagIndex |= MovedNode;
        for (index = _parents[index]; index != Null && !IsMoved(Nodes[index]); index = _parents[index])
            Nodes[index].FlagIndex |= MovedNode;
    }

    /// <summary>Mark a proxy (and its ancestors) moved without changing its box, so the pair finder looks at it.</summary>
    internal void MarkProxyMoved(int proxyId)
    {
        for (int index = _proxies[proxyId].Node; index != Null; index = _parents[index]) Nodes[index].FlagIndex |= MovedNode;
    }

    /// <summary>Clear every moved flag (the static tree, after the pair finder has seen it).</summary>
    internal void ClearMoved()
    {
        if (!IsMoved(Nodes[Root])) return;
        Nodes[Root].FlagIndex &= ~MovedNode;
        if (IsLeaf(Nodes[Root])) return;
        Span<int> stack = stackalloc int[StackSize];
        int top = 0;
        stack[top++] = Child(Nodes[Root]);
        while (top > 0)
        {
            int pair = stack[--top];
            for (int i = 0; i < 2; i++)
            {
                ref var node = ref Nodes[pair + i];
                if (!IsMoved(node)) continue;
                node.FlagIndex &= ~MovedNode;
                if (!IsLeaf(node) && top < StackSize) stack[top++] = Child(node);
            }
        }
    }

    /// <summary>Recompute moved internal nodes' boxes in one sweep from the last pair to the first (children always sit after parents).</summary>
    internal void Refit()
    {
        if (!IsMoved(Nodes[Root])) return;
        var nodes = Nodes;
        if (!_dfsOrdered) { RefitSubtree(Root); return; }
        for (int pair = _nodeEnd - 2; pair >= 0; pair -= 2)
        {
            uint f1 = nodes[pair].FlagIndex, f2 = nodes[pair + 1].FlagIndex;
            if (((f1 | f2) & MovedNode) == 0) continue;
            if ((f1 & (LeafNode | MovedNode)) == MovedNode) { int c = (int)(f1 & IndexMask); nodes[pair].Box = Union(nodes[c].Box, nodes[c + 1].Box); }
            if ((f2 & (LeafNode | MovedNode)) == MovedNode) { int c = (int)(f2 & IndexMask); nodes[pair + 1].Box = Union(nodes[c].Box, nodes[c + 1].Box); }
        }
    }

    private void RefitSubtree(int root)
    {
        if (IsLeaf(Nodes[root]) || !IsMoved(Nodes[root])) return;
        Span<int> stack = stackalloc int[StackSize];
        int top = 0;
        stack[top++] = ~root;
        stack[top++] = Child(Nodes[root]);
        stack[top++] = Child(Nodes[root]) + 1;
        while (top > 0)
        {
            int item = stack[--top];
            if (item < 0)
            {
                ref var parent = ref Nodes[~item];
                int pair = Child(parent);
                parent.Box = Union(Nodes[pair].Box, Nodes[pair + 1].Box);
                continue;
            }
            ref readonly var node = ref Nodes[item];
            if (IsLeaf(node) || !IsMoved(node)) continue;
            if (top + 3 <= StackSize)
            {
                int pair = Child(node);
                stack[top++] = ~item; stack[top++] = pair; stack[top++] = pair + 1;
            }
        }
    }

    // ---------------------------------------------------------------- rebuild

    /// <summary>
    /// Rebuild the moved parts of the tree (everything, with <paramref name="fullBuild"/>), keeping unmoved subtrees
    /// whole: median splits on the longer axis, written depth first into a fresh node array. Clears the moved flags.
    /// Returns how many build leaves (proxies or kept subtrees) were sorted.
    /// </summary>
    public int Rebuild(bool fullBuild)
    {
        if (_proxyCount == 0) return 0;
        var nodes = Nodes;
        if (!fullBuild && !IsMoved(nodes[Root]) && _dfsOrdered) return 0;
        if (_swapNodes.Length != nodes.Length) _swapNodes = new TreeNode[nodes.Length];
        if (_leafIndices.Length < _proxyCount)
        {
            int capacity = _proxyCount + _proxyCount / 2;
            _leafIndices = new int[capacity]; _leafNodes = new TreeNode[capacity]; _leafCenters = new Vector2[capacity];
        }
        var leafIndices = _leafIndices; var leaves = _leafNodes; var centers = _leafCenters;
        // Gather the build leaves: a marked internal node is opened (and abandoned); anything else is kept whole.
        int leafCount = 0;
        Span<int> stack = stackalloc int[StackSize];
        int top = 0;
        ref readonly var root = ref nodes[Root];
        if (!IsLeaf(root) && (fullBuild || IsMoved(root))) stack[top++] = Child(root);
        else
        {
            var node = root;
            node.FlagIndex &= ~MovedNode;
            leafIndices[0] = 0; leaves[0] = node; centers[0] = 0.5f * (node.Box.Min + node.Box.Max);
            leafCount = 1;
        }
        while (top > 0)
        {
            int pair = stack[--top];
            for (int i = 0; i < 2; i++)
            {
                var node = nodes[pair + i];
                if (!IsLeaf(node) && (fullBuild || IsMoved(node))) { stack[top++] = Child(node); continue; }
                node.FlagIndex &= ~MovedNode;
                leafIndices[leafCount] = leafCount;
                leaves[leafCount] = node;
                centers[leafCount] = 0.5f * (node.Box.Min + node.Box.Max);
                leafCount++;
            }
        }
        BuildTree(leafCount);
        (Nodes, _swapNodes) = (_swapNodes, Nodes);
        _pairFreeList = Null;
        _dfsOrdered = true;
        return leafCount;
    }

    private int BumpPair(int parent)
    {
        int pair = _nodeEnd;
        _nodeEnd += 2;
        _parents[pair] = parent; _parents[pair + 1] = parent;
        return pair;
    }

    private static void SetChild(ref TreeNode node, int pair) => node.FlagIndex = (node.FlagIndex & ~IndexMask) | (uint)pair;

    private struct BuildItem { public int NodeIndex, Pair, ChildCount, Start, Split, End; }

    private void BuildTree(int leafCount)
    {
        var nodes = _swapNodes;
        var leaves = _leafNodes; var leafIndices = _leafIndices; var centers = _leafCenters;
        _nodeEnd = 2;
        nodes[Root + 1] = Empty;
        _parents[Root] = Null; _parents[Root + 1] = Null;
        if (leafCount == 1) { PlaceLeaf(leaves[leafIndices[0]], Root); return; }
        Span<BuildItem> stack = stackalloc BuildItem[StackSize];
        int top = 0;
        stack[0] = new BuildItem { NodeIndex = Root, Pair = BumpPair(Root), ChildCount = -1, Start = 0, End = leafCount, Split = PartitionMid(leafIndices.AsSpan(0, leafCount), centers.AsSpan(0, leafCount)) };
        for (; ; )
        {
            ref var item = ref stack[top];
            item.ChildCount++;
            if (item.ChildCount == 2)
            {
                nodes[item.NodeIndex] = MakeInternal(nodes, item.Pair);
                if (top == 0) break;
                top--;
                continue;
            }
            int slot = item.ChildCount;
            int start = slot == 0 ? item.Start : item.Split, end = slot == 0 ? item.Split : item.End;
            int count = end - start;
            int nodeIndex = item.Pair + slot;
            if (count == 1) { PlaceLeaf(leaves[leafIndices[start]], nodeIndex); continue; }
            top++;
            stack[top] = new BuildItem
            {
                NodeIndex = nodeIndex, Pair = BumpPair(nodeIndex), ChildCount = -1, Start = start, End = end,
                Split = start + PartitionMid(leafIndices.AsSpan(start, count), centers.AsSpan(start, count)),
            };
        }
    }

    private void PlaceLeaf(in TreeNode leaf, int newIndex)
    {
        if (IsLeaf(leaf)) { _swapNodes[newIndex] = leaf; _proxies[Child(leaf)].Node = newIndex; }
        else CopySubtree(leaf, newIndex);
    }

    private struct CopyItem { public int OldPair, NewIndex; }

    /// <summary>Copy a kept subtree from the old array into the new one, depth first.</summary>
    private void CopySubtree(TreeNode node, int newIndex)
    {
        var oldNodes = Nodes; var newNodes = _swapNodes;
        Span<CopyItem> stack = stackalloc CopyItem[StackSize];
        int top = 0;
        int oldPair = Child(node);
        int newPair = BumpPair(newIndex);
        SetChild(ref node, newPair);
        newNodes[newIndex] = node;
        for (; ; )
        {
            newNodes[newPair] = oldNodes[oldPair];
            newNodes[newPair + 1] = oldNodes[oldPair + 1];
            if (IsLeaf(newNodes[newPair + 1])) _proxies[Child(newNodes[newPair + 1])].Node = newPair + 1;
            else stack[top++] = new CopyItem { OldPair = Child(newNodes[newPair + 1]), NewIndex = newPair + 1 };
            if (!IsLeaf(newNodes[newPair]))
            {
                int left = newPair;
                oldPair = Child(newNodes[newPair]);
                newPair = BumpPair(left);
                SetChild(ref newNodes[left], newPair);
                continue;
            }
            _proxies[Child(newNodes[newPair])].Node = newPair;
            if (top == 0) break;
            var next = stack[--top];
            oldPair = next.OldPair;
            newPair = BumpPair(next.NewIndex);
            SetChild(ref newNodes[next.NewIndex], newPair);
        }
    }

    /// <summary>Split at the middle of the centres' longer axis (Hoare partition); returns the left count.</summary>
    private static int PartitionMid(Span<int> indices, Span<Vector2> centers)
    {
        int count = indices.Length;
        if (count <= 2) return count / 2;
        var lo = centers[0]; var hi = centers[0];
        for (int i = 1; i < count; i++) { lo = SimMath.Min(lo, centers[i]); hi = SimMath.Max(hi, centers[i]); }
        var d = hi - lo;
        var c = 0.5f * (lo + hi);
        bool xAxis = d.X > d.Y;
        float pivot = xAxis ? c.X : c.Y;
        int i1 = 0, i2 = count;
        while (i1 < i2)
        {
            while (i1 < i2 && (xAxis ? centers[i1].X : centers[i1].Y) < pivot) i1++;
            while (i1 < i2 && (xAxis ? centers[i2 - 1].X : centers[i2 - 1].Y) >= pivot) i2--;
            if (i1 < i2)
            {
                (indices[i1], indices[i2 - 1]) = (indices[i2 - 1], indices[i1]);
                (centers[i1], centers[i2 - 1]) = (centers[i2 - 1], centers[i1]);
                i1++; i2--;
            }
        }
        return i1 > 0 && i1 < count ? i1 : count / 2;
    }

    // ---------------------------------------------------------------- queries

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool Overlaps(in Aabb a, in Aabb b) => a.Min.X <= b.Max.X && b.Min.X <= a.Max.X && a.Min.Y <= b.Max.Y && b.Min.Y <= a.Max.Y;

    /// <summary>Every leaf whose box overlaps <paramref name="box"/> and whose category is in <paramref name="maskBits"/>, in a fixed order.</summary>
    public TreeStats Query<T>(in Aabb box, ulong maskBits, ref T callback) where T : struct, ITreeQuery
    {
        var stats = new TreeStats();
        if (_proxyCount == 0) return stats;
        var nodes = Nodes;
        Span<int> stack = stackalloc int[StackSize];
        int top = 0;
        stack[top++] = RootPair();
        while (top > 0)
        {
            int pair = stack[--top];
            stats.NodeVisits++;
            for (int i = 0; i < 2; i++)
            {
                ref readonly var node = ref nodes[pair + i];
                if (!Overlaps(node.Box, box)) continue;
                if (IsLeaf(node))
                {
                    int proxyId = Child(node);
                    if ((_proxies[proxyId].CategoryBits & maskBits) == 0) continue;
                    stats.LeafVisits++;
                    if (!callback.Hit(proxyId, node.HeightOrShape)) return stats;
                }
                else if (top < StackSize - 1) stack[top++] = Child(node);
            }
        }
        return stats;
    }

    /// <summary>Overlap query against every leaf, whatever its category.</summary>
    public TreeStats Query<T>(in Aabb box, ref T callback) where T : struct, ITreeQuery => Query(box, ulong.MaxValue, ref callback);

    /// <summary>
    /// Leaves the segment from <paramref name="origin"/> along <paramref name="translation"/> (up to
    /// <paramref name="maxFraction"/>) may cross, nearest subtrees first; the callback's result clips the segment.
    /// </summary>
    public TreeStats CastRay<T>(Vector2 origin, Vector2 translation, float maxFraction, ulong maskBits, ref T callback) where T : struct, ITreeCast
    {
        var stats = new TreeStats();
        if (_proxyCount == 0) return stats;
        var r = SimMath.Normalize(translation);
        var v = new Vector2(-r.Y, r.X); // perpendicular to the segment
        var absV = new Vector2(MathF.Abs(v.X), MathF.Abs(v.Y));
        var p2 = origin + maxFraction * translation;
        var segment = new Aabb { Min = SimMath.Min(origin, p2), Max = SimMath.Max(origin, p2) };
        var nodes = Nodes;
        Span<int> stack = stackalloc int[StackSize];
        int top = 0;
        stack[top++] = RootPair();
        while (top > 0)
        {
            int pair = stack[--top];
            stats.NodeVisits++;
            int hit0 = -1, hit1 = -1;
            for (int i = 0; i < 2; i++)
            {
                ref readonly var node = ref nodes[pair + i];
                if (!Overlaps(node.Box, segment)) continue;
                // Separating axis for the segment (van den Bergen): |v·(p1 − c)| > |v|·h.
                var c = 0.5f * (node.Box.Min + node.Box.Max); var h = 0.5f * (node.Box.Max - node.Box.Min);
                if (V.Dot(absV, h) < MathF.Abs(V.Dot(v, origin - c))) continue;
                if (hit0 < 0) hit0 = pair + i; else hit1 = pair + i;
            }
            if (hit1 >= 0 && !IsLeaf(nodes[hit0]) && !IsLeaf(nodes[hit1]))
            {
                // Push the nearer one last, so it's walked first.
                var c0 = 0.5f * (nodes[hit0].Box.Min + nodes[hit0].Box.Max); var c1 = 0.5f * (nodes[hit1].Box.Min + nodes[hit1].Box.Max);
                if (SimMath.LengthSquared(c0 - origin) < SimMath.LengthSquared(c1 - origin)) (hit0, hit1) = (hit1, hit0);
            }
            for (int k = 0; k < 2; k++)
            {
                int index = k == 0 ? hit0 : hit1;
                if (index < 0) continue;
                ref readonly var node = ref nodes[index];
                if (!IsLeaf(node)) { if (top < StackSize - 1) stack[top++] = Child(node); continue; }
                int proxyId = Child(node);
                if ((_proxies[proxyId].CategoryBits & maskBits) == 0) continue;
                float value = callback.Hit(origin, translation, maxFraction, proxyId, node.HeightOrShape);
                stats.LeafVisits++;
                if (value == 0) return stats; // the caller ended the cast
                if (value > 0 && value <= maxFraction)
                {
                    maxFraction = value;
                    p2 = origin + maxFraction * translation;
                    segment = new Aabb { Min = SimMath.Min(origin, p2), Max = SimMath.Max(origin, p2) };
                }
            }
        }
        return stats;
    }

    /// <summary>Leaves a box swept along <paramref name="translation"/> may touch, nearest first; the callback's result clips the sweep. The callback sees the box's centre as the origin.</summary>
    public TreeStats CastBox<T>(in Aabb box, Vector2 translation, float maxFraction, ulong maskBits, ref T callback) where T : struct, ITreeCast
    {
        var stats = new TreeStats();
        if (_proxyCount == 0) return stats;
        var p1 = 0.5f * (box.Min + box.Max);
        var extension = 0.5f * (box.Max - box.Min);
        var v = new Vector2(-translation.Y, translation.X);
        var absV = new Vector2(MathF.Abs(v.X), MathF.Abs(v.Y));
        var t = maxFraction * translation;
        var total = new Aabb { Min = SimMath.Min(box.Min, box.Min + t), Max = SimMath.Max(box.Max, box.Max + t) };
        var nodes = Nodes;
        Span<int> stack = stackalloc int[StackSize];
        int top = 0;
        stack[top++] = RootPair();
        while (top > 0)
        {
            int pair = stack[--top];
            stats.NodeVisits++;
            int hit0 = -1, hit1 = -1;
            for (int i = 0; i < 2; i++)
            {
                ref readonly var node = ref nodes[pair + i];
                if (!Overlaps(node.Box, total)) continue;
                var c = 0.5f * (node.Box.Min + node.Box.Max); var h = 0.5f * (node.Box.Max - node.Box.Min) + extension;
                if (V.Dot(absV, h) < MathF.Abs(V.Dot(v, p1 - c))) continue;
                if (hit0 < 0) hit0 = pair + i; else hit1 = pair + i;
            }
            if (hit1 >= 0 && !IsLeaf(nodes[hit0]) && !IsLeaf(nodes[hit1]))
            {
                var c0 = 0.5f * (nodes[hit0].Box.Min + nodes[hit0].Box.Max); var c1 = 0.5f * (nodes[hit1].Box.Min + nodes[hit1].Box.Max);
                if (SimMath.LengthSquared(c0 - p1) < SimMath.LengthSquared(c1 - p1)) (hit0, hit1) = (hit1, hit0);
            }
            for (int k = 0; k < 2; k++)
            {
                int index = k == 0 ? hit0 : hit1;
                if (index < 0) continue;
                ref readonly var node = ref nodes[index];
                if (!IsLeaf(node)) { if (top < StackSize - 1) stack[top++] = Child(node); continue; }
                int proxyId = Child(node);
                if ((_proxies[proxyId].CategoryBits & maskBits) == 0) continue;
                float value = callback.Hit(p1, translation, maxFraction, proxyId, node.HeightOrShape);
                stats.LeafVisits++;
                if (value == 0) return stats;
                if (value > 0 && value < maxFraction)
                {
                    maxFraction = value;
                    t = maxFraction * translation;
                    total = new Aabb { Min = SimMath.Min(box.Min, box.Min + t), Max = SimMath.Max(box.Max, box.Max + t) };
                }
            }
        }
        return stats;
    }

    /// <summary>The sum of internal nodes' perimeters over the root's: what the surface area heuristic minimises (lower is better).</summary>
    public float AreaRatio()
    {
        if (_proxyCount == 0) return 0;
        float rootArea = Perimeter(Nodes[Root].Box);
        if (rootArea <= 0) return 0;
        float total = 0;
        for (int i = 1; i < _nodeEnd; i++)
            if (!IsLeaf(Nodes[i]) && !IsEmpty(Nodes[i])) total += Perimeter(Nodes[i].Box);
        return total / rootArea;
    }
}
