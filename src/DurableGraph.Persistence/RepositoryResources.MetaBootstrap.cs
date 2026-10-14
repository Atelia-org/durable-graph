
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Atelia.VersionStore;
using DataStore = Atelia.FrameStore.FrameStore;
using PublicationStore = Atelia.VersionStore.VersionStore;

namespace Atelia.DurableGraph.Persistence;

// Concrete metadata-graph owner used by the public factories and bounded internal probes.
public sealed partial class RepositoryResources {
    private const uint BranchRef = 1, SharedRef = 2;
    private const string BranchPrefix = "branch/", SchemaKey = "schema";
    private static ReadOnlySpan<byte> BranchMagic => "P0R1"u8;
    private static ReadOnlySpan<byte> SharedMagic => "P0S1"u8;
    private PublicationBaseline _baseline = null!;
    private RefRevision _branchRevision, _sharedRevision;
    private PendingRepositorySave? _pending;
    private bool _preparing, _committing;

    // This reference is the operation's exact baseline; it is never changed in place.
    internal sealed class PublicationBaseline(Dictionary<string, FrameAddress> branches, FrameAddress? sharedRoot) {
        internal Dictionary<string, FrameAddress> Branches { get; } = branches;
        internal FrameAddress? SharedRoot { get; } = sharedRoot;
    }

