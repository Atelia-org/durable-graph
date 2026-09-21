using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed class ImmutableLeafBindingTests {
    [Fact]
    public void ManualBindingsDefaultToNotImmutableLeaf() {
        StateModelBinding model = Model(SimpleSchema());
        Assert.False(model.IsImmutableLeaf);
    }

    [Fact]
    public void ExplicitImmutableLeafFlagIsRetainedThroughTheBaseContract() {
        StateModelBinding model = Model(SimpleSchema(), isImmutableLeaf: true);
        Assert.True(model.IsImmutableLeaf);
    }

    private static DurableSchema SimpleSchema() => new("immutable-leaf-binding", 1, new DurableFieldInfo(1, TypeTag.Int32));

    private static StateModelBinding<Leaf, int> Model(DurableSchema schema, bool isImmutableLeaf = false) {
        CapturedStatePreparation<int> preparation = new(schema,
            static (in int state) => throw new NotSupportedException(),
            static (in int prior, in int next) => throw new NotSupportedException());
        StateReaderBinding<int> reader = new(schema,
            static (ref BinaryPayloadReader body) => throw new NotSupportedException(),
            static (ref BinaryPayloadReader body, in int prior) => throw new NotSupportedException(),
            static (in int state, IStateReferenceVisitor visitor) => { });
        return new(preparation, [reader],
            static _ => throw new InvalidOperationException("Leaf must not normalize."),
            static () => throw new InvalidOperationException("Leaf must not allocate."),
            static (Leaf domain, in int state, ObjectReadTable objects) => throw new NotSupportedException(),
            static (Leaf domain, CaptureContext context) => throw new NotSupportedException(),
            static (in int state, IStateReferenceVisitor visitor) => { },
            isImmutableLeaf: isImmutableLeaf);
    }

    private sealed class Leaf : IDurableObject { }
}
