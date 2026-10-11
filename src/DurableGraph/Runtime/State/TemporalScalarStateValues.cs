using Atelia.Binary;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Runtime;

/// <summary>Static DateOnly operations for its complete day-number representation.</summary>
public readonly struct DateOnlyStateOps : IStateOps<DateOnly> {
    public static bool StateEquals(in DateOnly left, in DateOnly right, DurableFieldInfo slot) => left == right;
    public static void WriteBase(ref BareValueWriter writer, in DateOnly state, DurableFieldInfo slot) => BclValueCodec.WriteDateOnly(ref writer, state);
    public static DateOnly ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => BclValueCodec.ReadDateOnly(ref reader);
    public static PreparedDeltaBody PrepareDelta(in DateOnly prior, in DateOnly current, DurableFieldInfo slot) =>
        ScalarDelta.Prepare<DateOnly, DateOnlyStateOps>(in current, slot, StateEquals(in prior, in current, slot));
    public static DateOnly ApplyDelta(ref BareValueReader reader, in DateOnly prior, DurableFieldInfo slot) {
        DateOnly current = ReadBase(ref reader, slot);
        ScalarDelta.RequireChange(StateEquals(in prior, in current, slot));
        return current;
    }
    public static void VisitReferences(in DateOnly state, IStateReferenceVisitor visitor, DurableFieldInfo slot) { }
}

/// <summary>Static TimeOnly operations for its complete within-day tick representation.</summary>
public readonly struct TimeOnlyStateOps : IStateOps<TimeOnly> {
    public static bool StateEquals(in TimeOnly left, in TimeOnly right, DurableFieldInfo slot) => left == right;
    public static void WriteBase(ref BareValueWriter writer, in TimeOnly state, DurableFieldInfo slot) => BclValueCodec.WriteTimeOnly(ref writer, state);
    public static TimeOnly ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => BclValueCodec.ReadTimeOnly(ref reader);
    public static PreparedDeltaBody PrepareDelta(in TimeOnly prior, in TimeOnly current, DurableFieldInfo slot) =>
        ScalarDelta.Prepare<TimeOnly, TimeOnlyStateOps>(in current, slot, StateEquals(in prior, in current, slot));
    public static TimeOnly ApplyDelta(ref BareValueReader reader, in TimeOnly prior, DurableFieldInfo slot) {
        TimeOnly current = ReadBase(ref reader, slot);
        ScalarDelta.RequireChange(StateEquals(in prior, in current, slot));
        return current;
    }
    public static void VisitReferences(in TimeOnly state, IStateReferenceVisitor visitor, DurableFieldInfo slot) { }
}

/// <summary>Static DateTimeOffset operations preserving both clock ticks and UTC offset.</summary>
public readonly struct DateTimeOffsetStateOps : IStateOps<DateTimeOffset> {
    public static bool StateEquals(in DateTimeOffset left, in DateTimeOffset right, DurableFieldInfo slot) => left.EqualsExact(right);
    public static void WriteBase(ref BareValueWriter writer, in DateTimeOffset state, DurableFieldInfo slot) => BclValueCodec.WriteDateTimeOffset(ref writer, state);
    public static DateTimeOffset ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => BclValueCodec.ReadDateTimeOffset(ref reader);
    public static PreparedDeltaBody PrepareDelta(in DateTimeOffset prior, in DateTimeOffset current, DurableFieldInfo slot) =>
        ScalarDelta.Prepare<DateTimeOffset, DateTimeOffsetStateOps>(in current, slot, StateEquals(in prior, in current, slot));
    public static DateTimeOffset ApplyDelta(ref BareValueReader reader, in DateTimeOffset prior, DurableFieldInfo slot) {
        DateTimeOffset current = ReadBase(ref reader, slot);
        ScalarDelta.RequireChange(StateEquals(in prior, in current, slot));
        return current;
    }
    public static void VisitReferences(in DateTimeOffset state, IStateReferenceVisitor visitor, DurableFieldInfo slot) { }
}
