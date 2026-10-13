using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Atelia.VersionStore;
using DataStore = Atelia.FrameStore.FrameStore;
using PublicationStore = Atelia.VersionStore.VersionStore;

namespace Atelia.DurableGraph.Persistence;

// Concrete metadata-graph owner used by the public factories and bounded internal probes.
public sealed partial class GraphResources {
    private const uint BranchRef = 1, SharedRef = 2;
    private const string BranchPrefix = "branch/", SchemaKey = "schema";
    private static ReadOnlySpan<byte> BranchMagic => "P0R1"u8;
    private static ReadOnlySpan<byte> SharedMagic => "P0S1"u8;
    private MetaBootstrapPublicationBaseline _baseline = null!;
    private RefRevision _branchRevision, _sharedRevision;
    private P0PendingSave? _pending;
    private bool _preparing, _committing;

    // This reference is the operation's exact baseline; it is never changed in place.
    internal sealed class MetaBootstrapPublicationBaseline(Dictionary<string, FrameAddress> branches, FrameAddress? sharedRoot) {
        internal Dictionary<string, FrameAddress> Branches { get; } = branches;
        internal FrameAddress? SharedRoot { get; } = sharedRoot;
    }

    private GraphResources(DataStore data, PublicationStore versions,
        RefBytesSnapshot branches, RefBytesSnapshot shared, long readCacheBudgetBytes) {
        Data = data;
        IsReadOnly = versions.IsReadOnly;
        _versions = versions;
        _branchRevision = branches.Revision;
        _sharedRevision = shared.Revision;
        _baseline = new(DecodeBranches(branches.Payload), DecodeShared(shared.Payload));
        _metaBootstrapStore = new(data, RequireMetaBootstrapAvailable, readCacheBudgetBytes);
        try {
            if (_baseline.SharedRoot is { } schema && _metaBootstrapStore.ReadMetadata(schema).Kind != P0GraphKind.Meta) {
                throw new InvalidDataException("The shared root must select a P0 meta graph.");
            }
        } catch (Exception error) {
            // The outer factory closes VS/FS; release a facade created before root validation failed.
            CloseResources(error, _metaBootstrapStore, null, null);
            throw;
        }
    }

    private readonly P0GraphStore _metaBootstrapStore;
    internal P0GraphStore MetaBootstrapStore { get { RequireMetaBootstrapAvailable(); return _metaBootstrapStore; } }
    internal Action<P0SaveCheckpoint>? MetaBootstrapCheckpoint { get; set; }
    internal bool IsMetaBootstrapFaulted => _faulted || (!_disposed && !MetaBootstrapBackingUsable());
    internal bool HasMetaBootstrapPending { get { RequireMetaBootstrapAvailable(); return _pending is not null; } }
    internal FrameAddress? SharedMetaBootstrapRoot { get { RequireMetaBootstrapAvailable(); return _baseline.SharedRoot; } }

    internal static GraphResources CreateMetaBootstrapNew(string path,
        long readCacheBudgetBytes = P0GraphStore.DefaultReadCacheBudgetBytes, Action<uint>? afterCreateRef = null,
        long rotationThresholdBytes = DefaultRotationThresholdBytes) =>
        OpenMetaBootstrap(path, create: true, readOnly: false, readCacheBudgetBytes, afterCreateRef, rotationThresholdBytes);
    internal static GraphResources OpenMetaBootstrapExisting(string path,
        long readCacheBudgetBytes = P0GraphStore.DefaultReadCacheBudgetBytes,
        long rotationThresholdBytes = DefaultRotationThresholdBytes) =>
        OpenMetaBootstrap(path, create: false, readOnly: false, readCacheBudgetBytes, rotationThresholdBytes: rotationThresholdBytes);
    internal static GraphResources OpenMetaBootstrapReadOnlyExisting(string path,
        long readCacheBudgetBytes = P0GraphStore.DefaultReadCacheBudgetBytes) =>
        OpenMetaBootstrap(path, create: false, readOnly: true, readCacheBudgetBytes);

