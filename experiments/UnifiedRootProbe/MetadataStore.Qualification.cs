using System.Buffers.Binary;
using System.Text;
using Atelia.Data;
using Atelia.Data.Hashing;
using Atelia.EventJournal;
using Atelia.Rbf;
using Atelia.Rbf.Internal;

namespace Atelia.UnifiedRootProbe;

internal static partial class MetadataStore {
    private sealed class MetadataBudgetExceededException(string message) : Exception(message);
    private sealed class MetadataUnresolvedException(string message) : Exception(message);
    private sealed record MetadataQualification(
        Dictionary<string, long> Lengths, Dictionary<string, long> Boundaries,
        string? ShortFile, bool HeaderAction, ulong MaxSequence,
        Dictionary<EventAddress, MetadataEvent> Events,
        Dictionary<RefId, RefOpFrame> Allocations,
        Dictionary<SizedPtr, MetadataDirectory> Directories,
        MetadataRoot LatestRoot);

    private static IEnumerable<string> PresentFiles(string root) {
        yield return "00000001.rbf";
        if (System.IO.File.Exists(Segment(root, 2))) { yield return "00000002.rbf"; }
        foreach (string name in BaseFiles) { yield return name; }
    }

    private static string PathFor(string root, string name) => name switch {
        "00000001.rbf" => Segment(root, 1),
        "00000002.rbf" => Segment(root, 2),
        _ => File(root, name)
    };

