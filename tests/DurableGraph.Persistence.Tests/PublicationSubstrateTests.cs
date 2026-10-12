using Atelia.Data;
using Atelia.Data.Hashing;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.DurableGraph.Persistence.Tests;

/// <summary>G0 substrate witnesses; the two-byte records below are not a product wire format.</summary>
public sealed class PublicationSubstrateTests : IDisposable {
    private const uint ProbeTag = 0x50524F42;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"publication-substrate-{Guid.NewGuid():N}");
    private int _next;

    public PublicationSubstrateTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void EmptyAndCompletePublicationLogsColdReopen() {
        string path = NextPath();
        using (IRbfFile file = RbfFile.CreateNew(path)) {
            Assert.Equal(0, ReadAll(file));
            file.DurableFlush();
        }
        using (IRbfFile file = RbfFile.OpenExisting(path)) {
            Assert.Equal(0, ReadAll(file));
            Publish(file, 1);
            Publish(file, 2);
        }
        using IRbfFile reopened = RbfFile.OpenExisting(path);
        Assert.Equal(2, ReadAll(reopened));
    }

    [Theory]
    [InlineData(FailurePoint.BeforeAppend, 1)]
    [InlineData(FailurePoint.AfterAppend, 2)]
    [InlineData(FailurePoint.BeforeFlush, 2)]
    [InlineData(FailurePoint.AfterFlush, 2)]
    public void ThrowingAppendOrFlushCannotBeInterpretedAsUnpublished(FailurePoint point, byte visibleHead) {
        string path = NextPath();
        using (IRbfFile file = RbfFile.CreateNew(path)) {
            Publish(file, 1);
            var fault = new FaultFile(file) { Failure = point };
            Assert.Throws<IOException>(() => Publish(fault, 2));
            // This observation is process-local visibility, not a durability claim.
            Assert.Equal(visibleHead, ReadAll(file));
        }
        using IRbfFile reopened = RbfFile.OpenExisting(path);
        Assert.Equal(visibleHead, ReadAll(reopened));
        // A successful reopen barrier can confirm complete bytes after an uncertain outcome.
        reopened.DurableFlush();
    }

    [Theory]
    [InlineData("payload-crc")]
    [InlineData("trailer-crc")]
    [InlineData("truncate-aligned")]
    [InlineData("truncate-unaligned")]
    [InlineData("garbage-aligned")]
    [InlineData("tombstone")]
    public void StrictReplayRejectsBadTailInsteadOfReturningEarlierConfirmedHead(string damage) {
        string path = NextPath();
        long secondOffset;
        using (IRbfFile file = RbfFile.CreateNew(path)) {
            Publish(file, 1);
            secondOffset = file.TailOffset;
            Publish(file, 2);
        }
        byte[] bytes = File.ReadAllBytes(path);
        switch (damage) {
            case "payload-crc": bytes[(int)secondOffset + 4] ^= 0x20; break;
            case "trailer-crc": bytes[^8] ^= 0x20; break;
            case "truncate-aligned": bytes = bytes[..^4]; break;
            case "truncate-unaligned": bytes = bytes[..^1]; break;
            case "garbage-aligned": bytes = [.. bytes, 1, 2, 3, 4]; break;
            case "tombstone":
                Span<byte> trailer = bytes.AsSpan(bytes.Length - 20, 16);
                trailer[7] |= 0x80;
                RollingCrc.SealCodewordBackward(trailer);
                break;
        }
        File.WriteAllBytes(path, bytes);
        Assert.ThrowsAny<Exception>(() => {
            using IRbfFile file = RbfFile.OpenExisting(path);
            _ = ReadAll(file);
        });
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void OriginalWriterLeaseBarrierPrecedesRolloverAndPublication() {
        string segmentsPath = NextPath();
        string publicationPath = NextPath();
        var options = new RbfSegmentStoreOptions {
            SegmentSizeThresholdBytes = 32,
        };
        SizedPtr first;
        SizedPtr second;
        using (SegmentStore segments = SegmentStore.CreateNew(segmentsPath, options)) {
            using IRbfFile publication = RbfFile.CreateNew(publicationPath);
            using (RbfSegmentWriterLease writer = segments.OpenActiveWriter()) {
                Assert.Equal(1U, writer.SegmentNumber);
                first = writer.File.Append(ProbeTag, new byte[40]).Unwrap();
                writer.File.DurableFlush();
                Assert.Equal(1U, segments.ActiveSegmentNumber);
            }
            Publish(publication, 1);
            using (RbfSegmentWriterLease writer = segments.OpenActiveWriter()) {
                // Reacquiring a writer to flush the prior append would already select another file.
                Assert.Equal(2U, writer.SegmentNumber);
                second = writer.File.Append(ProbeTag, new byte[40]).Unwrap();
                writer.File.DurableFlush();
            }
            Publish(publication, 2);
        }
        using SegmentStore reopened = SegmentStore.OpenExisting(segmentsPath, options);
        using IRbfFile publicationLog = RbfFile.OpenExisting(publicationPath);
        Assert.Equal(2, ReadAll(publicationLog));
        using (RbfSegmentReaderLease reader = reopened.OpenReader(1)) {
            using RbfPooledFrame frame = reader.File.ReadPooledFrame(first).Unwrap();
            Assert.Equal(new byte[40], frame.PayloadAndMeta.ToArray());
        }
        using (RbfSegmentReaderLease reader = reopened.OpenReader(2)) {
            using RbfPooledFrame frame = reader.File.ReadPooledFrame(second).Unwrap();
            Assert.Equal(new byte[40], frame.PayloadAndMeta.ToArray());
        }
    }

    [Fact]
    public void StrictOpenRejectsTornTailAndPreservesTornBytes() {
        string path = NextPath();
        var options = new RbfSegmentStoreOptions();
        string activePath;
        using (SegmentStore segments = SegmentStore.CreateNew(path, options)) {
            using RbfSegmentWriterLease writer = segments.OpenActiveWriter();
            Publish(writer.File, 1);
            Publish(writer.File, 2);
            activePath = Directory.GetFiles(path, "*.rbf", SearchOption.AllDirectories).Single();
        }
        byte[] torn = File.ReadAllBytes(activePath)[..^4];
        File.WriteAllBytes(activePath, torn);
        // Strict open validates the active tail up front and rejects a torn tail
        // without repairing or truncating it.
        Assert.ThrowsAny<Exception>(() => SegmentStore.OpenExisting(path, options));
        Assert.Equal(torn, File.ReadAllBytes(activePath));
    }

    private static void Publish(IRbfFile file, byte ordinal) {
        file.Append(ProbeTag, new byte[] { 1, ordinal }).Unwrap();
        file.DurableFlush();
    }

    private static byte ReadAll(IRbfFile file) {
        byte head = 0;
        var frames = file.ScanForward(showTombstone: true).GetEnumerator();
        while (frames.MoveNext()) {
            using RbfPooledFrame frame = file.ReadPooledFrame(frames.Current.Ticket).Unwrap();
            ReadOnlySpan<byte> bytes = frame.PayloadAndMeta;
            if (frame.IsTombstone || frame.Tag != ProbeTag || frame.TailMetaLength != 0 ||
                bytes.Length != 2 || bytes[0] != 1 || bytes[1] != head + 1) {
                throw new InvalidDataException("Invalid probe publication record.");
            }
            head = bytes[1];
        }
        if (frames.TerminationError is { } error) {
            throw new InvalidDataException($"Invalid publication tail: {error.Message}");
        }
        return head;
    }

    private string NextPath() => Path.Combine(_root, $"{_next++}.rbf");
    public void Dispose() => Directory.Delete(_root, recursive: true);

    public enum FailurePoint { BeforeAppend, AfterAppend, BeforeFlush, AfterFlush }

    private sealed class FaultFile(IRbfFile inner) : IRbfFile {
        public FailurePoint Failure { get; init; }
        public long TailOffset => inner.TailOffset;
        public AteliaResult<SizedPtr> Append(uint tag, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> tailMeta = default) {
            if (Failure == FailurePoint.BeforeAppend) { throw new IOException("Before append."); }
            var result = inner.Append(tag, payload, tailMeta);
            if (Failure == FailurePoint.AfterAppend) { throw new IOException("After append."); }
            return result;
        }
        public void DurableFlush() {
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
