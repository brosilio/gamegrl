// Ported from Box2D (https://github.com/erincatto/box2d), Copyright (c) 2022 Erin Catto, under the MIT License;
// see THIRD_PARTY_NOTICES.md.
namespace Gamegrl.Physics2D;

// Persistent islands and the awake sets (after Box2D v3's island.c and solver_set.c by Erin Catto). An island is a set
// of non-static bodies connected by touching contacts and joints; it wakes and sleeps as one. Islands merge when a
// contact starts touching or a joint is made, and never split on their own: removing a contact or joint only counts a
// "constraint removal", and an island with removals is split (one per step, the sleepiest) before it may sleep. So the
// per-step cost is the awake bodies' sleep timers, not a rebuild from every contact.
//
// What's awake is kept in lists: the awake bodies (integrated and solved), the awake islands (checked for sleep) and the
// awake contacts (collided): a contact is awake while either body is. Sleeping and waking an island move its bodies and
// contacts in and out of those lists and the constraint graph, so nothing scales with what sleeps.
public sealed partial class PhysicsWorld2D
{
    private struct Island
    {
        public int[] Bodies, Contacts, Joints;
        public int BodyCount, ContactCount, JointCount;
        /// <summary>Contacts and joints removed since the island was last whole: it may have come apart.</summary>
        public int ConstraintRemoveCount;
        /// <summary>Place in the awake island list; -1 asleep, -2 free.</summary>
        public int AwakeIndex;
    }

    /// <summary>A contact's links: its place in each body's contact list (keys are contact × 2 + side), in the awake list and in its island.</summary>
    internal struct ContactLink
    {
        public int PrevA, NextA, PrevB, NextB;
        public int AwakeIndex, Island, IslandIndex;
    }

    private Island[] _islands = new Island[16];
    private int _islandHigh;
    private int[] _freeIslands = new int[16];
    private int _freeIslandCount;
    private int[] _awakeIslands = new int[16];
    private int _awakeIslandCount;
    private int[] _islandStamp = new int[16];
    private int _sleepStamp;

    internal int[] IslandOf = [], IslandIndex = []; // per body: its island and place in it (-1: static, no island)
    private int[] _awakeIndex = [];                 // per body: place in _awake, or -1
    private int[] _contactHead = [], _contactDegree = []; // per body: first contact key, and how many
    internal ContactLink[] Links = new ContactLink[64];
    private int[] _awakeContacts = new int[64];
    private int _awakeContactCount;

    // Scratch for splitting.
    private int[] _splitParent = new int[16], _splitRoot = new int[16], _splitBodies = new int[16], _splitContacts = new int[16], _splitJoints = new int[16];

    /// <summary>Islands currently awake (for tests and diagnostics).</summary>
    public int AwakeIslandCount => _awakeIslandCount;
    public int IslandCount => _islandHigh - _freeIslandCount;

    // ---------------------------------------------------------------- awake lists

    private void AddAwakeBody(int b)
    {
        _awakeIndex[b] = _awakeCount;
        _awake[_awakeCount++] = b;
    }

    private void RemoveAwakeBody(int b)
    {
        int at = _awakeIndex[b], last = _awake[--_awakeCount];
        _awake[at] = last;
        _awakeIndex[last] = at;
        _awakeIndex[b] = -1;
    }

    private void AddAwakeContact(int c)
    {
        if (_awakeContactCount == _awakeContacts.Length) Array.Resize(ref _awakeContacts, _awakeContactCount * 2);
        Links[c].AwakeIndex = _awakeContactCount;
        _awakeContacts[_awakeContactCount++] = c;
    }

    private void RemoveAwakeContact(int c)
    {
        int at = Links[c].AwakeIndex, last = _awakeContacts[--_awakeContactCount];
        _awakeContacts[at] = last;
        Links[last].AwakeIndex = at;
        Links[c].AwakeIndex = -1;
    }

    private void AddAwakeIsland(int i)
    {
        if (_awakeIslandCount == _awakeIslands.Length) Array.Resize(ref _awakeIslands, _awakeIslandCount * 2);
        _islands[i].AwakeIndex = _awakeIslandCount;
        _awakeIslands[_awakeIslandCount++] = i;
    }

    private void RemoveAwakeIsland(int i)
    {
        int at = _islands[i].AwakeIndex, last = _awakeIslands[--_awakeIslandCount];
        _awakeIslands[at] = last;
        _islands[last].AwakeIndex = at;
        _islands[i].AwakeIndex = -1;
    }

