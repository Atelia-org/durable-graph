using Atelia.FrameStore;

namespace Atelia.DurableGraph.Storage;

// Two owned components at one address share one entry, budget and LRU.
// Concrete readers supply immutable values and their explicit resident charges.
internal sealed class OwnedFrameReadCache<TFrame, TMap> where TFrame : class where TMap : class {
    internal const long EntryOverheadBytes = 160;
    private readonly long _budgetBytes;
    private readonly Dictionary<FrameAddress, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _lru = [];

    internal OwnedFrameReadCache(long budgetBytes) {
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        _budgetBytes = budgetBytes;
    }

    internal long ResidentChargeBytes { get; private set; }
    internal long PeakResidentChargeBytes { get; private set; }
    internal long Evictions { get; private set; }
    internal long AdmissionBypasses { get; private set; }
    internal int EntryCount => _entries.Count;

    internal bool TryGetFrame(FrameAddress address, out TFrame frame) {
        if (_entries.TryGetValue(address, out var node) && node.Value.Frame is { } value) {
            Touch(node);
            frame = value;
            return true;
        }
        frame = null!;
        return false;
    }

    internal bool TryGetMap(FrameAddress address, out TMap map) {
        if (_entries.TryGetValue(address, out var node) && node.Value.Map is { } value) {
            Touch(node);
            map = value;
            return true;
        }
        map = null!;
        return false;
    }

    internal void AdmitFrame(FrameAddress address, TFrame frame, long componentCharge) {
        ArgumentNullException.ThrowIfNull(frame);
        Admit(address, frame, null, componentCharge);
    }

    internal void AdmitMap(FrameAddress address, TMap map, long componentCharge) {
        ArgumentNullException.ThrowIfNull(map);
        Admit(address, null, map, componentCharge);
    }

    private void Admit(FrameAddress address, TFrame? frame, TMap? map, long componentCharge) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(componentCharge);
        // Ancestor reads during materialization can evict the root: look it up again.
        _entries.TryGetValue(address, out var existing);
        if (existing is not null && (frame is not null ? existing.Value.Frame is not null : existing.Value.Map is not null)) {
            Touch(existing);
            return;
        }
        long totalCharge = checked((existing?.Value.Charge ?? EntryOverheadBytes) + componentCharge);
        if (totalCharge > _budgetBytes) {
            AdmissionBypasses++;
            return;
        }
        long increment = totalCharge - (existing?.Value.Charge ?? 0);
        while (ResidentChargeBytes > _budgetBytes - increment) {
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
        } else { Touch(existing); }
        if (frame is not null) { existing.Value.Frame = frame; }
        if (map is not null) { existing.Value.Map = map; }
        existing.Value.Charge = totalCharge;
        ResidentChargeBytes += increment;
        PeakResidentChargeBytes = Math.Max(PeakResidentChargeBytes, ResidentChargeBytes);
    }

    private void Touch(LinkedListNode<Entry> node) {
        if (node == _lru.Last) { return; }
        _lru.Remove(node);
        _lru.AddLast(node);
    }

    private sealed class Entry(FrameAddress address) {
        internal FrameAddress Address { get; } = address;
        internal TFrame? Frame;
        internal TMap? Map;
        internal long Charge;
    }
}