    private RepositoryResources(DataStore data, PublicationStore versions,
        RefBytesSnapshot branches, RefBytesSnapshot shared, long readCacheBudgetBytes) {
        Data = data;
        IsReadOnly = versions.IsReadOnly;
        _versions = versions;
        _branchRevision = branches.Revision;
        _sharedRevision = shared.Revision;
        _baseline = new(DecodeBranches(branches.Payload), DecodeShared(shared.Payload));
        _graphStore = new(data, RequireStorageAvailable, readCacheBudgetBytes);
        try {
            if (_baseline.SharedRoot is { } schema && _graphStore.ReadMetadata(schema).Kind != StoredGraphKind.Meta) {
                throw new InvalidDataException("The shared root must select a P0 meta graph.");
            }
        } catch (Exception error) {
            // The outer factory closes VS/FS; release a facade created before root validation failed.
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
        long readCacheBudgetBytes = GraphRecordStore.DefaultReadCacheBudgetBytes, Action<uint>? afterCreateRef = null,
        long rotationThresholdBytes = DefaultRotationThresholdBytes) =>
        OpenStorage(path, create: true, readOnly: false, readCacheBudgetBytes, afterCreateRef, rotationThresholdBytes);
    internal static RepositoryResources OpenStorageExisting(string path,
        long readCacheBudgetBytes = GraphRecordStore.DefaultReadCacheBudgetBytes,
        long rotationThresholdBytes = DefaultRotationThresholdBytes) =>
        OpenStorage(path, create: false, readOnly: false, readCacheBudgetBytes, rotationThresholdBytes: rotationThresholdBytes);
    internal static RepositoryResources OpenStorageReadOnlyExisting(string path,
        long readCacheBudgetBytes = GraphRecordStore.DefaultReadCacheBudgetBytes) =>
        OpenStorage(path, create: false, readOnly: true, readCacheBudgetBytes);

    private static RepositoryResources OpenStorage(string path, bool create, bool readOnly, long readCacheBudgetBytes,
        Action<uint>? afterCreateRef = null, long rotationThresholdBytes = DefaultRotationThresholdBytes) {
        ArgumentOutOfRangeException.ThrowIfNegative(readCacheBudgetBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string root = Path.GetFullPath(path);
        if (create) {
            if (Directory.Exists(root) || File.Exists(root)) { throw new IOException("The P0 repository path already exists."); }
            Directory.CreateDirectory(root);
        } else if (!Directory.Exists(root)) { throw new DirectoryNotFoundException(root); }

        DataStore? data = null;
        PublicationStore? versions = null;
        try {
            string dataPath = Path.Combine(root, "data"), versionsPath = Path.Combine(root, "versions");
            data = create ? DataStore.Create(dataPath, rotationThresholdBytes) : readOnly
                ? DataStore.OpenReadOnly(dataPath) : DataStore.Open(dataPath, rotationThresholdBytes);
            versions = create ? PublicationStore.Create(versionsPath, data, reservedRefIdUpperBound: SharedRef)
                : readOnly ? PublicationStore.OpenReadOnly(versionsPath, data) : PublicationStore.Open(versionsPath, data);
            if (versions.ReservedRefIdUpperBound != SharedRef) { throw new InvalidDataException("Not a P0 publication layout."); }
            if (create) {
                versions.CreateRef(BranchRef, EncodeBranches(new(StringComparer.Ordinal)), out _).Unwrap();
                afterCreateRef?.Invoke(BranchRef);
                versions.CreateRef(SharedRef, EncodeShared(null), out _).Unwrap();
                afterCreateRef?.Invoke(SharedRef);
            }
            // Missing either ref is an incomplete initialization, never a resumable create.
            RefBytesSnapshot branches = versions.ReadRef(BranchRef).Unwrap();
            RefBytesSnapshot shared = versions.ReadRef(SharedRef).Unwrap();
            return new(data, versions, branches, shared, readCacheBudgetBytes);
        }
        catch (Exception error) {
            CloseResources(error, null, versions, data);
            throw;
        }
    }

    internal FrameAddress? GetBranchHead(string branch) {
        RequireStorageAvailable();
        ValidateBranchName(branch);
        return _baseline.Branches.TryGetValue(branch, out FrameAddress head) ? head : null;
    }

    internal FrameAddress? GetBranchStateHead(string branch) =>
        GraphStore.ResolveContentSource(StoredGraphKind.State, GetBranchHead(branch));

    internal IReadOnlyList<string> ListBranches() {
        RequireStorageAvailable();
        return _baseline.Branches.Keys.Order(StringComparer.Ordinal).ToArray();
    }

    // Metadata navigation intentionally does not read a directory, batch or typed body.
    internal HistoryGraphRecord ReadHistory(FrameAddress address) {
        GraphEnvelope metadata = GraphStore.ReadMetadata(address);
        CheckpointKind kind = metadata.Kind switch {
            StoredGraphKind.State => CheckpointKind.State,
            StoredGraphKind.Event => CheckpointKind.Event,
            _ => throw new InvalidDataException("A branch checkpoint must select a business State or Event."),
        };
        return new(address, metadata.Parent, kind, metadata.RootId);
    }

    internal sealed record PreparedBranchRootUpdate(RepositoryResources Owner,
        PublicationBaseline ExpectedBaseline, RefRevision ExpectedRevision,
        PublicationBaseline NextBaseline, byte[] Payload);

    internal PreparedBranchRootUpdate PrepareBranchRoot(string branch, FrameAddress head) {
        EnsureEditingAllowed();
        ValidateBranchName(branch);
        _ = ReadHistory(head);
        var branches = new Dictionary<string, FrameAddress>(_baseline.Branches, StringComparer.Ordinal) { [branch] = head };
        PublicationBaseline next = new(branches, _baseline.SharedRoot);
        return new(this, _baseline, _branchRevision, next, EncodeBranches(branches));
    }

    // This operation updates only the branch ref. The shared Schema head and revision stay fixed.
    internal void PublishBranchRoot(PreparedBranchRootUpdate candidate, out PublicationOutcome outcome) {
        outcome = PublicationOutcome.NotAttempted;
        EnsureEditingAllowed();
        ArgumentNullException.ThrowIfNull(candidate);
        if (!ReferenceEquals(candidate.Owner, this) || !ReferenceEquals(candidate.ExpectedBaseline, _baseline) ||
            candidate.ExpectedRevision != _branchRevision) {
            throw new InvalidOperationException("Branch publication requires this owner's latest prepared root.");
        }
        bool publicationRejected = false;
        _committing = true;
        try {
            var published = _versions.PublishRef(_branchRevision.RefId, candidate.Payload, out outcome);
            publicationRejected = published.IsFailure;
            RefRevision revision = published.Unwrap();
            _baseline = candidate.NextBaseline;
            _branchRevision = revision;
        } catch {
            if (!publicationRejected || outcome != PublicationOutcome.NotAttempted || !StorageBackingUsable()) { MarkFaulted(); }
            throw;
        } finally { _committing = false; }
    }

    internal void CreateStoredTag(string name, FrameAddress address, out PublicationOutcome outcome) {
        outcome = PublicationOutcome.NotAttempted;
        EnsureEditingAllowed();
        _ = ReadHistory(address);
        byte[] payload = new byte[FrameAddress.EncodedSize];
        if (!address.TryWrite(payload)) { throw new InvalidDataException("Invalid graph tag address."); }
        bool publicationRejected = false;
        _committing = true;
        try {
            var published = _versions.CreateTag(name, payload, out outcome);
            publicationRejected = published.IsFailure;
            published.Unwrap();
        } catch {
            if (!publicationRejected || outcome != PublicationOutcome.NotAttempted || !StorageBackingUsable()) { MarkFaulted(); }
            throw;
        } finally { _committing = false; }
    }

    internal FrameAddress ResolveStoredTag(string name) {
        RequireStorageAvailable();
        var payload = _versions.ResolveTag(name).Unwrap();
        if (payload.Bytes.Length != FrameAddress.EncodedSize || !FrameAddress.TryRead(payload.Bytes, out FrameAddress address)) {
            throw new InvalidDataException("Invalid graph address in tag payload.");
        }
        _ = ReadHistory(address);
        return address;
    }

    internal void EnsureEditingAllowed() {
        RequireStorageAvailable();
        if (_versions.IsReadOnly) { throw new InvalidOperationException("The P0 repository is read-only."); }
        if (_pending is not null || _preparing || _committing) { throw new InvalidOperationException("A P0 save is already being prepared or pending."); }
    }

    internal PendingRepositorySave PrepareSave(string branch, StoredGraphKind kind,
        PreparedGraphWrite business, PreparedGraphWrite? catalog = null) {
        EnsureEditingAllowed();
        return PrepareSaveCore(branch, kind, business, catalog);
    }

    internal PendingRepositorySave PrepareSaveFrom(string branch, StoredGraphKind kind,
        Func<(PreparedGraphWrite Business, PreparedGraphWrite? Catalog)> produce) =>
        PrepareSaveFromCore(branch, kind, produce, privateCatalog: false);

    internal PendingRepositorySave PreparePrivateSaveFrom(string branch, StoredGraphKind kind,
        Func<(PreparedGraphWrite Business, PreparedGraphWrite? Catalog)> produce) =>
        PrepareSaveFromCore(branch, kind, produce, privateCatalog: true);

    private PendingRepositorySave PrepareSaveFromCore(string branch, StoredGraphKind kind,
        Func<(PreparedGraphWrite Business, PreparedGraphWrite? Catalog)> produce, bool privateCatalog) {
        EnsureEditingAllowed();
        ArgumentNullException.ThrowIfNull(produce);
        ValidateBranchName(branch);
        if (kind is not StoredGraphKind.State and not StoredGraphKind.Event) { throw new ArgumentException("PrepareMetaBootstrap requires a business graph kind.", nameof(kind)); }
        _preparing = true;
        try {
            var prepared = produce();
            return PrepareSaveCore(branch, kind, prepared.Business, prepared.Catalog, privateCatalog);
        } finally { _preparing = false; }
    }

    private PendingRepositorySave PrepareSaveCore(string branch, StoredGraphKind kind,
        PreparedGraphWrite business, PreparedGraphWrite? catalog, bool privateCatalog = false) {
        RequireStorageAvailable();
        ValidateBranchName(branch);
        ArgumentNullException.ThrowIfNull(business);
        if (kind is not StoredGraphKind.State and not StoredGraphKind.Event) { throw new ArgumentException("PrepareMetaBootstrap requires a business graph kind.", nameof(kind)); }
        FrameAddress? parent = GetBranchHead(branch);
        if (business.SourceGraph != GraphStore.ResolveContentSource(kind, parent)) {
            throw new InvalidDataException("The business candidate must use the branch's exact latest State source.");
        }
        // Select the actual owner-bound source, never a caller-supplied Schema address.
        FrameAddress? catalogSource = privateCatalog && parent is { } existing
            ? GraphStore.ReadMetadata(existing).SchemaRoot
                ?? throw new InvalidDataException("A private continuation requires its current business head's Schema pin.")
            : _baseline.SharedRoot;
        if (catalogSource is { } source && GraphStore.ReadMetadata(source).Kind != StoredGraphKind.Meta) {
            throw new InvalidDataException("The selected catalog source must be an actual meta graph.");
        }
        if (catalog is not null && catalog.SourceGraph != catalogSource) {
            throw new InvalidDataException("The catalog candidate must use the exact selected meta source.");
        }
        var pending = new PendingRepositorySave(this, _baseline, branch, kind, business, catalog, privateCatalog, catalogSource);
        _pending = pending;
        return pending;
    }

    internal RepositorySaveResult CommitSave(PendingRepositorySave pending,
        Action<StoredGraphRevision>? installMeta = null, Action<StoredGraphRevision>? installState = null,
        Action<StoredGraphRevision>? prepareMeta = null, Action<StoredGraphRevision>? prepareState = null,
        Action<StoredGraphRevision>? prepareBusinessDelivery = null) {
        RequireStorageAvailable();
        ArgumentNullException.ThrowIfNull(pending);
        if (_versions.IsReadOnly) { throw new InvalidOperationException("The P0 repository is read-only."); }
        if (_committing || !ReferenceEquals(pending.Owner, this) || !ReferenceEquals(_pending, pending) ||
            !pending.IsPrepared || !ReferenceEquals(pending.Baseline, _baseline)) {
            throw new InvalidOperationException("CommitMetaBootstrap requires this owner's latest, unused pending save.");
        }
        pending.BeginCommit();
        _committing = true;
        PublicationOutcome outcome = PublicationOutcome.NotAttempted;
        FrameAddress? candidate = null;
        GraphRecordStore store = GraphStore;
        long framesBefore = store.AppendedFrames;
        bool publishing = false, publicationRejected = false;
        try {
            SaveStageObserver?.Invoke(RepositorySaveStage.BeforeAppend);
            RequireStorageAvailable();
            StoredGraphRevision? selectedMeta = pending.Catalog is not null
                ? store.Append(StoredGraphKind.Meta, pending.CatalogSource, null, pending.Catalog)
                : pending.CatalogSource is { } currentMeta ? store.ReadGraph(currentMeta) : null;
            if (pending.Catalog is not null) { SaveStageObserver?.Invoke(RepositorySaveStage.AfterMetaAppend); }
            RequireStorageAvailable();
            FrameAddress? schemaRoot = selectedMeta?.Address;
            FrameAddress? parent = _baseline.Branches.TryGetValue(pending.Branch, out FrameAddress oldHead) ? oldHead : null;
            StoredGraphRevision business = store.Append(pending.Kind, parent, schemaRoot, pending.Business);
            candidate = business.Address;
            SaveStageObserver?.Invoke(RepositorySaveStage.AfterBusinessAppend);
            RequireStorageAvailable();
            if (selectedMeta is not null) { prepareMeta?.Invoke(selectedMeta); }
            if (pending.Kind == StoredGraphKind.State) { prepareState?.Invoke(business); }
            prepareBusinessDelivery?.Invoke(business);
            RequireStorageAvailable();

            // Prepare all DG result/install material before crossing the publication boundary.
            var branches = new Dictionary<string, FrameAddress>(_baseline.Branches, StringComparer.Ordinal) {
                [pending.Branch] = business.Address
            };
            FrameAddress? sharedRoot = pending.PrivateCatalog ? _baseline.SharedRoot : schemaRoot;
            var nextBaseline = new PublicationBaseline(branches, sharedRoot);
            byte[] branchPayload = EncodeBranches(branches);
            byte[]? sharedPayload = pending.PrivateCatalog || pending.Catalog is null ? null : EncodeShared(schemaRoot);
            Dictionary<RefId, ReadOnlyMemory<byte>>? changes = sharedPayload is null ? null : new() {
                [_branchRevision.RefId] = branchPayload,
                [_sharedRevision.RefId] = sharedPayload
            };
            var saved = new RepositorySaveResult(business, selectedMeta, sharedRoot);
            SaveStageObserver?.Invoke(RepositorySaveStage.BeforePublication);
            RequireStorageAvailable();

            publishing = true;
            RefRevision nextBranch, nextShared = _sharedRevision;
            if (changes is not null) {
                var published = _versions.PublishRefs(changes, out outcome);
                publicationRejected = published.IsFailure;
                IReadOnlyDictionary<RefId, RefRevision> revisions = published.Unwrap();
                nextBranch = revisions[_branchRevision.RefId];
                nextShared = revisions[_sharedRevision.RefId];
            } else {
                var published = _versions.PublishRef(_branchRevision.RefId, branchPayload, out outcome);
                publicationRejected = published.IsFailure;
                nextBranch = published.Unwrap();
            }
            SaveStageObserver?.Invoke(RepositorySaveStage.AfterPublication);
            RequireStorageAvailable();
            SaveStageObserver?.Invoke(RepositorySaveStage.BeforeInstall);
            RequireStorageAvailable();
            _baseline = nextBaseline;
            _branchRevision = nextBranch;
            _sharedRevision = nextShared;
            // Even a reused exact meta graph can consume a producer's same-shape dirty set.
            if (selectedMeta is not null) { installMeta?.Invoke(selectedMeta); }
            RequireStorageAvailable();
            if (pending.Kind == StoredGraphKind.State) { installState?.Invoke(business); }
            RequireStorageAvailable();
            return saved;
        }
        catch (Exception error) {
            // A normal VS rejection proves no publication and may leave healthy orphan data.
            // Other output exceptions, or interrupted DG installation, invalidate old facades.
            bool fault = outcome != PublicationOutcome.NotAttempted || !StorageBackingUsable() ||
                (publishing ? !publicationRejected : store.AppendedFrames != framesBefore);
            if (fault) { MarkFaulted(); }
            RepositoryPublicationOutcome mapped = outcome switch {
                PublicationOutcome.Confirmed => RepositoryPublicationOutcome.Published,
                PublicationOutcome.Unknown => RepositoryPublicationOutcome.Unknown,
                _ => RepositoryPublicationOutcome.NotPublished,
            };
            throw new RepositoryPublicationException(mapped, candidate, error);
        }
        finally {
            pending.Finish();
            if (ReferenceEquals(_pending, pending)) { _pending = null; }
            _committing = false;
        }
    }

    internal void DiscardSave(PendingRepositorySave pending) {
        if (_committing || !ReferenceEquals(pending.Owner, this) || !ReferenceEquals(_pending, pending) || !pending.IsPrepared) {
            throw new InvalidOperationException("DiscardMetaBootstrap requires this owner's inactive, unused pending save.");
        }
        pending.Finish();
        _pending = null;
    }

    internal void RequireStorageAvailable() {
        RequireAvailable();
    }

    internal void RequirePreparationInactive() {
        if (_preparing) { throw new InvalidOperationException("An active metadata graph preparation cannot be disposed recursively."); }
    }

    private bool StorageBackingUsable() {
        try { Data.EnsureUsable(); _versions.EnsureUsable(); return true; }
        catch (InvalidOperationException) { return false; }
    }

    private static byte[] EncodeBranches(Dictionary<string, FrameAddress> branches) => EncodeRoot(BranchMagic,
        branches.ToDictionary(static pair => BranchPrefix + pair.Key, static pair => pair.Value, StringComparer.Ordinal));

    private static byte[] EncodeShared(FrameAddress? schema) => EncodeRoot(SharedMagic,
        schema is { } address ? new Dictionary<string, FrameAddress>(StringComparer.Ordinal) { [SchemaKey] = address } : new(StringComparer.Ordinal));

    private static byte[] EncodeRoot(ReadOnlySpan<byte> magic, IReadOnlyDictionary<string, FrameAddress> roots) {
        byte[] body = RootMapCodec.Encode(roots).Unwrap();
        int length = checked(magic.Length + body.Length);
        if (length > PublicationStore.MaxRootPayloadLength) { throw new InvalidOperationException("P0 publication root exceeds the ref payload capacity."); }
        byte[] payload = new byte[length];
        magic.CopyTo(payload);
        body.CopyTo(payload, magic.Length);
        return payload;
    }

    private static Dictionary<string, FrameAddress> DecodeBranches(ReadOnlySpan<byte> payload) {
        var roots = DecodeRoot(BranchMagic, payload);
        var branches = new Dictionary<string, FrameAddress>(StringComparer.Ordinal);
        foreach ((string key, FrameAddress head) in roots) {
            if (!key.StartsWith(BranchPrefix, StringComparison.Ordinal)) { throw new InvalidDataException("Unknown P0 branch-root key."); }
            string name = key[BranchPrefix.Length..];
            try { ValidateBranchName(name); }
            catch (ArgumentException error) { throw new InvalidDataException("Invalid P0 branch name.", error); }
            branches.Add(name, head);
        }
        return branches;
    }

    private static FrameAddress? DecodeShared(ReadOnlySpan<byte> payload) {
        var roots = DecodeRoot(SharedMagic, payload);
        if (roots.Count == 0) { return null; }
        if (roots.Count != 1 || !roots.TryGetValue(SchemaKey, out FrameAddress schema)) {
            throw new InvalidDataException("Unknown P0 shared-root key.");
        }
        return schema;
    }

    private static IReadOnlyDictionary<string, FrameAddress> DecodeRoot(ReadOnlySpan<byte> magic, ReadOnlySpan<byte> payload) {
        if (!payload.StartsWith(magic)) { throw new InvalidDataException("Unknown P0 publication-root format."); }
        return RootMapCodec.Decode(payload[magic.Length..]).Unwrap();
    }

}