    // ---------------------------------------------------------------- per-body contact lists

    private void AddContactEdges(int c, int a, int b)
    {
        ref var link = ref Links[c];
        int keyA = c << 1, keyB = c << 1 | 1;
        link.PrevA = -1; link.NextA = _contactHead[a];
        if (_contactHead[a] >= 0) SetPrev(_contactHead[a], keyA);
        _contactHead[a] = keyA; _contactDegree[a]++;
        link.PrevB = -1; link.NextB = _contactHead[b];
        if (_contactHead[b] >= 0) SetPrev(_contactHead[b], keyB);
        _contactHead[b] = keyB; _contactDegree[b]++;
    }

    private void RemoveContactEdges(int c, int a, int b)
    {
        ref var link = ref Links[c];
        Unchain(link.PrevA, link.NextA, a);
        Unchain(link.PrevB, link.NextB, b);
        _contactDegree[a]--; _contactDegree[b]--;
    }

    private void Unchain(int prev, int next, int body)
    {
        if (prev >= 0) SetNext(prev, next); else _contactHead[body] = next;
        if (next >= 0) SetPrev(next, prev);
    }

    private void SetPrev(int key, int prev)
    {
        if ((key & 1) == 0) Links[key >> 1].PrevA = prev; else Links[key >> 1].PrevB = prev;
    }

    private void SetNext(int key, int next)
    {
        if ((key & 1) == 0) Links[key >> 1].NextA = next; else Links[key >> 1].NextB = next;
    }

    /// <summary>The contact key after <paramref name="key"/> in its body's list.</summary>
    internal int NextContactKey(int key) => (key & 1) == 0 ? Links[key >> 1].NextA : Links[key >> 1].NextB;

    // ---------------------------------------------------------------- islands

    private int NewIsland(bool awake)
    {
        int i;
        if (_freeIslandCount > 0) i = _freeIslands[--_freeIslandCount];
        else
        {
            if (_islandHigh == _islands.Length) { Array.Resize(ref _islands, _islandHigh * 2); Array.Resize(ref _islandStamp, _islandHigh * 2); }
            i = _islandHigh++;
        }
        ref var island = ref _islands[i];
        island.Bodies ??= new int[4]; island.Contacts ??= new int[4]; island.Joints ??= new int[2];
        island.BodyCount = island.ContactCount = island.JointCount = island.ConstraintRemoveCount = 0;
        island.AwakeIndex = -1;
        _islandStamp[i] = 0;
        if (awake) AddAwakeIsland(i);
        return i;
    }

    private void FreeIsland(int i)
    {
        if (_islands[i].AwakeIndex >= 0) RemoveAwakeIsland(i);
        _islands[i].AwakeIndex = -2; // its arrays stay for reuse
        if (_freeIslandCount == _freeIslands.Length) Array.Resize(ref _freeIslands, _freeIslandCount * 2);
        _freeIslands[_freeIslandCount++] = i;
    }

    private void IslandAddBody(int i, int b)
    {
        ref var island = ref _islands[i];
        if (island.BodyCount == island.Bodies.Length) Array.Resize(ref island.Bodies, island.BodyCount * 2);
        IslandOf[b] = i;
        IslandIndex[b] = island.BodyCount;
        island.Bodies[island.BodyCount++] = b;
    }

    /// <summary>Take a body out of its island (destroyed, or turned static); an emptied island goes.</summary>
    private void RemoveBodyFromIsland(int b)
    {
        int i = IslandOf[b];
        if (i < 0) return;
        ref var island = ref _islands[i];
        int at = IslandIndex[b], last = island.Bodies[--island.BodyCount];
        island.Bodies[at] = last;
        IslandIndex[last] = at;
        IslandOf[b] = -1; IslandIndex[b] = -1;
        if (island.BodyCount == 0) FreeIsland(i);
        else island.ConstraintRemoveCount++; // what held the rest together may have gone with it
    }

    private void IslandAddContact(int i, int c)
    {
        ref var island = ref _islands[i];
        if (island.ContactCount == island.Contacts.Length) Array.Resize(ref island.Contacts, island.ContactCount * 2);
        Links[c].Island = i;
        Links[c].IslandIndex = island.ContactCount;
        island.Contacts[island.ContactCount++] = c;
    }

    private void IslandAddJoint(int i, int j)
    {
        ref var island = ref _islands[i];
        if (island.JointCount == island.Joints.Length) Array.Resize(ref island.Joints, island.JointCount * 2);
        _joints[j].Island = i;
        _joints[j].IslandIndex = island.JointCount;
        island.Joints[island.JointCount++] = j;
    }

