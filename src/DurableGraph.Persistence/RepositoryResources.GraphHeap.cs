using Atelia.HeadStore;

namespace Atelia.DurableGraph.Persistence;

public sealed partial class RepositoryResources {
    private readonly HeadId _heapHead;
    private static ReadOnlySpan<byte> EmptyHeapPayload => "P0HE"u8;

    // The empty entry is an internal control value, never a business State/Event.
    // Heap locators cannot enter the registered business branch/history APIs.
    internal HeadEntryId ReadHeapTip() {
        RequireStorageAvailable();
        HeadEntry entry = _heads.ReadHead(_heapHead).Unwrap();
        ValidateHeapEntry(entry, allowEmpty: true);
        return entry.Id;
    }

    internal HistoryGraphRecord ReadHeapDescriptor(HeadEntryId id) {
        RequireStorageAvailable();
        if (id.HeadId != _heapHead) { throw new ArgumentException("The reference must select this repository's graph heap.", nameof(id)); }
        return ValidateHeapEntry(_heads.ReadEntry(id).Unwrap(), allowEmpty: false)!;
    }

    private HistoryGraphRecord? ValidateHeapEntry(HeadEntry entry, bool allowEmpty) {
        if (entry.Id.HeadId != _heapHead) { throw new InvalidDataException("Wrong graph heap Head."); }
        if (entry.Payload.SequenceEqual(EmptyHeapPayload)) {
            if (!allowEmpty) { throw new InvalidDataException("The empty graph heap has no saved graph."); }
            return null;
        }
        var payload = BusinessEntryPayloadCodec.Decode(entry.Payload);
        GraphEnvelope metadata = GraphStore.ReadMetadata(payload.Graph);
        if (metadata.Kind != StoredGraphKind.State) { throw new InvalidDataException("A graph heap entry must select a State graph."); }
        return new(entry.Id, payload.Graph, CommitKind.State, metadata.RootId, payload.Summary);
    }

    // Save targets are either a registered business branch or the single internal
    // heap Head. Do not weaken RequireMember or ordinary descriptor/history reads.
    private HeadEntryId ReadSaveTarget(HeadId target) => target == _heapHead ? ReadHeapTip() : ReadCurrent(target).Id;
}