    private static GraphResources OpenMetaBootstrap(string path, bool create, bool readOnly, long readCacheBudgetBytes,
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

    internal FrameAddress? GetMetaBootstrapHead(string branch) {
        RequireMetaBootstrapAvailable();
        ValidateBranchName(branch);
        return _baseline.Branches.TryGetValue(branch, out FrameAddress head) ? head : null;
    }

    internal FrameAddress? GetMetaBootstrapStateHead(string branch) =>
        MetaBootstrapStore.ResolveContentSource(P0GraphKind.State, GetMetaBootstrapHead(branch));

    internal IReadOnlyList<string> ListMetaBootstrapBranches() {
        RequireMetaBootstrapAvailable();
        return _baseline.Branches.Keys.Order(StringComparer.Ordinal).ToArray();
    }

    // Metadata navigation intentionally does not read a directory, batch or typed body.
    internal HistoryGraphRecord ReadMetaBootstrapHistory(FrameAddress address) {
        P0GraphMetadata metadata = MetaBootstrapStore.ReadMetadata(address);
        GraphFrameKind kind = metadata.Kind switch {
            P0GraphKind.State => GraphFrameKind.State,
            P0GraphKind.Event => GraphFrameKind.Event,
            _ => throw new InvalidDataException("A branch checkpoint must select a business State or Event."),
        };
        return new(address, metadata.Parent, kind, metadata.RootId);
    }

    internal sealed record MetaBootstrapBranchUpdate(GraphResources Owner,
        MetaBootstrapPublicationBaseline ExpectedBaseline, RefRevision ExpectedRevision,
        MetaBootstrapPublicationBaseline NextBaseline, byte[] Payload);

    internal MetaBootstrapBranchUpdate PrepareMetaBootstrapBranchRoot(string branch, FrameAddress head) {
        EnsureMetaBootstrapEditingAllowed();
        ValidateBranchName(branch);
        _ = ReadMetaBootstrapHistory(head);
        var branches = new Dictionary<string, FrameAddress>(_baseline.Branches, StringComparer.Ordinal) { [branch] = head };
        MetaBootstrapPublicationBaseline next = new(branches, _baseline.SharedRoot);
        return new(this, _baseline, _branchRevision, next, EncodeBranches(branches));
    }

    // This operation updates only the branch ref. The shared Schema head and revision stay fixed.
    internal void PublishMetaBootstrapBranchRoot(MetaBootstrapBranchUpdate candidate, out PublicationOutcome outcome) {
        outcome = PublicationOutcome.NotAttempted;
        EnsureMetaBootstrapEditingAllowed();
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
            if (!publicationRejected || outcome != PublicationOutcome.NotAttempted || !MetaBootstrapBackingUsable()) { MarkFaulted(); }
            throw;
        } finally { _committing = false; }
    }

    internal void CreateMetaBootstrapTag(string name, FrameAddress address, out PublicationOutcome outcome) {
        outcome = PublicationOutcome.NotAttempted;
        EnsureMetaBootstrapEditingAllowed();
        _ = ReadMetaBootstrapHistory(address);
        byte[] payload = new byte[FrameAddress.EncodedSize];
        if (!address.TryWrite(payload)) { throw new InvalidDataException("Invalid graph tag address."); }
        bool publicationRejected = false;
        _committing = true;
        try {
            var published = _versions.CreateTag(name, payload, out outcome);
            publicationRejected = published.IsFailure;
            published.Unwrap();
        } catch {
            if (!publicationRejected || outcome != PublicationOutcome.NotAttempted || !MetaBootstrapBackingUsable()) { MarkFaulted(); }
            throw;
        } finally { _committing = false; }
    }

    internal FrameAddress ResolveMetaBootstrapTag(string name) {
        RequireMetaBootstrapAvailable();
        var payload = _versions.ResolveTag(name).Unwrap();
        if (payload.Bytes.Length != FrameAddress.EncodedSize || !FrameAddress.TryRead(payload.Bytes, out FrameAddress address)) {
            throw new InvalidDataException("Invalid graph address in tag payload.");
        }
        _ = ReadMetaBootstrapHistory(address);
        return address;
    }

