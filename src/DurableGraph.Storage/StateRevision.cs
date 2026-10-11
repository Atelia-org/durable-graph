using Atelia.FrameStore;

namespace Atelia.DurableGraph.Storage;

/// <summary>
/// Immutable local object-version records and ObjectHeadMap metadata of one StateRevision.
/// </summary>
public sealed class StateRevision {
    private readonly FrozenList<ObjectVersionRecord> _localObjects;
    private readonly FrozenList<uint> _localObjectIds;
    private readonly FrozenSortedDictionary<uint, FrameAddress> _externalObjectHeads;
    private readonly FrozenList<uint> _removedObjectIds;

    private StateRevision(
        FrameAddress? parentRevisionAddress,
        ObjectHeadMapKind objectHeadMapKind,
        IEnumerable<ObjectVersionRecord> localObjects,
        IEnumerable<KeyValuePair<uint, FrameAddress>> externalObjectHeads,
        IEnumerable<uint> removedObjectIds) {
        ArgumentNullException.ThrowIfNull(localObjects);
        ArgumentNullException.ThrowIfNull(externalObjectHeads);
        ArgumentNullException.ThrowIfNull(removedObjectIds);
        if (parentRevisionAddress is { } parent) {
            FrameAddressValidator.ValidateRequired(parent, nameof(parentRevisionAddress));
        }

        ObjectVersionRecord[] frozenObjects = localObjects.ToArray();
        foreach (ObjectVersionRecord record in frozenObjects) {
            if (record is null) {
                throw new ArgumentException("A local object record cannot be null.", nameof(localObjects));
            }
        }

        if (parentRevisionAddress is null && frozenObjects.Any(static record => record.Kind == ObjectVersionKind.Delta)) {
            throw new ArgumentException("A local object Delta requires an exact parent Revision address.", nameof(parentRevisionAddress));
        }

        Array.Sort(frozenObjects, static (left, right) => left.ObjectId.CompareTo(right.ObjectId));
        uint[] frozenLocalIds = FreezeObjectIds(frozenObjects.Select(static item => item.ObjectId), nameof(localObjects));
        SortedDictionary<uint, FrameAddress> frozenExternalHeads = FreezeExternalHeads(externalObjectHeads);
        uint[] frozenRemovedIds = FreezeObjectIds(removedObjectIds, nameof(removedObjectIds));

        switch (objectHeadMapKind) {
            case ObjectHeadMapKind.Base:
                if (frozenRemovedIds.Length != 0) {
                    throw new ArgumentException("An ObjectHeadMap Base cannot contain removed ObjectIds.", nameof(removedObjectIds));
                }

                EnsureDisjoint(frozenLocalIds, frozenExternalHeads.Keys, "Local and external ObjectIds");
                break;
            case ObjectHeadMapKind.Delta:
                if (parentRevisionAddress is null) {
                    throw new ArgumentException("An ObjectHeadMap Delta requires an exact parent Revision address.", nameof(parentRevisionAddress));
                }

                if (frozenExternalHeads.Count != 0) {
                    throw new ArgumentException("An ObjectHeadMap Delta cannot contain external Object heads.", nameof(externalObjectHeads));
                }

                EnsureDisjoint(frozenLocalIds, frozenRemovedIds, "Local and removed ObjectIds");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(objectHeadMapKind), objectHeadMapKind, "Unknown ObjectHeadMap kind.");
        }

        ParentRevisionAddress = parentRevisionAddress;
        ObjectHeadMapKind = objectHeadMapKind;
        _localObjects = new(frozenObjects);
        _localObjectIds = new(frozenLocalIds);
        _externalObjectHeads = new(frozenExternalHeads);
        _removedObjectIds = new(frozenRemovedIds);
    }

    public FrameAddress? ParentRevisionAddress { get; }
    public ObjectHeadMapKind ObjectHeadMapKind { get; }

    /// <summary>All object records located in the containing Revision Frame, in ascending ObjectId order.</summary>
    public IReadOnlyList<ObjectVersionRecord> LocalObjects => _localObjects;

    /// <summary>IDs derived from the local object records. Each head is its containing Revision Frame.</summary>
    public IReadOnlyList<uint> LocalObjectIds => _localObjectIds;

    /// <summary>Completed object heads completing an ObjectHeadMap Base; empty for an ObjectHeadMap Delta.</summary>
    public IReadOnlyDictionary<uint, FrameAddress> ExternalObjectHeads => _externalObjectHeads;

    /// <summary>IDs removed by a map Delta; a map Base expresses non-live objects by absence.</summary>
    public IReadOnlyList<uint> RemovedObjectIds => _removedObjectIds;

    /// <summary>
    /// Creates a revision with an ObjectHeadMap Base representation. This does not
    /// constrain the Base or Delta representation of its local object records.
    /// </summary>
    public static StateRevision CreateObjectHeadMapBase(
        FrameAddress? parentRevisionAddress,
        IEnumerable<ObjectVersionRecord> localObjects,
        IEnumerable<KeyValuePair<uint, FrameAddress>> externalObjectHeads) =>
        new(parentRevisionAddress, ObjectHeadMapKind.Base, localObjects, externalObjectHeads, []);

    /// <summary>
    /// Creates a revision with an ObjectHeadMap Delta representation. This does not
    /// constrain the Base or Delta representation of its local object records.
    /// </summary>
    public static StateRevision CreateObjectHeadMapDelta(
        FrameAddress parentRevisionAddress,
        IEnumerable<ObjectVersionRecord> localObjects,
        IEnumerable<uint> removedObjectIds) =>
        new(parentRevisionAddress, ObjectHeadMapKind.Delta, localObjects, [], removedObjectIds);

    private static uint[] FreezeObjectIds(IEnumerable<uint> objectIds, string parameterName) {
        uint[] frozen = objectIds.Order().ToArray();
        uint? previous = null;
        foreach (uint objectId in frozen) {
            if (objectId == 0) {
                throw new ArgumentOutOfRangeException(parameterName, objectId, "ObjectId 0 is reserved.");
            }

            if (previous == objectId) {
                throw new ArgumentException($"ObjectId {objectId} occurs more than once.", parameterName);
            }

            previous = objectId;
        }

        return frozen;
    }

    private static SortedDictionary<uint, FrameAddress> FreezeExternalHeads(
        IEnumerable<KeyValuePair<uint, FrameAddress>> externalObjectHeads) {
        SortedDictionary<uint, FrameAddress> frozen = [];
        foreach ((uint objectId, FrameAddress address) in externalObjectHeads) {
            if (objectId == 0) {
                throw new ArgumentOutOfRangeException(nameof(externalObjectHeads), objectId, "ObjectId 0 is reserved.");
            }

            FrameAddressValidator.ValidateRequired(address, nameof(externalObjectHeads));
            if (!frozen.TryAdd(objectId, address)) {
                throw new ArgumentException($"ObjectId {objectId} occurs more than once in external heads.", nameof(externalObjectHeads));
            }
        }

        return frozen;
    }

    private static void EnsureDisjoint(IEnumerable<uint> left, IEnumerable<uint> right, string role) {
        HashSet<uint> seen = new(left);
        foreach (uint objectId in right) {
            if (!seen.Add(objectId)) {
                throw new ArgumentException($"{role} overlap at ObjectId {objectId}.");
            }
        }
    }

    // Only read interfaces are exposed; ICollection.SyncRoot must not reveal backing storage.
    private sealed class FrozenList<T>(T[] items) : IReadOnlyList<T> {
        public int Count => items.Length;
        public T this[int index] => items[index];
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)items).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
