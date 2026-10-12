using System.Buffers.Binary;
using System.Text;
using Atelia.Data;
using Atelia.EventJournal;
using Atelia.Rbf;
using Atelia.Rbf.Internal;

namespace Atelia.UnifiedRootProbe;

internal static partial class MetadataStore {
    private static void RequireInventory(string root) {
        string[] top = Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName)
            .Order(StringComparer.Ordinal).ToArray()!;
        string[] expected = ["allocation.rbf", "catalog.rbf", "events", "repository.lock", "roots.rbf"];
        if (!top.SequenceEqual(expected, StringComparer.Ordinal)) {
            throw new InvalidDataException("Unknown or missing repository entry.");
        }
        foreach (string name in expected) {
            FileAttributes attributes = System.IO.File.GetAttributes(File(root, name));
            bool directory = name == "events";
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                ((attributes & FileAttributes.Directory) != 0) != directory) {
                throw new InvalidDataException("Wrong repository entry type: " + name);
            }
        }
        string[] eventEntries = Directory.EnumerateFileSystemEntries(Events(root)).Select(Path.GetFileName).ToArray()!;
        if (eventEntries.Length != 1 || eventEntries[0] != "segments") {
            throw new InvalidDataException("Event layout must contain only flat segments.");
        }
        FileAttributes segmentDirectory = System.IO.File.GetAttributes(Path.Combine(Events(root), "segments"));
        if ((segmentDirectory & FileAttributes.Directory) == 0 ||
            (segmentDirectory & FileAttributes.ReparsePoint) != 0) {
            throw new InvalidDataException("Flat segment directory is not ordinary.");
        }
        string[] segments = Directory.EnumerateFileSystemEntries(Path.Combine(Events(root), "segments"))
            .Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;
        if (!(segments.SequenceEqual(["00000001.rbf"], StringComparer.Ordinal) ||
            segments.SequenceEqual(["00000001.rbf", "00000002.rbf"], StringComparer.Ordinal))) {
            throw new InvalidDataException("Unknown or missing event segment.");
        }
        foreach (string name in segments) {
            FileAttributes attributes = System.IO.File.GetAttributes(Path.Combine(Events(root), "segments", name));
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) {
                throw new InvalidDataException("Segment is not an ordinary file.");
            }
        }
    }

    private static MetadataRoot ReadSelectedRoot(string root, long? generation) {
        string path = File(root, "roots.rbf");
        long length = new FileInfo(path).Length;
        long full = (length - Header) / RootPhysicalLength;
        if (length < Header + RootPhysicalLength || full < 1) { throw new InvalidDataException("No complete root."); }
        long boundary = Header + full * RootPhysicalLength;
        if (length != boundary) { CheckRootSuffix(root, boundary, length, full); }
        long selected = generation ?? full - 1;
        if (selected < 0 || selected >= full) { throw new ArgumentOutOfRangeException(nameof(generation)); }
        using var file = RbfFile.OpenReadOnlyCandidate(path, Header + (selected + 1) * RootPhysicalLength, RbfCacheMode.Off);
        return ParseRoot(file, selected);
    }

    private static MetadataRoot ParseRoot(IRbfFile file, long generation) {
        SizedPtr ticket = SizedPtr.Create(Header + generation * RootPhysicalLength, RootTicketLength);
        using var frame = CheckedFrame(file, ticket);
        ReadOnlySpan<byte> bytes = frame.PayloadAndMeta;
        if (frame.Tag != RootTag || frame.IsTombstone || frame.TailMetaLength != 0 ||
            bytes.Length != RootPayloadLength || !bytes[..8].SequenceEqual(RootMagic) ||
            BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]) != generation ||
            bytes[40..48].ContainsAnyExcept((byte)0)) {
            throw new InvalidDataException("Root slot has invalid format or generation.");
        }
        var result = new MetadataRoot(generation,
            SizedPtr.FromPacked(BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..])),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[24..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[32..]));
        if (result.DirectoryTicket.Packed == 0 || result.DirectoryTicket.Offset < Header ||
            result.DirectoryTicket.Offset + result.DirectoryTicket.Length + 4 != result.DirectoryEof ||
            result.DirectoryEof < Header || result.AllocationEof < Header) {
            throw new InvalidDataException("Root directory or allocation frontier is invalid.");
        }
        return result;
    }

    private static MetadataDirectory ReadDirectory(string root, MetadataRoot selected) {
        string path = File(root, "catalog.rbf");
        if (new FileInfo(path).Length < selected.DirectoryEof) {
            throw new InvalidDataException("Selected catalog EOF is unavailable.");
        }
        using var catalog = RbfFile.OpenReadOnlyCandidate(path, selected.DirectoryEof, RbfCacheMode.Off);
        return ParseDirectory(catalog, selected.DirectoryTicket, selected.Generation);
    }

    private static MetadataDirectory ParseDirectory(IRbfFile file, SizedPtr ticket, long expectedGeneration) {
        using var frame = CheckedFrame(file, ticket);
        ReadOnlySpan<byte> bytes = frame.PayloadAndMeta;
        if (frame.Tag != DirectoryTag || frame.IsTombstone || frame.TailMetaLength != 0 ||
            bytes.Length < 40 || !bytes[..8].SequenceEqual(DirectoryMagic) ||
            (expectedGeneration >= 0 && BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]) != expectedGeneration)) {
            throw new InvalidDataException("Directory frame has bad shape or generation.");
        }
        uint active = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes[20..]);
        long first = BinaryPrimitives.ReadInt64LittleEndian(bytes[24..]);
        long second = BinaryPrimitives.ReadInt64LittleEndian(bytes[32..]);
        if (active is not (1 or 2) || count < 1 || count > 100000 || first < Header ||
            (active == 1 && second != 0) || (active == 2 && second < Header)) {
            throw new InvalidDataException("Directory segment view is invalid.");
        }
        var branches = new SortedDictionary<string, MetadataBranch>(StringComparer.Ordinal);
        var identities = new HashSet<RefId>();
        int pos = 40;
        for (int i = 0; i < count; i++) {
            if (pos >= bytes.Length) { throw new InvalidDataException("Directory entry truncated."); }
            int nameLength = bytes[pos++];
            if (nameLength < 1 || pos + nameLength + 24 > bytes.Length) {
                throw new InvalidDataException("Directory entry length invalid.");
            }
            string name = Encoding.UTF8.GetString(bytes.Slice(pos, nameLength));
            pos += nameLength;
            if (!ValidName(name) || Encoding.UTF8.GetByteCount(name) != nameLength) {
                throw new InvalidDataException("Directory name invalid.");
            }
            var id = new RefId(BinaryPrimitives.ReadUInt64LittleEndian(bytes[pos..]));
            var decodedAddress = EventAddressCodec.Decode(bytes.Slice(pos + 8, 16));
            if (decodedAddress.IsFailure) { throw new InvalidDataException("Directory EventAddress invalid: " + decodedAddress.Error); }
            EventAddress head = decodedAddress.Unwrap();
            pos += 24;
            if (id.IsDefault || !identities.Add(id) || !branches.TryAdd(name, new(id, head))) {
                throw new InvalidDataException("Directory name or identity alias.");
            }
        }
        if (pos != bytes.Length || !branches.ContainsKey("a")) {
            throw new InvalidDataException("Directory frame has surplus bytes or no A.");
        }
        return new(BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]), active, first, second, branches);
    }

    private static bool ValidName(string name) {
        if (name.Length is < 1 or > 128 || name.EndsWith(".", StringComparison.Ordinal) ||
            name.EndsWith(".lock", StringComparison.Ordinal)) { return false; }
        for (int i = 0; i < name.Length; i++) {
            char c = name[i];
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' ||
                i > 0 && c is '.' or '_' or '-')) { return false; }
        }
        return true;
    }

    private static MetadataEvent ReadSelectedBranch(string root, MetadataRoot selected,
        MetadataDirectory directory, string name, MetadataBranch branch) {
        long eventEof = branch.Head.SegmentNumber switch {
            1 => directory.Segment1Eof,
            2 when directory.ActiveSegment == 2 => directory.Segment2Eof,
            _ => throw new InvalidDataException("Branch head uses an invisible segment.")
        };
        if (branch.Head.Ticket.Offset + branch.Head.Ticket.Length + 4 > eventEof ||
            new FileInfo(Segment(root, branch.Head.SegmentNumber)).Length < eventEof) {
            throw new InvalidDataException("Branch head exceeds selected segment EOF.");
        }
        if (new FileInfo(File(root, "allocation.rbf")).Length < selected.AllocationEof) {
            throw new InvalidDataException("Selected allocation EOF is unavailable.");
        }
        SizedPtr allocationTicket = SizedPtr.FromPacked(branch.Id.Packed);
        if (allocationTicket.Offset < Header || allocationTicket.Offset + allocationTicket.Length + 4 > selected.AllocationEof) {
            throw new InvalidDataException("Branch identity outside selected allocation EOF.");
        }
        using var allocation = RbfFile.OpenReadOnlyCandidate(File(root, "allocation.rbf"),
            selected.AllocationEof, RbfCacheMode.Off);
        RefOpFrame create = ReadCreate(allocation, allocationTicket);
        if (create.BranchName != name) { throw new InvalidDataException("Branch name/identity mismatch."); }
        MetadataEvent head = ReadEvent(root, branch.Head, directory);
        EventAddress? parent = branch.Head;
        var visited = new HashSet<EventAddress>();
        bool foundStart = false;
        ulong previousSequence = ulong.MaxValue;
        while (parent is { } address) {
            if (!visited.Add(address)) { throw new InvalidDataException("Event parent cycle."); }
            MetadataEvent current = ReadEvent(root, address, directory);
            if (current.Header.SequenceNumber >= previousSequence) {
                throw new InvalidDataException("Event parent sequence is not decreasing.");
            }
            previousSequence = current.Header.SequenceNumber;
            if (address == create.StartHead) { foundStart = true; }
            parent = current.Header.Parent;
        }
        if (!foundStart) { throw new InvalidDataException("Branch head does not descend from Create start."); }
        return head;
    }

    private static MetadataEvent ReadEvent(string root, EventAddress address, MetadataDirectory view) {
        long eof = address.SegmentNumber switch {
            1 => view.Segment1Eof,
            2 when view.ActiveSegment == 2 => view.Segment2Eof,
            _ => throw new InvalidDataException("Event parent uses invisible segment.")
        };
        if (address.Ticket.Offset < Header || address.Ticket.Offset + address.Ticket.Length + 4 > eof ||
            address.Ticket.Length != EventTicketLength) {
            throw new InvalidDataException("Event address exceeds segment view or has wrong size.");
        }
        using var file = RbfFile.OpenReadOnlyCandidate(Segment(root, address.SegmentNumber), eof, RbfCacheMode.Off);
        return ReadEvent(file, address);
    }

    private static MetadataEvent ReadEvent(IRbfFile file, EventAddress address) {
        using var frame = CheckedFrame(file, address.Ticket);
        if (frame.Tag != Atelia.EventJournal.EventJournal.EventFrameTag || frame.IsTombstone ||
            frame.TailMetaLength != EventFrameHeaderCodec.FixedLength ||
            frame.PayloadAndMeta.Length != EventPayloadLength + EventFrameHeaderCodec.FixedLength) {
            throw new InvalidDataException("EventFrame shape invalid.");
        }
        ReadOnlySpan<byte> bytes = frame.PayloadAndMeta;
        var decodedHeader = EventFrameHeaderCodec.Decode(bytes[^EventFrameHeaderCodec.FixedLength..]);
        if (decodedHeader.IsFailure) { throw new InvalidDataException("EventFrame header invalid: " + decodedHeader.Error); }
        EventFrameHeader header = decodedHeader.Unwrap();
        if (header.PayloadCodecId != EventPayloadCodecId.Identity || header.PayloadLength != EventPayloadLength ||
            header.Hint != address.Hint || header.SequenceNumber == 0) {
            throw new InvalidDataException("EventFrame header contradicts candidate.");
        }
        return new(address, header, bytes[..EventPayloadLength].ToArray());
    }

    private static RefOpFrame ReadCreate(IRbfFile file, SizedPtr ticket) {
        using var frame = CheckedFrame(file, ticket);
        if (frame.Tag != Atelia.EventJournal.EventJournal.RefOpFrameTag || frame.IsTombstone || frame.TailMetaLength != 0) {
            throw new InvalidDataException("Allocation frame shape invalid.");
        }
        var decodedOp = RefOpFrameCodec.Decode(frame.PayloadAndMeta);
        if (decodedOp.IsFailure) { throw new InvalidDataException("Create frame invalid: " + decodedOp.Error); }
        RefOpFrame op = decodedOp.Unwrap();
        if (op.Operation != RefOpOperation.Create || !ValidName(op.BranchName) ||
            !op.RefId.IsDefault || !op.SourceRefId.IsDefault || op.SourceHead is not null ||
            op.SourceMoveSequenceNumber != 0 || op.StartHead is null || op.UtcUnixTimeMilliseconds != 0) {
            throw new InvalidDataException("Allocation Create semantics invalid.");
        }
        return op;
    }

    private static RbfPooledFrame CheckedFrame(IRbfFile file, SizedPtr ticket) {
        var result = file.ReadPooledFrame(ticket);
        if (result.IsFailure) { throw new InvalidDataException("RBF frame read failed: " + result.Error); }
        return result.Unwrap();
    }

    private static void CheckRootSuffix(string root, long boundary, long length, long nextGeneration) {
        long count = length - boundary;
        if (count <= 0 || count >= RootPhysicalLength) { throw new InvalidDataException("Root suffix size invalid."); }
        byte[] bytes = new byte[count];
        using var stream = new FileStream(File(root, "roots.rbf"), FileMode.Open, FileAccess.Read, FileShare.Read, 1);
        stream.Position = boundary;
        ReadExactMeasured(stream, bytes);
        long catalogLength = new FileInfo(File(root, "catalog.rbf")).Length;
        long allocationLength = new FileInfo(File(root, "allocation.rbf")).Length;
        SizedPtr nextTicket = LastTicketAtEof(File(root, "catalog.rbf"), catalogLength);
        var nextRoot = new MetadataRoot(nextGeneration, nextTicket, catalogLength, allocationLength);
        MetadataDirectory nextDirectory = ReadDirectory(root, nextRoot);
        if (nextDirectory.Generation != nextGeneration) {
            throw new InvalidDataException("Short root has no complete next directory.");
        }
        foreach (var (name, branch) in nextDirectory.Branches) {
            ReadSelectedBranch(root, nextRoot, nextDirectory, name, branch);
        }
        byte[] payload = EncodeRoot(nextRoot);
        Span<byte> head = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(head, RootTicketLength);
        CheckKnownBytes(bytes, 0, head);
        CheckKnownBytes(bytes, 4, payload);
        CheckKnownBytes(bytes, 60, new byte[4]);
        CheckKnownBytes(bytes, 64, BitConverter.GetBytes(RootTag));
        CheckKnownBytes(bytes, 68, head);
        byte[] image = new byte[Header + bytes.Length];
        "RBF1"u8.CopyTo(image);
        bytes.CopyTo(image.AsSpan(Header));
        using var source = new MemoryStream(image, writable: false);
        var inspection = RbfPrefixInspector.Inspect(source);
        if (inspection.Status != RbfPrefixStatus.IncompleteSuffix || inspection.LastCompleteBoundary != Header) {
            throw new InvalidDataException("Short root has invalid RBF prefix: " + inspection.Status);
        }
    }

    private static SizedPtr LastTicketAtEof(string path, long eof) {
        if (eof < Header + 28) { throw new InvalidDataException("No last complete directory."); }
        Span<byte> tail = stackalloc byte[8];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
        stream.Position = eof - 8;
        ReadExactMeasured(stream, tail);
        if (!tail[4..].SequenceEqual("RBF1"u8)) { throw new InvalidDataException("Last directory lacks Fence."); }
        int ticketLength = BinaryPrimitives.ReadInt32LittleEndian(tail);
        if (ticketLength < 24 || ticketLength > eof - Header - 4 || (ticketLength & 3) != 0) {
            throw new InvalidDataException("Last directory tail length invalid.");
        }
        return SizedPtr.Create(eof - ticketLength - 4, ticketLength);
    }

    private static void CheckKnownBytes(ReadOnlySpan<byte> actual, int offset, ReadOnlySpan<byte> expected) {
        int available = Math.Clamp(actual.Length - offset, 0, expected.Length);
        if (available > 0 && !actual.Slice(offset, available).SequenceEqual(expected[..available])) {
            throw new InvalidDataException("Short frame contradicts known writer bytes.");
        }
    }
}
