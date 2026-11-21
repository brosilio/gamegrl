// Ported from Box2D (https://github.com/erincatto/box2d), Copyright (c) 2022 Erin Catto, under the MIT License;
// see THIRD_PARTY_NOTICES.md.
using System.Numerics;

namespace Gamegrl.Physics2D;

// The broadphase (after Box2D v3's broad_phase.c by Erin Catto): one tree per body type. A shape whose box leaves its
// fat box marks its leaf moved, and the mark climbs to the root; a linear refit fixes the moved nodes' boxes. New pairs
// come from colliding the dynamic tree's moved subtrees with each other, and with the static and kinematic trees
// (moved against anything), instead of querying every moved proxy against the whole tree: linear rather than n·log n.
// Then the moved parts of the dynamic and kinematic trees are rebuilt, which keeps them tight however things move.
public sealed partial class PhysicsWorld2D
{
    private DynamicTree[] _trees = [new DynamicTree(), new DynamicTree(), new DynamicTree()]; // by BodyType
    internal byte[] ProxyTree = []; // per shape: the tree its proxy is in (a BodyType)
    private int[] _movedSiblings = new int[64];
    private int _movedSiblingCount;
    private NodePair[] _crossSeeds = new NodePair[2 * CrossSeedCount];
    private int _crossSeedCount, _staticSeedCount;
    private int _pairCursor;
    // New pairs per thread, merged and sorted after.
    private ulong[][] _threadPairs = [new ulong[64]];
    private int[] _threadPairCount = new int[1];
    private static readonly ProfileMarker RebuildMarker = ProfileMarker.Get("Physics2D.TreeRebuild", ProfileCategory.Engine);

    private const int CrossSeedCount = 64;

    private struct NodePair { public TreeNode A, B; }

    private DynamicTree TreeOf(int s) => _trees[ProxyTree[s]];

    private void CreateProxy(int s, bool invokeContactCreation = true)
    {
        int b = ShapeBody[s];
        var box = SpeculativeBox(s);
        float margin = FatMargin(s);
        box.Min -= new Vector2(margin); box.Max += new Vector2(margin);
        Fat[s] = box;
        FatPoses[s] = new FatPose { Center = Center[b], Rotation = Rotation[b], Slack = margin };
        ProxyTree[s] = (byte)Types[b];
        // A static shape finds its pairs only when asked to (a new static shape under a resting body should).
        bool mark = Types[b] != BodyType.Static || invokeContactCreation;
        Proxy[s] = _trees[ProxyTree[s]].CreateProxy(box, Filters[s].Category, s, mark);
    }

    private void DestroyProxy(int s) => TreeOf(s).DestroyProxy(Proxy[s]);

    /// <summary>Have the next pair update look at shape <paramref name="s"/> again (its filter changed, or a joint stopped blocking a pair).</summary>
    private void MarkMoved(int s) => TreeOf(s).MarkProxyMoved(Proxy[s]);

    /// <summary>Re-fit a shape's fat box if it left it (or always, when forced: teleports), moving its proxy now.</summary>
    private void RefreshProxy(int s, float dt, bool force)
    {
        var box = SpeculativeBox(s);
        ref var fat = ref Fat[s];
        if (!force && fat.Min.X <= box.Min.X && fat.Min.Y <= box.Min.Y && fat.Max.X >= box.Max.X && fat.Max.Y >= box.Max.Y) return;
        float margin = FatMargin(s);
        box.Min -= new Vector2(margin);
        box.Max += new Vector2(margin);
        fat = box;
        FatPoses[s] = new FatPose { Center = Center[ShapeBody[s]], Rotation = Rotation[ShapeBody[s]], Slack = margin };
        TreeOf(s).MoveProxy(Proxy[s], box, markMoved: true);
    }

