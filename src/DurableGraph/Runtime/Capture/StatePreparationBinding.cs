using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Runtime;

/// <summary>Prepares an owned Base body from a frozen DTO.</summary>
public delegate PreparedBaseBody StateBasePreparer<TState>(in TState state) where TState : unmanaged;

/// <summary>Prepares one fused change decision and Delta body from frozen DTOs.</summary>
public delegate PreparedDeltaBody StateDeltaPreparer<TState>(in TState prior, in TState current) where TState : unmanaged;

/// <summary>Proves equality of all persistent members of two frozen DTOs, without encoding.</summary>
/// <remarks>True certifies complete persistent representations are equal, including reference ObjectIds;
/// false may conservatively mean no proof. This does not certify transitive instance sharing.
/// Do not consult domain instances or mutable external context. Errors propagate; they do not mean comparison is unavailable.</remarks>
public delegate bool StateEquality<TState>(in TState left, in TState right) where TState : unmanaged;

/// <summary>Pairs an exact Schema and DTO with its statically bound body operations.</summary>
/// <remarks>
/// Reuse one stable instance for a captured type. Callbacks must be deterministic and must
/// not retain mutable domain state. This binding supplies operations, not persistent type authority.
/// The optional equality callback supplies a sharing proof. Omitting it disables that proof,
/// not reading; comparison never falls back to the Base or Delta preparers.
/// </remarks>
public sealed class StatePreparationBinding<TState> : IStatePreparationBinding where TState : unmanaged {
    private readonly StateBasePreparer<TState> _prepareBase;
    private readonly StateDeltaPreparer<TState> _prepareDelta;
    private readonly StateEquality<TState>? _stateEquals;

    public StatePreparationBinding(
        DurableSchema schema,
        StateBasePreparer<TState> prepareBase,
        StateDeltaPreparer<TState> prepareDelta,
        StateEquality<TState>? stateEquals = null) {
        ArgumentNullException.ThrowIfNull(schema);
        schema.RequireReferenceObject();
        ArgumentNullException.ThrowIfNull(prepareBase);
        ArgumentNullException.ThrowIfNull(prepareDelta);
        Schema = schema;
        _prepareBase = prepareBase;
        _prepareDelta = prepareDelta;
        _stateEquals = stateEquals;
    }

    public DurableSchema Schema { get; }

    void IStatePreparationBinding.Validate(ObjectStateRecord item) {
        if (item.Kind != ObjectStateKind.Durable || !Schema.Equals(item.Schema)) {
            throw new InvalidOperationException("The preparation binding requires its exact durable Schema.");
        }
        _ = item.GetState<TState>();
    }

    bool IStatePreparationBinding.ProvesSameState(ObjectStateRecord left, ObjectStateRecord right) {
        ((IStatePreparationBinding)this).Validate(left);
        ((IStatePreparationBinding)this).Validate(right);
        if (_stateEquals is null) { return false; }
        TState leftState = left.GetState<TState>();
        TState rightState = right.GetState<TState>();
        return _stateEquals(in leftState, in rightState);
    }

    PreparedBaseBody IStatePreparationBinding.PrepareBase(ObjectStateRecord current) {
        TState state = current.GetState<TState>();
        return _prepareBase(in state) ?? throw new InvalidOperationException("Base preparation returned null.");
    }

    PreparedDeltaBody IStatePreparationBinding.PrepareDelta(ObjectStateRecord previous, ObjectStateRecord current) {
        TState prior = previous.GetState<TState>();
        TState state = current.GetState<TState>();
        return _prepareDelta(in prior, in state) ?? throw new InvalidOperationException("Delta preparation returned null.");
    }
}

internal interface IStatePreparationBinding {
    void Validate(ObjectStateRecord item);
    // False also covers a missing comparison capability. This proves state only, not identity or reference closure.
    bool ProvesSameState(ObjectStateRecord left, ObjectStateRecord right);
    PreparedBaseBody PrepareBase(ObjectStateRecord current);
    PreparedDeltaBody PrepareDelta(ObjectStateRecord previous, ObjectStateRecord current);
}
