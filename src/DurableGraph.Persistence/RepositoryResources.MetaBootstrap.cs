using System.Buffers.Binary;
using System.Collections.ObjectModel;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Atelia.HeadStore;
using DataStore = Atelia.FrameStore.FrameStore;
using PublicationStore = Atelia.HeadStore.HeadStore;

namespace Atelia.DurableGraph.Persistence;

public sealed partial class RepositoryResources {
    private const uint BranchDirectoryHead = 1, SharedSchemaHead = 2, BookmarkDirectoryHead = 3;
    private const string SchemaKey = "schema";
    private static ReadOnlySpan<byte> SharedMagic => "P0S1"u8;
    private PublicationBaseline _baseline = null!;
    private DecodedGraphRows? _projectedBranchDirectory;
    private IReadOnlyDictionary<HeadId, string>? _branchLabels;
    internal long BranchDirectoryProjectedEntries { get; private set; }
    private HeadEntryId _branchEntry, _sharedEntry;
    private PendingRepositorySave? _pending;
    private bool _preparing, _committing;

    internal sealed class PublicationBaseline(DecodedGraphRows? branches, FrameAddress? sharedRoot) {
        internal DecodedGraphRows? Branches { get; } = branches;
        internal FrameAddress? SharedRoot { get; } = sharedRoot;
    }

    private RepositoryResources(DataStore data, PublicationStore heads,
        HeadEntry branches, HeadEntry shared, HeadEntry bookmarks, long readCacheBudgetBytes) {
        Data = data;
        IsReadOnly = heads.IsReadOnly;
        _heads = heads;
        _branchEntry = branches.Id;
        _sharedEntry = shared.Id;
        _bookmarkEntry = bookmarks.Id;
        // Read and validate every required control pointer before constructing graph caches.
        FrameAddress? branchRoot = ManagementDirectoryPointer.Decode(ManagementDirectoryKind.Branches, branches.Payload);
        FrameAddress? bookmarkRoot = ManagementDirectoryPointer.Decode(ManagementDirectoryKind.Bookmarks, bookmarks.Payload);
        FrameAddress? schemaRoot = DecodeShared(shared.Payload);
        _graphStore = new(data, RequireStorageAvailable, readCacheBudgetBytes);
        try {
            _baseline = new(branchRoot is { } d ? ManagementDirectoryReader.ReadBranches(_graphStore, d) : null, schemaRoot);
            _bookmarks = bookmarkRoot is { } t ? ManagementDirectoryReader.ReadBookmarks(_graphStore, t) : null;
            if (schemaRoot is { } s) { _ = _graphStore.ReadMetaGraph(s); }
        } catch (Exception error) {
            CloseResources(error, _graphStore, null, null);
            throw;
        }
    }

    private readonly GraphRecordStore _graphStore;
    internal GraphRecordStore GraphStore { get { RequireStorageAvailable(); return _graphStore; } }
    internal Action<RepositorySaveStage>? SaveStageObserver { get; set; }
    internal bool IsStorageFaulted => _faulted || (!_disposed && !StorageBackingUsable());
    internal bool HasPendingSave { get { RequireStorageAvailable(); return _pending is not null; } }
    internal FrameAddress? SharedSchemaRoot { get { RequireStorageAvailable(); return _baseline.SharedRoot; } }

    internal static RepositoryResources CreateStorageNew(string path,
        long readCacheBudgetBytes = GraphRecordStore.DefaultReadCacheBudgetBytes, Action<uint>? afterCreateHead = null,
        long rotationThresholdBytes = DefaultRotationThresholdBytes) =>
        OpenStorage(path, true, false, readCacheBudgetBytes, afterCreateHead, rotationThresholdBytes);
    internal static RepositoryResources OpenStorageExisting(string path,
        long readCacheBudgetBytes = GraphRecordStore.DefaultReadCacheBudgetBytes,
        long rotationThresholdBytes = DefaultRotationThresholdBytes) =>
        OpenStorage(path, false, false, readCacheBudgetBytes, rotationThresholdBytes: rotationThresholdBytes);
    internal static RepositoryResources OpenStorageReadOnlyExisting(string path,
        long readCacheBudgetBytes = GraphRecordStore.DefaultReadCacheBudgetBytes) =>
        OpenStorage(path, false, true, readCacheBudgetBytes);

