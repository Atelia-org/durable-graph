using Atelia.DurableGraph.Runtime;

namespace Atelia.DurableGraph.Persistence;

/// <summary>One operation's complete normalized source and validated reachable root selection.</summary>
/// <remarks>
/// Normalized retains every source member and its exact storage provenance, including rows
/// no longer reachable after Upgrade. This material owns no domain instances or editing session
/// and grants no permission to reuse preparation across operations by itself. Repository
/// reuse additionally requires a successfully delivered State and its exact Schema certificate.
/// </remarks>
internal sealed record PreparedGraphSelection(NormalizedRevision Normalized, ObjectId RootId,
    StateModelBinding RootModel, StateModelSnapshot Models, IReadOnlyList<ObjectId> Reachable);
