using Atelia.Binary;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Runtime;

/// <summary>Projects an optional domain value through its typed child projection.</summary>
public readonly struct NullableValueProjection<TDomain, TState, TProjection> : IValueProjection<TDomain?, NullableState<TState>>
    where TDomain : struct where TState : unmanaged where TProjection : IValueProjection<TDomain, TState> {
    public static NullableState<TState> Capture(in TDomain? value, CaptureContext context, DurableFieldInfo slot) {
        DurableFieldInfo child = NullableStateSlot.Child(slot);
        if (!value.HasValue) { return default; }
        TDomain domain = value.GetValueOrDefault();
        return new(TProjection.Capture(in domain, context, child));
    }

    public static void Hydrate(ref TDomain? target, in NullableState<TState> state, ObjectReadTable objects, DurableFieldInfo slot) {
        DurableFieldInfo child = NullableStateSlot.Child(slot);
        if (!state.HasValue) { target = null; return; }
        TDomain value = default;
        TState childState = state.Value;
        TProjection.Hydrate(ref value, in childState, objects, child);
        target = value;
    }
}

/// <summary>Static operations for one optional exact child layout.</summary>
public readonly struct NullableStateOps<TState, TOps> : IStateOps<NullableState<TState>>
    where TState : unmanaged where TOps : IStateOps<TState> {
    public static bool StateEquals(in NullableState<TState> left, in NullableState<TState> right, DurableFieldInfo slot) {
        DurableFieldInfo child = NullableStateSlot.Child(slot);
        if (left.HasValue != right.HasValue) { return false; }
        if (!left.HasValue) { return true; }
        TState a = left.Value;
        TState b = right.Value;
        return TOps.StateEquals(in a, in b, child);
    }

    public static void WriteBase(ref BareValueWriter writer, in NullableState<TState> state, DurableFieldInfo slot) {
        DurableFieldInfo child = NullableStateSlot.Child(slot);
        writer.WriteByte(state.HasValue ? (byte)1 : (byte)0);
        if (state.HasValue) {
            TState value = state.Value;
            TOps.WriteBase(ref writer, in value, child);
        }
    }

    public static NullableState<TState> ReadBase(ref BareValueReader reader, DurableFieldInfo slot) {
        DurableFieldInfo child = NullableStateSlot.Child(slot);
        return reader.ReadByte() switch {
            0 => default,
            1 => new(TOps.ReadBase(ref reader, child)),
            _ => throw new InvalidDataException("Invalid Nullable Base presence marker."),
        };
    }

    public static PreparedDeltaBody PrepareDelta(in NullableState<TState> prior, in NullableState<TState> current, DurableFieldInfo slot) {
        DurableFieldInfo child = NullableStateSlot.Child(slot);
        if (!current.HasValue) { return prior.HasValue ? new(true, [0]) : new(false, []); }

        TState value = current.Value;
        PreparedDeltaBody? patch = null;
        if (prior.HasValue) {
            TState before = prior.Value;
            patch = TOps.PrepareDelta(in before, in value, child);
            if (!patch.HasChanges) { return new(false, []); }
        }
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        writer.WriteByte(prior.HasValue ? (byte)2 : (byte)1);
        if (patch is null) { TOps.WriteBase(ref writer, in value, child); }
        else { writer.WriteRawBytes(patch.Body); }
        return new(true, buffer.WrittenSpan);
    }

    public static NullableState<TState> ApplyDelta(ref BareValueReader reader, in NullableState<TState> prior, DurableFieldInfo slot) {
        DurableFieldInfo child = NullableStateSlot.Child(slot);
        byte operation = reader.ReadByte();
        if (operation == 0 && prior.HasValue) { return default; }
        if (operation == 1 && !prior.HasValue) { return new(TOps.ReadBase(ref reader, child)); }
        if (operation == 2 && prior.HasValue) {
            TState before = prior.Value;
            TState value = TOps.ApplyDelta(ref reader, in before, child);
            if (TOps.StateEquals(in before, in value, child)) {
                throw new InvalidDataException("A Nullable Patch must change its child value.");
            }
            return new(value);
        }
        throw new InvalidDataException("Invalid Nullable Delta operation or prior presence.");
    }

    public static void VisitReferences(in NullableState<TState> state, IStateReferenceVisitor visitor, DurableFieldInfo slot) {
        DurableFieldInfo child = NullableStateSlot.Child(slot);
        if (state.HasValue) {
            TState value = state.Value;
            TOps.VisitReferences(in value, visitor, child);
        }
    }
}

internal static class NullableStateSlot {
    internal static DurableFieldInfo Child(DurableFieldInfo slot) {
        if (slot.TypeTag != TypeTag.Nullable || slot.NullableLayout is null) {
            throw new ArgumentException("Nullable operations require an exact Nullable slot.", nameof(slot));
        }
        return slot.NullableLayout.ElementSlot;
    }
}