    private static RepositoryResources OpenStorage(string path, bool create, bool readOnly, long readCacheBudgetBytes,
        Action<uint>? afterCreateHead = null, long rotationThresholdBytes = DefaultRotationThresholdBytes) {
        ArgumentOutOfRangeException.ThrowIfNegative(readCacheBudgetBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string root = Path.GetFullPath(path);
        if (create) {
            if (Directory.Exists(root) || File.Exists(root)) { throw new IOException("The repository path already exists."); }
            Directory.CreateDirectory(root);
        } else if (!Directory.Exists(root)) { throw new DirectoryNotFoundException(root); }
        DataStore? data = null;
        PublicationStore? heads = null;
        try {
            string dataPath = Path.Combine(root, "data"), headsPath = Path.Combine(root, "versions");
            data = create ? DataStore.Create(dataPath, rotationThresholdBytes) : readOnly
                ? DataStore.OpenReadOnly(dataPath) : DataStore.Open(dataPath, rotationThresholdBytes);
            heads = create ? PublicationStore.Create(headsPath, data, reservedHeadIdUpperBound: BookmarkDirectoryHead)
                : readOnly ? PublicationStore.OpenReadOnly(headsPath, data) : PublicationStore.Open(headsPath, data);
            if (heads.ReservedHeadIdUpperBound != BookmarkDirectoryHead) { throw new InvalidDataException("Not a DG publication layout."); }
            if (create) {
                heads.CreateHead(BranchDirectoryHead, ManagementDirectoryPointer.Encode(ManagementDirectoryKind.Branches, null), out _).Unwrap();
                afterCreateHead?.Invoke(BranchDirectoryHead);
                heads.CreateHead(SharedSchemaHead, EncodeShared(null), out _).Unwrap();
                afterCreateHead?.Invoke(SharedSchemaHead);
                heads.CreateHead(BookmarkDirectoryHead, ManagementDirectoryPointer.Encode(ManagementDirectoryKind.Bookmarks, null), out _).Unwrap();
                afterCreateHead?.Invoke(BookmarkDirectoryHead);
            }
            HeadEntry branches = heads.ReadHead(BranchDirectoryHead).Unwrap();
            HeadEntry shared = heads.ReadHead(SharedSchemaHead).Unwrap();
            HeadEntry bookmarks = heads.ReadHead(BookmarkDirectoryHead).Unwrap();
            return new(data, heads, branches, shared, bookmarks, readCacheBudgetBytes);
        } catch (Exception error) {
            CloseResources(error, null, heads, data);
            throw;
        }
    }

    private static uint EncodeHead(HeadId id) {
        Span<byte> bytes = stackalloc byte[HeadId.EncodedSize];
        if (!id.TryWrite(bytes)) { throw new ArgumentException("A valid BranchId is required.", nameof(id)); }
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private void RequireMember(HeadId branch) {
        RequireStorageAvailable();
        _ = EncodeHead(branch); // Preserve invalid/default ID rejection before membership lookup.
        if (!ListBranches().ContainsKey(branch)) {
            throw new KeyNotFoundException("The Head is not a registered DurableGraph Branch.");
        }
    }

    internal string GetBranchLabel(HeadId branch) {
        RequireStorageAvailable();
        return ListBranches().TryGetValue(branch, out string? label) ? label
            : throw new KeyNotFoundException("Branch is not registered in this repository.");
    }

    internal IReadOnlyDictionary<HeadId, string> ListBranches() {
        RequireStorageAvailable();
        DecodedGraphRows? directory = _baseline.Branches;
        if (_branchLabels is not null && ReferenceEquals(_projectedBranchDirectory, directory)) {
            return _branchLabels;
        }
        // Only accepted D rows select this derived view. Ordinary B/S publications retain
        // the same D instance; candidates do not replace it before successful installation.
        Dictionary<HeadId, string> members = [];
        if (directory is not null) {
            Span<byte> bytes = stackalloc byte[HeadId.EncodedSize];
            foreach (var pair in directory.Rows[directory.Snapshot.RootId].GetDictionaryState<uint, ObjectId>().Entries) {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, pair.Key);
                if (!HeadId.TryRead(bytes, out HeadId id)) { throw new InvalidDataException("Invalid branch ID."); }
                members.Add(id, directory.Rows[pair.Value].StringContent);
                BranchDirectoryProjectedEntries++;
            }
        }
        IReadOnlyDictionary<HeadId, string> labels = new ReadOnlyDictionary<HeadId, string>(members);
        _projectedBranchDirectory = directory;
        _branchLabels = labels;
        return labels;
    }

    internal HistoryGraphRecord ReadCurrent(HeadId branch) {
        RequireMember(branch);
        return Describe(_heads.ReadHead(branch).Unwrap());
    }
    internal HistoryGraphRecord ReadDescriptor(HeadEntryId id) {
        RequireMember(id.HeadId);
        return Describe(_heads.ReadEntry(id).Unwrap());
    }
    private HistoryGraphRecord Describe(HeadEntry entry) {
        RequireMember(entry.Id.HeadId);
        var payload = BusinessEntryPayloadCodec.Decode(entry.Payload);
        GraphEnvelope metadata = GraphStore.ReadMetadata(payload.Graph);
        CommitKind kind = metadata.Kind switch {
            StoredGraphKind.State => CommitKind.State,
            StoredGraphKind.Event => CommitKind.Event,
            _ => throw new InvalidDataException("A business Entry must select State or Event."),
        };
        return new(entry.Id, payload.Graph, kind, metadata.RootId, payload.Summary);
    }

    // Each visitor ends before callers decode objects or enter another HS history operation.
    private void ScanHistory(HeadEntryId id, Func<HistoryGraphRecord, bool> visitor) {
        RequireMember(id.HeadId);
        _heads.ReadEntryHistory(id, new(long.MaxValue, long.MaxValue), entry => visitor(Describe(entry))).Unwrap();
    }
    internal IReadOnlyList<HistoryGraphRecord> ReadHistory(HeadEntryId id) {
        List<HistoryGraphRecord> result = [];
        ScanHistory(id, item => { result.Add(item); return true; });
        return result;
    }
    internal HistoryGraphRecord? FindNearestState(HeadEntryId id, bool inclusive) =>
        FindKind(id, CommitKind.State, inclusive);
    internal HistoryGraphRecord? FindPreviousKind(HeadEntryId id, CommitKind kind) => FindKind(id, kind, false);
    private HistoryGraphRecord? FindKind(HeadEntryId id, CommitKind kind, bool inclusive) {
        HistoryGraphRecord? found = null;
        bool first = true;
        ScanHistory(id, item => {
            bool skip = first && !inclusive;
            first = false;
            if (skip || item.Kind != kind) { return true; }
            found = item;
            return false;
        });
        return found;
    }
    internal HistoryGraphRecord? ReadPrevious(HeadEntryId id) {
        HistoryGraphRecord? previous = null;
        bool first = true;
        ScanHistory(id, item => {
            if (first) { first = false; return true; }
            previous = item;
            return false;
        });
        return previous;
    }

    // Isolated B2 bridge only. Formal merge requires targeted upstream origin lookup;
    // this all-origins scan can still fail on an unrelated unregistered Head.
    internal HeadEntryId? ReadOrigin(HeadId branch) {
        // The all-origins API omits roots and missing files alike. An exact current read
        // distinguishes a real root from a registered ID whose physical Head is absent.
        _ = ReadCurrent(branch);
        Span<byte> bytes = stackalloc byte[HeadEntryId.EncodedSize];
        foreach (HeadOriginInfo origin in _heads.ListHeadOrigins(int.MaxValue).Unwrap()) {
            if (origin.ChildHeadId != branch) { continue; }
            RequireMember(origin.SourceHeadId);
            origin.SourceHeadId.TryWrite(bytes);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[HeadId.EncodedSize..], origin.SourceEntryTicket.Packed);
            if (!HeadEntryId.TryRead(bytes, out HeadEntryId id)) { throw new InvalidDataException("Invalid branch origin."); }
            _ = ReadDescriptor(id);
            return id;
        }
        return null;
    }