    internal void EnsureMetaBootstrapEditingAllowed() {
        RequireMetaBootstrapAvailable();
        if (_versions.IsReadOnly) { throw new InvalidOperationException("The P0 repository is read-only."); }
        if (_pending is not null || _preparing || _committing) { throw new InvalidOperationException("A P0 save is already being prepared or pending."); }
    }

    internal P0PendingSave PrepareMetaBootstrap(string branch, P0GraphKind kind,
        P0PreparedGraphWrite business, P0PreparedGraphWrite? catalog = null) {
        EnsureMetaBootstrapEditingAllowed();
        return PrepareMetaBootstrapCore(branch, kind, business, catalog);
    }

    internal P0PendingSave PrepareMetaBootstrapFrom(string branch, P0GraphKind kind,
        Func<(P0PreparedGraphWrite Business, P0PreparedGraphWrite? Catalog)> produce) {
        EnsureMetaBootstrapEditingAllowed();
        ArgumentNullException.ThrowIfNull(produce);
        ValidateBranchName(branch);
        if (kind is not P0GraphKind.State and not P0GraphKind.Event) { throw new ArgumentException("PrepareMetaBootstrap requires a business graph kind.", nameof(kind)); }
        _preparing = true;
        try {
            var prepared = produce();
            return PrepareMetaBootstrapCore(branch, kind, prepared.Business, prepared.Catalog);
        } finally { _preparing = false; }
    }

    private P0PendingSave PrepareMetaBootstrapCore(string branch, P0GraphKind kind,
        P0PreparedGraphWrite business, P0PreparedGraphWrite? catalog) {
        RequireMetaBootstrapAvailable();
        ValidateBranchName(branch);
        ArgumentNullException.ThrowIfNull(business);
        if (kind is not P0GraphKind.State and not P0GraphKind.Event) { throw new ArgumentException("PrepareMetaBootstrap requires a business graph kind.", nameof(kind)); }
        FrameAddress? parent = GetMetaBootstrapHead(branch);
        if (business.SourceGraph != MetaBootstrapStore.ResolveContentSource(kind, parent)) {
            throw new InvalidDataException("The business candidate must use the branch's exact latest State source.");
        }
        if (catalog is not null && catalog.SourceGraph != _baseline.SharedRoot) {
            throw new InvalidDataException("The catalog candidate must use the latest shared meta source.");
        }
        var pending = new P0PendingSave(this, _baseline, branch, kind, business, catalog);
        _pending = pending;
        return pending;
    }