    /// <summary>The bigger island takes in the smaller (both awake); returns the survivor. -1 stands for "no island" (static).</summary>
    private int MergeIslands(int i1, int i2)
    {
        if (i1 == i2 || i2 < 0) return i1;
        if (i1 < 0) return i2;
        int big = i1, small = i2;
        if (_islands[i1].BodyCount < _islands[i2].BodyCount) { big = i2; small = i1; }
        ref var s = ref _islands[small];
        for (int k = 0; k < s.BodyCount; k++) IslandAddBody(big, s.Bodies[k]);
        for (int k = 0; k < s.ContactCount; k++) IslandAddContact(big, s.Contacts[k]);
        for (int k = 0; k < s.JointCount; k++) IslandAddJoint(big, s.Joints[k]);
        _islands[big].ConstraintRemoveCount += s.ConstraintRemoveCount;
        FreeIsland(small);
        return big;
    }

    /// <summary>A contact started touching: wake a sleeping side, merge the islands and add the contact.</summary>
    private void LinkContact(int c)
    {
        ref readonly var contact = ref Contacts[c];
        int a = contact.BodyA, b = contact.BodyB;
        if (Awake[a] && !Awake[b] && Types[b] != BodyType.Static) WakeIsland(IslandOf[b]);
        if (Awake[b] && !Awake[a] && Types[a] != BodyType.Static) WakeIsland(IslandOf[a]);
        int island = MergeIslands(IslandOf[a], IslandOf[b]);
        if (island >= 0) IslandAddContact(island, c);
    }

    /// <summary>A contact stopped touching or went: out of its island, which may now be in pieces.</summary>
    private void UnlinkContact(int c)
    {
        int i = Links[c].Island;
        if (i < 0) return;
        ref var island = ref _islands[i];
        int at = Links[c].IslandIndex, last = island.Contacts[--island.ContactCount];
        island.Contacts[at] = last;
        Links[last].IslandIndex = at;
        Links[c].Island = -1; Links[c].IslandIndex = -1;
        island.ConstraintRemoveCount++;
    }

    private void LinkJoint(int j)
    {
        ref readonly var joint = ref _joints[j];
        int a = joint.A, b = joint.B;
        int ia = a >= 0 ? IslandOf[a] : -1, ib = IslandOf[b];
        if (ia < 0 && ib < 0) return;
        // Merged islands must share a state: joining wakes both (CreateJoint wakes the bodies too).
        WakeIsland(ia);
        WakeIsland(ib);
        IslandAddJoint(MergeIslands(ia, ib), j);
    }

    private void UnlinkJoint(int j)
    {
        int i = _joints[j].Island;
        if (i < 0) return;
        ref var island = ref _islands[i];
        int at = _joints[j].IslandIndex, last = island.Joints[--island.JointCount];
        island.Joints[at] = last;
        _joints[last].IslandIndex = at;
        _joints[j].Island = -1; _joints[j].IslandIndex = -1;
        island.ConstraintRemoveCount++;
    }

    // ---------------------------------------------------------------- waking and sleeping

    /// <summary>Wake an island: its bodies join the awake list, their contacts the awake contacts, its touching contacts the graph.</summary>
    private void WakeIsland(int i)
    {
        if (i < 0 || _islands[i].AwakeIndex >= 0) return;
        AddAwakeIsland(i);
        ref var island = ref _islands[i];
        for (int k = 0; k < island.BodyCount; k++)
        {
            int b = island.Bodies[k];
            Awake[b] = true;
            SleepTime[b] = 0;
            AddAwakeBody(b);
        }
        for (int k = 0; k < island.BodyCount; k++)
            for (int key = _contactHead[island.Bodies[k]]; key >= 0; key = NextContactKey(key))
                if (Links[key >> 1].AwakeIndex < 0) AddAwakeContact(key >> 1);
        if (_recolor) return;
        for (int k = 0; k < island.JointCount; k++)
        {
            int j = island.Joints[k];
            if (_joints[j].GraphColor == 0 && JointSolvable(j)) AddJointToGraph(j);
        }
        for (int k = 0; k < island.ContactCount; k++)
        {
            int c = island.Contacts[k];
            if (Contacts[c].GraphColor == 0 && Solvable(Contacts[c])) AddToGraph(c);
        }
    }

