using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence;

/// <summary>One successfully restored State's owned preparation and live Schema certificate.</summary>
/// <remarks>
/// Only the repository slot retains the certificate and optional owned read-only leaf table;
/// editable baselines retain normalized source alone. Collected distinguishes no eligible leaves
/// from a slot prepared before the leaf experiment was enabled, without allocating an empty table.
/// </remarks>
internal sealed record PreparedStateRestoration(CheckpointAddress StateCheckpoint,
    PreparedGraphSelection Selection, StateBindingContext.ExactSchemaRequirementSet Requirements,
    IReadOnlyDictionary<ObjectId, object>? ImmutableLeaves = null, bool ImmutableLeavesCollected = false) {
    internal FrameAddress RevisionAddress => Selection.Normalized.RevisionAddress;
    internal ObjectId RootId => Selection.RootId;
    internal int SchemaRequirementCount => Requirements.Count;
    internal int ImmutableLeafCount => ImmutableLeaves?.Count ?? 0;

    internal bool Matches(CheckpointAddress state) => StateCheckpoint == state &&
        RevisionAddress == state.RevisionAddress && RootId == state.RootId;

    internal void Validate(RevisionReadSession session, CheckpointAddress state) {
        if (state.Kind != GraphFrameKind.State || !Matches(state) ||
            !ReferenceEquals(Selection.Models, session.Models)) {
            throw new InvalidDataException("Prepared State does not match this exact selection and model environment.");
        }
        Selection.Normalized.RequireStorageSource(session.Store, session.Schemas);
        if (!Selection.Normalized.Objects.TryGetValue(RootId, out NormalizedObject? root) ||
            root.Model is not StateModelBinding rootModel || !ReferenceEquals(rootModel, Selection.RootModel) ||
            rootModel.DomainType.IsAbstract || !typeof(IDurableObject).IsAssignableFrom(rootModel.DomainType) ||
            !session.Models.TryGetCurrentModel(rootModel.DomainType, out StateModelBinding? current) ||
            !ReferenceEquals(current, rootModel) || !root.Current.Layout.Equals(rootModel.CurrentLayout)) {
            throw new InvalidDataException("Prepared State root must retain its exact current durable binding.");
        }
        Requirements.Validate(session.Models);
    }
}