    internal HeadId ForkBranch(HeadEntryId source, string label, out PublicationOutcome outcome) {
        outcome = PublicationOutcome.NotAttempted;
        EnsureEditingAllowed();
        ValidateDisplayLabel(label);
        _ = ReadDescriptor(source);
        GraphRecordStore store = GraphStore;
        long before = store.AppendedFrames;
        HeadId? created = null;
        bool registering = false, rejected = false;
        PublicationOutcome physicalOutcome = PublicationOutcome.NotAttempted;
        _committing = true;
        try {
            var fork = _heads.ForkHead(source, out physicalOutcome);
            rejected = fork.IsFailure;
            created = fork.Unwrap();
            PreparedDirectoryWrite candidate = ManagementDirectoryWriter.PrepareBranchAddition(GraphStore, _baseline.Branches, created.Value, label);
            StoredGraphRevision graph = GraphStore.AppendMeta(candidate.Write.SourceGraph, candidate.Write);
            candidate.PrepareInstall(graph);
            DecodedGraphRows next = candidate.GetInstallation(_baseline.Branches);
            byte[] payload = ManagementDirectoryPointer.Encode(ManagementDirectoryKind.Branches, graph.Address);
            SaveStageObserver?.Invoke(RepositorySaveStage.BeforePublication);
            RequireStorageAvailable();
            registering = true;
            var publication = _heads.PublishHead(_branchEntry.HeadId, payload, out outcome);
            rejected = publication.IsFailure;
            HeadEntryId entry = publication.Unwrap();
            SaveStageObserver?.Invoke(RepositorySaveStage.AfterPublication);
            RequireStorageAvailable();
            SaveStageObserver?.Invoke(RepositorySaveStage.BeforeInstall);
            RequireStorageAvailable();
            _baseline = new(next, _baseline.SharedRoot);
            _branchEntry = entry;
            return created.Value;
        } catch (Exception error) {
            if (outcome != PublicationOutcome.NotAttempted || physicalOutcome != PublicationOutcome.NotAttempted ||
                !StorageBackingUsable() || store.AppendedFrames != before || (registering && !rejected)) { MarkFaulted(); }
            throw PublicationFailure(outcome, null, error, created, null, null);
        } finally { _committing = false; }
    }