    /// <summary>Fat boxes of moving shapes, then new pairs from the moved subtrees, created in sorted order.</summary>
    private void UpdatePairs(float dt)
    {
        // Boxes in parallel; the shapes that left their fat boxes mark their leaves serially, in body order.
        if (_needsMove.Length < _shapeCapacity) { _needsMove = new bool[_shapeCapacity]; _swept = new Aabb[_shapeCapacity]; }
        var sweep = new SweepJob { World = this, Dt = dt };
        int bodyWords = (_bodyHigh + 63) >> 6;
        if (_moveBits.Length < bodyWords) _moveBits = new ulong[bodyWords * 2];
        using (SweepMarker.Scope()) { if (Jobs is { } jobs) jobs.ParallelFor(_bodyHigh, 256, ref sweep); else sweep.Execute(0, _bodyHigh); }
        int moved = 0;
        using (TreeMarker.Scope())
        {
            for (int bw = 0; bw < bodyWords; bw++)
            {
                ulong bits = _moveBits[bw];
                _moveBits[bw] = 0;
                while (bits != 0)
                {
                    int b = (bw << 6) + BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    for (int s = FirstShape[b]; s >= 0; s = NextShape[s])
                    {
                        if (!_needsMove[s]) continue;
                        var box = _swept[s];
                        float margin = AabbMargin[s];
                        box.Min -= new Vector2(margin); box.Max += new Vector2(margin);
                        Fat[s] = box;
                        FatPoses[s] = new FatPose { Center = Center[b], Rotation = Rotation[b], Slack = margin };
                        TreeOf(s).MarkProxyMoved(Proxy[s], box);
                        moved++;
                    }
                }
            }
            _trees[(int)BodyType.Kinematic].Refit();
            _trees[(int)BodyType.Dynamic].Refit();
        }
        MovedProxies = moved;
        var staticTree = _trees[(int)BodyType.Static]; var kinematicTree = _trees[(int)BodyType.Kinematic]; var dynamicTree = _trees[(int)BodyType.Dynamic];
        _newPairCount = 0;
        PairCandidates = 0;
        if (!staticTree.HasMoved && !kinematicTree.NeedsRebuild && !dynamicTree.NeedsRebuild) return;

        using (QueryMarker.Scope())
        {
            GatherMovedSiblings(dynamicTree);
            _staticSeedCount = GatherCrossSeeds(dynamicTree, staticTree, 0);
            _crossSeedCount = _staticSeedCount + GatherCrossSeeds(dynamicTree, kinematicTree, _staticSeedCount);
            int work = _crossSeedCount + _movedSiblingCount;
            int threads = Jobs is { } jobs2 ? Math.Clamp(work / 32, 1, jobs2.WorkerCount + 1) : 1;
            if (_threadPairs.Length < threads)
            {
                var grown = new ulong[threads][];
                for (int t = 0; t < threads; t++) grown[t] = t < _threadPairs.Length ? _threadPairs[t] : new ulong[64];
                _threadPairs = grown;
                _threadPairCount = new int[threads];
            }
            _pairCursor = 0;
            var job = new PairJob { World = this };
            if (threads > 1) Jobs!.RunOnThreads(threads, ref job); else job.Execute(0, 1);
            for (int t = 0; t < threads; t++)
            {
                int n = _threadPairCount[t];
                if (_newPairCount + n > _newPairs.Length) Array.Resize(ref _newPairs, Math.Max(_newPairs.Length * 2, _newPairCount + n));
                Array.Copy(_threadPairs[t], 0, _newPairs, _newPairCount, n);
                _newPairCount += n;
            }
        }
        using (RebuildMarker.Scope())
        {
            staticTree.ClearMoved();
            dynamicTree.Rebuild(false);
            kinematicTree.Rebuild(false);
        }
        PairCandidates = _newPairCount;
        using var addScope = AddPairsMarker.Scope();
        // Found in an order that depends on threads; created in key order, so it doesn't.
        var pairs = _newPairs.AsSpan(0, _newPairCount);
        Sort.Heap(pairs);
        foreach (ulong key in pairs) AddContact((int)(key >> 32), (int)(uint)key);
    }

