using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class StateReaderRegistryTests {
    [Fact]
    public void StableBindingRepeatsButEqualSchemaDoesNotChooseBetweenDifferentReaders() {
        StateReaderRegistry registry = new();
        DurableSchema schema = new("Model", 1, new DurableFieldInfo(1, TypeTag.Byte));
        StateReaderBinding<byte> first = Binding(schema);
        registry.Register(first);
        registry.Register(first);
        Assert.Throws<InvalidOperationException>(() => registry.Register(Binding(schema)));
        Assert.Throws<InvalidOperationException>(() => registry.Register(
            Binding(new("Model", 1, new DurableFieldInfo(1, TypeTag.SByte)))));
        registry.Register(Binding(new("Model", 2, new DurableFieldInfo(1, TypeTag.Byte))));
        registry.Register(Binding(new("Other", 1, new DurableFieldInfo(1, TypeTag.Byte))));
        Assert.Throws<ArgumentNullException>(() => registry.Register((StateReaderBinding)null!));
    }

    private static StateReaderBinding<byte> Binding(DurableSchema schema) => new(
        schema,
        static (ref BareValueReader reader) => reader.ReadByte(),
        static (ref BareValueReader reader, in byte prior) => reader.ReadByte(),
        static (in byte state, IStateReferenceVisitor visitor) => { });
}