    internal void EnsureEditingAllowed() {
        RequireStorageAvailable();
        if (_heads.IsReadOnly) { throw new InvalidOperationException("The repository is read-only."); }
        if (_pending is not null || _preparing || _committing) { throw new InvalidOperationException("A save is already being prepared or pending."); }
    }

    internal PendingRepositorySave PrepareSaveFrom(HeadId? branch, HeadEntryId? expected, string? rootLabel,
        StoredGraphKind kind, ReadOnlyMemory<byte> summary, Func<(PreparedGraphWrite Business, PreparedGraphWrite? Catalog)> produce) {
        EnsureEditingAllowed();
        ArgumentNullException.ThrowIfNull(produce);
        if (kind is not StoredGraphKind.State and not StoredGraphKind.Event) { throw new ArgumentException("A business graph kind is required.", nameof(kind)); }
        BusinessEntryPayloadCodec.ValidateSummaryLength(summary.Length);
        byte[] ownedSummary = summary.ToArray();
        FrameAddress? source = null;
        if (branch is { } target) {
            if (expected is null || rootLabel is not null || ReadCurrent(target).Id != expected.Value) { throw new InvalidOperationException("The branch's accepted Commit has changed."); }
            source = FindNearestState(expected.Value, true)?.Address;
        } else {
            if (expected is not null) { throw new ArgumentException("A new root cannot have an accepted Commit."); }
            ValidateDisplayLabel(rootLabel!);
        }
        _preparing = true;
        try {
            var prepared = produce();
            RequireStorageAvailable();
            if (branch is { } current && ReadCurrent(current).Id != expected) { throw new InvalidOperationException("The branch changed during capture."); }
            if (prepared.Business.SourceGraph != source) { throw new InvalidDataException("The business candidate must use the exact latest State selected by HeadStore history."); }
            if (prepared.Catalog is not null && prepared.Catalog.SourceGraph != _baseline.SharedRoot) { throw new InvalidDataException("The catalog candidate must use the accepted catalog source."); }
            var pending = new PendingRepositorySave(this, _baseline, branch, expected, rootLabel, kind, ownedSummary,
                prepared.Business, prepared.Catalog, _baseline.SharedRoot, source);
            _pending = pending;
            return pending;
        } finally { _preparing = false; }
    }

