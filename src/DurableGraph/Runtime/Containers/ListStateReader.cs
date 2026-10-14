using Atelia.Binary;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Runtime;

/// <summary>Reads exact List contents without requiring the historical domain element CLR type.</summary>
public abstract class ListStateReader : ObjectReaderBinding {
    private protected ListStateReader(ListLayout layout) : base(ObjectLayout.ForList(layout)) { }

    public static ListStateReader Create(ListLayout layout, StateValueBinding element) {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(element);
        if (layout.ElementSlot != DurableFieldInfo.WithFieldId(element.Slot, 1)) {
            throw new ArgumentException("The stored element binding must match the complete List element layout.");
        }
        return (ListStateReader)Activator.CreateInstance(
            typeof(ListStateReader<,>).MakeGenericType(element.StateType, element.StateOpsType), layout)!;
    }
}

internal sealed class ListStateReader<TState, TOps> : ListStateReader
    where TState : unmanaged where TOps : IStateOps<TState> {
    public ListStateReader(ListLayout layout) : base(layout) { }
    public override Type StateType => typeof(FrozenListState<TState>);

    internal override ObjectStateRecord Read(ObjectId id, IStateBodySource source) {
        ArgumentNullException.ThrowIfNull(source);
        if (id.IsNull || source.Count <= 0) { throw new InvalidDataException("A List requires a nonzero ID and a Base body."); }
        BareValueReader reader = new(source.GetBody(0));
        FrozenListState<TState> state = ListStateBody<TState, TOps>.ReadBase(ref reader, Layout.List!);
        reader.EnsureFullyConsumed();
        for (int index = 1; index < source.Count; index++) {
            reader = new(source.GetBody(index));
            state = ListStateBody<TState, TOps>.ApplyDelta(ref reader, state, Layout.List!);
            reader.EnsureFullyConsumed();
        }
        return new(id, Layout.List!, state);
    }

    internal override void VisitReferences(ObjectStateRecord item, IStateReferenceVisitor visitor) {
        if (!Layout.Equals(item.Layout)) { throw new InvalidDataException("The List reader requires its exact layout."); }
        ListStateBody<TState, TOps>.VisitReferences(item.GetListState<TState>(), visitor, Layout.List!);
    }
}

// Internal experiment seam: alternative matchers feed the same writer, without a public policy or format change.
internal delegate List<ListDeltaRange> ListDeltaPlanFactory<TState>(ReadOnlySpan<TState> prior,
    ReadOnlySpan<TState> current, DurableFieldInfo slot) where TState : unmanaged;

internal static class ListStateBody<TState, TOps> where TState : unmanaged where TOps : IStateOps<TState> {
    private const byte Copy = 1;
    private const byte New = 2;
    private const byte CopyAndPatch = 3;

    internal static PreparedBaseBody PrepareBase(FrozenListState<TState> state, ListLayout layout) {
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        writer.WriteVarUInt32((uint)state.Count);
        foreach (ref readonly TState element in state.Elements) { TOps.WriteBase(ref writer, in element, layout.ElementSlot); }
        return new(buffer.WrittenSpan);
    }

    internal static FrozenListState<TState> ReadBase(ref BareValueReader reader, ListLayout layout) {
        int count = ReadCount(ref reader);
        RequireTailFits(count, ref reader, layout);
        TState[] elements = new TState[count];
        for (int index = 0; index < elements.Length; index++) { elements[index] = TOps.ReadBase(ref reader, layout.ElementSlot); }
        return new(elements, takeOwnership: true);
    }

    internal static PreparedDeltaBody PrepareDelta(FrozenListState<TState> prior, FrozenListState<TState> current,
        ListLayout layout, ListDeltaAlgorithm algorithm = ListDeltaAlgorithm.Adaptive,
        ListDeltaPlanFactory<TState>? planFactory = null) =>
        PrepareDeltaObserved(prior, current, layout, out _, algorithm, planFactory);

    /// <summary>Encodes an owner-verified unchanged prefix and its new tail using the ordinary List Delta wire.</summary>
    /// <remarks>The owner proves the exact prior and that its prefix is unchanged; this emitter does not inspect old elements.</remarks>
    internal static PreparedDeltaBody PrepareAppendDelta(int priorCount, ReadOnlySpan<TState> appended, ListLayout layout) {
        ArgumentOutOfRangeException.ThrowIfNegative(priorCount);
        if (priorCount > Array.MaxLength || appended.Length > Array.MaxLength - priorCount) {
            throw new ArgumentOutOfRangeException(nameof(priorCount), "The appended List exceeds the supported CLR buffer length.");
        }
        ArgumentNullException.ThrowIfNull(layout);
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        writer.WriteVarUInt32((uint)(priorCount + appended.Length));
        if (priorCount != 0) { WriteSourceRange(ref writer, Copy, 0, priorCount); }
        if (!appended.IsEmpty) { TryWriteNewRange(ref writer, buffer, appended, layout, null, out _); }
        return new(!appended.IsEmpty, buffer.WrittenSpan);
    }

