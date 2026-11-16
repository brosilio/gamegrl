using System.Numerics;
using static Gamegrl.Tests.Harness;

namespace Gamegrl.Tests;

public struct Position : IComponent { public float X, Y; }
public struct Velocity : IComponent { public float X, Y; }
public struct Health : IComponent { public int Value; }
public struct Frozen : IComponent;
public struct Counter : IComponent { public int Value; }

public struct Gravity { public float Value; }
public struct Tally { public int Visited; }

public static partial class TestSystems
{
    [System(Phase.FixedUpdate)]
    private static void Integrate(ref Position p, in Velocity v, [Res] in Time time)
    {
        p.X += v.X * time.FixedDelta;
        p.Y += v.Y * time.FixedDelta;
    }

    [System(Phase.Update, Parallel = true)]
    [Without<Frozen>]
    private static void Fall(ref Velocity v, [Res] in Gravity g) => v.Y -= g.Value;

    [System(Phase.Update)]
    private static void CountHealthy(Entity e, in Health h, [Res] ref Tally tally)
    {
        if (!e.IsNull && h.Value > 0) tally.Visited++;
    }

    [System(Phase.Update, Order = 10)]
    private static void Reap(Entity e, in Health h, CommandBuffer commands)
    {
        if (h.Value <= 0) commands.Destroy(e);
    }
}