    /// <summary>Put an island to sleep, unless it may have come apart (it's split first). Returns whether it slept.</summary>
    private bool TrySleepIsland(int i)
    {
        ref var island = ref _islands[i];
        if (island.ConstraintRemoveCount > 0 && island.BodyCount > 1) return false;
        // Out of the graph while the bodies still count as awake (their colour bits clear with them).
        if (!_recolor)
        {
            for (int k = 0; k < island.ContactCount; k++)
                if (Contacts[island.Contacts[k]].GraphColor != 0) RemoveFromGraph(island.Contacts[k]);
            for (int k = 0; k < island.JointCount; k++)
                if (_joints[island.Joints[k]].GraphColor != 0) RemoveJointFromGraph(island.Joints[k]);
        }
        for (int k = 0; k < island.BodyCount; k++)
        {
            int b = island.Bodies[k];
            Awake[b] = false;
            RemoveAwakeBody(b);
            Velocity[b] = default;
            AngularVelocityOf[b] = 0;
            // Written back once more (velocity now zero), then skipped while it sleeps.
            if (!_justSlept[b]) { _justSlept[b] = true; _justSleptList[_justSleptCount++] = b; }
        }
        // A contact stays awake while either body is.
        for (int k = 0; k < island.BodyCount; k++)
        {
            int b = island.Bodies[k];
            for (int key = _contactHead[b]; key >= 0; key = NextContactKey(key))
            {
                int c = key >> 1;
                int other = (key & 1) == 0 ? Contacts[c].BodyB : Contacts[c].BodyA;
                if (Links[c].AwakeIndex >= 0 && !Awake[other]) RemoveAwakeContact(c);
            }
        }
        RemoveAwakeIsland(i);
        return true;
    }

    /// <summary>Split an island into its connected parts (union-find over its contacts and joints), as Box2D's b2SplitIsland.</summary>
    private void SplitIsland(int baseId)
    {
        ref var baseIsland = ref _islands[baseId];
        int n = baseIsland.BodyCount;
        if (_splitParent.Length < n) { _splitParent = new int[n * 2]; _splitRoot = new int[n * 2]; }
        var parent = _splitParent;
        for (int k = 0; k < n; k++) parent[k] = k;
        for (int k = 0; k < baseIsland.ContactCount; k++)
        {
            ref readonly var contact = ref Contacts[baseIsland.Contacts[k]];
            UnionInIsland(baseId, contact.BodyA, contact.BodyB);
        }
        for (int k = 0; k < baseIsland.JointCount; k++)
        {
            ref readonly var joint = ref _joints[baseIsland.Joints[k]];
            if (joint.A >= 0) UnionInIsland(baseId, joint.A, joint.B);
        }
        int components = 0;
        for (int k = 0; k < n; k++)
        {
            parent[k] = FindInIsland(k);
            if (parent[k] == k) components++;
        }
        if (components == 1) { baseIsland.ConstraintRemoveCount = 0; return; }

        // One new island per part, in order of each part's first body; the base island's lists are read as they were.
        var root = _splitRoot;
        for (int k = 0; k < n; k++) root[k] = -1;
        // The lists are copied out: the first new island reuses the base island's slot and arrays.
        int contactCount = baseIsland.ContactCount, jointCount = baseIsland.JointCount;
        if (_splitBodies.Length < n) _splitBodies = new int[n * 2];
        if (_splitContacts.Length < contactCount) _splitContacts = new int[contactCount * 2];
        if (_splitJoints.Length < jointCount) _splitJoints = new int[jointCount * 2];
        var bodies = _splitBodies; var contacts = _splitContacts; var joints = _splitJoints;
        Array.Copy(baseIsland.Bodies, bodies, n);
        Array.Copy(baseIsland.Contacts, contacts, contactCount);
        Array.Copy(baseIsland.Joints, joints, jointCount);
        bool awake = baseIsland.AwakeIndex >= 0;
        FreeIsland(baseId);
        for (int k = 0; k < n; k++)
        {
            int r = parent[k];
            if (root[r] < 0) root[r] = NewIsland(awake);
            IslandAddBody(root[r], bodies[k]);
        }
        for (int k = 0; k < contactCount; k++)
        {
            int c = contacts[k];
            int a = Contacts[c].BodyA;
            IslandAddContact(IslandOf[a] >= 0 ? IslandOf[a] : IslandOf[Contacts[c].BodyB], c);
        }
        for (int k = 0; k < jointCount; k++)
        {
            int j = joints[k];
            int a = _joints[j].A;
            IslandAddJoint(a >= 0 && IslandOf[a] >= 0 ? IslandOf[a] : IslandOf[_joints[j].B], j);
        }
    }