    internal RepositorySaveResult CommitSave(PendingRepositorySave pending,
        Action<StoredGraphRevision>? installMeta = null, Action<StoredGraphRevision>? installState = null,
        Action<StoredGraphRevision>? prepareMeta = null, Action<StoredGraphRevision>? prepareState = null,
        Action<StoredGraphRevision>? prepareBusinessDelivery = null, Action<RepositorySaveResult>? deliver = null) {
        RequireStorageAvailable();
        ArgumentNullException.ThrowIfNull(pending);
        if (_heads.IsReadOnly || _committing || !ReferenceEquals(pending.Owner, this) || !ReferenceEquals(_pending, pending) ||
            !pending.IsPrepared || !ReferenceEquals(pending.Baseline, _baseline)) { throw new InvalidOperationException("Commit requires the owner's latest unused pending save."); }
        if (pending.Branch is { } target && ReadCurrent(target).Id != pending.ExpectedEntry) { throw new InvalidOperationException("The branch's accepted Commit has changed."); }
        pending.BeginCommit();
        _committing = true;
        PublicationOutcome outcome = PublicationOutcome.NotAttempted, physicalOutcome = PublicationOutcome.NotAttempted;
        FrameAddress? candidate = null;
        HeadId? targetBranch = pending.Branch;
        HeadEntryId? commitId = null;
        GraphRecordStore store = GraphStore;
        long framesBefore = store.AppendedFrames;
        bool publishing = false, publicationRejected = false;
        try {
            SaveStageObserver?.Invoke(RepositorySaveStage.BeforeAppend);
            RequireStorageAvailable();
            StoredGraphRevision? meta = pending.Catalog is not null ? store.AppendMeta(pending.CatalogSource, pending.Catalog)
                : pending.CatalogSource is { } existingMeta ? store.ReadMetaGraph(existingMeta) : null;
            if (pending.Catalog is not null) { SaveStageObserver?.Invoke(RepositorySaveStage.AfterMetaAppend); }
            RequireStorageAvailable();
            StoredGraphRevision business = store.AppendBusiness(pending.Kind, pending.StateSource, meta?.Address, pending.Business);
            candidate = business.Address;
            SaveStageObserver?.Invoke(RepositorySaveStage.AfterBusinessAppend);
            RequireStorageAvailable();
            if (meta is not null) { prepareMeta?.Invoke(meta); }
            if (pending.Kind == StoredGraphKind.State) { prepareState?.Invoke(business); }
            prepareBusinessDelivery?.Invoke(business);
            RequireStorageAvailable();
            byte[] businessPayload = BusinessEntryPayloadCodec.Encode(business.Address, pending.Summary.Span);
            PreparedDirectoryWrite? registration = null;
            DecodedGraphRows? nextDirectory = _baseline.Branches;
            byte[]? directoryPayload = null;
            if (targetBranch is null) {
                var physical = _heads.CreateHead(businessPayload, out physicalOutcome);
                publicationRejected = physical.IsFailure;
                commitId = physical.Unwrap();
                targetBranch = commitId.Value.HeadId;
                registration = ManagementDirectoryWriter.PrepareBranchAddition(store, _baseline.Branches, targetBranch.Value, pending.RootLabel!);
                StoredGraphRevision directory = store.AppendMeta(registration.Write.SourceGraph, registration.Write);
                registration.PrepareInstall(directory);
                nextDirectory = registration.GetInstallation(_baseline.Branches);
                directoryPayload = ManagementDirectoryPointer.Encode(ManagementDirectoryKind.Branches, directory.Address);
            }
            byte[]? schemaPayload = pending.Catalog is null ? null : EncodeShared(meta!.Address);
            var changes = new Dictionary<HeadId, ReadOnlyMemory<byte>> {
                [directoryPayload is null ? targetBranch.Value : _branchEntry.HeadId] = directoryPayload ?? businessPayload
            };
            if (schemaPayload is not null) { changes.Add(_sharedEntry.HeadId, schemaPayload); }
            SaveStageObserver?.Invoke(RepositorySaveStage.BeforePublication);
            RequireStorageAvailable();
            // Recheck every baseline after application/observer callbacks, before crossing publication.
            if (!ReferenceEquals(pending.Baseline, _baseline) || (pending.Branch is { } b && ReadCurrent(b).Id != pending.ExpectedEntry)) {
                throw new InvalidOperationException("The prepared save baseline changed.");
            }
            if (registration is not null) { _ = registration.GetInstallation(_baseline.Branches); }
            publishing = true;
            var published = _heads.PublishHeads(changes, out outcome);
            publicationRejected = published.IsFailure;
            IReadOnlyDictionary<HeadId, HeadEntryId> entries = published.Unwrap();
            if (pending.Branch is not null) { commitId = entries[targetBranch.Value]; }
            SaveStageObserver?.Invoke(RepositorySaveStage.AfterPublication);
            RequireStorageAvailable();
            SaveStageObserver?.Invoke(RepositorySaveStage.BeforeInstall);
            RequireStorageAvailable();
            _baseline = new(nextDirectory, meta?.Address);
            if (registration is not null) { _branchEntry = entries[_branchEntry.HeadId]; }
            if (schemaPayload is not null) { _sharedEntry = entries[_sharedEntry.HeadId]; }
            if (meta is not null) { installMeta?.Invoke(meta); }
            RequireStorageAvailable();
            if (pending.Kind == StoredGraphKind.State) { installState?.Invoke(business); }
            RequireStorageAvailable();
            var result = new RepositorySaveResult(business, meta, meta?.Address, commitId!.Value, targetBranch.Value);
            deliver?.Invoke(result);
            RequireStorageAvailable();
            return result;
        } catch (Exception error) {
            bool fault = outcome != PublicationOutcome.NotAttempted || physicalOutcome != PublicationOutcome.NotAttempted || !StorageBackingUsable() ||
                (publishing ? !publicationRejected : store.AppendedFrames != framesBefore);
            if (fault) { MarkFaulted(); }
            throw PublicationFailure(outcome, candidate, error, targetBranch, pending.ExpectedEntry, commitId);
        } finally {
            pending.Finish();
            if (ReferenceEquals(_pending, pending)) { _pending = null; }
            _committing = false;
        }
    }

