using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed class InlineSchemaContractTests {
    [Fact]
    public void ExactValueDependencyParticipatesInOwnerEqualityAndHash() {
        DurableSchema value1 = Value("point", 1, new DurableFieldInfo(1, TypeTag.Int32));
        DurableSchema value1Copy = Value("point", 1, new DurableFieldInfo(1, TypeTag.Int32));
        DurableSchema owner = Owner(value1);
        DurableSchema equal = Owner(value1Copy);
        Assert.Equal(owner, equal);
        Assert.Equal(owner.GetHashCode(), equal.GetHashCode());
        Assert.Equal(owner.Fields[0], equal.Fields[0]);
        Assert.NotEqual(owner, Owner(Value("point", 2, new DurableFieldInfo(1, TypeTag.Int32))));
        Assert.NotEqual(owner, Owner(Value("point", 1, new DurableFieldInfo(1, TypeTag.Int64))));
        Assert.NotEqual(value1, new DurableSchema("point", 1, new DurableFieldInfo(1, TypeTag.Int32)));
    }

    [Fact]
    public void InlineFieldsHaveExclusiveExactValueOperand() {
        DurableSchema point = Value("point", 1);
        DurableSchema reference = new("node", 1);
        Assert.Throws<ArgumentNullException>(() => new DurableFieldInfo(1, TypeTag.InlineValue));
        Assert.Throws<ArgumentException>(() => new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: reference));
        Assert.Throws<ArgumentException>(() => new DurableFieldInfo(1, TypeTag.InlineValue, "point", point));
        Assert.Throws<ArgumentException>(() => new DurableFieldInfo(1, TypeTag.Int32, inlineSchema: point));
        Assert.Throws<ArgumentException>(() => new DurableFieldInfo(1, TypeTag.ObjectReference, "node", point));
        Assert.Throws<ArgumentException>(() => new DurableSchema("point", 1, [], reference, SchemaKind.InlineValue));
        Assert.Throws<ArgumentException>(() => new DurableSchema("node", 1, [], point));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableSchema("point", 1, (SchemaKind)0));
        Assert.Same(point, Owner(point).Fields[0].InlineSchema);
        Assert.Null(Owner(point).Fields[0].TargetSchemaId);
    }

    [Fact]
    public void SharedDeepDagEqualityDoesNotExpandAsATree() {
        DurableSchema left = Value("leaf", 1, new DurableFieldInfo(1, TypeTag.Double));
        DurableSchema right = Value("leaf", 1, new DurableFieldInfo(1, TypeTag.Double));
        DurableSchema different = Value("leaf", 1, new DurableFieldInfo(1, TypeTag.Single));
        for (int depth = 0; depth < 200; depth++) {
            left = Duplicate("layer" + depth, left);
            right = Duplicate("layer" + depth, right);
            different = Duplicate("layer" + depth, different);
        }
        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.NotEqual(left, different);
    }

    [Fact]
    public void InlineSchemaCannotAcquireObjectOperationsOrIdentity() {
        DurableSchema point = Value("point", 1);
        Assert.Throws<ArgumentException>(() => new StateReaderBinding<int>(point, Read, Apply, Visit));
        Assert.Throws<ArgumentException>(() => new StatePreparationBinding<int>(point, PrepareBase, PrepareDelta));
        Assert.Throws<ArgumentException>(() => new ObjectStateRecord(new ObjectId(1), point, 42));
        CaptureSession session = new();
        using CaptureContext context = session.BeginCapture();
        Assert.Throws<ArgumentException>(() => context.AddRoot<Domain, int>(new Domain(), point, (_, _) => 42));
        Assert.False(StateReferenceValidator.Accepts(point, point.SchemaId));
    }

    private static DurableSchema Value(string id, int version, params DurableFieldInfo[] fields) =>
        new(id, version, SchemaKind.InlineValue, fields);
    private static DurableSchema Owner(DurableSchema inline) => new("owner", 1,
        new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: inline));
    private static DurableSchema Duplicate(string id, DurableSchema inline) => Value(id, 1,
        new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: inline), new(2, TypeTag.InlineValue, inlineSchema: inline));
    private static int Read(ref BareValueReader reader) => reader.ReadVarInt32();
    private static int Apply(ref BareValueReader reader, in int prior) => reader.ReadVarInt32();
    private static void Visit(in int value, IStateReferenceVisitor visitor) { }
    private static PreparedBaseBody PrepareBase(in int value) => new([]);
    private static PreparedDeltaBody PrepareDelta(in int prior, in int current) => new(false, []);
    private sealed class Domain : IDurableObject { }
}
