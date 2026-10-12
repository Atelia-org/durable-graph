using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.Rbf.Internal;

namespace Atelia.UnifiedRootProbe;

internal sealed record RootView(long Generation, long AValue, long BValue,
    string AIdentity, string BIdentity, long ATicketOffset, long BTicketOffset,
    long AVisibleEof, long BVisibleEof);

internal sealed record RecoveryResult(string Status, string Detail, string? TruncatedFile, RootView? View,
    bool ActionAttempted, bool LengthChanged, bool BarrierCompleted) {
    internal bool SourceMayHaveChanged => ActionAttempted;
}

internal sealed record InventoryFile(string Path, long Length, long CompleteLength, string Sha256, long[] Offsets);

internal readonly record struct ChildRef(Guid Identity, SizedPtr Ticket, long VisibleEof);
internal readonly record struct RootRecord(long Generation, ChildRef A, ChildRef B);
internal readonly record struct ChildRecord(long Generation, long Value);

/// <summary>Fixed-shape, single-writer experiment; none of its wire formats are product formats.</summary>
internal static class UnifiedRootStore {
    private const int HeaderLength = 4;
    private const int IdentityPayloadLength = 24;
    private const int DataPayloadLength = 32;
    private const int RootPayloadLength = 80;
    internal const int IdentityTicketLength = IdentityPayloadLength + 24;
    internal const int DataTicketLength = DataPayloadLength + 24;
    internal const int RootTicketLength = RootPayloadLength + 24;
    internal const int IdentityPhysicalLength = IdentityTicketLength + 4;
    internal const int DataPhysicalLength = DataTicketLength + 4;
    internal const int RootPhysicalLength = RootTicketLength + 4;
    internal const int FirstDataOffset = HeaderLength + IdentityPhysicalLength;
    private const uint IdentityTag = 0x5549_4446;
    private const uint DataTag = 0x5544_4154;
    private const uint RootTag = 0x5552_4F54;
    private static ReadOnlySpan<byte> IdentityMagic => "URID0001"u8;
    private static ReadOnlySpan<byte> DataMagic => "URDT0001"u8;
    private static ReadOnlySpan<byte> RootMagic => "URRT0001"u8;
    private static readonly string[] Files = ["a.rbf", "b.rbf", "roots.rbf"];

    private static string FilePath(string root, string name) => Path.Combine(root, name);

