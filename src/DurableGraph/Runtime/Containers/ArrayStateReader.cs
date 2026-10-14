using Atelia.Binary;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Runtime;

/// <summary>Reads exact array state without requiring the historical domain element CLR type.</summary>
public abstract class ArrayStateReader : ObjectReaderBinding {
    private protected ArrayStateReader(ArrayLayout layout) : base(ObjectLayout.ForArray(layout)) { }

    public static ArrayStateReader Create(ArrayLayout layout, StateValueBinding element) {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(element);
        if (layout.ElementSlot != DurableFieldInfo.WithFieldId(element.Slot, 1)) {
            throw new ArgumentException("The stored element binding must match the complete array element layout.");
        }
        return (ArrayStateReader)Activator.CreateInstance(
            typeof(ArrayStateReader<,>).MakeGenericType(element.StateType, element.StateOpsType), layout)!;
    }
}

internal sealed class ArrayStateReader<TState, TOps> : ArrayStateReader
    where TState : unmanaged where TOps : IStateOps<TState> {
    public ArrayStateReader(ArrayLayout layout) : base(layout) { }
    public override Type StateType => typeof(FrozenArrayState<TState>);

    internal override ObjectStateRecord Read(ObjectId id, IStateBodySource source) {
        ArgumentNullException.ThrowIfNull(source);
        if (id.IsNull || source.Count <= 0) { throw new InvalidDataException("An array requires a nonzero ID and a Base body."); }
        BareValueReader reader = new(source.GetBody(0));
        FrozenArrayState<TState> state = ArrayStateBody<TState, TOps>.ReadBase(ref reader, Layout.Array!);
        reader.EnsureFullyConsumed();
        for (int index = 1; index < source.Count; index++) {
            reader = new(source.GetBody(index));
            state = ArrayStateBody<TState, TOps>.ApplyDelta(ref reader, state, Layout.Array!);
            reader.EnsureFullyConsumed();
        }
        return new(id, Layout.Array!, state);
    }

    internal override void VisitReferences(ObjectStateRecord item, IStateReferenceVisitor visitor) {
        if (!Layout.Equals(item.Layout)) { throw new InvalidDataException("The array reader requires its exact layout."); }
        ArrayStateBody<TState, TOps>.VisitReferences(item.GetArrayState<TState>(), visitor, Layout.Array!);
    }
}

internal static class ArrayStateBody<TState, TOps> where TState : unmanaged where TOps : IStateOps<TState> {
    internal static PreparedBaseBody PrepareBase(FrozenArrayState<TState> state, ArrayLayout layout) {
        RequireShape(state.Shape, layout);
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        foreach (int length in state.Shape.Lengths) { writer.WriteVarUInt32((uint)length); }
        foreach (ref readonly TState element in state.Elements) { TOps.WriteBase(ref writer, in element, layout.ElementSlot); }
        return new(buffer.WrittenSpan);
    }

    internal static FrozenArrayState<TState> ReadBase(ref BareValueReader reader, ArrayLayout layout) {
        int[] lengths = new int[layout.Rank];
        for (int dimension = 0; dimension < lengths.Length; dimension++) {
            uint length = reader.ReadVarUInt32();
            if (length > Array.MaxLength) { throw new InvalidDataException("Array dimension exceeds the supported CLR length."); }
            lengths[dimension] = (int)length;
        }
        ArrayShape shape = new(lengths);
        int minimum = StateBodySize.MinimumBaseBytes(layout.ElementSlot);
        if (minimum > 0 && shape.Count > reader.RemainingCount / minimum) {
            throw new InvalidDataException("Array length cannot fit in the remaining Base payload.");
        }
        TState[] elements = new TState[shape.Count];
        for (int index = 0; index < elements.Length; index++) { elements[index] = TOps.ReadBase(ref reader, layout.ElementSlot); }
        return new(shape, elements, takeOwnership: true);
    }

    internal static PreparedDeltaBody PrepareDelta(FrozenArrayState<TState> prior, FrozenArrayState<TState> current, ArrayLayout layout) {
        RequireShape(prior.Shape, layout);
        RequireShape(current.Shape, layout);
        if (!prior.Shape.Equals(current.Shape)) { throw new InvalidOperationException("An array Delta cannot change shape."); }
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        bool changed = false;
        for (int index = 0; index < current.Shape.Count; index++) {
            PreparedDeltaBody delta = TOps.PrepareDelta(in prior.Elements[index], in current.Elements[index], layout.ElementSlot);
            if (!delta.HasChanges) { continue; }
            changed = true;
            writer.WriteVarUInt32((uint)index + 1);
            writer.WriteRawBytes(delta.Body);
        }
        writer.WriteVarUInt32(0);
        return new(changed, buffer.WrittenSpan);
    }

    internal static FrozenArrayState<TState> ApplyDelta(ref BareValueReader reader, FrozenArrayState<TState> prior, ArrayLayout layout) {
        RequireShape(prior.Shape, layout);
        TState[] elements = prior.Elements.ToArray();
        uint previous = 0;
        while (true) {
            uint encoded = reader.ReadVarUInt32();
            if (encoded == 0) { break; }
            if (encoded <= previous || encoded > elements.Length) {
                throw new InvalidDataException("Array Delta indexes must be strictly increasing and within the array.");
            }
            int index = (int)encoded - 1;
            elements[index] = TOps.ApplyDelta(ref reader, in elements[index], layout.ElementSlot);
            previous = encoded;
        }
        return new(prior.Shape, elements, takeOwnership: true);
    }

    internal static void VisitReferences(FrozenArrayState<TState> state, IStateReferenceVisitor visitor, ArrayLayout layout) {
        RequireShape(state.Shape, layout);
        foreach (ref readonly TState element in state.Elements) { TOps.VisitReferences(in element, visitor, layout.ElementSlot); }
    }

    private static void RequireShape(ArrayShape shape, ArrayLayout layout) {
        if (shape.Rank != layout.Rank) { throw new InvalidDataException("Array shape rank does not match its exact layout."); }
    }

}
