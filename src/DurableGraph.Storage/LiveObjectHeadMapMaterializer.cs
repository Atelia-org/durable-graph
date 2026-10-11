using Atelia.FrameStore;

namespace Atelia.DurableGraph.Storage;

/// <summary>
/// Reconstructs the canonical live Object head map from a specified StateRevision
/// address.
/// </summary>
internal static class LiveObjectHeadMapMaterializer {
    internal static FrozenSortedDictionary<uint, FrameAddress> Materialize(
        FrameAddress head,
        Func<FrameAddress, StateRevision> readRevision) {
        ArgumentNullException.ThrowIfNull(readRevision);
        FrameAddressValidator.ValidateRequired(head, nameof(head));

        Stack<(FrameAddress Address, StateRevision Revision)> pendingDeltas = [];
        HashSet<FrameAddress> visited = [];
        FrameAddress address = head;
        FrameAddress baseAddress;
        StateRevision baseRevision;

        while (true) {
            if (!visited.Add(address)) {
                throw new InvalidDataException("The ObjectHeadMap Parent chain contains a cycle.");
            }
            StateRevision revision = readRevision(address)
                ?? throw new InvalidDataException(
                    $"State Revision reader returned null for {address}.");
            if (revision.ObjectHeadMapKind == ObjectHeadMapKind.Base) {
                baseAddress = address;
                baseRevision = revision;
                break;
            }

            pendingDeltas.Push((address, revision));
            address = revision.ParentRevisionAddress
                ?? throw new InvalidDataException(
                    $"ObjectHeadMap Delta at {address} has no parent Revision.");
        }

        SortedDictionary<uint, FrameAddress> heads = [];
        foreach (uint objectId in baseRevision.LocalObjectIds) {
            if (!heads.TryAdd(objectId, baseAddress)) {
                throw new InvalidDataException(
                    $"ObjectHeadMap Base repeats ObjectId {objectId}.");
            }
        }

        foreach ((uint objectId, FrameAddress externalHead) in
            baseRevision.ExternalObjectHeads) {
            if (!heads.TryAdd(objectId, externalHead)) {
                throw new InvalidDataException(
                    $"ObjectHeadMap Base repeats ObjectId {objectId}.");
            }
        }

        while (pendingDeltas.TryPop(
            out (FrameAddress Address, StateRevision Revision) pending)) {
            foreach (uint objectId in pending.Revision.RemovedObjectIds) {
                _ = heads.Remove(objectId);
            }

            foreach (uint objectId in pending.Revision.LocalObjectIds) {
                heads[objectId] = pending.Address;
            }
        }

        return new FrozenSortedDictionary<uint, FrameAddress>(heads);
    }
}
