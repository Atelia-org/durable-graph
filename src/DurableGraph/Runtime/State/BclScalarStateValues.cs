using Atelia.Binary;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Runtime;

/// <summary>Static Guid operations for its complete built-in state representation.</summary>
public readonly struct GuidStateOps : IStateOps<Guid> {
    public static bool StateEquals(in Guid left, in Guid right, DurableFieldInfo slot) => left == right;
    public static void WriteBase(ref BareValueWriter writer, in Guid state, DurableFieldInfo slot) => BclValueCodec.WriteGuid(ref writer, state);
    public static Guid ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => BclValueCodec.ReadGuid(ref reader);
    public static PreparedDeltaBody PrepareDelta(in Guid prior, in Guid current, DurableFieldInfo slot) =>
        ScalarDelta.Prepare<Guid, GuidStateOps>(in current, slot, StateEquals(in prior, in current, slot));
    public static Guid ApplyDelta(ref BareValueReader reader, in Guid prior, DurableFieldInfo slot) {
        Guid current = ReadBase(ref reader, slot);
        ScalarDelta.RequireChange(StateEquals(in prior, in current, slot));
        return current;
    }
    public static void VisitReferences(in Guid state, IStateReferenceVisitor visitor, DurableFieldInfo slot) { }
}

/// <summary>Static Decimal operations for its complete built-in state representation.</summary>
public readonly struct DecimalStateOps : IStateOps<decimal> {
    public static bool StateEquals(in decimal left, in decimal right, DurableFieldInfo slot) => ScalarStateEquality.DecimalEquals(in left, in right);
    public static void WriteBase(ref BareValueWriter writer, in decimal state, DurableFieldInfo slot) => BclValueCodec.WriteDecimal(ref writer, state);
    public static decimal ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => BclValueCodec.ReadDecimal(ref reader);
    public static PreparedDeltaBody PrepareDelta(in decimal prior, in decimal current, DurableFieldInfo slot) =>
        ScalarDelta.Prepare<decimal, DecimalStateOps>(in current, slot, StateEquals(in prior, in current, slot));
    public static decimal ApplyDelta(ref BareValueReader reader, in decimal prior, DurableFieldInfo slot) {
        decimal current = ReadBase(ref reader, slot);
        ScalarDelta.RequireChange(StateEquals(in prior, in current, slot));
        return current;
    }
    public static void VisitReferences(in decimal state, IStateReferenceVisitor visitor, DurableFieldInfo slot) { }
}

/// <summary>Static TimeSpan operations for its complete built-in state representation.</summary>
public readonly struct TimeSpanStateOps : IStateOps<TimeSpan> {
    public static bool StateEquals(in TimeSpan left, in TimeSpan right, DurableFieldInfo slot) => left.Ticks == right.Ticks;
    public static void WriteBase(ref BareValueWriter writer, in TimeSpan state, DurableFieldInfo slot) => BclValueCodec.WriteTimeSpan(ref writer, state);
    public static TimeSpan ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => BclValueCodec.ReadTimeSpan(ref reader);
    public static PreparedDeltaBody PrepareDelta(in TimeSpan prior, in TimeSpan current, DurableFieldInfo slot) =>
        ScalarDelta.Prepare<TimeSpan, TimeSpanStateOps>(in current, slot, StateEquals(in prior, in current, slot));
    public static TimeSpan ApplyDelta(ref BareValueReader reader, in TimeSpan prior, DurableFieldInfo slot) {
        TimeSpan current = ReadBase(ref reader, slot);
        ScalarDelta.RequireChange(StateEquals(in prior, in current, slot));
        return current;
    }
    public static void VisitReferences(in TimeSpan state, IStateReferenceVisitor visitor, DurableFieldInfo slot) { }
}
