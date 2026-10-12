using System.Runtime.CompilerServices;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Storage;

// One address index, one LRU, one budget. Values own their data; eviction only drops references.
internal sealed class StateRevisionReadCache(long budgetBytes) {
    internal const long EntryOverheadBytes = OwnedFrameReadCache<StateRevision, FrozenSortedDictionary<uint, FrameAddress>>.EntryOverheadBytes;
    private readonly OwnedFrameReadCache<StateRevision, FrozenSortedDictionary<uint, FrameAddress>> _cache = new(budgetBytes);

    internal long ResidentChargeBytes => _cache.ResidentChargeBytes;
    internal long PeakResidentChargeBytes => _cache.PeakResidentChargeBytes;
    internal long Evictions => _cache.Evictions;
    internal long AdmissionBypasses => _cache.AdmissionBypasses;
    internal int EntryCount => _cache.EntryCount;

    internal bool TryGetRevision(FrameAddress address, out StateRevision revision) => _cache.TryGetFrame(address, out revision);
    internal bool TryGetHeads(FrameAddress address, out FrozenSortedDictionary<uint, FrameAddress> heads) => _cache.TryGetMap(address, out heads);

    internal void AdmitRevision(FrameAddress address, StateRevision revision) =>
        _cache.AdmitFrame(address, revision, GetRevisionCharge(revision));

    internal void AdmitHeads(FrameAddress address, FrozenSortedDictionary<uint, FrameAddress> heads) =>
        _cache.AdmitMap(address, heads, GetMapCharge(heads.Count));

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
