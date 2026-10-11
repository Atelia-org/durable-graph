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
public interface IStateOps<TState> where TState : unmanaged {
    /// <summary>Compares persistent values in the same exact slot, without encoding or allocating.</summary>
    static abstract bool StateEquals(in TState left, in TState right, DurableFieldInfo slot);
    static abstract void WriteBase(ref BareValueWriter writer, in TState state, DurableFieldInfo slot);
    static abstract TState ReadBase(ref BareValueReader reader, DurableFieldInfo slot);
    static abstract PreparedDeltaBody PrepareDelta(in TState prior, in TState current, DurableFieldInfo slot);
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
        StateBindingContext.WithFieldId(Slot, fieldId), StateType, StateOpsType, DomainType, ProjectionType);
}
