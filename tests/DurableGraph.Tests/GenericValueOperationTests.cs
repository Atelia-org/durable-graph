using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed class GenericValueOperationTests {
    [Fact]
    public void FloatingChangesUseBitsIncludingSignedZeroAndNanPayloads() {
        CheckChange<Half, HalfStateOps>(TypeTag.Half, (Half)0, BitConverter.UInt16BitsToHalf(0x8000));
        CheckChange<float, SingleStateOps>(TypeTag.Single, 0f, -0f);
        CheckChange<double, DoubleStateOps>(TypeTag.Double, 0d, -0d);
        CheckChange<float, SingleStateOps>(TypeTag.Single,
            BitConverter.Int32BitsToSingle(unchecked((int)0x7FC00001)),
            BitConverter.Int32BitsToSingle(unchecked((int)0x7FC00002)));
        CheckChange<double, DoubleStateOps>(TypeTag.Double,
            BitConverter.Int64BitsToDouble(0x7FF8000000000001),
            BitConverter.Int64BitsToDouble(0x7FF8000000000002));
    }

    [Fact]
    public void UInt32NumberStringIdAndDurableIdHaveDistinctReferenceSemantics() {
        TypeExpr family = TypeExpr.Named("Box", TypeExpr.Builtin(TypeTag.Int32));
        DurableSchema schema = new(family, 1, []);
        StateReferenceValidator visitor = new(new Dictionary<ObjectId, ObjectStateRecord> {
            [new ObjectId(1)] = new(new ObjectId(1), schema, 0), [new ObjectId(2)] = new(new ObjectId(2), "value"),
        });
        ObjectId durable = new(1), text = new(2);
        uint unknown = 999;
        UInt32StateOps.VisitReferences(in unknown, visitor, new(1, TypeTag.UInt32));
        StringIdStateOps.VisitReferences(in text, visitor, new(1, TypeTag.String));
        DurableIdStateOps.VisitReferences(in durable, visitor, DurableFieldInfo.Reference(1, family));
        Assert.Throws<InvalidDataException>(() => StringIdStateOps.VisitReferences(in durable, visitor, new(1, TypeTag.String)));
        Assert.Throws<InvalidDataException>(() => DurableIdStateOps.VisitReferences(in text, visitor, DurableFieldInfo.Reference(1, family)));
        Assert.Throws<InvalidDataException>(() => visitor.VisitDurable(durable,
            TypeExpr.Named("Box", TypeExpr.Builtin(TypeTag.String))));
        Assert.Throws<InvalidDataException>(() => visitor.VisitDurable(durable, "Box"));
    }

    [Fact]
    public void ConstructedReferenceAncestryIsInvariantAndUsesStoredSchemas() {
        TypeExpr stringBase = TypeExpr.Named("Base", TypeExpr.Builtin(TypeTag.String));
        TypeExpr nodeBase = TypeExpr.Named("Base", TypeExpr.Named("Node"));
        DurableSchema oldBase = new(stringBase, 1, []);
        DurableSchema newBase = new(nodeBase, 1, []);
        DurableSchema oldDerived = new(TypeExpr.Named("Derived"), 1, [], oldBase);
        DurableSchema newDerived = new(TypeExpr.Named("Derived"), 2, [], newBase);
        StateReferenceValidator oldView = new(new Dictionary<ObjectId, ObjectStateRecord> { [new ObjectId(1)] = new(new ObjectId(1), oldDerived, 0) });
        StateReferenceValidator newView = new(new Dictionary<ObjectId, ObjectStateRecord> { [new ObjectId(1)] = new(new ObjectId(1), newDerived, 0) });
        oldView.VisitDurable(new ObjectId(1), stringBase);
        newView.VisitDurable(new ObjectId(1), nodeBase);
        Assert.Throws<InvalidDataException>(() => oldView.VisitDurable(new ObjectId(1), nodeBase));
        Assert.Throws<InvalidDataException>(() => newView.VisitDurable(new ObjectId(1), stringBase));
        Assert.Throws<ArgumentException>(() => newView.VisitDurable(new ObjectId(0), TypeExpr.Parameter(0)));
    }

    private static void CheckChange<T, TOps>(TypeTag tag, T prior, T current)
        where T : unmanaged where TOps : IStateOps<T> {
        DurableFieldInfo slot = new(1, tag);
        PreparedDeltaBody delta = TOps.PrepareDelta(in prior, in current, slot);
        Assert.True(delta.HasChanges);
        Assert.False(TOps.StateEquals(in prior, in current, slot));
        BareValueReader reader = new(delta.Body);
        T restored = TOps.ApplyDelta(ref reader, in prior, slot);
        reader.EnsureFullyConsumed();
        Assert.Equal(Encode<T, TOps>(in current, slot), Encode<T, TOps>(in restored, slot));
        Assert.False(TOps.PrepareDelta(in restored, in current, slot).HasChanges);
        Assert.True(TOps.StateEquals(in restored, in current, slot));
        byte[] same = Encode<T, TOps>(in prior, slot);
        Assert.Throws<InvalidDataException>(() => {
            BareValueReader redundant = new(same);
            TOps.ApplyDelta(ref redundant, in prior, slot);
        });
    }

    private static byte[] Encode<T, TOps>(in T state, DurableFieldInfo slot)
        where T : unmanaged where TOps : IStateOps<T> {
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        TOps.WriteBase(ref writer, in state, slot);
        return buffer.WrittenSpan.ToArray();
    }
}
