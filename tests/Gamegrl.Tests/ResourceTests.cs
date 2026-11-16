using static Gamegrl.Tests.Harness;
namespace Gamegrl.Tests;
internal static class ResourceTests
{
    private struct Counter { public int Value; }
    private struct RuntimeOnly { public int Value; }
    private sealed class Mutable { public List<int> Values = new(); }
    public static void Register()
    {
        Test("resources: play sessions restore values, deep clones, factories and remove runtime slots", () =>
        {
            using var world = new World();
            var edit = new Mutable { Values = [1] };
            var builder = EngineBuilder.ForWorld(world)
                .SetResource(new Counter { Value = 3 })
                .SetResource(edit, value => new Mutable { Values = new(value.Values) });
            world.Resource<Counter>().Value = 7;
            using (builder.BeginResourceSession())
            {
                world.Resource<Counter>().Value = 99;
                world.Resource<Mutable>().Values.Add(2);
                world.SetResource(new RuntimeOnly { Value = 42 });
            }
            Assert.Equal(7, world.Resource<Counter>().Value);
            Assert.Equal(1, world.Resource<Mutable>().Values.Count);
            Assert.True(!world.HasResource<RuntimeOnly>());
            using (builder.BeginResourceSession()) { world.RemoveResource<Counter>(); world.Resource<Mutable>().Values.Clear(); }
            Assert.Equal(7, world.Resource<Counter>().Value); Assert.Equal(1, world.Resource<Mutable>().Values.Count);
            var factoryBuilder = EngineBuilder.ForWorld(world).SetResourceFactory(() => new Mutable { Values = [5] });
            var initial = world.Resource<Mutable>();
            using (factoryBuilder.BeginResourceSession())
            { Assert.True(!ReferenceEquals(initial, world.Resource<Mutable>())); world.Resource<Mutable>().Values.Clear(); }
            Assert.True(ReferenceEquals(initial, world.Resource<Mutable>()) && initial.Values.Count == 1);
        });
        Test("resources: unsafe shallow checkpoints are rejected and failed factories roll back", () =>
        {
            using var world = new World();
            var builder = EngineBuilder.ForWorld(world).SetResource(new Mutable());
            Assert.Throws<InvalidOperationException>(() => builder.BeginResourceSession());
            int calls = 0;
            var factory = EngineBuilder.ForWorld(world).SetResourceFactory(() =>
            { if (++calls > 1) throw new InvalidOperationException("factory failed"); return new Mutable(); });
            var edit = world.Resource<Mutable>();
            Assert.Throws<InvalidOperationException>(() => factory.BeginResourceSession());
            Assert.True(ReferenceEquals(edit, world.Resource<Mutable>()));
        });
        Test("app: editor clock checkpoint preserves fractional fixed-step time", () =>
        {
            var builder = EngineBuilder.Create(); builder.WorkerThreads = 0;
            using var app = builder.Build();
            app.Tick(0.01); long clock = app.CaptureClock();
            app.RestoreClock(0); app.Tick(0.001);
            app.RestoreClock(clock); app.Tick(0.007);
            Assert.Equal(1UL, app.World.Resource<Time>().Tick);
        });
    }
}