    private static FileStream Lock(string root) => new(FilePath(root, "repository.lock"),
        FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    private static void RequireInventory(string root) {
        string[] actual = Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName)
            .Order(StringComparer.Ordinal).ToArray()!;
        string[] expected = ["a.rbf", "b.rbf", "repository.lock", "roots.rbf"];
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal)) {
            throw new InvalidDataException("Unknown or missing repository entry.");
        }
        foreach (string name in expected) {
            FileAttributes attributes = File.GetAttributes(FilePath(root, name));
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) {
                throw new InvalidDataException("Repository entry is not an ordinary file: " + name);
            }
        }
    }

    internal static RootView Seed(string root, long aValue, long bValue) {
        root = Path.GetFullPath(root);
        if (Directory.Exists(root)) { throw new IOException("Seed requires a new fixture directory."); }
        Directory.CreateDirectory(root);
        using var repositoryLock = new FileStream(FilePath(root, "repository.lock"), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None);
        Guid aIdentity = Guid.NewGuid(), bIdentity = Guid.NewGuid();
        SizedPtr aTicket, bTicket;
        long aEof, bEof;
        using (var a = RbfFile.CreateNew(FilePath(root, "a.rbf"), RbfCacheMode.Off)) {
            AppendIdentity(a, aIdentity);
            aTicket = AppendData(a, 0, aValue);
            Flush(a);
            aEof = a.TailOffset;
            using var b = RbfFile.CreateNew(FilePath(root, "b.rbf"), RbfCacheMode.Off);
            AppendIdentity(b, bIdentity);
            bTicket = AppendData(b, 0, bValue);
            Flush(b);
            bEof = b.TailOffset;
            using var roots = RbfFile.CreateNew(FilePath(root, "roots.rbf"), RbfCacheMode.Off);
            AppendRoot(roots, new(0, new(aIdentity, aTicket, aEof),
                new(bIdentity, bTicket, bEof)));
            Flush(roots);
        }
        // Fixture initialization establishes names before transactions. This is not a Windows directory fsync claim.
        return new(0, aValue, bValue, aIdentity.ToString("N"), bIdentity.ToString("N"),
            aTicket.Offset, bTicket.Offset, aEof, bEof);
    }

    internal static RootReader Open(string root, long? generation = null) {
        root = Path.GetFullPath(root);
        FileStream repositoryLock = Lock(root);
        try {
            RequireInventory(root);
            RootRecord record = ReadRoot(root, generation);
            return new RootReader(root, repositoryLock, record);
        }
        catch { repositoryLock.Dispose(); throw; }
    }

    internal static RootView Inspect(string root, long? generation = null) {
        using var reader = Open(root, generation);
        return reader.Read();
    }

    internal sealed class RootReader(string root, FileStream repositoryLock, RootRecord record) : IDisposable {
        private bool _disposed;
        internal RootView Read() {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ChildRecord a = ReadChild(root, "a.rbf", record.A, record.Generation);
            ChildRecord b = ReadChild(root, "b.rbf", record.B, record.Generation);
            return ToView(record, a, b);
        }
        public void Dispose() { if (_disposed) { return; } _disposed = true; repositoryLock.Dispose(); }
    }

    internal static RootView Commit(string root, long aValue, long bValue, Action<string>? checkpoint = null) {
        root = Path.GetFullPath(root);
        using var repositoryLock = Lock(root);
        RequireInventory(root);
        Qualification qualified = Qualify(root, long.MaxValue, default);
        if (qualified.ShortFile is not null) { throw new InvalidDataException("A short suffix requires offline recovery before append."); }
        RootRecord old = ReadRoot(root, null);
        long nextGeneration = checked(old.Generation + 1);
        using var a = RbfFile.OpenExisting(FilePath(root, "a.rbf"), RbfCacheMode.Off);
        using var b = RbfFile.OpenExisting(FilePath(root, "b.rbf"), RbfCacheMode.Off);
        using var roots = RbfFile.OpenExisting(FilePath(root, "roots.rbf"), RbfCacheMode.Off);
        if (a.TailOffset != qualified.Boundaries["a.rbf"] || b.TailOffset != qualified.Boundaries["b.rbf"] ||
            roots.TailOffset != qualified.Boundaries["roots.rbf"]) {
            throw new IOException("Length changed after qualification.");
        }
        // Reconfirm the selected old root and dependencies before appending behind complete orphans.
        Flush(a); Flush(b); Flush(roots);
        SizedPtr aTicket = AppendData(a, nextGeneration, aValue);
        checkpoint?.Invoke("AfterAppendA");
        SizedPtr bTicket = AppendData(b, nextGeneration, bValue);
        checkpoint?.Invoke("AfterAppendB");
        Flush(a); checkpoint?.Invoke("AfterFlushA");
        Flush(b); checkpoint?.Invoke("AfterFlushB");
        RootRecord next = new(nextGeneration, new(old.A.Identity, aTicket, a.TailOffset),
            new(old.B.Identity, bTicket, b.TailOffset));
        AppendRoot(roots, next);
        checkpoint?.Invoke("AfterAppendRoot");
        Flush(roots); checkpoint?.Invoke("AfterFlushRoot");
        checkpoint?.Invoke("BeforeAck");
        return ToView(next, new(nextGeneration, aValue), new(nextGeneration, bValue));
    }

    /// <summary>Cost-fixture preparation only. Complete physical pairs remain outside root authority.</summary>
    internal static void PopulateOrphans(string root, int count) {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        root = Path.GetFullPath(root);
        using var repositoryLock = Lock(root);
        RequireInventory(root);
        Qualification qualified = Qualify(root, long.MaxValue, default);
        if (qualified.ShortFile is not null) { throw new InvalidDataException("Recover the short suffix first."); }
        long generation = checked(qualified.RootGeneration + 1);
        using var a = RbfFile.OpenExisting(FilePath(root, "a.rbf"), RbfCacheMode.Off);
        using var b = RbfFile.OpenExisting(FilePath(root, "b.rbf"), RbfCacheMode.Off);
        for (int i = 0; i < count; i++) {
            AppendData(a, generation, i);
            AppendData(b, generation, i);
            Flush(a);
            Flush(b);
        }
    }

    internal static RecoveryResult Recover(string root, long readBudget = long.MaxValue,
        Action<string>? checkpoint = null, CancellationToken cancellationToken = default) {
        ArgumentOutOfRangeException.ThrowIfNegative(readBudget);
        root = Path.GetFullPath(root);
        bool attempted = false, lengthChanged = false, barrierCompleted = false;
        string? selectedFile = null;
        void Point(string name) {
            cancellationToken.ThrowIfCancellationRequested();
            checkpoint?.Invoke(name);
            cancellationToken.ThrowIfCancellationRequested();
        }
        try {
            using var repositoryLock = Lock(root);
            RequireInventory(root);
            Qualification qualified = Qualify(root, readBudget, cancellationToken);
            selectedFile = qualified.ShortFile;
            Point("CandidateResourcesDisposed");
            Point("BeforeAction");
            if (qualified.ShortFile is string shortFile) {
                RequireInventory(root);
                foreach (string name in Files) {
                    if (new FileInfo(FilePath(root, name)).Length != qualified.Lengths[name]) {
                        throw new IOException("Length changed before action: " + name);
                    }
                }
                string path = FilePath(root, shortFile);
                using var target = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1);
                var again = RbfPrefixInspector.Inspect(target, long.MaxValue, cancellationToken);
                ProbeMeasurements.Current?.RecordReads(again.ReadRequests, again.RequestedBytes, again.ReturnedBytes);
                if (again.Status == RbfPrefixStatus.Cancelled) { throw new OperationCanceledException(cancellationToken); }
                if (again.Status != RbfPrefixStatus.IncompleteSuffix ||
                    again.OriginalLength != qualified.Lengths[shortFile] ||
                    again.LastCompleteBoundary != qualified.Boundaries[shortFile] ||
                    target.Length != qualified.Lengths[shortFile]) {
                    throw new IOException("Short suffix changed before action.");
                }
                CheckFixedSuffix(target, shortFile, again.LastCompleteBoundary, again.OriginalLength,
                    qualified.RootGeneration, qualified.AIdentity, qualified.BIdentity,
                    qualified.Boundaries["a.rbf"], qualified.Boundaries["b.rbf"]);
                Point("BeforeSetLength");
                attempted = true;
                target.SetLength(again.LastCompleteBoundary);
                lengthChanged = true;
                Point("AfterSetLengthBeforeFlush");
                long flushStart = Stopwatch.GetTimestamp();
                target.Flush(flushToDisk: true);
                ProbeMeasurements.Current?.RecordFlush(Stopwatch.GetTimestamp() - flushStart);
                barrierCompleted = true;
            }
            Point("AfterAction");
            Point("BeforeFreshOpen");
            // Fresh handles after any action; never resume qualification caches or candidates.
            Qualification fresh = Qualify(root, long.MaxValue, cancellationToken);
            if (fresh.ShortFile is not null) { throw new InvalidDataException("Strict reopen still has a short suffix."); }
            using (var a = RbfFile.OpenExisting(FilePath(root, "a.rbf"), RbfCacheMode.Off)) {
                Flush(a); Point("AfterConfirmA");
            }
            using (var b = RbfFile.OpenExisting(FilePath(root, "b.rbf"), RbfCacheMode.Off)) {
                Flush(b); Point("AfterConfirmB");
            }
            using (var roots = RbfFile.OpenExisting(FilePath(root, "roots.rbf"), RbfCacheMode.Off)) {
                Flush(roots); Point("AfterConfirmRoot");
            }
            Point("BeforeDelivery");
            RequireInventory(root);
            foreach (string name in Files) {
                if (new FileInfo(FilePath(root, name)).Length != fresh.Boundaries[name]) {
                    throw new IOException("Length changed before delivery: " + name);
                }
            }
            RootRecord rootRecord = ReadRoot(root, null);
            return new("Succeeded", "Fresh strict reopen and A/B/root confirmation completed.",
                qualified.ShortFile, ToView(rootRecord,
                    ReadChild(root, "a.rbf", rootRecord.A, rootRecord.Generation),
                    ReadChild(root, "b.rbf", rootRecord.B, rootRecord.Generation)),
                attempted, lengthChanged, barrierCompleted);
        }
        catch (OperationCanceledException ex) { return new("Cancelled", ex.Message, selectedFile, null, attempted, lengthChanged, barrierCompleted); }
        catch (BudgetExceededException ex) { return new("BudgetExceeded", ex.Message, selectedFile, null, attempted, lengthChanged, barrierCompleted); }
        catch (InvalidDataException ex) { return new("Refused", ex.Message, selectedFile, null, attempted, lengthChanged, barrierCompleted); }
        catch (IOException ex) { return new("IoOrActionFailure", ex.Message, selectedFile, null, attempted, lengthChanged, barrierCompleted); }
        catch (UnauthorizedAccessException ex) { return new("IoOrActionFailure", ex.Message, selectedFile, null, attempted, lengthChanged, barrierCompleted); }
    }

    private static RootView ToView(RootRecord root, ChildRecord a, ChildRecord b) =>
        new(root.Generation, a.Value, b.Value, root.A.Identity.ToString("N"), root.B.Identity.ToString("N"),
            root.A.Ticket.Offset, root.B.Ticket.Offset, root.A.VisibleEof, root.B.VisibleEof);

    private static void Flush(IRbfFile file) {
        long start = Stopwatch.GetTimestamp();
        file.DurableFlush();
        ProbeMeasurements.Current?.RecordFlush(Stopwatch.GetTimestamp() - start);
    }

    private static SizedPtr AppendIdentity(IRbfFile file, Guid identity) {
        byte[] payload = new byte[IdentityPayloadLength];
        IdentityMagic.CopyTo(payload);
        identity.TryWriteBytes(payload.AsSpan(8));
        SizedPtr ticket = file.Append(IdentityTag, payload).Unwrap();
        ProbeMeasurements.Current?.RecordWrite(ticket.Length + 4);
        if (ticket.Offset != HeaderLength || ticket.Length != IdentityTicketLength || file.TailOffset != FirstDataOffset) {
            throw new InvalidDataException("Identity frame did not have the fixed shape.");
        }
        return ticket;
    }

    private static SizedPtr AppendData(IRbfFile file, long generation, long value) {
        byte[] payload = new byte[DataPayloadLength];
        DataMagic.CopyTo(payload);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(8), generation);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(16), value);
        SizedPtr ticket = file.Append(DataTag, payload).Unwrap();
        ProbeMeasurements.Current?.RecordWrite(ticket.Length + 4);
        if (ticket.Length != DataTicketLength || file.TailOffset != ticket.Offset + DataPhysicalLength) {
            throw new InvalidDataException("Data frame did not have the fixed shape.");
        }
        return ticket;
    }

    private static SizedPtr AppendRoot(IRbfFile file, RootRecord root) {
        byte[] payload = new byte[RootPayloadLength];
        RootMagic.CopyTo(payload);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(8), root.Generation);
        WriteChild(payload.AsSpan(16, 32), root.A);
        WriteChild(payload.AsSpan(48, 32), root.B);
        SizedPtr ticket = file.Append(RootTag, payload).Unwrap();
        ProbeMeasurements.Current?.RecordWrite(ticket.Length + 4);
        if (ticket.Length != RootTicketLength || file.TailOffset != ticket.Offset + RootPhysicalLength ||
            ticket.Offset != HeaderLength + root.Generation * RootPhysicalLength) {
            throw new InvalidDataException("Root frame did not occupy its generation slot.");
        }
        return ticket;
    }

    private static void WriteChild(Span<byte> target, ChildRef child) {
        child.Identity.TryWriteBytes(target);
        BinaryPrimitives.WriteUInt64LittleEndian(target[16..], child.Ticket.Packed);
        BinaryPrimitives.WriteInt64LittleEndian(target[24..], child.VisibleEof);
    }

    private static ChildRef ParseChild(ReadOnlySpan<byte> payload) => new(
        new Guid(payload[..16]), SizedPtr.FromPacked(BinaryPrimitives.ReadUInt64LittleEndian(payload[16..])),
        BinaryPrimitives.ReadInt64LittleEndian(payload[24..]));

    private static RootRecord ReadRoot(string root, long? generation) {
        string path = FilePath(root, "roots.rbf");
        long length = new FileInfo(path).Length;
        long fullCount = (length - HeaderLength) / RootPhysicalLength;
        long boundary = HeaderLength + fullCount * RootPhysicalLength;
        if (length < HeaderLength + RootPhysicalLength || fullCount < 1) {
            throw new InvalidDataException("No complete root slot.");
        }
        if (boundary != length) {
            Guid aId = ReadIdentityAt(root, "a.rbf"), bId = ReadIdentityAt(root, "b.rbf");
            long aLength = new FileInfo(FilePath(root, "a.rbf")).Length;
            long bLength = new FileInfo(FilePath(root, "b.rbf")).Length;
            RequireUpcomingChildren(root, fullCount, aLength, bLength);
            CheckFixedSuffix(path, "roots.rbf", boundary, length, fullCount - 1, aId, bId,
                aLength, bLength);
        }
        long selected = generation ?? fullCount - 1;
        if (selected < 0 || selected >= fullCount) { throw new ArgumentOutOfRangeException(nameof(generation)); }
        long selectedEof = HeaderLength + (selected + 1) * RootPhysicalLength;
        using var file = RbfFile.OpenReadOnlyCandidate(path, selectedEof);
        RootRecord record = ReadRootSlot(file, selected);
        return record;
    }

    private static RootRecord ReadRootSlot(IRbfFile file, long generation) {
        SizedPtr ticket = SizedPtr.Create(HeaderLength + generation * RootPhysicalLength, RootTicketLength);
        using var frame = ReadFrame(file, ticket);
        ReadOnlySpan<byte> payload = frame.PayloadAndMeta;
        if (frame.Tag != RootTag || frame.IsTombstone || frame.TailMetaLength != 0 ||
            payload.Length != RootPayloadLength || !payload[..8].SequenceEqual(RootMagic) ||
            BinaryPrimitives.ReadInt64LittleEndian(payload[8..]) != generation) {
            throw new InvalidDataException("Root slot has bad shape, tag, magic, or generation.");
        }
        ChildRef a = ParseChild(payload[16..48]), b = ParseChild(payload[48..80]);
        ValidateChildRef(a); ValidateChildRef(b);
        if (generation == 0 && (a.Ticket.Offset != FirstDataOffset || b.Ticket.Offset != FirstDataOffset)) {
            throw new InvalidDataException("Seed root does not select the first data records.");
        }
        return new(generation, a, b);
    }

    private static Guid ReadIdentityAt(string root, string name) {
        if (new FileInfo(FilePath(root, name)).Length < FirstDataOffset) {
            throw new InvalidDataException("Child identity frame is missing: " + name);
        }
        using var file = RbfFile.OpenReadOnlyCandidate(FilePath(root, name), FirstDataOffset);
        return ReadIdentity(file);
    }

    private static Guid ReadIdentity(IRbfFile file) {
        using var frame = ReadFrame(file, SizedPtr.Create(HeaderLength, IdentityTicketLength));
        ReadOnlySpan<byte> payload = frame.PayloadAndMeta;
        if (frame.Tag != IdentityTag || frame.IsTombstone || frame.TailMetaLength != 0 ||
            payload.Length != IdentityPayloadLength || !payload[..8].SequenceEqual(IdentityMagic)) {
            throw new InvalidDataException("Bad child file identity frame.");
        }
        Guid id = new(payload[8..24]);
        if (id == Guid.Empty) { throw new InvalidDataException("Empty child file identity."); }
        return id;
    }

    private static ChildRecord ReadChild(string root, string name, ChildRef selected, long rootGeneration) {
        ValidateChildRef(selected);
        if (new FileInfo(FilePath(root, name)).Length < selected.VisibleEof) {
            throw new InvalidDataException("Selected child EOF exceeds physical file: " + name);
        }
        IRbfFile file;
        try { file = RbfFile.OpenReadOnlyCandidate(FilePath(root, name), selected.VisibleEof); }
        catch (ArgumentOutOfRangeException ex) { throw new InvalidDataException("Selected child EOF is unavailable: " + name, ex); }
        using (file) {
            if (ReadIdentity(file) != selected.Identity) { throw new InvalidDataException("Child file identity changed: " + name); }
            ChildRecord child = ReadData(file, selected.Ticket);
            if (child.Generation != rootGeneration) { throw new InvalidDataException("Mixed child generation: " + name); }
            return child;
        }
    }

    private static void RequireUpcomingChildren(string root, long upcomingGeneration, long aEof, long bEof) {
        foreach ((string name, long eof) in new[] { ("a.rbf", aEof), ("b.rbf", bEof) }) {
            if (eof < FirstDataOffset + DataPhysicalLength ||
                (eof - FirstDataOffset) % DataPhysicalLength != 0) {
                throw new InvalidDataException("Short root has no complete upcoming child: " + name);
            }
            using var file = RbfFile.OpenReadOnlyCandidate(FilePath(root, name), eof);
            if (ReadData(file, SizedPtr.Create(eof - DataPhysicalLength, DataTicketLength)).Generation != upcomingGeneration) {
                throw new InvalidDataException("Short root lacks the upcoming child generation: " + name);
            }
        }
    }

    private static void ValidateChildRef(ChildRef selected) {
        if (selected.Identity == Guid.Empty || selected.Ticket.Length != DataTicketLength ||
            selected.Ticket.Offset < FirstDataOffset ||
            (selected.Ticket.Offset - FirstDataOffset) % DataPhysicalLength != 0 ||
            selected.VisibleEof != selected.Ticket.Offset + DataPhysicalLength) {
            throw new InvalidDataException("Root child ticket, identity, or EOF is invalid.");
        }
    }

    private static ChildRecord ReadData(IRbfFile file, SizedPtr ticket) {
        using var frame = ReadFrame(file, ticket);
        ReadOnlySpan<byte> payload = frame.PayloadAndMeta;
        if (frame.Tag != DataTag || frame.IsTombstone || frame.TailMetaLength != 0 ||
            payload.Length != DataPayloadLength || !payload[..8].SequenceEqual(DataMagic) ||
            BinaryPrimitives.ReadInt64LittleEndian(payload[24..]) != 0) {
            throw new InvalidDataException("Bad child data frame.");
        }
        long generation = BinaryPrimitives.ReadInt64LittleEndian(payload[8..]);
        if (generation < 0) { throw new InvalidDataException("Negative child generation."); }
        return new(generation, BinaryPrimitives.ReadInt64LittleEndian(payload[16..]));
    }

    private static RbfPooledFrame ReadFrame(IRbfFile file, SizedPtr ticket) {
        var result = file.ReadPooledFrame(ticket);
        if (result.IsFailure) { throw new InvalidDataException("Invalid complete RBF frame: " + result.Error); }
        return result.Unwrap();
    }

    private sealed class BudgetExceededException(string message) : Exception(message);
    private sealed record Qualification(Dictionary<string, long> Lengths, Dictionary<string, long> Boundaries,
        string? ShortFile, long RootGeneration, Guid AIdentity, Guid BIdentity);

    private static Qualification Qualify(string root, long budget, CancellationToken token) {
        var lengths = new Dictionary<string, long>(StringComparer.Ordinal);
        var boundaries = new Dictionary<string, long>(StringComparer.Ordinal);
        string? shortFile = null;
        foreach (string name in Files) {
            token.ThrowIfCancellationRequested();
            var inspection = RbfPrefixInspector.Inspect(FilePath(root, name), budget, token);
            ProbeMeasurements.Current?.RecordReads(inspection.ReadRequests, inspection.RequestedBytes, inspection.ReturnedBytes);
            budget -= inspection.RequestedBytes;
            if (inspection.Status == RbfPrefixStatus.BudgetExceeded) { throw new BudgetExceededException(name + ": " + inspection.Detail); }
            if (inspection.Status == RbfPrefixStatus.Cancelled) { throw new OperationCanceledException(token); }
            if (inspection.Status == RbfPrefixStatus.IoFailure) { throw new IOException(name + ": " + inspection.Detail); }
            if (inspection.Status is not (RbfPrefixStatus.Complete or RbfPrefixStatus.IncompleteSuffix)) {
                throw new InvalidDataException(name + ": " + inspection.Status + ": " + inspection.Detail);
            }
            if (inspection.Status == RbfPrefixStatus.IncompleteSuffix) {
                if (shortFile is not null) { throw new InvalidDataException("More than one short suffix."); }
                shortFile = name;
            }
            lengths.Add(name, inspection.OriginalLength);
            boundaries.Add(name, inspection.LastCompleteBoundary);
        }
        if (boundaries["a.rbf"] < FirstDataOffset + DataPhysicalLength ||
            boundaries["b.rbf"] < FirstDataOffset + DataPhysicalLength ||
            (boundaries["a.rbf"] - FirstDataOffset) % DataPhysicalLength != 0 ||
            (boundaries["b.rbf"] - FirstDataOffset) % DataPhysicalLength != 0 ||
            boundaries["roots.rbf"] < HeaderLength + RootPhysicalLength ||
            (boundaries["roots.rbf"] - HeaderLength) % RootPhysicalLength != 0) {
            throw new InvalidDataException("Complete prefix is outside fixed record grid.");
        }
        long latest = (boundaries["roots.rbf"] - HeaderLength) / RootPhysicalLength - 1;
        // All candidate resources are fresh and end at inspected complete boundaries. Every complete orphan is checked.
        void ChargeRead(int count) {
            token.ThrowIfCancellationRequested();
            if (count > budget) { throw new BudgetExceededException("Qualification read budget exhausted."); }
            budget -= count;
        }
        using var a = RbfFile.OpenReadOnlyCandidate(FilePath(root, "a.rbf"), boundaries["a.rbf"],
            RbfCacheMode.Off, ChargeRead, token);
        using var b = RbfFile.OpenReadOnlyCandidate(FilePath(root, "b.rbf"), boundaries["b.rbf"],
            RbfCacheMode.Off, ChargeRead, token);
        using var roots = RbfFile.OpenReadOnlyCandidate(FilePath(root, "roots.rbf"), boundaries["roots.rbf"],
            RbfCacheMode.Off, ChargeRead, token);
        Guid aId = ReadIdentity(a), bId = ReadIdentity(b);
        long previousAGen = -1, previousBGen = -1;
        for (long offset = FirstDataOffset; offset < boundaries["a.rbf"]; offset += DataPhysicalLength) {
            token.ThrowIfCancellationRequested();
            long gen = ReadData(a, SizedPtr.Create(offset, DataTicketLength)).Generation;
            if (gen < previousAGen || gen > latest + 1) { throw new InvalidDataException("A generation contradicts writer order."); }
            if (offset > FirstDataOffset && gen == 0) { throw new InvalidDataException("A has an extra seed-generation record."); }
            previousAGen = gen;
        }
        for (long offset = FirstDataOffset; offset < boundaries["b.rbf"]; offset += DataPhysicalLength) {
            token.ThrowIfCancellationRequested();
            long gen = ReadData(b, SizedPtr.Create(offset, DataTicketLength)).Generation;
            if (gen < previousBGen || gen > latest + 1) { throw new InvalidDataException("B generation contradicts writer order."); }
            if (offset > FirstDataOffset && gen == 0) { throw new InvalidDataException("B has an extra seed-generation record."); }
            previousBGen = gen;
        }
        long previousA = -1, previousB = -1;
        for (long gen = 0; gen <= latest; gen++) {
            token.ThrowIfCancellationRequested();
            RootRecord record = ReadRootSlot(roots, gen);
            ValidateChildRef(record.A); ValidateChildRef(record.B);
            if (record.A.Identity != aId || record.B.Identity != bId ||
                record.A.VisibleEof > boundaries["a.rbf"] || record.B.VisibleEof > boundaries["b.rbf"]) {
                throw new InvalidDataException("Root references unavailable or wrong-identity child.");
            }
            if (record.A.Ticket.Offset <= previousA || record.B.Ticket.Offset <= previousB) {
                throw new InvalidDataException("Root reuses or reverses a child head.");
            }
            if (ReadData(a, record.A.Ticket).Generation != gen ||
                ReadData(b, record.B.Ticket).Generation != gen) {
                throw new InvalidDataException("Root selects a mixed generation.");
            }
            previousA = record.A.Ticket.Offset;
            previousB = record.B.Ticket.Offset;
        }
        if (shortFile is string file) {
            if (file == "roots.rbf") {
                if (ReadData(a, SizedPtr.Create(boundaries["a.rbf"] - DataPhysicalLength, DataTicketLength)).Generation != latest + 1 ||
                    ReadData(b, SizedPtr.Create(boundaries["b.rbf"] - DataPhysicalLength, DataTicketLength)).Generation != latest + 1) {
                    throw new InvalidDataException("Short root lacks the newly flushed dependency pair.");
                }
            }
            if (file == "b.rbf" &&
                ReadData(a, SizedPtr.Create(boundaries["a.rbf"] - DataPhysicalLength, DataTicketLength)).Generation != latest + 1) {
                throw new InvalidDataException("Short B suffix lacks its preceding complete A record.");
            }
            CheckFixedSuffix(FilePath(root, file), file, boundaries[file], lengths[file], latest, aId, bId,
                boundaries["a.rbf"], boundaries["b.rbf"], ChargeRead);
        }
        return new(lengths, boundaries, shortFile, latest, aId, bId);
    }

    private static void CheckFixedSuffix(string path, string name, long boundary, long length,
        long latestGeneration, Guid aId, Guid bId, long aBoundary, long bBoundary,
        Action<int>? beforeRead = null) {
        int expectedPhysical = name == "roots.rbf" ? RootPhysicalLength : DataPhysicalLength;
        long remaining = length - boundary;
        if (remaining <= 0 || remaining >= expectedPhysical) {
            throw new InvalidDataException("Suffix is not one short fixed record.");
        }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
        CheckFixedSuffix(stream, name, boundary, length, latestGeneration, aId, bId, aBoundary, bBoundary, beforeRead);
    }

    private static void CheckFixedSuffix(Stream stream, string name, long boundary, long length,
        long latestGeneration, Guid aId, Guid bId, long aBoundary, long bBoundary,
        Action<int>? beforeRead = null) {
        int expectedPhysical = name == "roots.rbf" ? RootPhysicalLength : DataPhysicalLength;
        long remaining = length - boundary;
        if (remaining <= 0 || remaining >= expectedPhysical) {
            throw new InvalidDataException("Suffix is not one short fixed record.");
        }
        byte[] suffix = new byte[checked((int)remaining)];
        stream.Position = boundary;
        int read = 0;
        while (read < suffix.Length) {
            int requested = suffix.Length - read;
            beforeRead?.Invoke(requested);
            int got = stream.Read(suffix.AsSpan(read));
            ProbeMeasurements.Current?.RecordRead(requested, got);
            if (got == 0) { throw new IOException("Short read during suffix qualification."); }
            read += got;
        }
        byte[] expectedHead = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(expectedHead, name == "roots.rbf" ? RootTicketLength : DataTicketLength);
        CheckKnownBytes(suffix, 0, expectedHead);
        byte[] generation = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(generation, checked(latestGeneration + 1));
        if (name == "roots.rbf") {
            if (aBoundary < FirstDataOffset + DataPhysicalLength || bBoundary < FirstDataOffset + DataPhysicalLength ||
                (aBoundary - FirstDataOffset) % DataPhysicalLength != 0 ||
                (bBoundary - FirstDataOffset) % DataPhysicalLength != 0) {
                throw new InvalidDataException("Short root has no fixed-grid last child records.");
            }
            byte[] expectedPayload = new byte[RootPayloadLength];
            RootMagic.CopyTo(expectedPayload);
            generation.CopyTo(expectedPayload.AsSpan(8));
            WriteChild(expectedPayload.AsSpan(16, 32),
                new(aId, SizedPtr.Create(aBoundary - DataPhysicalLength, DataTicketLength), aBoundary));
            WriteChild(expectedPayload.AsSpan(48, 32),
                new(bId, SizedPtr.Create(bBoundary - DataPhysicalLength, DataTicketLength), bBoundary));
            CheckKnownBytes(suffix, 4, expectedPayload);
            CheckKnownBytes(suffix, 92, new byte[4]); // No tail meta, padding, or tombstone.
            CheckKnownBytes(suffix, 96, BitConverter.GetBytes(RootTag));
            CheckKnownBytes(suffix, 100, expectedHead);
        }
        else {
            CheckKnownBytes(suffix, 4, DataMagic);
            CheckKnownBytes(suffix, 12, generation);
            CheckKnownBytes(suffix, 28, new byte[8]);
            CheckKnownBytes(suffix, 44, new byte[4]);
            CheckKnownBytes(suffix, 48, BitConverter.GetBytes(DataTag));
            CheckKnownBytes(suffix, 52, expectedHead);
        }
        byte[] synthetic = new byte[HeaderLength + suffix.Length];
        "RBF1"u8.CopyTo(synthetic);
        suffix.CopyTo(synthetic.AsSpan(HeaderLength));
        using var source = new MemoryStream(synthetic, writable: false);
        var inspection = RbfPrefixInspector.Inspect(source);
        if (inspection.Status != RbfPrefixStatus.IncompleteSuffix || inspection.LastCompleteBoundary != HeaderLength) {
            throw new InvalidDataException("Fixed-shape suffix is invalid or unresolved: " + inspection.Status);
        }
    }

    private static void CheckKnownBytes(ReadOnlySpan<byte> actual, int offset, ReadOnlySpan<byte> expected) {
        int available = Math.Clamp(actual.Length - offset, 0, expected.Length);
        if (available == 0) { return; }
        if (!actual.Slice(offset, available).SequenceEqual(expected[..available])) {
            throw new InvalidDataException("Short suffix contradicts fixed record shape.");
        }
    }

    internal static InventoryFile[] Inventory(string root) {
        root = Path.GetFullPath(root);
        using var repositoryLock = Lock(root);
        RequireInventory(root);
        var result = new List<InventoryFile>();
        foreach (string name in Files) {
            string path = FilePath(root, name);
            var physical = RbfPrefixInspector.Inspect(path);
            ProbeMeasurements.Current?.RecordReads(physical.ReadRequests, physical.RequestedBytes, physical.ReturnedBytes);
            if (physical.Status is not (RbfPrefixStatus.Complete or RbfPrefixStatus.IncompleteSuffix)) {
                throw new InvalidDataException(name + ": " + physical.Status);
            }
            long boundary = physical.LastCompleteBoundary;
            int stride = name == "roots.rbf" ? RootPhysicalLength : DataPhysicalLength;
            long first = name == "roots.rbf" ? HeaderLength : FirstDataOffset;
            if (boundary < first || (boundary - first) % stride != 0) {
                throw new InvalidDataException("Inventory complete prefix is outside fixed record grid: " + name);
            }
            var offsets = new List<long>();
            if (name != "roots.rbf") { offsets.Add(HeaderLength); }
            for (long offset = first; offset < boundary; offset += stride) { offsets.Add(offset); }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[64 * 1024];
            long left = boundary;
            while (left > 0) {
                int requested = (int)Math.Min(left, buffer.Length);
                int got = stream.Read(buffer, 0, requested);
                ProbeMeasurements.Current?.RecordRead(requested, got);
                if (got == 0) { throw new IOException("Short read while hashing complete prefix."); }
                sha.AppendData(buffer.AsSpan(0, got));
                left -= got;
            }
            result.Add(new(name, physical.OriginalLength, boundary,
                Convert.ToHexString(sha.GetHashAndReset()), offsets.ToArray()));
        }
        return result.ToArray();
    }
}
