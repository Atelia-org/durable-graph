using Atelia.EventJournal;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using JournalStore = Atelia.EventJournal.EventJournal;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;
using StateFrameAddress = Atelia.DurableGraph.Storage.FrameAddress;

namespace Atelia.DurableGraph.Persistence;

/// <summary>Owns the repository lock and its sole publication journal.</summary>
/// <remarks>
/// Existing writable opens initially read only. The host confirms Schema/State and validates
/// every graph dependency before ConfirmDurable confirms Journal data and enables writes.
/// No open path repairs a tail, and failed creation leaves its directory for inspection.
/// </remarks>
internal sealed class HistoryJournal : IDisposable {
    private readonly FileStream _repositoryLock;
    private readonly string _path;
    private readonly List<HistoryGraphRecord> _frames = [];
    private JournalStore _journal;
    private bool _confirmed;
    private bool _disposed;

    private HistoryJournal(string path, FileStream repositoryLock, JournalStore journal, bool readOnly) {
        _path = path;
        _repositoryLock = repositoryLock;
        _journal = journal;
        IsReadOnly = readOnly;
    }

    internal JournalStore Journal {
        get {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _journal;
        }
    }

    internal bool IsReadOnly { get; }

    // Tests observe completed durability barriers and interrupt before the next dependency stage.
    internal Action<string>? AfterConfirmFile { get; set; }

    internal static HistoryJournal Create(string root) {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        root = Path.GetFullPath(root);
        if (Directory.Exists(root) || File.Exists(root)) { throw new IOException("Repository path already exists."); }
        Directory.CreateDirectory(root);
        FileStream repositoryLock = OpenLock(root, create: true, readOnly: false);
        try {
            string path = Path.Combine(root, "journal");
            JournalStore journal = JournalStore.CreateNew(path, StrictOptions());
            return new(path, repositoryLock, journal, readOnly: false);
        } catch {
            repositoryLock.Dispose();
            throw;
        }
    }

