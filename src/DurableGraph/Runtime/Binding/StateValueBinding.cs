using Atelia.Binary;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Runtime;

/// <summary>Static current-domain projection for one supported closed value slot.</summary>
public interface IValueProjection<TDomain, TState> where TState : unmanaged {
    static abstract TState Capture(in TDomain value, CaptureContext context, DurableFieldInfo slot);
    static abstract void Hydrate(ref TDomain target, in TState state, ObjectReadTable objects, DurableFieldInfo slot);
}

/// <summary>Static operations for one exact frozen value representation, independent of a domain type.</summary>
/// <remarks>
/// Operations use the supplied complete slot and must preserve their frozen inputs. Equal inputs and
/// a fixed slot must have stable persistent results; invocation counts must not affect those results.
/// </remarks>
public interface IStateOps<TState> where TState : unmanaged {
    /// <summary>Compares persistent values in the same exact slot, without encoding or allocating.</summary>
    /// <remarks>Equality covers the complete persistent representation, including reference ObjectIds,
    /// and must agree with the inverse of <see cref="PreparedDeltaBody.HasChanges"/> returned by <c>PrepareDelta</c>.</remarks>
    static abstract bool StateEquals(in TState left, in TState right, DurableFieldInfo slot);
    static abstract void WriteBase(ref BareValueWriter writer, in TState state, DurableFieldInfo slot);
    static abstract TState ReadBase(ref BareValueReader reader, DurableFieldInfo slot);
    /// <summary>Prepares the change decision and owned Delta body without modifying either frozen input.</summary>
    /// <remarks><see cref="PreparedDeltaBody.HasChanges"/> must be false exactly when <c>StateEquals</c> is true.
    /// A no-change result is not a child patch to pass to <c>ApplyDelta</c>.</remarks>
    static abstract PreparedDeltaBody PrepareDelta(in TState prior, in TState current, DurableFieldInfo slot);
    /// <summary>Applies one changed child Delta without modifying the prior frozen value.</summary>
    /// <remarks>Reject a patch whose result is equal to prior under <c>StateEquals</c> with
    /// <see cref="InvalidDataException"/>. Array and List readers rely on this child-operation check.</remarks>
    static abstract TState ApplyDelta(ref BareValueReader reader, in TState prior, DurableFieldInfo slot);
    static abstract void VisitReferences(in TState state, IStateReferenceVisitor visitor, DurableFieldInfo slot);
}

/// <summary>Derived CLR execution types paired with the complete semantics of one value slot.</summary>
public sealed class StateValueBinding {
    public StateValueBinding(DurableFieldInfo slot, Type stateType, Type stateOpsType,
        Type? domainType = null, Type? projectionType = null) {
        if (slot.FieldId <= 0) { throw new ArgumentException("A value binding requires a valid slot.", nameof(slot)); }
        ArgumentNullException.ThrowIfNull(stateType);
        ArgumentNullException.ThrowIfNull(stateOpsType);
        if ((domainType is null) != (projectionType is null)) {
            throw new ArgumentException("Current projection requires both domain and projection types.");
        }
        Slot = slot;
        StateType = stateType;
        StateOpsType = stateOpsType;
        DomainType = domainType;
        ProjectionType = projectionType;
    }

    public DurableFieldInfo Slot { get; }
    public Type StateType { get; }
    public Type StateOpsType { get; }
    public Type? DomainType { get; }
    public Type? ProjectionType { get; }

    public StateValueBinding WithFieldId(int fieldId) => new(
        DurableFieldInfo.WithFieldId(Slot, fieldId), StateType, StateOpsType, DomainType, ProjectionType);
}
