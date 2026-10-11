using Atelia.FrameStore;

namespace Atelia.DurableGraph.Storage;

/// <summary>
/// An immutable raw reconstruction chain, ordered from Base to the requested head.
/// </summary>
/// <remarks>
/// Storage checks record locations and each Delta's agreement with its exact
/// Parent's declared head. This result does not authenticate a Schema, decode
/// bodies, validate graph references, or prove how a Delta was prepared.
/// </remarks>
public sealed class ObjectVersionChain {
    private readonly FrozenEntries _records;

    internal ObjectVersionChain(
        uint objectId,
        FrameAddress headAddress,
        IEnumerable<ObjectVersionChainEntry> records) {
        ArgumentOutOfRangeException.ThrowIfZero(objectId);
        FrameAddressValidator.ValidateRequired(headAddress, nameof(headAddress));
        ArgumentNullException.ThrowIfNull(records);
        ObjectVersionChainEntry[] frozen = records.ToArray();
        if (frozen.Length == 0) {
            throw new InvalidOperationException("An ObjectVersion chain requires a Base.");
        }

        long reconstructionBytes = 0;
        for (int index = 0; index < frozen.Length; index++) {
            ObjectVersionChainEntry entry = frozen[index];
            if (entry is null || entry.Record.ObjectId != objectId ||
                entry.Record.Kind != (index == 0 ? ObjectVersionKind.Base : ObjectVersionKind.Delta)) {
                throw new InvalidOperationException("An ObjectVersion chain must start with Base and contain only this ObjectId.");
            }

            if (index > 0 && entry.Record.PriorAddress != frozen[index - 1].ContainingRevisionAddress) {
                throw new InvalidOperationException("An ObjectVersion chain must follow exact prior addresses.");
            }

            reconstructionBytes = checked(reconstructionBytes + entry.ObjectVersionPayloadBytes);
        }

        if (frozen[^1].ContainingRevisionAddress != headAddress) {
            throw new InvalidOperationException("An ObjectVersion chain must end at the requested head.");
        }

        ObjectId = objectId;
        ObjectHeadAddress = headAddress;
        ReconstructionPayloadBytes = reconstructionBytes;
        _records = new(frozen);
    }

    public uint ObjectId { get; }
    public FrameAddress ObjectHeadAddress { get; }
    public IReadOnlyList<ObjectVersionChainEntry> Records => _records;

    /// <summary>
    /// Checked sum of each entry's actual ObjectVersion payload bytes (H).
    /// This is not total physical read traffic: ObjectHeadMap membership and
    /// shared Revision-Frame reads are excluded, including any repeated reads
    /// used to validate prior edges.
    /// </summary>
    public long ReconstructionPayloadBytes { get; }

    // Exposing only read interfaces also prevents ICollection.SyncRoot from
    // revealing the backing array.
    private sealed class FrozenEntries(ObjectVersionChainEntry[] entries) : IReadOnlyList<ObjectVersionChainEntry> {
        public int Count => entries.Length;
        public ObjectVersionChainEntry this[int index] => entries[index];
        public IEnumerator<ObjectVersionChainEntry> GetEnumerator() =>
            ((IEnumerable<ObjectVersionChainEntry>)entries).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