    /// <summary>Sibling pairs of the dynamic tree with a moved member: each is collided across (its two subtrees against each other).</summary>
    private void GatherMovedSiblings(DynamicTree tree)
    {
        var nodes = tree.Nodes;
        int end = tree.NodeEnd;
        if (_movedSiblings.Length < end / 2 + 1) _movedSiblings = new int[end];
        int count = 0;
        for (int pair = 2; pair < end; pair += 2)
            if (((nodes[pair].FlagIndex | nodes[pair + 1].FlagIndex) & DynamicTree.MovedNode) != 0) _movedSiblings[count++] = pair;
        _movedSiblingCount = count;
    }

    private static bool TestPair(in TreeNode a, in TreeNode b) =>
        ((a.FlagIndex | b.FlagIndex) & DynamicTree.MovedNode) != 0 && DynamicTree.Overlaps(a.Box, b.Box);

    /// <summary>A breadth-first walk of two trees together until enough node pairs are queued to share among threads.</summary>
    private int GatherCrossSeeds(DynamicTree treeA, DynamicTree treeB, int offset)
    {
        var nodesA = treeA.Nodes; var nodesB = treeB.Nodes;
        Span<NodePair> queue = stackalloc NodePair[2 * CrossSeedCount];
        int mask = 2 * CrossSeedCount - 1, head = 0, tail = 0, count = 0;
        if (TestPair(nodesA[DynamicTree.Root], nodesB[DynamicTree.Root])) queue[tail++ & mask] = new NodePair { A = nodesA[DynamicTree.Root], B = nodesB[DynamicTree.Root] };
        while (head < tail && count + (tail - head) + 3 < CrossSeedCount)
        {
            var pair = queue[head++ & mask];
            if (DynamicTree.IsLeaf(pair.A) || DynamicTree.IsLeaf(pair.B)) { _crossSeeds[offset + count++] = pair; continue; }
            int a = DynamicTree.Child(pair.A), b = DynamicTree.Child(pair.B);
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                    if (TestPair(nodesA[a + i], nodesB[b + j])) queue[tail++ & mask] = new NodePair { A = nodesA[a + i], B = nodesB[b + j] };
        }
        while (head < tail) _crossSeeds[offset + count++] = queue[head++ & mask];
        return count;
    }

    private struct PairJob : IJobThreads
    {
        public PhysicsWorld2D World;
        public readonly void Execute(int thread, int threadCount) => World.FindPairs(thread);
    }

    /// <summary>Claim blocks of work (cross seeds, then moved siblings) until none are left, collecting new pairs in this thread's list.</summary>
    private void FindPairs(int thread)
    {
        _threadPairCount[thread] = 0;
        var dynamicNodes = _trees[(int)BodyType.Dynamic].Nodes;
        int total = _crossSeedCount + _movedSiblingCount;
        const int block = 8;
        for (; ; )
        {
            int start = Interlocked.Add(ref _pairCursor, block) - block;
            if (start >= total) break;
            int end = Math.Min(start + block, total);
            for (int k = start; k < end; k++)
            {
                if (k < _crossSeedCount)
                {
                    var other = (k < _staticSeedCount ? _trees[(int)BodyType.Static] : _trees[(int)BodyType.Kinematic]).Nodes;
                    CollideCrossPairs(dynamicNodes, other, _crossSeeds[k].A, _crossSeeds[k].B, thread);
                }
                else
                {
                    int pair = _movedSiblings[k - _crossSeedCount];
                    CollideCrossPairs(dynamicNodes, dynamicNodes, dynamicNodes[pair], dynamicNodes[pair + 1], thread);
                }
            }
        }
    }

    private struct IndexPair { public int A, B; }

    /// <summary>
    /// Collide two subtrees against each other (they may share a tree): only pairs across them, so self collision of the
    /// moved siblings never finds a pair twice. A pair is wanted when either side moved.
    /// </summary>
    private void CollideCrossPairs(TreeNode[] arrayA, TreeNode[] arrayB, in TreeNode subtreeA, in TreeNode subtreeB, int thread)
    {
        Span<IndexPair> stack = stackalloc IndexPair[DynamicTree.StackSize];
        int top = 0;
        if (TestPair(subtreeA, subtreeB)) ExpandPair(arrayA, arrayB, subtreeA, subtreeB, stack, ref top, thread);
        while (top > 0)
        {
            var p = stack[--top];
            for (int i = 0; i < 2; i++)
            {
                ref readonly var a = ref arrayA[p.A + i];
                for (int j = 0; j < 2; j++)
                {
                    ref readonly var b = ref arrayB[p.B + j];
                    if (TestPair(a, b)) ExpandPair(arrayA, arrayB, a, b, stack, ref top, thread);
                }
            }
        }
    }

