using System.Runtime.CompilerServices;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Storage;

// One address index, one LRU, one budget. Values own their data; eviction only drops references.
internal sealed class StateRevisionReadCache(long budgetBytes) {
    internal const long EntryOverheadBytes = 160; // Entry, linked-list node and amortized dictionary slot.
    private readonly Dictionary<FrameAddress, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _lru = [];

    internal long ResidentChargeBytes { get; private set; }
    internal long PeakResidentChargeBytes { get; private set; }
    internal long Evictions { get; private set; }
    internal long AdmissionBypasses { get; private set; }
    internal int EntryCount => _entries.Count;

    internal bool TryGetRevision(FrameAddress address, out StateRevision revision) {
        if (_entries.TryGetValue(address, out var node) && node.Value.Revision is { } value) {
            Touch(node);
            revision = value;
            return true;
        }
        revision = null!;
        return false;
    }

    internal bool TryGetHeads(FrameAddress address, out FrozenSortedDictionary<uint, FrameAddress> heads) {
        if (_entries.TryGetValue(address, out var node) && node.Value.Heads is { } value) {
            Touch(node);
            heads = value;
            return true;
        }
        heads = null!;
        return false;
    }

    internal void AdmitRevision(FrameAddress address, StateRevision revision) =>
        Admit(address, revision, null, GetRevisionCharge(revision));

    internal void AdmitHeads(FrameAddress address, FrozenSortedDictionary<uint, FrameAddress> heads) =>
        Admit(address, null, heads, GetMapCharge(heads.Count));

    private void Admit(
        FrameAddress address,
        StateRevision? revision,
        FrozenSortedDictionary<uint, FrameAddress>? heads,
        long componentCharge) {
        // Materialization may have evicted the root while reading ancestors: always look it up anew.
        _entries.TryGetValue(address, out var existing);
        if (existing is not null &&
            (revision is not null ? existing.Value.Revision is not null : existing.Value.Heads is not null)) {
            Touch(existing);
            return;
        }

        long totalCharge = checked((existing?.Value.Charge ?? EntryOverheadBytes) + componentCharge);
        if (totalCharge > budgetBytes) {
            // In particular, a combined oversize entry leaves both the old component and other hot entries alone.
            AdmissionBypasses++;
            return;
        }

        long increment = totalCharge - (existing?.Value.Charge ?? 0);
        while (ResidentChargeBytes > budgetBytes - increment) {
            LinkedListNode<Entry> victim = _lru.First!;
            if (victim == existing) { victim = victim.Next!; }
            _lru.Remove(victim);
            _entries.Remove(victim.Value.Address);
            ResidentChargeBytes -= victim.Value.Charge;
            Evictions++;
        }

        if (existing is null) {
            existing = _lru.AddLast(new Entry(address));
            _entries.Add(address, existing);
        } else {
            Touch(existing);
        }
        if (revision is not null) { existing.Value.Revision = revision; }
        if (heads is not null) { existing.Value.Heads = heads; }
        existing.Value.Charge = totalCharge;
        ResidentChargeBytes += increment;
        PeakResidentChargeBytes = Math.Max(PeakResidentChargeBytes, ResidentChargeBytes);
    }

    private void Touch(LinkedListNode<Entry> node) {
        if (node == _lru.Last) { return; }
        _lru.Remove(node);
        _lru.AddLast(node);
    }

    // Conservative 64-bit object/header/alignment estimates, not measured GC heap sizes.
    // Array element widths reflect the actual address representation. Builders, caller-held values,
    // dictionary capacity high-water and garbage awaiting collection are outside resident charge.
    internal static long GetMapCharge(int count) {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return checked(32 + ArrayCharge(count, sizeof(uint)) + ArrayCharge(count, Unsafe.SizeOf<FrameAddress>()));
    }

    internal static long GetRevisionCharge(StateRevision revision) {
        checked {
            long charge = 96 + 3 * 24 // Revision and three readonly list wrappers.
                + ArrayCharge(revision.LocalObjects.Count, IntPtr.Size)
                + ArrayCharge(revision.LocalObjectIds.Count, sizeof(uint))
                + ArrayCharge(revision.RemovedObjectIds.Count, sizeof(uint))
                + GetMapCharge(revision.ExternalObjectHeads.Count);
            foreach (ObjectVersionRecord record in revision.LocalObjects) {
                charge += 64 + ArrayCharge(record.Body.Length, sizeof(byte));
            }
            return charge;
        }
    }

    private static long ArrayCharge(int count, int elementBytes) =>
        checked((24L + (long)count * elementBytes + 7) & ~7L);

    private sealed class Entry(FrameAddress address) {
        internal FrameAddress Address { get; } = address;
        internal StateRevision? Revision;
        internal FrozenSortedDictionary<uint, FrameAddress>? Heads;
        internal long Charge;
    }
}

// Internal mechanism diagnostics; deliberately separate from typed GraphReadStatistics.
internal readonly record struct StateRevisionReadCacheStatistics(
    long RevisionHits,
    long RevisionMisses,
    long RevisionDecodes,
    long MapHits,
    long MapMisses,
    long MapMaterializations,
    long Evictions,
    long AdmissionBypasses,
    long ResidentChargeBytes,
    long PeakResidentChargeBytes,
    int EntryCount);