    private static MetadataQualification Qualify(string root, long budget, CancellationToken token) {
        ArgumentOutOfRangeException.ThrowIfNegative(budget);
        RequireInventory(root);
        var lengths = new Dictionary<string, long>(StringComparer.Ordinal);
        var boundaries = new Dictionary<string, long>(StringComparer.Ordinal);
        string? shortFile = null;
        bool headerAction = false;
        void Charge(int bytes) {
            token.ThrowIfCancellationRequested();
            if (bytes < 0 || bytes > budget) { throw new MetadataBudgetExceededException("Qualification read budget exhausted."); }
            budget -= bytes;
        }
        foreach (string name in PresentFiles(root)) {
            token.ThrowIfCancellationRequested();
            var inspection = RbfPrefixInspector.Inspect(PathFor(root, name), budget, token);
            ProbeMeasurements.Current?.RecordReads(inspection.ReadRequests, inspection.RequestedBytes, inspection.ReturnedBytes);
            budget -= inspection.RequestedBytes;
            if (inspection.Status == RbfPrefixStatus.BudgetExceeded) { throw new MetadataBudgetExceededException(name + ": " + inspection.Detail); }
            if (inspection.Status == RbfPrefixStatus.Cancelled) { throw new OperationCanceledException(token); }
            if (inspection.Status == RbfPrefixStatus.IoFailure) { throw new IOException(name + ": " + inspection.Detail); }
            if (inspection.Status == RbfPrefixStatus.Unresolved) {
                throw new MetadataUnresolvedException(name + ": " + inspection.Detail);
            }
            if (inspection.Status == RbfPrefixStatus.IncompleteHeader && name == "00000002.rbf") {
                headerAction = true;
                CheckSecondHeaderPrefix(root, inspection.OriginalLength, Charge);
            }
            else if (inspection.Status is not (RbfPrefixStatus.Complete or RbfPrefixStatus.IncompleteSuffix)) {
                throw new InvalidDataException(name + ": " + inspection.Status + ": " + inspection.Detail);
            }
            if (inspection.Status is RbfPrefixStatus.IncompleteSuffix or RbfPrefixStatus.IncompleteHeader) {
                if (shortFile is not null) { throw new InvalidDataException("More than one short physical suffix."); }
                shortFile = name;
            }
            lengths.Add(name, inspection.OriginalLength);
            boundaries.Add(name, inspection.LastCompleteBoundary);
        }
        if (boundaries["00000001.rbf"] < Header || boundaries["allocation.rbf"] < Header ||
            boundaries["catalog.rbf"] < Header || boundaries["roots.rbf"] < Header + RootPhysicalLength ||
            (boundaries["roots.rbf"] - Header) % RootPhysicalLength != 0) {
            throw new InvalidDataException("Missing complete seed or root grid invalid.");
        }
        var events = new Dictionary<EventAddress, MetadataEvent>();
        ulong sequence = 0;
        foreach (uint number in new uint[] { 1, 2 }) {
            string name = number == 1 ? "00000001.rbf" : "00000002.rbf";
            if (!boundaries.TryGetValue(name, out long end) || end <= Header) { continue; }
            using var file = RbfFile.OpenReadOnlyCandidate(Segment(root, number), end,
                RbfCacheMode.Off, Charge, token);
            foreach (SizedPtr ticket in FrameTickets(Segment(root, number), end, Charge, token)) {
                token.ThrowIfCancellationRequested();
                MetadataEvent item = ReadEvent(file, new(ticket, number, AddressHint.None));
                if (item.Header.SequenceNumber != checked(sequence + 1)) {
                    throw new InvalidDataException("Physical EventFrame sequence is not consecutive.");
                }
                if (item.Header.Parent is { } parent &&
                    (!events.TryGetValue(parent, out MetadataEvent prior) ||
                     prior.Header.SequenceNumber >= item.Header.SequenceNumber)) {
                    throw new InvalidDataException("EventFrame parent is not an earlier physical member.");
                }
                sequence = item.Header.SequenceNumber;
                events.Add(item.Address, item);
            }
        }
        if (sequence == 0) { throw new InvalidDataException("Seed EventFrame absent."); }
        var allocations = new Dictionary<RefId, RefOpFrame>();
        using (var file = RbfFile.OpenReadOnlyCandidate(File(root, "allocation.rbf"),
            boundaries["allocation.rbf"], RbfCacheMode.Off, Charge, token)) {
            foreach (SizedPtr ticket in FrameTickets(File(root, "allocation.rbf"), boundaries["allocation.rbf"], Charge, token)) {
                RefOpFrame create = ReadCreate(file, ticket);
                if (!events.ContainsKey(create.StartHead!.Value)) {
                    throw new InvalidDataException("Complete Create references absent EventFrame.");
                }
                allocations.Add(new RefId(ticket.Packed), create);
            }
        }
        var directories = new Dictionary<SizedPtr, MetadataDirectory>();
        using (var file = RbfFile.OpenReadOnlyCandidate(File(root, "catalog.rbf"),
            boundaries["catalog.rbf"], RbfCacheMode.Off, Charge, token)) {
            foreach (SizedPtr ticket in FrameTickets(File(root, "catalog.rbf"), boundaries["catalog.rbf"], Charge, token)) {
                MetadataDirectory directory = ParseDirectory(file, ticket, -1);
                ValidateDirectory(directory, boundaries, events, allocations);
                directories.Add(ticket, directory);
            }
        }
        long rootCount = (boundaries["roots.rbf"] - Header) / RootPhysicalLength;
        MetadataRoot latest = default;
        MetadataDirectory? previousDirectory = null;
        var publishedCatalogFrontiers = new List<long>();
        var publishedDirectories = new List<MetadataDirectory>();
        using (var file = RbfFile.OpenReadOnlyCandidate(File(root, "roots.rbf"),
            boundaries["roots.rbf"], RbfCacheMode.Off, Charge, token)) {
            for (long generation = 0; generation < rootCount; generation++) {
                token.ThrowIfCancellationRequested();
                MetadataRoot item = ParseRoot(file, generation);
                if (!directories.TryGetValue(item.DirectoryTicket, out MetadataDirectory? directory) ||
                    directory.Generation != generation ||
                    item.DirectoryTicket.Offset + item.DirectoryTicket.Length + 4 != item.DirectoryEof ||
                    item.DirectoryEof > boundaries["catalog.rbf"] ||
                    item.AllocationEof > boundaries["allocation.rbf"] ||
                    item.AllocationEof != Header &&
                    !allocations.Keys.Any(id => {
                        SizedPtr ptr = SizedPtr.FromPacked(id.Packed);
                        return ptr.Offset + ptr.Length + 4 == item.AllocationEof;
                    })) {
                    throw new InvalidDataException("Complete root selects invalid directory or EOF.");
                }
                ValidateDirectory(directory, boundaries, events, allocations, item);
                if (previousDirectory is not null &&
                    (item.DirectoryEof <= latest.DirectoryEof || item.AllocationEof < latest.AllocationEof)) {
                    throw new InvalidDataException("Root dependency frontiers regress.");
                }
                previousDirectory = directory;
                latest = item;
                publishedCatalogFrontiers.Add(item.DirectoryEof);
                publishedDirectories.Add(directory);
            }
        }
        if (previousDirectory is null || !previousDirectory.Branches.ContainsKey("a")) {
            throw new InvalidDataException("Seed root absent.");
        }
        // A complete directory is a candidate for the next root until that root
        // selects one. Failed candidates may share the same generation, but no
        // writer can append an older or arbitrarily future generation in an
        // interval bounded by two published catalog frontiers.
        int publishedIndex = 0;
        foreach (var (ticket, directory) in directories.OrderBy(entry => entry.Key.Offset)) {
            long frameEnd = ticket.Offset + ticket.Length + 4;
            while (publishedIndex < publishedCatalogFrontiers.Count &&
                frameEnd > publishedCatalogFrontiers[publishedIndex]) {
                publishedIndex++;
            }
            long expectedGeneration = publishedIndex;
            if (directory.Generation != expectedGeneration) {
                throw new InvalidDataException("Directory generation contradicts publication interval.");
            }
            if (expectedGeneration > 0) {
                ValidateDirectoryTransition(publishedDirectories[checked((int)(expectedGeneration - 1))],
                    directory, events);
            }
        }
        if (headerAction && previousDirectory.ActiveSegment == 2) {
            throw new InvalidDataException("Complete root refers to unfinished second segment header.");
        }
        if (shortFile is not null && !headerAction) {
            CheckShortFrame(root, shortFile, lengths[shortFile], boundaries[shortFile],
                latest, previousDirectory, sequence, events, allocations, directories, Charge);
        }
        return new(lengths, boundaries, shortFile, headerAction, sequence, events,
            allocations, directories, latest);
    }