    internal static PreparedDeltaBody PrepareDeltaObserved(FrozenListState<TState> prior, FrozenListState<TState> current,
        ListLayout layout, out ListDeltaCompetitionObservation observation,
        ListDeltaAlgorithm algorithm = ListDeltaAlgorithm.Adaptive,
        ListDeltaPlanFactory<TState>? planFactory = null) {
        if (algorithm is not (ListDeltaAlgorithm.Position or ListDeltaAlgorithm.LocalResync or
                ListDeltaAlgorithm.BoundedMyers or ListDeltaAlgorithm.Adaptive)) {
            throw new ArgumentOutOfRangeException(nameof(algorithm));
        }
        bool changed = prior.Count != current.Count;
        if (!changed) {
            for (int index = 0; index < current.Count; index++) {
                if (!TOps.StateEquals(in prior.Elements[index], in current.Elements[index], layout.ElementSlot)) {
                    changed = true;
                    break;
                }
            }
        }
        // NoChange is a property of the two sequences, not of the matcher or its search budget.
        if (!changed) {
            ArrayBufferWriter<byte> buffer = new();
            BareValueWriter writer = new(buffer);
            writer.WriteVarUInt32((uint)current.Count);
            if (current.Count > 0) { WriteSourceRange(ref writer, Copy, 0, current.Count); }
            observation = new(ListDeltaCompetitionOutcome.NoChange, buffer.WrittenCount, 0, 0);
            return new(false, buffer.WrittenSpan);
        }

        // An explicit research plan overrides writer selection and is encoded exactly once.
        if (planFactory is null && algorithm == ListDeltaAlgorithm.Adaptive) {
            return ListDeltaCompetition<TState, TOps>.PrepareChanged(prior, current, layout, out observation);
        }
        List<ListDeltaRange> ranges = planFactory is null
            ? ListDeltaMatcher<TState, TOps>.Plan(prior.Elements, current.Elements, layout.ElementSlot, algorithm)
            : planFactory(prior.Elements, current.Elements, layout.ElementSlot);
        TryEncodePlan(prior, current, layout, ranges, null, out PreparedDeltaBody? result);
        observation = new(ListDeltaCompetitionOutcome.ExplicitPlan, result!.Body.Length, 0, 0);
        return result!;
    }

    // Encodes a changed pair. A finite limit accepts only a strictly shorter complete body;
    // it bounds actual output at element-call boundaries, not child allocations or GetSpan hints.
    internal static bool TryEncodePlan(FrozenListState<TState> prior, FrozenListState<TState> current,
        ListLayout layout, IReadOnlyList<ListDeltaRange> ranges, int? exclusiveByteLimit, out PreparedDeltaBody? result) =>
        TryEncodePlan(prior, current, layout, ranges, exclusiveByteLimit, out result, out _);

    internal static bool TryEncodePlan(FrozenListState<TState> prior, FrozenListState<TState> current,
        ListLayout layout, IReadOnlyList<ListDeltaRange> ranges, int? exclusiveByteLimit,
        out PreparedDeltaBody? result, out int writtenBytes) {
        if (exclusiveByteLimit.HasValue) { ArgumentOutOfRangeException.ThrowIfNegative(exclusiveByteLimit.Value); }
        result = null;
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        writer.WriteVarUInt32((uint)current.Count);
        if (LimitReached(buffer, exclusiveByteLimit, out writtenBytes)) { return false; }
        foreach (ListDeltaRange range in ranges) {
            if (range.OldStart < 0) {
                if (!TryWriteNewRange(ref writer, buffer, current.Elements.Slice(range.NewStart, range.Count),
                        layout, exclusiveByteLimit, out writtenBytes)) { return false; }
                continue;
            }
            bool rangeStarted = false;
            for (int index = 0; index < range.Count; index++) {
                ref readonly TState oldValue = ref prior.Elements[range.OldStart + index];
                ref readonly TState newValue = ref current.Elements[range.NewStart + index];
                if (TOps.StateEquals(in oldValue, in newValue, layout.ElementSlot)) { continue; }
                if (!rangeStarted) {
                    WriteSourceRange(ref writer, CopyAndPatch, range.OldStart, range.Count);
                    if (LimitReached(buffer, exclusiveByteLimit, out writtenBytes)) { return false; }
                    rangeStarted = true;
                }
                writer.WriteVarUInt32((uint)index + 1);
                if (LimitReached(buffer, exclusiveByteLimit, out writtenBytes)) { return false; }
                PreparedDeltaBody delta = TOps.PrepareDelta(in oldValue, in newValue, layout.ElementSlot);
                if (!delta.HasChanges) {
                    throw new InvalidOperationException("List element StateEquals and PrepareDelta disagree about a changed value.");
                }
                writer.WriteRawBytes(delta.Body);
                if (LimitReached(buffer, exclusiveByteLimit, out writtenBytes)) { return false; }
            }
            if (rangeStarted) {
                writer.WriteVarUInt32(0);
            } else {
                WriteSourceRange(ref writer, Copy, range.OldStart, range.Count);
            }
            if (LimitReached(buffer, exclusiveByteLimit, out writtenBytes)) { return false; }
        }
        if (LimitReached(buffer, exclusiveByteLimit, out writtenBytes)) { return false; }
        result = new(true, buffer.WrittenSpan);
        return true;
    }

