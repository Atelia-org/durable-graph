using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Atelia.Data;
using Atelia.EventJournal;
using Atelia.Rbf;
using Atelia.Rbf.Internal;
using Atelia.RbfSegmentStore;

namespace Atelia.UnifiedRootProbe;

internal sealed record MetadataBranchView(string Name, string RefId, EventAddress Head,
    ulong Sequence, string PayloadHex);
internal sealed record MetadataView(long Generation, uint ActiveSegment, long Segment1Eof,
    long Segment2Eof, long AllocationEof, MetadataBranchView[] Branches);
internal sealed record MetadataRecoveryResult(string Status, string Detail, string? Action,
    MetadataView? View, bool ActionAttempted, bool LengthChanged, bool BarrierCompleted);
internal enum MetadataDurabilityMode { ProcessCrashOnly, PowerLossRequired }

internal readonly record struct MetadataBranch(RefId Id, EventAddress Head);
internal sealed record MetadataDirectory(long Generation, uint ActiveSegment, long Segment1Eof,
    long Segment2Eof, SortedDictionary<string, MetadataBranch> Branches);
internal readonly record struct MetadataRoot(long Generation, SizedPtr DirectoryTicket,
    long DirectoryEof, long AllocationEof);
internal readonly record struct MetadataEvent(EventAddress Address, EventFrameHeader Header,
    byte[] Payload);

/// <summary>Isolated process-crash experiment. The allocation.rbf RefId domain is not an EventJournal directory.</summary>
internal static partial class MetadataStore {
    private const int Header = 4;
    private const int EventPayloadLength = 32;
    private const int EventTicketLength = EventPayloadLength + EventFrameHeaderCodec.FixedLength + 24;
    private const int RootPayloadLength = 48;
    private const int RootTicketLength = RootPayloadLength + 24;
    private const int RootPhysicalLength = RootTicketLength + 4;
    private const uint DirectoryTag = 0x3144_5255; // URD1
    private const uint RootTag = 0x3152_5255; // URR1
    private static ReadOnlySpan<byte> DirectoryMagic => "URDIR001"u8;
    private static ReadOnlySpan<byte> RootMagic => "URROOT01"u8;
    private static readonly string[] BaseFiles = ["allocation.rbf", "catalog.rbf", "roots.rbf"];