    private static IEnumerable<SizedPtr> FrameTickets(string path, long boundary,
        Action<int> charge, CancellationToken token) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
        for (long offset = Header; offset < boundary;) {
            token.ThrowIfCancellationRequested();
            Span<byte> head = stackalloc byte[4];
            stream.Position = offset;
            ReadExactMeasured(stream, head, charge);
            int ticketLength = BinaryPrimitives.ReadInt32LittleEndian(head);
            if (ticketLength < 24 || (ticketLength & 3) != 0 || offset + ticketLength + 4 > boundary) {
                throw new InvalidDataException("Frame grid contradicts complete prefix.");
            }
            yield return SizedPtr.Create(offset, ticketLength);
            offset += ticketLength + 4;
        }
    }

    private static void ValidateDirectory(MetadataDirectory directory,
        Dictionary<string, long> boundaries, Dictionary<EventAddress, MetadataEvent> events,
        Dictionary<RefId, RefOpFrame> allocations, MetadataRoot? selected = null) {
        if (directory.Generation < 0 || directory.Segment1Eof > boundaries["00000001.rbf"] ||
            directory.Segment1Eof < Header || directory.Segment2Eof < 0 ||
            directory.ActiveSegment == 2 &&
            (!boundaries.TryGetValue("00000002.rbf", out long secondBoundary) ||
             directory.Segment2Eof > secondBoundary)) {
            throw new InvalidDataException("Directory segment range unavailable.");
        }
        bool EventBoundary(uint segment, long eof) => eof == Header ||
            events.Keys.Any(e => e.SegmentNumber == segment &&
                e.Ticket.Offset + e.Ticket.Length + 4 == eof);
        if (!EventBoundary(1, directory.Segment1Eof) ||
            directory.ActiveSegment == 2 && !EventBoundary(2, directory.Segment2Eof)) {
            throw new InvalidDataException("Directory segment EOF is not a complete physical boundary.");
        }
        if (selected is { } root &&
            (directory.Generation != root.Generation || root.AllocationEof > boundaries["allocation.rbf"])) {
            throw new InvalidDataException("Directory does not match selected root.");
        }
        foreach (var (name, branch) in directory.Branches) {
            SizedPtr allocationTicket = SizedPtr.FromPacked(branch.Id.Packed);
            if (!allocations.TryGetValue(branch.Id, out RefOpFrame create) || create.BranchName != name ||
                selected is { } selectedRoot &&
                allocationTicket.Offset + allocationTicket.Length + 4 > selectedRoot.AllocationEof ||
                !events.ContainsKey(branch.Head) || !events.ContainsKey(create.StartHead!.Value)) {
                throw new InvalidDataException("Directory branch identity or event member invalid.");
            }
            if (!Within(directory, branch.Head) || !Within(directory, create.StartHead!.Value) ||
                !IsAncestor(create.StartHead.Value, branch.Head, events)) {
                throw new InvalidDataException("Directory branch head/start outside view or ancestry.");
            }
            EventAddress? ancestor = branch.Head;
            var seen = new HashSet<EventAddress>();
            while (ancestor is { } address) {
                if (!seen.Add(address) || !Within(directory, address) ||
                    !events.TryGetValue(address, out MetadataEvent member)) {
                    throw new InvalidDataException("Directory parent closure leaves its fixed segment view.");
                }
                ancestor = member.Header.Parent;
            }
        }
    }

    private static bool Within(MetadataDirectory directory, EventAddress address) =>
        address.SegmentNumber switch {
            1 => address.Ticket.Offset + address.Ticket.Length + 4 <= directory.Segment1Eof,
            2 when directory.ActiveSegment == 2 =>
                address.Ticket.Offset + address.Ticket.Length + 4 <= directory.Segment2Eof,
            _ => false
        };

    private static bool IsAncestor(EventAddress ancestor, EventAddress head,
        Dictionary<EventAddress, MetadataEvent> events) {
        EventAddress? current = head;
        var visited = new HashSet<EventAddress>();
        while (current is { } item && visited.Add(item) && events.TryGetValue(item, out MetadataEvent frame)) {
            if (item == ancestor) { return true; }
            current = frame.Header.Parent;
        }
        return false;
    }

    private static void ValidateDirectoryTransition(MetadataDirectory prior,
        MetadataDirectory next, Dictionary<EventAddress, MetadataEvent> events) {
        if (next.Generation != prior.Generation + 1 ||
            next.ActiveSegment < prior.ActiveSegment ||
            next.Segment1Eof < prior.Segment1Eof ||
            next.Segment2Eof < prior.Segment2Eof ||
            next.Branches.Count < prior.Branches.Count) {
            throw new InvalidDataException("Directory branch transition invalid.");
        }
        foreach (var (name, oldBranch) in prior.Branches) {
            if (!next.Branches.TryGetValue(name, out MetadataBranch newBranch) ||
                newBranch.Id != oldBranch.Id ||
                !IsAncestor(oldBranch.Head, newBranch.Head, events)) {
                throw new InvalidDataException("Directory branch transition invalid.");
            }
        }
    }

    private static void CheckSecondHeaderPrefix(string root, long length, Action<int> charge) {
        if (length is < 0 or > 3) { throw new InvalidDataException("Second segment header action outside 0..3B."); }
        byte[] prefix = new byte[length];
        using var stream = new FileStream(Segment(root, 2), FileMode.Open, FileAccess.Read, FileShare.Read, 1);
        ReadExactMeasured(stream, prefix, charge);
        if (!"RBF1"u8[..prefix.Length].SequenceEqual(prefix)) {
            throw new InvalidDataException("Second segment header prefix contradicts RBF1.");
        }
    }

    private static void CheckShortFrame(string root, string name, long length, long boundary,
        MetadataRoot latest, MetadataDirectory current, ulong sequence,
        Dictionary<EventAddress, MetadataEvent> events,
        Dictionary<RefId, RefOpFrame> allocations,
        Dictionary<SizedPtr, MetadataDirectory> directories, Action<int> charge) {
        int count = checked((int)(length - boundary));
        if (count <= 0) { throw new InvalidDataException("Missing short frame."); }
        byte[] existing = new byte[count];
        using (var stream = new FileStream(PathFor(root, name), FileMode.Open, FileAccess.Read, FileShare.Read, 1)) {
            stream.Position = boundary;
            ReadExactMeasured(stream, existing, charge);
        }
        var candidates = new List<byte[]>();
        if (name is "00000001.rbf" or "00000002.rbf") {
            uint number = name == "00000001.rbf" ? 1u : 2u;
            if (number != current.ActiveSegment && !(number == 2 && current.ActiveSegment == 1)) {
                throw new InvalidDataException("Short event is outside active/next segment.");
            }
            EventAddress a = current.Branches["a"].Head;
            if (!current.Branches.ContainsKey("b")) {
                candidates.Add(EventCandidate(checked(sequence + 1), a, 0x42));
            }
            else {
                candidates.Add(EventCandidate(checked(sequence + 1), current.Branches["b"].Head, 0x43));
            }
            candidates.Add(EventCandidate(checked(sequence + 1), a, 0x52));
        }
        else if (name == "allocation.rbf") {
            MetadataEvent newest = events.Values.OrderBy(e => e.Header.SequenceNumber).LastOrDefault();
            if (newest.Address.Ticket.Packed == 0 || current.Branches.ContainsKey("b")) {
                throw new InvalidDataException("Short Create has no B allocation candidate.");
            }
            var create = new RefOpFrame(RefOpOperation.Create, "b", default, default, 0, null,
                newest.Address, 0, 0);
            candidates.Add(FrameBytes(RefOpFrameCodec.Encode(in create), [], Atelia.EventJournal.EventJournal.RefOpFrameTag));
        }
        else if (name == "catalog.rbf") {
            MetadataDirectory next = NextDirectoryCandidate(current, events, allocations,
                boundary == 0 ? Header : new FileInfo(Segment(root, 1)).Length,
                System.IO.File.Exists(Segment(root, 2)) ? new FileInfo(Segment(root, 2)).Length : 0);
            candidates.Add(FrameBytes(EncodeDirectory(next), [], DirectoryTag));
        }
        else if (name == "roots.rbf") {
            var last = directories.MaxBy(entry => entry.Key.Offset);
            if (last.Key.Packed == 0 || last.Value.Generation != latest.Generation + 1) {
                throw new InvalidDataException("Short root has no next directory.");
            }
            MetadataRoot next = new(latest.Generation + 1, last.Key,
                new FileInfo(File(root, "catalog.rbf")).Length,
                new FileInfo(File(root, "allocation.rbf")).Length);
            candidates.Add(FrameBytes(EncodeRoot(next), [], RootTag));
        }
        if (!candidates.Any(candidate => PrefixMatches(existing, candidate))) {
            throw new InvalidDataException("Short suffix contradicts known writer frame prefix.");
        }
    }

    private static MetadataDirectory NextDirectoryCandidate(MetadataDirectory current,
        Dictionary<EventAddress, MetadataEvent> events, Dictionary<RefId, RefOpFrame> allocations,
        long firstEof, long secondEof) {
        var branches = new SortedDictionary<string, MetadataBranch>(current.Branches, StringComparer.Ordinal);
        MetadataEvent newest = events.Values.MaxBy(e => e.Header.SequenceNumber)!;
        if (!branches.ContainsKey("b")) {
            var b = allocations.FirstOrDefault(entry => entry.Value.BranchName == "b" && entry.Value.StartHead == newest.Address);
            if (b.Key.IsDefault) { throw new InvalidDataException("Directory candidate lacks B Create."); }
            branches.Add("b", new(b.Key, newest.Address));
        }
        else { branches["b"] = new(branches["b"].Id, newest.Address); }
        uint active = secondEof >= Header && newest.Address.SegmentNumber == 2 ? 2u : current.ActiveSegment;
        return new(current.Generation + 1, active, firstEof,
            active == 2 ? secondEof : 0, branches);
    }

    private static byte[] EventCandidate(ulong sequence, EventAddress parent, byte fill) {
        byte[] payload = new byte[EventPayloadLength];
        payload.AsSpan().Fill(fill);
        var header = new EventFrameHeader(EventPayloadCodecId.Identity, sequence, 0, 0,
            AddressHint.None, EventPayloadLength, parent);
        byte[] tailMeta = new byte[EventFrameHeaderCodec.FixedLength];
        EventFrameHeaderCodec.Encode(in header, tailMeta);
        return FrameBytes(payload, tailMeta, Atelia.EventJournal.EventJournal.EventFrameTag);
    }

    private static byte[] FrameBytes(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> tailMeta, uint tag) {
        int covered = checked(payload.Length + tailMeta.Length);
        int padding = (4 - (covered & 3)) & 3;
        int ticketLength = checked(covered + padding + 24);
        byte[] result = new byte[ticketLength + 4];
        BinaryPrimitives.WriteInt32LittleEndian(result, ticketLength);
        payload.CopyTo(result.AsSpan(4));
        tailMeta.CopyTo(result.AsSpan(4 + payload.Length));
        uint crc = RollingCrc.CrcForward(result.AsSpan(4, covered + padding));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4 + covered + padding), crc);
        uint descriptor = TrailerCodewordHelper.BuildDescriptor(false, padding, tailMeta.Length);
        TrailerCodewordHelper.Serialize(result.AsSpan(ticketLength - 16, 16), descriptor, tag, (uint)ticketLength);
        "RBF1"u8.CopyTo(result.AsSpan(ticketLength));
        return result;
    }

    private static bool PrefixMatches(byte[] actual, byte[] expected) {
        if (actual.Length >= expected.Length) { return false; }
        int comparable = Math.Min(actual.Length, expected.Length);
        return actual.AsSpan(0, comparable).SequenceEqual(expected.AsSpan(0, comparable));
    }
}
