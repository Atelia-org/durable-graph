using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed class StateReaderBindingTests {
    private static readonly DurableSchema Schema = new("reader.test", 1,
        new DurableFieldInfo(1, TypeTag.Byte), new DurableFieldInfo(2, TypeTag.String));

    [Fact]
    public void ReaderReconstructsTypedChainAndReturnsIndependentStateCopies() {
        BodySource source = new([10, 7], [1, 11], [2, 9], [1, 12]);
        StateReaderBinding<State> binding = new(Schema, ReadBase, ApplyDelta, ValidateStrings);
        ObjectStateRecord row = binding.Read(new ObjectId(3), source);

        Assert.Equal(new[] { 0, 1, 2, 3 }, source.Requests);
        Assert.Equal(new ObjectId(3u), row.Id);
        Assert.Same(Schema, row.Schema);
        Assert.Equal(ObjectStateKind.Durable, row.Kind);
        Assert.Null(row.Preparation);
        State copy = row.GetState<State>();
        Assert.Equal((byte)12, copy.Value);
        Assert.Equal(new ObjectId(9u), copy.Text);
        copy.Value = 99;
        copy.Text = new(99);
        Assert.Equal((byte)12, row.GetState<State>().Value);
        Assert.Equal(new ObjectId(9u), row.GetState<State>().Text);
        // Only the final DTO is validated: string 7 is no longer referenced.
        binding.VisitReferences(row, new StateReferenceValidator(new Dictionary<ObjectId, ObjectStateRecord> { [new ObjectId(9)] = new(new ObjectId(9), "current") }));
        Assert.Throws<InvalidDataException>(() => binding.VisitReferences(row, new StateReferenceValidator(new Dictionary<ObjectId, ObjectStateRecord>())));
    }

    [Fact]
    public void ReaderAcceptsBaseOnlyAndNullStringReference() {
        StateReaderBinding<State> binding = new(Schema, ReadBase, ApplyDelta, ValidateStrings);
        ObjectStateRecord row = binding.Read(new ObjectId(uint.MaxValue), new BodySource([5, 0]));
        Assert.Equal((byte)5, row.GetState<State>().Value);
        binding.VisitReferences(row, new StateReferenceValidator(new Dictionary<ObjectId, ObjectStateRecord>()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TrailingBytesRejectTheWholeReadBeforeAnyLaterBody(bool inBase) {
        BodySource source = inBase
            ? new([10, 0, 99], [1, 12])
            : new([10, 0], [1, 12, 99], [1, 13]);
        StateReaderBinding<State> binding = new(Schema, ReadBase, ApplyDelta, ValidateStrings);
        ObjectStateRecord? result = null;
        Assert.Throws<InvalidDataException>(() => result = binding.Read(new ObjectId(1), source));
        Assert.Null(result);
        Assert.Equal(inBase ? new[] { 0 } : new[] { 0, 1 }, source.Requests);
    }

    [Fact]
    public void TruncatedDeltaPropagatesWithoutReturningAPartialDto() {
        StateReaderBinding<State> binding = new(Schema, ReadBase, ApplyDelta, ValidateStrings);
        ObjectStateRecord? result = null;
        Assert.Throws<EndOfStreamException>(() => result = binding.Read(new ObjectId(1), new BodySource([10, 0], [1])));
        Assert.Null(result);
    }

    [Fact]
    public void EmptySourceAndZeroObjectIdAreRejectedBeforeReading() {
        StateReaderBinding<State> binding = new(Schema, ReadBase, ApplyDelta, ValidateStrings);
        BodySource empty = new();
        Assert.Throws<InvalidDataException>(() => binding.Read(new ObjectId(1), empty));
        Assert.Empty(empty.Requests);
        BodySource source = new([10, 0]);
        Assert.Throws<InvalidDataException>(() => binding.Read(new ObjectId(0), source));
        Assert.Empty(source.Requests);
        Assert.Throws<ArgumentNullException>(() => binding.Read(new ObjectId(1), null!));
    }

    [Fact]
    public void ReferenceValidationRejectsWrongKindSchemaAncestorOrDtoBeforeCallback() {
        int calls = 0;
        StateReaderBinding<State> binding = new(Schema, ReadBase, ApplyDelta,
            (in State state, IStateReferenceVisitor visitor) => calls++);
        StateReferenceValidator table = new(new Dictionary<ObjectId, ObjectStateRecord>());
        DurableSchema wrongFields = new(Schema.SchemaId, Schema.Version,
            new DurableFieldInfo(1, TypeTag.UInt32), new DurableFieldInfo(2, TypeTag.String));
        DurableSchema wrongVersion = new(Schema.SchemaId, 2, Schema.Fields.ToArray());
        DurableSchema wrongAncestor = new(Schema.SchemaId, Schema.Version, Schema.Fields.ToArray(),
            new DurableSchema("reader.ancestor", 1));
        ObjectStateRecord[] invalidRows = [
            new(new ObjectId(1), "text"),
            new(new ObjectId(1), wrongFields, new State()),
            new(new ObjectId(1), wrongVersion, new State()),
            new(new ObjectId(1), wrongAncestor, new State()),
            new(new ObjectId(1), Schema, 123u),
        ];
        foreach (ObjectStateRecord row in invalidRows) {
            Assert.Throws<InvalidDataException>(() => binding.VisitReferences(row, table));
        }
        Assert.Equal(0, calls);
        // Equivalent immutable definitions need not be the same CLR instance.
        DurableSchema equal = new(Schema.SchemaId, Schema.Version, Schema.Fields.ToArray());
        binding.VisitReferences(new ObjectStateRecord(new ObjectId(1), equal, new State()), table);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void BindingRejectsMissingSchemaOrCallbacks() {
        Assert.Throws<ArgumentNullException>(() => new StateReaderBinding<State>(null!, ReadBase, ApplyDelta, ValidateStrings));
        Assert.Throws<ArgumentNullException>(() => new StateReaderBinding<State>(Schema, null!, ApplyDelta, ValidateStrings));
        Assert.Throws<ArgumentNullException>(() => new StateReaderBinding<State>(Schema, ReadBase, null!, ValidateStrings));
        Assert.Throws<ArgumentNullException>(() => new StateReaderBinding<State>(Schema, ReadBase, ApplyDelta, null!));
    }

    [Fact]
    public void DecodedStringTableCopiesBindingsAndRetainsDistinctNonemptyInstances() {
        string first = new(['x']);
        string second = new(['x']);
        List<(ObjectId Id, string Value)> records = [(new ObjectId(7), first), (new ObjectId(9), second), (new ObjectId(11), string.Empty), (new ObjectId(12), string.Empty)];
        StringReadTable table = StringReadTable.FromDecoded(records);
        records[0] = (new ObjectId(7), "replacement");
        records.Clear();
        Assert.Same(first, table.ResolveString(new ObjectId(7)));
        Assert.Same(second, table.ResolveString(new ObjectId(9)));
        Assert.NotSame(table.ResolveString(new ObjectId(7)), table.ResolveString(new ObjectId(9)));
        Assert.Equal(table.ResolveString(new ObjectId(7)), table.ResolveString(new ObjectId(9)));
        Assert.Same(string.Empty, table.ResolveString(new ObjectId(11)));
        Assert.Same(string.Empty, table.ResolveString(new ObjectId(12)));
        Assert.Null(table.ResolveString(new ObjectId(0)));
        Assert.Throws<InvalidDataException>(() => table.ResolveString(new ObjectId(8)));
    }

    [Fact]
    public void DecodedStringTableRejectsInvalidIdentitiesAndMissingContents() {
        string shared = new(['x']);
        Assert.Throws<InvalidDataException>(() => StringReadTable.FromDecoded([(new ObjectId(0u), shared)]));
        Assert.Throws<InvalidDataException>(() => StringReadTable.FromDecoded([(new ObjectId(1u), shared), (new ObjectId(1u), "other")]));
        Assert.Throws<InvalidDataException>(() => StringReadTable.FromDecoded([(new ObjectId(1u), shared), (new ObjectId(2u), shared)]));
        Assert.Throws<InvalidDataException>(() => StringReadTable.FromDecoded([(new ObjectId(1u), null!)]));
        Assert.Throws<InvalidDataException>(() => StringReadTable.FromDecoded([(new ObjectId(1u), string.Empty), (new ObjectId(1u), string.Empty)]));
        Assert.Throws<ArgumentNullException>(() => StringReadTable.FromDecoded(null!));
        Assert.Null(StringReadTable.FromDecoded([]).ResolveString(new ObjectId(0)));
    }

    [Fact]
    public void DecodedStringEnumerationFailureReturnsNoPartialTable() {
        bool disposed = false;
        IEnumerable<(ObjectId Id, string Value)> Records() {
            try {
                yield return (new ObjectId(1), "first");
                throw new IOException("late failure");
            } finally {
                disposed = true;
            }
        }
        StringReadTable? result = null;
        Assert.Throws<IOException>(() => result = StringReadTable.FromDecoded(Records()));
        Assert.Null(result);
        Assert.True(disposed);
    }

    private struct State {
        public byte Value;
        public ObjectId Text;
    }

    private static State ReadBase(ref BareValueReader reader) => new() {
        Value = reader.ReadByte(),
        Text = new ObjectId(reader.ReadVarUInt32()),
    };

    private static State ApplyDelta(ref BareValueReader reader, in State prior) {
        State result = prior;
        byte bitmap = reader.ReadByte();
        if ((bitmap & 1) != 0) {
            result.Value = reader.ReadByte();
        }
        if ((bitmap & 2) != 0) {
            result.Text = new ObjectId(reader.ReadVarUInt32());
        }
        return result;
    }

    private static void ValidateStrings(in State state, IStateReferenceVisitor visitor) => visitor.VisitString(state.Text);

    private sealed class BodySource(params byte[][] bodies) : IStateBodySource {
        public List<int> Requests { get; } = [];
        public int Count => bodies.Length;
        public ReadOnlySpan<byte> GetBody(int index) {
            Requests.Add(index);
            return bodies[index];
        }
    }
}
