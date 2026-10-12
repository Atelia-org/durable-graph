using Atelia.DurableGraph.Schema;
using Atelia.Data;
using Atelia.Data.Hashing;
using Atelia.Rbf;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class SchemaStoreTests : IDisposable {
    private readonly List<string> _paths = [];

    [Fact]
    public void CompleteClosureRegistersOnceAndColdReopensWithExactDefinitions() {
        string path = NewPath();
        DurableSchema ancestor = new("Base", 2, new DurableFieldInfo(1, TypeTag.Int64));
        DurableSchema leaf = new("Leaf", 4, [new(2, TypeTag.String)], ancestor);
        using (IRbfFile file = RbfFile.CreateNew(path)) {
            var recording = new RecordingFile(file);
            var store = new SchemaStore(recording);
            Assert.Same(leaf, store.Register(leaf));
            Assert.Equal(2, store.Count);
            long tail = file.TailOffset;
            DurableSchema equal = new("Leaf", 4, [new(2, TypeTag.String)], new("Base", 2, new DurableFieldInfo(1, TypeTag.Int64)));
            Assert.Same(leaf, store.Register(equal));
            store.RegisterBatch([]);
            RepresentationId[] ids = store.RegisterRepresentations([ObjectLayout.ForDurable(leaf), ObjectLayout.ForDurable(ancestor)]);
            Assert.Equal(new RepresentationId[] { new(3), new(2) }, ids);
            Assert.Equal(ObjectLayout.ForDurable(leaf), store.GetRepresentation(ids[0]));
            Assert.Equal(tail, file.TailOffset);
            Assert.Equal(1, recording.Appends);
            Assert.Equal(1, recording.Flushes);
        }
        using (IRbfFile file = RbfFile.OpenExisting(path)) {
            var store = new SchemaStore(file);
            Assert.Equal(leaf, store.GetRequired("Leaf", 4));
            Assert.Same(store.GetRequired("Base", 2), store.GetRequired("Leaf", 4).BaseSchema);
            SchemaNotFoundException missing = Assert.Throws<SchemaNotFoundException>(
                () => store.GetRequired("Leaf", 3));
            Assert.Equal("Leaf", missing.SchemaId);
            Assert.Equal(3, missing.Version);
            long tail = file.TailOffset;
            DurableSchema conflicting = new("Leaf", 4, [], ancestor);
            SchemaConflictException conflict = Assert.Throws<SchemaConflictException>(
                () => store.Register(conflicting));
            Assert.Same(store.GetRequired("Leaf", 4), conflict.RegisteredSchema);
            Assert.Same(conflicting, conflict.ConflictingSchema);
            Assert.Equal("Leaf", conflict.SchemaId);
            Assert.Equal(4, conflict.Version);
            Assert.Throws<SchemaConflictException>(() => store.Register(new("Leaf", 4, [new(2, TypeTag.String)], new("Base", 3))));
            Assert.Equal(tail, file.TailOffset);
            Assert.False(store.IsFaulted);
        }
    }

    [Fact]
    public void FullInputAndAncestorConflictsAreRejectedBeforeAnyWrite() {
        using IRbfFile file = RbfFile.CreateNew(NewPath());
        var recording = new RecordingFile(file);
        var store = new SchemaStore(recording);
        DurableSchema old = new("Taken", 1, new DurableFieldInfo(1, TypeTag.Int32));
        store.Register(old);
        long tail = file.TailOffset;
        Assert.Throws<SchemaConflictException>(() => store.RegisterBatch([
            new("New", 1), new("Taken", 1, new DurableFieldInfo(1, TypeTag.String))]));
        Assert.Throws<SchemaConflictException>(() => store.RegisterBatch([
            new("X", 1), new("X", 1, new DurableFieldInfo(1, TypeTag.Int32))]));
        Assert.Throws<SchemaConflictException>(() => store.RegisterBatch([
            new("Leaf1", 1, [], new("Ancestor", 1)),
            new("Leaf2", 1, [], new("Ancestor", 1, new DurableFieldInfo(1, TypeTag.Int32)))]));
        Assert.Equal(1, store.Count);
        Assert.Equal(1, recording.Appends);
        Assert.Equal(1, recording.Flushes);
        Assert.Equal(tail, file.TailOffset);
    }

    [Fact]
    public void EnumerationFailureNullAndDepthFailureLeaveStoreUsable() {
        using IRbfFile file = RbfFile.CreateNew(NewPath());
        var store = new SchemaStore(file);
        long tail = file.TailOffset;
        Assert.Throws<IOException>(() => store.RegisterBatch(FailingEnumeration()));
        Assert.Throws<ArgumentNullException>(() => store.RegisterBatch([new("A", 1), null!]));
        Assert.Throws<ArgumentException>(() => store.Register(SchemaCatalogWireCodecTests.Chain(257)[^1]));
        Assert.Equal(tail, file.TailOffset);
        Assert.Equal(0, store.Count);
        Assert.False(store.IsFaulted);
        store.Register(new("A", 1));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void EnumerationCannotReenterAndCaughtReentryDoesNotPoison() {
        using IRbfFile file = RbfFile.CreateNew(NewPath());
        var store = new SchemaStore(file);
        store.RegisterBatch(AttemptReentry());
        Assert.Equal(1, store.Count);
        Assert.False(store.IsFaulted);

        IEnumerable<DurableSchema> AttemptReentry() {
            Assert.Throws<InvalidOperationException>(() => store.Register(new("Inner", 1)));
            Assert.Throws<InvalidOperationException>(() => store.GetRequired("A", 1));
            Assert.Throws<InvalidOperationException>(() => store.Count);
            Assert.False(store.IsFaulted);
            yield return new("A", 1);
        }
    }

    [Theory]
    [InlineData(FailurePoint.BeforeAppend, false)]
    [InlineData(FailurePoint.AfterAppend, true)]
    [InlineData(FailurePoint.BeforeFlush, true)]
    [InlineData(FailurePoint.AfterFlush, true)]
    public void AmbiguousWriteOrFlushFaultsInstanceAndColdOpenDecides(FailurePoint point, bool batchExists) {
        string path = NewPath();
        using (IRbfFile file = RbfFile.CreateNew(path)) {
            var recording = new RecordingFile(file) { Failure = point };
            var store = new SchemaStore(recording);
            Assert.Throws<IOException>(() => store.Register(new("A", 1)));
            Assert.True(store.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => store.RegisterBatch([]));
            Assert.Throws<InvalidOperationException>(() => store.Register(new("B", 1)));
            Assert.Throws<InvalidOperationException>(() => store.GetRequired("A", 1));
            Assert.Throws<InvalidOperationException>(() => store.Count);
        }
        // Process-local close/reopen observes complete bytes; this does not simulate power loss.
        using (IRbfFile file = RbfFile.OpenExisting(path)) {
            var recovered = new SchemaStore(file);
            Assert.Equal(batchExists ? 1 : 0, recovered.Count);
            recovered.Register(new("A", 1));
            Assert.Equal(1, recovered.Count);
        }
    }

    [Fact]
    public void ReadOnlyStoreRebuildsWithoutTakingOwnershipOrWriting() {
        string path = NewPath();
        using (IRbfFile file = RbfFile.CreateNew(path)) { new SchemaStore(file).Register(new("A", 1)); }
        byte[] original = File.ReadAllBytes(path);
        using (IRbfFile file = RbfFile.OpenReadOnlyExisting(path)) {
            var store = new SchemaStore(file, readOnly: true);
            Assert.Equal(1, store.Count);
            Assert.Throws<InvalidOperationException>(() => store.Register(new("B", 1)));
            Assert.Throws<InvalidOperationException>(() => store.RegisterBatch([]));
            Assert.True(file.TailOffset > 0);
        }
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void WritableReopenConfirmsUncertainCompleteBytesBeforeReturning() {
        string path = NewPath();
        using (IRbfFile file = RbfFile.CreateNew(path)) {
            var failed = new RecordingFile(file) { Failure = FailurePoint.AfterAppend };
            Assert.Throws<IOException>(() => new SchemaStore(failed).Register(new("A", 1)));
            Assert.Equal(0, failed.Flushes);
        }
        using (IRbfFile file = RbfFile.OpenReadOnlyExisting(path)) {
            var recording = new RecordingFile(file);
            Assert.Equal(1, new SchemaStore(recording, readOnly: true).Count);
            Assert.Equal(0, recording.Flushes);
        }
        using (IRbfFile file = RbfFile.OpenExisting(path)) {
            var recording = new RecordingFile(file);
            var recovered = new SchemaStore(recording);
            Assert.Equal(1, recording.Flushes);
            recovered.Register(new("A", 1));
            Assert.Equal(0, recording.Appends);
            Assert.Equal(1, recording.Flushes);
        }
    }

    [Theory]
    [InlineData(FailurePoint.BeforeFlush)]
    [InlineData(FailurePoint.AfterFlush)]
    public void FailedReopenBarrierDoesNotReturnARegistrationStore(FailurePoint point) {
        string path = NewPath();
        using (IRbfFile file = RbfFile.CreateNew(path)) { new SchemaStore(file).Register(new("A", 1)); }
        byte[] original = File.ReadAllBytes(path);
        using (IRbfFile file = RbfFile.OpenExisting(path)) {
            var recording = new RecordingFile(file) { Failure = point };
            Assert.Throws<IOException>(() => new SchemaStore(recording));
            Assert.Equal(0, recording.Appends);
            Assert.Equal(1, recording.Flushes);
        }
        Assert.Equal(original, File.ReadAllBytes(path));
        using (IRbfFile file = RbfFile.OpenExisting(path)) { Assert.Equal(1, new SchemaStore(file).Count); }
    }

    [Fact]
    public void PhysicallyValidTombstoneIsRejectedRatherThanHiddenByTheScanner() {
        string path = NewPath();
        using (IRbfFile file = RbfFile.CreateNew(path)) { new SchemaStore(file).Register(new("A", 1)); }
        byte[] bytes = File.ReadAllBytes(path);
        // RBF trailer is the 16-byte codeword before the four-byte fence.
        // Descriptor bit 31 marks tombstones; reseal its independent trailer CRC.
        Span<byte> trailer = bytes.AsSpan(bytes.Length - 20, 16);
        trailer[7] |= 0x80;
        RollingCrc.SealCodewordBackward(trailer);
        File.WriteAllBytes(path, bytes);
        using (IRbfFile file = RbfFile.OpenExisting(path)) {
            var frames = file.ScanForward(showTombstone: true).GetEnumerator();
            Assert.True(frames.MoveNext());
            using RbfPooledFrame frame = file.ReadPooledFrame(frames.Current.Ticket).Unwrap();
            Assert.True(frame.IsTombstone);
            Assert.Throws<InvalidDataException>(() => new SchemaStore(file));
        }
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("tag")]
    [InlineData("meta")]
    [InlineData("payload")]
    [InlineData("conflict")]
    public void InvalidCompleteFramesAreNotSkipped(string kind) {
        string path = NewPath();
        using (IRbfFile file = RbfFile.CreateNew(path)) {
            new SchemaStore(file).Register(new("A", 1));
            byte[] payload = kind switch {
                "payload" => [99],
                "conflict" => CatalogTestData.Encode(CatalogTestData.Schemas([new DurableSchema("A", 1, new DurableFieldInfo(1, TypeTag.String))], 3)),
                _ => CatalogTestData.Encode(CatalogTestData.Schemas([new DurableSchema("B", 1)], 3)),
            };
            file.Append(kind == "tag" ? 999U : SchemaCatalogWireCodec.RbfTag, payload,
                kind == "meta" ? new byte[] { 1 } : []).Unwrap();
            file.DurableFlush();
        }
        byte[] original = File.ReadAllBytes(path);
        using (IRbfFile file = RbfFile.OpenExisting(path)) {
            Assert.ThrowsAny<Exception>(() => new SchemaStore(file));
        }
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("trailer")]
    [InlineData("tail")]
    [InlineData("unaligned")]
    public void ConfirmedCorruptionAndTornTailFailWithoutChangingBytes(string kind) {
        string path = NewPath();
        long secondOffset;
        using (IRbfFile file = RbfFile.CreateNew(path)) {
            var store = new SchemaStore(file);
            store.Register(new("A", 1));
            secondOffset = file.TailOffset;
            store.Register(new("B", 1));
        }
        byte[] corrupt = File.ReadAllBytes(path);
        switch (kind) {
            case "payload": corrupt[(int)secondOffset + 4] ^= 0x20; break;
            case "trailer": corrupt[^8] ^= 0x20; break;
            case "tail": corrupt = [.. corrupt, 1, 2, 3, 4]; break;
            case "unaligned": corrupt = [.. corrupt, 1]; break;
        }
        File.WriteAllBytes(path, corrupt);
        Assert.ThrowsAny<Exception>(() => {
            using IRbfFile file = RbfFile.OpenExisting(path);
            _ = new SchemaStore(file);
        });
        Assert.Equal(corrupt, File.ReadAllBytes(path));
    }

    [Fact]
    public void EquivalentPhysicalDuplicatesAreRejectedAsReusedCatalogIds() {
        using IRbfFile file = RbfFile.CreateNew(NewPath());
        byte[] payload = CatalogTestData.Encode(CatalogTestData.Schemas([new DurableSchema("A", 1)]));
        file.Append(SchemaCatalogWireCodec.RbfTag, payload).Unwrap();
        file.Append(SchemaCatalogWireCodec.RbfTag, payload).Unwrap();
        file.DurableFlush();
        Assert.Throws<InvalidDataException>(() => new SchemaStore(file));
    }

    [Fact]
    public void AnotherFacadeCannotContinueFromItsStaleRegistrationIndex() {
        using IRbfFile file = RbfFile.CreateNew(NewPath());
        var first = new SchemaStore(file);
        var stale = new SchemaStore(file);
        first.Register(new("A", 1));
        Assert.Throws<InvalidOperationException>(() => stale.Register(new("A", 1, new DurableFieldInfo(1, TypeTag.String))));
        Assert.True(stale.IsFaulted);
        Assert.Equal(1, first.Count);
    }

    [Fact]
    public void RawWriteDuringInputEnumerationIsDetectedBeforeTheStoreAppends() {
        using IRbfFile file = RbfFile.CreateNew(NewPath());
        var store = new SchemaStore(file);
        Assert.Throws<InvalidOperationException>(() => store.RegisterBatch(ExternalWrite()));
        Assert.True(store.IsFaulted);
        var reopened = new SchemaStore(file);
        Assert.Equal(1, reopened.Count);
        Assert.Equal("External", reopened.GetRequired("External", 1).SchemaId);
        Assert.Throws<SchemaNotFoundException>(() => reopened.GetRequired("Requested", 1));

        IEnumerable<DurableSchema> ExternalWrite() {
            file.Append(SchemaCatalogWireCodec.RbfTag, CatalogTestData.Encode(CatalogTestData.Schemas([new DurableSchema("External", 1)]))).Unwrap();
            yield return new("Requested", 1);
        }
    }

    private static IEnumerable<DurableSchema> FailingEnumeration() {
        yield return new("New", 1);
        throw new IOException("Injected enumeration failure.");
    }

    private string NewPath() {
        string path = Path.Combine(Path.GetTempPath(), $"durable-schema-{Guid.NewGuid():N}.rbf");
        _paths.Add(path);
        return path;
    }

    public void Dispose() {
        foreach (string path in _paths) { File.Delete(path); }
    }

    public enum FailurePoint { None, BeforeAppend, AfterAppend, BeforeFlush, AfterFlush }

    private sealed class RecordingFile(IRbfFile inner) : IRbfFile {
        public int Appends { get; private set; }
        public int Flushes { get; private set; }
        public FailurePoint Failure { get; init; }
        public long TailOffset => inner.TailOffset;
        public AteliaResult<SizedPtr> Append(uint tag, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> tailMeta = default) {
            Appends++;
            if (Failure == FailurePoint.BeforeAppend) { throw new IOException("Before append."); }
            var result = inner.Append(tag, payload, tailMeta);
            if (Failure == FailurePoint.AfterAppend) { throw new IOException("After append."); }
            return result;
        }
        public void DurableFlush() {
            Flushes++;
            if (Failure == FailurePoint.BeforeFlush) { throw new IOException("Before flush."); }
            inner.DurableFlush();
            if (Failure == FailurePoint.AfterFlush) { throw new IOException("After flush."); }
        }
        public RbfFrameBuilder BeginAppend() => inner.BeginAppend();
        public AteliaResult<RbfPooledFrame> ReadPooledFrame(SizedPtr ptr) => inner.ReadPooledFrame(ptr);
        public AteliaResult<RbfFrame> ReadFrame(SizedPtr ptr, Span<byte> buffer) => inner.ReadFrame(ptr, buffer);
        public RbfReverseSequence ScanReverse(bool showTombstone = false) => inner.ScanReverse(showTombstone);
        public RbfForwardSequence ScanForward(bool showTombstone = false) => inner.ScanForward(showTombstone);
        public AteliaResult<RbfScanBoundary> GetScanBoundaryAfter(SizedPtr ticket) => inner.GetScanBoundaryAfter(ticket);
        public AteliaResult<RbfForwardSequence> ScanForward(RbfScanBoundary boundary, bool showTombstone = false) => inner.ScanForward(boundary, showTombstone);
        public long GetPhysicalOffsetImmediatelyAfter(SizedPtr ticket) => inner.GetPhysicalOffsetImmediatelyAfter(ticket);
        public AteliaResult<OptionalRbfFrameInfo> ReadFrameInfoImmediatelyAfter(SizedPtr ticket) => inner.ReadFrameInfoImmediatelyAfter(ticket);
        public AteliaResult<RbfFrameInfo> ReadFrameInfo(SizedPtr ticket) => inner.ReadFrameInfo(ticket);
        public AteliaResult<RbfTailMeta> ReadTailMeta(SizedPtr ticket, Span<byte> buffer) => inner.ReadTailMeta(ticket, buffer);
        public AteliaResult<RbfPooledTailMeta> ReadPooledTailMeta(SizedPtr ticket) => inner.ReadPooledTailMeta(ticket);
        public void Truncate(long newLengthBytes) => inner.Truncate(newLengthBytes);
        public void SetupReadLog(string? logPath) => inner.SetupReadLog(logPath);
        public void Dispose() => inner.Dispose();
    }
}
