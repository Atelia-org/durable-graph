using System.Diagnostics;
using Atelia.Rbf;

namespace Atelia.UnifiedRootProbe;

// Synchronous, thread-local experiment counters. RBF reads are measured by the
// storage source; these counters cover the probe's own direct FileStream calls.
internal static class ProbeMeasurements {
    [ThreadStatic]
    private static Scope? _current;

    internal static Scope? Current => _current;

    internal static ProbeMeasurementCheckpoint Capture() {
        Scope scope = _current ?? throw new InvalidOperationException("No active probe measurement.");
        RbfReadMetrics reads = RbfReadMetrics.Current ??
            throw new InvalidOperationException("No active RBF read measurement.");
        return new ProbeMeasurementCheckpoint(Stopwatch.GetTimestamp(),
            GC.GetAllocatedBytesForCurrentThread(), reads.Snapshot(), scope.Snapshot());
    }

    internal static ProbeMeasurement Difference(ProbeMeasurementCheckpoint start, ProbeMeasurementCheckpoint end) {
        RbfReadMetricsSnapshot a = start.RbfReads, b = end.RbfReads;
        ProbeIoSnapshot d = start.DirectIo, e = end.DirectIo;
        long ticks = end.Timestamp - start.Timestamp;
        if (ticks < 0) { throw new InvalidOperationException("Measurement checkpoints are out of order."); }
        return new ProbeMeasurement(ticks, ticks * 1000.0 / Stopwatch.Frequency,
            end.AllocatedBytes - start.AllocatedBytes,
            new RbfReadMetricsSnapshot(b.ReadCalls - a.ReadCalls,
                b.RequestedBytes - a.RequestedBytes, b.ReturnedBytes - a.ReturnedBytes,
                b.RawReadCalls - a.RawReadCalls, b.RawRequestedBytes - a.RawRequestedBytes,
                b.RawReturnedBytes - a.RawReturnedBytes,
                b.ReadAheadReturnedBytes - a.ReadAheadReturnedBytes,
                b.HeaderReadCalls - a.HeaderReadCalls),
            new ProbeIoSnapshot(e.ReadCalls - d.ReadCalls,
                e.RequestedBytes - d.RequestedBytes, e.ReturnedBytes - d.ReturnedBytes,
                e.ReadAheadReturnedBytes - d.ReadAheadReturnedBytes,
                e.WriteCalls - d.WriteCalls, e.WrittenBytes - d.WrittenBytes,
                e.FlushCalls - d.FlushCalls, e.FlushTicks - d.FlushTicks,
                e.FlushMilliseconds - d.FlushMilliseconds));
    }

    internal static (T Result, ProbeMeasurement Measurement) Measure<T>(Func<T> operation) {
        ArgumentNullException.ThrowIfNull(operation);
        using var scope = new Scope();
        using var rbf = RbfReadMetrics.Begin();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        T result = operation();
        long elapsedTicks = Stopwatch.GetTimestamp() - start;
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        return (result, new ProbeMeasurement(
            elapsedTicks,
            elapsedTicks * 1000.0 / Stopwatch.Frequency,
            allocatedBytes,
            rbf.Snapshot(),
            scope.Snapshot()));
    }

    internal sealed class Scope : IDisposable {
        private readonly Scope? _previous;
        private readonly int _threadId;
        private long _directReadCalls;
        private long _directRequestedBytes;
        private long _directReturnedBytes;
        private long _directReadAheadBytes;
        private long _writeCalls;
        private long _writtenBytes;
        private long _flushCalls;
        private long _flushTicks;
        private bool _disposed;

        internal Scope() {
            _previous = _current;
            _threadId = Environment.CurrentManagedThreadId;
            _current = this;
        }

        // Invoke only after the direct read completed. Count requested and actual
        // returned bytes separately; prefetch is the returned subset outside the
        // logical requested range and must never exceed returned bytes.
        internal void RecordRead(long requested, long returned, long readAhead = 0) {
            RecordReads(1, requested, returned, readAhead);
        }

        // For scanners which already have exact per-call counters, import the
        // aggregate once after the scanner returns; do not infer call count from
        // a total byte length.
        internal void RecordReads(long requests, long requested, long returned, long readAhead = 0) {
            RequireActive();
            if (requests < 0 || requested < 0 || returned < 0 || readAhead < 0 || readAhead > returned) {
                throw new ArgumentOutOfRangeException(nameof(requested));
            }
            for (Scope? scope = this; scope is not null; scope = scope._previous) {
                scope._directReadCalls += requests;
                scope._directRequestedBytes += requested;
                scope._directReturnedBytes += returned;
                scope._directReadAheadBytes += readAhead;
            }
        }

        // Invoke only after a successful probe-level append/file write. This is
        // logical frame bytes, not device I/O; failures have separate witnesses.
        internal void RecordWrite(long bytes) {
            RequireActive();
            ArgumentOutOfRangeException.ThrowIfNegative(bytes);
            for (Scope? scope = this; scope is not null; scope = scope._previous) {
                scope._writeCalls++;
                scope._writtenBytes += bytes;
            }
        }

        // elapsedTicks must surround the actual flush call, including its wait.
        internal void RecordFlush(long elapsedTicks) {
            RequireActive();
            ArgumentOutOfRangeException.ThrowIfNegative(elapsedTicks);
            for (Scope? scope = this; scope is not null; scope = scope._previous) {
                scope._flushCalls++;
                scope._flushTicks += elapsedTicks;
            }
        }

        internal ProbeIoSnapshot Snapshot() {
            RequireActive();
            return new ProbeIoSnapshot(_directReadCalls, _directRequestedBytes,
                _directReturnedBytes, _directReadAheadBytes, _writeCalls,
                _writtenBytes, _flushCalls, _flushTicks,
                _flushTicks * 1000.0 / Stopwatch.Frequency);
        }

        public void Dispose() {
            if (_disposed) { return; }
            RequireActive();
            _current = _previous;
            _disposed = true;
        }

        private void RequireActive() {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_threadId != Environment.CurrentManagedThreadId || _current != this) {
                throw new InvalidOperationException("Measurement scopes must remain on one thread and dispose in reverse order.");
            }
        }
    }
}

internal sealed record ProbeMeasurementCheckpoint(
    long Timestamp, long AllocatedBytes, RbfReadMetricsSnapshot RbfReads, ProbeIoSnapshot DirectIo);

internal sealed record ProbeMeasurement(
    long ElapsedTicks,
    double ElapsedMilliseconds,
    long ThreadAllocatedBytes,
    RbfReadMetricsSnapshot RbfReads,
    ProbeIoSnapshot DirectIo);

internal sealed record ProbeIoSnapshot(
    long ReadCalls,
    long RequestedBytes,
    long ReturnedBytes,
    long ReadAheadReturnedBytes,
    long WriteCalls,
    long WrittenBytes,
    long FlushCalls,
    long FlushTicks,
    double FlushMilliseconds);
