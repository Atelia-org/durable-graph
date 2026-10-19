using Atelia.FrameStore;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Storage;


namespace Atelia.DurableGraph.Persistence;

/// <summary>One successfully restored State's owned preparation and exact Schema certificate.</summary>
/// <remarks>
/// Only the repository slot retains the certificate and optional owned read-only leaf table;
/// editable baselines retain normalized source alone. Collected distinguishes no eligible leaves
/// from a slot prepared before the leaf experiment was enabled, without allocating an empty table.
/// </remarks>
internal sealed record PreparedStateRestoration(HistoryGraphRecord StateCheckpoint,
    PreparedGraphSelection Selection, StateBindingContext.ExactSchemaRequirementSet Requirements,
    IReadOnlyDictionary<ObjectId, object>? ImmutableLeaves = null, bool ImmutableLeavesCollected = false,
    DecodedRevision.SchemaViewBindingEnvironment? BindingEnvironment = null) {
    internal FrameAddress RevisionAddress => Selection.Normalized.RevisionAddress;
    internal ObjectId RootId => Selection.RootId;
    internal int SchemaRequirementCount => Requirements.Count;
    internal int ImmutableLeafCount => ImmutableLeaves?.Count ?? 0;

    internal bool Matches(HistoryGraphRecord state) => StateCheckpoint.Id == state.Id &&
        RevisionAddress == state.Address && RootId == state.RootId;

    internal void ValidateStoredSource(GraphRecordStore store, StateModelRegistry.FrozenConfiguration configuration,
        HistoryGraphRecord state, FrameAddress? expectedSource, GraphReadContext context) {
        if (state.Kind != CommitKind.State || !Matches(state) || BindingEnvironment is not { } environment ||
            !ReferenceEquals(Selection.Models, environment.Models) || !ReferenceEquals(context.Models, environment.Models)) {
            throw new InvalidDataException("Prepared State must retain its exact split model environment and selection.");
        }
        GraphEnvelope metadata = store.ReadMetadata(state.Address);
        store.ValidateBusinessMetadata(state.Address, expectedSource);
        if (metadata.Kind != StoredGraphKind.State || metadata.RootId != RootId || metadata.SchemaRoot is not { } schemaRoot) {
            throw new InvalidDataException("Prepared State must select its actual State root and Schema pin.");
        }
        environment.RequireMatches(store, configuration, schemaRoot);
        Selection.Normalized.RequireStoredSource(store, context.Models);
        ValidateRoot(context.Models);
        Requirements.Validate(context.Models);
    }

    private void ValidateRoot(ModelExecutionEnvironment models) {
        if (!Selection.Normalized.Objects.TryGetValue(RootId, out NormalizedObject? root) ||
            root.Model is not StateModelBinding rootModel || !ReferenceEquals(rootModel, Selection.RootModel) ||
            rootModel.DomainType.IsAbstract || !typeof(IDurableObject).IsAssignableFrom(rootModel.DomainType) ||
            !models.TryGetCurrentModel(rootModel.DomainType, out StateModelBinding? current) ||
            !ReferenceEquals(current, rootModel) || !root.Current.Layout.Equals(rootModel.CurrentLayout)) {
            throw new InvalidDataException("Prepared State root must retain its exact current durable binding.");
        }
    }
}