    private static bool TryWriteNewRange(ref BareValueWriter writer, ArrayBufferWriter<byte> buffer,
        ReadOnlySpan<TState> elements, ListLayout layout, int? exclusiveByteLimit, out int writtenBytes) {
        writer.WriteByte(New);
        writer.WriteVarUInt32((uint)elements.Length);
        if (LimitReached(buffer, exclusiveByteLimit, out writtenBytes)) { return false; }
        foreach (ref readonly TState element in elements) {
            TOps.WriteBase(ref writer, in element, layout.ElementSlot);
            if (LimitReached(buffer, exclusiveByteLimit, out writtenBytes)) { return false; }
        }
        writtenBytes = buffer.WrittenCount;
        return true;
    }

    private static bool LimitReached(ArrayBufferWriter<byte> buffer, int? exclusiveByteLimit, out int writtenBytes) {
        writtenBytes = buffer.WrittenCount;
        return exclusiveByteLimit.HasValue && writtenBytes >= exclusiveByteLimit.Value;
    }

    internal static FrozenListState<TState> ApplyDelta(ref BareValueReader reader, FrozenListState<TState> prior, ListLayout layout) {
        int count = ReadCount(ref reader);
        TState[]? elements = null;
        int output = 0;
        while (output < count) {
            byte operation = reader.ReadByte();
            if (operation == New) {
                int length = ReadRangeLength(ref reader, count - output);
                // Source copies can represent many elements in a few bytes; only literals have this lower bound.
                RequireTailFits(length, ref reader, layout);
                elements ??= new TState[count];
                for (int index = 0; index < length; index++) {
                    elements[output + index] = TOps.ReadBase(ref reader, layout.ElementSlot);
                }
                output += length;
                continue;
            }
            if (operation is not (Copy or CopyAndPatch)) {
                throw new InvalidDataException("Unknown List Delta operation.");
            }
            uint source = reader.ReadVarUInt32();
            int rangeLength = ReadRangeLength(ref reader, count - output);
            if (source > (uint)prior.Count || rangeLength > prior.Count - (int)source) {
                throw new InvalidDataException("List Delta source range is outside the prior state.");
            }
            elements ??= new TState[count];
            prior.Elements.Slice((int)source, rangeLength).CopyTo(elements.AsSpan(output, rangeLength));
            if (operation == CopyAndPatch) {
                uint previous = 0;
                while (true) {
                    uint encoded = reader.ReadVarUInt32();
                    if (encoded == 0) {
                        if (previous == 0) { throw new InvalidDataException("A List CopyAndPatch operation must change an element."); }
                        break;
                    }
                    if (encoded <= previous || encoded > rangeLength) {
                        throw new InvalidDataException("List patch indexes must be strictly increasing and within their source range.");
                    }
                    int index = (int)encoded - 1;
                    elements[output + index] = TOps.ApplyDelta(ref reader, in prior.Elements[(int)source + index], layout.ElementSlot);
                    previous = encoded;
                }
            }
            // A final CopyAndPatch still consumes its terminating zero before completing the output.
            output += rangeLength;
        }
        return new(elements ?? [], takeOwnership: true);
    }

    private static int ReadRangeLength(ref BareValueReader reader, int remaining) {
        uint length = reader.ReadVarUInt32();
        if (length == 0 || length > remaining) {
            throw new InvalidDataException("List Delta range length must be positive and fit the remaining output.");
        }
        return (int)length;
    }

    private static void WriteSourceRange(ref BareValueWriter writer, byte operation, int start, int count) {
        writer.WriteByte(operation);
        writer.WriteVarUInt32((uint)start);
        writer.WriteVarUInt32((uint)count);
    }

    internal static void VisitReferences(FrozenListState<TState> state, IStateReferenceVisitor visitor, ListLayout layout) {
        foreach (ref readonly TState element in state.Elements) { TOps.VisitReferences(in element, visitor, layout.ElementSlot); }
    }

    private static int ReadCount(ref BareValueReader reader) {
        uint count = reader.ReadVarUInt32();
        if (count > Array.MaxLength) { throw new InvalidDataException("List count exceeds the supported CLR buffer length."); }
        return (int)count;
    }

    private static void RequireTailFits(int count, ref BareValueReader reader, ListLayout layout) {
        int minimum = StateBodySize.MinimumBaseBytes(layout.ElementSlot);
        if (minimum > 0 && count > reader.RemainingCount / minimum) {
            throw new InvalidDataException("List elements cannot fit in the remaining payload.");
        }
    }
}
