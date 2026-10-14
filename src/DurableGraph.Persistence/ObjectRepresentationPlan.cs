namespace Atelia.DurableGraph.Persistence;

internal enum ObjectRepresentationMode {
    Base,
    Delta,
}

internal readonly record struct ObjectWriteDecision(
    ObjectId ObjectId,
    ObjectRepresentationMode Mode);

/// <summary>
/// Sparse object content writes, ordered by ObjectId, for the caller's frozen
/// post-live set. Omitted NoChange objects reuse their Parent-selected object heads.
/// This is neither a complete ObjectHeadMap nor an appendable Revision;
/// The persistence adapter supplies membership Removes. Changes to the
/// post-live set require a new plan.
/// </summary>
internal sealed class ObjectRepresentationPlan {
    internal ObjectRepresentationPlan(ObjectWriteDecision[] writes) {
        Writes = new FrozenList<ObjectWriteDecision>(writes);
    }

    internal IReadOnlyList<ObjectWriteDecision> Writes { get; }
}