    private int FindInIsland(int k)
    {
        var parent = _splitParent;
        while (parent[k] != k) { parent[k] = parent[parent[k]]; k = parent[k]; }
        return k;
    }

    private void UnionInIsland(int island, int a, int b)
    {
        // Static bodies (and bodies of other islands, which can't be connected) don't join anything.
        if (IslandOf[a] != island || IslandOf[b] != island) return;
        int ra = FindInIsland(IslandIndex[a]), rb = FindInIsland(IslandIndex[b]);
        if (ra == rb) return;
        if (ra < rb) _splitParent[rb] = ra; else _splitParent[ra] = rb; // the lower index wins: order-independent
    }

    /// <summary>
    /// Sleep timers and island sleep (Box2D's): a body is resting while its sleep speed stays under its threshold; an
    /// island sleeps once every body has rested for <see cref="PhysicsSettings2D.TimeToSleep"/>. Islands that may have
    /// come apart can't sleep: the sleepiest is split each step, so its resting parts sleep the next.
    /// </summary>
    private void UpdateSleep(float dt)
    {
        for (int k = 0; k < _justSleptCount; k++) _justSlept[_justSleptList[k]] = false;
        _justSleptCount = 0;
        var settings = Settings;
        if (!settings.EnableSleep) return;
        int stamp = ++_sleepStamp;
        int splitIsland = -1;
        float splitTime = 0;
        for (int k = 0; k < _awakeCount; k++)
        {
            int b = _awake[k];
            bool resting = AllowSleep[b] && _sleepVelocity[b] <= SleepThreshold[b];
            float time = SleepTime[b] = resting ? SleepTime[b] + dt : 0;
            int i = IslandOf[b];
            if (time < settings.TimeToSleep) _islandStamp[i] = stamp;
            else if (_islands[i].ConstraintRemoveCount > 0 && (time > splitTime || (time == splitTime && i > splitIsland)))
            {
                splitIsland = i;
                splitTime = time;
            }
        }
        // Backwards: sleeping swaps the last awake island into the hole, and that one has been looked at.
        for (int k = _awakeIslandCount - 1; k >= 0; k--)
        {
            int i = _awakeIslands[k];
            if (_islandStamp[i] != stamp) TrySleepIsland(i);
        }
        if (splitIsland >= 0 && _islands[splitIsland].AwakeIndex >= 0) SplitIsland(splitIsland);
    }

    private void GrowIslandBodies(int capacity)
    {
        int old = IslandOf.Length;
        Array.Resize(ref IslandOf, capacity); Array.Resize(ref IslandIndex, capacity); Array.Resize(ref _awakeIndex, capacity);
        Array.Resize(ref _contactHead, capacity); Array.Resize(ref _contactDegree, capacity);
        Array.Resize(ref _jointHead, capacity); Array.Resize(ref _jointDegree, capacity);
        for (int b = old; b < capacity; b++) { IslandOf[b] = -1; IslandIndex[b] = -1; _awakeIndex[b] = -1; _contactHead[b] = -1; _jointHead[b] = -1; }
    }

    private void CloneIslands(PhysicsWorld2D w)
    {
        w._islands = (Island[])_islands.Clone();
        for (int i = 0; i < _islandHigh; i++)
        {
            ref var island = ref w._islands[i];
            if (island.Bodies is not null) { island.Bodies = (int[])island.Bodies.Clone(); island.Contacts = (int[])island.Contacts.Clone(); island.Joints = (int[])island.Joints.Clone(); }
        }
        w._freeIslands = (int[])_freeIslands.Clone(); w._awakeIslands = (int[])_awakeIslands.Clone(); w._islandStamp = (int[])_islandStamp.Clone();
        w.IslandOf = (int[])IslandOf.Clone(); w.IslandIndex = (int[])IslandIndex.Clone(); w._awakeIndex = (int[])_awakeIndex.Clone();
        w._contactHead = (int[])_contactHead.Clone(); w._contactDegree = (int[])_contactDegree.Clone();
        w._jointHead = (int[])_jointHead.Clone(); w._jointDegree = (int[])_jointDegree.Clone();
        w.Links = (ContactLink[])Links.Clone(); w._awakeContacts = (int[])_awakeContacts.Clone();
        w._splitParent = new int[_splitParent.Length]; w._splitRoot = new int[_splitRoot.Length]; w._splitBodies = new int[_splitBodies.Length]; w._splitContacts = new int[_splitContacts.Length]; w._splitJoints = new int[_splitJoints.Length];
    }
}
