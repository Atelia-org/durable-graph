using Atelia.FrameStore;

namespace Atelia.DurableGraph.Storage;

/// <summary>One owned raw ObjectVersion and its exact containing Frame.</summary>
public sealed class ObjectVersionChainEntry {
    internal ObjectVersionChainEntry(FrameAddress address, ObjectVersionRecord record) {
        FrameAddressValidator.ValidateRequired(address, nameof(address));
        ArgumentNullException.ThrowIfNull(record);
        int payloadBytes = record.EncodedPayloadBytes
            ?? throw new InvalidOperationException("A chain entry requires a record read from its containing Frame.");
        if (payloadBytes <= 0) {
            throw new InvalidOperationException("An encoded ObjectVersion payload includes a nonempty envelope.");
        }

        ContainingRevisionAddress = address;
        Record = record;
        ObjectVersionPayloadBytes = payloadBytes;
    }

    public FrameAddress ContainingRevisionAddress { get; }
    public ObjectVersionRecord Record { get; }

    /// <summary>
    /// Observed encoded bytes from representation kind through body, including a
    /// Delta's prior locator. Excludes ObjectId, ObjectHeadMap membership, and
    /// shared Revision-Frame bytes.
    /// </summary>
    public int ObjectVersionPayloadBytes { get; }
}