    private void ExpandPair(TreeNode[] arrayA, TreeNode[] arrayB, in TreeNode a, in TreeNode b, Span<IndexPair> stack, ref int top, int thread)
    {
        bool leafA = DynamicTree.IsLeaf(a), leafB = DynamicTree.IsLeaf(b);
        if (leafA && leafB) ConsiderPair(a.HeightOrShape, b.HeightOrShape, thread);
        else if (leafA) CollideProxyAndSubtree(a, arrayB, DynamicTree.Child(b), thread);
        else if (leafB) CollideProxyAndSubtree(b, arrayA, DynamicTree.Child(a), thread);
        else if (top < stack.Length) stack[top++] = new IndexPair { A = DynamicTree.Child(a), B = DynamicTree.Child(b) };
    }

    private void CollideProxyAndSubtree(in TreeNode proxy, TreeNode[] nodes, int pair, int thread)
    {
        bool proxyMoved = DynamicTree.IsMoved(proxy);
        var box = proxy.Box;
        int shape = proxy.HeightOrShape;
        Span<int> stack = stackalloc int[DynamicTree.StackSize];
        int top = 0;
        stack[top++] = pair;
        while (top > 0)
        {
            pair = stack[--top];
            for (int i = 0; i < 2; i++)
            {
                ref readonly var node = ref nodes[pair + i];
                if (!proxyMoved && !DynamicTree.IsMoved(node)) continue;
                if (!DynamicTree.Overlaps(box, node.Box)) continue;
                if (DynamicTree.IsLeaf(node)) ConsiderPair(shape, node.HeightOrShape, thread);
                else if (top < stack.Length) stack[top++] = DynamicTree.Child(node);
            }
        }
    }

    /// <summary>A candidate pair: new, on different bodies, allowed by filters and joints? Then it goes in this thread's list (read-only otherwise: safe in parallel).</summary>
    private void ConsiderPair(int s, int other, int thread)
    {
        int lo = Math.Min(s, other), hi = Math.Max(s, other);
        int a = ShapeBody[lo], b = ShapeBody[hi];
        if (a == b) return;
        ulong key = (ulong)(uint)lo << 32 | (uint)hi;
        if (_pairs.Contains(key)) return; // already a contact
        if (Types[a] != BodyType.Dynamic && Types[b] != BodyType.Dynamic) return;
        if (!Filters[lo].Accepts(Filters[hi])) return;
        if (!CanCollide(Geometry[lo], Geometry[hi])) return;
        if (_jointDegree[a] > 0 && _jointDegree[b] > 0 && JointPreventsCollision(a, b)) return;
        ref var list = ref _threadPairs[thread];
        ref int count = ref _threadPairCount[thread];
        if (count == list.Length) Array.Resize(ref list, list.Length * 2);
        list[count++] = key;
    }

    /// <summary>Box2D's shape type matrix: segments and chain segments don't collide with each other.</summary>
    private static bool CanCollide(in Shape a, in Shape b) =>
        !((a.IsSegment || a.Kind == ShapeKind.ChainSegment) && (b.IsSegment || b.Kind == ShapeKind.ChainSegment));

    /// <summary>Rebuild the static tree from scratch (after building or streaming in a level: inserts one at a time make a worse tree).</summary>
    public void RebuildStaticTree() => _trees[(int)BodyType.Static].Rebuild(fullBuild: true);

    private void CloneBroadPhase(PhysicsWorld2D w)
    {
        w._trees = [_trees[0].Clone(), _trees[1].Clone(), _trees[2].Clone()];
        w.ProxyTree = (byte[])ProxyTree.Clone();
        w._movedSiblings = new int[_movedSiblings.Length];
        w._crossSeeds = new NodePair[_crossSeeds.Length];
    }
}