internal static class CoreTests
{
    public static void Register()
    {
        Test("entity: spawn, get, set, destroy, generations", () =>
        {
            using var w = new World();
            var a = w.Spawn(new Position { X = 1 }, new Velocity { Y = 2 });
            Assert.True(w.IsAlive(a));
            Assert.Equal(1f, w.Get<Position>(a).X);
            w.Get<Position>(a).X = 5;
            Assert.Equal(5f, w.Get<Position>(a).X);
            w.Destroy(a);
            Assert.True(!w.IsAlive(a));
            var b = w.Spawn(new Position());
            Assert.Equal(a.Index, b.Index);            // index reused
            Assert.True(b.Generation != a.Generation); // but not the handle
            Assert.True(!w.IsAlive(default));
            Assert.Throws<InvalidOperationException>(() => w.Get<Position>(a));
        });

        Test("entity: add and remove move between archetypes and keep data", () =>
        {
            using var w = new World();
            var es = new Entity[100];
            for (int i = 0; i < es.Length; i++) es[i] = w.Spawn(new Position { X = i });
            for (int i = 0; i < es.Length; i += 2) w.Add(es[i], new Velocity { X = -i });
            for (int i = 0; i < es.Length; i++)
            {
                Assert.Equal((float)i, w.Get<Position>(es[i]).X);
                Assert.Equal(i % 2 == 0, w.Has<Velocity>(es[i]));
                if (i % 2 == 0) Assert.Equal((float)-i, w.Get<Velocity>(es[i]).X);
            }
            for (int i = 0; i < es.Length; i += 4) w.Remove<Velocity>(es[i]);
            for (int i = 0; i < es.Length; i++)
            {
                Assert.Equal((float)i, w.Get<Position>(es[i]).X);
                Assert.Equal(i % 2 == 0 && i % 4 != 0, w.Has<Velocity>(es[i]));
            }
            // destroying from the middle keeps everyone else's data (swap-back)
            for (int i = 1; i < es.Length; i += 3) w.Destroy(es[i]);
            for (int i = 0; i < es.Length; i++)
                if (i % 3 != 1) Assert.Equal((float)i, w.Get<Position>(es[i]).X);
            Assert.Equal(100 - 33, w.EntityCount);
        });

        Test("query: cached, sees archetypes created later, respects Without", () =>
        {
            using var w = new World();
            var q = w.Query(new QueryDesc().With<Position>().Without<Frozen>());
            Assert.True(ReferenceEquals(q, w.Query(new QueryDesc().With<Position>().Without<Frozen>())));
            w.Spawn(new Position());
            w.Spawn(new Position(), new Velocity());
            var f = w.Spawn(new Position());
            w.Add(f, new Frozen());
            Assert.Equal(2, q.Count());
        });

        Test("structural change during iteration throws; command buffer defers it", () =>
        {
            using var w = new World();
            var e = w.Spawn(new Position());
            w.BeginIteration();
            Assert.Throws<InvalidOperationException>(() => w.Add(e, new Velocity()));
            var spawned = w.Commands.Spawn();
            w.Commands.Add(spawned, new Health { Value = 7 });
            w.Commands.Add(e, new Velocity { X = 3 });
            w.EndIteration();
            w.Commands.Playback();
            Assert.Equal(3f, w.Get<Velocity>(e).X);
            Assert.Equal(7, w.Get<Health>(spawned).Value);
            Assert.True(w.Commands.IsEmpty);
        });

        Test("resources: set, get by ref, missing throws", () =>
        {
            using var w = new World();
            Assert.Throws<InvalidOperationException>(() => w.Resource<Gravity>());
            w.SetResource(new Gravity { Value = 9.8f });
            w.Resource<Gravity>().Value = 1;
            Assert.Equal(1f, w.Resource<Gravity>().Value);
        });

        Test("generated systems: serial, parallel, resources, Without, Entity, CommandBuffer", () =>
        {
            var b = EngineBuilder.Create();
            b.FixedRate = 10;
            Gamegrl.Generated.GamegrlTestsSystems.AddTo(b.Schedule);
            b.SetResource(new Gravity { Value = 1 });
            b.SetResource(new Tally());
            const int n = 10_000; // enough to split across workers
            for (int i = 0; i < n; i++) b.World.Spawn(new Position(), new Velocity { X = 1 });
            var frozen = b.World.Spawn(new Position(), new Velocity());
            b.World.Add(frozen, new Frozen());
            var alive = b.World.Spawn(new Health { Value = 5 });
            var dead = b.World.Spawn(new Health { Value = 0 });
            using var app = b.Build();

            app.Tick(0.1); // one fixed step at 10 Hz, then Update
            var q = app.World.Query(new QueryDesc().With<Velocity>().Without<Frozen>());
            int checkedCount = 0;
            foreach (var a in q.Archetypes)
            {
                foreach (ref var v in a.GetSpan<Velocity>())
                {
                    Assert.Equal(-1f, v.Y);
                    checkedCount++;
                }
                foreach (ref var p in a.GetSpan<Position>()) Assert.True(MathF.Abs(p.X - 0.1f) < 1e-6f);
            }
            Assert.Equal(n, checkedCount);
            Assert.Equal(0f, app.World.Get<Velocity>(frozen).Y);
            Assert.Equal(1, app.World.Resource<Tally>().Visited);
            Assert.True(app.World.IsAlive(alive));
            Assert.True(!app.World.IsAlive(dead));
        });

        Test("jobs: parallel for covers every index exactly once", () =>
        {
            using var jobs = new JobScheduler(4);
            var hits = new int[100_003];
            for (int round = 0; round < 50; round++)
            {
                var job = new MarkJob { Hits = hits };
                jobs.ParallelFor(hits.Length, 97, ref job);
            }
            foreach (int h in hits) Assert.Equal(50, h);
        });

        Test("app: fixed steps follow accumulated time", () =>
        {
            var b = EngineBuilder.Create();
            b.FixedRate = 50;
            using var app = b.Build();
            for (int i = 0; i < 10; i++) app.Tick(0.03); // 0.3 s => 15 steps
            Assert.Equal(15ul, app.World.Resource<Time>().Tick);
            Assert.Equal(10ul, app.World.Resource<Time>().Frame);
        });

        Test("transforms: LocalToWorld follows LocalTransform", () =>
        {
            var b = EngineBuilder.Create().AddModule<CoreModule>();
            var e = b.World.Spawn(LocalTransform.At(new Vector3(1, 2, 3), 2), new LocalToWorld());
            using var app = b.Build();
            app.Tick(0.016);
            var m = app.World.Get<LocalToWorld>(e).Value;
            Assert.Equal(new Vector3(1, 2, 3), m.Translation);
            Assert.Equal(2f, m.M11);
        });
    }

    private struct MarkJob : IJobFor
    {
        public int[] Hits;
        public readonly void Execute(int start, int end)
        {
            for (int i = start; i < end; i++) Interlocked.Increment(ref Hits[i]);
        }
    }
}
