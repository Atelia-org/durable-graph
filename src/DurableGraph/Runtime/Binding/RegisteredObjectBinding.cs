using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Runtime;

// Registered nominal objects share graph identity, deferred Base encoding and Delta planning.
internal abstract class RegisteredObjectBinding : ObjectBinding, IStatePreparationBinding {
    protected RegisteredObjectBinding(Type domain, DurableSchema schema) : base(domain, ObjectLayout.ForDurable(schema)) =>
        Requirements = StateBindingContext.ExactSchemaRequirementSet.Create((schema, "registered object"));

    internal StateBindingContext.ExactSchemaRequirementSet Requirements { get; }
    internal abstract object Registration { get; }
    internal virtual IReadOnlyList<StateReaderBinding> Readers => [];
    internal abstract long? Estimate(ObjectStateRecord state);
    public abstract void Validate(ObjectStateRecord state);
    public abstract PreparedBaseBody PrepareBase(ObjectStateRecord state);
    internal abstract PreparedDeltaBody? TryPrepareDelta(ObjectStateRecord previous, ObjectStateRecord current);
    PreparedDeltaBody IStatePreparationBinding.PrepareDelta(ObjectStateRecord previous, ObjectStateRecord current) =>
        TryPrepareDelta(previous, current) ?? throw new InvalidOperationException("This object pair requires a Base.");
}

internal abstract class RegisteredReaderBinding(DurableSchema schema) : StateReaderBinding(schema) {
    internal StateBindingContext.ExactSchemaRequirementSet Requirements { get; } =
        StateBindingContext.ExactSchemaRequirementSet.Create((schema, "registered object reader"));
}