    private static string File(string root, string name) => Path.Combine(root, name);
    private static string Events(string root) => Path.Combine(root, "events");
    private static string Segment(string root, uint number) =>
        RbfSegmentPath.GetSegmentPath(Events(root), RbfSegmentStoreLayout.Flat, number);
    private static FileStream Lock(string root) => new(File(root, "repository.lock"),
        FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    internal static MetadataView Seed(string root, int unrelatedBranches = 0, int history = 0,
        MetadataDurabilityMode durability = MetadataDurabilityMode.ProcessCrashOnly) {
        RequireProcessCrashOnly(durability);
        ArgumentOutOfRangeException.ThrowIfNegative(unrelatedBranches);
        ArgumentOutOfRangeException.ThrowIfNegative(history);
        root = Path.GetFullPath(root);
        if (Directory.Exists(root)) { throw new IOException("Seed requires a new fixture directory."); }
        Directory.CreateDirectory(root);
        using var repositoryLock = new FileStream(File(root, "repository.lock"), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None);
        Directory.CreateDirectory(Path.GetDirectoryName(Segment(root, 1))!);
        using var allocation = RbfFile.CreateNew(File(root, "allocation.rbf"), RbfCacheMode.Off);
        using var catalog = RbfFile.CreateNew(File(root, "catalog.rbf"), RbfCacheMode.Off);
        using var roots = RbfFile.CreateNew(File(root, "roots.rbf"), RbfCacheMode.Off);
        using var segment = RbfFile.CreateNew(Segment(root, 1), RbfCacheMode.Off);
        var branches = new SortedDictionary<string, MetadataBranch>(StringComparer.Ordinal);
        ulong sequence = 1;
        EventAddress aHead = AppendEvent(segment, 1, sequence, null, 0x41);
        Flush(segment);
        RefId aId = AppendCreate(allocation, "a", aHead);
        Flush(allocation);
        branches.Add("a", new(aId, aHead));
        MetadataDirectory directory = new(0, 1, segment.TailOffset, 0, branches);
        SizedPtr ticket = AppendDirectory(catalog, directory);
        Flush(catalog);
        AppendRoot(roots, new(0, ticket, catalog.TailOffset, allocation.TailOffset));
        Flush(roots);
        // Additional fixture work uses the same append-only root publication route.
        for (int i = 0; i < history; i++) {
            ++sequence;
            aHead = AppendEvent(segment, 1, sequence, aHead, 0x48);
            Flush(segment);
            branches["a"] = new(aId, aHead);
            directory = new(directory.Generation + 1, 1, segment.TailOffset, 0,
                new(branches, StringComparer.Ordinal));
            ticket = AppendDirectory(catalog, directory);
            Flush(catalog);
            AppendRoot(roots, new(directory.Generation, ticket, catalog.TailOffset, allocation.TailOffset));
            Flush(roots);
        }
        for (int i = 0; i < unrelatedBranches; i++) {
            string name = "u" + i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            RefId id = AppendCreate(allocation, name, aHead);
            Flush(allocation);
            branches.Add(name, new(id, aHead));
            directory = new(directory.Generation + 1, 1, segment.TailOffset, 0,
                new(branches, StringComparer.Ordinal));
            ticket = AppendDirectory(catalog, directory);
            Flush(catalog);
            AppendRoot(roots, new(directory.Generation, ticket, catalog.TailOffset, allocation.TailOffset));
            Flush(roots);
        }
        // No experiment claims a directory durability barrier for these new names.
        return new(directory.Generation, directory.ActiveSegment, directory.Segment1Eof,
            directory.Segment2Eof, allocation.TailOffset,
            branches.Select(pair => new MetadataBranchView(pair.Key, pair.Value.Id.ToString(),
                pair.Value.Head, pair.Key == "a" ? sequence : sequence,
                Convert.ToHexString(Enumerable.Repeat(pair.Key == "a" && history > 0 ? (byte)0x48 : (byte)0x41,
                    EventPayloadLength).ToArray()))).ToArray());
    }

    internal static MetadataReader Open(string root, long? generation = null) {
        root = Path.GetFullPath(root);
        var held = Lock(root);
        try {
            RequireInventory(root);
            MetadataRoot selected = ReadSelectedRoot(root, generation);
            MetadataDirectory directory = ReadDirectory(root, selected);
            return new MetadataReader(root, held, selected, directory);
        }
        catch { held.Dispose(); throw; }
    }

    internal sealed class MetadataReader(string root, FileStream held,
        MetadataRoot selected, MetadataDirectory directory) : IDisposable {
        private bool _disposed;
        internal MetadataBranchView Read(string branchName) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!directory.Branches.TryGetValue(branchName, out MetadataBranch branch)) {
                throw new KeyNotFoundException("Branch not in selected root: " + branchName);
            }
            MetadataEvent member = ReadSelectedBranch(root, selected, directory, branchName, branch);
            return BranchView(branchName, branch, member);
        }
        internal MetadataView ReadAll() {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var result = new List<MetadataBranchView>(directory.Branches.Count);
            foreach (var (name, branch) in directory.Branches) {
                result.Add(BranchView(name, branch, ReadSelectedBranch(root, selected, directory, name, branch)));
            }
            return new(directory.Generation, directory.ActiveSegment, directory.Segment1Eof,
                directory.Segment2Eof, selected.AllocationEof, result.ToArray());
        }
        public void Dispose() { if (_disposed) { return; } _disposed = true; held.Dispose(); }
    }

    internal static MetadataView CreateB(string root, Action<string>? checkpoint = null,
        MetadataDurabilityMode durability = MetadataDurabilityMode.ProcessCrashOnly) =>
        Commit(root, create: true, checkpoint, durability);
    internal static MetadataView AdvanceB(string root, Action<string>? checkpoint = null,
        MetadataDurabilityMode durability = MetadataDurabilityMode.ProcessCrashOnly) =>
        Commit(root, create: false, checkpoint, durability);

    private static MetadataView Commit(string root, bool create, Action<string>? checkpoint,
        MetadataDurabilityMode durability) {
        RequireProcessCrashOnly(durability);
        root = Path.GetFullPath(root);
        using var held = Lock(root);
        RequireInventory(root);
        MetadataQualification qualification = Qualify(root, long.MaxValue, default);
        if (qualification.ShortFile is not null || qualification.HeaderAction) {
            throw new InvalidDataException("A short suffix requires offline recovery.");
        }
        MetadataRoot oldRoot = ReadSelectedRoot(root, null);
        MetadataDirectory old = ReadDirectory(root, oldRoot);
        if (create == old.Branches.ContainsKey("b")) {
            throw new InvalidOperationException(create ? "B already exists." : "B is absent.");
        }
        ulong sequence = checked(qualification.MaxSequence + 1);
        var branches = new SortedDictionary<string, MetadataBranch>(old.Branches, StringComparer.Ordinal);
        EventAddress parent = create ? branches["a"].Head : branches["b"].Head;
        uint active = old.ActiveSegment;
        // The first B event uses the first segment's captured tail as its threshold.
        // A surviving unselected second segment is adopted after qualification.
        bool rotate = create && active == 1;
        if (rotate) { active = 2; }
        using var allocation = RbfFile.OpenExisting(File(root, "allocation.rbf"), RbfCacheMode.Off);
        using var catalog = RbfFile.OpenExisting(File(root, "catalog.rbf"), RbfCacheMode.Off);
        using var roots = RbfFile.OpenExisting(File(root, "roots.rbf"), RbfCacheMode.Off);
        using var first = RbfFile.OpenExisting(Segment(root, 1), RbfCacheMode.Off);
        if (first.TailOffset != qualification.Boundaries["00000001.rbf"] ||
            allocation.TailOffset != qualification.Boundaries["allocation.rbf"] ||
            catalog.TailOffset != qualification.Boundaries["catalog.rbf"] ||
            roots.TailOffset != qualification.Boundaries["roots.rbf"]) {
            throw new IOException("Physical length changed after qualification.");
        }
        // Reconfirm existing dependencies, including complete orphans, before new writes.
        Flush(first); Flush(allocation); Flush(catalog);
        IRbfFile? second = null;
        try {
            if (active == 2) {
                string path = Segment(root, 2);
                second = System.IO.File.Exists(path)
                    ? RbfFile.OpenExisting(path, RbfCacheMode.Off)
                    : RbfFile.CreateNew(path, RbfCacheMode.Off);
                if (qualification.Boundaries.TryGetValue("00000002.rbf", out long qualifiedSecond) &&
                    second.TailOffset != qualifiedSecond) { throw new IOException("Second segment length changed."); }
                checkpoint?.Invoke("AfterSegmentCreated");
                Flush(second);
                Flush(roots);
                checkpoint?.Invoke("AfterSegmentHeaderFlush");
            }
            else { Flush(roots); }
            IRbfFile eventWriter = active == 2 ? second! : first;
            EventAddress head = AppendEvent(eventWriter, active, sequence, parent, create ? (byte)0x42 : (byte)0x43);
            checkpoint?.Invoke("AfterEventAppend");
            Flush(eventWriter); checkpoint?.Invoke("AfterEventFlush");
            RefId id = create ? AppendCreate(allocation, "b", head) : branches["b"].Id;
            if (create) { checkpoint?.Invoke("AfterAllocationAppend"); }
            Flush(allocation); if (create) { checkpoint?.Invoke("AfterAllocationFlush"); }
            branches["b"] = new(id, head);
            MetadataDirectory next = new(checked(old.Generation + 1), active,
                first.TailOffset, active == 2 ? second!.TailOffset : 0, branches);
            SizedPtr directoryTicket = AppendDirectory(catalog, next);
            checkpoint?.Invoke("AfterDirectoryAppend");
            Flush(catalog); checkpoint?.Invoke("AfterDirectoryFlush");
            AppendRoot(roots, new(next.Generation, directoryTicket, catalog.TailOffset, allocation.TailOffset));
            checkpoint?.Invoke("AfterRootAppend");
            Flush(roots); checkpoint?.Invoke("AfterRootFlush");
            checkpoint?.Invoke("BeforeAck");
            MetadataBranchView[] resultBranches = branches.Select(pair => pair.Key == "b"
                ? new MetadataBranchView("b", pair.Value.Id.ToString(), head, sequence,
                    Convert.ToHexString(Enumerable.Repeat(create ? (byte)0x42 : (byte)0x43,
                        EventPayloadLength).ToArray()))
                : BranchView(pair.Key, pair.Value, qualification.Events[pair.Value.Head])).ToArray();
            return new(next.Generation, next.ActiveSegment, next.Segment1Eof,
                next.Segment2Eof, allocation.TailOffset, resultBranches);
        }
        finally { second?.Dispose(); }
    }

    internal static void PopulateOrphans(string root, int count) {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        root = Path.GetFullPath(root);
        using var held = Lock(root);
        MetadataQualification q = Qualify(root, long.MaxValue, default);
        if (q.ShortFile is not null || q.HeaderAction) { throw new InvalidDataException("Recover first."); }
        MetadataDirectory selected = ReadDirectory(root, ReadSelectedRoot(root, null));
        uint active = selected.ActiveSegment;
        if (active == 1 && q.Boundaries.ContainsKey("00000002.rbf")) {
            throw new InvalidOperationException("Orphan setup is limited to the selected physical active segment; continue B first.");
        }
        using var writer = RbfFile.OpenExisting(Segment(root, active), RbfCacheMode.Off);
        ulong sequence = q.MaxSequence;
        for (int i = 0; i < count; i++) {
            AppendEvent(writer, active, checked(++sequence), selected.Branches["a"].Head, 0x52);
            Flush(writer);
        }
    }

    private static EventAddress AppendEvent(IRbfFile file, uint segment, ulong sequence,
        EventAddress? parent, byte fill) {
        byte[] payload = new byte[EventPayloadLength];
        Array.Fill(payload, fill);
        var header = new EventFrameHeader(EventPayloadCodecId.Identity, sequence, 0, 0,
            AddressHint.None, EventPayloadLength, parent);
        byte[] meta = new byte[EventFrameHeaderCodec.FixedLength];
        EventFrameHeaderCodec.Encode(in header, meta);
        SizedPtr ticket = file.Append(Atelia.EventJournal.EventJournal.EventFrameTag, payload, meta).Unwrap();
        ProbeMeasurements.Current?.RecordWrite(ticket.Length + 4);
        if (ticket.Length != EventTicketLength) { throw new InvalidDataException("Unexpected EventFrame size."); }
        return new(ticket, segment, AddressHint.None);
    }

    private static RefId AppendCreate(IRbfFile file, string name, EventAddress start) {
        var op = new RefOpFrame(RefOpOperation.Create, name, default, default, 0, null,
            start, 0, 0);
        byte[] payload = RefOpFrameCodec.Encode(in op);
        SizedPtr ticket = file.Append(Atelia.EventJournal.EventJournal.RefOpFrameTag, payload).Unwrap();
        ProbeMeasurements.Current?.RecordWrite(ticket.Length + 4);
        return new RefId(ticket.Packed);
    }

    private static SizedPtr AppendDirectory(IRbfFile file, MetadataDirectory directory) {
        byte[] payload = EncodeDirectory(directory);
        SizedPtr ticket = file.Append(DirectoryTag, payload).Unwrap();
        ProbeMeasurements.Current?.RecordWrite(ticket.Length + 4);
        return ticket;
    }

    private static SizedPtr AppendRoot(IRbfFile file, MetadataRoot root) {
        byte[] payload = EncodeRoot(root);
        SizedPtr ticket = file.Append(RootTag, payload).Unwrap();
        ProbeMeasurements.Current?.RecordWrite(ticket.Length + 4);
        if (ticket.Length != RootTicketLength ||
            ticket.Offset != Header + root.Generation * RootPhysicalLength) {
            throw new InvalidDataException("Root slot was not fixed.");
        }
        return ticket;
    }

    private static byte[] EncodeRoot(MetadataRoot root) {
        byte[] result = new byte[RootPayloadLength];
        RootMagic.CopyTo(result);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(8), root.Generation);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(16), root.DirectoryTicket.Packed);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(24), root.DirectoryEof);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(32), root.AllocationEof);
        return result;
    }

    private static byte[] EncodeDirectory(MetadataDirectory directory) {
        int length = 40;
        foreach (var (name, _) in directory.Branches) { length = checked(length + 1 + Encoding.UTF8.GetByteCount(name) + 24); }
        byte[] bytes = new byte[length];
        DirectoryMagic.CopyTo(bytes);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), directory.Generation);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), directory.ActiveSegment);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), directory.Branches.Count);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), directory.Segment1Eof);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32), directory.Segment2Eof);
        int offset = 40;
        foreach (var (name, branch) in directory.Branches) {
            int count = Encoding.UTF8.GetBytes(name, bytes.AsSpan(offset + 1));
            bytes[offset] = checked((byte)count);
            offset += 1 + count;
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), branch.Id.Packed);
            EventAddressCodec.Encode(branch.Head, bytes.AsSpan(offset + 8));
            offset += 24;
        }
        return bytes;
    }

    private static void Flush(IRbfFile file) {
        long start = Stopwatch.GetTimestamp();
        file.DurableFlush();
        ProbeMeasurements.Current?.RecordFlush(Stopwatch.GetTimestamp() - start);
    }

    private static void RequireProcessCrashOnly(MetadataDurabilityMode durability) {
        if (durability == MetadataDurabilityMode.PowerLossRequired) {
            throw new NotSupportedException("NamesBarrierNotProvided: this fixture only supports ProcessCrashOnly.");
        }
        if (durability != MetadataDurabilityMode.ProcessCrashOnly) {
            throw new ArgumentOutOfRangeException(nameof(durability));
        }
    }

    private static MetadataBranchView BranchView(string name, MetadataBranch branch, MetadataEvent member) =>
        new(name, branch.Id.ToString(), branch.Head, member.Header.SequenceNumber,
            Convert.ToHexString(member.Payload));

    private static MetadataView View(string root, MetadataDirectory directory, long allocationEof) {
        var branches = new List<MetadataBranchView>();
        foreach (var (name, branch) in directory.Branches) {
            MetadataRoot selected = new(directory.Generation, default, 0, allocationEof);
            branches.Add(BranchView(name, branch, ReadSelectedBranch(root, selected, directory, name, branch)));
        }
        return new(directory.Generation, directory.ActiveSegment, directory.Segment1Eof,
            directory.Segment2Eof, allocationEof, branches.ToArray());
    }
}