    internal static HistoryJournal Open(string root, bool readOnly) {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) { throw new DirectoryNotFoundException(root); }
        FileStream repositoryLock = OpenLock(root, create: false, readOnly);
        HistoryJournal? owner = null;
        JournalStore? journal = null;
        try {
            string path = Path.Combine(root, "journal");
            string refObjects = Path.Combine(path, "refs", "objects");
            if (!Directory.Exists(refObjects)) { throw new DirectoryNotFoundException(refObjects); }
            ValidatePhysicalFiles(path);
            List<EventAddress> addresses = ReadPhysicalAddresses(path);
            journal = JournalStore.OpenReadOnlyExisting(path, StrictOptions());
            owner = new(path, repositoryLock, journal, readOnly);
            foreach (EventAddress address in addresses) { owner._frames.Add(owner.Read(address)); }
            return owner;
        } catch {
            if (owner is not null) { owner.Dispose(); }
            else {
                try { journal?.Dispose(); } finally { repositoryLock.Dispose(); }
            }
            throw;
        }
    }

    internal IReadOnlyList<HistoryGraphRecord> ReadAllFrames() {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _frames.AsReadOnly();
    }

    internal HistoryGraphRecord Read(EventAddress address) {
        using EventFrame frame = Journal.ReadEvent(address).Unwrap();
        if (frame.Header.Parent is { } parent && (parent.SegmentNumber > address.SegmentNumber ||
            parent.SegmentNumber == address.SegmentNumber && parent.Ticket.Offset >= address.Ticket.Offset)) {
            throw new InvalidDataException("A Journal parent must physically precede its child.");
        }
        GraphFrameKind kind = GraphEnvelopeCodec.DecodeKind(frame.Header.OpaqueEventKind);
        var envelope = GraphEnvelopeCodec.Decode(frame.Payload);
        return new(address, frame.Header.Parent, kind, envelope.RevisionAddress, envelope.RootId);
    }

    internal EventAddress Append(GraphFrameKind kind, StateFrameAddress revisionAddress, ObjectId rootId, EventAddress? parent) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsReadOnly || !_confirmed) { throw new InvalidOperationException("Journal writes require confirmed writable resources."); }
        GraphEnvelopeCodec.DecodeKind((uint)kind);
        byte[] payload = GraphEnvelopeCodec.Encode(revisionAddress, rootId);
        EventAddress address = Journal.AppendEventFrame(parent, payload, (uint)kind).Unwrap();
        _frames.Add(new(address, parent, kind, revisionAddress, rootId));
        return address;
    }

    internal void ConfirmDurable() {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsReadOnly) { throw new InvalidOperationException("A read-only journal cannot be promoted or flushed."); }
        if (_confirmed) { return; }
        // RBF write handles are exclusive. Retain the repository lock across this reopen.
        _journal.Dispose();
        string[] files = ValidatePhysicalFiles(_path);
        string eventsPrefix = Path.Combine(_path, "events") + Path.DirectorySeparatorChar;
        string refObjectsPrefix = Path.Combine(_path, "refs", "objects") + Path.DirectorySeparatorChar;
        string refOpLog = Path.Combine(_path, "refs", "ref-op-log.rbf");
        // Validate everything before the first confirmation. Reconfirm graph references before
        // ref objects, and the branch-name/tag ref-op publication frontier last. Physical orphan files
        // also participate; directory enumeration order must never choose barrier ordering.
        foreach (string filePath in files.OrderBy(path => path == refOpLog ? 3 :
            path.StartsWith(eventsPrefix, StringComparison.Ordinal) ? 0 :
            path.StartsWith(refObjectsPrefix, StringComparison.Ordinal) ? 1 : 2).ThenBy(path => path, StringComparer.Ordinal)) {
            using IRbfFile file = RbfFile.OpenExisting(filePath);
            file.DurableFlush();
            AfterConfirmFile?.Invoke(filePath);
        }
        _journal = JournalStore.OpenExisting(_path, StrictOptions());
        // Ref objects are lazy on writable open; force replay of all visible refs now.
        foreach (string branch in _journal.ListBranches()) { _journal.GetHead(_journal.OpenBranch(branch).Unwrap()); }
        _confirmed = true;
    }

    private static FileStream OpenLock(string root, bool create, bool readOnly) => new(
        Path.Combine(root, "repository.lock"), create ? FileMode.CreateNew : FileMode.Open,
        readOnly ? FileAccess.Read : FileAccess.ReadWrite, readOnly ? FileShare.Read : FileShare.None);

    private static EventJournalOptions StrictOptions() => new() {
        EventSegmentStoreOptions = new() { RecoverActiveTailOnOpen = false },
        RefSegmentStoreOptions = new() { RecoverActiveTailOnOpen = false },
        RefOpLogOptions = new() { RecoverActiveTailOnOpen = false },
    };

    private static string[] ValidatePhysicalFiles(string path) {
        if (!Directory.Exists(path)) { throw new DirectoryNotFoundException(path); }
        string[] files = Directory.GetFiles(path, "*.rbf", SearchOption.AllDirectories);
        foreach (string filePath in files) {
            using IRbfFile file = RbfFile.OpenReadOnlyExisting(filePath);
            var frames = file.ScanForward(showTombstone: true).GetEnumerator();
            while (frames.MoveNext()) {
                using RbfPooledFrame frame = file.ReadPooledFrame(frames.Current.Ticket).Unwrap();
                if (frame.IsTombstone) { throw new InvalidDataException("EventHistory journal files cannot contain tombstones."); }
            }
            if (frames.TerminationError is { } error) { throw new InvalidDataException($"Invalid Journal tail: {error.Message}"); }
        }
        return files;
    }

    private static List<EventAddress> ReadPhysicalAddresses(string path) {
        using SegmentStore segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(path, "events"), StrictOptions().EventSegmentStoreOptions);
        List<EventAddress> addresses = [];
        for (uint number = 1; number <= segments.ActiveSegmentNumber; number++) {
            using RbfSegmentReaderLease lease = segments.OpenReader(number);
            var frames = lease.File.ScanForward(showTombstone: true).GetEnumerator();
            while (frames.MoveNext()) {
                RbfFrameInfo info = frames.Current;
                if (info.Tag != JournalStore.EventFrameTag || info.IsTombstone) { throw new InvalidDataException("Unexpected frame in EventHistory event segments."); }
                using RbfPooledTailMeta tail = info.ReadPooledTailMeta().Unwrap();
                EventFrameHeader header = EventFrameHeaderCodec.Decode(tail.TailMeta).Unwrap();
                addresses.Add(new(info.Ticket, number, header.Hint));
            }
            if (frames.TerminationError is { } error) { throw new InvalidDataException($"Invalid Journal event tail: {error.Message}"); }
        }
        return addresses;
    }

    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true;
        try { _journal.Dispose(); } finally { _repositoryLock.Dispose(); }
    }
}
