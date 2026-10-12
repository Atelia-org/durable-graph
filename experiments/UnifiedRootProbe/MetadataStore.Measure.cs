namespace Atelia.UnifiedRootProbe;

internal static partial class MetadataStore {
    // FileStream buffering is disabled by callers. Count every requested read,
    // including a retry after a short read; empty spans perform no I/O.
    private static void ReadExactMeasured(Stream source, Span<byte> destination, Action<int>? charge = null) {
        while (!destination.IsEmpty) {
            int requested = destination.Length;
            charge?.Invoke(requested);
            int returned;
            try { returned = source.Read(destination); }
            catch {
                ProbeMeasurements.Current?.RecordRead(requested, 0);
                throw;
            }
            ProbeMeasurements.Current?.RecordRead(requested, returned);
            if (returned == 0) { throw new IOException("Unexpected EOF during fixed-range read."); }
            destination = destination[returned..];
        }
    }
}
