using System.Numerics;
using static Gamegrl.Tests.Harness;

namespace Gamegrl.Tests;

public struct InspectorHints : IComponent
{
    [Header("Movement \"settings\"\n"), Tooltip("Speed in units/second"), Range(0, 10)] public float Speed;
    public float Unbounded;
    [Header("Advanced"), Tooltip("Nested settings")] public Settings Extra;
    [NoSerialize, Header("Hidden")] public int Runtime;
    public struct Settings
    {
        [Range(-5, 5)] public int Count;
        public bool Enabled;
    }
}

internal static class MetadataTests
{
    public static unsafe void Register()
    {
        Test("metadata: generated hints precede fields, reset per field and survive nested structs under AOT", () =>
        {
            var visitor = new Recorder();
            var value = new InspectorHints();
            ComponentRegistry.Get(Component<InspectorHints>.Id).Serializer!.Visit(&value, visitor);
            Assert.Equal(5, visitor.Fields.Count);
            Assert.Equal(new FieldMetadata("Movement \"settings\"\n", "Speed in units/second", 0, 10), visitor.Fields["Speed"]);
            Assert.Equal(default, visitor.Fields["Unbounded"]);
            Assert.Equal(new FieldMetadata("Advanced", "Nested settings"), visitor.Fields["Extra"]);
            Assert.Equal(new FieldMetadata(Minimum: -5, Maximum: 5), visitor.Fields["Count"]);
            Assert.Equal(default, visitor.Fields["Enabled"]);
            Assert.True(!visitor.Fields.ContainsKey("Runtime"));
        });
        Test("metadata: presentation hints leave text and binary serialization values unchanged", () =>
        {
            using var world = new World();
            world.Spawn(new InspectorHints { Speed = 99, Unbounded = -123, Extra = new() { Count = -99 } });
            string text = Scene.SaveText(world);
            using var loaded = new World();
            Scene.LoadBinary(loaded, Scene.SaveBinary(world));
            Assert.Equal(text, Scene.SaveText(loaded));
            Assert.True(text.Contains("Speed = 99", StringComparison.Ordinal));
            Assert.True(text.Contains("Extra.Count = -99", StringComparison.Ordinal));
        });
    }
    private sealed class Recorder : IFieldVisitor
    {
        private FieldMetadata _next;
        public Dictionary<string, FieldMetadata> Fields { get; } = new();
        public void Metadata(in FieldMetadata metadata) => _next = metadata;
        public void Begin(string name) => Fields.Add(name, _next);
        public void End() { }
        public void Field(string name, ref bool value) => Fields.Add(name, _next);
        public void Field(string name, ref byte value) => Fields.Add(name, _next);
        public void Field(string name, ref sbyte value) => Fields.Add(name, _next);
        public void Field(string name, ref short value) => Fields.Add(name, _next);
        public void Field(string name, ref ushort value) => Fields.Add(name, _next);
        public void Field(string name, ref int value) => Fields.Add(name, _next);
        public void Field(string name, ref uint value) => Fields.Add(name, _next);
        public void Field(string name, ref long value) => Fields.Add(name, _next);
        public void Field(string name, ref ulong value) => Fields.Add(name, _next);
        public void Field(string name, ref float value) => Fields.Add(name, _next);
        public void Field(string name, ref double value) => Fields.Add(name, _next);
        public void Field(string name, ref Vector2 value) => Fields.Add(name, _next);
        public void Field(string name, ref Vector3 value) => Fields.Add(name, _next);
        public void Field(string name, ref Vector4 value) => Fields.Add(name, _next);
        public void Field(string name, ref Quaternion value) => Fields.Add(name, _next);
        public void Field(string name, ref Matrix4x4 value) => Fields.Add(name, _next);
        public void Field(string name, ref Guid value) => Fields.Add(name, _next);
        public void Field(string name, ref FixedString64 value) => Fields.Add(name, _next);
        public void Field(string name, ref Entity value) => Fields.Add(name, _next);
        public void Asset(string name, ref Guid value) => Fields.Add(name, _next);
        public void Enum(string name, ref long value, ReadOnlySpan<string> names, ReadOnlySpan<long> values) => Fields.Add(name, _next);
    }
}
