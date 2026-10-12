using System.Runtime.CompilerServices;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.MetaBootstrapP0;

internal sealed record P0DirectoryResult(FrozenSortedDictionary<uint, FrameAddress> Heads, long PayloadBytes);

// Fixed wire roles and complete directory results share the same address LRU.
internal sealed class P0GraphReadCache(long budgetBytes) {
    private sealed record CachedFrame(uint Tag, object Value);
    internal const long EntryOverheadBytes = OwnedFrameReadCache<CachedFrame, P0DirectoryResult>.EntryOverheadBytes;
    private readonly OwnedFrameReadCache<CachedFrame, P0DirectoryResult> _cache = new(budgetBytes);

    internal long ResidentChargeBytes => _cache.ResidentChargeBytes;
    internal long PeakResidentChargeBytes => _cache.PeakResidentChargeBytes;
    internal long Evictions => _cache.Evictions;
    internal long AdmissionBypasses => _cache.AdmissionBypasses;
    internal int EntryCount => _cache.EntryCount;

    internal bool TryGetGraph(FrameAddress address, out P0GraphMetadata graph) => TryGetFrame(address, P0GraphWire.GraphTag, out graph);
    internal bool TryGetBatch(FrameAddress address, out P0Batch batch) => TryGetFrame(address, P0GraphWire.BatchTag, out batch);
    internal bool TryGetDirectoryVersion(FrameAddress address, out P0DirectoryVersion directory) =>
        TryGetFrame(address, P0GraphWire.DirectoryTag, out directory);
    internal bool TryGetDirectory(FrameAddress address, out P0DirectoryResult directory) => _cache.TryGetMap(address, out directory);

    internal void AdmitGraph(FrameAddress address, P0GraphMetadata graph) =>
        _cache.AdmitFrame(address, new(P0GraphWire.GraphTag, graph), GetGraphCharge());
    internal void AdmitBatch(FrameAddress address, P0Batch batch) =>
        _cache.AdmitFrame(address, new(P0GraphWire.BatchTag, batch), GetBatchCharge(batch));
    internal void AdmitDirectoryVersion(FrameAddress address, P0DirectoryVersion directory) =>
        _cache.AdmitFrame(address, new(P0GraphWire.DirectoryTag, directory), GetDirectoryVersionCharge(directory));
    internal void AdmitDirectory(FrameAddress address, P0DirectoryResult directory) =>
        _cache.AdmitMap(address, directory, GetDirectoryCharge(directory.Heads.Count));

    private bool TryGetFrame<T>(FrameAddress address, uint tag, out T value) {
        if (_cache.TryGetFrame(address, out var cached)) {
            if (cached.Tag != tag || cached.Value is not T decoded) {
                throw new InvalidDataException("P0 address selects the wrong completed frame role.");
            }
            value = decoded;
            return true;
        }
        value = default!;
        return false;
    }

    // Conservative resident estimates, including wrappers and owned bodies, not total GC heap.
    internal static long GetGraphCharge() => 32 + Align(16L + Unsafe.SizeOf<P0GraphMetadata>());
    internal static long GetBatchCharge(P0Batch batch) {
        checked {
            long charge = 32 + Align(16L + 2L * Unsafe.SizeOf<FrameAddress?>() + IntPtr.Size)
                + 24 + ArrayCharge(batch.Records.Count, IntPtr.Size);
            foreach (ObjectVersionRecord row in batch.Records) { charge += 64 + ArrayCharge(row.Body.Length, sizeof(byte)); }
            return charge;
        }
    }
    internal static long GetDirectoryVersionCharge(P0DirectoryVersion directory) =>
        checked(32 + Align(16L + 2L * Unsafe.SizeOf<FrameAddress?>() + IntPtr.Size + sizeof(byte) + sizeof(int))
            + ArrayCharge(directory.Body.Length, sizeof(byte)));
    internal static long GetDirectoryCharge(int count) => checked(32 + StateRevisionReadCache.GetMapCharge(count));
    private static long ArrayCharge(int count, int width) => Align(checked(24L + (long)count * width));
    private static long Align(long bytes) => checked((bytes + 7) & ~7L);
}

// Physical diagnostics count actual decodes/replay, independently of typed restoration counters.
internal readonly record struct P0GraphReadCacheStatistics(
    long GraphHits, long GraphMisses, long GraphDecodes,
    long BatchHits, long BatchMisses, long BatchDecodes,
    long DirectoryVersionHits, long DirectoryVersionMisses, long DirectoryVersionDecodes,
    long DirectoryHits, long DirectoryMisses, long DirectoryMaterializations,
    long DirectoryReplayFrames, long DirectoryPayloadHits,
    long Evictions, long AdmissionBypasses, long ResidentChargeBytes, long PeakResidentChargeBytes, int EntryCount);
