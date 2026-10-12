using Atelia.FrameStore;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Storage;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;

namespace Atelia.DurableGraph.Persistence;

/// <summary>One successfully restored State's owned preparation and exact Schema certificate.</summary>
/// <remarks>
/// Only the repository slot retains the certificate and optional owned read-only leaf table;
/// editable baselines retain normalized source alone. Collected distinguishes no eligible leaves
/// from a slot prepared before the leaf experiment was enabled, without allocating an empty table.
/// </remarks>
internal sealed record PreparedStateRestoration(CheckpointAddress StateCheckpoint,
    PreparedGraphSelection Selection, StateBindingContext.ExactSchemaRequirementSet Requirements,
    IReadOnlyDictionary<ObjectId, object>? ImmutableLeaves = null, bool ImmutableLeavesCollected = false,
    DecodedRevision.SplitBindingEnvironment? BindingEnvironment = null) {
    internal FrameAddress RevisionAddress => Selection.Normalized.RevisionAddress;
    internal ObjectId RootId => Selection.RootId;
    internal int SchemaRequirementCount => Requirements.Count;
    internal int ImmutableLeafCount => ImmutableLeaves?.Count ?? 0;

    internal bool Matches(CheckpointAddress state) => StateCheckpoint == state &&
        RevisionAddress == state.RevisionAddress && RootId == state.RootId;

    internal void ValidateSplit(P0GraphStore store, StateModelRegistry.FrozenConfiguration configuration,
        CheckpointAddress state, GraphReadContext context) {
        if (state.Kind != GraphFrameKind.State || !Matches(state) || BindingEnvironment is not { } environment ||
            !ReferenceEquals(Selection.Models, environment.Models) || !ReferenceEquals(context.Models, environment.Models)) {
            throw new InvalidDataException("Prepared State must retain its exact split model environment and selection.");
        }
        P0GraphMetadata metadata = store.ReadMetadata(state.Address);
        if (metadata.Kind != P0GraphKind.State || metadata.RootId != RootId || metadata.SchemaRoot is not { } schemaRoot) {
            throw new InvalidDataException("Prepared State must select its actual State root and Schema pin.");
        }
        environment.RequireMatches(store, configuration, schemaRoot);
        Selection.Normalized.RequireSplitSource(store, context.Models);
        ValidateRoot(context.Models);
        Requirements.Validate(context.Models);
    }

    private void ValidateRoot(StateModelSnapshot models) {
        if (!Selection.Normalized.Objects.TryGetValue(RootId, out NormalizedObject? root) ||
            root.Model is not StateModelBinding rootModel || !ReferenceEquals(rootModel, Selection.RootModel) ||
            rootModel.DomainType.IsAbstract || !typeof(IDurableObject).IsAssignableFrom(rootModel.DomainType) ||
            !models.TryGetCurrentModel(rootModel.DomainType, out StateModelBinding? current) ||
            !ReferenceEquals(current, rootModel) || !root.Current.Layout.Equals(rootModel.CurrentLayout)) {
            throw new InvalidDataException("Prepared State root must retain its exact current durable binding.");
        }
    }
}