    private static RepositoryPublicationException PublicationFailure(PublicationOutcome outcome, FrameAddress? candidate,
        Exception error, HeadId? branch, HeadEntryId? expected, HeadEntryId? commit) => new(outcome switch {
            PublicationOutcome.Confirmed => RepositoryPublicationOutcome.Published,
            PublicationOutcome.Unknown => RepositoryPublicationOutcome.Unknown,
            _ => RepositoryPublicationOutcome.NotPublished,
        }, candidate, error, branch, expected, commit);

    internal void DiscardSave(PendingRepositorySave pending) {
        if (_committing || !ReferenceEquals(pending.Owner, this) || !ReferenceEquals(_pending, pending) || !pending.IsPrepared) {
            throw new InvalidOperationException("Discard requires the owner's inactive unused pending save.");
        }
        pending.Finish();
        _pending = null;
    }
    internal void RequireStorageAvailable() => RequireAvailable();
    internal void RequirePreparationInactive() {
        if (_preparing) { throw new InvalidOperationException("An active graph preparation cannot be disposed recursively."); }
    }
    private bool StorageBackingUsable() {
        try { Data.EnsureUsable(); _heads.EnsureUsable(); return true; }
        catch (InvalidOperationException) { return false; }
    }
    private static byte[] EncodeShared(FrameAddress? schema) => [.. SharedMagic,
        .. NamedFrameMapCodec.Encode(schema is { } address ? new Dictionary<string, FrameAddress>(StringComparer.Ordinal) { [SchemaKey] = address } : []).Unwrap()];
    private static FrameAddress? DecodeShared(ReadOnlySpan<byte> payload) {
        if (!payload.StartsWith(SharedMagic)) { throw new InvalidDataException("Unknown shared catalog root format."); }
        var roots = NamedFrameMapCodec.Decode(payload[SharedMagic.Length..]).Unwrap();
        if (roots.Count == 0) { return null; }
        if (roots.Count != 1 || !roots.TryGetValue(SchemaKey, out FrameAddress schema)) { throw new InvalidDataException("Unknown shared catalog root key."); }
        return schema;
    }
}