    internal P0SaveResult CommitMetaBootstrap(P0PendingSave pending,
        Action<P0GraphSnapshot>? installMeta = null, Action<P0GraphSnapshot>? installState = null,
        Action<P0GraphSnapshot>? prepareMeta = null, Action<P0GraphSnapshot>? prepareState = null,
        Action<P0GraphSnapshot>? prepareBusinessDelivery = null) {
        RequireMetaBootstrapAvailable();
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
        P0GraphStore store = MetaBootstrapStore;
        long framesBefore = store.AppendedFrames;
        bool publishing = false, publicationRejected = false;
        try {
            MetaBootstrapCheckpoint?.Invoke(P0SaveCheckpoint.BeforeAppend);
            RequireMetaBootstrapAvailable();
            P0GraphSnapshot? selectedMeta = pending.Catalog is not null
                ? store.Append(P0GraphKind.Meta, _baseline.SharedRoot, null, pending.Catalog)
                : _baseline.SharedRoot is { } currentMeta ? store.ReadGraph(currentMeta) : null;
            if (pending.Catalog is not null) { MetaBootstrapCheckpoint?.Invoke(P0SaveCheckpoint.AfterMetaAppend); }
            RequireMetaBootstrapAvailable();
            FrameAddress? schemaRoot = selectedMeta?.Address;
            FrameAddress? parent = _baseline.Branches.TryGetValue(pending.Branch, out FrameAddress oldHead) ? oldHead : null;
            P0GraphSnapshot business = store.Append(pending.Kind, parent, schemaRoot, pending.Business);
            candidate = business.Address;
            MetaBootstrapCheckpoint?.Invoke(P0SaveCheckpoint.AfterBusinessAppend);
            RequireMetaBootstrapAvailable();
            if (selectedMeta is not null) { prepareMeta?.Invoke(selectedMeta); }
            if (pending.Kind == P0GraphKind.State) { prepareState?.Invoke(business); }
            prepareBusinessDelivery?.Invoke(business);
            RequireMetaBootstrapAvailable();

            // Prepare all DG result/install material before crossing the publication boundary.
            var branches = new Dictionary<string, FrameAddress>(_baseline.Branches, StringComparer.Ordinal) {
                [pending.Branch] = business.Address
            };
            var nextBaseline = new MetaBootstrapPublicationBaseline(branches, schemaRoot);
            byte[] branchPayload = EncodeBranches(branches);
            byte[]? sharedPayload = pending.Catalog is null ? null : EncodeShared(schemaRoot);
            Dictionary<RefId, ReadOnlyMemory<byte>>? changes = sharedPayload is null ? null : new() {
                [_branchRevision.RefId] = branchPayload,
                [_sharedRevision.RefId] = sharedPayload
            };
            var saved = new P0SaveResult(business, selectedMeta, schemaRoot);
            MetaBootstrapCheckpoint?.Invoke(P0SaveCheckpoint.BeforePublication);
            RequireMetaBootstrapAvailable();

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
            MetaBootstrapCheckpoint?.Invoke(P0SaveCheckpoint.AfterPublication);
            RequireMetaBootstrapAvailable();
            MetaBootstrapCheckpoint?.Invoke(P0SaveCheckpoint.BeforeInstall);
            RequireMetaBootstrapAvailable();
            _baseline = nextBaseline;
            _branchRevision = nextBranch;
            _sharedRevision = nextShared;
            // Even a reused exact meta graph can consume a producer's same-shape dirty set.
            if (selectedMeta is not null) { installMeta?.Invoke(selectedMeta); }
            RequireMetaBootstrapAvailable();
            if (pending.Kind == P0GraphKind.State) { installState?.Invoke(business); }
            RequireMetaBootstrapAvailable();
            return saved;
        }
        catch (Exception error) {
            // A normal VS rejection proves no publication and may leave healthy orphan data.
            // Other output exceptions, or interrupted DG installation, invalidate old facades.
            bool fault = outcome != PublicationOutcome.NotAttempted || !MetaBootstrapBackingUsable() ||
                (publishing ? !publicationRejected : store.AppendedFrames != framesBefore);
            if (fault) { MarkFaulted(); }
            GraphCommitOutcome mapped = outcome switch {
                PublicationOutcome.Confirmed => GraphCommitOutcome.Published,
                PublicationOutcome.Unknown => GraphCommitOutcome.Unknown,
                _ => GraphCommitOutcome.NotPublished,
            };
            throw new GraphCommitException(mapped, candidate, error);
        }
        finally {
            pending.Finish();
            if (ReferenceEquals(_pending, pending)) { _pending = null; }
            _committing = false;
        }
    }

    internal void DiscardMetaBootstrap(P0PendingSave pending) {
        if (_committing || !ReferenceEquals(pending.Owner, this) || !ReferenceEquals(_pending, pending) || !pending.IsPrepared) {
            throw new InvalidOperationException("DiscardMetaBootstrap requires this owner's inactive, unused pending save.");
        }
        pending.Finish();
        _pending = null;
    }

    internal void RequireMetaBootstrapAvailable() {
        RequireAvailable();
    }

    internal void RequireMetaBootstrapPreparationInactive() {
        if (_preparing) { throw new InvalidOperationException("An active metadata graph preparation cannot be disposed recursively."); }
    }

    private bool MetaBootstrapBackingUsable() {
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
